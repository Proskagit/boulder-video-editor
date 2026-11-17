using System.Collections.Immutable;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Composition;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Core.Playback;
using Xunit;

namespace AiVideoEditor.Timeline.Tests;

/// <summary>
/// D063 §6: the transform keyframes through the edit service — one undo step each, refused when not valid or not a video
/// clip, kept in source time through Trim, Split (each part with the same curve: no change at the cut), a move and Speed,
/// copied by Duplicate, scaled with the canvas, written by Fill on the playhead's keyframe, a part's keyframes kept with its
/// Follow; presentation only.
/// </summary>
public class TransformKeyframeEditTests
{
    private static readonly FrameRate Rate = FrameRate.Fps30;

    private static MediaTime S(double seconds) => MediaTime.FromSeconds(seconds);

    private static TransformKeyframes Keys(params (double Seconds, double X, double Scale)[] keys) =>
        new(keys.Select(k => new TransformKeyframe(Guid.NewGuid(), S(k.Seconds), k.X, 0, k.Scale)).ToImmutableArray());

    private static (TimelineFixture F, VideoClip Clip) Setup(double seconds = 8)
    {
        var f = new TimelineFixture();
        var asset = f.AddAsset("climb.mp4", MediaKind.Video, new MediaMetadata
        {
            Duration = S(seconds), FrameRate = Rate, Width = 1920, Height = 1080, DisplayWidth = 1920, DisplayHeight = 1080
        });
        Assert.True(f.Service.AddClip(asset.Id).Success);
        return (f, f.V1.Clips.OfType<VideoClip>().Single());
    }

    private static VisualProperties ShownAt(TimelineFixture f, MediaTime time) =>
        PlaybackSnapshotBuilder.Build(f.Project, 1).LayersAt(time).OfType<PictureLayer>().Single().Visual;

    [Fact]
    public void Keyframes_are_set_in_one_undo_step_and_are_presentation_only()
    {
        var (f, clip) = Setup();
        var before = PlaybackSnapshotBuilder.Build(f.Project, 1);
        var keys = Keys((0, 0, 1), (4, 400, 2));
        Assert.True(f.Service.SetClipProperties(clip.Id, new ClipPropertyChange { Keyframes = new KeyframeValues(keys) }).Success);
        Assert.Equal(keys, clip.Keyframes);
        Assert.True(before.DiffersOnlyInPresentation(PlaybackSnapshotBuilder.Build(f.Project, 2)));
        Assert.Equal(200, ShownAt(f, S(2)).PositionX, 9);
        Assert.Equal(1.5, ShownAt(f, S(2)).Scale, 9);

        f.UndoRedo.Undo();
        Assert.Null(clip.Keyframes);
        Assert.Equal(0, ShownAt(f, S(2)).PositionX);
        f.UndoRedo.Redo();
        Assert.Equal(keys, clip.Keyframes);
    }

    [Fact]
    public void Invalid_keyframes_and_keyframes_of_another_kind_are_refused_without_a_step()
    {
        var (f, clip) = Setup();
        var position = f.UndoRedo.CurrentPosition;
        var key = new TransformKeyframe(Guid.NewGuid(), S(1), 0, 0, 1);
        var twoAtOnce = new TransformKeyframes(ImmutableArray.Create(key, key with { Id = Guid.NewGuid() }));
        Assert.False(f.Service.SetClipProperties(clip.Id, new ClipPropertyChange { Keyframes = new KeyframeValues(twoAtOnce) }).Success);
        var tooSmall = new TransformKeyframes(ImmutableArray.Create(key with { Scale = 0.001 }));
        Assert.False(f.Service.SetClipProperties(clip.Id, new ClipPropertyChange { Keyframes = new KeyframeValues(tooSmall) }).Success);
        Assert.Same(position, f.UndoRedo.CurrentPosition);                         // no step

        var image = f.PlaceImageFrames(f.V1, f.Image(), 400, 500, Rate);
        Assert.False(f.Service.SetClipProperties(image.Id, new ClipPropertyChange { Keyframes = new KeyframeValues(Keys((0, 0, 1))) }).Success);

        var badPart = FollowSettings.Of(new FollowPart(Keyframes: tooSmall));
        Assert.False(f.Service.SetClipFollow(clip.Id, badPart).Success);
        Assert.Null(clip.Follow);
    }

    [Fact]
    public void A_split_gives_both_parts_the_same_curve_so_the_picture_does_not_change_at_the_cut()
    {
        var (f, clip) = Setup();
        f.Service.SetClipProperties(clip.Id, new ClipPropertyChange { Keyframes = new KeyframeValues(Keys((1, 0, 1), (7, 600, 3))) });
        var shown = Enumerable.Range(0, 240).Select(i => ShownAt(f, MediaTime.FromFrame(i, Rate))).ToList();

        Assert.True(f.Service.Split(S(4), new[] { clip.Id }).Success);
        var parts = f.V1.Clips.OfType<VideoClip>().OrderBy(c => c.TimelineStart).ToList();
        Assert.Equal(2, parts.Count);
        Assert.Equal(parts[0].Keyframes, parts[1].Keyframes);
        for (var i = 0; i < 240; i++) Assert.Equal(shown[i], ShownAt(f, MediaTime.FromFrame(i, Rate)));

        f.UndoRedo.Undo();
        Assert.Single(f.V1.Clips);
        Assert.Equal(2, clip.Keyframes!.Count);
    }

