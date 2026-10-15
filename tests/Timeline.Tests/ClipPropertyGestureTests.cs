using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Timeline.Commands;
using Xunit;

namespace AiVideoEditor.Timeline.Tests;

/// <summary>
/// D038 §4: the property changes of one Preview gesture are one undo step whatever they change; two gestures are two
/// steps; a gesture never merges with an Inspector edit (no gesture) or the reverse; a gesture that returns to its start
/// leaves no step and nothing to redo; D017's merging without a gesture is unchanged.
/// </summary>
public class ClipPropertyGestureTests
{
    private static TimelineFixture WithVideo(out VideoClip clip)
    {
        var f = new TimelineFixture();
        Assert.True(f.Service.AddClip(f.Video(10, FrameRate.Ntsc30).Id).Success);
        clip = (VideoClip)f.V1.Clips[0];
        return f;
    }

    private static VisualProperties Visual(Clip clip) => VisualProperties.Of(clip)!.Value;

    private static TimelineEditResult Set(TimelineFixture f, Clip clip, VisualProperties visual, ClipPropertyGesture? gesture) =>
        f.Service.SetClipProperties(clip.Id, new ClipPropertyChange { Visual = visual, Gesture = gesture });

    private static int Steps(TimelineFixture f)
    {
        var n = 0;
        while (f.UndoRedo.CanUndo) { f.UndoRedo.Undo(); n++; }
        for (var i = 0; i < n; i++) f.UndoRedo.Redo();
        return n;
    }

    [Fact]
    public void One_gesture_is_one_step_whatever_it_changes()
    {
        var f = WithVideo(out var clip);
        var start = Visual(clip);
        var baseSteps = Steps(f);
        var move = new ClipPropertyGesture("Crop Clip");

        Assert.True(Set(f, clip, start with { PositionX = 10 }, move).Success);                     // X only
        Assert.True(Set(f, clip, start with { PositionX = 10, PositionY = 5 }, move).Success);      // then Y
        Assert.True(Set(f, clip, start with { PositionX = 12, PositionY = 5, Scale = 0.5, Crop = new CropRect(0.1, 0, 0, 0) }, move).Success);

        Assert.Equal(baseSteps + 1, Steps(f));
        Assert.Equal("Crop Clip", f.UndoRedo.NextUndo!.Description);
        f.UndoRedo.Undo();
        Assert.Equal(start, Visual(clip));
        f.UndoRedo.Redo();
        Assert.Equal(start with { PositionX = 12, PositionY = 5, Scale = 0.5, Crop = new CropRect(0.1, 0, 0, 0) }, Visual(clip));
    }

    [Fact]
    public void Two_gestures_of_the_same_properties_are_two_steps()
    {
        var f = WithVideo(out var clip);
        var start = Visual(clip);
        var baseSteps = Steps(f);

        Set(f, clip, start with { PositionX = 10 }, new ClipPropertyGesture("Move Clip"));
        Set(f, clip, start with { PositionX = 20 }, new ClipPropertyGesture("Move Clip"));

        Assert.Equal(baseSteps + 2, Steps(f));
        f.UndoRedo.Undo();
        Assert.Equal(10, Visual(clip).PositionX);
        f.UndoRedo.Undo();
        Assert.Equal(start, Visual(clip));
    }

    [Fact]
    public void A_gesture_never_merges_with_an_edit_without_one_and_the_reverse()
    {
        var f = WithVideo(out var clip);
        var start = Visual(clip);
        var baseSteps = Steps(f);

        Set(f, clip, start with { PositionX = 10 }, null);                                   // the Inspector's Position X
        Set(f, clip, start with { PositionX = 20 }, new ClipPropertyGesture("Move Clip"));   // a horizontal drag
        Set(f, clip, start with { PositionX = 30 }, null);                                   // the Inspector again

        Assert.Equal(baseSteps + 3, Steps(f));
    }

