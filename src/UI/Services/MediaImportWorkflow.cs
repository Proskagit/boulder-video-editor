using AiVideoEditor.Core.Interfaces;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;

namespace AiVideoEditor.UI.Services;

/// <summary>
/// Orchestrates the whole "pick files → validate/import → add to project" action
/// as a single reusable step. Both the Toolbar's and the Media Browser's Import
/// buttons call this, so there is exactly one place that does the work — no logic
/// duplicated between the two view models. <see cref="ViewModels.Panels.MediaBrowserViewModel"/>
/// doesn't get pushed into directly by this class; it picks the result up
/// reactively via <see cref="IProjectService.MediaAssetsChanged"/>, so it doesn't
/// matter which button triggered the import.
/// <para>
/// An import belongs to the project it was started in (D027 §7): the workflow takes the current project before the
/// file picker opens and checks after every await that it is still the current one. If another project became current
/// meanwhile (New, Open, Recover), nothing is added, no analysis is queued, and the status bar says so.
/// </para>
/// </summary>
public sealed class MediaImportWorkflow
{
    private readonly IFilePickerService _filePicker;
    private readonly IMediaImportService _mediaImportService;
    private readonly IProjectService _projectService;
    private readonly MediaAnalysisCoordinator _analysisCoordinator;
    private readonly StatusService _status;
    private readonly ILogger<MediaImportWorkflow> _logger;
    private readonly Func<Task> _showStatus;

    public MediaImportWorkflow(
        IFilePickerService filePicker,
        IMediaImportService mediaImportService,
        IProjectService projectService,
        MediaAnalysisCoordinator analysisCoordinator,
        StatusService status,
        ILogger<MediaImportWorkflow> logger,
        Func<Task>? showStatus = null)
    {
        _filePicker = filePicker;
        _mediaImportService = mediaImportService;
        _projectService = projectService;
        _analysisCoordinator = analysisCoordinator;
        _status = status;
        _logger = logger;
        // The picked files are checked on the UI thread; let the window render the "Importing…" status first.
        _showStatus = showStatus ?? (() => Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background).GetTask());
    }

    /// <summary>Status message when the project changed while the import ran (D027 §7).</summary>
    public const string ProjectChangedMessage = "Import stopped: another project was opened, so nothing was added.";

    public async Task RunAsync(CancellationToken ct = default)
    {
        // The import belongs to this project (D027 §7); checked after every await below.
        var project = _projectService.Current;

        var request = new FilePickerRequest
        {
            Title = "Import Media",
            AllowMultiple = true,
            FileTypeFilters = new[]
            {
                new FilePickerFileTypeFilter { Name = "Video files", Patterns = new[] { "*.mp4", "*.mov", "*.mkv", "*.webm", "*.avi" } },
                new FilePickerFileTypeFilter { Name = "Audio files", Patterns = new[] { "*.mp3", "*.wav", "*.flac", "*.aac", "*.m4a" } },
                new FilePickerFileTypeFilter { Name = "Image files", Patterns = new[] { "*.png", "*.jpg", "*.jpeg", "*.webp", "*.bmp" } },
                new FilePickerFileTypeFilter
                {
                    Name = "All supported media",
                    Patterns = new[]
                    {
                        "*.mp4", "*.mov", "*.mkv", "*.webm", "*.avi",
                        "*.mp3", "*.wav", "*.flac", "*.aac", "*.m4a",
                        "*.png", "*.jpg", "*.jpeg", "*.webp", "*.bmp"
                    }
                }
            }
        };

        var paths = await _filePicker.PickFilesAsync(request, ct);
        if (paths.Count == 0)
        {
            // User cancelled the dialog — a normal outcome, not an error. No status
            // spam; just note it in the log.
            _logger.LogInformation("Import Media: file picker was cancelled.");
            return;
        }
        if (ProjectChanged(project)) return;

        // Feedback only (D024 Step 9.8): the import itself is unchanged; the result message below replaces this one.
        _status.Report(paths.Count == 1 ? "Importing 1 file…" : $"Importing {paths.Count} files…");
        await _showStatus();
        if (ProjectChanged(project)) return;

        MediaImportBatchResult importResult;
        try
        {
            importResult = await _mediaImportService.ImportManyAsync(paths, ct);
        }
        catch
        {
            _status.Report("Import didn't finish.");
            throw;
        }
        if (ProjectChanged(project)) return;

        var addResult = _projectService.AddMediaAssets(importResult.Imported);

        // Fire-and-forget on purpose: analysis runs in the background and updates
        // each asset in place as it completes (see MediaAnalysisCoordinator).
        // Import itself must finish immediately — never block on analysis here.
        _analysisCoordinator.QueueAnalysis(addResult.Added);

        _status.Report(BuildStatusMessage(
            addResult.Added.Count,
            addResult.DuplicateCount,
            importResult.UnsupportedFiles.Count,
            importResult.MissingFiles.Count));
    }

    /// <summary>True — with the status message — when <paramref name="project"/>, the project the import started in, is no
    /// longer the current one: its files must not go into another project (D027 §7).</summary>
    private bool ProjectChanged(Core.Entities.Project project)
    {
        if (ReferenceEquals(project, _projectService.Current)) return false;
        _logger.LogInformation("Import Media: another project became current while importing; nothing was added.");
        _status.Report(ProjectChangedMessage);
        return true;
    }

    private static string BuildStatusMessage(int imported, int duplicates, int unsupported, int missing)
    {
        if (imported == 0 && duplicates == 0 && missing == 0 && unsupported > 0)
            return "No supported media files selected";

        var parts = new List<string>();

        if (imported > 0)
            parts.Add(imported == 1 ? "Imported 1 media file" : $"Imported {imported} media files");

        if (duplicates > 0)
            parts.Add(duplicates == 1
                ? "1 file skipped because it is already imported"
                : $"{duplicates} files skipped because they are already imported");

        if (unsupported > 0)
            parts.Add(unsupported == 1 ? "1 unsupported file skipped" : $"{unsupported} unsupported files skipped");

        if (missing > 0)
            parts.Add(missing == 1 ? "1 file skipped because it is missing" : $"{missing} files skipped because they are missing");

        return parts.Count > 0 ? string.Join("; ", parts) : "Nothing imported";
    }
}
