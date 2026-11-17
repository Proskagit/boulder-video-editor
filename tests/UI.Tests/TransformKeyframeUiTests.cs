using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Composition;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Playback;
using AiVideoEditor.Project;
using AiVideoEditor.Timeline;
using AiVideoEditor.Timeline.Playback;
using AiVideoEditor.Timeline.Tests.Playback;
using AiVideoEditor.UI.Common;
using AiVideoEditor.UI.Services;
using AiVideoEditor.UI.ViewModels;
using AiVideoEditor.UI.ViewModels.Panels;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiVideoEditor.UI.Tests;

/// <summary>
/// D063 (Phase 44) through the real shell wiring: the timeline's keyframe button (add / remove at the playhead, the picture
/// unchanged), the diamonds, Delete on a selected keyframe, the playhead snapped to a diamond, the Inspector's fields showing
/// and editing the keyframe on the playhead's frame, its easing, the Preview's Move writing the keyframe (one undo step) and
/// the magnetic snap to the canvas edges (exact, released beyond the threshold, measured in screen pixels).
/// </summary>
public sealed class TransformKeyframeUiTests : IAsyncLifetime
{
    private static readonly FrameRate Rate = FrameRate.Fps25;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "AiVideoEditorTests", Guid.NewGuid().ToString("N"));
    private readonly UndoRedoService _undo = new();
    private readonly ProjectService _projects;
    private readonly TimelineEditService _edit;
    private readonly PlaybackService _playback;
    private readonly StatusService _status = new();
    private readonly EditingLock _lock = new();
    private readonly MainWindowViewModel _vm;

    public TransformKeyframeUiTests()
    {
        _projects = new ProjectService(_undo, NullLogger<ProjectService>.Instance);
        _edit = new TimelineEditService(_projects, _undo, NullLogger<TimelineEditService>.Instance);
        _playback = new PlaybackService(new FakeVideoDecoder(), new FakeReferenceClock(), NullLogger<PlaybackService>.Instance,
            new PlaybackSettings { BufferFrames = 4 }, new FakeAudioDecoder(), new FakeAudioOutput());
        var analysis = new MediaAnalysisCoordinator(new HoldMarkupUiTests.NoAnalysis(), _projects, NullLogger<MediaAnalysisCoordinator>.Instance);
        var import = new MediaImportWorkflow(new ScriptedPicker(), new HoldMarkupUiTests.NoImport(), _projects, analysis, _status, NullLogger<MediaImportWorkflow>.Instance);
        var files = new ProjectFileWorkflow(_projects, analysis, new NullAutosave(), new ScriptedDialogs(), new ScriptedPicker(), _status,
            NullLogger<ProjectFileWorkflow>.Instance);
        _vm = new MainWindowViewModel(
            new ToolbarViewModel(_undo, files, import, _status),
            new MediaBrowserViewModel(_projects, import, NullLogger<MediaBrowserViewModel>.Instance),
            new PreviewViewModel(_status, _playback, _projects, NullLogger<PreviewViewModel>.Instance),
            new InspectorViewModel(_edit, _status, editingLock: _lock),
            new TimelineViewModel(_projects, _edit, _status, NullLogger<TimelineViewModel>.Instance),
            _status, files, _projects, NullLogger<MainWindowViewModel>.Instance,
            previewTransform: new PreviewTransformViewModel(_edit, _projects, _status, _lock, _ => (100, 50)));
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        await _playback.DisposeAsync();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private TimelineViewModel Timeline => _vm.Timeline;
    private PreviewTransformViewModel Tool => _vm.PreviewTransform!;
    private InspectorViewModel Inspector => _vm.Inspector;

    private static MediaTime F(long frame) => MediaTime.FromFrame(frame, Rate);

    private VideoClip AddVideo(int width = 1920, int height = 1080)
    {
        var asset = new MediaAsset
        {
            FilePath = Path.Combine(_root, "media", "v.mp4"),
            Kind = MediaKind.Video,
            AnalysisStatus = MediaAnalysisStatus.Completed,
            Metadata = new MediaMetadata
            {
                Duration = MediaTime.FromSeconds(10), FrameRate = Rate, AvgFrameRate = Rate,
                Width = width, Height = height, DisplayWidth = width, DisplayHeight = height
            }
        };
        _projects.AddMediaAssets(new[] { asset });
        Timeline.AddMedia(asset);
        var clip = _projects.Current.Timeline.VideoTracks.SelectMany(t => t.Clips).OfType<VideoClip>().Single();
        Timeline.SetPlayhead(F(25));
        return clip;
    }

    /// <summary>What the composition shows on <paramref name="frame"/> (the snapshot's own evaluation, Preview = export).</summary>
    private VisualProperties Shown(long frame)
    {
        var snapshot = PlaybackSnapshotBuilder.Build(_projects.Current, 1);
        var picture = snapshot.LayersAt(F(frame)).OfType<PictureLayer>().Single();
        return picture.Visual;
    }

    /// <summary>The undo history's length (undone and redone; the selection and the playhead, which an undone Add drops, put
    /// back).</summary>
    private int Steps()
    {
        var selected = _projects.Current.Timeline.VideoTracks.SelectMany(t => t.Clips).FirstOrDefault()?.Id;
        var playhead = Timeline.Playhead;
        var n = 0;
        while (_undo.CanUndo) { _undo.Undo(); n++; }
        for (var i = 0; i < n; i++) _undo.Redo();
        if (selected is { } id) Timeline.SelectClipFromPreview(id);
        Timeline.SetPlayhead(playhead);
        return n;
    }

    private TimelineClipViewModel ClipVm(Clip clip) => Timeline.Tracks.SelectMany(t => t.Clips).Single(c => c.Id == clip.Id);

    // --- the button --------------------------------------------------------------------------------------------------

    [Fact]
    public void The_button_adds_a_keyframe_with_the_shown_values_and_a_second_press_removes_it()
    {
        var clip = AddVideo();
        _edit.SetClipProperties(clip.Id, new Core.Interfaces.ClipPropertyChange { Visual = VisualProperties.Of(clip)!.Value with { PositionX = 40, Scale = 1.5 } });
        var before = Shown(25);
        var steps = Steps();

        Assert.True(Timeline.ToggleKeyframeCommand.CanExecute(null));
        Timeline.ToggleKeyframeCommand.Execute(null);
        var key = Assert.Single(clip.Keyframes!.Keys);
        Assert.Equal((40.0, 0.0, 1.5), (key.X, key.Y, key.Scale));
        Assert.Equal(before, Shown(25));                                        // the picture does not change
        var vm = ClipVm(clip);
        var marker = Assert.Single(vm.KeyframeMarkers);
        Assert.False(marker.IsFollow);
        Assert.True(marker.IsSelected);
        Assert.Equal(steps + 1, Steps());
        vm = ClipVm(clip);

        Timeline.ToggleKeyframeCommand.Execute(null);
        Assert.Null(clip.Keyframes);
        Assert.Equal((40.0, 1.5), (clip.PositionX, clip.Scale));               // the last one's values stay as the static ones
        Assert.Empty(vm.KeyframeMarkers);
    }

    [Fact]
    public void The_button_needs_a_selected_video_clip_under_the_playhead()
    {
        var clip = AddVideo();
        Timeline.SetPlayhead(clip.TimelineEnd + MediaTime.FromSeconds(2));
        Assert.False(Timeline.ToggleKeyframeCommand.CanExecute(null));
        Timeline.SetPlayhead(F(10));
        Assert.True(Timeline.ToggleKeyframeCommand.CanExecute(null));
    }

    [Fact]
    public void Two_keyframes_interpolate_over_exactly_the_time_between_them_and_undo_takes_each_back()
    {
        var clip = AddVideo();
        Timeline.SetPlayhead(F(0));
        Timeline.ToggleKeyframeCommand.Execute(null);
        Timeline.SetPlayhead(F(125));                                           // five seconds later
        Inspector.PositionX = 500;                                              // an edit away from a keyframe adds one there
        Assert.Equal(2, clip.Keyframes!.Count);
        Assert.Equal(0, Shown(0).PositionX);
        Assert.Equal(248, Shown(62).PositionX, 9);                              // frame 62 = 2.48 s of the 5 s: 248 px
        Assert.Equal(500, Shown(125).PositionX);
        Assert.Equal(500, Shown(200).PositionX);                                // after the last: held

        _undo.Undo();
        Assert.Single(clip.Keyframes!.Keys);
        _undo.Undo();
        Assert.Null(clip.Keyframes);
        _undo.Redo();
        _undo.Redo();
        Assert.Equal(2, clip.Keyframes!.Count);
    }

    // --- the diamonds ------------------------------------------------------------------------------------------------

    [Fact]
    public void Delete_with_a_selected_keyframe_removes_the_keyframe_not_the_clip()
    {
        var clip = AddVideo();
        Timeline.SetPlayhead(F(10));
        Timeline.ToggleKeyframeCommand.Execute(null);
        Timeline.SetPlayhead(F(50));
        Timeline.ToggleKeyframeCommand.Execute(null);
        Assert.NotNull(Timeline.SelectedKeyframe);

        Timeline.DeleteSelectedCommand.Execute(null);
        Assert.Single(_projects.Current.Timeline.VideoTracks.SelectMany(t => t.Clips));
        Assert.Equal(F(10).ToFrameFloor(Rate), new TransformKeyframeEditor(_edit, _projects).FrameOf(clip, Assert.Single(clip.Keyframes!.Keys).Time));
        Assert.Null(Timeline.SelectedKeyframe);
    }

    [Fact]
    public void A_press_on_a_diamond_selects_it_and_a_drag_moves_it_along_the_frames_in_one_step()
    {
        var clip = AddVideo();
        Timeline.SetPlayhead(F(10));
        Timeline.ToggleKeyframeCommand.Execute(null);
        Timeline.SetPlayhead(F(60));
        Inspector.PositionX = 100;
        var steps = Steps();
        var vm = ClipVm(clip);
        Assert.Equal(2, vm.KeyframeMarkers.Count);
        var first = vm.KeyframeMarkers.OrderBy(m => m.Left).First();

        Assert.True(Timeline.BeginKeyframeDrag(vm, first, first.Left));
        Assert.Equal(F(10), Timeline.Playhead);                                 // the playhead goes to the keyframe
        var pxPerFrame = Timeline.PixelsPerSecond / 25.0;
        Assert.True(Timeline.IsKeyframeDragging);
        Timeline.UpdateKeyframeDrag(first.Left + 5 * pxPerFrame);
        var moved = vm.KeyframeMarkers.OrderBy(m => m.Left).First();
        Assert.True(moved.Left > first.Left, $"{moved.Left} {first.Left} {pxPerFrame}");
        Timeline.EndKeyframeDrag();

        var editor = new TransformKeyframeEditor(_edit, _projects);
        Assert.Equal(new long[] { 15, 60 }, clip.Keyframes!.Keys.Select(k => editor.FrameOf(clip, k.Time)).ToArray());
        Assert.Equal(steps + 1, Steps());
    }

    [Fact]
    public void With_snap_on_the_playhead_lands_on_a_diamond_of_the_selected_clip()
    {
        var clip = AddVideo();
        Timeline.SetPlayhead(F(40));
        Timeline.ToggleKeyframeCommand.Execute(null);
        var vm = Timeline.Tracks.SelectMany(t => t.Clips).Single(c => c.Id == clip.Id);
        var marker = Assert.Single(vm.KeyframeMarkers);
        Timeline.SnappingEnabled = true;
        Timeline.SetPlayheadFromX(vm.Left + marker.Left + 4);
        Assert.Equal(F(40), Timeline.Playhead);
        Timeline.SnappingEnabled = false;
        Timeline.SetPlayheadFromX(vm.Left + marker.Left + 4 * Timeline.PixelsPerSecond / 25.0);
        Assert.NotEqual(F(40), Timeline.Playhead);
    }

    // --- the Inspector -----------------------------------------------------------------------------------------------

    [Fact]
    public void The_Inspector_shows_the_values_on_the_playheads_frame_and_sets_the_easing_of_the_keyframe_there()
    {
        var clip = AddVideo();
        Timeline.SetPlayhead(F(0));
        Timeline.ToggleKeyframeCommand.Execute(null);
        Timeline.SetPlayhead(F(100));
        Inspector.ScalePercent = 200;
        Timeline.SetPlayhead(F(50));
        Assert.Equal(150m, Inspector.ScalePercent);
        Assert.False(Inspector.IsOnKeyframe);

        Timeline.SetPlayhead(F(0));
        Assert.True(Inspector.IsOnKeyframe);
        Inspector.KeyframeEasingIndex = (int)KeyframeEasing.EaseIn;
        Assert.Equal(KeyframeEasing.EaseIn, clip.Keyframes!.Keys[0].Easing);
        Assert.True(Shown(50).Scale < 1.5);                                     // eased in: slower at the start
        Assert.Contains("2", Inspector.KeyframeOwnerText);
    }

    [Fact]
    public void Previous_and_next_put_the_playhead_on_the_keyframes()
    {
        AddVideo();
        foreach (var frame in new long[] { 5, 30, 80 })
        {
            Timeline.SetPlayhead(F(frame));
            Timeline.ToggleKeyframeCommand.Execute(null);
        }
        Timeline.SetPlayhead(F(50));
        Inspector.NextKeyframeCommand.Execute(null);
        Assert.Equal(F(80), Timeline.Playhead);
        Inspector.PreviousKeyframeCommand.Execute(null);
        Inspector.PreviousKeyframeCommand.Execute(null);
        Assert.Equal(F(5), Timeline.Playhead);
    }

    // --- the Preview ---------------------------------------------------------------------------------------------------

    [Fact]
    public void A_Preview_move_of_a_keyframed_clip_writes_its_keyframe_on_the_frame_in_one_undo_step()
    {
        var clip = AddVideo();
        Timeline.SetPlayhead(F(0));
        Timeline.ToggleKeyframeCommand.Execute(null);
        Timeline.SetPlayhead(F(50));
        var steps = Steps();
        Tool.IsMoveOn = true;
        Assert.True(Tool.BeginMove(new PointD(960, 540)));
        Tool.UpdateGesture(new PointD(1000, 560), keepAxis: false);
        Tool.UpdateGesture(new PointD(1100, 600), keepAxis: false);
        Tool.EndGesture();

        Assert.Equal(2, clip.Keyframes!.Count);
        Assert.Equal((140.0, 60.0), (Shown(50).PositionX, Shown(50).PositionY));
        Assert.Equal((0.0, 0.0), (clip.PositionX, clip.PositionY));            // the static values untouched
        Assert.Equal(steps + 1, Steps());
    }

    [Fact]
    public void Esc_during_a_Preview_move_of_a_keyframed_clip_leaves_no_step_and_no_keyframe()
    {
        var clip = AddVideo();
        Timeline.ToggleKeyframeCommand.Execute(null);
        Timeline.SetPlayhead(F(60));
        var keys = clip.Keyframes;
        var steps = Steps();
        Tool.IsMoveOn = true;
        Assert.True(Tool.BeginMove(new PointD(960, 540)));
        Tool.UpdateGesture(new PointD(1100, 600), keepAxis: false);
        Tool.CancelGesture();
        Assert.Equal(keys, clip.Keyframes);
        Assert.Equal(steps, Steps());
    }

    [Fact]
    public void A_move_near_a_canvas_edge_snaps_exactly_onto_it_and_releases_beyond_the_threshold()
    {
        var clip = AddVideo();
        _edit.SetClipProperties(clip.Id, new Core.Interfaces.ClipPropertyChange { Visual = VisualProperties.Of(clip)!.Value with { Scale = 1.2 } });
        Tool.ScreenPixelsPerCanvasPixel = 0.5;                                   // a Preview at half size: 8 screen px = 16 canvas px
        Tool.IsMoveOn = true;
        // Scaled 1.2 the picture spans −192 … 2112: its left edge is 192 px left of the canvas edge.
        Assert.True(Tool.BeginMove(new PointD(960, 540)));
        Tool.UpdateGesture(new PointD(960 + 180, 540), keepAxis: false);          // the edge at −12: within 16 → snapped
        Assert.Equal(CropEdges.Left, Tool.SnappedEdges);
        var geometry = CompositionMath.Layout(new FrameSize(1920, 1080), new FrameSize(1920, 1080), VisualProperties.Of(clip)!.Value);
        Assert.InRange(geometry.Bounds.X, -1e-9, 0);
        Assert.True(geometry.CoversCanvas || geometry.Bounds.Y > 0);             // the left edge reaches the canvas edge exactly

        Tool.UpdateGesture(new PointD(960 + 160, 540), keepAxis: false);          // the edge at −32: beyond the threshold
        Assert.Equal(CropEdges.None, Tool.SnappedEdges);
        Assert.Equal(160, clip.PositionX);
        Tool.EndGesture();
        Assert.Equal(CropEdges.None, Tool.SnappedEdges);
    }

    [Fact]
    public void A_corner_scale_snaps_the_picture_onto_the_canvas_edges_exactly()
    {
        var clip = AddVideo();
        Tool.ScreenPixelsPerCanvasPixel = 1;
        Tool.IsMoveOn = true;
        // At 99.5 % the picture leaves a 4.8 px black band left and right; a corner drag to there snaps to fill the canvas.
        var corner = Tool.Target!.Frame.HandlePoint(CropEdges.Right | CropEdges.Bottom);
        Assert.True(Tool.BeginScale(CropEdges.Right | CropEdges.Bottom, corner));
        Tool.UpdateGesture(new PointD(960 + (corner.X - 960) * 0.995, 540 + (corner.Y - 540) * 0.995), keepAxis: false);
        Assert.NotEqual(CropEdges.None, Tool.SnappedEdges);
        Tool.EndGesture();
        var geometry = CompositionMath.Layout(new FrameSize(1920, 1080), new FrameSize(1920, 1080), VisualProperties.Of(clip)!.Value);
        Assert.True(geometry.CoversCanvas);
        Assert.Equal(1.0, clip.Scale);
    }

    // --- the review's regressions ------------------------------------------------------------------------------------

    [Fact]
    public void A_climber_drag_on_a_part_with_keyframes_writes_start_plus_the_drag_in_one_step_and_Esc_restores_it()
    {
        var clip = AddVideo();
        var keys = new TransformKeyframes(System.Collections.Immutable.ImmutableArray.Create(new TransformKeyframe(Guid.NewGuid(), MediaTime.Zero, 10, 20, 1.5)));
        Assert.True(_edit.SetClipFollow(clip.Id, FollowSettings.Of(new FollowPart(Keyframes: keys))).Success);
        Tool.FollowAt = (c, t) => (0, VisualProperties.Of(c)!.Value with { PositionX = 0 });
        var steps = Steps();
        Tool.IsMoveOn = true;
        Assert.Equal(0, Tool.Target!.FollowPart);
        Assert.True(Tool.BeginMove(new PointD(960, 540)));
        for (var i = 1; i <= 5; i++) Tool.UpdateGesture(new PointD(960 + 2 * i, 540 + i), keepAxis: false);   // 10 px right, 5 down in 5 moves
        Tool.EndGesture();
        var value = clip.Follow!.Parts[0].Keyframes!.At(new TransformKeyframeEditor(_edit, _projects).SourceAt(clip, 25));
        Assert.Equal((20.0, 25.0), (value.X, value.Y));                                    // exactly the start + the drag
        Assert.Equal(steps + 1, Steps());

        Tool.IsMoveOn = true;
        var before = clip.Follow;
        Assert.True(Tool.BeginMove(new PointD(960, 540)));
        Tool.UpdateGesture(new PointD(1000, 560), keepAxis: false);
        Tool.CancelGesture();
        Assert.Equal(before, clip.Follow);
    }

    [Fact]
    public void Fill_from_the_timelines_menu_writes_the_scale_into_the_keyframe_on_the_playheads_frame()
    {
        var clip = AddVideo();
        Timeline.SetPlayhead(F(0));
        Inspector.ScalePercent = 50;
        Timeline.ToggleKeyframeCommand.Execute(null);
        Timeline.SetPlayhead(F(100));
        Timeline.ToggleKeyframeCommand.Execute(null);
        Timeline.FillFrameCommand.Execute(ClipVm(clip));
        Assert.Equal(new[] { 0.5, 1.0 }, clip.Keyframes!.Keys.Select(k => k.Scale));       // the playhead's keyframe, not the first
    }

    [Fact]
    public void Inspector_edits_of_two_keyframes_are_two_undo_steps_and_a_scrub_of_one_is_one()
    {
        var clip = AddVideo();
        Timeline.SetPlayhead(F(0));
        Timeline.ToggleKeyframeCommand.Execute(null);
        var steps = Steps();
        Timeline.SetPlayhead(F(50));
        Inspector.PositionX = 100;                                                          // adds a keyframe at 50
        Inspector.PositionX = 120;                                                          // the same keyframe: the same step
        Timeline.SetPlayhead(F(100));
        Inspector.PositionX = 300;                                                          // another keyframe: its own step
        Assert.Equal(3, clip.Keyframes!.Count);
        Assert.Equal(steps + 2, Steps());
        _undo.Undo();
        Assert.Equal(2, clip.Keyframes!.Count);
        Assert.Equal(120, clip.Keyframes.Keys[1].X);
    }

    // --- D064 amendment 1: the same keyframes on sound ---------------------------------------------------------------

    [Fact]
    public void On_an_audio_clip_the_button_adds_a_volume_keyframe_with_a_diamond_and_Delete_removes_it()
    {
        var asset = new MediaAsset
        {
            FilePath = Path.Combine(_root, "media", "music.wav"), Kind = MediaKind.Audio, AnalysisStatus = MediaAnalysisStatus.Completed,
            Metadata = new MediaMetadata { Duration = MediaTime.FromSeconds(10), AudioCodec = "pcm" }
        };
        _projects.AddMediaAssets(new[] { asset });
        Timeline.AddMedia(asset);
        var clip = _projects.Current.Timeline.AudioTracks.SelectMany(t => t.Clips).OfType<AudioClip>().Single();
        Timeline.SelectClipFromPreview(clip.Id);
        Timeline.SetPlayhead(F(25));

        Assert.True(Timeline.ToggleKeyframeCommand.CanExecute(null));
        Timeline.ToggleKeyframeCommand.Execute(null);
        var point = Assert.Single(clip.Volumes!.Points);
        Assert.Equal(1, point.Gain);                                              // the line's value: nothing changes yet
        var marker = Assert.Single(ClipVm(clip).KeyframeMarkers);
        Assert.Equal(point.Id, marker.Id);

        Timeline.ToggleKeyframeCommand.Execute(null);                             // a second press on the same frame removes it
        Assert.Null(clip.Volumes);
        Assert.Empty(ClipVm(clip).KeyframeMarkers);

        Timeline.ToggleKeyframeCommand.Execute(null);
        Assert.True(Timeline.BeginKeyframeDrag(ClipVm(clip), ClipVm(clip).KeyframeMarkers[0], 0));
        Timeline.EndKeyframeDrag();
        Timeline.DeleteSelectedCommand.Execute(null);
        Assert.Null(clip.Volumes);
        Assert.Single(_projects.Current.Timeline.AudioTracks[0].Clips);          // the clip stays
    }

    [Fact]
    public void On_a_video_clip_with_sound_the_keyframe_brings_its_volume_point()
    {
        var clip = AddVideo();
        _projects.Current.MediaAssets.Single().Metadata!.AudioCodec = "aac";
        Timeline.ToggleKeyframeCommand.Execute(null);
        var key = Assert.Single(clip.Keyframes!.Keys);
        var point = Assert.Single(clip.Volumes!.Points);
        Assert.Equal((key.Id, key.Time), (point.Id, point.Time));
    }
}