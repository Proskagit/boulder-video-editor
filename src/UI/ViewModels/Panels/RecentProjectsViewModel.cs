using System.Collections.ObjectModel;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.UI.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiVideoEditor.UI.ViewModels.Panels;

/// <summary>
/// The toolbar's <c>Recent ▾</c> drop-down (D026 §6, Step 11.8). Every opening reads the list again from
/// <see cref="IRecentProjectsStore"/> and checks each entry's availability with <see cref="IRecentProjectsStore.IsAvailableAsync"/>
/// (Checking → Available / Unavailable; an error is Unavailable). Only an available entry opens — through
/// <see cref="ProjectFileWorkflow.OpenFolderAsync"/>, so the unsaved-changes question, the open itself and the list update
/// are the workflow's; an entry of any state can be removed. Disabled while an export runs (<see cref="EditingLock"/>).
/// </summary>
/// <remarks>
/// <para>Called on the UI thread; the store does its file work elsewhere and every await comes back to the UI thread, so
/// the items change only there. A check's result goes to the item it was started for; every opening makes new items, so a
/// result that arrives after the list was read again or an entry was removed changes nothing shown. A list read that a
/// later opening or a removal overtook is dropped. One check per project at a time: an opening while a project's check
/// still runs waits for that check instead of starting another (there is no time limit — a disconnected drive may answer
/// late, and its entry stays Checking until then).</para>
/// <para>One action at a time: while an entry is opened or removed, no other can start.</para>
/// </remarks>
public sealed partial class RecentProjectsViewModel : ViewModelBase
{
    private readonly IRecentProjectsStore _store;
    private readonly ProjectFileWorkflow _projectFiles;
    private readonly StatusService _status;
    private readonly EditingLock _editingLock;
    private readonly ILogger _logger;
    private readonly Dictionary<string, Task<bool>> _checks = new(StringComparer.OrdinalIgnoreCase);
    private int _listVersion;

