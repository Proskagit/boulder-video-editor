using System.Collections.Immutable;
using System.Globalization;
using AiVideoEditor.Core.Color;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Export;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Core.Motion;
using AiVideoEditor.Core.Playback;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiVideoEditor.UI.Services;

/// <summary>The hold tracking of one clip, for the Inspector, the Preview and the export (R2 → E, K2).</summary>
public enum CameraMotionState
{
    /// <summary>Not applicable: no holds, or the media is offline / not analysed.</summary>
    None,

    /// <summary>The analysis has not finished: the contours stay where they were marked, the export waits.</summary>
    Analysing,

    /// <summary>The contours follow the wall on every analysed frame.</summary>
    Tracked,

    /// <summary>The contours follow the wall; on some frames the camera's position is not reliably known and the contours
    /// are hidden there (fewer than <see cref="CameraMotionCoordinator.UnreliableShare"/> of the frames).</summary>
    PartlyUnreliable,

    /// <summary>The camera's position is not reliably known on most frames (at least
    /// <see cref="CameraMotionCoordinator.UnreliableShare"/>): the contours are hidden there — the tracking is not good enough.</summary>
    Unreliable,

    /// <summary>The analysis failed: the contours are drawn where they were marked — without the camera's motion.</summary>
    Failed,

    /// <summary>The user switched the tracking off for this clip (saved with the project): the contours stay where they were
    /// marked.</summary>
    Off
}

/// <summary>A clip's tracking state and, when tracked, the share of analysed frames whose contours are hidden.</summary>
public readonly record struct CameraMotionStatus(CameraMotionState State, double HiddenFraction = 0)
{
    /// <summary>The contours do not get a correct camera-motion compensation on every frame (K2: shown in the Preview, a
    /// warning on export).</summary>
    public bool IsProblem => State is CameraMotionState.PartlyUnreliable or CameraMotionState.Unreliable or CameraMotionState.Failed;
}

/// <summary>
/// Hold tracking (R2 → E): keeps a camera-motion track for every video clip with confirmed holds, so its contours stay on
/// the wall while the camera moves. The track is derived from the video alone — like a thumbnail it is never part of the
/// project (no Undo step): analysed in the background over the range the clip's contours are shown in
/// (<see cref="CameraMotionRange"/>), kept in memory by the file (its path, size and last write time), the range and the
/// reference frame, and between sessions in <see cref="ICameraMotionCache"/> by the file's content (D4) — an unchanged
/// clip is never analysed again. The tracks go to the Preview's snapshot and to the export's preflight. While a clip's
/// analysis runs, its contours are drawn as marked and the export waits for it
/// (<see cref="ExportIssueKind.HoldTrackingPending"/>), so the Preview and the export always use the same track.
/// <para>A failed or unreliable analysis is never silent (K2): its state is shown in the Preview and the Inspector, and the
/// export warns (<see cref="ExportIssueKind.HoldTrackingFailed"/>, <see cref="ExportIssueKind.HoldTrackingUnreliable"/>)
/// so the user cancels or exports knowingly. "Follow the camera" off is the clip's saved choice (K3,
/// <see cref="HoldMarkup.FollowsCamera"/>): no analysis, the contours as marked.</para>
/// <para>Frames are decoded in software at <see cref="AnalysisWidth"/> px with the asset's HDR rule (D033) — the picture
/// the holds were marked on — and analysed by <see cref="CameraMotionSampler"/>. Runs on the caller's (UI) context; the
/// analysis itself on the thread pool, one at a time.</para>
/// </summary>
public sealed class CameraMotionCoordinator : IDisposable
{
    public const int AnalysisWidth = 960;
    public static readonly MediaTime SampleInterval = CameraMotionSampler.DefaultInterval;

    /// <summary>The share of unreliable frames from which a clip's tracking counts as <see cref="CameraMotionState.Unreliable"/>
    /// rather than <see cref="CameraMotionState.PartlyUnreliable"/>.</summary>
    public const double UnreliableShare = 0.5;

    private readonly IProjectService _projects;
    private readonly IVideoDecoder _decoder;
    private readonly ICameraMotionCache? _cache;
    private readonly ILogger _logger;
    private readonly Dictionary<Request, CameraMotionTrack?> _results = new();
    private Dictionary<RouteKey, Request> _wanted = new();
    private HashSet<RouteKey> _off = new();
    private CancellationTokenSource _cancel = new();
    private Task? _worker;
    private bool _disposed;

