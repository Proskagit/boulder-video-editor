using System.Diagnostics;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Playback;
using AiVideoEditor.Timeline.Playback;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiVideoEditor.Timeline.Tests.Playback;

/// <summary>
/// D057 §3.14: paused at a position between the project's frames, the Preview shows the exact source frame there — the
/// pipeline counts on the navigation grid of the highest source rate; on the project's grid it is as before; Play runs on the
/// project's grid again.
/// </summary>
public sealed class NavigationGridPlaybackTests : IAsyncLifetime
{
    private static readonly FrameRate Project = FrameRate.Fps25;
    private static readonly FrameRate Fast = new(100, 1);

    private readonly TimelineFixture _f = new();
    private readonly FakeVideoDecoder _decoder = new();
    private readonly FakeReferenceClock _clock = new();
    private readonly PlaybackService _service;
    private long _version;

    public NavigationGridPlaybackTests() =>
        _service = new PlaybackService(_decoder, _clock, NullLogger<PlaybackService>.Instance, new PlaybackSettings { BufferFrames = 4 });

    public Task InitializeAsync() => Task.CompletedTask;
    public async Task DisposeAsync() => await _service.DisposeAsync();

    private MediaAsset Video(string name, FrameRate rate, int frames)
    {
        var asset = _f.Video(frames * (double)rate.Denominator / rate.Numerator, rate, name);
        asset.Metadata!.AvgFrameRate = rate;
        _decoder.Add(asset.FilePath, new FakeSource(rate, frames));
        return asset;
    }

    private void Publish() => _service.UpdateSnapshot(PlaybackSnapshotBuilder.Build(_f.Project, ++_version));

    private async Task<PlaybackFrame> SettleAsync()
    {
        var watch = Stopwatch.StartNew();
        while (true)
        {
            var frame = _service.Update();
            if (!frame.IsBuffering && frame.IsTopCurrent()) return frame;
            if (watch.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException($"No settled picture; last: {frame}");
            await Task.Delay(1);
        }
    }

    private static int Number(PlaybackFrame frame) => FakeVideoDecoder.Number(frame.TopPicture()!.Frame!);

    private void Setup()
    {
        var first = Video("first.mp4", Project, 50);                        // fixes the project's 25 fps (D007)
        Assert.True(_f.Service.AddClip(first.Id).Success);
        var fast = Video("fast.mp4", Fast, 400);                            // 4 s at 100 fps after it, from 2 s
        Assert.True(_f.Service.AddClip(fast.Id).Success);
        Assert.Equal(Project, _f.Settings.FrameRate);
        Publish();
    }

    [Fact]
    public async Task Between_the_projects_frames_the_exact_source_frame_is_shown()
    {
        Setup();
        var at = MediaTime.FromSeconds(2) + MediaTime.FromFrame(5, Fast);   // the 100 fps clip's source frame 5
        Assert.True(await _service.SeekAsync(at));
        var frame = await SettleAsync();
        Assert.Equal(5, Number(frame));
        Assert.Equal(Fast, frame.Rate);
        Assert.Equal(at, MediaTime.FromFrame(frame.TimelineFrame, frame.Rate!.Value));
    }

    [Fact]
    public async Task On_the_projects_grid_it_is_as_before_and_play_returns_to_it()
    {
        Setup();
        Assert.True(await _service.SeekAsync(MediaTime.FromSeconds(2) + MediaTime.FromFrame(2, Project)));
        var onGrid = await SettleAsync();
        Assert.Equal(Project, onGrid.Rate);
        Assert.Equal(8, Number(onGrid));                                    // 2 project frames = 8 source frames

        Assert.True(await _service.SeekAsync(MediaTime.FromSeconds(2) + MediaTime.FromFrame(7, Fast)));
        Assert.Equal(Fast, (await SettleAsync()).Rate);
        _service.Play();
        var playing = _service.Update();
        Assert.Equal(Project, playing.Rate);                                // played on the project's grid
        Assert.Equal(MediaTime.FromSeconds(2) + MediaTime.FromFrame(1, Project), playing.Position);   // from the frame holding it
    }
}