    [Fact]
    public void Edits_without_a_gesture_merge_exactly_as_before()
    {
        var f = WithVideo(out var clip);
        var start = Visual(clip);
        var baseSteps = Steps(f);

        Set(f, clip, start with { PositionX = 10 }, null);
        Set(f, clip, start with { PositionX = 11 }, null);                  // same field: merged (D017)
        Set(f, clip, start with { PositionX = 11, PositionY = 3 }, null);   // another field: a new step

        Assert.Equal(baseSteps + 2, Steps(f));
        Assert.Equal("Change Position", f.UndoRedo.NextUndo!.Description);
    }

    [Fact]
    public void A_gesture_back_at_its_start_leaves_no_step_and_nothing_to_redo()
    {
        var f = WithVideo(out var clip);
        var start = Visual(clip);
        var baseSteps = Steps(f);
        var gesture = new ClipPropertyGesture("Scale Clip");

        Set(f, clip, start with { Scale = 0.5 }, gesture);
        Set(f, clip, start with { Scale = 0.25, PositionX = 3 }, gesture);
        Set(f, clip, start, gesture);   // Esc

        Assert.Equal(start, Visual(clip));
        Assert.Equal(baseSteps, Steps(f));
        Assert.False(f.UndoRedo.CanRedo);
    }

    [Fact]
    public void A_cancelled_gesture_returns_to_the_save_point()
    {
        var f = WithVideo(out var clip);
        var start = Visual(clip);
        f.UndoRedo.MarkSavePoint(f.UndoRedo.CurrentPosition);
        Assert.True(f.UndoRedo.IsAtSavePoint);
        var gesture = new ClipPropertyGesture("Move Clip");

        Set(f, clip, start with { PositionX = 40 }, gesture);
        Assert.False(f.UndoRedo.IsAtSavePoint);
        Set(f, clip, start, gesture);

        Assert.True(f.UndoRedo.IsAtSavePoint);
    }

    [Fact]
    public void A_gesture_after_an_undo_starts_a_new_step()
    {
        var f = WithVideo(out var clip);
        var start = Visual(clip);
        var first = new ClipPropertyGesture("Move Clip");
        Set(f, clip, start with { PositionX = 10 }, first);
        f.UndoRedo.Undo();
        Assert.True(f.UndoRedo.CanRedo);

        Set(f, clip, start with { PositionX = 20 }, new ClipPropertyGesture("Move Clip"));
        Assert.False(f.UndoRedo.CanRedo);
        f.UndoRedo.Undo();
        Assert.Equal(start, Visual(clip));
    }

    [Fact]
    public void A_gesture_on_a_locked_track_is_refused_without_a_step()
    {
        var f = WithVideo(out var clip);
        f.V1.IsLocked = true;
        var start = Visual(clip);
        var baseSteps = Steps(f);

        var result = Set(f, clip, start with { PositionX = 10 }, new ClipPropertyGesture("Move Clip"));

        Assert.False(result.Success);
        Assert.Equal(start, Visual(clip));
        Assert.Equal(baseSteps, Steps(f));
    }

    [Fact]
    public void Changes_of_one_gesture_on_two_clips_do_not_merge()
    {
        var f = WithVideo(out var a);
        Assert.True(f.Service.AddClip(f.Video(10, FrameRate.Ntsc30, "b.mp4").Id).Success);
        var b = f.V1.Clips.Single(c => c != a);
        var baseSteps = Steps(f);
        var gesture = new ClipPropertyGesture("Move Clip");

        Set(f, a, Visual(a) with { PositionX = 10 }, gesture);
        Set(f, b, Visual(b) with { PositionX = 10 }, gesture);

        Assert.Equal(baseSteps + 2, Steps(f));
    }

    [Fact]
    public void The_command_unites_the_fields_of_a_gesture()
    {
        var f = WithVideo(out var clip);
        var gesture = new ClipPropertyGesture("Crop Clip");
        var start = Visual(clip);
        Set(f, clip, start with { PositionX = 1 }, gesture);
        Set(f, clip, start with { PositionX = 1, Crop = new CropRect(0, 0.2, 0, 0) }, gesture);

        var top = (NotifyingCommand)f.UndoRedo.NextUndo!;
        var command = Assert.IsType<SetClipPropertiesCommand>(top.Inner);
        Assert.Equal(ClipPropertyFields.PositionX | ClipPropertyFields.CropTop, command.Fields);
        Assert.Same(gesture, command.Gesture);
    }
}
