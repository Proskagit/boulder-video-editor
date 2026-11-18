using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Composition;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Playback;
using Xunit;

namespace AiVideoEditor.Core.Tests;

/// <summary>
/// D069 §8: the eight new types. Вытеснение / Круг / Шторки draw B over A from their handles, B revealed through a shape that
/// grows to the whole canvas; Смахивание is a push with a motion blur; Размытие / Отдаление / Вращение / Глитч show each clip on
/// its own frames with an effect peaking at the cut; the draw plan carries the reveal (in target pixels) and the effect; the
/// pixel effects; a turn of 0 keeps the motion's transform exactly as before.
/// </summary>
public class TransitionLibrary2Tests
{
    private static readonly FrameRate Rate = FrameRate.Fps25;
    private static readonly FrameSize Canvas = new(1920, 1080);
    private static MediaTime F(long n) => MediaTime.FromFrame(n, Rate);

    private readonly Project _project = new() { Settings = { FrameRate = Rate } };
    private readonly Track _v1 = new() { Type = TrackType.Video, Name = "V1", Order = 0 };

    public TransitionLibrary2Tests() => _project.Timeline.VideoTracks.Add(_v1);

    private VideoClip Video(long start, long end, long sourceIn = 0)
    {
        var asset = new MediaAsset
        {
            FilePath = $@"C:\media\{Guid.NewGuid():N}.mp4", Kind = MediaKind.Video,
            Metadata = new MediaMetadata
            {
                Duration = MediaTime.FromSeconds(60), FrameRate = Rate, Width = 1920, Height = 1080, DisplayWidth = 1920, DisplayHeight = 1080
            }
        };
        _project.MediaAssets.Add(asset);
        var clip = new VideoClip { MediaAssetId = asset.Id, TimelineStart = F(start), Duration = F(end) - F(start), SourceIn = F(sourceIn) };
        clip.SourceOut = clip.SourceIn + clip.Duration;
        _v1.Clips.Add(clip);
        return clip;
    }

    private (VideoClip A, VideoClip B, PlaybackSnapshot Snapshot) Zone(string type, long frames = 10, TransitionDirection? direction = null)
    {
        var a = Video(0, 50);
        var b = Video(50, 100, sourceIn: 60);
        _v1.Transitions.Add(new Transition
        {
            TransitionTypeId = type, Duration = F(frames), LeftClipId = a.Id, RightClipId = b.Id,
            Direction = direction ?? TransitionRules.DefaultDirection(type)
        });
        return (a, b, PlaybackSnapshotBuilder.Build(_project, 1));
    }

    [Theory]
    [InlineData(TransitionRules.Wipe, true, true)]
    [InlineData(TransitionRules.Circle, true, false)]
    [InlineData(TransitionRules.Doors, true, false)]
    [InlineData(TransitionRules.Whip, true, true)]
    [InlineData(TransitionRules.Blur, false, false)]
    [InlineData(TransitionRules.ZoomOut, false, false)]
    [InlineData(TransitionRules.Spin, false, false)]
    [InlineData(TransitionRules.Glitch, false, false)]
    public void Each_type_has_its_rules(string type, bool handles, bool direction)
    {
        Assert.Contains(type, TransitionRules.VideoTypes);
        Assert.Equal(handles, TransitionRules.RequiresHandles(type));
        Assert.Equal(direction, TransitionRules.HasDirection(type));
        Assert.False(TransitionRules.IsFill(type));
        var limits = TransitionRules.LimitsOf(type);
        Assert.True(limits.Min <= limits.Default && (limits.Max is null || limits.Default <= limits.Max));
        Assert.NotEqual(Localization.Messages.TransitionName_CrossDissolve, Localization.TransitionTypeNames.Of(type));
    }

    [Theory]
    [InlineData(TransitionRules.Wipe)]
    [InlineData(TransitionRules.Circle)]
    [InlineData(TransitionRules.Doors)]
    public void A_reveal_draws_B_over_A_through_a_growing_shape(string type)
    {
        var (a, b, snapshot) = Zone(type);                                           // zone [45, 55)
        double previous = -1;
        foreach (var n in new long[] { 45, 48, 50, 54 })
        {
            var layers = snapshot.LayersAt(F(n));
            Assert.Equal(new[] { a.Id, b.Id }, layers.Select(l => l.ClipId));
            Assert.Null(layers[0].Reveal);
            var reveal = layers[1].Reveal!.Value;
            Assert.False(((PictureLayer)layers[1]).OccludesBelow);
            var area = reveal.Bounds.Width * reveal.Bounds.Height;
            Assert.True(area > previous);
            previous = area;
        }
        var last = TransitionPresentation.Reveal(type, 9, 10, TransitionDirection.FromRight, Canvas).Bounds;
        Assert.True(last.Width >= Canvas.Width * 0.95);                              // nearly all of it on the last frame
    }

    [Theory]
    [InlineData(TransitionDirection.FromRight, 1920, 0)]
    [InlineData(TransitionDirection.FromLeft, 0, 0)]
    [InlineData(TransitionDirection.FromBottom, 0, 1080)]
    [InlineData(TransitionDirection.FromTop, 0, 0)]
    public void A_wipe_comes_from_its_side(TransitionDirection direction, double edgeX, double edgeY)
    {
        var early = TransitionPresentation.Reveal(TransitionRules.Wipe, 1, 10, direction, Canvas).Bounds;
        Assert.Equal(edgeX, direction == TransitionDirection.FromRight ? early.X + early.Width : early.X, 6);
        Assert.Equal(edgeY, direction == TransitionDirection.FromBottom ? early.Y + early.Height : early.Y, 6);
    }

