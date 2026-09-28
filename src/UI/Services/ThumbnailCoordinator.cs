using System.Collections.Concurrent;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace AiVideoEditor.UI.Services;

/// <summary>
/// Makes and keeps the Media Browser's thumbnails of the current project (D024 Step 9.4) — the orchestration between
/// the project's media, <see cref="IThumbnailService"/> and the UI; no bitmaps here (that is the view's, 9.4d).
/// <list type="bullet">
/// <item>Which assets: video and images with completed analysis get a thumbnail made; offline media only shows a
/// cached one (never decoded); audio, pending or failed analysis get none. Analysis itself stays
/// <see cref="MediaAnalysisCoordinator"/>'s — a thumbnail is requested when an asset's analysis has completed
/// (<see cref="IProjectService.MediaAssetsChanged"/>).</item>
/// <item>Generations: every project (<see cref="IProjectService.ProjectChanged"/> — New, Open, Recover) starts a new
/// one and cancels the previous one's work; a result of a cancelled generation is dropped — it is never stored and
/// raises no event, also when its work ended after the switch. Results are applied on the caller's context (the UI
/// thread in the app), the same thread that replaces the project. Save / Save As keep the generation: only the cache
/// folder changes, and later requests use the new one.</item>
/// <item>Once per asset and generation (by <see cref="MediaAsset.Id"/>): a request for an asset that is being handled,
/// has its thumbnail, or has none to make is ignored.</item>
/// <item>A cached thumbnail is read without a slot and never decodes; making one takes one of
/// <see cref="MaxConcurrentGenerations"/> slots (an implementation detail, not configurable) — the wait ends with the
/// generation. Cache reads and decoding run on the thread pool.</item>
/// </list>
/// </summary>
public sealed class ThumbnailCoordinator
{
    /// <summary>Thumbnails made (decoded) at the same time (product owner, PO-4).</summary>
    internal const int MaxConcurrentGenerations = 2;

    private readonly IThumbnailService _thumbnails;
    private readonly IProjectService _projects;
    private readonly IThumbnailCacheLocation _location;
    private readonly ILogger<ThumbnailCoordinator> _logger;
    private readonly SemaphoreSlim _slots = new(MaxConcurrentGenerations, MaxConcurrentGenerations);
    private readonly ConcurrentDictionary<Task, byte> _running = new();
    private Generation _generation = new();

    public ThumbnailCoordinator(IThumbnailService thumbnails, IProjectService projects, IThumbnailCacheLocation location,
        ILogger<ThumbnailCoordinator> logger)
    {
        _thumbnails = thumbnails;
        _projects = projects;
        _location = location;
        _logger = logger;
        projects.ProjectChanged += (_, _) => StartNewGeneration();
        projects.MediaAssetsChanged += (_, _) => RequestAll();
        RequestAll();
    }

    /// <summary>Raised (on the caller's context) with the asset id when its thumbnail is ready — only for the current
    /// project.</summary>
    public event EventHandler<Guid>? ThumbnailReady;

    /// <summary>The thumbnail of an asset of the current project, if it is ready.</summary>
    public Thumbnail? Get(Guid assetId) => _generation.Results.TryGetValue(assetId, out var thumbnail) ? thumbnail : null;

    /// <summary>Completes when every piece of work started so far has ended (tests).</summary>
    internal Task IdleAsync() => Task.WhenAll(_running.Keys);

    /// <summary>Work started and not ended — reading the cache, waiting for a slot or making a thumbnail (tests).</summary>
    internal int RunningCount => _running.Count;

    /// <summary>The app is closing: cancels all work and waits (up to 5 s) until it has ended, so no decoding outlives
    /// the window. Never throws.</summary>
    public async Task ShutdownAsync()
    {
        _generation.Cancel();
        try
        {
            await IdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Thumbnail work did not end cleanly at shutdown.");
        }
    }

    private void StartNewGeneration()
    {
        var previous = _generation;
        _generation = new Generation();
        previous.Cancel(); // its work ends on its own; its results are dropped
    }

    private void RequestAll()
    {
        foreach (var asset in _projects.Current.MediaAssets)
            Request(asset);
    }

    private void Request(MediaAsset asset)
    {
        var generation = _generation;
        if (generation.IsCancelled || !MayHaveThumbnail(asset) || !generation.Handled.TryAdd(asset.Id, 0))
            return;

        // Captured now, on the caller's thread: the folder of this project as it is now.
        Track(HandleAsync(asset, CanMake(asset), _location.CurrentFolder, generation));
    }

    /// <summary>Video or image: a thumbnail can exist (made, or cached for offline media).</summary>
    private static bool MayHaveThumbnail(MediaAsset asset) =>
        asset.Kind is MediaKind.Video or MediaKind.Image &&
        (asset.IsMissing || asset.AnalysisStatus == MediaAnalysisStatus.Completed && asset.Metadata is not null);

    /// <summary>A thumbnail may be made (decoded) — not for offline media.</summary>
    private static bool CanMake(MediaAsset asset) => !asset.IsMissing;

    private async Task HandleAsync(MediaAsset asset, bool canMake, string folder, Generation generation)
    {
        var token = generation.Token;
        var slot = false;
        try
        {
            var thumbnail = await Task.Run(() => _thumbnails.TryGetCached(asset, folder), token);
            if (thumbnail is null && canMake)
            {
                await _slots.WaitAsync(token);
                slot = true;
                token.ThrowIfCancellationRequested(); // a slot freed by the cancelled generation must not start work
                thumbnail = await Task.Run(() => _thumbnails.GetOrCreateAsync(asset, folder, token), token);
            }

            if (token.IsCancellationRequested)
                return; // another project is current now: publish nothing
            if (thumbnail is null)
                return; // none to show (offline without a cache, undecodable) — not asked again in this generation

            generation.Results[asset.Id] = thumbnail;
            ThumbnailReady?.Invoke(this, asset.Id);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            _logger.LogDebug("Thumbnail of '{Path}' dropped: its project was replaced or the app is closing.", asset.FilePath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "No thumbnail for '{Path}'.", asset.FilePath);
        }
        finally
        {
            if (slot) _slots.Release();
        }
    }

    private void Track(Task work)
    {
        if (work.IsCompleted) return;
        _running.TryAdd(work, 0);
        _ = work.ContinueWith(t => _running.TryRemove(t, out _), CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    /// <summary>One project's thumbnails: its cancellation, the assets already handled (in work, done or with none),
    /// and the thumbnails made.</summary>
    private sealed class Generation
    {
        private readonly CancellationTokenSource _cancellation = new();

        public CancellationToken Token => _cancellation.Token;
        public bool IsCancelled => _cancellation.IsCancellationRequested;
        public ConcurrentDictionary<Guid, byte> Handled { get; } = new();
        public ConcurrentDictionary<Guid, Thumbnail> Results { get; } = new();

        // Not disposed: its token is still held by the work it cancels (which ends on its own).
        public void Cancel() => _cancellation.Cancel();
    }
}
