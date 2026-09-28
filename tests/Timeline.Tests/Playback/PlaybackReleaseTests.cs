using System.Diagnostics;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Playback;
using AiVideoEditor.Timeline.Playback;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiVideoEditor.Timeline.Tests.Playback;

/// <summary>
/// Releasing playback when the app closes (D024 Step 9.3): the release runs once, and from its first line on
/// nothing — a late snapshot, transport call or seek, also while the release is still in progress — opens a
/// decoder again.
/// </summary>
public sealed class PlaybackReleaseTests : IAsyncLifetime
{
    private static readonly FrameRate Rate = FrameRate.Fps25;

    private readonly TimelineFixture _f = new();
    private readonly FakeVideoDecoder _video = new();
    private readonly FakeAudioDecoder _audio = new();
    private readonly FakeAudioOutput _output = new();
    private readonly PlaybackService _service;
    private long _version;

    public PlaybackReleaseTests() =>
        _service = new PlaybackService(_video, new FakeReferenceClock(), NullLogger<PlaybackService>.Instance,
            new PlaybackSettings(), _audio, _output);

    public Task InitializeAsync() => Task.CompletedTask;
    public async Task DisposeAsync() => await _service.DisposeAsync();

    private void Publish() => _service.UpdateSnapshot(PlaybackSnapshotBuilder.Build(_f.Project, ++_version));

    /// <summary>A 25 fps video with sound on V1, playing, with its video and audio streams open.</summary>
    private async Task<string> PlayingVideoWithSound()
    {
        var asset = _f.Video(10, Rate, "a.mp4");
        asset.Metadata!.AudioCodec = "aac";
        asset.Metadata.AvgFrameRate = Rate;
        _video.Add(asset.FilePath, new FakeSource(Rate, 250));
        _audio.Add(asset.FilePath, new FakeAudioSource((long)(10 * AudioFormat.SampleRate)));
        Assert.True(_f.Service.AddClip(asset.Id).Success);
        Publish();
        _service.Play();
        await Eventually(() => { _service.Update(); return _video.LiveStreams >= 1 && _audio.LiveStreams >= 1; }, "streams did not open");
        return asset.FilePath;
    }

    /// <summary>Everything that could open a decoder, as a late event or continuation would call it.</summary>
    private async Task CallEverything()
    {
        Publish();                                                    // e.g. a finished analysis rebuilding the snapshot
        _f.Service.MoveClips(new[] { _f.V1.Clips[0].Id }, 5);
        Publish();
        _service.Play();
        _service.Pause();
        _service.Stop();
        Assert.False(await _service.SeekAsync(MediaTime.FromSeconds(3)));
        var frame = _service.Update();
        Assert.True(frame.Layers.IsEmpty);
    }

    [Fact]
    public async Task After_release_no_call_opens_a_decoder_or_starts_the_device()
    {
        await PlayingVideoWithSound();

        await _service.DisposeAsync();
        Assert.Equal((0, 0, false), (_video.LiveStreams, _audio.LiveStreams, _output.IsRunning));
        Assert.Equal(PlaybackState.Paused, _service.State);
        var (videoOpens, audioOpens, starts) = (_video.Requests.Count, _audio.Requests.Count, _output.StartCount);

        await CallEverything();
        await Task.Delay(50); // any reader that would still start has had time to open

        Assert.Equal((videoOpens, audioOpens, starts), (_video.Requests.Count, _audio.Requests.Count, _output.StartCount));
        Assert.Equal((0, 0, false), (_video.LiveStreams, _audio.LiveStreams, _output.IsRunning));
    }

    [Fact]
    public async Task Calls_while_the_release_is_still_in_progress_open_nothing()
    {
        await PlayingVideoWithSound();
        _audio.StreamDisposeDelay = TimeSpan.FromMilliseconds(150); // the release takes a while

        var release = _service.DisposeAsync().AsTask();
        Assert.False(release.IsCompleted);
        var (videoOpens, audioOpens, starts) = (_video.Requests.Count, _audio.Requests.Count, _output.StartCount);

        await CallEverything();
        await release;
        await Task.Delay(50);

        Assert.Equal((videoOpens, audioOpens, starts), (_video.Requests.Count, _audio.Requests.Count, _output.StartCount));
        Assert.Equal((0, 0, false), (_video.LiveStreams, _audio.LiveStreams, _output.IsRunning));
    }

    [Fact]
    public async Task Release_runs_once_and_later_calls_return_it()
    {
        await PlayingVideoWithSound();
        _audio.StreamDisposeDelay = TimeSpan.FromMilliseconds(100);

        var first = _service.DisposeAsync().AsTask();
        var second = _service.DisposeAsync().AsTask();
        Assert.False(second.IsCompleted); // waits for the release in progress, doesn't report it done early
        await Task.WhenAll(first, second);

        Assert.True(_service.DisposeAsync().IsCompletedSuccessfully); // e.g. the host disposing it after the window
        Assert.Equal((0, 0), (_video.LiveStreams, _audio.LiveStreams));
    }

    private static async Task Eventually(Func<bool> condition, string what)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException(what);
            await Task.Delay(1);
        }
    }
}