    [Fact]
    public void Trim_a_move_and_a_speed_change_keep_every_keyframe_on_its_frame_of_the_video()
    {
        var (f, clip) = Setup();
        var keys = Keys((1, 0, 1), (5, 400, 1));
        f.Service.SetClipProperties(clip.Id, new ClipPropertyChange { Keyframes = new KeyframeValues(keys) });

        Assert.True(f.Service.TrimClip(clip.Id, ClipEdge.Start, S(2)).Success);    // the first keyframe now lies before the clip
        Assert.Equal(keys, clip.Keyframes);                                         // kept (an un-trim brings it back)
        Assert.Equal(100, ShownAt(f, S(2)).PositionX, 9);                           // source 2 s: a quarter of the way

        Assert.True(f.Service.MoveClips(new[] { clip.Id }, 90).Success);            // three seconds later on the timeline
        Assert.Equal(keys, clip.Keyframes);
        Assert.Equal(100, ShownAt(f, S(5)).PositionX, 9);                           // the same source frame, the same value

        Assert.True(f.Service.SetClipSpeed(clip.Id, ClipSpeed.FromSteps(40)).Success);   // 2×
        Assert.Equal(keys, clip.Keyframes);
        Assert.Equal(300, ShownAt(f, S(5) + S(1)).PositionX, 9);                    // one timeline second = two source seconds (4 s)
    }

    [Fact]
    public void Duplicate_copies_the_keyframes_and_the_copies_stay_independent()
    {
        var (f, clip) = Setup(4);
        var keys = Keys((0, 0, 1), (3, 300, 1));
        f.Service.SetClipProperties(clip.Id, new ClipPropertyChange { Keyframes = new KeyframeValues(keys) });
        Assert.True(f.Service.DuplicateClips(new[] { clip.Id }).Success);
        var copy = f.Project.Timeline.VideoTracks.SelectMany(t => t.Clips).OfType<VideoClip>().Single(c => c.Id != clip.Id);
        Assert.Equal(keys, copy.Keyframes);
        f.Service.SetClipProperties(copy.Id, new ClipPropertyChange { Keyframes = new KeyframeValues(Keys((0, -50, 1))) });
        Assert.Equal(keys, clip.Keyframes);
    }

    [Fact]
    public void A_canvas_change_scales_the_keyframes_positions_with_the_static_ones()
    {
        var (f, clip) = Setup();
        f.Service.SetClipProperties(clip.Id, new ClipPropertyChange { Keyframes = new KeyframeValues(Keys((0, 100, 1.5), (2, -40, 2))) });
        Assert.True(f.Service.SetCanvasSize(3840, 2160).Success);
        Assert.Equal(new[] { 200.0, -80.0 }, clip.Keyframes!.Keys.Select(k => k.X));
        Assert.Equal(new[] { 1.5, 2.0 }, clip.Keyframes!.Keys.Select(k => k.Scale));
        f.UndoRedo.Undo();
        Assert.Equal(new[] { 100.0, -40.0 }, clip.Keyframes!.Keys.Select(k => k.X));
    }

    [Fact]
    public void Fill_on_a_keyframed_clip_writes_the_keyframe_at_the_given_time()
    {
        var (f, clip) = Setup();
        f.Service.SetClipProperties(clip.Id, new ClipPropertyChange { Keyframes = new KeyframeValues(Keys((0, 0, 0.5), (4, 0, 0.5))) });
        Assert.True(f.Service.FrameClip(clip.Id, ClipFraming.Fill, S(4)).Success);
        Assert.Equal(new[] { 0.5, 1.0 }, clip.Keyframes!.Keys.Select(k => k.Scale));
        Assert.Equal(1, clip.Scale);                                                // the static scale is not what the frame shows: untouched
        Assert.True(f.Service.FrameClip(clip.Id, ClipFraming.Fill, S(2)).Success);  // between keyframes: one is added there
        Assert.Equal(3, clip.Keyframes!.Count);
    }

    [Fact]
    public void A_followed_parts_keyframes_go_with_its_Follow_through_a_split_and_undo()
    {
        var (f, clip) = Setup();
        var partKeys = Keys((3, 20, 2.5));
        var follow = FollowSettings.Of(new FollowPart(S(2), S(6), OffsetX: 5, Keyframes: partKeys));
        Assert.True(f.Service.SetClipFollow(clip.Id, follow).Success);
        Assert.Equal(partKeys, clip.Follow!.Parts[0].Keyframes);
        Assert.Null(clip.Keyframes);                                               // the two systems are apart

        Assert.True(f.Service.Split(S(4), new[] { clip.Id }).Success);
        Assert.All(f.V1.Clips.OfType<VideoClip>(), c => Assert.Equal(partKeys, c.Follow!.Parts[0].Keyframes));
        f.UndoRedo.Undo();
        f.UndoRedo.Undo();
        Assert.Null(clip.Follow);
    }
}
