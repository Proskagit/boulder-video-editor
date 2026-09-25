using System.Diagnostics;
using System.Runtime.InteropServices;
using AiVideoEditor.Audio;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Playback;
using AiVideoEditor.Timeline.Playback;
using AiVideoEditor.Timeline.Tests.Playback;
using Microsoft.Extensions.Logging.Abstractions;
using NAudio.Wave;
using PlaybackState = AiVideoEditor.Core.Playback.PlaybackState;
using WavePlayback = NAudio.Wave.PlaybackState;
using Xunit;

namespace AiVideoEditor.Video.Tests;

/// <summary>
/// D024 Step 9.3 (product owner option B): <see cref="WasapiAudioOutput"/> checks the default render device at every
/// start — Play and the restart after a seek — and moves to it when its endpoint id differs from the open device's;
/// the same id keeps the open output. A device lost during playback keeps D013 (the service continues on the
/// Stopwatch, no switch during playback) and the next Play opens the default device of that moment. Devices are fakes
/// behind <see cref="IAudioEndpoints"/>: no real device is added, removed or switched here.
/// </summary>
public sealed class AudioDeviceChangeTests : IAsyncLifetime
{
    private const string DeviceA = "{0.0.0.00000000}.{a1a1a1a1-0000-0000-0000-00000000000a}";
    private const string DeviceB = "{0.0.0.00000000}.{b2b2b2b2-0000-0000-0000-00000000000b}";

    private readonly FakeEndpoints _devices = new() { Default = DeviceA };
    private readonly WasapiAudioOutput _output;

