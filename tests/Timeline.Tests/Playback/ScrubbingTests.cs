using System.Diagnostics;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Playback;
using AiVideoEditor.Timeline.Playback;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiVideoEditor.Timeline.Tests.Playback;

/// <summary>
/// D057 §3.13: live scrubbing — seeks made while the previous one has no picture yet are not queued: only the newest waits
/// and then starts, the ones it supersedes are dropped; the position is the newest at once; after the last seek the picture
/// is exactly its frame; playback is unchanged. The owner's acceptance of 2026-11-10: the previous seek's finished picture
/// is shown (once, as an interim picture) before the waiting seek replaces it, so a drag shows a picture per decode; a short
/// step forward while paused reads on with the running decoders (no new decoder), a step back or a longer jump restarts.
/// </summary>
public sealed class ScrubbingTests : IAsyncLifetime
{
    private static readonly FrameRate Rate = FrameRate.Fps25;
    private readonly TimelineFixture _f = new();
    private readonly FakeVideoDecoder _decoder = new();
    private readonly FakeReferenceClock _clock = new();
    private PlaybackService _service;
    private MediaAsset _asset = null!;

    public ScrubbingTests() =>
        _service = new PlaybackService(_decoder, _clock, NullLogger<PlaybackService>.Instance, new PlaybackSettings { BufferFrames = 4 });

    public Task InitializeAsync() => Task.CompletedTask;
    public async Task DisposeAsync() => await _service.DisposeAsync();

    private static MediaTime F(long frame) => MediaTime.FromFrame(frame, Rate);

    private async Task SetupAsync()
    {
        _asset = _f.Video(40, Rate, "scrub.mp4");
        _asset.Metadata!.AvgFrameRate = Rate;
        _decoder.Add(_asset.FilePath, new FakeSource(Rate, 1000));
        Assert.True(_f.Service.AddClip(_asset.Id).Success);
        _service.UpdateSnapshot(PlaybackSnapshotBuilder.Build(_f.Project, 1));
        Assert.True(await TickUntilAsync(_service.SeekAsync(MediaTime.Zero)));   // as the Preview: its timer calls Update
        await SettleAsync();
    }

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

