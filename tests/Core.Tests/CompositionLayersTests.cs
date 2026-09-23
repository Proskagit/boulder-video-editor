using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Composition;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Playback;
using Xunit;

namespace AiVideoEditor.Core.Tests;

/// <summary><see cref="PlaybackSnapshot.LayersAt"/> (D018): visible layers bottom to top, culling
/// below a provably opaque full-canvas video, text layers, built from a real project.</summary>
public class CompositionLayersTests
{
    private static readonly FrameRate Rate = FrameRate.Fps25;

    private readonly Project _project = new() { Settings = { FrameRate = Rate } }; // canvas 1920 × 1080
    private readonly Track _v1 = new() { Type = TrackType.Video, Name = "V1", Order = 0 };
    private readonly Track _v2 = new() { Type = TrackType.Video, Name = "V2", Order = 1 };
    private readonly Track _v3 = new() { Type = TrackType.Video, Name = "V3", Order = 2 };

    public CompositionLayersTests()
    {
        _project.Timeline.VideoTracks.AddRange(new[] { _v1, _v2, _v3 });
    }

    private static MediaTime F(long frame) => MediaTime.FromFrame(frame, Rate);

    private MediaAsset Asset(MediaKind kind, int width = 1920, int height = 1080, bool withSize = true)
    {
        var asset = new MediaAsset
        {
            FilePath = $@"C:\media\{Guid.NewGuid():N}",
            Kind = kind,
            Metadata = new MediaMetadata
            {
                Duration = MediaTime.FromSeconds(60), FrameRate = Rate,
                Width = withSize ? width : null, Height = withSize ? height : null
            }
        };
        _project.MediaAssets.Add(asset);
        return asset;
    }

    private T Add<T>(Track track, T clip, long start = 0, long end = 100) where T : Clip
    {
        clip.TimelineStart = F(start);
        clip.Duration = F(end) - F(start);
        if (clip is MediaBackedClip media) media.SourceOut = clip.Duration;
        track.Clips.Add(clip);
        track.Clips.Sort((a, b) => a.TimelineStart.CompareTo(b.TimelineStart));
        return clip;
    }

    private VideoClip Video(Track track, VisualProperties? visual = null, int width = 1920, int height = 1080, long start = 0, long end = 100)
    {
        var clip = Add(track, new VideoClip { MediaAssetId = Asset(MediaKind.Video, width, height).Id }, start, end);
        if (visual is { } v)
            (clip.PositionX, clip.PositionY, clip.Scale, clip.RotationDegrees, clip.Opacity, clip.Crop) =
                (v.PositionX, v.PositionY, v.Scale, v.RotationDegrees, v.Opacity, v.Crop);
        return clip;
    }

    private IReadOnlyList<CompositionLayer> Layers(long frame = 10) => PlaybackSnapshotBuilder.Build(_project, 1).LayersAt(F(frame));

    private static Guid[] Ids(IEnumerable<CompositionLayer> layers) => layers.Select(l => l.ClipId).ToArray();

    [Fact]
    public void Layers_come_bottom_to_top_by_track_order()
    {
        var bottom = Video(_v1, VisualProperties.Default with { Scale = 0.5 });
        var middle = Video(_v2, VisualProperties.Default with { Scale = 0.5, PositionX = 100 });
        var top = Video(_v3, VisualProperties.Default with { Scale = 0.5, PositionX = 200 });

        var layers = Layers();

        Assert.Equal(new[] { bottom.Id, middle.Id, top.Id }, Ids(layers));
        Assert.Equal(new[] { _v1.Id, _v2.Id, _v3.Id }, layers.Select(l => l.TrackId));
        var geometry = Assert.IsType<PictureLayer>(layers[2]).Geometry!;
        Assert.Equal(new Affine2D(0.5, 0, 0, 0.5, 680, 270), geometry.Transform);
    }

    [Fact]
    public void Track_order_not_list_position_decides_stacking()
    {
        _v1.Order = 5; // V1 is now the topmost track
        var a = Video(_v1, VisualProperties.Default with { Scale = 0.5 });
        var b = Video(_v2, VisualProperties.Default with { Scale = 0.5 });

        Assert.Equal(new[] { b.Id, a.Id }, Ids(Layers()));
    }

