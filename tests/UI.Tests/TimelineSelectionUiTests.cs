using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Project;
using AiVideoEditor.Timeline;
using AiVideoEditor.UI.Common;
using AiVideoEditor.UI.Services;
using AiVideoEditor.UI.ViewModels.Panels;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiVideoEditor.UI.Tests;

/// <summary>
/// Phase 22 (D037) in the timeline panel: Select All and Esc, the marquee, "1 of N selected", the nudges, the drag preview
/// of the clips that make room, a snap that never makes an overlap, the snapped drop (a new clip on an empty track at 0)
/// and the auto-scroll rule. Real project, edit service and view-model.
/// </summary>
public sealed class TimelineSelectionUiTests
{
    private static readonly FrameRate Rate = FrameRate.Default;

    private readonly UndoRedoService _undo = new();
    private readonly ProjectService _projects;
    private readonly TimelineEditService _edit;
    private readonly StatusService _status = new();
    private readonly EditingLock _lock = new();

    public TimelineSelectionUiTests()
    {
        _projects = new ProjectService(_undo, NullLogger<ProjectService>.Instance);
        _edit = new TimelineEditService(_projects, _undo, NullLogger<TimelineEditService>.Instance);
    }

    private TimelineViewModel Timeline() => new(_projects, _edit, _status, NullLogger<TimelineViewModel>.Instance, _lock);

    private Sequence Sequence => _projects.Current.Timeline;
    private Track V1 => Sequence.VideoTracks[0];
    private static MediaTime F(long n) => MediaTime.FromFrame(n, Rate);
    private static long Start(Clip c) => c.TimelineStart.ToNearestFrame(Rate);

    private TextClip Text(Track track, long start, long end)
    {
        var clip = new TextClip { Text = "T", TimelineStart = F(start), Duration = F(end) - F(start) };
        var index = track.Clips.FindIndex(c => c.TimelineStart > clip.TimelineStart);
        track.Clips.Insert(index < 0 ? track.Clips.Count : index, clip);
        return clip;
    }

    private Track AddVideoTrack()
    {
        Assert.True(_edit.AddTrack(TrackType.Video).Success);
        return Sequence.VideoTracks[^1];
    }

    private static TimelineClipViewModel Vm(TimelineViewModel timeline, Clip clip) =>
        timeline.Tracks.SelectMany(t => t.Clips).Single(c => c.Clip == clip);

    private static List<Clip> Selected(TimelineViewModel timeline) =>
        timeline.Tracks.SelectMany(t => t.Clips).Where(c => c.IsSelected).Select(c => c.Clip).ToList();

    private static double X(TimelineViewModel timeline, long frame) => TimelineCoordinateMapper.TimeToX(F(frame), timeline.PixelsPerSecond);

    private static int Lane(TimelineViewModel timeline, Track track) => timeline.Tracks.Select(t => t.Track).ToList().IndexOf(track);

    /// <summary>A linked pair: V1 a recording with sound at [0, 100), its sound unlinked to A1 and linked again.</summary>
    private (VideoClip Video, AudioClip Audio) LinkedPair()
    {
        var asset = new MediaAsset
        {
            FilePath = Path.Combine(Path.GetTempPath(), "aive-select-ui", Guid.NewGuid().ToString("N"), "take.mp4"),
            Kind = MediaKind.Video, AnalysisStatus = MediaAnalysisStatus.Completed,
            Metadata = new MediaMetadata { Duration = MediaTime.FromSeconds(20), FrameRate = Rate, Width = 1920, Height = 1080, AudioCodec = "aac" }
        };
        _projects.AddMediaAssets(new[] { asset });
        Assert.True(_edit.AddClip(asset.Id).Success);
        var video = (VideoClip)V1.Clips[0];
        Assert.True(_edit.TrimClip(video.Id, ClipEdge.End, F(100)).Success);
        Assert.True(_edit.UnlinkAudio(video.Id).Success);
        var audio = (AudioClip)Sequence.AudioTracks[0].Clips[0];
        Assert.True(_edit.LinkClips(new[] { video.Id, audio.Id }).Success);
        return (video, audio);
    }

    private MediaAsset Image()
    {
        var asset = new MediaAsset
        {
            FilePath = Path.Combine(Path.GetTempPath(), "aive-select-ui", Guid.NewGuid().ToString("N"), "logo.png"),
            Kind = MediaKind.Image, AnalysisStatus = MediaAnalysisStatus.Pending
        };
        _projects.AddMediaAssets(new[] { asset });
        return asset;
    }

    // --- Select All, Esc, the Inspector line -------------------------------------------------------------------------------

