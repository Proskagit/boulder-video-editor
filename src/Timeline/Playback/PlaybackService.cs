using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Playback;
using Microsoft.Extensions.Logging;

namespace AiVideoEditor.Timeline.Playback;

/// <summary>
/// <see cref="IPlaybackService"/> over <see cref="PlaybackClock"/>, <see cref="VideoPipeline"/> and
/// <see cref="AudioPipeline"/> (D010). Every seek or snapshot change re-anchors the clock at the
/// target position, bumps the seek generation and replaces the pipelines; the clock keeps running
/// while the new pipelines buffer, so decoder latency never shifts the timeline position. A result
/// is used only while its (snapshot version, seek generation) is still current.
/// <para>
/// Audio: while playing with an available device, the device's played-frames clock is the master
/// (Stopwatch otherwise, and as fallback if the device fails). Pause and seek stop the device,
/// which discards queued audio; a snapshot update keeps it running and continues the mix at the
/// mixer's write position, reusing readers of unchanged clips.
/// </para>
/// </summary>
public sealed class PlaybackService : IPlaybackService
{
    private readonly IVideoDecoder _decoder;
    private readonly IAudioDecoder? _audioDecoder;
    private readonly IAudioOutput? _audioOutput;
    private readonly PlaybackSettings _settings;
    private readonly ILogger<PlaybackService> _logger;
    private readonly PlaybackClock _clock;
    private readonly AudioMixer _mixer = new();

    private PlaybackSnapshot? _snapshot;
    private VideoPipeline? _pipeline;
    private PreviewPicture? _picture;
    private long _seekGeneration;
    private long _currentSnapshotVersion = long.MinValue;
    private bool _decoderUnavailable;
    private AudioPipeline? _audio;
    private readonly List<Task> _retiring = new(); // superseded pipelines being disposed
    private bool _audioRunning;
    private bool _audioFailed;

    public PlaybackService(IVideoDecoder decoder, IReferenceClock referenceClock, ILogger<PlaybackService> logger,
        PlaybackSettings? settings = null, IAudioDecoder? audioDecoder = null, IAudioOutput? audioOutput = null)
    {
        _decoder = decoder;
        _audioDecoder = audioDecoder;
        _audioOutput = audioOutput;
        _logger = logger;
        _settings = settings ?? new PlaybackSettings();
        _clock = new PlaybackClock(referenceClock);
    }

    public PlaybackState State { get; private set; } = PlaybackState.Paused;

    public bool IsBuffering => _pipeline is { } pipeline && (!pipeline.IsReady || _picture is null);

    public bool IsAvailable => !_decoderUnavailable;

    public bool IsAudioAvailable => _audioOutput is not null && _audioDecoder is not null && !_audioFailed;

    public MediaTime Position => Clamp(_clock.Position);

    public MediaTime Duration => _snapshot?.Duration ?? MediaTime.Zero;

    /// <summary>Current seek generation (diagnostics and tests). Changes only on user seeks
    /// and snapshot updates, never on decoder-internal recovery.</summary>
    internal long SeekGeneration => Interlocked.Read(ref _seekGeneration);

    /// <summary>True when the audio for timeline samples [from, until) is decoded (tests).</summary>
    internal bool AudioHasData(long from, long until) => _audio?.HasData(from, until) ?? true;

    /// <summary>Open audio readers (tests).</summary>
    internal int AudioReaderCount => _audio?.ReaderCount ?? 0;