    [Fact]
    public void Only_clips_covering_the_time_are_layers()
    {
        var early = Video(_v1, VisualProperties.Default with { Scale = 0.5 }, start: 0, end: 10);
        var late = Video(_v2, VisualProperties.Default with { Scale = 0.5 }, start: 10, end: 20);

        Assert.Equal(new[] { early.Id }, Ids(Layers(9)));
        Assert.Equal(new[] { late.Id }, Ids(Layers(10)));  // half-open spans
        Assert.Empty(Layers(20));
    }

    [Fact]
    public void Opaque_fullscreen_video_hides_everything_below()
    {
        Video(_v1);
        Add(_v2, new TextClip { Text = "hidden below" });
        var top = Video(_v3);

        var layer = Assert.Single(Layers());
        Assert.Equal(top.Id, layer.ClipId);
        Assert.True(((PictureLayer)layer).OccludesBelow);
    }

    [Fact]
    public void Culling_stops_at_the_first_occluder_but_keeps_what_is_above_it()
    {
        Video(_v1, VisualProperties.Default with { Scale = 0.5 });
        var occluder = Video(_v2, VisualProperties.Default with { Scale = 1.5, RotationDegrees = 180 });
        var above = Video(_v3, VisualProperties.Default with { Scale = 0.25 });

        Assert.Equal(new[] { occluder.Id, above.Id }, Ids(Layers()));
    }

    public static TheoryData<string, VisualProperties> NotCoveringOrNotOpaque => new()
    {
        { "half transparent", VisualProperties.Default with { Opacity = 0.5 } },
        { "almost opaque", VisualProperties.Default with { Opacity = 0.999 } },
        { "smaller", VisualProperties.Default with { Scale = 0.99 } },
        { "cropped", VisualProperties.Default with { Crop = new CropRect(0, 0, 0.01, 0) } },
        { "moved", VisualProperties.Default with { PositionX = 0.5 } },
        { "rotated", VisualProperties.Default with { RotationDegrees = 10 } },
        { "turned 90°", VisualProperties.Default with { RotationDegrees = 90 } },
    };

    [Theory]
    [MemberData(nameof(NotCoveringOrNotOpaque))]
    public void Layers_that_are_not_provably_opaque_and_covering_keep_those_below(string description, VisualProperties visual)
    {
        var below = Video(_v1);
        var top = Video(_v2, visual);

        Assert.True(new[] { below.Id, top.Id }.SequenceEqual(Ids(Layers())), description);
        Assert.False(((PictureLayer)Layers()[1]).OccludesBelow, description);
    }

    [Fact]
    public void Transformed_layers_that_provably_cover_do_cull()
    {
        Video(_v1);
        var zoomedAndCropped = Video(_v2, new VisualProperties(30, -20, 1.2, 0, 1, new CropRect(0.05, 0.05, 0.05, 0.05)));
        Assert.Equal(new[] { zoomedAndCropped.Id }, Ids(Layers()));

        _v2.Clips.Clear();
        var rotated = Video(_v2, VisualProperties.Default with { RotationDegrees = 45, Scale = 3 });
        Assert.Equal(new[] { rotated.Id }, Ids(Layers()));
    }

    [Fact]
    public void Rotated_video_with_a_covering_bounding_box_but_uncovered_corners_keeps_the_layer_below()
    {
        var below = Video(_v1);
        var rotated = Video(_v2, VisualProperties.Default with { RotationDegrees = 45 });

        var layers = Layers();
        var top = Assert.IsType<PictureLayer>(layers[1]);
        var bounds = top.Geometry!.Bounds;
        Assert.True(bounds.X <= 0 && bounds.Y <= 0 && bounds.Right >= 1920 && bounds.Bottom >= 1080); // AABB alone would cull
        Assert.False(top.OccludesBelow);
        Assert.Equal(new[] { below.Id, rotated.Id }, Ids(layers));
    }

    [Fact]
    public void Rotated_video_containing_every_canvas_corner_culls_the_layer_below()
    {
        Video(_v1);
        var rotated = Video(_v2, VisualProperties.Default with { RotationDegrees = 30, Scale = 2 });

        Assert.Equal(new[] { rotated.Id }, Ids(Layers()));
    }

