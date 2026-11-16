using System.Collections.Immutable;
using System.Globalization;
using AiVideoEditor.Core.Color;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Core.Localization;
using AiVideoEditor.Core.People;
using AiVideoEditor.Core.Playback;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiVideoEditor.UI.Services;

/// <summary>Which people analysis of a video (D061 §5).</summary>
public enum PeopleAnalysisKind
{
    /// <summary>The people and their tracks (Follow; the prompts of the masks).</summary>
    Tracks,

    /// <summary>Every person's mask (Occlusion).</summary>
    Masks
}

/// <summary>The state of one people analysis of a video, for the Inspector, the Preview and the export.</summary>
public enum PeopleAnalysisState
{
    /// <summary>Not wanted: no clip of the video uses Follow / Occlusion, or the media is offline.</summary>
    None,

    /// <summary>Wanted and not started yet (another analysis runs, or the tracks it needs are not ready).</summary>
    Waiting,

    /// <summary>Running (<see cref="PeopleAnalysisStatus.Progress"/>).</summary>
    Analysing,

    /// <summary>Done (from the cache or analysed).</summary>
    Ready,

    /// <summary>Stopped by the user; <see cref="PeopleAnalysisCoordinator.Retry"/> starts it again.</summary>
    Cancelled,

    /// <summary>Failed, or unavailable on this computer (<see cref="PeopleAnalysisStatus.Message"/>): no model, no GPU for the
    /// masks, a decoding error. Never silent: the Inspector shows it and the export warns.</summary>
    Failed
}

/// <summary>One analysis's state, its progress (0..1 while analysing) and, when failed, the message.</summary>
public readonly record struct PeopleAnalysisStatus(PeopleAnalysisState State, double Progress = 0, string? Message = null)
{
    public bool IsBusy => State is PeopleAnalysisState.Waiting or PeopleAnalysisState.Analysing;
}

/// <summary>
/// The people analyses of the project's videos (D061 §5): whenever a video clip uses Follow (needs the tracks) or hides its
/// outlines behind people (needs the tracks, then the masks), its video is analysed in the background — one analysis at a
/// time, the whole file once (so Trim, Split, Slip and Speed never analyse again), never blocking the UI. Like the
/// camera-motion tracks the results are derived from the video alone, never part of the project (no Undo step): kept in
/// memory by the file (path, size, last write time) and between sessions in <see cref="IPeopleAnalysisCache"/> by the file's
/// content, the model and the analysis version — an unchanged video is never analysed again; a replaced, edited or relinked
/// file is a new analysis.
/// <para>Progress, the user's cancel and retry (<see cref="Cancel"/>, <see cref="Retry"/>), failures with a message (no
/// model, no GPU for the masks, a decoder error) — never silent. A project change cancels everything running. Runs on the
/// caller's (UI) context; the analysis itself on the thread pool.</para>
/// </summary>
public sealed class PeopleAnalysisCoordinator : IDisposable
{
    private readonly IProjectService _projects;
    private readonly IVideoDecoder _decoder;
    private readonly IPeopleVision? _vision;
    private readonly IPeopleAnalysisCache? _cache;
    private readonly ILogger _logger;
    private readonly Dictionary<Request, object> _results = new();          // PersonTracks / PersonMaskTrack / Failure
    private readonly HashSet<Request> _cancelled = new();
    private Dictionary<Guid, (Request? Tracks, Request? Masks)> _wanted = new();
    private (Request Request, double Progress, CancellationTokenSource Cancel)? _running;
    private CancellationTokenSource _projectCancel = new();
    private Task? _worker;
    private bool _disposed;

    private sealed record Failure(string Message);

    public PeopleAnalysisCoordinator(IProjectService projects, IVideoDecoder decoder, IPeopleVision? vision = null,
        IPeopleAnalysisCache? cache = null, ILogger<PeopleAnalysisCoordinator>? logger = null)
    {
        _projects = projects;
        _decoder = decoder;
        _vision = vision;
        _cache = cache;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
        _projects.ProjectChanged += (_, _) =>
        {
            _projectCancel.Cancel();
            _projectCancel = new CancellationTokenSource();
            _cancelled.Clear();
            Refresh();
        };
        _projects.TimelineChanged += (_, _) => Refresh();
        _projects.MediaAssetsChanged += (_, _) => Refresh();
        Refresh();
    }