    public event EventHandler? StateChanged;

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
        StartAudio();
        _clock.Start();
        SetState(PlaybackState.Playing);
    }

    public void Pause()
    {
        if (State == PlaybackState.Paused) return;
        StopAudio();
        _clock.Pause();          // the last position the device reported as played
        SetState(PlaybackState.Paused);
        RestartAudio(Position, reuse: false);
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

        if (_audioRunning && _audioOutput!.HasFailed)
        {
            // Device lost: keep going on the Stopwatch from the same position, without sound.
            _logger.LogWarning("Audio output failed; continuing playback without sound.");
            _audioOutput.Stop();
            _audioRunning = false;
            _audioFailed = true;
            _clock.SetMaster(null);
        }

        var position = _clock.Position;
        if (State == PlaybackState.Playing && position >= snapshot.Duration)
        {
            Pause();
            _clock.Seek(snapshot.Duration);
        }
        position = Clamp(_clock.Position);
        _audio?.Maintain(_audioRunning ? _mixer.WritePosition : AudioTiming.NearestSample(position));

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
        var resumeAudio = reanchor && _audioRunning;
        if (reanchor)
        {
            StopAudio(); // discard queued audio of the old position before the new anchor
            _clock.Seek(position);
        }
        var generation = Interlocked.Increment(ref _seekGeneration);

        var old = _pipeline;
        var startFrame = Math.Max(0, Math.Min(position.ToFrameFloor(snapshot.FrameRate),
            FrameMath.CeilingFrame(snapshot.Duration, snapshot.FrameRate) - 1));
        _pipeline = new VideoPipeline(snapshot, generation, startFrame, _decoder, _settings, _logger);
        _picture = null;
        if (old is not null) Retire(old);

        RestartAudio(position, reuse: !reanchor);
        if (resumeAudio) StartAudio();
        return _pipeline;
    }

    /// <summary>New audio pipeline: at the mixer's write position if the device keeps running
    /// (snapshot update), otherwise at <paramref name="position"/>.</summary>
    private void RestartAudio(MediaTime position, bool reuse)
    {
        if (_audioDecoder is null || _snapshot is null) return;

        var start = _audioRunning ? _mixer.WritePosition : AudioTiming.NearestSample(position);
        var old = _audio;
        _audio = new AudioPipeline(_snapshot, Interlocked.Read(ref _seekGeneration), start, _mixer, _audioDecoder,
            _settings, _logger, reuse ? old : null);
        if (!_audioRunning) _mixer.Reset(start);
        if (old is not null) Retire(old);
    }

    /// <summary>Starts the device from the current position and makes it the master clock;
    /// falls back to the Stopwatch when there is no usable device.</summary>
    private void StartAudio()
    {
        if (_audioRunning) return;
        if (_audioOutput is null || _audioDecoder is null)
        {
            _clock.SetMaster(null);
            return;
        }

        _mixer.Reset(AudioTiming.NearestSample(Position));
        if (_audioOutput.TryStart(_mixer))
        {
            _audioRunning = true;
            _audioFailed = false;
            _clock.SetMaster(_audioOutput.Clock);
        }
        else
        {
            _logger.LogWarning("No audio output could be started; playing without sound.");
            _audioFailed = true;
            _clock.SetMaster(null);
        }
    }

    private void StopAudio()
    {
        if (!_audioRunning) return;
        _audioOutput!.Stop();
        _audioRunning = false;
    }

    /// <summary>True while the pipeline still belongs to the latest snapshot and seek.</summary>
    private bool IsCurrent(VideoPipeline pipeline) =>
        pipeline.SeekGeneration == Interlocked.Read(ref _seekGeneration) &&
        pipeline.SnapshotVersion == Volatile.Read(ref _currentSnapshotVersion);

    /// <summary>Disposes a superseded pipeline in the background; <see cref="DisposeAsync"/> waits for it.</summary>
    private void Retire(IAsyncDisposable pipeline)
    {
        _retiring.RemoveAll(t => t.IsCompleted);
        _retiring.Add(DisposeInBackground(pipeline));
    }

    private async Task DisposeInBackground(IAsyncDisposable pipeline)
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
        StopAudio();
        _clock.Pause();
        if (_pipeline is { } pipeline)
        {
            _pipeline = null;
            await pipeline.DisposeAsync();
        }
        if (_audio is { } audio)
        {
            _audio = null;
            await audio.DisposeAsync();
        }
        await Task.WhenAll(_retiring);
    }
}
