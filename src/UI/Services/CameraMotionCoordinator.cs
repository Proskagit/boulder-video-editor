using System.Collections.Immutable;
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

/// <summary>The hold tracking of one clip, for the Inspector.</summary>
public enum CameraMotionState
{
    /// <summary>Not applicable: no holds, or the media is offline / not analysed.</summary>
    None,

    /// <summary>The analysis has not finished: the contours stay where they were marked, the export waits.</summary>
    Analysing,

    /// <summary>The contours follow the wall (hidden on the frames whose position is not reliably known).</summary>
    Tracked,

    /// <summary>The analysis failed: the contours stay where they were marked.</summary>
    Failed,

    /// <summary>The user switched the tracking off for this clip (this session): the contours stay where they were marked.</summary>
    Off
}

/// <summary>A clip's tracking state and, when tracked, the share of analysed frames whose contours are hidden.</summary>
public readonly record struct CameraMotionStatus(CameraMotionState State, double HiddenFraction = 0);

/// <summary>
/// Hold tracking (R2 → E): keeps a camera-motion track for every video clip with confirmed holds, so its contours stay on
/// the wall while the camera moves. The track is derived from the video alone — like a thumbnail it is never part of the
/// project (<c>project.json</c> v9 unchanged, no Undo step): analysed in the background over the range the clip's contours
/// are shown in (<see cref="CameraMotionRange"/>), cached by the file, the range and the reference frame, and handed to
/// the Preview's snapshot and to the export's preflight. While a clip's analysis runs, its contours are drawn as before
/// and the export waits for it (<see cref="ExportIssueKind.HoldTrackingPending"/>), so the Preview and the export always
/// use the same track. A failed analysis leaves the contours where they were marked (logged).
/// <para>Frames are decoded in software at <see cref="AnalysisWidth"/> px with the asset's HDR rule (D033) — the picture
/// the holds were marked on — and sampled every <see cref="SampleInterval"/>. Runs on the caller's (UI) context; the
/// analysis itself on the thread pool, one at a time.</para>
/// </summary>
public sealed class CameraMotionCoordinator : IDisposable
{
    public const int AnalysisWidth = 960;
    public static readonly MediaTime SampleInterval = MediaTime.FromSeconds(0.1);

    private readonly IProjectService _projects;
    private readonly IVideoDecoder _decoder;
    private readonly ILogger _logger;
    private readonly Dictionary<Request, CameraMotionTrack?> _results = new();
    private readonly HashSet<Guid> _off = new();
    private Dictionary<Guid, Request> _wanted = new();
    private CancellationTokenSource _cancel = new();
    private Task? _worker;
    private bool _disposed;

    public CameraMotionCoordinator(IProjectService projects, IVideoDecoder decoder, ILogger<CameraMotionCoordinator>? logger = null)
    {
        _projects = projects;
        _decoder = decoder;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
        _projects.ProjectChanged += (_, _) => { _cancel.Cancel(); _cancel = new CancellationTokenSource(); _off.Clear(); Refresh(); };
        _projects.TimelineChanged += (_, _) => Refresh();
        _projects.MediaAssetsChanged += (_, _) => Refresh();
        Refresh();
    }

    /// <summary>Raised on the caller's context when a clip's track appeared, changed or went away.</summary>
    public event EventHandler? TracksChanged;

    /// <summary>Raised on the caller's context whenever the clips' states may have changed (<see cref="StatusOf"/>).</summary>
    public event EventHandler? StatusChanged;

    /// <summary>The tracking of <paramref name="clipId"/>.</summary>
    public CameraMotionStatus StatusOf(Guid clipId)
    {
        if (!_wanted.TryGetValue(clipId, out var request)) return new CameraMotionStatus(CameraMotionState.None);
        if (_off.Contains(clipId)) return new CameraMotionStatus(CameraMotionState.Off);
        if (!_results.TryGetValue(request, out var track)) return new CameraMotionStatus(CameraMotionState.Analysing);
        return track is null
            ? new CameraMotionStatus(CameraMotionState.Failed)
            : new CameraMotionStatus(CameraMotionState.Tracked, track.UnreliableFraction);
    }

    /// <summary>The manual correction when the tracking is wrong: <c>false</c> leaves the clip's contours where they were
    /// marked — in the Preview and the export alike — until switched on again. Session state of the editor, like the
    /// track itself: never in the project, no Undo step; a new project switches every clip back on.</summary>
    public void SetFollowsCamera(Guid clipId, bool follows)
    {
        if (follows ? !_off.Remove(clipId) : !_off.Add(clipId)) return;
        Publish();
        StartWorker();
    }

    /// <summary>Analyses the clip again after a failure (a file that was busy, a decoder error).</summary>
    public void Retry(Guid clipId)
    {
        if (!_wanted.TryGetValue(clipId, out var request) || !_results.TryGetValue(request, out var track) || track is not null) return;
        _results.Remove(request);
        Publish();
        StartWorker();
    }