    [Fact]
    public void Select_all_skips_locked_tracks_and_makes_the_earliest_clip_the_primary()
    {
        var v2 = AddVideoTrack();
        var a = Text(V1, 10, 20);
        var b = Text(V1, 0, 4);
        Text(v2, 0, 5);
        Assert.True(_edit.SetTrackLocked(v2.Id, true).Success);
        _undo.Clear();
        var timeline = Timeline();
        TimelineClipSelection? last = null;
        timeline.SelectionChanged += (_, s) => last = s;

        timeline.SelectAllCommand.Execute(null);

        Assert.Equal(new HashSet<Clip> { a, b }, Selected(timeline).ToHashSet());
        Assert.Same(b, last?.Clip);
        Assert.Equal(2, last?.SelectedCount);
        Assert.Equal("2 clips selected", _status.Message);
        Assert.False(_undo.CanUndo);                                          // view state, no undo step
    }

    [Fact]
    public void Select_all_brings_a_partner_even_from_a_locked_track_as_a_click_does()
    {
        var (video, audio) = LinkedPair();
        Assert.True(_edit.SetTrackLocked(Sequence.AudioTracks[0].Id, true).Success);
        var timeline = Timeline();

        timeline.SelectAllCommand.Execute(null);

        Assert.Equal(new HashSet<Clip> { video, audio }, Selected(timeline).ToHashSet());
    }

    [Fact]
    public void Select_all_with_nothing_to_select_says_so()
    {
        var timeline = Timeline();
        timeline.SelectAllCommand.Execute(null);
        Assert.Equal("There is no clip on an unlocked track to select.", _status.Message);
    }

    [Fact]
    public void Escape_clears_the_selection()
    {
        var a = Text(V1, 0, 10);
        var timeline = Timeline();
        timeline.OnClipPressed(Vm(timeline, a), toggle: false);

        timeline.EscapeCommand.Execute(null);

        Assert.Empty(Selected(timeline));
    }

    [Fact]
    public void The_inspector_names_the_primary_as_one_of_the_selected_clips()
    {
        var a = Text(V1, 0, 10);
        var inspector = new InspectorViewModel(_edit, _status);

        inspector.ShowClip(new TimelineClipSelection(a, "Title", null, Rate, SelectedCount: 3));
        Assert.Equal("Title (1 of 3 selected)", inspector.ClipName);
        inspector.ShowClip(new TimelineClipSelection(a, "Title", null, Rate));
        Assert.Equal("Title", inspector.ClipName);
    }

    // --- Marquee ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void A_marquee_selects_the_clips_it_intersects_on_unlocked_tracks_live_and_ctrl_adds()
    {
        var v2 = AddVideoTrack();
        var a = Text(V1, 0, 10);
        var b = Text(V1, 20, 30);
        var c = Text(v2, 5, 15);
        var timeline = Timeline();
        var h = TimelineTrackViewModel.Height;
        var (laneV2, laneV1) = (Lane(timeline, v2), Lane(timeline, V1));
        Assert.Equal(0, laneV2);

        timeline.BeginMarquee(X(timeline, 8), laneV2 * h + h / 2, additive: false);
        timeline.UpdateMarquee(X(timeline, 12), laneV1 * h + h / 2);

        Assert.True(timeline.IsMarqueeVisible);
        Assert.Equal(new HashSet<Clip> { a, c }, Selected(timeline).ToHashSet());    // live, before the release
        timeline.EndMarquee();
        Assert.False(timeline.IsMarqueeVisible);
        Assert.Equal("2 clips selected", _status.Message);

        timeline.BeginMarquee(X(timeline, 24), laneV1 * h + 1, additive: true);
        timeline.UpdateMarquee(X(timeline, 27), laneV1 * h + 2);
        timeline.EndMarquee();
        Assert.Equal(new HashSet<Clip> { a, b, c }, Selected(timeline).ToHashSet());
    }

    [Fact]
    public void A_marquee_skips_locked_tracks_brings_partners_and_esc_restores_the_selection_before_it()
    {
        var (video, audio) = LinkedPair();
        var v2 = AddVideoTrack();
        Text(v2, 0, 50);
        Assert.True(_edit.SetTrackLocked(v2.Id, true).Success);
        var timeline = Timeline();
        var h = TimelineTrackViewModel.Height;

        timeline.BeginMarquee(X(timeline, 10), 1, additive: false);                         // from the locked V2 …
        timeline.UpdateMarquee(X(timeline, 20), Lane(timeline, V1) * h + h / 2);            // … into V1 only
        Assert.Equal(new HashSet<Clip> { video, audio }, Selected(timeline).ToHashSet());

        timeline.EscapeCommand.Execute(null);                                               // Esc during the marquee
        Assert.False(timeline.IsMarqueeVisible);
        Assert.Empty(Selected(timeline));
        Assert.False(timeline.IsMarqueeActive);
    }