    public CameraMotionCoordinator(IProjectService projects, IVideoDecoder decoder, ILogger<CameraMotionCoordinator>? logger = null,
        ICameraMotionCache? cache = null)
    {
        _projects = projects;
        _decoder = decoder;
        _cache = cache;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
        _projects.ProjectChanged += (_, _) => { _cancel.Cancel(); _cancel = new CancellationTokenSource(); _results.Clear(); Refresh(); };
        _projects.TimelineChanged += (_, _) => Refresh();
        _projects.MediaAssetsChanged += (_, _) => Refresh();
        Refresh();
    }

    /// <summary>Raised on the caller's context when a clip's track appeared, changed or went away.</summary>
    public event EventHandler? TracksChanged;

    /// <summary>Raised on the caller's context whenever the clips' states may have changed (<see cref="StatusOf"/>).</summary>
    public event EventHandler? StatusChanged;

    /// <summary>The tracking of one route of a clip (D053: each route has its own marking frame and window).</summary>
    public CameraMotionStatus StatusOf(RouteKey route)
    {
        if (_off.Contains(route)) return new CameraMotionStatus(CameraMotionState.Off);
        if (!_wanted.TryGetValue(route, out var request)) return new CameraMotionStatus(CameraMotionState.None);
        if (!_results.TryGetValue(request, out var track)) return new CameraMotionStatus(CameraMotionState.Analysing);
        if (track is null) return new CameraMotionStatus(CameraMotionState.Failed);
        var hidden = track.UnreliableFraction;
        return new CameraMotionStatus(hidden switch
        {
            0 => CameraMotionState.Tracked,
            >= UnreliableShare => CameraMotionState.Unreliable,
            _ => CameraMotionState.PartlyUnreliable
        }, hidden);
    }

    /// <summary>Analyses the route again after a failure (a file that was busy, a decoder error).</summary>
    public void Retry(RouteKey route)
    {
        if (!_wanted.TryGetValue(route, out var request) || !_results.TryGetValue(request, out var track) || track is not null) return;
        _results.Remove(request);
        Publish();
        StartWorker();
    }

    /// <summary>The tracks of the current project's routes, by clip and route (D053).</summary>
    public ImmutableDictionary<RouteKey, CameraMotionTrack> Tracks { get; private set; } = ImmutableDictionary<RouteKey, CameraMotionTrack>.Empty;

    /// <summary>The clips with a route whose analysis has not finished yet.</summary>
    public ImmutableHashSet<Guid> Pending { get; private set; } = ImmutableHashSet<Guid>.Empty;

    /// <summary>What the export's preflight uses: the same tracks the Preview draws with, the clips still analysed, the
    /// clips whose analysis failed and the share of hidden frames of the partly or mostly unreliable ones.</summary>
    public ExportCameraMotion ForExport()
    {
        // D053: a clip is failed / unreliable when one of its routes is (the larger hidden share of its routes).
        var failed = new HashSet<Guid>();
        var unreliable = new Dictionary<Guid, double>();
        foreach (var route in _wanted.Keys)
        {
            var status = StatusOf(route);
            if (status.State == CameraMotionState.Failed) failed.Add(route.ClipId);
            else if (status.State is CameraMotionState.PartlyUnreliable or CameraMotionState.Unreliable)
                unreliable[route.ClipId] = Math.Max(unreliable.GetValueOrDefault(route.ClipId), status.HiddenFraction);
        }
        foreach (var clipId in failed) unreliable.Remove(clipId);
        return new ExportCameraMotion(Tracks, Pending) { Failed = failed, Unreliable = unreliable };
    }

