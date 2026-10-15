using System.Collections.Immutable;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Composition;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Playback;
using AiVideoEditor.UI.Rendering;
using Xunit;

namespace AiVideoEditor.Rendering.Tests;

/// <summary>
/// Phase 21 (D036 §4): the animated outline in pixels. The Preview (its control at the canvas size) and the export draw
/// the same frame from the same <see cref="PlaybackSnapshot.LayersAt"/> — equal pixels in the middle of an appear fade and
/// of the disappear fade; a hidden contour leaves the picture untouched; a fading hold's keyline and core fade as one
/// group (the keyline never shows through the core) and its overlapping segments never darken.
/// </summary>
[Collection(AvaloniaCollection.Name)]
public sealed class HoldAnimationRenderTests
{
    private const byte Grey = 64;
    private static readonly FrameRate Rate = FrameRate.Fps25;
    private static readonly FrameSize Canvas = new(640, 360);
    private static readonly FrameSize Source = new(160, 90);

    private static ImmutableArray<NormalizedPoint> Box(double x0, double y0, double x1, double y1) => ImmutableArray.Create(
        new NormalizedPoint(x0, y0), new NormalizedPoint(x1, y0), new NormalizedPoint(x1, y1), new NormalizedPoint(x0, y1));

    /// <summary>A clip of 400 frames with two holds — the lower one (left box) first, then the upper (right box) at
    /// frame 13; 0.25 s fades = 6 frames, disappear at 13 + 6 + 75 = 94 over 13 frames.</summary>
    private static PlaybackSnapshot Snapshot(bool keyline = false)
    {
        var project = new Project { Settings = { FrameRate = Rate, FrameWidth = Canvas.Width, FrameHeight = Canvas.Height } };
        var track = new Track { Type = TrackType.Video, Name = "V1" };
        project.Timeline.VideoTracks.Add(track);
        var asset = new MediaAsset
        {
            FilePath = "/media/a.mp4", Kind = MediaKind.Video,
            Metadata = new MediaMetadata
            {
                Duration = MediaTime.FromSeconds(60), FrameRate = Rate, Width = Source.Width, Height = Source.Height,
                DisplayRotation = 0, DisplayWidth = Source.Width, DisplayHeight = Source.Height
            }
        };
        project.MediaAssets.Add(asset);
        var markup = new HoldMarkup
        {
            StrokeWidth1080 = 24, ColorHex = "#FFFFFF", Keyline = keyline ? KeylineMode.On : KeylineMode.Off, KeylineActive = keyline,
            Holds = ImmutableArray.Create(
                new Hold(Guid.NewGuid(), Box(0.15, 0.3, 0.4, 0.9), default, default, MediaTime.Zero),
                new Hold(Guid.NewGuid(), Box(0.6, 0.1, 0.85, 0.7), default, default, MediaTime.Zero)),
            Animation = new HoldAnimation()
        };
        var clip = new VideoClip { MediaAssetId = asset.Id, TimelineStart = MediaTime.Zero, Duration = MediaTime.FromFrame(400, Rate), HoldMarkup = markup };
        clip.SourceOut = clip.Duration;
        track.Clips.Add(clip);
        return PlaybackSnapshotBuilder.Build(project, 1);
    }

    private static readonly DecodedFrame Wall = Scene.Solid(Source.Width, Source.Height, Grey, Grey, Grey);

    private static ImmutableArray<ResolvedLayer> At(PlaybackSnapshot snapshot, long frame) =>
        snapshot.LayersAt(MediaTime.FromFrame(frame, Rate)).Select(l => new ResolvedLayer(l, Wall)).ToImmutableArray();

    private static Image Export(PlaybackSnapshot snapshot, long frame) => Render.Export(Scene.Plan(Canvas, At(snapshot, frame).ToArray()));

    private static Image Preview(PlaybackSnapshot snapshot, long frame) =>
        Render.Preview(Canvas, Canvas.Width, Canvas.Height, At(snapshot, frame).Select(Scene.AsPreview));

    /// <summary>The left edge of the lower hold (x = 0.15 · 640 = 96) and of the upper one (0.6 · 640 = 384), mid-height.</summary>
    private static byte LowerEdge(Image image) => image.At(96, 216).G;
    private static byte UpperEdge(Image image) => image.At(384, 144).G;

    [Theory]
    [InlineData(0L)]   // the lower hold appearing, 1/7
    [InlineData(2L)]   // the lower hold fading in, 3/7
    [InlineData(15L)]  // the upper one fading in, 3/7; the lower at 1
    [InlineData(50L)]  // both shown
    [InlineData(100L)] // fading out together, 7/14
    public void The_preview_and_the_export_draw_the_same_pixels_on_every_animation_frame(long frame)
    {
        var snapshot = Snapshot();
        var export = Export(snapshot, frame);
        var preview = Preview(snapshot, frame);
        var maxDiff = 0;
        for (var i = 0; i < export.Pixels.Length; i++) maxDiff = Math.Max(maxDiff, Math.Abs(export.Pixels[i] - preview.Pixels[i]));
        Assert.True(maxDiff <= 1, $"max channel difference {maxDiff}");
    }

    [Fact]
    public void Each_contour_is_drawn_at_its_own_opacity_and_a_hidden_one_not_at_all()
    {
        var snapshot = Snapshot();
        static double Share(byte g) => (g - Grey) / (255.0 - Grey);

        var appearing = Export(snapshot, 2);                                  // the lower one at 3/7, the upper hidden
        Assert.InRange(Share(LowerEdge(appearing)), 3 / 7.0 - 0.02, 3 / 7.0 + 0.02);
        Assert.Equal(Grey, UpperEdge(appearing));

        var both = Export(snapshot, 15);
        Assert.InRange(Share(LowerEdge(both)), 0.98, 1.0);
        Assert.InRange(Share(UpperEdge(both)), 3 / 7.0 - 0.02, 3 / 7.0 + 0.02);

        var fading = Export(snapshot, 100);                                   // j = 6: 7/14 for both
        Assert.InRange(Share(LowerEdge(fading)), 0.5 - 0.02, 0.5 + 0.02);
        Assert.InRange(Share(UpperEdge(fading)), 0.5 - 0.02, 0.5 + 0.02);

        var gone = Export(snapshot, 107);
        Assert.Equal(Grey, LowerEdge(gone));
        Assert.Equal(Grey, UpperEdge(gone));
    }

    [Fact]
    public void A_fading_holds_keyline_never_shows_through_its_core_and_its_corners_do_not_darken()
    {
        var snapshot = Snapshot(keyline: true);                               // white core over a black keyline, 8 px wide
        var image = Export(snapshot, 100);                                    // both at 1/2

        // The core's middle: white at 1/2 over the wall — as if the keyline were not there (grouped: 159.5); a keyline
        // fading separately under a separately fading core would give 0.5·255 + 0.5·32 = 143.5.
        Assert.InRange(image.At(96, 216).G, 157, 162);
        // The keyline's outer quarter: black at 1/2 over the wall.
        Assert.InRange(image.At(96 - 3, 216).G, 30, 34);

        // A corner, where the round caps of two segments overlap, is no brighter than the middle of an edge.
        var corner = image.At(96, (int)(0.3 * 360));
        Assert.InRange(corner.G, image.At(96, 216).G - 2, image.At(96, 216).G + 2);
    }
}
