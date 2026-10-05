using System.Collections.ObjectModel;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
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

    /// <summary>Raised by "Add to Timeline" (button / double-click). MainWindowViewModel
    /// forwards it to the Timeline panel, which owns the edit and reports the result.</summary>
    public event EventHandler<MediaAsset>? AddToTimelineRequested;

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

        _projectService.ProjectChanged += (_, _) => ReloadFromProject();
        _projectService.MediaAssetsChanged += (_, _) => ReloadFromProject();

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

        Items.Clear();
        foreach (var asset in _projectService.Current.MediaAssets)
            Items.Add(new MediaBrowserItemViewModel(asset) { Thumbnail = _thumbnails?.Get(asset.Id) });

        // Keep the same item selected across a reload (e.g. after an import) when
        // it's still there; otherwise clear selection rather than pointing at a
        // stale view model instance.
        SelectedItem = previouslySelectedId is null
            ? null
            : Items.FirstOrDefault(i => i.Asset.Id == previouslySelectedId);

        OnPropertyChanged(nameof(HasNoMedia));
        OnPropertyChanged(nameof(HasOfflineMedia));
        NotifyRelinkCommands();                                  // what is offline may have changed
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

    [RelayCommand(CanExecute = nameof(CanAddToTimeline))]
    private void AddToTimeline()
    {
        if (SelectedItem is { } item)
            AddToTimelineRequested?.Invoke(this, item.Asset);
    }

    private bool CanAddToTimeline() => CanEdit() && SelectedItem is not null;

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
        if (_edit is null || SelectedItem is not { } item || !CanRemove()) return;
        var asset = item.Asset;
        if (_edit.GetRemoveMediaBlockReason(asset.Id) is { } blocked)
        {
            _status?.Report(blocked);
            return;
        }

        var clipCount = _edit.CountClipsUsing(asset.Id);
        if (clipCount > 0)
        {
            if (_dialogs is null) return;
            var clips = clipCount == 1 ? "1 clip" : $"{clipCount} clips";
            var choice = await _dialogs.AskAsync(new DialogRequest
            {
                Title = "Remove Media",
                Message = $"{asset.FileName} is used by {clips} on the timeline. Removing it from the project deletes " +
                          $"{(clipCount == 1 ? "that clip" : "those clips")} too, and any dissolves on them.\n\n" +
                          "The file on disk is not deleted. You can undo this with Undo.",
                Buttons = new[] { "Remove", "Cancel" }
            });
            // The answer may come after an export or a relink started, or the project was replaced; the service
            // re-checks the rest.
            if (choice != 0 || !CanEdit() || _relink is { IsRunning: true }) return;
        }

        var result = _edit.RemoveMedia(asset.Id);
        if (!result.Success)
        {
            _status?.Report(result.Message ?? "The media could not be removed.");
            return;
        }
        var removed = clipCount == 0
            ? $"Removed {asset.FileName} from the project."
            : $"Removed {asset.FileName} and {(clipCount == 1 ? "its clip" : $"its {clipCount} clips")} from the project.";
        _status?.Report(result.Message is { } note ? $"{removed} {note}" : removed);
    }
}
