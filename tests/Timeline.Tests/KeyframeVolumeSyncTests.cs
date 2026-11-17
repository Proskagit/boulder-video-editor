using AiVideoEditor.Core.People;
using AiVideoEditor.Core.Audio;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Composition;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using Xunit;

namespace AiVideoEditor.Timeline.Tests;

/// <summary>
/// D064 amendment 1: on a video clip with its own sound the transform keyframes and the volume points are one set — a keyframe
/// added, moved or removed adds, moves or removes its point (and the other way round), with the same id and time, without changing
/// what is heard or seen; the last one removed leaves its values static; Undo restores both; a clip without sound, a followed part
/// and an audio clip are not tied.
/// </summary>
public class KeyframeVolumeSyncTests
{
    private static void Ok(TimelineEditResult r) => Assert.True(r.Success, r.Message);
    private static MediaTime S(double s) => MediaTime.FromSeconds(s);

    private static (TimelineFixture F, VideoClip Clip) WithSound()
    {
        var f = new TimelineFixture();
        Ok(f.Service.AddClip(f.VideoWithSound(4, FrameRate.Fps25).Id));
        return (f, (VideoClip)f.V1.Clips[0]);
    }

    private static TransformKeyframes Keys(params (double Seconds, double X)[] keys) =>
        TransformKeyframes.Of(keys.Select(k => new TransformKeyframe(Guid.NewGuid(), S(k.Seconds), k.X, 0, 1)))!;

    [Fact]
    public void Adding_keyframes_adds_points_with_the_curves_gain_and_the_same_ids()
    {
        var (f, clip) = WithSound();
        var keys = Keys((1, 0), (3, 100));
        Ok(f.Service.SetClipProperties(clip.Id, new ClipPropertyChange { Keyframes = new KeyframeValues(keys) }));

        Assert.Equal(keys.Keys.Select(k => (k.Id, k.Time)), clip.Volumes!.Points.Select(p => (p.Id, p.Time)));
        Assert.All(clip.Volumes.Points, p => Assert.Equal(1, p.Gain));
        f.UndoRedo.Undo();
        Assert.Null(clip.Keyframes);
        Assert.Null(clip.Volumes);
    }

    [Fact]
    public void Moving_a_keyframe_moves_its_point_and_moving_a_point_moves_its_keyframe()
    {
        var (f, clip) = WithSound();
        Ok(f.Service.SetClipProperties(clip.Id, new ClipPropertyChange { Keyframes = new KeyframeValues(Keys((1, 0), (3, 100))) }));
        var first = clip.Keyframes!.Keys[0];
        Ok(f.Service.SetClipProperties(clip.Id, new ClipPropertyChange { Keyframes = new KeyframeValues(clip.Keyframes.With(first with { Time = S(2) })) }));
        Assert.Equal(S(2), clip.Volumes!.Find(first.Id)!.Time);

        var point = clip.Volumes.Find(first.Id)!;
        Ok(f.Service.SetClipProperties(clip.Id, new ClipPropertyChange
        {
            Audio = AudioProperties.Of(clip)!.Value with { Volumes = clip.Volumes.With(point with { Time = S(0.5), Gain = 0.3 }) }
        }));
        Assert.Equal(S(0.5), clip.Keyframes!.Find(first.Id)!.Time);
        Assert.Equal(0.3, clip.Volumes!.Find(first.Id)!.Gain);
        Assert.Equal(0, clip.Keyframes.Find(first.Id)!.X);                    // the picture's values stay
    }

    [Fact]
    public void A_point_added_on_the_line_adds_a_keyframe_with_the_pictures_values_there()
    {
        var (f, clip) = WithSound();
        Ok(f.Service.SetClipProperties(clip.Id, new ClipPropertyChange { Keyframes = new KeyframeValues(Keys((0, 0), (4, 100))) }));
        var added = new VolumePoint(Guid.NewGuid(), S(2), 0.5);
        Ok(f.Service.SetClipProperties(clip.Id, new ClipPropertyChange
        {
            Audio = AudioProperties.Of(clip)!.Value with { Volumes = clip.Volumes!.With(added) }
        }));
        var key = clip.Keyframes!.Find(added.Id)!;
        Assert.Equal(S(2), key.Time);
        Assert.Equal(50, key.X, 9);                                              // half way: the picture does not move
    }

    [Fact]
    public void Removing_a_point_removes_its_keyframe_and_the_last_leaves_its_values_static()
    {
        var (f, clip) = WithSound();
        Ok(f.Service.SetClipProperties(clip.Id, new ClipPropertyChange { Keyframes = new KeyframeValues(Keys((1, 40))) }));
        var only = clip.Volumes!.Points[0];
        Ok(f.Service.SetClipProperties(clip.Id, new ClipPropertyChange
        {
            Audio = AudioProperties.Of(clip)!.Value with { Volumes = clip.Volumes.With(only with { Gain = 0.5 }) }
        }));
        Ok(f.Service.SetClipProperties(clip.Id, new ClipPropertyChange { Audio = AudioProperties.Of(clip)!.Value with { Volumes = null } }));
        Assert.Null(clip.Keyframes);
        Assert.Equal(40, clip.PositionX);                                        // the picture keeps the last keyframe's place

        f.UndoRedo.Undo();
        Assert.NotNull(clip.Keyframes);
        // The last keyframe removed: its point's gain goes into the clip's volume.
        Ok(f.Service.SetClipProperties(clip.Id, new ClipPropertyChange { Keyframes = new KeyframeValues(null) }));
        Assert.Null(clip.Volumes);
        Assert.Equal(0.5, clip.Volume, 9);
    }

