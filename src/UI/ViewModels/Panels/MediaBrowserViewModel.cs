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
    private MediaBrowserItemViewModel? _selectedItem;

    public bool HasNoMedia => Items.Count == 0;

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
        EditingLock? editingLock = null)
    {
        _projectService = projectService;
        _importWorkflow = importWorkflow;
        _logger = logger;
        _editingLock = editingLock ?? new EditingLock();
        _editingLock.PropertyChanged += (_, _) =>
        {
            ImportCommand.NotifyCanExecuteChanged();
            AddToTimelineCommand.NotifyCanExecuteChanged();
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
        var previouslySelectedPath = SelectedItem?.Asset.FilePath;

        Items.Clear();
        foreach (var asset in _projectService.Current.MediaAssets)
            Items.Add(new MediaBrowserItemViewModel(asset));

        // Keep the same item selected across a reload (e.g. after an import) when
        // it's still there; otherwise clear selection rather than pointing at a
        // stale view model instance.
        SelectedItem = previouslySelectedPath is null
            ? null
            : Items.FirstOrDefault(i => i.Asset.FilePath == previouslySelectedPath);

        OnPropertyChanged(nameof(HasNoMedia));
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
}
