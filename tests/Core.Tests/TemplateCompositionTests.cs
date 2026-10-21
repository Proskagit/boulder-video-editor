using System.Collections.Immutable;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Composition;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Localization;
using AiVideoEditor.Core.Playback;
using Xunit;

namespace AiVideoEditor.Core.Tests;

/// <summary>
/// Phase 29 (D045 §3): the template rules (defaults, limits, validation), the template's place in the composition — a layer
/// over what is below its track that never hides it and adds no sound —, its clock on every frame (the appearance /
/// disappearance from the effective fades, the counter continuing from its start), its draw operation, and the VHS
/// treatment: deterministic, opaque, a function of the pixels and the clock only.
/// </summary>
public class TemplateCompositionTests
{
    private static readonly FrameRate Rate = FrameRate.Fps25;
    private readonly Project _project = new() { Settings = { FrameRate = Rate } };
    private readonly Track _v1 = new() { Type = TrackType.Video, Name = "V1", Order = 0 };
    private readonly Track _v2 = new() { Type = TrackType.Video, Name = "V2", Order = 1 };

    public TemplateCompositionTests() => _project.Timeline.VideoTracks.AddRange(new[] { _v1, _v2 });

    private static MediaTime F(long frame) => MediaTime.FromFrame(frame, Rate);

    private VideoClip Video(long start, long end)
    {
        var asset = new MediaAsset
        {
            FilePath = $@"C:\media\{Guid.NewGuid():N}.mp4", Kind = MediaKind.Video,
            Metadata = new MediaMetadata
            {
                Duration = MediaTime.FromSeconds(60), FrameRate = Rate, Width = 1920, Height = 1080, DisplayRotation = 0,
                DisplayWidth = 1920, DisplayHeight = 1080, AudioCodec = "aac"
            }
        };
        _project.MediaAssets.Add(asset);
        var clip = new VideoClip { MediaAssetId = asset.Id, TimelineStart = F(start), Duration = F(end) - F(start), SourceOut = F(end) - F(start) };
        _v1.Clips.Add(clip);
        return clip;
    }

    private TemplateClip Template(TemplateKind kind, long start, long end, long fadeIn = 5, long fadeOut = 5, long counter = 0)
    {
        var fields = TemplateRules.Defaults(kind);
        var clip = new TemplateClip
        {
            Kind = kind, TimelineStart = F(start), Duration = F(end) - F(start), FadeIn = F(fadeIn), FadeOut = F(fadeOut),
            CounterStart = F(counter), Title = fields.Title, AccentHex = fields.AccentHex
        };
        _v2.Clips.Add(clip);
        return clip;
    }

    // --- rules -----------------------------------------------------------------------------------------------------------

    [Fact]
    public void The_defaults_of_each_kind()
    {
        Assert.Equal((10.0, 10.0, 8.0, 3.0), (TemplateRules.DefaultDuration(TemplateKind.Rec).TotalSeconds,
            TemplateRules.DefaultDuration(TemplateKind.Vhs).TotalSeconds, TemplateRules.DefaultDuration(TemplateKind.Boulder).TotalSeconds,
            TemplateRules.DefaultDuration(TemplateKind.Highlight).TotalSeconds));
        Assert.Equal((0.0, 0.0), TemplateRules.Animation(TemplateKind.Vhs));
        Assert.True(TemplateRules.HasTreatment(TemplateKind.Vhs));
        Assert.All(new[] { TemplateKind.Rec, TemplateKind.Boulder, TemplateKind.Highlight }, k => Assert.False(TemplateRules.HasTreatment(k)));
        Assert.Equal(Messages.Template_DefaultTitle, TemplateRules.Defaults(TemplateKind.Highlight).Title);
        Assert.Equal("", TemplateRules.Defaults(TemplateKind.Boulder).Title);
        Assert.Equal(BoulderStatus.None, TemplateRules.Defaults(TemplateKind.Boulder).Status);
        Assert.Null(TemplateRules.Validate(TemplateRules.Defaults(TemplateKind.Rec)));
    }

    [Fact]
    public void Validation_limits_the_fields()
    {
        var ok = TemplateRules.Defaults(TemplateKind.Boulder) with { Route = new string('7', 12), Grade = "6B+", Date = "27.09.2026" };
        Assert.Null(TemplateRules.Validate(ok));
        Assert.Equal(string.Format(Messages.Template_FieldLength, 12), TemplateRules.Validate(ok with { Route = new string('7', 13) }));
        Assert.Equal(Messages.Template_OneLine, TemplateRules.Validate(ok with { Subtitle = "a\tb" }));
        Assert.Equal(Messages.Template_FieldMissing, TemplateRules.Validate(ok with { Grade = null! }));
        Assert.Equal(Messages.Prop_ColorFormat, TemplateRules.Validate(ok with { AccentHex = "#FFF" }));
        Assert.Equal(Messages.Template_UnknownStatus, TemplateRules.Validate(ok with { Status = (BoulderStatus)3 }));
    }