    [Fact]
    public void A_video_clip_without_sound_and_an_audio_clip_are_not_tied()
    {
        var f = new TimelineFixture();
        Ok(f.Service.AddClip(f.Video(4, FrameRate.Fps25).Id));
        var silent = (VideoClip)f.V1.Clips[0];
        Ok(f.Service.SetClipProperties(silent.Id, new ClipPropertyChange { Keyframes = new KeyframeValues(Keys((1, 0))) }));
        Assert.Null(silent.Volumes);

        Ok(f.Service.AddClip(f.Audio(4).Id));
        var music = (AudioClip)f.A1.Clips[0];
        Ok(f.Service.SetClipProperties(music.Id, new ClipPropertyChange
        {
            Audio = AudioProperties.Of(music)!.Value with { Volumes = VolumeEnvelope.Of(new[] { new VolumePoint(Guid.NewGuid(), S(1), 0.5) }) }
        }));
        Assert.Single(music.Volumes!.Points);
    }

    // --- the owner's second check: the keyframes of a followed part ---------------------------------------------------

    private static (TimelineFixture F, VideoClip Clip) Followed()
    {
        var (f, clip) = WithSound();
        Ok(f.Service.SetClipFollow(clip.Id, FollowSettings.Of(new FollowPart(S(1), S(3)))));
        return (f, clip);
    }

    [Fact]
    public void A_followed_parts_keyframe_brings_its_point_and_moving_it_moves_the_point()
    {
        var (f, clip) = Followed();
        var key = new TransformKeyframe(Guid.NewGuid(), S(2), 10, 0, 1.5);
        Ok(f.Service.SetClipFollow(clip.Id, clip.Follow!.WithPart(0, clip.Follow.Parts[0] with { Keyframes = TransformKeyframes.Of(new[] { key }) })));
        var point = Assert.Single(clip.Volumes!.Points);
        Assert.Equal((key.Id, key.Time, 1.0), (point.Id, point.Time, point.Gain));

        var gesture = new ClipPropertyGesture("Move Keyframe");
        foreach (var t in new[] { 2.2, 2.4, 2.6 })
            Ok(f.Service.SetClipFollow(clip.Id, clip.Follow!.WithPart(0, clip.Follow.Parts[0] with
            {
                Keyframes = TransformKeyframes.Of(new[] { key with { Time = S(t) } })
            }), gesture: gesture));
        Assert.Equal(S(2.6), clip.Volumes!.Points[0].Time);
        f.UndoRedo.Undo();                                                       // the drag is one step
        Assert.Equal(S(2), clip.Volumes!.Points[0].Time);
        Assert.Equal(S(2), clip.Follow!.Parts[0].Keyframes!.Keys[0].Time);
    }

    [Fact]
    public void A_point_added_inside_a_followed_part_becomes_that_parts_keyframe()
    {
        var (f, clip) = Followed();
        var inside = new VolumePoint(Guid.NewGuid(), S(2), 0.5);
        var outside = new VolumePoint(Guid.NewGuid(), S(3.5), 0.7);
        Ok(f.Service.SetClipProperties(clip.Id, new ClipPropertyChange
        {
            Audio = AudioProperties.Of(clip)!.Value with { Volumes = VolumeEnvelope.Of(new[] { inside, outside }) }
        }));
        Assert.Equal(inside.Id, Assert.Single(clip.Follow!.Parts[0].Keyframes!.Keys).Id);
        Assert.Equal(outside.Id, Assert.Single(clip.Keyframes!.Keys).Id);

        // Removing the point removes the part's keyframe; the part keeps its framing static.
        Ok(f.Service.SetClipProperties(clip.Id, new ClipPropertyChange
        {
            Audio = AudioProperties.Of(clip)!.Value with { Volumes = clip.Volumes!.Without(inside.Id) }
        }));
        Assert.Null(clip.Follow!.Parts[0].Keyframes);
        f.UndoRedo.Undo();
        Assert.NotNull(clip.Follow!.Parts[0].Keyframes);
        Assert.Equal(2, clip.Volumes!.Count);
    }

    [Fact]
    public void Switching_Follow_off_keeps_the_sound()
    {
        var (f, clip) = Followed();
        var key = new TransformKeyframe(Guid.NewGuid(), S(2), 10, 0, 1.5);
        Ok(f.Service.SetClipFollow(clip.Id, clip.Follow!.WithPart(0, clip.Follow.Parts[0] with { Keyframes = TransformKeyframes.Of(new[] { key }) })));
        Ok(f.Service.SetClipFollow(clip.Id, null));
        Assert.Single(clip.Volumes!.Points);                                     // the sound does not change
    }
}