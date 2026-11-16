using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Project;
using AiVideoEditor.Timeline;
using AiVideoEditor.UI.Common;
using AiVideoEditor.UI.Services;
using AiVideoEditor.UI.ViewModels.Panels;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiVideoEditor.UI.Tests;

/// <summary>
/// D061 amendment 1, the followed part of a clip, with the real edit service on a 20 s, 25 fps clip: «Следить отсюда» at
/// the playhead switches Follow (and with it Occlusion) on from that frame, one undo step, and plays; «Остановить слежение
/// здесь» ends the part at the frame then shown and pauses; the part's strip on the timeline follows it and its edges drag
/// (one undo step per release; dragged to the clip's edge, the side is open again); a split keeps the part on the same
/// source frames.
/// </summary>
public sealed class FollowRangeUiTests
{
    private static readonly FrameRate Rate = FrameRate.Fps25;

    private readonly UndoRedoService _undo = new();
    private readonly ProjectService _projects;
    private readonly TimelineEditService _edit;
    private readonly StatusService _status = new();

    public FollowRangeUiTests()
    {
        _projects = new ProjectService(_undo, NullLogger<ProjectService>.Instance);
        _edit = new TimelineEditService(_projects, _undo, NullLogger<TimelineEditService>.Instance);
    }

    private (MediaAsset Asset, VideoClip Clip) AddClip()
    {
        var asset = new MediaAsset
        {
            FilePath = Path.Combine(Path.GetTempPath(), "follow-range", Guid.NewGuid().ToString("N"), "climb.mp4"), Kind = MediaKind.Video,
            Metadata = new MediaMetadata { Duration = MediaTime.FromSeconds(20), FrameRate = Rate, Width = 1920, Height = 1080 },
            AnalysisStatus = MediaAnalysisStatus.Completed
        };
        _projects.Current.MediaAssets.Add(asset);
        Assert.True(_edit.AddClip(asset.Id).Success);
        return (asset, (VideoClip)_projects.Current.Timeline.VideoTracks[0].Clips[0]);
    }

    private static MediaTime S(double seconds) => MediaTime.FromSeconds(seconds);

    [Fact]
    public void Follow_from_here_then_stop_here_sets_the_part_and_plays_then_pauses()
    {
        var (asset, clip) = AddClip();
        var inspector = new InspectorViewModel(_edit, _status);
        var playhead = S(4);
        inspector.Playhead = () => playhead;
        var playback = new List<bool>();
        inspector.FollowPlaybackRequested += (_, play) => playback.Add(play);
        inspector.ShowClip(new TimelineClipSelection(clip, "climb", asset, Rate));

        inspector.FollowFromHereCommand.Execute(null);
        Assert.Equal(new FollowSettings(From: S(4)), clip.Follow);
        Assert.True(clip.HidesOutlinesBehindPeople);                       // Follow switched Occlusion on
        Assert.True(inspector.IsRecordingFollow);
        Assert.Equal(new[] { true }, playback);

        playhead = S(9);                                                   // it played on
        inspector.StopFollowHereCommand.Execute(null);
        Assert.Equal(new FollowSettings(From: S(4), To: S(9)), clip.Follow);
        Assert.False(inspector.IsRecordingFollow);
        Assert.Equal(new[] { true, false }, playback);

        _undo.Undo();                                                      // the stop
        Assert.Equal(new FollowSettings(From: S(4)), clip.Follow);
        _undo.Undo();                                                      // the start: Follow and Occlusion together
        Assert.Null(clip.Follow);
        Assert.False(clip.HidesOutlinesBehindPeople);
    }

    [Fact]
    public void Stop_here_before_half_a_second_keeps_the_part_open_and_the_playhead_off_the_clip_refuses_the_start()
    {
        var (asset, clip) = AddClip();
        var inspector = new InspectorViewModel(_edit, _status);
        var playhead = S(4);
        inspector.Playhead = () => playhead;
        inspector.ShowClip(new TimelineClipSelection(clip, "climb", asset, Rate));
        inspector.FollowFromHereCommand.Execute(null);
        playhead = S(4.2);
        inspector.StopFollowHereCommand.Execute(null);
        Assert.Equal(new FollowSettings(From: S(4)), clip.Follow);

        _undo.Undo();
        playhead = S(25);                                                   // past the clip
        inspector.FollowFromHereCommand.Execute(null);
        Assert.Null(clip.Follow);
    }

