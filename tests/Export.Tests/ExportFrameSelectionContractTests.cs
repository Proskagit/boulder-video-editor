using System.Diagnostics;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Composition;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Export;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Core.Playback;
using AiVideoEditor.Timeline;
using AiVideoEditor.Timeline.Playback;
using AiVideoEditor.Timeline.Tests;
using AiVideoEditor.Timeline.Tests.Playback;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiVideoEditor.Export.Tests;

/// <summary>
/// Phase 8 Step 2 (D023): the export's source-frame selection is the Preview's. For every output frame
/// the layers (clips, order, culling) and the decoded source frame of each picture layer must equal what
/// the Preview's <see cref="VideoPipeline"/> shows for that timeline frame — while playing forward from
/// the start and after a seek to it. Both run on the same snapshot with the same fake sources
/// (<see cref="FakeSource"/>: a frame's pixel encodes its index).
/// </summary>
public sealed class ExportFrameSelectionContractTests
{
    private readonly TimelineFixture _f = new();
    private readonly FakeVideoDecoder _previewDecoder = new();
    private readonly FakeVideoDecoder _exportDecoder = new();

    private MediaTime F(long frame) => MediaTime.FromFrame(frame, _f.Rate);

    private MediaAsset Video(FrameRate rate, double seconds, string name, long startPts = 0, int? frames = null, FrameSize? display = null)
    {
        var asset = _f.Video(seconds, rate, name);
        if (display is { } size) (asset.Metadata!.DisplayWidth, asset.Metadata.DisplayHeight) = (size.Width, size.Height);
        Source(asset, new FakeSource(rate, frames ?? (int)Math.Ceiling(seconds * rate.Numerator / rate.Denominator), startPts));
        return asset;
    }

    private void Source(MediaAsset asset, FakeSource source)
    {
        _previewDecoder.Add(asset.FilePath, source);
        _exportDecoder.Add(asset.FilePath, source);
    }

    private static void Ok(TimelineEditResult result) => Assert.True(result.Success, result.Message);

    private PlaybackSnapshot Snapshot() => PlaybackSnapshotBuilder.Build(_f.Project, 1);

    /// <summary>"clip:frame" per layer, bottom to top ("clip:T" for text) — the observable selection.</summary>
    private static string Describe(IEnumerable<(Guid ClipId, DecodedFrame? Frame)> layers) =>
        string.Join(" | ", layers.Select(l => $"{l.ClipId.ToString()[..8]}:{(l.Frame is { } f ? FakeVideoDecoder.Number(f).ToString() : "T")}"));

    // --- the two sides --------------------------------------------------------------------------------

    private async Task<List<string>> ExportFrames(PlaybackSnapshot snapshot)
    {
        await using var source = new ExportFrameSource(snapshot, _exportDecoder);
        var frames = new List<string>();
        for (var n = 0L; n < source.FrameCount; n++)
        {
            var frame = await source.GetFrameAsync(n);
            Assert.Equal(MediaTime.FromFrame(n, snapshot.FrameRate), frame.Time);
            Assert.All(frame.Layers, l => Assert.Equal(l.Layer is TextLayer, l.Frame is null));
            frames.Add(Describe(frame.Layers.Select(l => (l.Layer.ClipId, l.Frame))));
        }
        return frames;
    }

    /// <summary>What the Preview shows for frames [from, to) when it starts playing at <paramref name="from"/>:
    /// each frame once every layer has its current (not late, not pending) picture.</summary>
    private async Task<List<string>> PreviewFrames(PlaybackSnapshot snapshot, long from, long to)
    {
        await using var pipeline = new VideoPipeline(snapshot, 1, from, _previewDecoder,
            new PlaybackSettings { BufferFrames = 4 }, NullLogger.Instance);
        var frames = new List<string>();
        for (var n = from; n < to; n++)
        {
            var watch = Stopwatch.StartNew();
            for (var spin = 0; ; spin++)
            {
                var layers = pipeline.GetFrame(n).Layers;
                Assert.DoesNotContain(layers, l => l.IsPlaceholder);
                if (layers.All(l => l.State == LayerPictureState.Text || l is { State: LayerPictureState.Frame, IsCurrent: true }))
                {
                    frames.Add(Describe(layers.Select(l => (l.Layer.ClipId, l.State == LayerPictureState.Text ? null : l.Frame))));
                    break;
                }
                if (watch.Elapsed > TimeSpan.FromSeconds(10)) throw new TimeoutException($"Preview frame {n} never became current.");
                if (spin < 200) await Task.Yield(); else await Task.Delay(1);
            }
        }
        return frames;
    }

