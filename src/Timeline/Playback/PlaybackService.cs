using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Playback;
using Microsoft.Extensions.Logging;

namespace AiVideoEditor.Timeline.Playback;

/// <summary>
/// <see cref="IPlaybackService"/> over <see cref="PlaybackClock"/> and <see cref="VideoPipeline"/>
/// (D010). Every seek or snapshot change re-anchors the clock at the target position, bumps the
/// seek generation and replaces the pipeline; the clock keeps running while the new pipeline
/// buffers, so decoder latency never shifts the timeline position. A result is used only while
/// its (snapshot version, seek generation) is still current.
/// </summary>
public sealed class PlaybackService : IPlaybackService
{
    private readonly IVideoDecoder _decoder;
    private readonly PlaybackSettings _settings;
    private readonly ILogger<PlaybackService> _logger;
    private readonly PlaybackClock _clock;

    private PlaybackSnapshot? _snapshot;
    private VideoPipeline? _pipeline;
    private PreviewPicture? _picture;
    private long _seekGeneration;
    private long _currentSnapshotVersion = long.MinValue;
    private bool _decoderUnavailable;

    public PlaybackService(IVideoDecoder decoder, IReferenceClock referenceClock, ILogger<PlaybackService> logger,
        PlaybackSettings? settings = null)
    {
        _decoder = decoder;
        _logger = logger;
        _settings = settings ?? new PlaybackSettings();
        _clock = new PlaybackClock(referenceClock);
    }

    public PlaybackState State { get; private set; } = PlaybackState.Paused;

    public bool IsBuffering => _pipeline is { } pipeline && (!pipeline.IsReady || _picture is null);

    public bool IsAvailable => !_decoderUnavailable;

    public MediaTime Position => Clamp(_clock.Position);

    public MediaTime Duration => _snapshot?.Duration ?? MediaTime.Zero;

    /// <summary>Current seek generation (diagnostics and tests). Changes only on user seeks
    /// and snapshot updates, never on decoder-internal recovery.</summary>
    internal long SeekGeneration => Interlocked.Read(ref _seekGeneration);

    public event EventHandler? StateChanged;

    /// <summary>Selects the master reference (the audio device clock); null = fallback. The
    /// position does not change.</summary>
    public void SetMasterClock(IReferenceClock? master) => _clock.SetMaster(master);

    public void UpdateSnapshot(PlaybackSnapshot snapshot)
    {
        if (_snapshot is { } current && snapshot.SnapshotVersion <= current.SnapshotVersion)
        {
            _logger.LogDebug("Ignoring playback snapshot {Version}; {Current} is newer.", snapshot.SnapshotVersion, current.SnapshotVersion);
            return;
        }

        _snapshot = snapshot;
        Volatile.Write(ref _currentSnapshotVersion, snapshot.SnapshotVersion);

        // Resync where playback is now; state unchanged. The clock anchor is kept unless the
        // position has to be clamped (shorter timeline): re-anchoring would drop the time that
        // passes between reading the position and setting the new anchor.
        var position = _clock.Position;
        var clamped = Clamp(position);
        Restart(clamped, reanchor: clamped != position);
    }

    public void Play()
    {
        if (_snapshot is null || Duration <= MediaTime.Zero || State == PlaybackState.Playing)
            return;

        if (Position >= Duration)
            Restart(MediaTime.Zero);
        _clock.Start();
        SetState(PlaybackState.Playing);
    }

    public void Pause()
    {
        if (State == PlaybackState.Paused) return;
        _clock.Pause();
        SetState(PlaybackState.Paused);
    }

    public void Stop()
    {
        Pause();
        _ = SeekAsync(MediaTime.Zero);
    }

    public async Task<bool> SeekAsync(MediaTime position, CancellationToken ct = default)
    {
        if (_snapshot is null)
        {
            _clock.Seek(MediaTime.Zero);
            return true;
        }

        var pipeline = Restart(Clamp(position));
        var ready = await pipeline.Ready.WaitAsync(ct).ConfigureAwait(false);
        return ready && IsCurrent(pipeline);
    }

    public PlaybackFrame Update()
    {
        if (_snapshot is not { } snapshot || _pipeline is not { } pipeline)
            return new PlaybackFrame(Position, 0, State, false, PreviewPicture.Black, true);

        var position = _clock.Position;
        if (State == PlaybackState.Playing && position >= snapshot.Duration)
        {
            _clock.Pause();
            _clock.Seek(snapshot.Duration);
            SetState(PlaybackState.Paused);
        }
        position = Clamp(_clock.Position);

        var frame = position.ToFrameFloor(snapshot.FrameRate);
        // At Duration no clip covers the time (half-open spans): show the last frame instead.
        var lastFrame = FrameMath.CeilingFrame(snapshot.Duration, snapshot.FrameRate) - 1;
        var pictureFrame = lastFrame >= 0 ? Math.Min(frame, lastFrame) : 0;

        var picture = pipeline.GetPicture(pictureFrame);
        if (pipeline.DecoderUnavailable) _decoderUnavailable = true;
        var current = picture is not null && IsCurrent(pipeline);
        if (current)
            _picture = picture;

        return new PlaybackFrame(position, frame, State, IsBuffering, _picture, current);
    }

    private VideoPipeline Restart(MediaTime position, bool reanchor = true)
    {
        var snapshot = _snapshot!;
        if (reanchor) _clock.Seek(position);
        var generation = Interlocked.Increment(ref _seekGeneration);

        var old = _pipeline;
        var startFrame = Math.Max(0, Math.Min(position.ToFrameFloor(snapshot.FrameRate),
            FrameMath.CeilingFrame(snapshot.Duration, snapshot.FrameRate) - 1));
        _pipeline = new VideoPipeline(snapshot, generation, startFrame, _decoder, _settings, _logger);
        _picture = null;
        if (old is not null) _ = DisposeInBackground(old);
        return _pipeline;
    }

    /// <summary>True while the pipeline still belongs to the latest snapshot and seek.</summary>
    private bool IsCurrent(VideoPipeline pipeline) =>
        pipeline.SeekGeneration == Interlocked.Read(ref _seekGeneration) &&
        pipeline.SnapshotVersion == Volatile.Read(ref _currentSnapshotVersion);

    private async Task DisposeInBackground(VideoPipeline pipeline)
    {
        try
        {
            await pipeline.DisposeAsync();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Disposing a superseded playback pipeline failed.");
        }
    }

    private MediaTime Clamp(MediaTime time) =>
        time < MediaTime.Zero ? MediaTime.Zero : time > Duration ? Duration : time;

    private void SetState(PlaybackState state)
    {
        if (State == state) return;
        State = state;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public async ValueTask DisposeAsync()
    {
        _clock.Pause();
        if (_pipeline is { } pipeline)
        {
            _pipeline = null;
            await pipeline.DisposeAsync();
        }
    }
}
