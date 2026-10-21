using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Playback;
using AiVideoEditor.Video.Tests;
using Xunit;
using static AiVideoEditor.ExportEndToEnd.Tests.EndToEnd;

namespace AiVideoEditor.ExportEndToEnd.Tests;

/// <summary>
/// Phase 29 (D045 §3, §10 criteria 2 and 4): a project with templates exports through the real pipeline — every frame, the
/// Preview's own pipeline and control drawing each sampled frame byte for byte like the export at the canvas size, on a
/// horizontal and a vertical canvas — and its audio is sample-identical to the same project without the templates: no
/// template changes the sound.
/// </summary>
[Collection(EndToEndCollection.Name)]
public sealed class ExportTemplateEndToEndTests
{
    private readonly E2EMedia _media;

    public ExportTemplateEndToEndTests(E2EMedia media) => _media = media;

    private Task<ExportRun> Export(ProjectBuilder p, string name) =>
        ExportProject(p.Project, Path.Combine(_media.OutputFolder(), name + ".mp4"));

    /// <summary>The pattern with its tone on V1, music on A1, the four templates one after another on V2 (and a text on V1's
    /// second half under them).</summary>
    private ProjectBuilder Build(bool templates, int width = E2EMedia.Width, int height = E2EMedia.Height)
    {
        var p = new ProjectBuilder(FrameRate.Fps25, width, height);
        var v1 = p.VideoTrack();
        p.Video(v1, _media.PatternWithTone(), 0, 100);
        p.Audio(p.AudioTrack(), _media.Tone(), 0, 100).Volume = 0.5;
        var v2 = p.VideoTrack();
        if (templates)
        {
            p.Template(v2, TemplateKind.Rec, 0, 25);
            p.Template(v2, TemplateKind.Vhs, 25, 50);
            p.Template(v2, TemplateKind.Boulder, 50, 75).Route = "7";
            p.Template(v2, TemplateKind.Highlight, 75, 100);
        }
        return p;
    }

    [FfmpegFact]
    public async Task Templates_never_change_the_sound()
    {
        var with = await Export(Build(templates: true), "templates");
        var without = await Export(Build(templates: false), "no-templates");

        AssertValidMp4(with);
        Assert.Equal(without.Pcm.Length, with.Pcm.Length);
        Assert.True(with.Pcm.AsSpan().SequenceEqual(without.Pcm), "a template changed the audio");
        Assert.True(EncoderHarness.DecodeLeft(with.Path).AsSpan().SequenceEqual(EncoderHarness.DecodeLeft(without.Path)), "the encoded audio differs");
        Assert.NotEqual(with.Canvases[30], without.Canvases[30]);                        // the VHS frame was drawn
    }

    public static TheoryData<int, int> Canvases => new() { { E2EMedia.Width, E2EMedia.Height }, { E2EMedia.Height, E2EMedia.Width } };

    [FfmpegTheory]
    [MemberData(nameof(Canvases))]
    public async Task The_preview_draws_every_template_like_the_export(int width, int height)
    {
        var p = Build(templates: true, width, height);
        var run = await Export(p, $"parity-{width}x{height}");
        AssertValidMp4(run);

        var snapshot = PlaybackSnapshotBuilder.Build(p.Project, 1);
        foreach (var n in new long[] { 2, 12, 30, 45, 60, 80, 99 })
        {
            var preview = await Preview(snapshot, n);
            Assert.True(preview.Pixels.AsSpan().SequenceEqual(run.Canvases[(int)n]), $"frame {n} differs from the export");
        }
    }