    /// <summary>The tracks of the current project's clips, by clip id.</summary>
    public ImmutableDictionary<Guid, CameraMotionTrack> Tracks { get; private set; } = ImmutableDictionary<Guid, CameraMotionTrack>.Empty;

    /// <summary>The clips whose analysis has not finished yet.</summary>
    public ImmutableHashSet<Guid> Pending { get; private set; } = ImmutableHashSet<Guid>.Empty;

    /// <summary>What the export's preflight uses: the same tracks the Preview draws with, and the clips still analysed.</summary>
    public ExportCameraMotion ForExport() => new(Tracks, Pending);

    /// <summary>The key of one analysis: the file, its start time and HDR rule, the range and the reference frame.</summary>
    private sealed record Request(string FilePath, MediaTime FileStart, HdrConversion? Hdr, FrameRate? NominalRate, CameraMotionRange Range);

    private void Refresh()
    {
        if (_disposed) return;
        var project = _projects.Current;
        var assets = project.MediaAssets.ToDictionary(a => a.Id);
        var wanted = new Dictionary<Guid, Request>();
        foreach (var clip in project.Timeline.VideoTracks.SelectMany(t => t.Clips).OfType<VideoClip>())
        {
            if (!assets.TryGetValue(clip.MediaAssetId, out var asset) || asset.IsMissing || asset.Kind != MediaKind.Video ||
                asset.Metadata is not { } metadata || CameraMotionRange.For(clip, project.Settings.FrameRate) is not { } range)
                continue;
            wanted[clip.Id] = new Request(asset.FilePath, metadata.StartTime ?? MediaTime.Zero,
                HdrColor.Classify(asset.Kind, metadata).Conversion, SourceFrameSelector.NominalRate(metadata), range);
        }
        _wanted = wanted;
        Publish();
        StartWorker();
    }

    private void StartWorker()
    {
        if (!_disposed && (_worker is null || _worker.IsCompleted) && NextRequest() is not null)
            _worker = RunAsync(_cancel);
    }

    private void Publish()
    {
        var tracks = ImmutableDictionary.CreateBuilder<Guid, CameraMotionTrack>();
        var pending = ImmutableHashSet.CreateBuilder<Guid>();
        foreach (var (clipId, request) in _wanted)
        {
            if (_off.Contains(clipId)) continue;   // switched off: drawn as marked, nothing to wait for
            if (!_results.TryGetValue(request, out var track)) pending.Add(clipId);
            else if (track is not null) tracks[clipId] = track;
        }
        var newTracks = tracks.ToImmutable();
        var newPending = pending.ToImmutable();
        var changed = !SameTracks(newTracks, Tracks);
        Tracks = newTracks;
        Pending = newPending;
        if (changed) TracksChanged?.Invoke(this, EventArgs.Empty);
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>A request some clip that follows the camera still needs.</summary>
    private Request? NextRequest() =>
        _wanted.Where(pair => !_off.Contains(pair.Key)).Select(pair => pair.Value).FirstOrDefault(r => !_results.ContainsKey(r));

    private static bool SameTracks(ImmutableDictionary<Guid, CameraMotionTrack> a, ImmutableDictionary<Guid, CameraMotionTrack> b) =>
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
                track = await Task.Run(() => AnalyzeAsync(request, cancellationToken), cancellationToken);
                _logger.LogInformation("Hold tracking: {File} {Start}–{End}: {Model}, {Samples} samples, {Unreliable:P0} unreliable.",
                    Path.GetFileName(request.FilePath), request.Range.Start, request.Range.End, track.Model, track.Samples.Length,
                    track.UnreliableFraction);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Not fatal: the contours stay where they were marked, as before tracking existed.
                _logger.LogWarning(ex, "Hold tracking failed for {File}; its contours are not tracked.", request.FilePath);
            }
            _results[request] = track;
            Publish();
        }
        _worker = null;
        StartWorker();
    }

    /// <summary>Completes when no analysis is running or waiting (tests).</summary>
    internal async Task IdleAsync()
    {
        while (_worker is { IsCompleted: false } worker) await worker;
    }

    /// <summary>The track of one request: the reference frame, then every frame of the range at the sample interval.</summary>
    private async Task<CameraMotionTrack> AnalyzeAsync(Request request, CancellationToken cancellationToken)
    {
        var reference = await DecodeAtAsync(request, request.Range.ReferenceTime, cancellationToken);
        var estimator = new CameraMotionEstimator(GrayImage.FromBgra(reference), request.Range.ReferenceTime);

        await using var stream = await _decoder.OpenAsync(Decode(request, request.Range.Start), cancellationToken);
        var next = request.Range.Start;
        while (await stream.ReadFrameAsync(cancellationToken) is { } frame)
        {
            var time = CameraMotionMath.SourceTimeOf(frame.Timestamp, request.FileStart);
            if (time > request.Range.End + SampleInterval) break;
            if (time < next) continue;
            estimator.Add(GrayImage.FromBgra(frame), time);
            next = time + SampleInterval;
        }
        return estimator.Build();
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