    [Fact]
    public void A_press_on_an_empty_place_without_a_drag_clears_the_selection_and_with_ctrl_keeps_it()
    {
        var a = Text(V1, 0, 10);
        var timeline = Timeline();
        timeline.OnClipPressed(Vm(timeline, a), toggle: false);

        timeline.BeginMarquee(X(timeline, 50), 5, additive: true);
        timeline.UpdateMarquee(X(timeline, 50) + 1, 6);                                     // under the drag threshold
        timeline.EndMarquee();
        Assert.Equal(new[] { (Clip)a }, Selected(timeline));

        timeline.BeginMarquee(X(timeline, 50), 5, additive: false);
        timeline.EndMarquee();
        Assert.Empty(Selected(timeline));
    }

    // --- Nudge -----------------------------------------------------------------------------------------------------------

    [Fact]
    public void Nudges_move_the_selection_by_a_frame_or_a_second_each_one_undo_step()
    {
        var a = Text(V1, 10, 20);
        var timeline = Timeline();
        Assert.False(timeline.NudgeRightCommand.CanExecute(null));
        timeline.OnClipPressed(Vm(timeline, a), toggle: false);

        timeline.NudgeRightCommand.Execute(null);
        timeline.NudgeRightCommand.Execute(null);
        Assert.Equal(12, Start(a));
        timeline.NudgeLeftSecondCommand.Execute(null);                                      // 30 frames at 30 fps: refused
        Assert.Equal(12, Start(a));
        Assert.Contains("beginning of the timeline", _status.Message);
        timeline.NudgeRightSecondCommand.Execute(null);
        Assert.Equal(42, Start(a));
        Assert.True(Vm(timeline, a).IsSelected);

        _undo.Undo();
        Assert.Equal(12, Start(a));
        _undo.Undo();
        Assert.Equal(11, Start(a));
        _undo.Undo();
        Assert.Equal(10, Start(a));
        Assert.False(_undo.CanUndo);                                                        // the refusal left no step

        using (_lock.Acquire()) Assert.False(timeline.NudgeLeftCommand.CanExecute(null));
    }

    [Fact]
    public void A_nudge_pushes_on_the_right_and_a_collision_on_the_left_is_refused()
    {
        var a = Text(V1, 0, 10);
        var b = Text(V1, 10, 20);
        var timeline = Timeline();
        timeline.OnClipPressed(Vm(timeline, b), toggle: false);

        timeline.NudgeLeftCommand.Execute(null);
        Assert.Contains("start inside another clip", _status.Message);
        Assert.False(_undo.CanUndo);

        timeline.OnClipPressed(Vm(timeline, a), toggle: false);
        timeline.NudgeRightCommand.Execute(null);
        Assert.Equal((1, 11), (Start(a), Start(b)));
    }

    // --- Drag preview and snapping ---------------------------------------------------------------------------------------

    [Fact]
    public void The_drag_preview_shows_the_clips_that_make_room_where_the_release_puts_them()
    {
        var a = Text(V1, 0, 10);
        var b = Text(V1, 12, 22);
        var c = Text(V1, 40, 50);
        var timeline = Timeline();
        timeline.SnappingEnabled = false;
        timeline.OnClipPressed(Vm(timeline, a), toggle: false);
        timeline.BeginMove(X(timeline, 5));

        timeline.UpdateGesture(X(timeline, 10), timeline.Tracks[Lane(timeline, V1)]);      // +5 frames

        Assert.Equal(X(timeline, 15), Vm(timeline, b).Left, 6);                            // pushed by the overlap 3
        Assert.Equal(X(timeline, 40), Vm(timeline, c).Left, 6);
        Assert.False(Vm(timeline, a).IsInvalid);

        timeline.EndGesture();
        Assert.Equal((5, 15, 40), (Start(a), Start(b), Start(c)));
        _undo.Undo();
        Assert.Equal((0, 12, 40), (Start(a), Start(b), Start(c)));
    }

    [Fact]
    public void A_refused_drag_is_marked_invalid_and_the_release_changes_nothing()
    {
        Text(V1, 0, 10);
        var b = Text(V1, 20, 30);
        var timeline = Timeline();
        timeline.SnappingEnabled = false;
        timeline.OnClipPressed(Vm(timeline, b), toggle: false);
        timeline.BeginMove(X(timeline, 25));

        timeline.UpdateGesture(X(timeline, 10), timeline.Tracks[Lane(timeline, V1)]);      // b would start at 5, inside

        Assert.True(Vm(timeline, b).IsInvalid);
        timeline.EndGesture();
        Assert.Equal(20, Start(b));
        Assert.False(_undo.CanUndo);
    }