    /// <summary>«Боулдер» on V2 over the pattern (frames 0–75, ПОПЫТКА, its moment <paramref name="statusAt"/>) — whole, or
    /// split at frame 20 the way Split leaves it (the inner edges without an animation, the right part's counter at 20, the
    /// moment kept in both parts).</summary>
    private ProjectBuilder Boulder(BoulderStatus status, long statusAt, bool split, int width = E2EMedia.Width, int height = E2EMedia.Height)
    {
        var p = new ProjectBuilder(FrameRate.Fps25, width, height);
        p.Video(p.VideoTrack(), _media.PatternWithTone(), 0, 100);
        var v2 = p.VideoTrack();
        var whole = p.Template(v2, TemplateKind.Boulder, 0, 75);
        (whole.Route, whole.Grade, whole.Status, whole.StatusAt) = ("7", "7A", status, MediaTime.FromFrame(statusAt, FrameRate.Fps25));
        if (!split) return p;
        var right = p.Template(v2, TemplateKind.Boulder, 20, 75);
        (right.Route, right.Grade, right.Status, right.StatusAt) = (whole.Route, whole.Grade, whole.Status, whole.StatusAt);
        (right.FadeIn, right.CounterStart) = (MediaTime.Zero, MediaTime.FromFrame(20, FrameRate.Fps25));
        (whole.Duration, whole.FadeOut) = (MediaTime.FromFrame(20, FrameRate.Fps25), MediaTime.Zero);
        return p;
    }

    [FfmpegTheory]
    [MemberData(nameof(Canvases))]
    public async Task The_status_appears_at_its_moment_in_the_export_and_the_preview_also_after_a_split(int width, int height)
    {
        // D045 §17: up to the moment's frame (30) the export shows the route block alone — like the same template without a
        // status; from the next frame the status appears; a split before the moment changes no frame; the Preview draws the
        // frames around the moment like the export.
        var none = await Export(Boulder(BoulderStatus.None, 30, split: false, width, height), $"status-none-{width}x{height}");
        var whole = Boulder(BoulderStatus.Attempt, 30, split: false, width, height);
        var run = await Export(whole, $"status-whole-{width}x{height}");
        var split = await Export(Boulder(BoulderStatus.Attempt, 30, split: true, width, height), $"status-split-{width}x{height}");
        AssertValidMp4(run);

        for (var n = 0; n <= 30; n++)
            Assert.True(run.Canvases[n].AsSpan().SequenceEqual(none.Canvases[n]), $"frame {n}: the status shows before its moment");
        for (var n = 31; n < 75; n++)
            Assert.False(run.Canvases[n].AsSpan().SequenceEqual(none.Canvases[n]), $"frame {n}: no status after its moment");
        // The appearance lasts 0.25 s (frames 31–36 in between); from frame 37 the status is as it is shown from the start.
        var always = await Export(Boulder(BoulderStatus.Attempt, 0, split: false, width, height), $"status-always-{width}x{height}");
        for (var n = 31; n <= 36; n++)
            Assert.False(run.Canvases[n].AsSpan().SequenceEqual(always.Canvases[n]), $"frame {n}: no appearance in between");
        for (var n = 37; n < 75; n++)
            Assert.True(run.Canvases[n].AsSpan().SequenceEqual(always.Canvases[n]), $"frame {n}: the status keeps moving after its appearance");
        for (var n = 0; n < run.Canvases.Count; n++)
            Assert.True(run.Canvases[n].AsSpan().SequenceEqual(split.Canvases[n]), $"frame {n}: the split changed the picture");

        var snapshot = PlaybackSnapshotBuilder.Build(whole.Project, 1);
        foreach (var n in new long[] { 29, 30, 31, 33, 36, 50 })
        {
            var preview = await Preview(snapshot, n);
            Assert.True(preview.Pixels.AsSpan().SequenceEqual(run.Canvases[(int)n]), $"frame {n} differs from the export");
        }
    }

    [FfmpegFact]
    public async Task A_status_moment_after_the_template_s_end_never_shows_the_status()
    {
        var none = await Export(Boulder(BoulderStatus.None, 0, split: false), "status-beyond-none");
        var beyond = await Export(Boulder(BoulderStatus.Top, 80, split: false), "status-beyond");
        for (var n = 0; n < none.Canvases.Count; n++)
            Assert.True(beyond.Canvases[n].AsSpan().SequenceEqual(none.Canvases[n]), $"frame {n}: the status appeared");
    }
}