    public AudioDeviceChangeTests() => _output = new WasapiAudioOutput(NullLogger<WasapiAudioOutput>.Instance, _devices);

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (_service is not null) await _service.DisposeAsync();
        _output.Dispose();
    }

    // --- fakes ------------------------------------------------------------------------------------------------

    private sealed class FakeEndpoints : IAudioEndpoints
    {
        public string? Default { get; set; }
        public HashSet<string> Unopenable { get; } = new();
        public List<FakePlayer> Opened { get; } = new();
        public int DefaultQueries { get; private set; }

        public string? DefaultRenderDeviceId()
        {
            DefaultQueries++;
            return Default is null ? null : new string(Default.ToCharArray()); // a fresh string, as the system returns
        }

        public IWavePlayer Open(string deviceId, int latencyMilliseconds)
        {
            if (Unopenable.Contains(deviceId))
                throw new COMException("The audio endpoint device has been unplugged.", unchecked((int)0x88890004));
            var player = new FakePlayer(deviceId);
            Opened.Add(player);
            return player;
        }
    }

    /// <summary>An opened output: plays the provider only when <see cref="Pull"/> is called (the "device" consuming
    /// audio); its position is the bytes played in the current session, like WASAPI's.</summary>
    private sealed class FakePlayer(string deviceId) : IWavePlayer, IWavePosition
    {
        private IWaveProvider? _provider;
        private long _position;

        public string DeviceId { get; } = deviceId;
        public int InitCount { get; private set; }
        public int PlayCount { get; private set; }
        public int DisposeCount { get; private set; }
        public WavePlayback PlaybackState { get; private set; } = WavePlayback.Stopped;
        public WaveFormat OutputWaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(AudioFormat.SampleRate, AudioFormat.Channels);
        public float Volume { get; set; } = 1;
        public event EventHandler<StoppedEventArgs>? PlaybackStopped;

        public void Init(IWaveProvider waveProvider) { _provider = waveProvider; InitCount++; }
        public void Play() { ObjectDisposedException.ThrowIf(DisposeCount > 0, this); PlaybackState = WavePlayback.Playing; PlayCount++; }
        public void Pause() => PlaybackState = WavePlayback.Paused;
        public void Stop() { PlaybackState = WavePlayback.Stopped; _position = 0; }
        public long GetPosition() => _position;
        public void Dispose() { DisposeCount++; PlaybackState = WavePlayback.Stopped; }

        public void Pull(int frames)
        {
            Assert.Equal(WavePlayback.Playing, PlaybackState);
            var bytes = new byte[frames * OutputWaveFormat.BlockAlign];
            _provider!.Read(bytes, 0, bytes.Length);
            _position += bytes.Length;
        }

        /// <summary>The device vanished: WASAPI's render thread fails and NAudio reports the stop with the error.</summary>
        public void Remove()
        {
            PlaybackState = WavePlayback.Stopped;
            PlaybackStopped?.Invoke(this, new StoppedEventArgs(new COMException("The audio endpoint device has been invalidated.", unchecked((int)0x88890004))));
        }
    }

    private sealed class Silence : IAudioSampleSource
    {
        public void Read(Span<float> interleaved) => interleaved.Clear();
    }

    private FakePlayer Open(string id) => _devices.Opened.Last(p => p.DeviceId == id);

    // --- the output on its own ------------------------------------------------------------------------------------

    [Fact]
    public void Starting_again_on_the_same_default_device_reuses_the_open_output()
    {
        Assert.True(_output.TryStart(new Silence()));
        _output.Stop();
        Assert.True(_output.TryStart(new Silence()));
        _output.Stop();
        Assert.True(_output.TryStart(new Silence()));

        var player = Assert.Single(_devices.Opened);                         // no needless recreate
        Assert.Equal((1, 3, 0), (player.InitCount, player.PlayCount, player.DisposeCount));
        Assert.Equal(3, _devices.DefaultQueries);                            // asked at every start, not on Stop
    }

    [Fact]
    public void Starting_after_the_default_device_changed_closes_the_old_output_and_opens_the_new_default()
    {
        Assert.True(_output.TryStart(new Silence()));
        _output.Stop();
        _devices.Default = DeviceB;

        Assert.True(_output.TryStart(new Silence()));

        Assert.Equal(new[] { DeviceA, DeviceB }, _devices.Opened.Select(p => p.DeviceId));
        Assert.Equal((1, 1), (Open(DeviceA).PlayCount, Open(DeviceA).DisposeCount)); // closed once, never played again
        Assert.Equal((WavePlayback.Playing, 0), (Open(DeviceB).PlaybackState, Open(DeviceB).DisposeCount));
    }

    [Fact]
    public void The_device_is_identified_by_its_endpoint_id()
    {
        Assert.True(_output.TryStart(new Silence()));
        _output.Stop();
        _devices.Default = DeviceA.ToUpperInvariant(); // the same endpoint id, spelled in other letter case

        Assert.True(_output.TryStart(new Silence()));
        Assert.Single(_devices.Opened);
        _output.Stop();

        _devices.Default = DeviceA[..^2] + "0c}";      // another endpoint (the seam carries no names at all)
        Assert.True(_output.TryStart(new Silence()));
        Assert.Equal(2, _devices.Opened.Count);
    }

    [Fact]
    public void The_clock_keeps_counting_across_a_change_of_device()
    {
        Assert.True(_output.TryStart(new Silence()));
        Open(DeviceA).Pull(4_800);
        _output.Stop();
        _devices.Default = DeviceB;
        Assert.True(_output.TryStart(new Silence()));
        Open(DeviceB).Pull(2_400);

        Assert.Equal(7_200, _output.Clock.Now.Value); // cumulative, never backwards (D013)
    }

    [Fact]
    public void No_default_device_or_one_that_cannot_be_opened_fails_the_start_without_leaking_the_old_output()
    {
        Assert.True(_output.TryStart(new Silence()));
        _output.Stop();

        _devices.Default = null;                        // no output device at all
        Assert.False(_output.TryStart(new Silence()));
        Assert.Equal(1, Open(DeviceA).DisposeCount);

        _devices.Default = DeviceB;
        _devices.Unopenable.Add(DeviceB);               // the new default refuses to open
        Assert.False(_output.TryStart(new Silence()));

        _devices.Unopenable.Clear();
        Assert.True(_output.TryStart(new Silence()));   // and works once it can be opened
        Assert.Equal(new[] { DeviceA, DeviceB }, _devices.Opened.Select(p => p.DeviceId));
    }

    [Fact]
    public void Disposing_closes_each_output_exactly_once()
    {
        Assert.True(_output.TryStart(new Silence()));
        _output.Stop();
        _devices.Default = DeviceB;
        Assert.True(_output.TryStart(new Silence()));

        _output.Dispose();
        _output.Dispose();

        Assert.All(_devices.Opened, p => Assert.Equal(1, p.DisposeCount));
    }

    // --- with playback --------------------------------------------------------------------------------------------

    private static readonly FrameRate Rate = FrameRate.Fps25;
    private readonly FakeVideoDecoder _video = new();
    private readonly FakeAudioDecoder _audio = new();
    private readonly FakeReferenceClock _stopwatch = new();
    private PlaybackService? _service;

    /// <summary>Playback of a 10 s video with sound on the real service, audio through the output under test.</summary>
    private PlaybackService PlayingService()
    {
        var project = new Core.Entities.Project { Settings = { FrameRate = Rate, IsFrameRateLocked = true } };
        var v1 = new Track { Type = TrackType.Video, Name = "V1" };
        project.Timeline.VideoTracks.Add(v1);
        var asset = new MediaAsset
        {
            FilePath = Path.Combine(Path.GetTempPath(), "aive-device", Guid.NewGuid().ToString("N"), "a.mp4"),
            Kind = MediaKind.Video, AnalysisStatus = MediaAnalysisStatus.Completed,
            Metadata = new MediaMetadata { Duration = MediaTime.FromSeconds(10), FrameRate = Rate, AvgFrameRate = Rate, Width = 64, Height = 36, AudioCodec = "aac" }
        };
        project.MediaAssets.Add(asset);
        var length = MediaTime.FromSeconds(10);
        v1.Clips.Add(new VideoClip { MediaAssetId = asset.Id, TimelineStart = MediaTime.Zero, Duration = length, SourceIn = MediaTime.Zero, SourceOut = length });
        _video.Add(asset.FilePath, new FakeSource(Rate, 250));
        _audio.Add(asset.FilePath, new FakeAudioSource(10 * AudioFormat.SampleRate));

        _service = new PlaybackService(_video, _stopwatch, NullLogger<PlaybackService>.Instance, new PlaybackSettings(), _audio, _output);
        _service.UpdateSnapshot(PlaybackSnapshotBuilder.Build(project, 1));
        _service.Play();
        return _service;
    }

    [Fact]
    public async Task A_seek_while_playing_restarts_the_sound_on_the_new_default_device()
    {
        var service = PlayingService();
        Assert.True(service.IsAudioAvailable);
        Open(DeviceA).Pull(4_800);

        _devices.Default = DeviceB;                        // e.g. headphones chosen in Windows while playing
        Open(DeviceA).Pull(4_800);                          // no switch during playback: A keeps playing
        Assert.Single(_devices.Opened);

        await service.SeekAsync(MediaTime.FromSeconds(3));  // the restart after the seek takes the new default

        Assert.Equal(new[] { DeviceA, DeviceB }, _devices.Opened.Select(p => p.DeviceId));
        Assert.Equal(1, Open(DeviceA).DisposeCount);
        Assert.Equal(WavePlayback.Playing, Open(DeviceB).PlaybackState);
        Assert.True(service.IsAudioAvailable);
        Assert.Equal(PlaybackState.Playing, service.State);
        Open(DeviceB).Pull(4_800);
        Assert.Equal(MediaTime.FromSeconds(3.1), service.Position); // the new device drives the clock from the seek
    }

    [Fact]
    public async Task A_seek_without_a_device_change_keeps_the_output()
    {
        var service = PlayingService();
        await service.SeekAsync(MediaTime.FromSeconds(3));
        service.Pause();
        service.Play();

        var player = Assert.Single(_devices.Opened);
        Assert.Equal((1, 3, 0), (player.InitCount, player.PlayCount, player.DisposeCount));
    }

    [Fact]
    public void A_device_removed_during_playback_falls_back_to_the_stopwatch_without_a_jump()
    {
        var service = PlayingService();
        Open(DeviceA).Pull(4_800);
        Assert.Equal(MediaTime.FromSeconds(0.1), service.Position);

        Open(DeviceA).Remove();
        _devices.Default = DeviceB;                        // Windows picks another default meanwhile
        service.Update();

        Assert.False(service.IsAudioAvailable);
        Assert.Equal(MediaTime.FromSeconds(0.1), service.Position);   // no jump
        _stopwatch.Advance(0.5);
        Assert.Equal(MediaTime.FromSeconds(0.6), service.Position);   // the Stopwatch drives it now
        Assert.Equal(PlaybackState.Playing, service.State);
        Assert.Single(_devices.Opened);                                // nothing switches during playback
    }

    [Theory]
    [InlineData(DeviceB)] // the lost device is gone: another one is the default now
    [InlineData(DeviceA)] // the same device is the default again (plugged back in)
    public void After_a_lost_device_the_next_play_opens_the_current_default(string nextDefault)
    {
        var service = PlayingService();
        Open(DeviceA).Remove();
        service.Update();
        Assert.False(service.IsAudioAvailable);

        _devices.Default = nextDefault;
        service.Pause();
        service.Play();

        Assert.True(service.IsAudioAvailable);
        Assert.Equal(2, _devices.Opened.Count);
        var failed = _devices.Opened[0];
        Assert.Equal((1, 1), (failed.PlayCount, failed.DisposeCount)); // the failed output is closed, never reused
        Assert.Equal((nextDefault, WavePlayback.Playing), (_devices.Opened[1].DeviceId, _devices.Opened[1].PlaybackState));
    }

    [Fact]
    public void If_the_new_default_cannot_be_opened_playback_stays_on_the_stopwatch()
    {
        var service = PlayingService();
        Open(DeviceA).Remove();
        service.Update();
        _devices.Default = DeviceB;
        _devices.Unopenable.Add(DeviceB);

        service.Pause();
        service.Play();

        Assert.False(service.IsAudioAvailable);                 // unchanged semantics: no sound, playback continues
        Assert.Equal(PlaybackState.Playing, service.State);
        var before = service.Position;
        _stopwatch.Advance(0.2);
        Assert.Equal(before + MediaTime.FromSeconds(0.2), service.Position);
    }
}
