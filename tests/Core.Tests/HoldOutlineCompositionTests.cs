using System.Collections.Immutable;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Composition;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Playback;
using Xunit;

namespace AiVideoEditor.Core.Tests;

/// <summary>
/// Phase 19 Step 19.3 (D034 §4, §6): the route outline in the snapshot and the shared draw plan — only video clips with
/// holds carry one; a markup change is presentation-only; the outline's geometry follows the layer (crop, scale,
/// rotation, position) in the frame's crop-local coordinates; the stroke per the canvas's short side; the outline takes the
/// layer's opacity (fades, dissolves) and vanishes with a hidden track.
/// </summary>
public class HoldOutlineCompositionTests
{
    private static readonly FrameRate Rate = FrameRate.Fps25;
    private static MediaTime F(long n) => MediaTime.FromFrame(n, Rate);

    private readonly Project _project = new() { Settings = { FrameRate = Rate } };   // 1920 × 1080
    private readonly Track _v1 = new() { Type = TrackType.Video, Name = "V1", Order = 0 };

    public HoldOutlineCompositionTests() => _project.Timeline.VideoTracks.Add(_v1);

    private static readonly ImmutableArray<NormalizedPoint> Square = ImmutableArray.Create(
        new NormalizedPoint(0.4, 0.4), new NormalizedPoint(0.6, 0.4), new NormalizedPoint(0.6, 0.6), new NormalizedPoint(0.4, 0.6));

    private static HoldMarkup Markup(double width = 4) => RouteColor.WithResolved(new HoldMarkup
    {
        StrokeWidth1080 = width,
        Holds = ImmutableArray.Create(new Hold(Guid.NewGuid(), Square, RouteColor.HexToOkLab("#E53935"), RouteColor.HexToOkLab("#EEEEEE"), MediaTime.Zero))
    });

    private VideoClip Video(long start, long end, HoldMarkup? markup = null, VisualProperties? visual = null)
    {
        var asset = new MediaAsset
        {
            FilePath = $@"C:\media\{Guid.NewGuid():N}.mp4", Kind = MediaKind.Video,
            Metadata = new MediaMetadata
            {
                Duration = MediaTime.FromSeconds(60), FrameRate = Rate,
                Width = 1920, Height = 1080, DisplayRotation = 0, DisplayWidth = 1920, DisplayHeight = 1080
            }
        };
        _project.MediaAssets.Add(asset);
        var v = visual ?? VisualProperties.Default;
        var clip = new VideoClip
        {
            MediaAssetId = asset.Id, TimelineStart = F(start), Duration = F(end) - F(start), HoldMarkup = markup,
            PositionX = v.PositionX, PositionY = v.PositionY, Scale = v.Scale, RotationDegrees = v.RotationDegrees, Opacity = v.Opacity, Crop = v.Crop
        };
        clip.SourceOut = clip.Duration;
        _v1.Clips.Add(clip);
        _v1.Clips.Sort((a, b) => a.TimelineStart.CompareTo(b.TimelineStart));
        return clip;
    }

    private PlaybackSnapshot Snapshot() => PlaybackSnapshotBuilder.Build(_project, 1);

    private static DecodedFrame Frame(int w = 1920, int h = 1080) =>
        new(w, h, w * 4, new byte[w * h * 4], new SourceTimestamp(0, new TimeBase(1, 25)));

    private OutlineDraw? OutlineAt(MediaTime time, Guid clipId)
    {
        var layers = Snapshot().LayersAt(time).Select(l => new ResolvedLayer(l, Frame()));
        return CompositionDrawPlan.Build(new FrameSize(_project.Settings.FrameWidth, _project.Settings.FrameHeight), Affine2D.Identity, layers)
            .Operations.OfType<OutlineDraw>().SingleOrDefault(o => o.ClipId == clipId);
    }

    [Fact]
    public void Only_a_video_clip_with_holds_carries_an_outline()
    {
        var marked = Video(0, 25, Markup());
        var empty = Video(25, 50, new HoldMarkup());
        var none = Video(50, 75);
        var spans = Snapshot().VideoLayers.Single().Spans;
        Assert.NotNull(spans.Single(s => s.ClipId == marked.Id).Outline);
        Assert.Null(spans.Single(s => s.ClipId == empty.Id).Outline);
        Assert.Null(spans.Single(s => s.ClipId == none.Id).Outline);
        Assert.Equal(marked.HoldMarkup!.ColorHex, spans.Single(s => s.ClipId == marked.Id).Outline!.ColorHex);
    }

