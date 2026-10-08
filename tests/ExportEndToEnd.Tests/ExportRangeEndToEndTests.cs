using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Export;
using AiVideoEditor.Core.Playback;
using AiVideoEditor.Timeline.Commands;
using AiVideoEditor.Video.Tests;
using Xunit;
using static AiVideoEditor.ExportEndToEnd.Tests.EndToEnd;

namespace AiVideoEditor.ExportEndToEnd.Tests;

/// <summary>
/// Phase 15 Step 15.7 (D030 §8, Q10): the export of an In / Out range is the whole-sequence export's pipeline, offset —
/// its frames are byte for byte the whole export's frames at the same timeline frames, its sound is exactly the whole
/// export's samples <c>[⌈In⌉, ⌈Out⌉)</c> (the <c>AudioPlacement</c> rule), at 29.97 fps across a cut, a fade, a 2× clip,
/// a hidden track and a muted one; the Preview drawn at those timeline frames shows the same bytes. Without a range the
/// export is as before.
/// </summary>
[Collection(EndToEndCollection.Name)]
public sealed class ExportRangeEndToEndTests
{
    private const string Green = "20C040";
    private readonly E2EMedia _media;

    public ExportRangeEndToEndTests(E2EMedia media) => _media = media;

    private static async Task<ExportRun> ExportRange(Project project, string path, ExportRange? range)
    {
        var result = ExportPreflight.Check(project, path, EndToEnd.Environment, range);
        Assert.True(result.CanExport, string.Join("; ", result.Errors.Select(e => e.Message)));
        var encoder = new RecordingEncoder(EncoderHarness.Encoder());
        await Service(encoder).ExportAsync(result.Job!, null);
        return new ExportRun(path, result.Job!, encoder.Canvases, encoder.Pcm.ToArray(), Array.Empty<ExportProgress>());
    }

    [FfmpegFact]
    public async Task A_range_export_is_exactly_the_whole_exports_frames_and_samples_of_the_range()
    {
        var rate = FrameRate.Ntsc30;
        var p = new ProjectBuilder(rate);
        var v1 = p.VideoTrack();
        var v2 = p.VideoTrack();
        var a1 = p.AudioTrack();
        var a2 = p.AudioTrack();
        var sync = _media.Sync(rate);
        var first = p.Video(v1, sync, 0, 40);
        first.FadeOut = p.F(12);                                                          // a fade crossing In
        p.Video(v1, sync, 40, 100, speed: ClipSpeed.FromSteps(40));                       // a 2× clip crossing Out
        p.Video(v2, _media.Solid(Green, rate), 0, 100);                                   // hidden below
        new SetTrackStateCommand(v2, TrackStateFlag.Hidden, true).Execute();
        var tone = p.Audio(a1, _media.Tone(), 10, 110);
        tone.FadeIn = p.F(30);
        p.Audio(a2, _media.Bursts(), 0, 110);                                             // muted
        new SetTrackStateCommand(a2, TrackStateFlag.Muted, true).Execute();

        var whole = await ExportRange(p.Project, Path.Combine(_media.OutputFolder(), "range-whole.mp4"), null);
        var part = await ExportRange(p.Project, Path.Combine(_media.OutputFolder(), "range-part.mp4"), new ExportRange(33, 71));

        AssertValidMp4(part);
        Assert.Equal(110, whole.Canvases.Count);
        Assert.Equal(38, part.Canvases.Count);
        for (var m = 0; m < part.Canvases.Count; m++)
            Assert.True(part.Canvases[m].AsSpan().SequenceEqual(whole.Canvases[33 + m]), $"range frame {m} ≠ timeline frame {33 + m}");

        var firstSample = (int)part.Output.FirstSample;
        Assert.Equal(AudioTiming.CeilingSample(p.F(33)), firstSample);
        Assert.Equal(AudioTiming.CeilingSample(p.F(71)) - firstSample, part.Output.AudioSampleCount);
        Assert.Equal(part.Output.AudioSampleCount * 2, part.Pcm.Length);
        Assert.True(part.Pcm.AsSpan().SequenceEqual(whole.Pcm.AsSpan(firstSample * 2, part.Pcm.Length)), "the range's samples differ");

        foreach (var m in new[] { 0, 6, 7, 37 })                                          // In, across the cut at 40, Out − 1
        {
            var preview = await Preview(part.Job.Snapshot, 33 + m);
            var differing = Enumerable.Range(0, part.Canvases[m].Length).Count(i => preview.Pixels[i] != part.Canvases[m][i]);
            Assert.True(differing == 0, $"range frame {m}: {differing} bytes differ from the Preview at timeline frame {33 + m}");
        }
        AssertNoFfmpegLeft();
    }

    /// <summary>Phase 15 Step 15.9 (found by the R2 regression): a long image clip entered far past the decoder's preroll —
    /// a range starting 16 s into it, the Preview seeked there — shows its one frame: a still is decoded from its file's
    /// start (<c>SourceFrameSelector.StillImage</c>), not at the timeline's sample point (which found no frame before).</summary>
    [FfmpegFact]
    public async Task A_range_inside_a_long_image_clip_exports_and_previews_the_image()
    {
        var rate = FrameRate.Fps25;
        var p = new ProjectBuilder(rate);
        var v1 = p.VideoTrack();
        p.Image(v1, _media.PngAlpha(), 0, 500);                                           // 20 s, its only frame at 0

        var whole = await ExportRange(p.Project, Path.Combine(_media.OutputFolder(), "range-image-whole.mp4"), null);
        var part = await ExportRange(p.Project, Path.Combine(_media.OutputFolder(), "range-image-part.mp4"), new ExportRange(400, 410));

        AssertValidMp4(part);
        Assert.Equal(10, part.Canvases.Count);
        for (var m = 0; m < part.Canvases.Count; m++)
            Assert.True(part.Canvases[m].AsSpan().SequenceEqual(whole.Canvases[400 + m]), $"range frame {m} ≠ timeline frame {400 + m}");
        Assert.True(part.Canvases[0].AsSpan().SequenceEqual(whole.Canvases[0]), "the image held from frame 0 is not the picture at 16 s");
        var lit = Enumerable.Range(0, whole.Canvases[0].Length / 4).Count(i => whole.Canvases[0][4 * i] + whole.Canvases[0][4 * i + 1] + whole.Canvases[0][4 * i + 2] > 0);
        Assert.True(lit > whole.Canvases[0].Length / 16, $"only {lit} pixels drawn: the image is missing");   // not the black background

        foreach (var n in new long[] { 400, 409 })                                         // the Preview seeked 16 s in: no placeholder
        {
            var preview = await Preview(part.Job.Snapshot, n);
            var differing = Enumerable.Range(0, whole.Canvases[(int)n].Length).Count(i => preview.Pixels[i] != whole.Canvases[(int)n][i]);
            Assert.True(differing == 0, $"timeline frame {n}: {differing} bytes differ from the export");
        }
        AssertNoFfmpegLeft();
    }
}
