using AiVideoEditor.Core.Interfaces;
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
/// </summary>
public sealed class MediaImportWorkflow
{
    private readonly IFilePickerService _filePicker;
    private readonly IMediaImportService _mediaImportService;
    private readonly IProjectService _projectService;
    private readonly MediaAnalysisCoordinator _analysisCoordinator;
    private readonly StatusService _status;
    private readonly ILogger<MediaImportWorkflow> _logger;

    public MediaImportWorkflow(
        IFilePickerService filePicker,
        IMediaImportService mediaImportService,
        IProjectService projectService,
        MediaAnalysisCoordinator analysisCoordinator,
        StatusService status,
        ILogger<MediaImportWorkflow> logger)
    {
        _filePicker = filePicker;
        _mediaImportService = mediaImportService;
        _projectService = projectService;
        _analysisCoordinator = analysisCoordinator;
        _status = status;
        _logger = logger;
    }

    public async Task RunAsync(CancellationToken ct = default)
    {
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

        var importResult = await _mediaImportService.ImportManyAsync(paths, ct);
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