    /// <summary>The key of one analysis in memory: the file (path, size, last write time — a file replaced on disk is
    /// another request), its start time and HDR rule, the range and the reference frame.</summary>
    private sealed record Request(string FilePath, long FileLength, long FileWriteTicks, MediaTime FileStart, HdrConversion? Hdr,
        FrameRate? NominalRate, CameraMotionRange Range)
    {
        /// <summary>The key of the analysis between sessions: the file's content instead of its path.</summary>
        public string CacheKey(string fingerprint) => string.Join('|',
            $"v{CameraMotionSampler.AnalysisVersion}", fingerprint, FileStart.Ticks.ToString(CultureInfo.InvariantCulture),
            Hdr?.ToString() ?? "sdr", NominalRate?.ToString() ?? "-", Range.ReferenceTime.Ticks.ToString(CultureInfo.InvariantCulture),
            Range.Start.Ticks.ToString(CultureInfo.InvariantCulture), Range.End.Ticks.ToString(CultureInfo.InvariantCulture),
            AnalysisWidth.ToString(CultureInfo.InvariantCulture), SampleInterval.Ticks.ToString(CultureInfo.InvariantCulture));
    }

    private void Refresh()
    {
        if (_disposed) return;
        var project = _projects.Current;
        var assets = project.MediaAssets.ToDictionary(a => a.Id);
        var wanted = new Dictionary<RouteKey, Request>();
        var off = new HashSet<RouteKey>();
        foreach (var clip in project.Timeline.VideoTracks.SelectMany(t => t.Clips).OfType<VideoClip>())
        {
            if (!assets.TryGetValue(clip.MediaAssetId, out var asset) || asset.IsMissing || asset.Kind != MediaKind.Video ||
                asset.Metadata is not { } metadata)
                continue;
            // D053: one analysis per route — its own marking frame and window.
            foreach (var route in clip.Routes)
            {
                if (CameraMotionRange.For(clip, route, project.Settings.FrameRate) is not { } range) continue;
                var key = new RouteKey(clip.Id, route.Id);
                if (!route.FollowsCamera)
                {
                    off.Add(key);
                    continue;
                }
                var (length, written) = Stat(asset.FilePath);
                wanted[key] = new Request(asset.FilePath, length, written, metadata.StartTime ?? MediaTime.Zero,
                    HdrColor.Classify(asset.Kind, metadata).Conversion, SourceFrameSelector.NominalRate(metadata), range);
            }
        }
        _wanted = wanted;
        _off = off;
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
            _worker = RunAsync(_cancel);
    }

