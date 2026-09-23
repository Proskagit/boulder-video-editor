using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Playback;
using Microsoft.Extensions.Logging;

namespace AiVideoEditor.Timeline.Playback;

/// <summary>
/// Decoding for one (snapshot version, seek generation) pair, starting at a timeline frame.
/// Keeps a <see cref="SpanReader"/> for the visible clip and, within the prefetch window, for
/// the next one; readers for clips that are no longer current are disposed (a clip that
/// becomes visible again is reopened where it is needed). Pictures are requested from the
/// service's thread; readers decode in the background.
/// </summary>
internal sealed class VideoPipeline : IAsyncDisposable
{
    private readonly PlaybackSnapshot _snapshot;
    private readonly IVideoDecoder _decoder;
    private readonly PlaybackSettings _settings;
    private readonly ILogger _logger;
    private readonly Dictionary<Guid, SpanReader> _readers = new();
    private readonly List<Task> _retired = new(); // readers being disposed after leaving the picture
    private readonly TaskCompletionSource<bool> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _disposed;

    public VideoPipeline(PlaybackSnapshot snapshot, long seekGeneration, long startFrame,
        IVideoDecoder decoder, PlaybackSettings settings, ILogger logger)
    {
        _snapshot = snapshot;
        SeekGeneration = seekGeneration;
        _decoder = decoder;
        _settings = settings;
        _logger = logger;

        var span = snapshot.PictureAt(MediaTime.FromFrame(startFrame, snapshot.FrameRate));
        if (span is null || !IsDecodable(span))
        {
            _ready.TrySetResult(true);
            return;
        }

        var reader = Open(span, startFrame);
        reader.Updated += () =>
        {
            if (reader.IsReady(startFrame)) _ready.TrySetResult(true);
        };
        if (reader.IsReady(startFrame)) _ready.TrySetResult(true);
    }

    public long SnapshotVersion => _snapshot.SnapshotVersion;
    public long SeekGeneration { get; }

    /// <summary>Completes with true when the picture for the start frame is known, false if
    /// the pipeline was disposed first.</summary>
    public Task<bool> Ready => _ready.Task;

    public bool IsReady => _ready.Task.IsCompletedSuccessfully && _ready.Task.Result;

    /// <summary>Set once any reader reported that no decoder backend is available.</summary>
    public bool DecoderUnavailable { get; private set; }

    /// <summary>The picture for <paramref name="timelineFrame"/>, or null if not decoded yet.</summary>
    public PreviewPicture? GetPicture(long timelineFrame)
    {
        if (_disposed) return null;

        var time = MediaTime.FromFrame(timelineFrame, _snapshot.FrameRate);
        var span = _snapshot.PictureAt(time);
        var next = NextSpan(time);
        Retain(span, next);

        if (span is null) return PreviewPicture.Black;
        switch (span.Status)
        {
            case SpanStatus.Offline:
                return new PreviewPicture(PictureKind.Offline, null, span.ClipId, span.Reason);
            case SpanStatus.Unsupported:
                return new PreviewPicture(PictureKind.Unsupported, null, span.ClipId, span.Reason);
        }

        var reader = _readers.TryGetValue(span.ClipId, out var existing) ? existing : Open(span, timelineFrame);
        if (next is { } upcoming && !_readers.ContainsKey(upcoming.Span.ClipId))
            Open(upcoming.Span, upcoming.Frame);

        var result = reader.TryGet(timelineFrame);
        if (result.Error is { } error)
        {
            if (error.Error == VideoDecodeError.DecoderUnavailable) DecoderUnavailable = true;
            return error.Error == VideoDecodeError.FileNotFound
                ? new PreviewPicture(PictureKind.Offline, null, span.ClipId, error.Message)
                : new PreviewPicture(PictureKind.DecodeError, null, span.ClipId, error.Message);
        }
        return result.Frame is { } frame ? new PreviewPicture(PictureKind.Frame, frame, span.ClipId) : null;
    }

    /// <summary>The next decodable span that becomes visible within the prefetch window.</summary>
    private (PictureSpan Span, long Frame)? NextSpan(MediaTime time)
    {
        var change = _snapshot.NextPictureChange(time);
        if (change >= _snapshot.Duration || (change - time).Ticks > _settings.PrefetchWindow.Ticks)
            return null;
        var frame = FrameMath.CeilingFrame(change, _snapshot.FrameRate);
        var span = _snapshot.PictureAt(MediaTime.FromFrame(frame, _snapshot.FrameRate));
        return span is not null && IsDecodable(span) ? (span, frame) : null;
    }

    private void Retain(PictureSpan? current, (PictureSpan Span, long Frame)? next)
    {
        foreach (var (clipId, reader) in _readers.ToList())
        {
            if (clipId == current?.ClipId || clipId == next?.Span.ClipId) continue;
            _readers.Remove(clipId);
            _retired.RemoveAll(t => t.IsCompleted);
            _retired.Add(reader.DisposeAsync().AsTask());
        }
    }

    private SpanReader Open(PictureSpan span, long firstFrame)
    {
        var asset = _snapshot.Assets[span.AssetId];
        var reader = new SpanReader(span, asset, _snapshot.FrameRate, firstFrame, _decoder, _settings, _logger);
        _readers[span.ClipId] = reader;
        return reader;
    }

    private static bool IsDecodable(PictureSpan span) => span.Status is SpanStatus.Video or SpanStatus.StillImage;

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _ready.TrySetResult(false);
        var readers = _readers.Values.ToList();
        _readers.Clear();
        foreach (var reader in readers)
            await reader.DisposeAsync();
        await Task.WhenAll(_retired);
    }
}
