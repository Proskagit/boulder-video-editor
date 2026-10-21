using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Composition;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Playback;
using Xunit;

namespace AiVideoEditor.Core.Tests;

/// <summary>
/// Phase 28 Step 28.3 (D044 §2–§3, §10): the library's curves on the zone frames (§2's examples as references) and what
/// <see cref="PlaybackSnapshot.LayersAt"/> and <see cref="CompositionDrawPlan"/> make of each type — the clip's own frame
/// for Dip to black / Flash / Zoom (no extended range), both clips shifted for Push (extended, never occluding), the fill
/// above the track's layer and below the tracks above, the zoom about the canvas centre on top of the clip's transform,
/// the outline moving with its frame; a type or direction change presentation-only when the shown range stays; the
/// dissolve and everything outside a zone unchanged.
/// </summary>
public class TransitionCompositionTests
{
    // --- curves ----------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(6, new[] { 0.25, 0.5, 0.75, 1, 0.6667, 0.3333 })]   // Flash 0.2 s at 30 fps
    [InlineData(5, new[] { 0.3333, 0.6667, 1, 0.6667, 0.3333 })]    // at 24 fps
    [InlineData(2, new[] { 0.5, 1 })]
    public void The_fill_rises_over_A_is_full_on_the_cut_frame_and_falls_over_B(long frames, double[] expected)
    {
        var actual = Enumerable.Range(0, (int)frames).Select(j => Math.Round(TransitionPresentation.FillOpacity(j, frames), 4));
        Assert.Equal(expected, actual);
        Assert.Equal(1, Enumerable.Range(0, (int)frames).Count(j => TransitionPresentation.FillOpacity(j, frames) == 1.0));
        Assert.Equal(1.0, TransitionPresentation.FillOpacity(frames / 2, frames));                // exactly B's first frame
    }

    [Fact]
    public void The_zoom_is_one_bump_peaking_at_the_cut_without_a_jump()
    {
        // D044 §2's example: 0.3 s at 30 fps = 9 frames, 4 in A and 5 in B.
        var expected = new[] { 1.004, 1.035, 1.098, 1.191, 1.203, 1.123, 1.063, 1.023, 1.003 };
        var actual = Enumerable.Range(0, 9).Select(j => Math.Round(TransitionPresentation.ZoomScale(j, 9), 3, MidpointRounding.AwayFromZero));
        Assert.Equal(expected, actual);
        Assert.All(Enumerable.Range(0, 9), j => Assert.InRange(TransitionPresentation.ZoomScale(j, 9), 1.0, TransitionPresentation.ZoomPeak));
        Assert.True(Math.Abs(TransitionPresentation.ZoomScale(4, 9) - TransitionPresentation.ZoomScale(3, 9)) < 0.02);   // across the cut
    }

    [Theory]
    [InlineData(TransitionDirection.FromRight, 1, 0)]
    [InlineData(TransitionDirection.FromLeft, -1, 0)]
    [InlineData(TransitionDirection.FromBottom, 0, 1)]
    [InlineData(TransitionDirection.FromTop, 0, -1)]
    public void A_push_shifts_B_in_from_its_side_and_A_out_by_whole_pixels(TransitionDirection direction, int sx, int sy)
    {
        var canvas = new FrameSize(1920, 1080);
        var previous = double.MaxValue;
        for (var j = 0; j < 9; j++)
        {
            var (a, b) = TransitionPresentation.PushMotions(j, 9, direction, canvas);
            var e = TransitionPresentation.PushProgress(j, 9);
            Assert.Equal(Math.Round((1 - e) * (sx != 0 ? 1920 : 1080), MidpointRounding.AwayFromZero), Math.Abs(b.OffsetX + b.OffsetY));
            Assert.Equal((Math.Sign(b.OffsetX), Math.Sign(b.OffsetY)), (sx, sy));
            Assert.Equal((Math.Sign(a.OffsetX), Math.Sign(a.OffsetY)), (-sx, -sy));
            Assert.All(new[] { a.OffsetX, a.OffsetY, b.OffsetX, b.OffsetY }, v => Assert.Equal(Math.Round(v), v));
            Assert.Equal((1.0, 1.0), (a.Scale, b.Scale));
            Assert.True(Math.Abs(b.OffsetX + b.OffsetY) < previous);                                  // B keeps coming in
            previous = Math.Abs(b.OffsetX + b.OffsetY);
        }
    }

    // --- the snapshot ------------------------------------------------------------------------------------------------------

    private static readonly FrameRate Rate = FrameRate.Fps25;
    private static MediaTime F(long n) => MediaTime.FromFrame(n, Rate);