    /// <summary>Calls Update (as the Preview's timer does) until <paramref name="seek"/> completes.</summary>
    private async Task<bool> TickUntilAsync(Task<bool> seek)
    {
        var watch = Stopwatch.StartNew();
        while (!seek.IsCompleted)
        {
            _service.Update();
            if (watch.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException("the seek did not complete");
            await Task.Delay(1);
        }
        return await seek;
    }

    private static async Task UntilAsync(Func<bool> condition)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException("condition not met");
            await Task.Delay(1);
        }
    }

    private static int Number(PlaybackFrame frame) => FakeVideoDecoder.Number(frame.TopPicture()!.Frame!);

    [Fact]
    public async Task Seeks_while_the_previous_has_no_picture_are_dropped_and_the_newest_is_shown()
    {
        await SetupAsync();
        var gate = _decoder.Gate(_asset.FilePath);                         // the decoder is slow from now on
        var opensBefore = _decoder.OpenCount(_asset.FilePath);

        var first = _service.SeekAsync(F(100));
        var dropped = new List<Task<bool>>();
        for (var f = 110; f < 200; f += 10) dropped.Add(_service.SeekAsync(F(f)));
        var newest = _service.SeekAsync(F(250));

        Assert.True(_service.HasPendingSeek);
        Assert.Equal(F(250), _service.Position);                            // the playhead never steps back
        await UntilAsync(() => _decoder.OpenCount(_asset.FilePath) >= opensBefore + 1);
        Assert.Equal(opensBefore + 1, _decoder.OpenCount(_asset.FilePath)); // only the first seek opened a decoder

        gate.SetResult();
        await first;                                                        // true, or false once the newest replaced it
        Assert.All(await Task.WhenAll(dropped), Assert.False);               // superseded: dropped, never decoded
        Assert.True(await TickUntilAsync(newest));                          // started once the first's picture is there
        Assert.Equal(opensBefore + 2, _decoder.OpenCount(_asset.FilePath)); // the first and the newest only
        Assert.False(_service.HasPendingSeek);

        var shown = await SettleAsync();
        Assert.Equal(250, Number(shown));                                   // exactly the final position
        Assert.Equal(F(250), shown.Position);
    }

    [Fact]
    public async Task While_the_newest_waits_the_picture_is_the_waiting_pipelines_own()
    {
        await SetupAsync();
        var gate = _decoder.Gate(_asset.FilePath);
        var first = _service.SeekAsync(F(300));
        var newest = _service.SeekAsync(F(320));
        var frame = _service.Update();
        Assert.Equal(F(320), frame.Position);
        Assert.True(frame.IsBuffering);
        gate.SetResult();
        await first;
        Assert.True(await TickUntilAsync(newest));
        Assert.Equal(320, Number(await SettleAsync()));
    }

    [Fact]
    public async Task A_drag_shows_each_finished_picture_before_the_newest_seek_starts()
    {
        // The fallback start is pushed far out, so only the Update path is at work (a loaded test machine may be late).
        await using var service = new PlaybackService(_decoder, _clock, NullLogger<PlaybackService>.Instance,
            new PlaybackSettings { BufferFrames = 4, InterimPictureGrace = TimeSpan.FromSeconds(30) });
        _service = service;
        await SetupAsync();
        var gate = _decoder.Gate(_asset.FilePath);
        var first = _service.SeekAsync(F(300));
        var newest = _service.SeekAsync(F(200));
        Assert.True(_service.HasPendingSeek);
        gate.SetResult();

        PlaybackFrame interim = default;
        await UntilAsync(() => (interim = _service.Update()).IsInterimPicture);
        Assert.Equal(300, Number(interim));                                 // the first seek's picture, shown
        Assert.True(interim.IsBuffering);                                   // the newest still waits …
        Assert.Equal(F(200), interim.Position);                             // … and is the position
        Assert.True(_service.HasPendingSeek);                               // it starts only on the next Update
        Assert.True(await first);
        Assert.True(await TickUntilAsync(newest));
        Assert.Equal(200, Number(await SettleAsync()));
    }

    [Fact]
    public async Task Short_steps_forward_read_on_and_a_step_back_or_a_jump_restarts()
    {
        await SetupAsync();
        Assert.True(await _service.SeekAsync(F(100)));
        Assert.Equal(100, Number(await SettleAsync()));
        var opens = _decoder.OpenCount(_asset.FilePath);

        for (var f = 101; f <= 112; f++)                                   // 12 frames = 0.48 s at 25 fps: each within reach
        {
            Assert.True(await _service.SeekAsync(F(f)));
            var shown = await SettleAsync();
            Assert.Equal(f, Number(shown));                                  // exactly the frame
            Assert.Equal(F(f), shown.Position);
        }
        Assert.Equal(opens, _decoder.OpenCount(_asset.FilePath));          // no new decoder

        Assert.True(await _service.SeekAsync(F(111)));                      // a step back
        Assert.Equal(111, Number(await SettleAsync()));
        Assert.Equal(opens + 1, _decoder.OpenCount(_asset.FilePath));
        Assert.True(await _service.SeekAsync(F(111 + 13)));                 // 0.52 s ahead: beyond the reach
        Assert.Equal(124, Number(await SettleAsync()));
        Assert.Equal(opens + 2, _decoder.OpenCount(_asset.FilePath));
    }

    [Fact]
    public async Task A_seek_after_the_picture_is_there_starts_at_once_as_before()
    {
        await SetupAsync();
        Assert.True(await _service.SeekAsync(F(40)));
        Assert.Equal(40, Number(await SettleAsync()));
        Assert.True(await _service.SeekAsync(F(60)));
        Assert.False(_service.HasPendingSeek);
        Assert.Equal(60, Number(await SettleAsync()));
    }
}