    /// <summary>Raised on the caller's context when an analysis result appeared or went away.</summary>
    public event EventHandler? AnalysesChanged;

    /// <summary>Raised on the caller's context whenever a state or a progress may have changed.</summary>
    public event EventHandler? StatusChanged;

    /// <summary>The finished analyses of the project's videos (what the snapshot draws with).</summary>
    public PeopleAnalyses Analyses { get; private set; } = PeopleAnalyses.None;

    /// <summary>The key of one analysis: the file (path, size, last write time), its start, HDR rule, nominal rate,
    /// duration, and the kind.</summary>
    private sealed record Request(string FilePath, long FileLength, long FileWriteTicks, MediaTime FileStart, HdrConversion? Hdr,
        FrameRate? NominalRate, MediaTime Duration, PeopleAnalysisKind Kind)
    {
        public PeopleAnalyzer.Source Source => new(FilePath, FileStart, Hdr, NominalRate, Duration);

        public Request TracksRequest => this with { Kind = PeopleAnalysisKind.Tracks };

        /// <summary>The key between sessions: the file's content instead of its path, the model and the analysis.</summary>
        public string CacheKey(string fingerprint, string modelId) => string.Join('|',
            Kind == PeopleAnalysisKind.Tracks ? $"tracks-v{PersonTracks.AnalysisVersion}" : $"masks-v{PersonMaskTrack.AnalysisVersion}-t{PersonTracks.AnalysisVersion}",
            modelId, fingerprint, FileStart.Ticks.ToString(CultureInfo.InvariantCulture), Hdr?.ToString() ?? "sdr",
            PeopleAnalyzer.AnalysisLongSide.ToString(CultureInfo.InvariantCulture),
            (Kind == PeopleAnalysisKind.Tracks ? PeopleAnalyzer.DetectionInterval : PeopleAnalyzer.MaskInterval).Ticks.ToString(CultureInfo.InvariantCulture),
            PeopleAnalyzer.MaskLongSide.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>The state of <paramref name="kind"/> for <paramref name="assetId"/>.</summary>
    public PeopleAnalysisStatus StatusOf(Guid assetId, PeopleAnalysisKind kind)
    {
        if (!_wanted.TryGetValue(assetId, out var wanted)) return new PeopleAnalysisStatus(PeopleAnalysisState.None);
        var request = kind == PeopleAnalysisKind.Tracks ? wanted.Tracks : wanted.Masks;
        if (request is null) return new PeopleAnalysisStatus(PeopleAnalysisState.None);
        if (_results.TryGetValue(request, out var result))
            return result is Failure f ? new PeopleAnalysisStatus(PeopleAnalysisState.Failed, 0, f.Message) : new PeopleAnalysisStatus(PeopleAnalysisState.Ready, 1);
        if (_cancelled.Contains(request)) return new PeopleAnalysisStatus(PeopleAnalysisState.Cancelled);
        if (kind == PeopleAnalysisKind.Masks && _results.TryGetValue(request.TracksRequest, out var tracks) && tracks is Failure tf)
            return new PeopleAnalysisStatus(PeopleAnalysisState.Failed, 0, tf.Message);
        if (_cancelled.Contains(request.TracksRequest)) return new PeopleAnalysisStatus(PeopleAnalysisState.Cancelled);
        return _running is { } running && running.Request == request
            ? new PeopleAnalysisStatus(PeopleAnalysisState.Analysing, running.Progress)
            : new PeopleAnalysisStatus(PeopleAnalysisState.Waiting);
    }

    /// <summary>Stops the analyses of <paramref name="assetId"/> (running or waiting); they stay stopped until <see cref="Retry"/>.</summary>
    public void Cancel(Guid assetId)
    {
        if (!_wanted.TryGetValue(assetId, out var wanted)) return;
        foreach (var request in new[] { wanted.Tracks, wanted.Masks })
        {
            if (request is null || _results.ContainsKey(request)) continue;
            _cancelled.Add(request);
            if (_running is { } running && running.Request == request) running.Cancel.Cancel();
        }
        Publish();
    }

    /// <summary>Starts the stopped or failed analyses of <paramref name="assetId"/> again (a model installed meanwhile, a file
    /// that was busy).</summary>
    public void Retry(Guid assetId)
    {
        if (!_wanted.TryGetValue(assetId, out var wanted)) return;
        foreach (var request in new[] { wanted.Tracks, wanted.Masks })
        {
            if (request is null) continue;
            _cancelled.Remove(request);
            if (_results.TryGetValue(request, out var result) && result is Failure) _results.Remove(request);
        }
        Publish();
        StartWorker();
    }

    /// <summary>What the export's preflight uses: the Preview's own analyses, the assets still analysed (waiting, running or
    /// stopped — the export never draws without them), the failed ones.</summary>
    public Core.Export.ExportPeople ForExport()
    {
        var pending = new HashSet<Guid>();
        var failedTracks = new Dictionary<Guid, string>();
        var failedMasks = new Dictionary<Guid, string>();
        foreach (var (assetId, wanted) in _wanted)
        {
            var tracks = StatusOf(assetId, PeopleAnalysisKind.Tracks);
            if (tracks.State == PeopleAnalysisState.Failed) failedTracks[assetId] = tracks.Message ?? "";
            else if (tracks.State != PeopleAnalysisState.Ready) pending.Add(assetId);
            if (wanted.Masks is null) continue;
            var masks = StatusOf(assetId, PeopleAnalysisKind.Masks);
            if (masks.State == PeopleAnalysisState.Failed) failedMasks[assetId] = masks.Message ?? "";
            else if (masks.State != PeopleAnalysisState.Ready && tracks.State != PeopleAnalysisState.Failed) pending.Add(assetId);
        }
        return new Core.Export.ExportPeople(Analyses, pending) { FailedTracks = failedTracks, FailedMasks = failedMasks };
    }

    private void Refresh()
    {
        if (_disposed) return;
        var project = _projects.Current;
        var assets = project.MediaAssets.ToDictionary(a => a.Id);
        var wanted = new Dictionary<Guid, (Request?, Request?)>();
        foreach (var clip in project.Timeline.VideoTracks.SelectMany(t => t.Clips).OfType<VideoClip>())
        {
            if (clip.Follow is null && !clip.HidesOutlinesBehindPeople) continue;
            if (!assets.TryGetValue(clip.MediaAssetId, out var asset) || asset.IsMissing || asset.Kind != MediaKind.Video ||
                asset.Metadata is not { } metadata)
                continue;
            var (length, written) = Stat(asset.FilePath);
            var tracks = new Request(asset.FilePath, length, written, metadata.StartTime ?? MediaTime.Zero,
                HdrColor.Classify(asset.Kind, metadata).Conversion, SourceFrameSelector.NominalRate(metadata), metadata.Duration, PeopleAnalysisKind.Tracks);
            var previous = wanted.GetValueOrDefault(asset.Id);
            wanted[asset.Id] = (tracks, previous.Item2 ?? (clip.HidesOutlinesBehindPeople ? tracks with { Kind = PeopleAnalysisKind.Masks } : null));
        }
        _wanted = wanted;
        // A running analysis no clip wants any more stops (Follow switched off, the clip deleted).
        if (_running is { } running && !_wanted.Values.Any(w => w.Tracks == running.Request || w.Masks == running.Request))
            running.Cancel.Cancel();
        Publish();
        StartWorker();
    }

    private static (long Length, long WriteTicks) Stat(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? (info.Length, info.LastWriteTimeUtc.Ticks) : (-1, 0);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return (-1, 0);
        }
    }

    private void StartWorker()
    {
        if (!_disposed && (_worker is null || _worker.IsCompleted) && NextRequest() is not null)
            _worker = RunAsync(_projectCancel);
    }

    /// <summary>A wanted analysis without a result that can start: tracks first, masks once their tracks are there.</summary>
    private Request? NextRequest()
    {
        foreach (var (tracks, _) in _wanted.Values)
            if (tracks is not null && !_results.ContainsKey(tracks) && !_cancelled.Contains(tracks)) return tracks;
        foreach (var (_, masks) in _wanted.Values)
            if (masks is not null && !_results.ContainsKey(masks) && !_cancelled.Contains(masks) &&
                _results.TryGetValue(masks.TracksRequest, out var t) && t is PersonTracks)
                return masks;
        return null;
    }

    private void Publish()
    {
        var tracks = new Dictionary<Guid, PersonTracks>();
        var masks = new Dictionary<Guid, PersonMaskTrack>();
        foreach (var (assetId, wanted) in _wanted)
        {
            if (wanted.Tracks is { } t && _results.TryGetValue(t, out var tr) && tr is PersonTracks pt) tracks[assetId] = pt;
            if (wanted.Masks is { } m && _results.TryGetValue(m, out var mr) && mr is PersonMaskTrack pm) masks[assetId] = pm;
        }
        var changed = !Same(tracks, Analyses.Tracks) || !Same(masks, Analyses.Masks);
        if (changed) Analyses = new PeopleAnalyses(tracks, masks);
        if (changed) AnalysesChanged?.Invoke(this, EventArgs.Empty);
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    private static bool Same<T>(IReadOnlyDictionary<Guid, T> a, IReadOnlyDictionary<Guid, T> b) where T : class =>
        a.Count == b.Count && a.All(p => b.TryGetValue(p.Key, out var other) && ReferenceEquals(other, p.Value));

    /// <summary>Runs the wanted analyses one at a time until none can start. Hands over to a new worker when work arrived
    /// meanwhile or the project changed.</summary>
    private async Task RunAsync(CancellationTokenSource project)
    {
        while (!_disposed && !project.IsCancellationRequested && NextRequest() is { } request)
        {
            using var cancel = CancellationTokenSource.CreateLinkedTokenSource(project.Token);
            _running = (request, 0, cancel);
            Publish();
            var progress = new Progress<double>(p =>
            {
                if (_running is { } r && r.Request == request && p > r.Progress + 0.004)
                {
                    _running = (request, p, r.Cancel);
                    StatusChanged?.Invoke(this, EventArgs.Empty);
                }
            });
            object? result = null;
            // Read on this (UI) context: the worker thread never touches the coordinator's dictionaries.
            var tracks = request.Kind == PeopleAnalysisKind.Masks && _results.TryGetValue(request.TracksRequest, out var t) ? t as PersonTracks : null;
            try
            {
                result = await Task.Run(() => AnalyseAsync(request, tracks, progress, cancel.Token), cancel.Token);
            }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested)
            {
            }
            catch (PeopleModelException ex)
            {
                result = new Failure(ex.Message);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "People analysis ({Kind}) failed for {File}.", request.Kind, request.FilePath);
                result = new Failure(string.Format(Messages.People_AnalysisFailed, ex.Message));
            }
            _running = null;
            if (project != _projectCancel) break;
            if (result is not null) _results[request] = result;
            Publish();
        }
        _worker = null;
        StartWorker();
    }