    [Fact]
    public void The_parts_strip_follows_the_model_and_its_edges_drag_in_one_undo_step_each()
    {
        var (_, clip) = AddClip();
        var timeline = new TimelineViewModel(_projects, _edit, _status, NullLogger<TimelineViewModel>.Instance) { SnappingEnabled = false };
        var vm = timeline.Tracks.SelectMany(t => t.Clips).Single(v => v.Id == clip.Id);
        Assert.False(vm.HasFollowStrip);
        double X(double seconds) => TimelineCoordinateMapper.TimeToX(S(seconds), timeline.PixelsPerSecond);

        Assert.True(_edit.SetClipFollow(clip.Id, new FollowSettings(From: S(4), To: S(12))).Success);
        Assert.True(vm.HasFollowStrip);
        Assert.Equal(X(4), vm.FollowLeft, 6);
        Assert.Equal(X(8), vm.FollowWidth, 6);

        // The start edge two seconds later.
        Assert.True(timeline.BeginFollowDrag(vm, X(4) + 1, 1000));
        timeline.UpdateFollowDrag(1000 + X(2));
        Assert.NotNull(vm.StripText);
        Assert.Equal(new FollowSettings(From: S(4), To: S(12)), clip.Follow);   // nothing applied while dragging
        timeline.EndFollowDrag();
        Assert.Null(vm.StripText);
        Assert.Equal(new FollowSettings(From: S(6), To: S(12)), clip.Follow);
        Assert.Equal(X(6), vm.FollowLeft, 6);

        // The end edge past the clip's end: open to the end.
        Assert.True(timeline.BeginFollowDrag(vm, X(12), 1000));
        timeline.UpdateFollowDrag(1000 + X(30));
        timeline.EndFollowDrag();
        Assert.Equal(new FollowSettings(From: S(6)), clip.Follow);

        _undo.Undo();
        Assert.Equal(new FollowSettings(From: S(6), To: S(12)), clip.Follow);
        _undo.Undo();
        Assert.Equal(new FollowSettings(From: S(4), To: S(12)), clip.Follow);

        // The middle of the strip is the clip's (no edge); Esc cancels with nothing changed.
        Assert.False(timeline.BeginFollowDrag(vm, X(8), 1000));
        Assert.True(timeline.BeginFollowDrag(vm, X(12), 1000));
        timeline.UpdateFollowDrag(1000 - X(3));
        timeline.CancelFollowDrag();
        Assert.Equal(new FollowSettings(From: S(4), To: S(12)), clip.Follow);
        Assert.Equal(X(8), vm.FollowWidth, 6);
    }

    [Fact]
    public void Esc_the_export_lock_and_a_click_without_a_move_edit_nothing()
    {
        var (_, clip) = AddClip();
        var editingLock = new EditingLock();
        var timeline = new TimelineViewModel(_projects, _edit, _status, NullLogger<TimelineViewModel>.Instance, editingLock: editingLock) { SnappingEnabled = false };
        var vm = timeline.Tracks.SelectMany(t => t.Clips).Single(v => v.Id == clip.Id);
        double X(double seconds) => TimelineCoordinateMapper.TimeToX(S(seconds), timeline.PixelsPerSecond);
        var part = new FollowSettings(From: S(4), To: S(12));
        Assert.True(_edit.SetClipFollow(clip.Id, part).Success);
        timeline.OnClipClicked(vm, false);
        var steps = 0;
        _undo.StateChanged += (_, _) => steps++;

        // Esc (the timeline's Escape command, the key's route): the drag ends, nothing changes, the selection stays.
        Assert.True(timeline.BeginFollowDrag(vm, X(4), 1000));
        timeline.UpdateFollowDrag(1000 + X(2));
        timeline.EscapeCommand.Execute(null);
        Assert.False(timeline.IsFollowDragging);
        timeline.EndFollowDrag();
        Assert.Equal(part, clip.Follow);
        Assert.True(vm.IsSelected);
        Assert.Equal(X(4), vm.FollowLeft, 6);

        // A click on an edge without a move: no edit, no undo step.
        Assert.True(timeline.BeginFollowDrag(vm, X(12), 1000));
        timeline.EndFollowDrag();
        Assert.Equal(0, steps);

        // The export lock: a drag under way is cancelled; none starts while locked.
        Assert.True(timeline.BeginFollowDrag(vm, X(12), 1000));
        timeline.UpdateFollowDrag(1000 - X(3));
        using (editingLock.Acquire())
        {
            Assert.False(timeline.IsFollowDragging);
            timeline.EndFollowDrag();
            Assert.False(timeline.BeginFollowDrag(vm, X(12), 1000));
        }
        Assert.Equal(part, clip.Follow);
        Assert.Equal(0, steps);
    }

    [Fact]
    public void A_split_keeps_the_part_on_the_same_source_frames()
    {
        var (_, clip) = AddClip();
        var timeline = new TimelineViewModel(_projects, _edit, _status, NullLogger<TimelineViewModel>.Instance) { SnappingEnabled = false };
        Assert.True(_edit.SetClipFollow(clip.Id, new FollowSettings(From: S(4), To: S(12))).Success);
        Assert.True(_edit.Split(S(8), new[] { clip.Id }).Success);
        Assert.True(_edit.Split(S(15), _projects.Current.Timeline.VideoTracks[0].Clips.Where(c => c.TimelineStart == S(8)).Select(c => c.Id).ToList()).Success);
        double X(double seconds) => TimelineCoordinateMapper.TimeToX(S(seconds), timeline.PixelsPerSecond);
        var vms = timeline.Tracks.SelectMany(t => t.Clips).OrderBy(v => v.Clip.TimelineStart).ToList();
        Assert.Equal(3, vms.Count);
        Assert.True(vms[0].HasFollowStrip);
        Assert.Equal(X(4), vms[0].FollowLeft, 6);
        Assert.Equal(X(4), vms[0].FollowWidth, 6);                          // [4, 8)
        Assert.True(vms[1].HasFollowStrip);
        Assert.Equal(0, vms[1].FollowLeft, 6);
        Assert.Equal(X(4), vms[1].FollowWidth, 6);                          // [8, 12)
        Assert.False(vms[2].HasFollowStrip);                                // [15, 20): none of the part
    }
}