    private void Publish()
    {
        var tracks = ImmutableDictionary.CreateBuilder<RouteKey, CameraMotionTrack>();
        var pending = ImmutableHashSet.CreateBuilder<Guid>();
        foreach (var (route, request) in _wanted)
        {
            if (!_results.TryGetValue(request, out var track)) pending.Add(route.ClipId);
            else if (track is not null) tracks[route] = track;
        }
        var newTracks = tracks.ToImmutable();
        var newPending = pending.ToImmutable();
        var changed = !SameTracks(newTracks, Tracks);
        Tracks = newTracks;
        Pending = newPending;
        if (changed) TracksChanged?.Invoke(this, EventArgs.Empty);
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>A request some clip still needs.</summary>
    private Request? NextRequest() => _wanted.Values.FirstOrDefault(r => !_results.ContainsKey(r));

    private static bool SameTracks(ImmutableDictionary<RouteKey, CameraMotionTrack> a, ImmutableDictionary<RouteKey, CameraMotionTrack> b) =>
        a.Count == b.Count && a.All(pair => b.TryGetValue(pair.Key, out var other) && ReferenceEquals(other, pair.Value));

    /// <summary>Analyses the wanted requests without a result, one at a time, until none is left. When it ends it hands
    /// over to a new worker if work arrived meanwhile (a retry, a clip switched back on) or the project changed (a new
    /// token) — so no request is ever left waiting.</summary>
    private async Task RunAsync(CancellationTokenSource source)
    {
        var cancellationToken = source.Token;
        while (!_disposed && !cancellationToken.IsCancellationRequested && NextRequest() is { } request)
        {
            CameraMotionTrack? track = null;
            try
            {
                track = await Task.Run(() => TrackOfAsync(request, cancellationToken), cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Not fatal, never silent: the state is Failed — the Preview says so and the export warns (K2).
                _logger.LogWarning(ex, "Hold tracking failed for {File}; its contours are not tracked.", request.FilePath);
            }
            if (source != _cancel) break;   // the project changed meanwhile: this result belongs to no request any more
            _results[request] = track;
            Publish();
        }
        _worker = null;
        StartWorker();
    }

    /// <summary>The track of one request: from the cache when it holds this analysis, otherwise analysed and stored.</summary>
    private async Task<CameraMotionTrack> TrackOfAsync(Request request, CancellationToken cancellationToken)
    {
        var key = _cache?.Fingerprint(request.FilePath) is { } fingerprint ? request.CacheKey(fingerprint) : null;
        if (key is not null && _cache!.TryLoad(key) is { } cached)
        {
            _logger.LogInformation("Hold tracking: {File} {Start}–{End} from the cache ({Samples} samples).",
                Path.GetFileName(request.FilePath), request.Range.Start, request.Range.End, cached.Samples.Length);
            return cached;
        }
        var started = System.Diagnostics.Stopwatch.StartNew();
        var (track, analysed, densified) = await AnalyzeAsync(request, cancellationToken);
        _logger.LogInformation("Hold tracking: {File} {Start}–{End}: {Model}, {Samples} samples ({Densified} for fast motion), {Unreliable:P0} unreliable, {Seconds:F1} s.",
            Path.GetFileName(request.FilePath), request.Range.Start, request.Range.End, track.Model, analysed, densified,
            track.UnreliableFraction, started.Elapsed.TotalSeconds);
        if (key is not null) _cache!.Save(key, track);
        return track;
    }

    /// <summary>Completes when no analysis is running or waiting (tests).</summary>
    internal async Task IdleAsync()
    {
        while (_worker is { IsCompleted: false } worker) await worker;
    }

    /// <summary>The track of one request: the reference frame, then every frame of the range through the sampler.</summary>
    private async Task<(CameraMotionTrack Track, int Analysed, int Densified)> AnalyzeAsync(Request request, CancellationToken cancellationToken)
    {
        var reference = await DecodeAtAsync(request, request.Range.ReferenceTime, cancellationToken);
        var estimator = new CameraMotionEstimator(GrayImage.FromBgra(reference), request.Range.ReferenceTime);
        var sampler = new CameraMotionSampler(estimator, SampleInterval);

        await using var stream = await _decoder.OpenAsync(Decode(request, request.Range.Start), cancellationToken);
        while (await stream.ReadFrameAsync(cancellationToken) is { } frame)
        {
            var time = CameraMotionMath.SourceTimeOf(frame.Timestamp, request.FileStart);
            if (time > request.Range.End + SampleInterval) break;
            if (time < request.Range.Start) continue;
            sampler.Offer(GrayImage.FromBgra(frame), time);
        }
        return (sampler.Build(), sampler.AnalysedFrames, sampler.DensifiedFrames);
    }

    private async Task<DecodedFrame> DecodeAtAsync(Request request, MediaTime time, CancellationToken cancellationToken)
    {
        await using var stream = await _decoder.OpenAsync(Decode(request, time), cancellationToken);
        var point = new SourceSamplePoint(time.Ticks, 1);
        var current = await stream.ReadFrameAsync(cancellationToken)
                      ?? throw new VideoDecodeException(VideoDecodeError.NoVideo, $"'{request.FilePath}' produced no frame.");
        current = Copy(current);
        while (await stream.ReadFrameAsync(cancellationToken) is { } frame && SourceFrameSelector.IsAtOrBefore(frame.Timestamp, request.FileStart, point))
            current = Copy(frame);
        return current;
    }

    private static VideoDecodeRequest Decode(Request request, MediaTime at) => new()
    {
        FilePath = request.FilePath,
        StartTime = request.FileStart,
        FirstSamplePoint = new SourceSamplePoint(at.Ticks, 1),
        NominalFrameRate = request.NominalRate,
        MaxWidth = AnalysisWidth,
        MaxHeight = AnalysisWidth,
        Hardware = HardwareDecoding.Disabled,
        Hdr = request.Hdr
    };

    /// <summary>The frame's own copy (decoders may reuse their buffers).</summary>
    private static DecodedFrame Copy(DecodedFrame frame)
    {
        var rowBytes = frame.Width * DecodedFrame.BytesPerPixel;
        var pixels = new byte[rowBytes * frame.Height];
        var source = frame.Pixels.Span;
        for (var y = 0; y < frame.Height; y++)
            source.Slice(y * frame.Stride, rowBytes).CopyTo(pixels.AsSpan(y * rowBytes));
        return new DecodedFrame(frame.Width, frame.Height, rowBytes, pixels, frame.Timestamp);
    }

    public void Dispose()
    {
        _disposed = true;
        _cancel.Cancel();
    }
}