    // --- the composition ------------------------------------------------------------------------------------------------

    [Fact]
    public void A_template_is_a_layer_above_the_picture_that_hides_nothing_and_adds_no_sound()
    {
        var video = Video(0, 100);
        var template = Template(TemplateKind.Vhs, 10, 60);
        var snapshot = PlaybackSnapshotBuilder.Build(_project, 1);

        var layers = snapshot.LayersAt(F(20));
        Assert.Equal(2, layers.Length);
        Assert.Equal(video.Id, Assert.IsType<PictureLayer>(layers[0]).ClipId);
        var layer = Assert.IsType<TemplateLayer>(layers[1]);
        Assert.Equal((template.Id, TemplateKind.Vhs, 1.0), (layer.ClipId, layer.Kind, layer.Opacity));
        Assert.Single(snapshot.AudioSpans);                                   // the video's only
        Assert.Equal(video.Id, snapshot.AudioSpans[0].ClipId);
        Assert.Single(snapshot.LayersAt(F(60)));                              // half-open: gone at its end
    }

    [Fact]
    public void A_template_below_a_full_canvas_video_is_hidden_by_it_like_any_layer()
    {
        var top = Video(0, 50);                                           // on V1 …
        _v1.Clips.Remove(top);
        _v2.Clips.Add(top);                                               // … moved over the template's track
        _v1.Clips.Add(new TemplateClip { Kind = TemplateKind.Rec, TimelineStart = F(0), Duration = F(50), AccentHex = TemplateRules.DefaultAccentHex });

        var layers = PlaybackSnapshotBuilder.Build(_project, 1).LayersAt(F(10));

        Assert.Equal(top.Id, Assert.Single(layers).ClipId);
    }

    [Fact]
    public void The_clock_runs_in_whole_frames_with_the_effective_fades_and_the_counter()
    {
        Template(TemplateKind.Rec, 10, 60, fadeIn: 5, fadeOut: 10, counter: 25);
        var snapshot = PlaybackSnapshotBuilder.Build(_project, 1);
        TemplateClock At(long frame) => ((TemplateLayer)snapshot.LayersAt(F(frame)).Single()).Clock;

        var first = At(10);
        Assert.Equal((0.0, 2.0, 0.2, 0.4, 1.0, 25L), (first.Elapsed, first.Remaining, first.Intro, first.Outro, first.Counter, first.CounterFrame));
        Assert.Equal((0.0, 1.0), (first.In, first.Out));
        var middle = At(35);
        Assert.Equal((1.0, 1.0, 1.0), (middle.Elapsed, middle.In, middle.Out));
        Assert.Equal((2.0, 50L), (middle.Counter, middle.CounterFrame));
        var last = At(59);
        Assert.Equal(0.04, last.Remaining, 9);
        Assert.Equal(0.1, last.Out, 9);
        Assert.Equal(0.6, At(13).In, 9);
    }

    [Fact]
    public void The_status_moment_is_on_the_counter_s_clock_in_whole_frames()
    {
        // D045 §17: a right part of a split (its counter at 25 frames) whose status appears 40 frames after the original start.
        var clip = Template(TemplateKind.Boulder, 10, 60, fadeIn: 0, fadeOut: 5, counter: 25);
        clip.Status = BoulderStatus.Attempt;
        clip.StatusAt = F(40);
        var snapshot = PlaybackSnapshotBuilder.Build(_project, 1);
        TemplateClock At(long frame) => ((TemplateLayer)snapshot.LayersAt(F(frame)).Single()).Clock;

        Assert.Equal(-0.6, At(10).SinceStatus, 9);          // counter frame 25: 15 frames before the moment
        Assert.Equal(0.0, At(25).SinceStatus, 9);           // counter frame 40: the frame its appearance starts on
        Assert.Equal(0.04, At(26).SinceStatus, 9);
        Assert.Equal(1.36, At(59).SinceStatus, 9);

        clip.StatusAt = F(200);                            // after the clip's end: never reached
        var later = PlaybackSnapshotBuilder.Build(_project, 2);
        Assert.All(Enumerable.Range(10, 50), f => Assert.True(((TemplateLayer)later.LayersAt(F(f)).Single()).Clock.SinceStatus < 0));
    }

