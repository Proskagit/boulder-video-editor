using System.Collections.Concurrent;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace AiVideoEditor.UI.Services;

/// <summary>
/// Kicks off background metadata analysis for newly-imported media and applies
/// the result in place on the same <see cref="MediaAsset"/> instance. Never
/// awaited by its caller — <see cref="QueueAnalysis(MediaAsset)"/> returns
/// immediately so import stays instant and the UI thread is never blocked, per
/// the Phase 3 rule against freezing on large files. A per-asset in-flight guard
/// stops duplicate analysis tasks from being started for the same file (e.g. if
/// something ever calls this twice before the first run finishes).
/// </summary>
public sealed class MediaAnalysisCoordinator
{
    private readonly IMediaAnalysisService _analysisService;
    private readonly IProjectService _projectService;
    private readonly ILogger<MediaAnalysisCoordinator> _logger;
    private readonly ConcurrentDictionary<Guid, byte> _inFlight = new();

    public MediaAnalysisCoordinator(
        IMediaAnalysisService analysisService,
        IProjectService projectService,
        ILogger<MediaAnalysisCoordinator> logger)
    {
        _analysisService = analysisService;
        _projectService = projectService;
        _logger = logger;
    }

    public void QueueAnalysis(IEnumerable<MediaAsset> assets)
    {
        foreach (var asset in assets)
            QueueAnalysis(asset);
    }

    /// <summary>Queues only the assets that still need metadata: not analysed yet
    /// (<see cref="MediaAnalysisStatus.Pending"/>) and present on disk. Used after a project
    /// is opened — assets whose saved metadata was loaded are already
    /// <see cref="MediaAnalysisStatus.Completed"/>. Returns how many were queued.</summary>
    public int QueueWhereNeeded(IEnumerable<MediaAsset> assets)
    {
        var queued = 0;
        foreach (var asset in assets)
        {
            if (asset.AnalysisStatus != MediaAnalysisStatus.Pending || asset.IsMissing)
                continue;
            QueueAnalysis(asset);
            queued++;
        }
        return queued;
    }

    public void QueueAnalysis(MediaAsset asset)
    {
        if (asset.IsMissing)
        {
            // ffprobe can't succeed on a file that isn't there; the asset stays as it is
            // (offline) instead of being turned into a failed analysis.
            _logger.LogDebug("Not analysing missing media '{Path}'.", asset.FilePath);
            return;
        }

        if (!_inFlight.TryAdd(asset.Id, 0))
        {
            _logger.LogDebug("Analysis already in progress for '{Path}'; skipping duplicate request.", asset.FilePath);
            return;
        }

        asset.AnalysisStatus = MediaAnalysisStatus.Analyzing;
        _projectService.NotifyMediaAssetsChanged();

        // Deliberately fire-and-forget: the caller (import workflow) must not
        // wait for analysis to finish. Exceptions are handled inside AnalyzeAndApplyAsync
        // itself, so nothing here can produce an unobserved task exception.
        _ = AnalyzeAndApplyAsync(asset);
    }

    private async Task AnalyzeAndApplyAsync(MediaAsset asset)
    {
        try
        {
            var result = await _analysisService.AnalyzeAsync(asset.FilePath);

            if (result.Outcome == MediaAnalysisOutcome.Success)
            {
                asset.Metadata = result.Metadata;
                asset.AnalysisStatus = MediaAnalysisStatus.Completed;
                asset.AnalysisError = null;
            }
            else
            {
                asset.AnalysisStatus = MediaAnalysisStatus.Failed;
                asset.AnalysisError = result.ErrorMessage;
                _logger.LogWarning(
                    "Metadata analysis failed for '{Path}': {Outcome} — {Message}",
                    asset.FilePath, result.Outcome, result.ErrorMessage);
            }
        }
        catch (Exception ex)
        {
            // AnalyzeAsync already catches everything it knows how to categorize;
            // this is a last-resort net so a truly unexpected failure still leaves
            // the asset in a clean Failed state instead of stuck "Analyzing" forever.
            asset.AnalysisStatus = MediaAnalysisStatus.Failed;
            asset.AnalysisError = "Something went wrong while analyzing this file.";
            _logger.LogError(ex, "Unexpected error analyzing '{Path}'.", asset.FilePath);
        }
        finally
        {
            _inFlight.TryRemove(asset.Id, out _);
            _projectService.NotifyMediaAssetsChanged();
        }
    }
}
