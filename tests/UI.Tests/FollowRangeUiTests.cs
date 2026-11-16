using System.Collections.Immutable;
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
        Assert.Equal(FollowSettings.Of(new FollowPart(S(4))), clip.Follow);
        Assert.True(clip.HidesOutlinesBehindPeople);                       // Follow switched Occlusion on
        Assert.True(inspector.IsRecordingFollow);
        Assert.Equal(new[] { true }, playback);

        playhead = S(9);                                                   // it played on
        inspector.StopFollowHereCommand.Execute(null);
        Assert.Equal(FollowSettings.Of(new FollowPart(S(4), S(9))), clip.Follow);
        Assert.False(inspector.IsRecordingFollow);
        Assert.Equal(new[] { true, false }, playback);

        _undo.Undo();                                                      // the stop
        Assert.Equal(FollowSettings.Of(new FollowPart(S(4))), clip.Follow);
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
        Assert.Equal(FollowSettings.Of(new FollowPart(S(4))), clip.Follow);

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

        Assert.True(_edit.SetClipFollow(clip.Id, FollowSettings.Of(new FollowPart(S(4), S(12)))).Success);
        Assert.True(vm.HasFollowStrip);
        Assert.Equal(X(4), vm.FollowSegments[0].Left, 6);
        Assert.Equal(X(8), vm.FollowSegments[0].Width, 6);

        // The start edge two seconds later.
        Assert.True(timeline.BeginFollowDrag(vm, X(4) + 1, 1000));
        timeline.UpdateFollowDrag(1000 + X(2));
        Assert.NotNull(vm.StripText);
        Assert.Equal(FollowSettings.Of(new FollowPart(S(4), S(12))), clip.Follow);   // nothing applied while dragging
        timeline.EndFollowDrag();
        Assert.Null(vm.StripText);
        Assert.Equal(FollowSettings.Of(new FollowPart(S(6), S(12))), clip.Follow);
        Assert.Equal(X(6), vm.FollowSegments[0].Left, 6);

        // The end edge past the clip's end: open to the end.
        Assert.True(timeline.BeginFollowDrag(vm, X(12), 1000));
        timeline.UpdateFollowDrag(1000 + X(30));
        timeline.EndFollowDrag();
        Assert.Equal(FollowSettings.Of(new FollowPart(S(6))), clip.Follow);

        _undo.Undo();
        Assert.Equal(FollowSettings.Of(new FollowPart(S(6), S(12))), clip.Follow);
        _undo.Undo();
        Assert.Equal(FollowSettings.Of(new FollowPart(S(4), S(12))), clip.Follow);

        // The middle of the strip is the clip's (no edge); Esc cancels with nothing changed.
        Assert.False(timeline.BeginFollowDrag(vm, X(8), 1000));
        Assert.True(timeline.BeginFollowDrag(vm, X(12), 1000));
        timeline.UpdateFollowDrag(1000 - X(3));
        timeline.CancelFollowDrag();
        Assert.Equal(FollowSettings.Of(new FollowPart(S(4), S(12))), clip.Follow);
        Assert.Equal(X(8), vm.FollowSegments[0].Width, 6);
    }

    [Fact]
    public void Esc_the_export_lock_and_a_click_without_a_move_edit_nothing()
    {
        var (_, clip) = AddClip();
        var editingLock = new EditingLock();
        var timeline = new TimelineViewModel(_projects, _edit, _status, NullLogger<TimelineViewModel>.Instance, editingLock: editingLock) { SnappingEnabled = false };
        var vm = timeline.Tracks.SelectMany(t => t.Clips).Single(v => v.Id == clip.Id);
        double X(double seconds) => TimelineCoordinateMapper.TimeToX(S(seconds), timeline.PixelsPerSecond);
        var part = FollowSettings.Of(new FollowPart(S(4), S(12)));
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
        Assert.Equal(X(4), vm.FollowSegments[0].Left, 6);

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
    public void Two_independent_parts_on_one_clip_each_with_its_start_stop_strip_and_undo()
    {
        var (asset, clip) = AddClip();
        var inspector = new InspectorViewModel(_edit, _status);
        var playhead = S(4);
        inspector.Playhead = () => playhead;
        inspector.ShowClip(new TimelineClipSelection(clip, "climb", asset, Rate));
        var timeline = new TimelineViewModel(_projects, _edit, _status, NullLogger<TimelineViewModel>.Instance) { SnappingEnabled = false };
        var vm = timeline.Tracks.SelectMany(t => t.Clips).Single(v => v.Id == clip.Id);

        inspector.FollowFromHereCommand.Execute(null);                      // the first route
        playhead = S(9);
        inspector.StopFollowHereCommand.Execute(null);
        playhead = S(12);                                                  // the second route, later in the clip
        inspector.FollowFromHereCommand.Execute(null);
        Assert.True(inspector.IsRecordingFollow);
        playhead = S(15);
        inspector.StopFollowHereCommand.Execute(null);
        Assert.Equal(new FollowSettings(ImmutableArray.Create(new FollowPart(S(4), S(9)), new FollowPart(S(12), S(15)))), clip.Follow);
        Assert.Equal(2, vm.FollowSegments.Count);

        // Each part picks its own climber.
        inspector.OnPersonPicked(clip.Id, S(13), new NormalizedPoint(0.6, 0.5));
        Assert.Equal(new FollowPart(S(12), S(15), S(13), new NormalizedPoint(0.6, 0.5)), clip.Follow!.Parts[1]);
        Assert.Null(clip.Follow.Parts[0].TargetPoint);

        // «Следить отсюда» inside a part changes nothing.
        playhead = S(5);
        inspector.FollowFromHereCommand.Execute(null);
        Assert.False(inspector.IsRecordingFollow);
        Assert.Equal(2, clip.Follow!.Parts.Length);

        // Undo takes back the pick, the second part's end, then the second part; the first stays.
        _undo.Undo();
        _undo.Undo();
        Assert.Equal(new FollowSettings(ImmutableArray.Create(new FollowPart(S(4), S(9)), new FollowPart(S(12)))), clip.Follow);
        _undo.Undo();
        Assert.Equal(FollowSettings.Of(new FollowPart(S(4), S(9))), clip.Follow);
        Assert.Single(vm.FollowSegments);
    }

    [Fact]
    public void A_part_starts_up_to_the_next_part_and_its_edge_stops_at_the_neighbour_then_deletes()
    {
        var (asset, clip) = AddClip();
        Assert.True(_edit.SetClipFollow(clip.Id, FollowSettings.Of(new FollowPart(S(10), S(14)))).Success);
        var inspector = new InspectorViewModel(_edit, _status);
        var playhead = S(4);
        inspector.Playhead = () => playhead;
        inspector.ShowClip(new TimelineClipSelection(clip, "climb", asset, Rate));
        inspector.FollowFromHereCommand.Execute(null);                      // before the existing part: it ends where that one starts
        Assert.Equal(new FollowSettings(ImmutableArray.Create(new FollowPart(S(4), S(10)), new FollowPart(S(10), S(14)))), clip.Follow);
        inspector.StopFollowHereCommand.Execute(null);                      // the playhead never moved: too short, kept
        Assert.Equal(2, clip.Follow!.Parts.Length);

        var timeline = new TimelineViewModel(_projects, _edit, _status, NullLogger<TimelineViewModel>.Instance) { SnappingEnabled = false };
        var vm = timeline.Tracks.SelectMany(t => t.Clips).Single(v => v.Id == clip.Id);
        double X(double seconds) => TimelineCoordinateMapper.TimeToX(S(seconds), timeline.PixelsPerSecond);
        // The second part's end dragged past the clip's end: open (the parts never overlap).
        Assert.True(timeline.BeginFollowDrag(vm, X(14), 1000));
        timeline.UpdateFollowDrag(1000 + X(10));
        timeline.EndFollowDrag();
        Assert.Equal(new FollowPart(S(10), null), clip.Follow!.Parts[1]);    // dragged to the clip's end: open
        Assert.True(FollowSettings.AreValid(clip.Follow.Parts));

        // «Удалить участок» under the playhead.
        playhead = S(5);
        inspector.OnPlayheadMoved();
        Assert.True(inspector.HasFollowPartHere);
        inspector.DeleteFollowPartCommand.Execute(null);
        Assert.Equal(FollowSettings.Of(new FollowPart(S(10))), clip.Follow);
    }

    [Fact]
    public void After_a_split_an_edge_opened_to_the_clips_edge_stops_at_a_part_beyond_it()
    {
        var (asset, clip) = AddClip();
        Assert.True(_edit.SetClipFollow(clip.Id, new FollowSettings(ImmutableArray.Create(new FollowPart(S(2), S(5)), new FollowPart(S(12))))).Success);
        Assert.True(_edit.Split(S(10), new[] { clip.Id }).Success);
        var left = (VideoClip)_projects.Current.Timeline.VideoTracks[0].Clips.Single(c => c.TimelineStart == MediaTime.Zero);
        var right = (VideoClip)_projects.Current.Timeline.VideoTracks[0].Clips.Single(c => c.TimelineStart == S(10));
        var timeline = new TimelineViewModel(_projects, _edit, _status, NullLogger<TimelineViewModel>.Instance) { SnappingEnabled = false };
        var vm = timeline.Tracks.SelectMany(t => t.Clips).Single(v => v.Id == left.Id);
        double X(double seconds) => TimelineCoordinateMapper.TimeToX(S(seconds), timeline.PixelsPerSecond);

        // The left half shows only [2, 5); its end dragged to the half's end opens it — up to the part at 12 s (hidden).
        Assert.Single(vm.FollowSegments);
        Assert.True(timeline.BeginFollowDrag(vm, X(5), 1000));
        timeline.UpdateFollowDrag(1000 + X(20));
        timeline.EndFollowDrag();
        Assert.Equal(new FollowPart(S(2), S(12)), left.Follow!.Parts[0]);
        Assert.True(FollowSettings.AreValid(left.Follow.Parts));

        // «Следить отсюда» on the right half's first frame: the new part starts where the part before it ends.
        Assert.True(_edit.SetClipFollow(right.Id, new FollowSettings(ImmutableArray.Create(new FollowPart(S(2), S(5)), new FollowPart(S(14), S(16))))).Success);
        var inspector = new InspectorViewModel(_edit, _status) { Playhead = () => S(10) };
        inspector.ShowClip(new TimelineClipSelection(right, "climb", asset, Rate));
        inspector.FollowFromHereCommand.Execute(null);
        Assert.Equal(new FollowPart(S(5), S(14)), right.Follow!.Parts[1]);
        Assert.True(FollowSettings.AreValid(right.Follow.Parts));
    }

    [Fact]
    public void The_offset_fields_edit_the_part_under_the_playhead()
    {
        var (asset, clip) = AddClip();
        Assert.True(_edit.SetClipFollow(clip.Id, new FollowSettings(ImmutableArray.Create(new FollowPart(S(2), S(5)), new FollowPart(S(8), S(12))))).Success);
        var playhead = S(10);
        var inspector = new InspectorViewModel(_edit, _status) { Playhead = () => playhead };
        inspector.ShowClip(new TimelineClipSelection(clip, "climb", asset, Rate));
        Assert.True(inspector.HasFollowOffset);
        Assert.Equal(0m, inspector.FollowOffsetX);
        inspector.FollowOffsetX = 150;
        inspector.FollowOffsetY = -30;
        Assert.Equal(new FollowPart(S(8), S(12), OffsetX: 150, OffsetY: -30), clip.Follow!.Parts[1]);
        Assert.Equal(0, clip.Follow.Parts[0].OffsetX);

        playhead = S(6);                                                     // between the parts: no fields
        inspector.OnPlayheadMoved();
        Assert.False(inspector.HasFollowOffset);
        playhead = S(3);
        inspector.OnPlayheadMoved();
        Assert.True(inspector.HasFollowOffset);
        Assert.Equal(0m, inspector.FollowOffsetX);
    }

    [Fact]
    public void A_split_keeps_the_part_on_the_same_source_frames()
    {
        var (_, clip) = AddClip();
        var timeline = new TimelineViewModel(_projects, _edit, _status, NullLogger<TimelineViewModel>.Instance) { SnappingEnabled = false };
        Assert.True(_edit.SetClipFollow(clip.Id, FollowSettings.Of(new FollowPart(S(4), S(12)))).Success);
        Assert.True(_edit.Split(S(8), new[] { clip.Id }).Success);
        Assert.True(_edit.Split(S(15), _projects.Current.Timeline.VideoTracks[0].Clips.Where(c => c.TimelineStart == S(8)).Select(c => c.Id).ToList()).Success);
        double X(double seconds) => TimelineCoordinateMapper.TimeToX(S(seconds), timeline.PixelsPerSecond);
        var vms = timeline.Tracks.SelectMany(t => t.Clips).OrderBy(v => v.Clip.TimelineStart).ToList();
        Assert.Equal(3, vms.Count);
        Assert.True(vms[0].HasFollowStrip);
        Assert.Equal(X(4), vms[0].FollowSegments[0].Left, 6);
        Assert.Equal(X(4), vms[0].FollowSegments[0].Width, 6);                          // [4, 8)
        Assert.True(vms[1].HasFollowStrip);
        Assert.Equal(0, vms[1].FollowSegments[0].Left, 6);
        Assert.Equal(X(4), vms[1].FollowSegments[0].Width, 6);                          // [8, 12)
        Assert.False(vms[2].HasFollowStrip);                                // [15, 20): none of the part
    }
}