    /// <summary>Every output frame equals the Preview playing from the start, and a sample of frames
    /// equals the Preview right after a seek to them.</summary>
    private async Task<List<string>> AssertMatchesPreview(PlaybackSnapshot snapshot)
    {
        var export = await ExportFrames(snapshot);
        Assert.Equal(ExportOutput.For(snapshot).FrameCount, export.Count);

        var played = await PreviewFrames(snapshot, 0, export.Count);
        for (var n = 0; n < export.Count; n++)
            Assert.True(played[n] == export[n], $"frame {n}: preview '{played[n]}' ≠ export '{export[n]}'");

        var step = Math.Max(1, export.Count / 12);
        for (var k = 0L; k < export.Count; k += step)
            Assert.Equal(export[(int)k], (await PreviewFrames(snapshot, k, k + 1))[0]);
        Assert.Equal(export[^1], (await PreviewFrames(snapshot, export.Count - 1, export.Count))[0]);
        return export;
    }

    // --- rates × speeds, with trim, split, a gap and a second layer ---------------------------------------

    public static TheoryData<int, int, int> RatesAndSpeeds => new()
    {
        // project/source rate, speed in twentieths (5 = 0.25×, 20 = 1×, 80 = 4×)
        { 24000, 1001, 20 }, { 24000, 1001, 5 }, { 24000, 1001, 80 },
        { 30000, 1001, 20 }, { 30000, 1001, 5 }, { 30000, 1001, 80 },
    };

    [Theory]
    [MemberData(nameof(RatesAndSpeeds))]
    public async Task Every_output_frame_selects_the_preview_source_frame(int num, int den, int speedSteps)
    {
        var rate = new FrameRate(num, den);
        var main = Video(rate, speedSteps == 5 ? 3 : 8, "main.mp4");
        Ok(_f.Service.AddClip(main.Id));                                         // locks the project rate
        Assert.Equal(rate, _f.Rate);
        var clip = _f.V1.Clips.Single();
        Ok(_f.Service.SetClipSpeed(clip.Id, ClipSpeed.FromSteps(speedSteps)));

        // trim both ends, split, move the right half on → a gap and a split edge
        Ok(_f.Service.TrimClip(clip.Id, ClipEdge.Start, F(10)));
        Ok(_f.Service.TrimClip(clip.Id, ClipEdge.End, clip.TimelineEnd - F(7)));
        var splitAt = (clip.TimelineStart.ToFrameFloor(rate) + clip.TimelineEnd.ToFrameFloor(rate)) / 2;
        Ok(_f.Service.Split(F(splitAt), new[] { clip.Id }));
        var right = _f.V1.Clips.Single(c => c.TimelineStart == F(splitAt));
        Ok(_f.Service.MoveClips(new[] { right.Id }, 5));

        // a second layer from another rate at 1.35×, partly over both halves
        var other = Video(new FrameRate(25, 1), 10, "other.mp4");
        Ok(_f.Service.AddTrack(TrackType.Video));
        var v2 = _f.Project.Timeline.VideoTracks.Single(t => t.Id != _f.V1.Id);
        Ok(_f.Service.AddClip(other.Id, v2.Id, F(splitAt - 20)));
        var top = v2.Clips.Single();
        Ok(_f.Service.SetClipSpeed(top.Id, ClipSpeed.FromSteps(27)));
        Ok(_f.Service.TrimClip(top.Id, ClipEdge.End, F(splitAt + 40)));
        _f.AssertValid();

        var frames = await AssertMatchesPreview(Snapshot());

        // at 1× the export is also checked against the plain arithmetic of equal rates:
        // the left half shows source frame n (trimmed 10 frames at the start, starting at frame 10),
        // the right half (moved on by 5) source frame n − 5; the gap has no layer at all.
        if (speedSteps == 20)
        {
            var id = clip.Id.ToString()[..8];
            var rightId = right.Id.ToString()[..8];
            for (var n = 10; n < splitAt - 20; n++) Assert.Equal($"{id}:{n}", frames[n]);
            Assert.Equal($"{id}:{splitAt - 1}", frames[(int)splitAt - 1].Split(" | ")[0]);
            for (var n = (int)splitAt; n < splitAt + 5; n++) Assert.DoesNotContain(id, frames[n]);
            Assert.Equal($"{rightId}:{splitAt}", frames[(int)splitAt + 5].Split(" | ")[0]);
            Assert.Equal($"{rightId}:{frames.Count - 1 - 5}", frames[^1]);
        }
        Assert.All(frames.Take(10), f => Assert.DoesNotContain(clip.Id.ToString()[..8], f));   // before the trimmed start
    }