    [Fact]
    public void Images_offline_video_and_unknown_sizes_never_cull()
    {
        var below = Video(_v1);
        var image = Add(_v2, new ImageClip { MediaAssetId = Asset(MediaKind.Image).Id }); // may have alpha
        Assert.Equal(new[] { below.Id, image.Id }, Ids(Layers()));

        _v2.Clips.Clear();
        var offline = Video(_v2);
        _project.MediaAssets.Single(a => a.Id == offline.MediaAssetId).IsMissing = true;
        var layers = Layers();
        Assert.Equal(new[] { below.Id, offline.Id }, Ids(layers));
        Assert.Equal(SpanStatus.Offline, ((PictureLayer)layers[1]).Span.Status);

        _v2.Clips.Clear();
        var unknown = Add(_v2, new VideoClip { MediaAssetId = Asset(MediaKind.Video, withSize: false).Id });
        layers = Layers();
        Assert.Equal(new[] { below.Id, unknown.Id }, Ids(layers));
        Assert.Null(((PictureLayer)layers[1]).Geometry); // the renderer lays it out from the decoded size
    }

    [Fact]
    public void Invisible_clips_are_not_layers()
    {
        Video(_v1, VisualProperties.Default with { Opacity = 0 });
        Add(_v2, new TextClip { Text = "   \r\n " });
        var hidden = Video(_v3);
        _v3.IsHidden = true;

        Assert.Empty(Layers());
        Assert.DoesNotContain(hidden.Id, Ids(Layers()));
    }

    [Fact]
    public void Fully_transparent_top_layer_does_not_hide_the_ones_below()
    {
        var below = Video(_v1);
        Video(_v2, VisualProperties.Default with { Opacity = 0 });

        Assert.Equal(new[] { below.Id }, Ids(Layers()));
    }

    [Fact]
    public void Text_clips_are_renderer_neutral_layers_and_never_cull()
    {
        var below = Video(_v1);
        var text = Add(_v2, new TextClip
        {
            Text = "Line 1\nLine 2", FontFamily = "Arial", FontSize = 64, ColorHex = "#FF8800", Alignment = TextAlignment.Left,
            PositionX = -100, PositionY = 300, Scale = 1.5, RotationDegrees = 90, Opacity = 0.75
        });

        var layers = Layers();

        Assert.Equal(new[] { below.Id, text.Id }, Ids(layers));
        var layer = Assert.IsType<TextLayer>(layers[1]);
        Assert.Equal(new TextProperties("Line 1\nLine 2", "Arial", 64, "#FF8800", TextAlignment.Left), layer.Text);
        Assert.Equal(0.75, layer.Opacity);
        Assert.Equal(new Affine2D(0, -1.5, 1.5, 0, 860, 840), layer.Transform);
    }

    [Fact]
    public void Text_and_media_on_one_track_take_turns()
    {
        var clip = Video(_v1, VisualProperties.Default with { Scale = 0.5 }, start: 0, end: 10);
        var text = Add(_v1, new TextClip { Text = "after" }, start: 10, end: 20);

        Assert.Equal(new[] { clip.Id }, Ids(Layers(5)));
        Assert.Equal(new[] { text.Id }, Ids(Layers(15)));
    }

    [Fact]
    public void Picture_layers_carry_the_span_and_the_geometry_for_the_project_canvas()
    {
        _project.Settings.FrameWidth = 1080;
        _project.Settings.FrameHeight = 1920; // vertical project
        var clip = Video(_v1, VisualProperties.Default with { Opacity = 0.5 });

        var layer = Assert.IsType<PictureLayer>(Assert.Single(Layers()));

        Assert.Equal(clip.Id, layer.Span.ClipId);
        Assert.Equal(new FrameSize(1920, 1080), layer.Span.SourceSize);
        Assert.Equal(new FrameSize(1080, 1920), layer.Geometry!.Canvas);
        Assert.Equal(new Affine2D(0.5625, 0, 0, 0.5625, 0, 656.25), layer.Geometry.Transform);
        Assert.Equal(0.5, layer.Opacity);
    }

    [Fact]
    public void Picture_at_is_unchanged_by_composition_data()
    {
        // Phase 5 decoding still asks for the topmost visible media clip; text stays out of it.
        var below = Video(_v1);
        Add(_v2, new TextClip { Text = "on top" });

        var snapshot = PlaybackSnapshotBuilder.Build(_project, 1);
        Assert.Equal(below.Id, snapshot.PictureAt(F(10))!.ClipId);
    }
}
