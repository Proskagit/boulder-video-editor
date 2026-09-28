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
/// stops duplicate analysis tasks from being started for the same asset object (e.g. if
/// something ever calls this twice before the first run finishes).
/// <para>
/// Analyses belong to a project generation (D024 Step 9.3): whenever another project becomes current
/// (<see cref="IProjectService.ProjectChanged"/> — New, Open, Recover) the running generation is cancelled, which
/// ends its ffprobe processes, and a new one starts. A result of a cancelled generation is dropped: it changes no
/// asset and raises no event. Results are applied on the UI thread (the caller's context), the same thread that
/// replaces the project, so a result either belongs to the current project or is dropped — never applied to the
/// next one. The guard is per asset object, not per id: a project reopened while its own analysis still runs
/// has new asset objects with the same ids, and they are analysed again.
/// </para>
/// <para>
/// At most <see cref="MaxConcurrentAnalyses"/> analyses run at once (D024 Step 9.3; an implementation detail, not
/// configurable). An analysis holds its slot for its whole probe — every ffprobe it runs, the orientation probe
/// included, runs one after another inside it — so no more ffprobe processes than that run at a time. The others
/// wait, already shown as <see cref="MediaAnalysisStatus.Analyzing"/>, in the order they were queued; ffprobe's own
/// timeout starts only when the probe runs. Cancelling a generation also ends its waits, so a replaced project's
/// queue never holds a slot the next project needs.
/// </para>
/// </summary>
public sealed class MediaAnalysisCoordinator
{
    /// <summary>Analyses that run at the same time. Measured on generated media: 24 files took 788 ms one at a time,
    /// 312 ms four at a time and 232 ms eight at a time — past four the gain is small, while every further process
    /// competes with playback's decoders and, on a slow disk or share, with the other probes for their timeout.</summary>
    internal const int MaxConcurrentAnalyses = 4;

    private readonly SemaphoreSlim _slots = new(MaxConcurrentAnalyses, MaxConcurrentAnalyses);
    private readonly IMediaAnalysisService _analysisService;
    private readonly IProjectService _projectService;
    private readonly ILogger<MediaAnalysisCoordinator> _logger;
    private readonly ConcurrentDictionary<MediaAsset, byte> _inFlight = new(ReferenceEqualityComparer.Instance);
    private readonly ConcurrentDictionary<Task, byte> _running = new();
    private CancellationTokenSource _generation = new();

    public MediaAnalysisCoordinator(
        IMediaAnalysisService analysisService,
        IProjectService projectService,
        ILogger<MediaAnalysisCoordinator> logger)
    {
        _analysisService = analysisService;
        _projectService = projectService;
        _logger = logger;
        _projectService.ProjectChanged += (_, _) => StartNewGeneration();
    }

    /// <summary>Completes when every analysis started so far has finished or been dropped (tests).</summary>
    internal Task IdleAsync() => Task.WhenAll(_running.Keys);

    /// <summary>Analyses started and not yet ended, waiting for a slot or probing (tests).</summary>
    internal int RunningCount => _running.Count;

    private void StartNewGeneration()
    {
        var previous = _generation;
        _generation = new CancellationTokenSource();
        // Not disposed: its token is still held by the analyses it cancels (they end on their own).
        previous.Cancel();
    }

    public void QueueAnalysis(IEnumerable<MediaAsset> assets)
    {
        foreach (var asset in assets)
            QueueAnalysis(asset);
    }

    /// <summary>Queues only the assets that still need metadata: not analysed yet
    /// (<see cref="MediaAnalysisStatus.Pending"/>) and present on disk. Used after a project
    /// is opened — assets whose saved metadata was loaded are already
    /// <see cref="MediaAnalysisStatus.Completed"/>; those whose metadata lacks the display size
    /// (saved before Phase 7) are refreshed silently. Returns how many were queued.</summary>
    public int QueueWhereNeeded(IEnumerable<MediaAsset> assets)
    {
        var queued = 0;
        foreach (var asset in assets)
        {
            if (asset.IsMissing)
                continue;
            if (asset.AnalysisStatus == MediaAnalysisStatus.Pending)
            {
                QueueAnalysis(asset);
                queued++;
            }
            else if (asset.AnalysisStatus == MediaAnalysisStatus.Completed && asset.Metadata is { NeedsDisplaySizeProbe: true })
            {
                RefreshDisplaySize(asset);
                queued++;
            }
        }
        return queued;
    }