    // --- hold-first / hold-last ---------------------------------------------------------------------------

    [Theory]
    [InlineData(24000, 1001)]
    [InlineData(30000, 1001)]
    public async Task Before_the_first_source_frame_the_first_frame_is_held(int num, int den)
    {
        var rate = new FrameRate(num, den);
        var late = Video(rate, 4, "late-start.mp4", startPts: 3);                // frame i at source time (3 + i)/rate
        Ok(_f.Service.AddClip(late.Id));
        Ok(_f.Service.TrimClip(_f.V1.Clips.Single().Id, ClipEdge.End, F(40)));

        var frames = await AssertMatchesPreview(Snapshot());

        var id = _f.V1.Clips.Single().Id.ToString()[..8];
        for (var n = 0; n < 40; n++)
            Assert.Equal($"{id}:{Math.Max(0, n - 3)}", frames[n]);
    }

    [Theory]
    [InlineData(24000, 1001, 20)]
    [InlineData(30000, 1001, 20)]
    [InlineData(30000, 1001, 80)]
    public async Task After_the_last_source_frame_the_last_frame_is_held(int num, int den, int speedSteps)
    {
        var rate = new FrameRate(num, den);
        var shortStream = Video(rate, 8, "short-stream.mp4", frames: 50);        // metadata says 8 s, the stream has 50 frames
        Ok(_f.Service.AddClip(shortStream.Id));
        var clip = _f.V1.Clips.Single();
        Ok(_f.Service.SetClipSpeed(clip.Id, ClipSpeed.FromSteps(speedSteps)));
        Ok(_f.Service.TrimClip(clip.Id, ClipEdge.End, F(speedSteps == 80 ? 40 : 100)));

        var frames = await AssertMatchesPreview(Snapshot());

        var id = clip.Id.ToString()[..8];
        var lastShown = speedSteps == 80 ? 13 : 49;                               // 4×: frame n shows ⌊4n + ½⌋ → 49 from n = 13 on
        Assert.Equal($"{id}:49", frames[^1]);
        Assert.All(frames.Skip(lastShown), f => Assert.Equal($"{id}:49", f));
    }

    // --- culling, images and text ---------------------------------------------------------------------

    [Fact]
    public async Task Layers_culling_images_and_text_follow_LayersAt_and_culled_clips_are_not_decoded()
    {
        var rate = FrameRate.Ntsc30;
        var bottom = Video(rate, 5, "bottom.mp4");
        var cover = Video(rate, 5, "cover.mp4", display: new FrameSize(1920, 1080));   // opaque, fills the 1920×1080 canvas
        Ok(_f.Service.AddClip(bottom.Id));
        Ok(_f.Service.TrimClip(_f.V1.Clips.Single().Id, ClipEdge.End, F(100)));
        Ok(_f.Service.AddTrack(TrackType.Video));
        Ok(_f.Service.AddTrack(TrackType.Video));
        var tracks = _f.Project.Timeline.VideoTracks.OrderBy(t => t.Order).ToList();
        Ok(_f.Service.AddClip(cover.Id, tracks[1].Id, F(20)));
        Ok(_f.Service.TrimClip(tracks[1].Clips.Single().Id, ClipEdge.End, F(60)));

        var image = _f.Image();
        Source(image, new FakeSource(rate, 1));
        _f.PlaceImageFrames(tracks[2], image, 40, 80, rate);
        Ok(_f.Service.AddTrack(TrackType.Video));
        Ok(_f.Service.AddTextClip(F(50)));                                        // topmost track
        _f.AssertValid();

        var frames = await AssertMatchesPreview(Snapshot());

        var bottomId = _f.V1.Clips.Single().Id.ToString()[..8];
        Assert.All(frames.Skip(20).Take(40), f => Assert.DoesNotContain(bottomId, f));   // culled under the cover
        Assert.Contains(bottomId, frames[60]);
        Assert.Equal(2, _exportDecoder.OpenCount(bottom.FilePath));               // closed while culled, reopened at 60
        Assert.Equal(1, _exportDecoder.OpenCount(image.FilePath));                // a still image is decoded once
        Assert.EndsWith(":T", frames[50]);                                        // text on top
    }
}
