using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.UI.ViewModels;

namespace AiVideoEditor.UI.Services;

/// <summary>
/// "Project Settings…" (Phase 13 Step 13.6, D028): opens the dialog on a draft of the current project's canvas and frame
/// rate (<see cref="ProjectSettingsViewModel"/>). Not available while an export runs (<see cref="EditingLock"/>, checked
/// here and again by the dialog right before Apply changes the project). <see cref="Summary"/> — "1920 × 1080 · 25 FPS" —
/// follows the project (any timeline change, Undo / Redo included, and New / Open / Recover).
/// </summary>
public sealed class ProjectSettingsWorkflow
{
    private readonly IProjectService _projects;
    private readonly ITimelineEditService _edit;
    private readonly StatusService _status;
    private readonly EditingLock _editingLock;
    private readonly IProjectSettingsDialog _dialog;

    public ProjectSettingsWorkflow(IProjectService projects, ITimelineEditService edit, StatusService status,
        EditingLock editingLock, IProjectSettingsDialog dialog)
    {
        _projects = projects;
        _edit = edit;
        _status = status;
        _editingLock = editingLock;
        _dialog = dialog;
        _projects.TimelineChanged += (_, _) => SummaryChanged?.Invoke(this, EventArgs.Empty);
        _projects.ProjectChanged += (_, _) => SummaryChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The current canvas and rate, e.g. "1920 × 1080 · 30 FPS (provisional)".</summary>
    public string Summary
    {
        get
        {
            var s = _projects.Current.Settings;
            return ProjectSettingsViewModel.Summary(s.FrameWidth, s.FrameHeight, s.FrameRate, s.IsFrameRateLocked);
        }
    }

    public event EventHandler? SummaryChanged;

    /// <summary>True while the dialog is open.</summary>
    public bool IsOpen { get; private set; }

    public async Task RunAsync()
    {
        if (_editingLock.IsLocked)
        {
            _status.Report(ProjectSettingsViewModel.LockedMessage);
            return;
        }
        if (IsOpen) return;

        IsOpen = true;
        try
        {
            var settings = new ProjectSettingsViewModel(_projects.Current.Settings, _edit, _status, () => _editingLock.IsLocked);
            await _dialog.ShowAsync(settings);
        }
        finally
        {
            IsOpen = false;
        }
    }
}