    /// <summary>
    /// Metadata saved before orientation was probed (Phase 7) has a coded size but no display size.
    /// Probes the file again in the background without changing the asset's state: the asset stays
    /// Completed and usable, a successful probe replaces its metadata (a runtime refresh — the project
    /// does not become dirty), a failed one keeps the saved metadata and is only logged.
    /// </summary>
    private void RefreshDisplaySize(MediaAsset asset)
    {
        if (!_inFlight.TryAdd(asset, 0))
            return;
        Track(RefreshDisplaySizeAsync(asset, _generation.Token));
    }

    private async Task RefreshDisplaySizeAsync(MediaAsset asset, CancellationToken generation)
    {
        var slot = false;
        try
        {
            await _slots.WaitAsync(generation);
            slot = true;
            generation.ThrowIfCancellationRequested(); // see AnalyzeAndApplyAsync
            var result = await _analysisService.AnalyzeAsync(asset.FilePath, generation);
            if (generation.IsCancellationRequested)
                return; // another project is current now: this asset is no longer shown anywhere

            if (result.Outcome == MediaAnalysisOutcome.Success && result.Metadata is { } metadata)
            {
                asset.Metadata = metadata;
                _projectService.NotifyMediaAssetsChanged();
            }
            else
            {
                _logger.LogWarning("Could not refresh the orientation of '{Path}' ({Outcome}); keeping its saved metadata.",
                    asset.FilePath, result.Outcome);
            }
        }
        catch (Exception ex) when (!generation.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Could not refresh the orientation of '{Path}'; keeping its saved metadata.", asset.FilePath);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Orientation refresh of '{Path}' ended after its project was replaced.", asset.FilePath);
        }
        finally
        {
            if (slot) _slots.Release();
            _inFlight.TryRemove(asset, out _);
        }
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

        if (!_inFlight.TryAdd(asset, 0))
        {
            _logger.LogDebug("Analysis already in progress for '{Path}'; skipping duplicate request.", asset.FilePath);
            return;
        }

        asset.AnalysisStatus = MediaAnalysisStatus.Analyzing;
        _projectService.NotifyMediaAssetsChanged();

        // Deliberately fire-and-forget: the caller (import workflow) must not
        // wait for analysis to finish. Exceptions are handled inside AnalyzeAndApplyAsync
        // itself, so nothing here can produce an unobserved task exception.
        Track(AnalyzeAndApplyAsync(asset, _generation.Token));
    }

    private async Task AnalyzeAndApplyAsync(MediaAsset asset, CancellationToken generation)
    {
        var slot = false;
        try
        {
            await _slots.WaitAsync(generation); // queued: the asset already shows "Analyzing"
            slot = true;
            // The slot may come from an analysis that ended because this generation was cancelled, before this wait's
            // own cancellation ran: never start a probe for a replaced project.
            generation.ThrowIfCancellationRequested();
            var result = await _analysisService.AnalyzeAsync(asset.FilePath, generation);
            if (generation.IsCancellationRequested)
            {
                // Another project is current now. Whatever the probe returned (a result that raced the
                // cancellation included), this asset belongs to the replaced project: leave it, tell nobody.
                _logger.LogDebug("Analysis of '{Path}' dropped: its project was replaced.", asset.FilePath);
                return;
            }

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
            _projectService.NotifyMediaAssetsChanged();
        }
        catch (Exception ex) when (!generation.IsCancellationRequested)
        {
            // AnalyzeAsync already catches everything it knows how to categorize;
            // this is a last-resort net so a truly unexpected failure still leaves
            // the asset in a clean Failed state instead of stuck "Analyzing" forever.
            asset.AnalysisStatus = MediaAnalysisStatus.Failed;
            asset.AnalysisError = "Something went wrong while analyzing this file.";
            _logger.LogError(ex, "Unexpected error analyzing '{Path}'.", asset.FilePath);
            _projectService.NotifyMediaAssetsChanged();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Analysis of '{Path}' ended after its project was replaced.", asset.FilePath);
        }
        finally
        {
            if (slot) _slots.Release();
            _inFlight.TryRemove(asset, out _);
        }
    }

    private void Track(Task analysis)
    {
        if (analysis.IsCompleted) return;
        _running.TryAdd(analysis, 0);
        _ = analysis.ContinueWith(t => _running.TryRemove(t, out _), CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }
}