    [Fact]
    public void A_markup_change_is_presentation_only()
    {
        var clip = Video(0, 25, Markup());
        var before = Snapshot();
        clip.HoldMarkup = Markup(8);
        Assert.True(Snapshot().DiffersOnlyInPresentation(before));
        clip.HoldMarkup = null;
        Assert.True(Snapshot().DiffersOnlyInPresentation(before));
    }

    [Fact]
    public void The_outline_lives_in_the_frames_crop_local_space_and_follows_the_layer()
    {
        var visual = VisualProperties.Default with { Crop = new CropRect(0.25, 0, 0, 0.1), Scale = 0.7, RotationDegrees = 30, PositionX = 100, PositionY = -50 };
        var clip = Video(0, 25, Markup(), visual);
        var layers = Snapshot().LayersAt(F(0)).Select(l => new ResolvedLayer(l, Frame())).ToList();
        var plan = CompositionDrawPlan.Build(new FrameSize(_project.Settings.FrameWidth, _project.Settings.FrameHeight), Affine2D.Identity, layers);
        var frame = plan.Operations.OfType<FrameDraw>().Single();
        var outline = plan.Operations.OfType<OutlineDraw>().Single();

        Assert.Equal(frame.Transform, outline.Transform);     // the same layer transform as its frame
        Assert.Equal(frame.LocalRect, outline.LocalRect);     // clipped to the crop
        // (0.4, 0.4) of the source = (0.4·1920 − 0.25·1920, 0.4·1080) in crop-local pixels.
        var p = outline.Polygons[0][0];
        Assert.Equal(0.4 * 1920 - 0.25 * 1920, p.X, 9);
        Assert.Equal(0.4 * 1080, p.Y, 9);
        Assert.Equal(4.0, outline.StrokeWidth);               // canvas pixels: 4 × 1080 / 1080, the scale not involved
        Assert.Equal(1.0, outline.CanvasScale);
        Assert.Equal(clip.Id, outline.ClipId);
    }

    [Theory]
    [InlineData(1280, 720, 4.0 * 720 / 1080)]
    [InlineData(1920, 1080, 4.0)]
    [InlineData(3840, 2160, 8.0)]
    [InlineData(1080, 1920, 4.0)]
    public void The_stroke_is_per_1080_of_the_canvas_short_side(int width, int height, double expected)
    {
        Assert.Equal(expected, CompositionDrawPlan.StrokeWidthOn(new FrameSize(width, height), 4), 12);
    }

    [Fact]
    public void The_outline_takes_the_layers_opacity_with_fades()
    {
        var clip = Video(0, 50, Markup(), VisualProperties.Default with { Opacity = 0.8 });
        clip.FadeIn = F(10);
        var outline = OutlineAt(F(4), clip.Id)!;
        var layer = Snapshot().LayersAt(F(4)).Single();
        Assert.Equal(layer.Opacity, outline.Opacity);
        Assert.True(outline.Opacity < 0.8);
    }

    [Fact]
    public void In_a_dissolve_each_clip_keeps_its_own_outline_at_its_own_opacity()
    {
        var a = Video(0, 50, Markup());
        var b = Video(50, 100, Markup(6));
        _v1.Transitions.Add(new Transition { TransitionTypeId = TransitionRules.CrossDissolve, Duration = F(10), LeftClipId = a.Id, RightClipId = b.Id });

        var layers = Snapshot().LayersAt(F(48));
        var outlineA = OutlineAt(F(48), a.Id)!;
        var outlineB = OutlineAt(F(48), b.Id)!;
        Assert.Equal(layers.Single(l => l.ClipId == a.Id).Opacity, outlineA.Opacity);
        Assert.Equal(layers.Single(l => l.ClipId == b.Id).Opacity, outlineB.Opacity);
        Assert.True(outlineB.Opacity < 1);
    }

    [Fact]
    public void A_hidden_track_draws_no_outline()
    {
        var clip = Video(0, 25, Markup());
        _v1.IsHidden = true;
        Assert.Null(OutlineAt(F(0), clip.Id));
    }
}