    public RecentProjectsViewModel(
        IRecentProjectsStore store,
        ProjectFileWorkflow projectFiles,
        StatusService status,
        EditingLock editingLock,
        ILogger<RecentProjectsViewModel>? logger = null)
    {
        _store = store;
        _projectFiles = projectFiles;
        _status = status;
        _editingLock = editingLock;
        _logger = logger ?? (ILogger)NullLogger.Instance;
        _editingLock.PropertyChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(IsEnabled));
            NotifyItemCommands();
        };
    }

    /// <summary>The entries of the last reading, most recent first.</summary>
    public ObservableCollection<RecentProjectItemViewModel> Items { get; } = new();

    /// <summary>The drop-down button: off while an export runs.</summary>
    public bool IsEnabled => !_editingLock.IsLocked;

    /// <summary>The list was read and holds nothing.</summary>
    [ObservableProperty] private bool _isEmpty;

    /// <summary>An entry is being opened or removed.</summary>
    [ObservableProperty] private bool _isBusy;

    /// <summary>Asks the view to close the drop-down (an entry is being opened).</summary>
    public event EventHandler? CloseRequested;

    private bool _isOpen;

    /// <summary>The drop-down opened: reads the list again and checks every entry.</summary>
    public Task OnOpenedAsync()
    {
        _isOpen = true;
        return RefreshAsync();
    }

    /// <summary>The drop-down closed. Running checks finish on their own (nothing to cancel: a file check can't be
    /// interrupted); their results only reach their items.</summary>
    public void OnClosed() => _isOpen = false;

    /// <summary>Reads the list and replaces the items; a reading overtaken by a later one or by a removal is dropped.</summary>
    public async Task RefreshAsync()
    {
        var version = ++_listVersion;
        IReadOnlyList<RecentProject> entries;
        try
        {
            entries = await _store.GetAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Reading the recent projects failed.");
            entries = Array.Empty<RecentProject>();
        }

        if (version != _listVersion) return;

        foreach (var old in Items) old.Detach();
        Items.Clear();
        foreach (var entry in entries.Take(IRecentProjectsStore.MaxEntries))
            Items.Add(new RecentProjectItemViewModel(entry, this));
        IsEmpty = Items.Count == 0;
        NotifyItemCommands();

        foreach (var item in Items)
            _ = TrackAvailabilityAsync(item);
    }

    private async Task TrackAvailabilityAsync(RecentProjectItemViewModel item)
    {
        Task<bool> check;
        lock (_checks)
        {
            if (!_checks.TryGetValue(item.FolderPath, out check!))
                _checks[item.FolderPath] = check = CheckAsync(item.FolderPath);
        }

        var available = await check;

        lock (_checks)
        {
            if (_checks.TryGetValue(item.FolderPath, out var current) && ReferenceEquals(current, check))
                _checks.Remove(item.FolderPath);
        }
        // An item read again or removed meanwhile is no longer shown and can't act (IsDetached): its state is moot.
        item.Availability = available ? RecentProjectAvailability.Available : RecentProjectAvailability.Unavailable;
        item.NotifyCommands();
    }

    /// <summary>The store's check, started off this thread (the store does its file work elsewhere anyway); any error is
    /// unavailable.</summary>
    private async Task<bool> CheckAsync(string folderPath)
    {
        try
        {
            return await Task.Run(() => _store.IsAvailableAsync(folderPath));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Checking the recent project {Folder} failed.", folderPath);
            return false;
        }
    }

    // ---- Actions ------------------------------------------------------------------------------------------------------

    internal bool CanOpen(RecentProjectItemViewModel item) =>
        !item.IsDetached && item.Availability == RecentProjectAvailability.Available && CanAct();

    internal bool CanRemove(RecentProjectItemViewModel item) => !item.IsDetached && CanAct();

    private bool CanAct() => !IsBusy && !_editingLock.IsLocked;

    /// <summary>Opens an available entry like Open (unsaved changes asked first). A failure or a cancel leaves the
    /// current project and the list as they are; the workflow reports it and lists a project it opened.</summary>
    internal async Task OpenAsync(RecentProjectItemViewModel item)
    {
        if (!CanOpen(item)) return;
        SetBusy(true);
        CloseRequested?.Invoke(this, EventArgs.Empty);
        try
        {
            await _projectFiles.OpenFolderAsync(item.FolderPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Opening the recent project {Folder} failed.", item.FolderPath);
            _status.Report($"Couldn't open the project. {ex.Message}");
        }
        finally
        {
            SetBusy(false);
        }

        if (_isOpen)
            await RefreshAsync();   // opened again meanwhile: show the list as it is now
    }

    /// <summary>Removes an entry from the list (any state). Shown only once the store removed it; otherwise reported,
    /// and the list read again.</summary>
    internal async Task RemoveAsync(RecentProjectItemViewModel item)
    {
        if (!CanRemove(item)) return;
        SetBusy(true);
        bool removed;
        try
        {
            removed = await _store.RemoveAsync(item.FolderPath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Removing the recent project {Folder} failed.", item.FolderPath);
            removed = false;
        }

        try
        {
            if (removed)
            {
                _listVersion++;   // a reading started before the removal must not bring the entry back
                item.Detach();
                Items.Remove(item);
                IsEmpty = Items.Count == 0;
            }
            else
            {
                _status.Report($"Couldn't remove \"{item.Name}\" from the recent projects.");
            }
        }
        finally
        {
            SetBusy(false);
        }

        if (!removed)
            await RefreshAsync();
    }

    private void SetBusy(bool busy)
    {
        IsBusy = busy;
        NotifyItemCommands();
    }

    private void NotifyItemCommands()
    {
        foreach (var item in Items) item.NotifyCommands();
    }
}

/// <summary>Whether a recent project can be opened now; runtime only (D026 §6).</summary>
public enum RecentProjectAvailability
{
    Checking,
    Available,
    Unavailable
}

/// <summary>An entry of <c>Recent ▾</c>: the project's name, its folder (to tell projects of one name apart) and its
/// availability.</summary>
public sealed partial class RecentProjectItemViewModel : ViewModelBase
{
    public RecentProjectItemViewModel(RecentProject entry, RecentProjectsViewModel owner)
    {
        FolderPath = entry.FolderPath;
        Name = entry.Name;
        LastUsedAt = entry.LastUsedAt;
        OpenCommand = new AsyncRelayCommand(() => owner.OpenAsync(this), () => owner.CanOpen(this));
        RemoveCommand = new AsyncRelayCommand(() => owner.RemoveAsync(this), () => owner.CanRemove(this));
    }

    public string FolderPath { get; }
    public string Name { get; }
    public DateTimeOffset LastUsedAt { get; }

    public IAsyncRelayCommand OpenCommand { get; }
    public IAsyncRelayCommand RemoveCommand { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateText), nameof(ToolTip), nameof(IsUnavailable))]
    private RecentProjectAvailability _availability = RecentProjectAvailability.Checking;

    public string StateText => Availability switch
    {
        RecentProjectAvailability.Checking => "Checking…",
        RecentProjectAvailability.Unavailable => "Unavailable",
        _ => ""
    };

    public bool IsUnavailable => Availability == RecentProjectAvailability.Unavailable;

    public string ToolTip => Availability switch
    {
        RecentProjectAvailability.Checking => $"{FolderPath}\nChecking whether the project is there…",
        RecentProjectAvailability.Unavailable => $"{FolderPath}\nThe project can't be found there now. Remove it from the list with ✕.",
        _ => $"{FolderPath}\nLast used {LastUsedAt.LocalDateTime:g}"
    };

    /// <summary>No longer shown (the list was read again, or the entry removed).</summary>
    public bool IsDetached { get; private set; }

    internal void Detach()
    {
        IsDetached = true;
        NotifyCommands();
    }

    internal void NotifyCommands()
    {
        OpenCommand.NotifyCanExecuteChanged();
        RemoveCommand.NotifyCanExecuteChanged();
    }
}
