using System.Collections.ObjectModel;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Core.Localization;
using AiVideoEditor.UI.Localization;
using AiVideoEditor.UI.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace AiVideoEditor.UI.ViewModels.Panels;

/// <summary>
/// Left-hand Media Browser. <see cref="Items"/> is a pure reactive projection over
/// <see cref="IProjectService.Current"/>'s media assets — it rebuilds itself
/// whenever the project resets or media is added, rather than being pushed into
/// directly by the Import command. That means it doesn't matter whether an import
/// was triggered from here or from the Toolbar (see <see cref="MediaImportWorkflow"/>).
/// </summary>
public sealed partial class MediaBrowserViewModel : ViewModelBase
{
    private readonly IProjectService _projectService;
    private readonly MediaImportWorkflow _importWorkflow;
    private readonly ILogger<MediaBrowserViewModel> _logger;

    public ObservableCollection<MediaBrowserItemViewModel> Items { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoMedia))]
    [NotifyCanExecuteChangedFor(nameof(AddToTimelineCommand))]
    [NotifyCanExecuteChangedFor(nameof(RelinkCommand))]
    [NotifyCanExecuteChangedFor(nameof(RemoveCommand))]
    private MediaBrowserItemViewModel? _selectedItem;

    public bool HasNoMedia => Items.Count == 0;

    /// <summary>Some media of the project is offline: the Relink / Find Missing row is shown.</summary>
    public bool HasOfflineMedia => Items.Any(i => i.Asset.IsMissing);

    /// <summary>Raised whenever the selected media item changes, carrying the
    /// underlying asset (or null when selection is cleared). MainWindowViewModel
    /// listens to this and forwards it to the Inspector — Media Browser and
    /// Inspector never reference each other directly.</summary>
    public event EventHandler<MediaAsset?>? SelectionChanged;

    /// <summary>Raised by "Add to Timeline" (the button, an image's double click). MainWindowViewModel
    /// forwards it to the Timeline panel, which owns the edit and reports the result.</summary>
    public event EventHandler<MediaAsset>? AddToTimelineRequested;

    /// <summary>The item should open in the Preview's Source mode (Phase 16, D031 SQ11).</summary>
    public event EventHandler<MediaAsset>? OpenInSourceRequested;

    /// <summary>A double click (Phase 16): a video or audio file opens in Source; an image — which has no source
    /// range — is added to the timeline, as before.</summary>
    public void Activate(MediaBrowserItemViewModel item)
    {
        if (item.Asset.Kind == MediaKind.Image)
        {
            if (AddToTimelineCommand.CanExecute(null)) AddToTimelineCommand.Execute(null);
            return;
        }
        OpenInSourceRequested?.Invoke(this, item.Asset);
    }

    public MediaBrowserViewModel(
        IProjectService projectService,
        MediaImportWorkflow importWorkflow,
        ILogger<MediaBrowserViewModel> logger,
        EditingLock? editingLock = null,
        ThumbnailCoordinator? thumbnails = null,
        MediaRelinkWorkflow? relink = null,
        ITimelineEditService? edit = null,
        IDialogService? dialogs = null,
        StatusService? status = null)
    {
        _edit = edit;
        _dialogs = dialogs;
        _status = status;
        _relink = relink;
        if (relink is not null)
            relink.IsRunningChanged += (_, _) => NotifyRelinkCommands();
        _projectService = projectService;
        _importWorkflow = importWorkflow;
        _logger = logger;
        _thumbnails = thumbnails;
        if (thumbnails is not null)
            thumbnails.ThumbnailReady += (_, assetId) => OnThumbnailReady(assetId);
        _editingLock = editingLock ?? new EditingLock();
        _editingLock.PropertyChanged += (_, _) =>
        {
            ImportCommand.NotifyCanExecuteChanged();
            AddToTimelineCommand.NotifyCanExecuteChanged();
            NotifyRelinkCommands();
        };

        SelectedItems.CollectionChanged += (_, _) => AddToTimelineCommand.NotifyCanExecuteChanged();
        _projectService.ProjectChanged += (_, _) => ReloadFromProject();
        _projectService.MediaAssetsChanged += (_, _) => ReloadFromProject();
        if (importWorkflow is not null) importWorkflow.Imported += (_, ids) => SelectImported(ids);

        ReloadFromProject();
    }

    partial void OnSelectedItemChanged(MediaBrowserItemViewModel? value)
    {
        SelectionChanged?.Invoke(this, value?.Asset);
    }

    private void ReloadFromProject()
    {
        // By id: a relink changes the asset's path, not which item it is (Phase 11 Step 11.6).
        var previouslySelectedId = SelectedItem?.Asset.Id;
        var previouslySelected = SelectedItems.Select(i => i.Asset.Id).ToHashSet();
        SelectedItems.Clear();

        Items.Clear();
        foreach (var asset in _projectService.Current.MediaAssets)
            Items.Add(new MediaBrowserItemViewModel(asset) { Thumbnail = _thumbnails?.Get(asset.Id) });

        // Keep the same item selected across a reload (e.g. after an import) when
        // it's still there; otherwise clear selection rather than pointing at a
        // stale view model instance.
        SelectedItem = previouslySelectedId is null
            ? null
            : Items.FirstOrDefault(i => i.Asset.Id == previouslySelectedId);
        foreach (var item in Items.Where(i => previouslySelected.Contains(i.Asset.Id)))
            SelectedItems.Add(item);

        OnPropertyChanged(nameof(HasNoMedia));
        OnPropertyChanged(nameof(HasOfflineMedia));
        NotifyRelinkCommands();                                  // what is offline may have changed
    }

    /// <summary>D068 item 7: the files an import added become the selection, so «На таймлайн» acts on them at once.</summary>
    internal void SelectImported(IReadOnlyCollection<Guid> ids)
    {
        var added = Items.Where(i => ids.Contains(i.Asset.Id)).ToList();
        if (added.Count == 0) return;
        SelectedItems.Clear();
        foreach (var item in added) SelectedItems.Add(item);
        SelectedItem = added[0];
    }

    // Thumbnails (D024 Step 9.4): the coordinator makes them; rows take the ready ones when they are (re)built and
    // when one becomes ready. The coordinator only reports thumbnails of the current project.
    private readonly ThumbnailCoordinator? _thumbnails;

    private void OnThumbnailReady(Guid assetId)
    {
        foreach (var item in Items.Where(i => i.Asset.Id == assetId))
            item.Thumbnail = _thumbnails!.Get(assetId);
    }

    // Import and Add to Timeline change the project: disabled while an export runs (EditingLock).
    private readonly EditingLock _editingLock;

    private bool CanEdit() => !_editingLock.IsLocked;

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private Task Import() => _importWorkflow.RunAsync();

    /// <summary>D057 §3.2: the selected rows (several with Ctrl / Shift and Ctrl+A); <see cref="SelectedItem"/> stays the
    /// primary one the Inspector shows.</summary>
    public ObservableCollection<MediaBrowserItemViewModel> SelectedItems { get; } = new();

    /// <summary>Raised by "Add to Timeline" with several rows selected — the assets in the list's order (the shell puts
    /// them in their recording order).</summary>
    public event EventHandler<IReadOnlyList<MediaAsset>>? AddManyToTimelineRequested;

    [RelayCommand(CanExecute = nameof(CanAddToTimeline))]
    private void AddToTimeline()
    {
        var selected = Items.Where(i => SelectedItems.Contains(i)).Select(i => i.Asset).ToList();
        if (selected.Count > 1)
            AddManyToTimelineRequested?.Invoke(this, selected);
        else if (SelectedItem is { } item)
            AddToTimelineRequested?.Invoke(this, item.Asset);
        else if (selected.Count == 1)
            AddToTimelineRequested?.Invoke(this, selected[0]);
    }

    /// <summary>The owner's acceptance of 2026-11-10: what a drag started on <paramref name="item"/> carries — the selected
    /// files in the list's order when it is one of them (several selected), else the item alone.</summary>
    public IReadOnlyList<MediaAsset> DraggedGroup(MediaBrowserItemViewModel item) =>
        SelectedItems.Count > 1 && SelectedItems.Contains(item)
            ? Items.Where(SelectedItems.Contains).Select(i => i.Asset).ToList()
            : new[] { item.Asset };

    private bool CanAddToTimeline() => CanEdit() && (SelectedItem is not null || SelectedItems.Count > 0);

    /// <summary>Whether Ctrl+A in the list is the Media Browser's now — not in the hold markup mode, where it accepts every
    /// proposal (D047), set by the shell.</summary>
    public Func<bool> CanSelectAllNow { get; set; } = () => true;

    public bool AllowsSelectAll => CanSelectAllNow();

    /// <summary>D057 §3.2: Ctrl+A in the Media Browser — every file of the list.</summary>
    [RelayCommand]
    private void SelectAll()
    {
        foreach (var item in Items)
            if (!SelectedItems.Contains(item)) SelectedItems.Add(item);
        SelectedItem ??= Items.FirstOrDefault();
    }

    /// <summary>D067 §1: a rubber band dragged over the list — the selection becomes <paramref name="kept"/> (the selection
    /// before the drag, with Ctrl; empty without) plus the files <paramref name="touched"/>, in the list's order; the first
    /// touched file is the primary one when the old primary is no longer selected.</summary>
    public void SelectByMarquee(IReadOnlyCollection<MediaBrowserItemViewModel> touched, IReadOnlyCollection<MediaBrowserItemViewModel> kept)
    {
        var wanted = Items.Where(i => touched.Contains(i) || kept.Contains(i)).ToList();
        if (wanted.SequenceEqual(SelectedItems)) return;
        // D078 amendment 4: the primary file first — setting it after the group would reset the list's selection to it.
        if (SelectedItem is null || !wanted.Contains(SelectedItem))
            SelectedItem = Items.FirstOrDefault(touched.Contains) ?? wanted.FirstOrDefault();
        foreach (var item in SelectedItems.Where(i => !wanted.Contains(i)).ToList()) SelectedItems.Remove(item);
        for (var i = 0; i < wanted.Count; i++)
        {
            var at = SelectedItems.IndexOf(wanted[i]);
            if (at == i) continue;
            if (at >= 0) SelectedItems.RemoveAt(at);
            SelectedItems.Insert(i, wanted[i]);
        }
        if (SelectedItem is null || !SelectedItems.Contains(SelectedItem))
            SelectedItem = Items.FirstOrDefault(touched.Contains) ?? SelectedItems.FirstOrDefault();
    }

    // --- The context menu of a file (the owner's check of 0.38.1, 2026-11-10) ---------------------------------------------

    /// <summary>The file the context menu was opened on (null: the list's empty part).</summary>
    public MediaBrowserItemViewModel? ContextItem { get; private set; }

    /// <summary>Before the menu opens on <paramref name="item"/>: a file outside the selection becomes the selection, as
    /// Explorer does; a file of a group selection keeps the group (so «Добавить на таймлайн» adds them all).</summary>
    public void PrepareContextMenu(MediaBrowserItemViewModel? item)
    {
        ContextItem = item;
        if (item is not null && !SelectedItems.Contains(item))
        {
            SelectedItems.Clear();
            SelectedItems.Add(item);
            SelectedItem = item;
        }
        OnPropertyChanged(nameof(ContextItemIsMissing));
        ShowInExplorerCommand.NotifyCanExecuteChanged();
        RemoveContextItemCommand.NotifyCanExecuteChanged();
        RelinkContextItemCommand.NotifyCanExecuteChanged();
        AddToTimelineCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Raised by «Показать в проводнике» with the file's path; the view opens Explorer on it.</summary>
    public event EventHandler<string>? RevealRequested;

    private bool CanShowInExplorer() => ContextItem is { Asset.IsMissing: false };

    [RelayCommand(CanExecute = nameof(CanShowInExplorer))]
    private void ShowInExplorer()
    {
        if (ContextItem is { } item) RevealRequested?.Invoke(this, item.Asset.FilePath);
    }

    /// <summary>The menu's «Удалить из проекта», by the rules of Remove: every selected file when the menu was opened on one of
    /// them (D069 §1), else the file the menu was opened on alone.</summary>
    private bool CanRemoveContextItem() => ContextItem is not null && CanEdit() && _edit is not null && _relink is not { IsRunning: true };

    [RelayCommand(CanExecute = nameof(CanRemoveContextItem))]
    private Task RemoveContextItem()
    {
        if (ContextItem is not { } item || !CanRemove()) return Task.CompletedTask;
        // D069 §1: the group stays — Remove takes all of it. D078 amendment 4: the group is taken as it is, never by setting the
        // current file inside it (the list's SelectedItem binding would reset a multiple selection to that one file).
        if (SelectedItems.Contains(item)) return RemoveItemsAsync(SelectedItems.ToList());
        FocusOn(item);
        return RemoveItemsAsync(new List<MediaBrowserItemViewModel> { item });
    }

    private bool CanRelinkContextItem() => CanRelinkAny() && ContextItem is { Asset.IsMissing: true };

    [RelayCommand(CanExecute = nameof(CanRelinkContextItem))]
    private Task RelinkContextItem()
    {
        if (ContextItem is not { } item) return Task.CompletedTask;
        FocusOn(item);
        return RelinkCommand.ExecuteAsync(null);
    }

    public bool ContextItemIsMissing => ContextItem is { Asset.IsMissing: true };

    private void FocusOn(MediaBrowserItemViewModel item)
    {
        SelectedItems.Clear();
        SelectedItems.Add(item);
        SelectedItem = item;
    }

    // Relink (D026 §5, Phase 11 Step 11.6): only offline media, never during an export, one at a time. All the relink
    // rules are the relink service's (through MediaRelinkWorkflow); the list follows the project's events.
    private readonly MediaRelinkWorkflow? _relink;

    private bool CanRelinkAny() => CanEdit() && _relink is { IsRunning: false };

    private void NotifyRelinkCommands()
    {
        RelinkCommand.NotifyCanExecuteChanged();
        FindMissingCommand.NotifyCanExecuteChanged();
        RemoveCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanRelink))]
    private Task Relink() => SelectedItem is { } item && _relink is not null ? _relink.RelinkAsync(item.Asset.Id) : Task.CompletedTask;

    private bool CanRelink() => CanRelinkAny() && SelectedItem is { Asset.IsMissing: true };

    [RelayCommand(CanExecute = nameof(CanFindMissing))]
    private Task FindMissing() => _relink?.FindMissingAsync() ?? Task.CompletedTask;

    private bool CanFindMissing() => CanRelinkAny() && HasOfflineMedia;

    // Remove (D027 §4, Phase 12 Step 12.4): the selected asset leaves the project, with the clips that use it after the
    // user confirms. Every rule (the asset still there, locked tracks) and the change itself are the edit service's;
    // the file on disk is never touched. Not during an export or a relink.
    private readonly ITimelineEditService? _edit;
    private readonly IDialogService? _dialogs;
    private readonly StatusService? _status;

    private bool CanRemove() => CanEdit() && _edit is not null && _relink is not { IsRunning: true } && SelectedItem is not null;

    [RelayCommand(CanExecute = nameof(CanRemove))]
    private async Task Remove()
    {
        if (_edit is null || SelectedItem is not { } focused || !CanRemove()) return;
        // D068 amendment 1: every selected file goes (the file the command was given for when the selection is elsewhere),
        // after one question for all of them, as one Undo step.
        var items = SelectedItems.Contains(focused) ? SelectedItems.ToList() : new List<MediaBrowserItemViewModel> { focused };
        if (items.Count == 1) items = new List<MediaBrowserItemViewModel> { focused };
        await RemoveItemsAsync(items);
    }

    private async Task RemoveItemsAsync(List<MediaBrowserItemViewModel> items)
    {
        if (_edit is null || items.Count == 0 || !CanEdit()) return;
        foreach (var item in items)
        {
            if (_edit.GetRemoveMediaBlockReason(item.Asset.Id) is { } blocked)
            {
                _status?.ReportWarning(blocked);
                return;
            }
        }

        var clipCount = items.Sum(i => _edit.CountClipsUsing(i.Asset.Id));
        var single = items.Count == 1 ? items[0].Asset : null;
        if (clipCount > 0)
        {
            if (_dialogs is null) return;
            var choice = await _dialogs.AskAsync(new DialogRequest
            {
                Title = Strings.Media_RemoveTitle,
                Message = single is not null
                    ? clipCount == 1
                        ? string.Format(Strings.Media_RemoveQuestionOne, single.FileName)
                        : Plural.Format(clipCount, Strings.Media_RemoveQuestionMany, single.FileName)
                    : string.Format(Strings.Media_RemoveQuestionFiles, items.Count, clipCount),
                Buttons = new[] { Strings.Common_Remove, Strings.Common_Cancel }
            });
            // The answer may come after an export or a relink started, or the project was replaced; the service
            // re-checks the rest.
            if (choice != 0 || !CanEdit() || _relink is { IsRunning: true }) return;
        }

        var result = _edit.RemoveMediaMany(items.Select(i => i.Asset.Id).ToList());
        if (!result.Success)
        {
            _status?.ReportWarning(result.Message ?? Strings.Media_NotRemoved);
            return;
        }
        var removed = single is null
            ? string.Format(Strings.Media_RemovedFiles, items.Count, clipCount)
            : clipCount == 0
                ? string.Format(Strings.Media_Removed, single.FileName)
                : clipCount == 1
                    ? string.Format(Strings.Media_RemovedWithClipOne, single.FileName)
                    : Plural.Format(clipCount, Strings.Media_RemovedWithClipsMany, single.FileName);
        _status?.Report(result.Message is { } note ? $"{removed} {note}" : removed);
    }
}