    [Fact]
    public void A_whip_shifts_both_clips_with_a_motion_blur_along_its_direction()
    {
        var (a, b, snapshot) = Zone(TransitionRules.Whip, 9, TransitionDirection.FromTop);
        var layers = snapshot.LayersAt(F(50));
        Assert.Equal(new[] { a.Id, b.Id }, layers.Select(l => l.ClipId));
        Assert.All(layers, l => Assert.Equal(BlurAxis.Vertical, l.Effect.Axis));
        Assert.All(layers, l => Assert.True(l.Effect.Blur > 0));
        Assert.True(layers[1].Motion.OffsetY < 0 && layers[0].Motion.OffsetY > 0);
    }

    [Theory]
    [InlineData(TransitionRules.Blur)]
    [InlineData(TransitionRules.ZoomOut)]
    [InlineData(TransitionRules.Spin)]
    [InlineData(TransitionRules.Glitch)]
    public void An_own_frame_type_shows_one_clip_with_its_effect_peaking_at_the_cut(string type)
    {
        var (a, b, snapshot) = Zone(type);
        Assert.Equal(a.Id, Assert.Single(snapshot.LayersAt(F(46))).ClipId);
        Assert.Equal(b.Id, Assert.Single(snapshot.LayersAt(F(52))).ClipId);
        double Strength(long n)
        {
            var l = snapshot.LayersAt(F(n)).Single();
            return type switch
            {
                TransitionRules.Blur => l.Effect.Blur,
                TransitionRules.ZoomOut => 1 - l.Motion.Scale,
                TransitionRules.Spin => Math.Abs(l.Motion.RotationDegrees),
                _ => l.Effect.Glitch
            };
        }
        Assert.True(Strength(49) > Strength(46));
        Assert.True(Strength(50) > Strength(53));
        Assert.True(Assert.Single(snapshot.LayersAt(F(56))).Effect.IsNone);           // after the zone
    }

    [Fact]
    public void A_spin_stays_covered_and_a_turn_of_zero_keeps_the_old_transform()
    {
        var motion = TransitionPresentation.SpinMotion(4, 9);
        Assert.False(motion.KeepsCoverage);
        Assert.True(motion.Scale > 1);
        var plain = new CanvasMotion(1.2, 10, -4);
        Assert.Equal(new Affine2D(1.2, 0, 0, 1.2, 960 - 1.2 * 960 + 10, 540 - 1.2 * 540 - 4), plain.ToAffine(Canvas));
        var turned = new CanvasMotion(1, 0, 0, 90).ToAffine(Canvas);
        var centre = turned.Apply(new PointD(960, 540));
        Assert.Equal((960.0, 540.0), (Math.Round(centre.X, 6), Math.Round(centre.Y, 6)));   // about the centre
    }

    [Fact]
    public void The_draw_plan_carries_the_reveal_in_target_pixels_and_the_effect_on_the_picture()
    {
        var reveal = new RevealShape(true, new RectD(100, 50, 200, 200));
        var span = new PictureSpan(Guid.NewGuid(), Guid.NewGuid(), SpanStatus.Video, F(0), F(10), F(0)) { SourceSize = Canvas };
        var layer = new PictureLayer(Guid.NewGuid(), span, CompositionMath.Layout(Canvas, Canvas, VisualProperties.Default))
        {
            Reveal = reveal, Effect = new LayerEffect(0.02, BlurAxis.Both, 0, 0)
        };
        var frame = new DecodedFrame(1920, 1080, 1920 * 4, new byte[1920 * 1080 * 4], new SourceTimestamp(0, new TimeBase(1, 25)));
        var half = new Affine2D(0.5, 0, 0, 0.5, 10, 20);

        var draw = Assert.IsType<FrameDraw>(Assert.Single(CompositionDrawPlan.Build(Canvas, half, new[] { new ResolvedLayer(layer, frame) }).Operations));

        Assert.Equal(new RevealShape(true, new RectD(60, 45, 100, 100)), draw.Reveal);
        Assert.Equal(0.02, draw.Effect.Blur);
    }

    [Fact]
    public void A_blur_averages_and_keeps_a_flat_picture_and_the_alpha()
    {
        var px = new byte[16 * 4 * 4];
        for (var i = 0; i < 16 * 4; i++) (px[i * 4], px[i * 4 + 1], px[i * 4 + 2], px[i * 4 + 3]) = ((byte)(i % 16 < 8 ? 0 : 200), 80, 80, 77);
        TransitionEffects.Blur(px, 16, 4, 64, 2, BlurAxis.Horizontal);
        Assert.Equal(0, px[0]);                                                      // far from the edge: unchanged
        Assert.InRange(px[7 * 4], 1, 199);                                           // the edge is softened
        Assert.Equal(80, px[5 * 4 + 1]);                                             // a flat channel stays
        Assert.Equal(77, px[7 * 4 + 3]);
    }

    [Fact]
    public void A_glitch_splits_the_channels_and_looks_the_same_for_the_same_seed()
    {
        static byte[] Picture()
        {
            var px = new byte[64 * 8 * 4];
            for (var i = 0; i < 64 * 8; i++) (px[i * 4], px[i * 4 + 1], px[i * 4 + 2], px[i * 4 + 3]) = ((byte)(i % 64 * 4), 100, (byte)(i % 64 * 4), 255);
            return px;
        }
        var one = Picture();
        var two = Picture();
        TransitionEffects.Glitch(one, 64, 8, 256, 1, 7);
        TransitionEffects.Glitch(two, 64, 8, 256, 1, 7);
        Assert.Equal(one, two);
        Assert.NotEqual(Picture(), one);
        Assert.NotEqual(one[32 * 4], one[32 * 4 + 2]);                              // red and blue taken from different places
    }
}