    [Fact]
    public void A_negative_status_moment_is_invalid()
    {
        Assert.Equal(Messages.Template_StatusAtNegative,
            TemplateRules.Validate(TemplateRules.Defaults(TemplateKind.Boulder) with { StatusAt = new MediaTime(-1) }));
        Assert.Equal(MediaTime.Zero, TemplateRules.Defaults(TemplateKind.Boulder).StatusAt);
    }

    [Fact]
    public void A_template_draws_through_the_viewport_with_its_kind_fields_and_clock()
    {
        var template = Template(TemplateKind.Boulder, 0, 50);
        template.Route = "7";
        var layer = (TemplateLayer)PlaybackSnapshotBuilder.Build(_project, 1).LayersAt(F(20)).Single();
        var viewport = CompositionDrawPlan.Viewport(new FrameSize(1920, 1080), 960, 540);

        var op = Assert.IsType<TemplateDraw>(CompositionDrawPlan.Operation(new FrameSize(1920, 1080), viewport, new ResolvedLayer(layer, null)));

        Assert.Equal((viewport, 1.0, new FrameSize(1920, 1080), TemplateKind.Boulder, "7"), (op.Transform, op.Opacity, op.Canvas, op.Kind, op.Fields.Route));
        Assert.Equal(layer.Clock, op.Clock);
        Assert.False(op.HasTreatment);
    }

    [Fact]
    public void Template_timing_changes_are_not_presentation_only_but_field_changes_are()
    {
        var template = Template(TemplateKind.Highlight, 0, 50);
        var a = PlaybackSnapshotBuilder.Build(_project, 1);
        template.Title = "ПЕРВЫЙ ТОП";
        var b = PlaybackSnapshotBuilder.Build(_project, 2);
        template.Duration = F(40);
        var c = PlaybackSnapshotBuilder.Build(_project, 3);

        Assert.True(a.DiffersOnlyInPresentation(b));
        Assert.False(b.DiffersOnlyInPresentation(c));
    }

    // --- the VHS treatment ----------------------------------------------------------------------------------------------

    private static byte[] Gradient(int w, int h)
    {
        var p = new byte[w * h * 4];
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var i = (y * w + x) * 4;
            (p[i], p[i + 1], p[i + 2], p[i + 3]) = ((byte)(x * 255 / w), (byte)(y * 255 / h), (byte)((x + y) % 256), 255);
        }
        return p;
    }

    [Fact]
    public void The_vhs_treatment_is_deterministic_opaque_and_changes_the_picture()
    {
        var clock = new TemplateClock(1, 5, 0, 0, 1.0, 25);
        var a = Gradient(320, 180);
        var b = Gradient(320, 180);
        var source = Gradient(320, 180);

        TemplateTreatment.Vhs(a, 320, 180, 320 * 4, clock);
        TemplateTreatment.Vhs(b, 320, 180, 320 * 4, clock);

        Assert.Equal(a, b);
        Assert.All(Enumerable.Range(0, 320 * 180), i => Assert.Equal(255, a[i * 4 + 3]));
        Assert.NotEqual(source, a);
        var other = Gradient(320, 180);
        TemplateTreatment.Vhs(other, 320, 180, 320 * 4, clock with { CounterFrame = 26 });
        Assert.NotEqual(a, other);                                            // the noise moves with the frame
    }

    [Fact]
    public void The_tracking_band_crosses_only_in_its_phase_and_the_stride_is_respected()
    {
        // Counter 3.8 s: (3.8 + 1.2) mod 4.8 = 0.2 s into the crossing — a band; 4.2 s: 0.6 → still crossing; 1.0 s: 2.2 → rest.
        double Jitter(double counter)
        {
            var p = Gradient(400, 400);
            TemplateTreatment.Vhs(p, 400, 400, 400 * 4, new TemplateClock(0, 1, 0, 0, counter, 7));
            var q = Gradient(400, 400);
            TemplateTreatment.Vhs(q, 400, 400, 400 * 4, new TemplateClock(0, 1, 0, 0, counter, 8));
            return p.Zip(q).Count(t => t.First != t.Second) / (double)p.Length;
        }
        Assert.True(Jitter(3.8) > Jitter(1.0));

        var padded = new byte[(64 * 4 + 16) * 32];
        Assert.Throws<ArgumentException>(() => TemplateTreatment.Vhs(new byte[10], 64, 32, 64 * 4, default));
        TemplateTreatment.Vhs(padded, 64, 32, 64 * 4 + 16, default);
        for (var y = 0; y < 32; y++)
            Assert.All(padded.AsSpan(y * (64 * 4 + 16) + 64 * 4, 16).ToArray(), v => Assert.Equal(0, v));   // the row padding untouched
    }
}