    private readonly Project _project = new() { Settings = { FrameRate = Rate } };   // 1920 × 1080
    private readonly Track _v1 = new() { Type = TrackType.Video, Name = "V1", Order = 0 };
    private readonly Track _v2 = new() { Type = TrackType.Video, Name = "V2", Order = 1 };

    public TransitionCompositionTests() => _project.Timeline.VideoTracks.AddRange(new[] { _v1, _v2 });

    private VideoClip Video(Track track, long start, long end, long sourceIn = 0, double scale = 1)
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
        var clip = new VideoClip { MediaAssetId = asset.Id, TimelineStart = F(start), Duration = F(end) - F(start), SourceIn = F(sourceIn), Scale = scale };
        clip.SourceOut = clip.SourceIn + clip.Duration;
        track.Clips.Add(clip);
        track.Clips.Sort((a, b) => a.TimelineStart.CompareTo(b.TimelineStart));
        return clip;
    }

    private static Transition Add(Track track, Clip a, Clip b, string type, long frames, TransitionDirection? direction = null)
    {
        var t = new Transition
        {
            TransitionTypeId = type, Duration = F(frames), LeftClipId = a.Id, RightClipId = b.Id,
            Direction = direction ?? TransitionRules.DefaultDirection(type)
        };
        track.Transitions.Add(t);
        return t;
    }

    private PlaybackSnapshot Snapshot() => PlaybackSnapshotBuilder.Build(_project, 1);

    [Theory]
    [InlineData(TransitionRules.DipToBlack, false)]
    [InlineData(TransitionRules.Flash, false)]
    [InlineData(TransitionRules.Zoom, false)]
    [InlineData(TransitionRules.Push, true)]
    [InlineData(TransitionRules.CrossDissolve, true)]
    public void Only_dissolve_and_push_extend_the_clips_into_the_zone(string type, bool extends)
    {
        var a = Video(_v1, 0, 50);
        var b = Video(_v1, 50, 100, sourceIn: 60);
        Add(_v1, a, b, type, 10);                                                    // zone [45, 55)

        var layer = Snapshot().VideoLayers.Single(l => l.TrackId == _v1.Id);

        var zone = Assert.Single(layer.Dissolves);
        Assert.Equal((type, F(45), F(55)), (zone.TypeId, zone.Start, zone.End));
        Assert.Equal(extends ? F(55) : F(50), layer.Spans[0].ShownEnd);
        Assert.Equal(extends ? F(45) : F(50), layer.Spans[1].ShownStart);
    }

    [Theory]
    [InlineData(TransitionRules.DipToBlack, "#000000")]
    [InlineData(TransitionRules.Flash, "#FFFFFF")]
    public void A_fill_lies_over_the_clips_own_frame_and_at_the_cut_hides_everything_below(string type, string color)
    {
        var below = Video(_v1, 0, 100, scale: 0.5);                                  // a lower track, partly visible
        var a = Video(_v2, 0, 50, scale: 0.8);
        var b = Video(_v2, 50, 100, scale: 0.8);
        Add(_v2, a, b, type, 10);                                                    // zone [45, 55), cut 50
        var snapshot = Snapshot();

        var atA = snapshot.LayersAt(F(47));                                           // bottom to top
        Assert.Equal(new[] { below.Id, a.Id }, atA.OfType<PictureLayer>().Select(l => l.ClipId));
        var fill = Assert.IsType<SolidLayer>(atA[^1]);
        Assert.Equal((color, TransitionPresentation.FillOpacity(2, 10)), (fill.ColorHex, fill.Opacity));

        var atCut = snapshot.LayersAt(F(50));
        Assert.Equal(1.0, Assert.IsType<SolidLayer>(Assert.Single(atCut)).Opacity);   // fully covered: nothing below drawn

        var atB = snapshot.LayersAt(F(52));
        Assert.Equal(new[] { below.Id, b.Id }, atB.OfType<PictureLayer>().Select(l => l.ClipId));
        Assert.All(atB, l => Assert.True(l.Motion.IsNone));
    }

    [Fact]
    public void A_fill_on_a_lower_track_leaves_the_tracks_above_visible()
    {
        var a = Video(_v1, 0, 50);
        var b = Video(_v1, 50, 100);
        Add(_v1, a, b, TransitionRules.DipToBlack, 10);
        _v2.Clips.Add(new TextClip { Text = "Маршрут 6B", TimelineStart = F(40), Duration = F(20) });

        var atCut = Snapshot().LayersAt(F(50));

        Assert.Equal(2, atCut.Length);
        Assert.IsType<SolidLayer>(atCut[0]);
        Assert.IsType<TextLayer>(atCut[1]);                                           // the title above stays readable
    }

    [Fact]
    public void A_zoom_scales_the_clips_own_frame_about_the_centre_and_keeps_it_occluding()
    {
        var below = Video(_v1, 0, 100);
        var a = Video(_v2, 0, 50);
        var b = Video(_v2, 50, 100);
        Add(_v2, a, b, TransitionRules.Zoom, 9);                                     // zone [46, 55): 4 in A, 5 in B
        var snapshot = Snapshot();

        foreach (var (n, clip) in new[] { (46L, a), (49L, a), (50L, b), (54L, b) })
        {
            var layer = Assert.IsType<PictureLayer>(Assert.Single(snapshot.LayersAt(F(n))));   // V1 hidden: still covering
            Assert.Equal(clip.Id, layer.ClipId);
            Assert.Equal(new CanvasMotion(TransitionPresentation.ZoomScale(n - 46, 9), 0, 0), layer.Motion);
            Assert.True(layer.OccludesBelow);
        }
        Assert.True(Assert.Single(snapshot.LayersAt(F(55))).Motion.IsNone);         // after the zone
        Assert.Equal(below.Id, snapshot.LayersAt(F(40)).Single().ClipId == a.Id ? below.Id : Guid.Empty);   // A covers V1 as before
    }

    [Theory]
    [InlineData(TransitionDirection.FromRight)]
    [InlineData(TransitionDirection.FromLeft)]
    [InlineData(TransitionDirection.FromTop)]
    [InlineData(TransitionDirection.FromBottom)]
    public void A_push_draws_both_clips_shifted_B_above_A_and_uncovers_the_lower_track(TransitionDirection direction)
    {
        var below = Video(_v1, 0, 100);
        var a = Video(_v2, 0, 50);
        var b = Video(_v2, 50, 100, sourceIn: 60);
        Add(_v2, a, b, TransitionRules.Push, 9, direction);                          // zone [46, 55)
        var snapshot = Snapshot();
        var canvas = new FrameSize(1920, 1080);

        foreach (var n in new long[] { 46, 49, 50, 54 })
        {
            var layers = snapshot.LayersAt(F(n));
            Assert.Equal(new[] { below.Id, a.Id, b.Id }, layers.Select(l => l.ClipId));
            var (motionA, motionB) = TransitionPresentation.PushMotions(n - 46, 9, direction, canvas);
            Assert.Equal((motionA, motionB), (layers[1].Motion, layers[2].Motion));
            Assert.False(((PictureLayer)layers[1]).OccludesBelow);
            Assert.False(((PictureLayer)layers[2]).OccludesBelow);
            Assert.Equal((1.0, 1.0), (layers[1].Opacity, layers[2].Opacity));       // no ramp: the shift is the transition
        }
    }

    [Fact]
    public void The_dissolve_is_drawn_exactly_as_before()
    {
        var a = Video(_v1, 0, 50);
        var b = Video(_v1, 50, 100, sourceIn: 60);
        Add(_v1, a, b, TransitionRules.CrossDissolve, 10);

        var layers = Snapshot().LayersAt(F(47));

        Assert.Equal(new[] { a.Id, b.Id }, layers.Select(l => l.ClipId));
        Assert.Equal(FadeRule.Ramp(2, 10), layers[1].Opacity);
        Assert.All(layers, l => Assert.True(l.Motion.IsNone));
    }

    [Theory]
    [InlineData(TransitionRules.Zoom, TransitionRules.DipToBlack, true)]       // the same shown range: presentation only
    [InlineData(TransitionRules.Flash, TransitionRules.DipToBlack, true)]
    [InlineData(TransitionRules.CrossDissolve, TransitionRules.Push, true)]
    [InlineData(TransitionRules.CrossDissolve, TransitionRules.Zoom, false)]   // the handles drop out: decoding changes
    [InlineData(TransitionRules.Push, TransitionRules.Flash, false)]
    public void A_type_change_is_presentation_only_when_the_shown_range_stays(string from, string to, bool presentationOnly)
    {
        var a = Video(_v1, 0, 50);
        var b = Video(_v1, 50, 100, sourceIn: 60);
        var t = Add(_v1, a, b, from, 10);
        var before = Snapshot();
        t.TransitionTypeId = to;
        t.Direction = TransitionRules.DefaultDirection(to);

        Assert.Equal(presentationOnly, before.DiffersOnlyInPresentation(Snapshot()));
    }

    [Fact]
    public void A_direction_change_is_presentation_only()
    {
        var a = Video(_v1, 0, 50);
        var b = Video(_v1, 50, 100, sourceIn: 60);
        var t = Add(_v1, a, b, TransitionRules.Push, 10);
        var before = Snapshot();
        t.Direction = TransitionDirection.FromTop;

        Assert.True(before.DiffersOnlyInPresentation(Snapshot()));
    }

    [Theory]
    [InlineData(TransitionRules.DipToBlack)]
    [InlineData(TransitionRules.Flash)]
    [InlineData(TransitionRules.Zoom)]
    [InlineData(TransitionRules.Push)]
    public void The_fades_of_the_cuts_edges_are_off_for_every_type(string type)
    {
        var a = Video(_v1, 0, 50);
        var b = Video(_v1, 50, 100, sourceIn: 60);
        (a.FadeIn, a.FadeOut, b.FadeIn, b.FadeOut) = (F(5), F(10), F(10), F(5));
        Add(_v1, a, b, type, 10);

        var spans = Snapshot().VideoLayers.Single(l => l.TrackId == _v1.Id).Spans;

        Assert.Equal((5L, 0L), (spans[0].FadeInFrames, spans[0].FadeOutFrames));    // PO-8: A's out, B's in off
        Assert.Equal((0L, 5L), (spans[1].FadeInFrames, spans[1].FadeOutFrames));
    }

    // --- the draw plan -----------------------------------------------------------------------------------------------------

    private static readonly FrameSize Canvas = new(1920, 1080);
    private static DecodedFrame Frame() => new(1920, 1080, 1920 * 4, new byte[1920 * 1080 * 4], new SourceTimestamp(0, new TimeBase(1, 25)));

    [Fact]
    public void The_motion_follows_the_clips_transform_for_the_frame_and_its_outline_and_none_changes_nothing()
    {
        var a = Video(_v1, 0, 50, scale: 0.7);
        a.RotationDegrees = 20;
        a.HoldMarkup = new HoldMarkup
        {
            StrokeWidth1080 = 8, ColorHex = "#FFFFFF",
            Holds = System.Collections.Immutable.ImmutableArray.Create(new Hold(Guid.NewGuid(),
                System.Collections.Immutable.ImmutableArray.Create(new NormalizedPoint(0.2, 0.2), new NormalizedPoint(0.4, 0.2), new NormalizedPoint(0.3, 0.4)),
                default, default, MediaTime.Zero))
        };
        var plain = (PictureLayer)Snapshot().LayersAt(F(10)).Single();
        var motion = new CanvasMotion(1.2, 30, -40);
        var moved = plain with { Motion = motion };
        var viewport = CompositionDrawPlan.Viewport(Canvas, 960, 540);

        var plainOps = CompositionDrawPlan.LayerOperations(Canvas, viewport, new ResolvedLayer(plain, Frame())).ToList();
        var movedOps = CompositionDrawPlan.LayerOperations(Canvas, viewport, new ResolvedLayer(moved, Frame())).ToList();

        Assert.Equal(2, plainOps.Count);                                              // the frame and its outline
        Assert.Equal(viewport.Compose(plain.Geometry!.Transform), plainOps[0].Transform);   // no motion: as before, bit for bit
        var expected = viewport.Compose(motion.ToAffine(Canvas)).Compose(plain.Geometry.Transform);
        AssertClose(expected, movedOps[0].Transform);
        AssertClose(expected, movedOps[1].Transform);                                // the outline moves with its frame
        Assert.Equal(((OutlineDraw)plainOps[1]).StrokeWidth, ((OutlineDraw)movedOps[1]).StrokeWidth);   // its stroke doesn't change
        var centre = motion.ToAffine(Canvas).Apply(new PointD(960, 540));
        Assert.Equal(new PointD(990, 500), centre);                                   // scaled about the centre, then shifted
    }

    [Fact]
    public void A_fill_is_the_whole_canvas_in_its_colour_at_its_opacity()
    {
        var viewport = CompositionDrawPlan.Viewport(Canvas, 960, 540);
        var op = Assert.IsType<SolidDraw>(CompositionDrawPlan.Operation(Canvas, viewport,
            new ResolvedLayer(new SolidLayer(Guid.NewGuid(), "#FFFFFF", 0.75), null)));

        Assert.Equal((new RectD(0, 0, 1920, 1080), "#FFFFFF", 0.75, viewport), (op.LocalRect, op.ColorHex, op.Opacity, op.Transform));
    }

    private static void AssertClose(Affine2D expected, Affine2D actual)
    {
        foreach (var (e, a) in new[] { (expected.A, actual.A), (expected.B, actual.B), (expected.C, actual.C), (expected.D, actual.D),
                     (expected.Tx, actual.Tx), (expected.Ty, actual.Ty) })
            Assert.Equal(e, a, 9);
    }
}