    [Fact]
    public void A_snap_that_would_push_a_clip_is_skipped()
    {
        // A group's outer edges snap (D037 §2): its end to the marker at 134 would put X into N — skipped; X lands where
        // the pointer has it (1.6 frames → 2) and nothing is pushed.
        var v2 = AddVideoTrack();
        var x = Text(V1, 100, 110);
        var n = Text(V1, 113, 120);
        var y = Text(v2, 100, 130);
        Assert.True(_edit.AddMarker(F(134)).Success);
        _undo.Clear();
        var timeline = Timeline();
        Assert.True(timeline.SnappingEnabled);
        timeline.OnClipPressed(Vm(timeline, x), toggle: false);
        timeline.OnClipPressed(Vm(timeline, y), toggle: true);
        var perFrame = X(timeline, 1);
        timeline.BeginMove(X(timeline, 105));

        timeline.UpdateGesture(X(timeline, 105) + 1.6 * perFrame, null);

        Assert.False(timeline.IsSnapIndicatorVisible);
        Assert.Equal(X(timeline, 102), Vm(timeline, x).Left, 6);
        Assert.Equal(X(timeline, 113), Vm(timeline, n).Left, 6);
        timeline.EndGesture();
        Assert.Equal((102, 113, 102), (Start(x), Start(n), Start(y)));
    }

    [Fact]
    public void A_snap_that_makes_no_overlap_is_taken()
    {
        var x = Text(V1, 100, 110);
        Text(V1, 140, 150);
        Assert.True(_edit.AddMarker(F(112)).Success);
        var timeline = Timeline();
        timeline.OnClipPressed(Vm(timeline, x), toggle: false);
        var perFrame = X(timeline, 1);
        timeline.BeginMove(X(timeline, 105));

        timeline.UpdateGesture(X(timeline, 105) + 1.6 * perFrame, timeline.Tracks[Lane(timeline, V1)]);

        Assert.True(timeline.IsSnapIndicatorVisible);                                       // the end to the marker at 112
        timeline.EndGesture();
        Assert.Equal(102, Start(x));
    }

    // --- The drop --------------------------------------------------------------------------------------------------------

    [Fact]
    public void A_drop_onto_an_empty_track_lands_at_0_with_snap_and_under_the_pointer_without()
    {
        var timeline = Timeline();
        var image = Image();
        var lane = timeline.Tracks[Lane(timeline, V1)];

        timeline.DropMedia(image.Id, lane, X(timeline, 50));
        Assert.Equal(0, Start(V1.Clips.Single()));
        _undo.Undo();

        timeline.SnappingEnabled = false;
        timeline.DropMedia(image.Id, lane, X(timeline, 50));
        Assert.Equal(50, Start(V1.Clips.Single()));
    }

    [Fact]
    public void A_drop_snaps_to_a_clip_edge_shows_the_indicator_while_dragging_and_makes_room()
    {
        var a = Text(V1, 0, 100);
        var b = Text(V1, 300, 310);
        var timeline = Timeline();
        var image = Image();                                                                // 5 s = 150 frames
        var lane = timeline.Tracks[Lane(timeline, V1)];

        timeline.ShowDropPreview(image.Id, lane, X(timeline, 102));
        Assert.True(timeline.IsSnapIndicatorVisible);
        Assert.Equal(X(timeline, 100), timeline.SnapIndicatorX, 6);

        timeline.DropMedia(image.Id, lane, X(timeline, 102));
        var added = V1.Clips.Single(c => c != a && c != b);
        Assert.Equal(100, Start(added));
        Assert.False(timeline.IsSnapIndicatorVisible);

        timeline.DropMedia(image.Id, lane, X(timeline, 200));                               // inside the image: refused
        Assert.Contains("start inside another clip", _status.Message);
        timeline.DropMedia(image.Id, lane, X(timeline, 280));                               // onto b: b makes room
        Assert.Equal(430, Start(b));
    }

    // --- Auto-scroll -----------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(500, 0)]
    [InlineData(33, 0)]
    [InlineData(31, -1)]
    [InlineData(0, -TimelineViewModel.AutoScrollMaxStepPixels / 3)]
    [InlineData(-200, -TimelineViewModel.AutoScrollMaxStepPixels)]
    [InlineData(968, 0)]
    [InlineData(969, 1)]
    [InlineData(1000, TimelineViewModel.AutoScrollMaxStepPixels / 3)]
    [InlineData(1200, TimelineViewModel.AutoScrollMaxStepPixels)]
    public void The_auto_scroll_step_grows_with_the_depth_past_the_edge_zone(double viewportX, double expected) =>
        Assert.Equal(expected, TimelineViewModel.AutoScrollStep(viewportX, 1000), 6);
}
