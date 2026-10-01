using System.Collections.Concurrent;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace AiVideoEditor.UI.Services;

/// <summary>
/// Makes and keeps one kind of per-asset media data of the current project — thumbnails (D024 Step 9.4), waveforms
/// (Step 9.5) — between the project, the kind's service and the UI; no bitmaps or drawing here.
/// <list type="bullet">
/// <item>Which assets: the kind decides (<see cref="Candidates"/>, <see cref="MayHave"/>); offline media only ever gets a
/// cached result (never decoded). Requests are made on <see cref="IProjectService.MediaAssetsChanged"/> and whenever the
/// kind asks (<see cref="RequestAll"/>).</item>
/// <item>Generations: every project (<see cref="IProjectService.ProjectChanged"/> — New, Open, Recover) starts a new
/// one and cancels the previous one's work; a result of a cancelled generation is dropped — it is never stored and
/// raises no event, also when its work ended after the switch. Results are applied on the caller's context (the UI
/// thread in the app), the same thread that replaces the project. Save / Save As keep the generation: only the cache
/// folder changes, and later requests use the new one.</item>
/// <item>Once per asset and generation (by <see cref="MediaAsset.Id"/>): a request for an asset that is being handled,
/// has its result, or has none to make is ignored — until the asset is restarted (<see cref="Restart"/>): an asset whose
/// file has come back (<see cref="IProjectService.MediaAvailabilityChanged"/>, D026 §2) is requested again in the same
/// generation (now it may be made, not only read from the cache). Its earlier result stays shown until the new one is
/// ready; work started for it before the restart publishes nothing.</item>
/// <item>A cached result is read without a slot and never decodes; making one takes one of this kind's own slots
/// (the limit of one kind never holds up the other) — the wait ends with the generation. Cache reads and making run
/// on the thread pool.</item>
/// </list>
/// </summary>
public abstract class MediaCacheCoordinator<T> where T : class
{
    private readonly IProjectService _projects;
    private readonly string _kind;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _slots;
    private readonly ConcurrentDictionary<Task, byte> _running = new();
    private Generation _generation = new();

    /// <param name="maxConcurrent">Results made (decoded) at the same time.</param>
    /// <param name="kind">The kind in log messages (<c>thumbnail</c>, <c>waveform</c>).</param>
    protected MediaCacheCoordinator(IProjectService projects, int maxConcurrent, string kind, ILogger logger)
    {
        _projects = projects;
        _kind = kind;
        _logger = logger;
        _slots = new SemaphoreSlim(maxConcurrent, maxConcurrent);
        projects.ProjectChanged += (_, _) => StartNewGeneration();
        projects.MediaAssetsChanged += (_, _) => RequestAll();
        projects.MediaAvailabilityChanged += (_, e) => Restart(e.Returned.Select(a => a.Id));
    }

    /// <summary>Forgets that these assets were handled in the current generation and requests them again. Their results
    /// stay until replaced; work already running for them publishes nothing (it belongs to the handling before).</summary>
    internal void Restart(IEnumerable<Guid> assetIds)
    {
        var generation = _generation;
        var any = false;
        foreach (var id in assetIds)
            any |= generation.Handled.TryRemove(id, out _);
        if (any)
            RequestAll();
    }

    /// <summary>Raised (on the caller's context) with the asset id when its result is ready — only for the current
    /// project.</summary>
    public event EventHandler<Guid>? Ready;

    /// <summary>The result for an asset of the current project, if it is ready.</summary>
    public T? Get(Guid assetId) => _generation.Results.TryGetValue(assetId, out var result) ? result : null;

    /// <summary>Completes when every piece of work started so far has ended (tests).</summary>
    internal async Task IdleAsync()
    {
        // A piece of work leaves _running in a continuation that runs after the work itself has completed — and possibly
        // after WhenAll noticed it; wait until the bookkeeping is empty too.
        while (!_running.IsEmpty)
        {
            await Task.WhenAll(_running.Keys);
            await Task.Yield();
        }
    }

    /// <summary>Work started and not ended — reading the cache, waiting for a slot or making a result (tests).</summary>
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
            _logger.LogDebug(ex, "The {Kind} work did not end cleanly at shutdown.", _kind);
        }
    }

    /// <summary>The assets of <paramref name="project"/> this kind is wanted for.</summary>
    protected abstract IEnumerable<MediaAsset> Candidates(Core.Entities.Project project);

    /// <summary>A result can exist for the asset: made, or cached for offline media.</summary>
    protected abstract bool MayHave(MediaAsset asset);

    /// <summary>The cache folder of the current project, read per request.</summary>
    protected abstract string CurrentFolder { get; }

    protected abstract T? TryGetCached(MediaAsset asset, string folder);

    protected abstract Task<T?> GetOrCreateAsync(MediaAsset asset, string folder, CancellationToken ct);

    /// <summary>Requests every candidate of the current project (each once per generation).</summary>
    protected void RequestAll()
    {
        foreach (var asset in Candidates(_projects.Current))
            Request(asset);
    }

    private void StartNewGeneration()
    {
        var previous = _generation;
        _generation = new Generation();
        previous.Cancel(); // its work ends on its own; its results are dropped
    }

    private void Request(MediaAsset asset)
    {
        var generation = _generation;
        var handling = new object();
        if (generation.IsCancelled || !MayHave(asset) || !generation.Handled.TryAdd(asset.Id, handling))
            return;

        // Captured now, on the caller's thread: the folder of this project as it is now.
        Track(HandleAsync(asset, CanMake(asset), CurrentFolder, generation, handling));
    }

    /// <summary>A result may be made (decoded) — not for offline media.</summary>
    private static bool CanMake(MediaAsset asset) => !asset.IsMissing;

    private async Task HandleAsync(MediaAsset asset, bool canMake, string folder, Generation generation, object handling)
    {
        var token = generation.Token;
        var slot = false;
        try
        {
            var result = await Task.Run(() => TryGetCached(asset, folder), token);
            if (result is null && canMake)
            {
                await _slots.WaitAsync(token);
                slot = true;
                token.ThrowIfCancellationRequested(); // a slot freed by the cancelled generation must not start work
                result = await Task.Run(() => GetOrCreateAsync(asset, folder, token), token);
            }

            if (token.IsCancellationRequested)
                return; // another project is current now: publish nothing
            if (!generation.Handled.TryGetValue(asset.Id, out var current) || !ReferenceEquals(current, handling))
                return; // the asset was restarted meanwhile: a newer handling publishes its result
            if (result is null)
                return; // none to show (offline without a cache, undecodable) — not asked again in this generation

            generation.Results[asset.Id] = result;
            Ready?.Invoke(this, asset.Id);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            _logger.LogDebug("The {Kind} of '{Path}' dropped: its project was replaced or the app is closing.", _kind, asset.FilePath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "No {Kind} for '{Path}'.", _kind, asset.FilePath);
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

    /// <summary>One project's results: its cancellation, the assets already handled (in work, done or with none), and
    /// the results made.</summary>
    private sealed class Generation
    {
        private readonly CancellationTokenSource _cancellation = new();

        public CancellationToken Token => _cancellation.Token;
        public bool IsCancelled => _cancellation.IsCancellationRequested;
        /// <summary>Per asset id, the handling in charge of it (its identity tells a restarted asset's work apart).</summary>
        public ConcurrentDictionary<Guid, object> Handled { get; } = new();
        public ConcurrentDictionary<Guid, T> Results { get; } = new();

        // Not disposed: its token is still held by the work it cancels (which ends on its own).
        public void Cancel() => _cancellation.Cancel();
    }
}