    private async Task<object> AnalyseAsync(Request request, PersonTracks? tracksForMasks, IProgress<double> progress, CancellationToken cancellationToken)
    {
        if (_vision is null) throw new PeopleModelException(string.Format(Messages.People_ModelLoadFailed, nameof(IPeopleVision)));
        var key = _cache?.Fingerprint(request.FilePath) is { } fingerprint ? request.CacheKey(fingerprint, _vision.ModelId) : null;
        var analyzer = new PeopleAnalyzer(_decoder, _vision);
        var started = System.Diagnostics.Stopwatch.StartNew();
        if (request.Kind == PeopleAnalysisKind.Tracks)
        {
            if (key is not null && _cache!.TryLoadTracks(key) is { } cached) return cached;
            var tracks = await analyzer.DetectAsync(request.Source, progress, cancellationToken);
            _logger.LogInformation("People tracks: {File}: {Samples} frames, {Tracks} tracks, {Seconds:F1} s ({Device}).",
                Path.GetFileName(request.FilePath), tracks.Samples.Length, tracks.Tracks.Length, started.Elapsed.TotalSeconds, _vision.DetectorDevice);
            if (key is not null) _cache!.SaveTracks(key, tracks);
            return tracks;
        }
        if (key is not null && _cache!.TryLoadMasks(key) is { } cachedMasks) return cachedMasks;
        var masks = await analyzer.SegmentAsync(request.Source, tracksForMasks ?? throw new InvalidOperationException("The masks need the tracks."),
            progress, cancellationToken);
        _logger.LogInformation("People masks: {File}: {Samples} frames, {People} people, {Seconds:F1} s.",
            Path.GetFileName(request.FilePath), masks.Samples.Length, masks.PersonIds.Count(), started.Elapsed.TotalSeconds);
        if (key is not null) _cache!.SaveMasks(key, masks);
        return masks;
    }

    /// <summary>Completes when no analysis is running or waiting (tests).</summary>
    internal async Task IdleAsync()
    {
        while (_worker is { IsCompleted: false } worker) await worker;
    }

    public void Dispose()
    {
        _disposed = true;
        _projectCancel.Cancel();
        _running?.Cancel.Cancel();
    }
}
