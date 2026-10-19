using System.Collections.Immutable;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Composition;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Core.Playback;
using AiVideoEditor.Project;
using AiVideoEditor.Timeline;
using AiVideoEditor.Timeline.Playback;
using AiVideoEditor.Timeline.Tests.Playback;
using AiVideoEditor.UI.Common;
using AiVideoEditor.UI.Rendering;
using AiVideoEditor.UI.Services;
using AiVideoEditor.UI.ViewModels;
using AiVideoEditor.UI.ViewModels.Panels;
using Avalonia;
using Avalonia.Input;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiVideoEditor.UI.Tests;

/// <summary>
/// Phase 23 (D038) through the real shell wiring: the Move / Crop tools (exclusive, off in Source and during an export),
/// the target (the primary selection under the playhead, its frame from the model), Move / Scale / Crop gestures through
/// the overlay's viewport mapping (a letterboxed view), one undo step per gesture, Esc and every other cancellation
/// leaving no step, the keys swallowed during a gesture, a locked track, playback, a save during a gesture, the click
/// selection of the topmost layer of a 2 × 2 composition, text clips (Move, no crop) and the Inspector following live.
/// </summary>
public sealed class PreviewTransformUiTests : IAsyncLifetime
{
    private static readonly FrameRate Rate = FrameRate.Fps25;
    private static readonly FrameSize Landscape = new(1920, 1080);

    /// <summary>A letterboxed Preview: 1000 × 1000 view pixels for the 1920 × 1080 canvas (0.5208 view px per canvas px).</summary>
    private static readonly Size View = new(1000, 1000);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "AiVideoEditorTests", Guid.NewGuid().ToString("N"));
    private readonly UndoRedoService _undo = new();
    private readonly ProjectService _projects;
    private readonly TimelineEditService _edit;
    private readonly PlaybackService _playback;
    private readonly StatusService _status = new();
    private readonly EditingLock _lock = new();
    private readonly ProjectFileWorkflow _files;
    private readonly MainWindowViewModel _vm;
    private bool _playing;

    public PreviewTransformUiTests()
    {
        _projects = new ProjectService(_undo, NullLogger<ProjectService>.Instance);
        _edit = new TimelineEditService(_projects, _undo, NullLogger<TimelineEditService>.Instance);
        _playback = new PlaybackService(new FakeVideoDecoder(), new FakeReferenceClock(), NullLogger<PlaybackService>.Instance,
            new PlaybackSettings { BufferFrames = 4 }, new FakeAudioDecoder(), new FakeAudioOutput());
        var analysis = new MediaAnalysisCoordinator(new NoAnalysis(), _projects, NullLogger<MediaAnalysisCoordinator>.Instance);
        var import = new MediaImportWorkflow(new ScriptedPicker(), new NoImport(), _projects, analysis, _status, NullLogger<MediaImportWorkflow>.Instance);
        _files = new ProjectFileWorkflow(_projects, analysis, new NullAutosave(), new ScriptedDialogs(), new ScriptedPicker(), _status, NullLogger<ProjectFileWorkflow>.Instance);

        _vm = new MainWindowViewModel(
            new ToolbarViewModel(_undo, _files, import, _status),
            new MediaBrowserViewModel(_projects, import, NullLogger<MediaBrowserViewModel>.Instance),
            new PreviewViewModel(_status, _playback, _projects, NullLogger<PreviewViewModel>.Instance),
            new InspectorViewModel(_edit, _status, editingLock: _lock),
            new TimelineViewModel(_projects, _edit, _status, NullLogger<TimelineViewModel>.Instance),
            _status, _files, _projects, NullLogger<MainWindowViewModel>.Instance,
            previewTransform: new PreviewTransformViewModel(_edit, _projects, _status, _lock, MeasureText));
        Tool.IsPlaying = () => _playing;
    }

    /// <summary>A text box of 20 canvas px per character × 50 px (the painter needs a running Avalonia).</summary>
    private static (double, double) MeasureText(TextProperties text) => (Math.Max(1, text.Text.Length * 20.0), 50);

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        await _playback.DisposeAsync();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private PreviewTransformViewModel Tool => _vm.PreviewTransform!;

    private MediaAsset Asset(string name, int width, int height)
    {
        var asset = new MediaAsset
        {
            FilePath = Path.Combine(_root, "media", name),
            Kind = MediaKind.Video,
            AnalysisStatus = MediaAnalysisStatus.Completed,
            Metadata = new MediaMetadata
            {
                Duration = MediaTime.FromSeconds(10), FrameRate = Rate, AvgFrameRate = Rate,
                Width = width, Height = height, DisplayWidth = width, DisplayHeight = height
            }
        };
        _projects.AddMediaAssets(new[] { asset });
        return asset;
    }

    private VideoClip AddVideo(string name = "v.mp4", int width = 1920, int height = 1080)
    {
        var asset = Asset(name, width, height);
        _vm.Timeline.AddMedia(asset);
        var clip = _projects.Current.Timeline.VideoTracks.SelectMany(t => t.Clips).OfType<VideoClip>().Single(c => c.MediaAssetId == asset.Id);
        _vm.Timeline.SetPlayhead(clip.TimelineStart + MediaTime.FromSeconds(1));
        return clip;
    }

    private static VisualProperties Visual(Clip clip) => VisualProperties.Of(clip)!.Value;

    /// <summary>The undo history's length (undone and redone; the selection, which an undone Add drops, put back).</summary>
    private int Steps()
    {
        var selected = Primary();
        var n = 0;
        while (_undo.CanUndo) { _undo.Undo(); n++; }
        for (var i = 0; i < n; i++) _undo.Redo();
        if (selected is { } id) _vm.Timeline.SelectClipFromPreview(id);
        return n;
    }

    // The overlay's mapping, as a press / drag would use it.
    private static Affine2D Viewport => PreviewTransformOverlay.Viewport(Landscape, View);
    private static PointD Canvas(double viewX, double viewY) => PreviewTransformOverlay.ToCanvas(Viewport, new Point(viewX, viewY));
    private static Point ViewOf(PointD canvas) => PreviewTransformOverlay.ToView(Viewport, canvas);

    private PointD Drag(PointD from, double viewDx, double viewDy)
    {
        var v = ViewOf(from);
        return Canvas(v.X + viewDx, v.Y + viewDy);
    }

    // --- tools ------------------------------------------------------------------------------------------------------

    [Fact]
    public void The_tools_are_off_by_default_and_exclude_each_other()
    {
        Assert.Equal(PreviewTool.None, Tool.Tool);
        Assert.True(Tool.IsAvailable);
        Tool.IsMoveOn = true;
        Assert.Equal(PreviewTool.Move, Tool.Tool);
        Tool.IsCropOn = true;
        Assert.Equal(PreviewTool.Crop, Tool.Tool);
        Assert.False(Tool.IsMoveOn);
        Tool.IsCropOn = false;
        Assert.False(Tool.IsActive);
        Assert.Null(Tool.ModeText);
    }

    [Fact]
    public void An_export_turns_the_tool_off_cancels_a_gesture_and_keeps_it_off()
    {
        var clip = AddVideo();
        var start = Visual(clip);
        var steps = Steps();
        Tool.IsMoveOn = true;
        Assert.True(Tool.BeginMove(new PointD(960, 540)));
        Tool.UpdateGesture(new PointD(1060, 540), keepAxis: false);
        Assert.Equal(100, Visual(clip).PositionX);

        using (_lock.Acquire())
        {
            Assert.False(Tool.IsActive);
            Assert.False(Tool.IsGestureActive);
            Assert.Equal(start, Visual(clip));
            Assert.Equal(steps, Steps());
            Assert.False(Tool.IsAvailable);
            Tool.IsMoveOn = true;
            Assert.False(Tool.IsMoveOn);
        }
        Assert.True(Tool.IsAvailable);
    }

    [Fact]
    public void The_source_mode_turns_the_tool_off()
    {
        AddVideo();
        Tool.IsCropOn = true;
        Tool.IsSourceMode = () => true;
        Tool.OnPreviewStateChanged();
        Assert.False(Tool.IsActive);
        Assert.False(Tool.IsAvailable);
    }

    // --- target -----------------------------------------------------------------------------------------------------

    [Fact]
    public void The_target_is_the_selected_clip_under_the_playhead()
    {
        var clip = AddVideo();
        Tool.IsMoveOn = true;
        Assert.Same(clip, Tool.Target!.Clip);
        Assert.Equal(PreviewTransformViewModel.MoveText, Tool.ModeText);
        Assert.Equal(new PointD(960, 540), Tool.Target.Frame.Center);

        _vm.Timeline.SetPlayhead(clip.TimelineEnd + MediaTime.FromSeconds(1));
        Assert.Null(Tool.Target);
        Assert.Equal(PreviewTransformViewModel.NotUnderPlayheadText, Tool.ModeText);

        _vm.Timeline.SetPlayhead(clip.TimelineStart);
        _vm.Timeline.ClearSelection();
        Assert.Null(Tool.Target);
        Assert.Equal(PreviewTransformViewModel.NoClipText, Tool.ModeText);
    }

    [Fact]
    public void A_picture_of_unknown_size_is_no_target()
    {
        var clip = AddVideo();
        var asset = _projects.Current.MediaAssets.Single();
        asset.Metadata!.DisplayWidth = null;
        asset.Metadata.DisplayHeight = null;
        Tool.IsMoveOn = true;
        Tool.Refresh();
        Assert.Null(Tool.Target);
        Assert.Equal(PreviewTransformViewModel.UnknownSizeText, Tool.ModeText);
        Assert.NotNull(clip);
    }

    // --- Move -------------------------------------------------------------------------------------------------------

    [Fact]
    public void Move_follows_the_pointer_in_whole_canvas_pixels_live_and_is_one_undo_step()
    {
        var clip = AddVideo();
        var steps = Steps();
        Tool.IsMoveOn = true;
        var at = Canvas(500, 500);   // the view's centre is the canvas centre
        Assert.Equal(new PointD(960, 540), at);

        Assert.True(Tool.BeginMove(at));
        Tool.UpdateGesture(Drag(at, 10, 0), keepAxis: false);       // 10 view px = 19.2 canvas px → 19
        Assert.Equal(19, Visual(clip).PositionX);
        Assert.Equal(19m, _vm.Inspector.PositionX);                // the Inspector follows live
        Tool.UpdateGesture(Drag(at, 52, -26), keepAxis: false);     // 99.84 / −49.92 → 100 / −50
        Assert.Equal(new PointD(100, -50), new PointD(Visual(clip).PositionX, Visual(clip).PositionY));
        Assert.StartsWith("Position X 100 px", Tool.ModeText);
        Tool.EndGesture();

        Assert.Equal(steps + 1, Steps());
        Assert.Equal("Move Clip", _undo.NextUndo!.Description);
        _undo.Undo();
        Assert.Equal(VisualProperties.Default, Visual(clip));
        _undo.Redo();
        Assert.Equal(100, Visual(clip).PositionX);
    }

    [Fact]
    public void Move_with_Shift_keeps_one_axis()
    {
        var clip = AddVideo();
        Tool.IsMoveOn = true;
        Assert.True(Tool.BeginMove(new PointD(960, 540)));
        Tool.UpdateGesture(new PointD(1100, 560), keepAxis: true);
        Assert.Equal((140, 0), (Visual(clip).PositionX, Visual(clip).PositionY));
        Tool.EndGesture();
    }

    [Fact]
    public void A_gesture_that_returns_to_its_start_leaves_no_step()
    {
        var clip = AddVideo();
        var steps = Steps();
        Tool.IsMoveOn = true;
        Tool.BeginMove(new PointD(960, 540));
        Tool.UpdateGesture(new PointD(1000, 540), keepAxis: false);
        Tool.UpdateGesture(new PointD(960.2, 540), keepAxis: false);
        Tool.EndGesture();
        Assert.Equal(VisualProperties.Default, Visual(clip));
        Assert.Equal(steps, Steps());
        Assert.False(_undo.CanRedo);
    }

    [Fact]
    public void Two_drags_are_two_steps_and_an_Inspector_edit_is_its_own()
    {
        var clip = AddVideo();
        var steps = Steps();
        _vm.Inspector.PositionX = 10;
        Tool.IsMoveOn = true;
        Tool.BeginMove(new PointD(960, 540));
        Tool.UpdateGesture(new PointD(980, 540), keepAxis: false);
        Tool.EndGesture();
        Tool.BeginMove(new PointD(960, 540));
        Tool.UpdateGesture(new PointD(1000, 540), keepAxis: false);
        Tool.EndGesture();
        _vm.Inspector.PositionX = 77;

        Assert.Equal(steps + 4, Steps());
        Assert.Equal(77, Visual(clip).PositionX);
        _undo.Undo();
        Assert.Equal(70, Visual(clip).PositionX);
        _undo.Undo();
        Assert.Equal(30, Visual(clip).PositionX);
        _undo.Undo();
        Assert.Equal(10, Visual(clip).PositionX);
    }

    // --- Esc and the other cancellations ------------------------------------------------------------------------------

    [Fact]
    public void Esc_cancels_a_gesture_without_a_trace_and_then_turns_the_tool_off()
    {
        var clip = AddVideo();
        var steps = Steps();
        Tool.IsMoveOn = true;
        Tool.BeginMove(new PointD(960, 540));
        Tool.UpdateGesture(new PointD(1200, 700), keepAxis: false);

        var esc = ShortcutRouter.CommandFor(_vm, Key.Escape, KeyModifiers.None);
        Assert.Same(Tool.EscapeCommand, esc);
        esc!.Execute(null);

        Assert.False(Tool.IsGestureActive);
        Assert.Equal(VisualProperties.Default, Visual(clip));
        Assert.Equal(steps, Steps());
        Assert.False(_undo.CanRedo);
        Assert.True(Tool.IsActive);

        ShortcutRouter.CommandFor(_vm, Key.Escape, KeyModifiers.None)!.Execute(null);
        Assert.False(Tool.IsActive);
        Assert.Same(clip, _vm.Timeline.Tracks.SelectMany(t => t.Clips).Single(c => c.IsSelected).Clip);   // the selection stays
    }

    [Theory]
    [InlineData(Key.Z, KeyModifiers.Control)]
    [InlineData(Key.Y, KeyModifiers.Control)]
    [InlineData(Key.S, KeyModifiers.Control)]
    [InlineData(Key.Space, KeyModifiers.None)]
    [InlineData(Key.Delete, KeyModifiers.None)]
    [InlineData(Key.Right, KeyModifiers.Alt)]
    public void Every_other_key_is_swallowed_during_a_gesture(Key key, KeyModifiers modifiers)
    {
        var clip = AddVideo();
        Tool.IsMoveOn = true;
        Tool.BeginMove(new PointD(960, 540));
        Tool.UpdateGesture(new PointD(1000, 540), keepAxis: false);

        Assert.True(ShortcutRouter.Handle(_vm, key, modifiers, null, null));
        Assert.True(Tool.IsGestureActive);
        Assert.Equal(40, Visual(clip).PositionX);
        Assert.Same(ShortcutRouter.Swallowed, ShortcutRouter.CommandFor(_vm, key, modifiers));
        Tool.EndGesture();
        Assert.NotSame(ShortcutRouter.Swallowed, ShortcutRouter.CommandFor(_vm, key, modifiers));
    }

    [Fact]
    public void Playback_cancels_a_gesture_hides_the_handles_and_keeps_the_tool()
    {
        var clip = AddVideo();
        Tool.IsMoveOn = true;
        Tool.BeginMove(new PointD(960, 540));
        Tool.UpdateGesture(new PointD(1000, 540), keepAxis: false);

        _playing = true;
        Tool.OnPreviewStateChanged();
        Assert.False(Tool.IsGestureActive);
        Assert.Equal(VisualProperties.Default, Visual(clip));
        Assert.True(Tool.IsActive);
        Assert.False(Tool.IsPaused);
        Assert.False(Tool.CanEdit);
        Assert.False(Tool.HasScaleHandles);
        Assert.False(Tool.BeginMove(new PointD(960, 540)));
        Assert.Equal(PreviewTransformViewModel.PlayingText, Tool.ModeText);

        _playing = false;
        Tool.OnPreviewStateChanged();
        Assert.True(Tool.CanEdit);
    }

    [Fact]
    public void Another_selection_cancels_a_gesture()
    {
        var a = AddVideo("a.mp4");
        var b = AddVideo("b.mp4");
        _vm.Timeline.SelectClipFromPreview(a.Id);
        _vm.Timeline.SetPlayhead(a.TimelineStart);
        Tool.IsMoveOn = true;
        Tool.BeginMove(new PointD(960, 540));
        Tool.UpdateGesture(new PointD(1000, 540), keepAxis: false);

        _vm.Timeline.SelectClipFromPreview(b.Id);
        Assert.False(Tool.IsGestureActive);
        Assert.Equal(VisualProperties.Default, Visual(a));
    }

    [Fact]
    public async Task A_save_during_a_gesture_cancels_it_first_and_the_save_point_is_a_completed_step()
    {
        var clip = AddVideo();
        Directory.CreateDirectory(_root);
        await _projects.SaveAsAsync(Path.Combine(_root, "project"));
        Tool.IsMoveOn = true;
        Tool.BeginMove(new PointD(960, 540));
        Tool.UpdateGesture(new PointD(1000, 540), keepAxis: false);
        Tool.EndGesture();                                              // a completed step: X = 40
        var steps = Steps();
        Tool.BeginMove(new PointD(960, 540));
        Tool.UpdateGesture(new PointD(1100, 540), keepAxis: false);     // X = 180, not completed

        Assert.True(await _files.SaveAsync());

        Assert.False(Tool.IsGestureActive);
        Assert.Equal(40, Visual(clip).PositionX);
        Assert.False(_projects.Current.IsDirty);
        Assert.Equal(steps, Steps());
        // A new gesture after the save is its own step and makes the project dirty; its undo makes it clean again.
        Tool.BeginMove(new PointD(960, 540));
        Tool.UpdateGesture(new PointD(980, 540), keepAxis: false);
        Tool.EndGesture();
        Assert.True(_projects.Current.IsDirty);
        _undo.Undo();
        Assert.False(_projects.Current.IsDirty);
        Assert.Equal(40, Visual(clip).PositionX);
    }

    // --- locked track -----------------------------------------------------------------------------------------------

    [Fact]
    public void A_locked_track_shows_the_frame_without_handles_and_refuses_gestures()
    {
        var clip = AddVideo();
        _projects.Current.Timeline.VideoTracks[0].IsLocked = true;
        Tool.IsMoveOn = true;
        Tool.Refresh();

        Assert.NotNull(Tool.Target);
        Assert.True(Tool.Target!.IsLocked);
        Assert.False(Tool.CanEdit);
        Assert.False(Tool.HasScaleHandles);
        Assert.False(Tool.BeginMove(new PointD(960, 540)));
        Assert.StartsWith("Track ", Tool.ModeText);
        Tool.ReportLocked();
        Assert.EndsWith("is locked.", _status.Message);
        Assert.Equal(VisualProperties.Default, Visual(clip));
    }

    // --- Scale ------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(0)]
    [InlineData(30)]
    public void A_corner_drag_scales_around_the_centre_and_never_moves_it(double rotation)
    {
        var clip = AddVideo();
        _vm.Inspector.Rotation = (decimal)rotation;
        _vm.Inspector.ScalePercent = 50;
        _vm.Inspector.PositionX = 200;
        Tool.IsMoveOn = true;
        Assert.True(Tool.HasScaleHandles);
        var frame = Tool.Target!.Frame;
        var corner = frame.HandlePoint(CropEdges.Right | CropEdges.Bottom);
        // A press 2 view px off the corner still grabs it (within the reach).
        var press = Drag(corner, -2, 1);
        Assert.Equal(CropEdges.Right | CropEdges.Bottom,
            PreviewTransformOverlay.HandleAt(Viewport, frame, ViewOf(press), cornersOnly: true));

        Assert.True(Tool.BeginScale(CropEdges.Right | CropEdges.Bottom, press));
        var center = frame.Center;
        var target = new PointD(center.X + 1.5 * (corner.X - center.X), center.Y + 1.5 * (corner.Y - center.Y));
        Tool.UpdateGesture(new PointD(target.X + (press.X - corner.X), target.Y + (press.Y - corner.Y)), keepAxis: false);
        Tool.EndGesture();

        var v = Visual(clip);
        Assert.Equal(0.75, v.Scale, 4);
        Assert.Equal(200, v.PositionX);
        Assert.Equal(0, v.PositionY);
        Assert.Equal(rotation, v.RotationDegrees);
        Assert.Equal(75m, Math.Round(_vm.Inspector.ScalePercent!.Value, 2));
        Assert.Equal("Scale Clip", _undo.NextUndo!.Description);
    }

    [Fact]
    public void Scaling_stops_at_the_limits()
    {
        var clip = AddVideo();
        Tool.IsMoveOn = true;
        var frame = Tool.Target!.Frame;
        var corner = frame.HandlePoint(CropEdges.Left | CropEdges.Top);
        Tool.BeginScale(CropEdges.Left | CropEdges.Top, corner);
        Tool.UpdateGesture(new PointD(-50_000, -30_000), keepAxis: false);
        Assert.Equal(ClipPropertyLimits.MaxScale, Visual(clip).Scale);
        Tool.UpdateGesture(new PointD(3000, 2000), keepAxis: false);   // past the centre
        Assert.Equal(ClipPropertyLimits.MinScale, Visual(clip).Scale);
        Tool.EndGesture();
        Assert.Equal(0, Visual(clip).PositionX);
    }

    [Fact]
    public void Crop_mode_has_no_scale_handles_and_Move_no_crop_handles()
    {
        AddVideo();
        Tool.IsMoveOn = true;
        Assert.True(Tool.HasScaleHandles);
        Assert.False(Tool.HasCropHandles);
        Assert.False(Tool.BeginCrop(CropEdges.Left, new PointD(0, 540)));
        Tool.IsCropOn = true;
        Assert.False(Tool.HasScaleHandles);
        Assert.True(Tool.HasCropHandles);
        Assert.False(Tool.BeginScale(CropEdges.Left | CropEdges.Top, new PointD(0, 0)));
        Assert.False(Tool.BeginMove(new PointD(960, 540)));
    }

    // --- Crop -------------------------------------------------------------------------------------------------------

    [Fact]
    public void A_crop_handle_drag_moves_only_its_edge_and_the_picture_stays_in_place()
    {
        var clip = AddVideo();
        var steps = Steps();
        Tool.IsCropOn = true;
        var frame = Tool.Target!.Frame;
        var right = frame.HandlePoint(CropEdges.Right);
        var leftHandle = frame.HandlePoint(CropEdges.Left);
        Assert.Equal(CropEdges.Left, PreviewTransformOverlay.HandleAt(Viewport, frame, ViewOf(leftHandle), cornersOnly: false));

        Assert.True(Tool.BeginCrop(CropEdges.Left, leftHandle));
        Tool.UpdateGesture(new PointD(480, 700), keepAxis: false);   // the vertical move is ignored for a left edge
        Tool.EndGesture();

        var v = Visual(clip);
        Assert.Equal(new CropRect(0.25, 0, 0, 0), v.Crop);
        Assert.Equal(240, v.PositionX);
        var after = Tool.Target!.Frame;
        Assert.Equal(new PointD(480, 540), after.HandlePoint(CropEdges.Left));
        Assert.Equal(right, after.HandlePoint(CropEdges.Right));
        Assert.Equal(25m, _vm.Inspector.CropLeftPercent);
        Assert.Equal(steps + 1, Steps());
        Assert.Equal("Crop Clip", _undo.NextUndo!.Description);
    }

    [Fact]
    public void Cropping_a_portrait_clip_on_a_landscape_canvas_keeps_its_size_on_the_screen()
    {
        var clip = AddVideo("phone.mp4", 1080, 1920);
        Tool.IsCropOn = true;
        var frame = Tool.Target!.Frame;
        var bottomRight = frame.HandlePoint(CropEdges.Right | CropEdges.Bottom);
        var top = frame.HandlePoint(CropEdges.Top);

        Tool.BeginCrop(CropEdges.Top, top);
        Tool.UpdateGesture(new PointD(top.X, top.Y + 270), keepAxis: false);   // 270 canvas px = 480 source px at 0.5625
        Tool.EndGesture();

        var v = Visual(clip);
        Assert.Equal(0.25, v.Crop.Top);
        var after = Tool.Target!.Frame;
        Assert.Equal(bottomRight, after.HandlePoint(CropEdges.Right | CropEdges.Bottom));
        Assert.Equal(top.Y + 270, after.HandlePoint(CropEdges.Top).Y, 9);
    }

    [Fact]
    public void A_corner_crop_of_a_rotated_clip_keeps_the_opposite_corner()
    {
        var clip = AddVideo();
        _vm.Inspector.Rotation = 30;
        _vm.Inspector.ScalePercent = 60;
        Tool.IsCropOn = true;
        var frame = Tool.Target!.Frame;
        var opposite = frame.HandlePoint(CropEdges.Right | CropEdges.Bottom);
        var corner = frame.HandlePoint(CropEdges.Left | CropEdges.Top);
        var inward = new PointD(corner.X + 0.2 * (frame.Center.X - corner.X), corner.Y + 0.2 * (frame.Center.Y - corner.Y));

        Tool.BeginCrop(CropEdges.Left | CropEdges.Top, corner);
        Tool.UpdateGesture(inward, keepAxis: false);
        Tool.EndGesture();

        var v = Visual(clip);
        Assert.Equal(0.1, v.Crop.Left, 3);   // a fifth of the half diagonal inward = a tenth of each side
        Assert.Equal(0.1, v.Crop.Top, 3);
        var after = Tool.Target!.Frame.HandlePoint(CropEdges.Right | CropEdges.Bottom);
        Assert.Equal(opposite.X, after.X, 7);
        Assert.Equal(opposite.Y, after.Y, 7);
        Assert.Equal(30, v.RotationDegrees);
    }

    [Fact]
    public void Esc_during_a_crop_restores_crop_scale_and_position_exactly()
    {
        var clip = AddVideo();
        _vm.Inspector.ScalePercent = 70;
        var start = Visual(clip);
        var steps = Steps();
        Tool.IsCropOn = true;
        Tool.BeginCrop(CropEdges.Right, Tool.Target!.Frame.HandlePoint(CropEdges.Right));
        Tool.UpdateGesture(new PointD(1000, 540), keepAxis: false);
        Assert.NotEqual(start, Visual(clip));
        Tool.EscapeCommand.Execute(null);
        Assert.Equal(start, Visual(clip));
        Assert.Equal(steps, Steps());
    }

    // --- hold markup after Move and Crop -------------------------------------------------------------------------------

    private static readonly ImmutableArray<NormalizedPoint> HoldSquare = ImmutableArray.Create(
        new NormalizedPoint(0.4, 0.4), new NormalizedPoint(0.6, 0.4), new NormalizedPoint(0.6, 0.6), new NormalizedPoint(0.4, 0.6));

    /// <summary>The Preview's draw plan at the playhead (the snapshot the Preview plays, a decoded frame of the source size)
    /// and its route outline: where the clip's first hold vertex lands on the canvas.</summary>
    private (OutlineDraw Outline, FrameDraw Frame, PointD FirstVertex) PreviewOutline(VideoClip clip)
    {
        var at = _vm.Timeline.Playhead;
        var layer = PlaybackSnapshotBuilder.Build(_projects.Current, 1).LayersAt(at).OfType<PictureLayer>().Single(l => l.ClipId == clip.Id);
        var decoded = new DecodedFrame(1920, 1080, 1920 * 4, new byte[1920 * 1080 * 4], new SourceTimestamp(0, new TimeBase(1, 25)));
        var plan = PreviewDrawPlan.Build(Landscape, 1920, 1080, new[] { new LayerPicture(layer, LayerPictureState.Frame, decoded) });
        var outline = plan.Operations.OfType<OutlineDraw>().Single();
        var frame = plan.Operations.OfType<FrameDraw>().Single();
        return (outline, frame, outline.Transform.Apply(outline.Polygons[0][0]));
    }

    [Fact]
    public async Task The_hold_markup_stays_and_follows_the_picture_after_a_Move_and_a_Crop_and_survives_a_save()
    {
        var clip = AddVideo();
        var hold = new Hold(Guid.NewGuid(), HoldSquare, RouteColor.HexToOkLab("#E53935"), RouteColor.HexToOkLab("#EEEEEE"), MediaTime.Zero);
        Assert.True(_edit.AddHold(clip.Id, hold).Success);
        var markup = clip.HoldMarkup!;
        Assert.Equal(new PointD(0.4 * 1920, 0.4 * 1080), PreviewOutline(clip).FirstVertex);   // (768, 432) on the canvas

        // Move by (100, −50): the markup is the same object, the outline moves with the picture.
        Tool.IsMoveOn = true;
        Tool.BeginMove(new PointD(960, 540));
        Tool.UpdateGesture(new PointD(1060, 490), keepAxis: false);
        Tool.EndGesture();
        Assert.Same(markup, clip.HoldMarkup);
        var moved = PreviewOutline(clip);
        Assert.Equal(new PointD(868, 382), moved.FirstVertex);
        Assert.Equal(moved.Frame.Transform, moved.Outline.Transform);   // drawn through the frame's own transform

        // Crop the left quarter (the hold's left edge at x = 0.4 stays visible): anchored — the outline stays where it was,
        // clipped to the new crop rectangle.
        Tool.IsCropOn = true;
        Tool.BeginCrop(CropEdges.Left, Tool.Target!.Frame.HandlePoint(CropEdges.Left));
        Tool.UpdateGesture(new PointD(580, 490), keepAxis: false);      // canvas x 100 + 480 = source x 480
        Tool.EndGesture();
        Assert.Equal(new CropRect(0.25, 0, 0, 0), Visual(clip).Crop);
        Assert.Same(markup, clip.HoldMarkup);
        var cropped = PreviewOutline(clip);
        Assert.Equal(868, cropped.FirstVertex.X, 9);
        Assert.Equal(382, cropped.FirstVertex.Y, 9);
        Assert.Equal(cropped.Frame.LocalRect, cropped.Outline.LocalRect);   // clipped to the crop
        Assert.Equal(hold.Id, markup.Holds.Single().Id);
        Assert.Equal(HoldSquare.AsEnumerable(), markup.Holds.Single().Points.AsEnumerable());

        // Saved and opened again: the same hold, the same picture properties, the same outline place.
        var folder = Path.Combine(_root, "holds");
        Directory.CreateDirectory(_root);
        await _projects.SaveAsAsync(folder);
        var visual = Visual(clip);
        await _projects.OpenAsync(folder);
        var reopened = _projects.Current.Timeline.VideoTracks.SelectMany(t => t.Clips).OfType<VideoClip>().Single();
        Assert.Equal(visual, Visual(reopened));
        Assert.Equal(hold.Id, reopened.HoldMarkup!.Holds.Single().Id);
        Assert.Equal(HoldSquare.AsEnumerable(), reopened.HoldMarkup.Holds.Single().Points.AsEnumerable());
        var after = PreviewOutline(reopened);
        Assert.Equal(868, after.FirstVertex.X, 9);
        Assert.Equal(382, after.FirstVertex.Y, 9);
    }

    [Fact]
    public void Undoing_a_Move_and_a_Crop_leaves_the_hold_markup_untouched()
    {
        var clip = AddVideo();
        Assert.True(_edit.AddHold(clip.Id,
            new Hold(Guid.NewGuid(), HoldSquare, RouteColor.HexToOkLab("#1E88E5"), RouteColor.HexToOkLab("#EEEEEE"), MediaTime.Zero)).Success);
        var markup = clip.HoldMarkup!;
        Tool.IsMoveOn = true;
        Tool.BeginMove(new PointD(960, 540));
        Tool.UpdateGesture(new PointD(1000, 540), keepAxis: false);
        Tool.EndGesture();
        Tool.IsCropOn = true;
        Tool.BeginCrop(CropEdges.Top, Tool.Target!.Frame.HandlePoint(CropEdges.Top));
        Tool.UpdateGesture(new PointD(1000, 108), keepAxis: false);
        Tool.EndGesture();

        _undo.Undo();
        _undo.Undo();
        Assert.Equal(VisualProperties.Default, Visual(clip));
        Assert.Same(markup, clip.HoldMarkup);
        Assert.Equal(new PointD(768, 432), PreviewOutline(clip).FirstVertex);
        _undo.Redo();
        _undo.Redo();
        Assert.Same(markup, clip.HoldMarkup);
        Assert.Equal(new PointD(808, 432), PreviewOutline(clip).FirstVertex);   // moved by 40, the top crop anchored
    }

    // --- text -------------------------------------------------------------------------------------------------------

    [Fact]
    public void A_text_clip_moves_and_scales_but_has_no_crop()
    {
        AddVideo();
        _vm.Timeline.SetPlayhead(MediaTime.Zero);
        Assert.True(_edit.AddTextClip(MediaTime.Zero).Success);
        var text = _projects.Current.Timeline.VideoTracks.SelectMany(t => t.Clips).OfType<TextClip>().Single();
        _vm.Timeline.SelectClipFromPreview(text.Id);
        Tool.IsMoveOn = true;

        Assert.True(Tool.Target!.IsText);
        Assert.Equal(new RectD(-40, -25, 80, 50), Tool.Target.Frame.LocalRect);   // "Text": 4 × 20 px
        Assert.True(Tool.BeginMove(new PointD(960, 540)));
        Tool.UpdateGesture(new PointD(900, 600), keepAxis: false);
        Tool.EndGesture();
        Assert.Equal((-60, 60), (Visual(text).PositionX, Visual(text).PositionY));

        // Scale by the bottom-right corner, dragged to twice its distance from the centre (900, 600): the text doubles
        // around its centre, the position stays; one undo step; no crop appears.
        var steps = Steps();
        var frame = Tool.Target!.Frame;
        Assert.Equal(new PointD(900, 600), frame.Center);
        var corner = frame.HandlePoint(CropEdges.Right | CropEdges.Bottom);
        Assert.Equal(new PointD(940, 625), corner);
        Assert.True(Tool.BeginScale(CropEdges.Right | CropEdges.Bottom, corner));
        Tool.UpdateGesture(new PointD(960, 637.5), keepAxis: false);   // ×1.5 on the way
        Assert.Equal(1.5, Visual(text).Scale);
        Tool.UpdateGesture(new PointD(980, 650), keepAxis: false);     // ×2
        Tool.EndGesture();

        var scaled = Visual(text);
        Assert.Equal(VisualProperties.Default with { PositionX = -60, PositionY = 60, Scale = 2 }, scaled);
        Assert.Equal(CropRect.None, scaled.Crop);
        Assert.Equal(new PointD(980, 650), Tool.Target!.Frame.HandlePoint(CropEdges.Right | CropEdges.Bottom));
        Assert.Equal(new PointD(900, 600), Tool.Target.Frame.Center);
        Assert.Equal(200m, _vm.Inspector.ScalePercent);
        Assert.Equal(steps + 1, Steps());
        Assert.Equal("Scale Clip", _undo.NextUndo!.Description);

        _undo.Undo();
        Assert.Equal(VisualProperties.Default with { PositionX = -60, PositionY = 60 }, Visual(text));   // the move stays
        _undo.Redo();
        Assert.Equal(scaled, Visual(text));
        Assert.Equal(CropRect.None, Visual(text).Crop);

        Tool.IsCropOn = true;
        Assert.False(Tool.HasCropHandles);
        Assert.Equal(PreviewTransformViewModel.TextHasNoCrop, Tool.ModeText);
        Assert.False(Tool.BeginCrop(CropEdges.Left, new PointD(0, 0)));
    }

    // --- click selection in a 2 × 2 composition ------------------------------------------------------------------------

    private List<VideoClip> TwoByTwo()
    {
        var clips = new List<VideoClip>();
        for (var i = 0; i < 4; i++)
        {
            if (i > 0) Assert.True(_edit.AddTrack(TrackType.Video).Success);
            var asset = Asset($"q{i}.mp4", 1920, 1080);
            var track = _projects.Current.Timeline.VideoTracks[^1];
            Assert.True(_edit.AddClip(asset.Id, track.Id, MediaTime.Zero).Success);
            var clip = (VideoClip)track.Clips.Single();
            var visual = VisualProperties.Default with { Scale = 0.5, PositionX = i % 2 == 0 ? -480 : 480, PositionY = i < 2 ? -270 : 270 };
            Assert.True(_edit.SetClipProperties(clip.Id, new ClipPropertyChange { Visual = visual }).Success);
            clips.Add(clip);
        }
        _vm.Timeline.SetPlayhead(MediaTime.FromSeconds(1));
        return clips;
    }

    private ImmutableArray<LayerPicture> Layers()
    {
        var snapshot = PlaybackSnapshotBuilder.Build(_projects.Current, 1);
        return snapshot.LayersAt(MediaTime.FromSeconds(1)).Select(l => new LayerPicture(l, LayerPictureState.Pending)).ToImmutableArray();
    }

    [Fact]
    public void A_click_on_a_quarter_of_a_2x2_composition_selects_its_clip()
    {
        var clips = TwoByTwo();
        Tool.IsMoveOn = true;
        var layers = Layers();
        Assert.Equal(4, layers.Length);

        foreach (var (clip, x, y) in new[] { (clips[0], 480.0, 270.0), (clips[1], 1440.0, 270.0), (clips[2], 480.0, 810.0), (clips[3], 1440.0, 810.0) })
        {
            var id = PreviewTransformOverlay.LayerAt(Landscape, layers, new PointD(x, y));
            Assert.Equal(clip.Id, id);
            Assert.True(Tool.SelectLayer(id!.Value));
            Assert.Same(clip, Tool.Target!.Clip);
            Assert.Equal(clip.Id, Primary());
            Assert.Contains("q", _vm.Inspector.ClipName);
        }
        Assert.Null(PreviewTransformOverlay.LayerAt(Landscape, layers, new PointD(-10, 540)));
    }

    [Fact]
    public void The_topmost_layer_under_the_pointer_wins_and_its_drag_moves_only_it()
    {
        var clips = TwoByTwo();
        // The top clip (V4) moved over the centre covers part of V1's quarter.
        Assert.True(_edit.SetClipProperties(clips[3].Id, new ClipPropertyChange { Visual = Visual(clips[3]) with { PositionX = 0, PositionY = 0 } }).Success);
        Tool.IsMoveOn = true;
        var layers = Layers();
        Assert.Equal(clips[3].Id, PreviewTransformOverlay.LayerAt(Landscape, layers, new PointD(800, 400)));   // both there: the top
        Assert.Equal(clips[0].Id, PreviewTransformOverlay.LayerAt(Landscape, layers, new PointD(300, 200)));

        Tool.SelectLayer(clips[3].Id);
        Assert.True(Tool.BeginMove(new PointD(800, 400)));
        Tool.UpdateGesture(new PointD(850, 400), keepAxis: false);
        Tool.EndGesture();
        Assert.Equal(50, Visual(clips[3]).PositionX);
        Assert.Equal(-480, Visual(clips[0]).PositionX);
    }

    [Fact]
    public void A_click_on_a_picture_does_nothing_while_playing_or_with_no_tool()
    {
        var clips = TwoByTwo();
        _vm.Timeline.SelectClipFromPreview(clips[0].Id);
        Assert.False(Tool.SelectLayer(clips[1].Id));           // no tool
        Tool.IsMoveOn = true;
        _playing = true;
        Tool.OnPreviewStateChanged();
        Assert.False(Tool.SelectLayer(clips[1].Id));           // playing
        Assert.Same(clips[0], Tool.Target?.Clip ?? clips[0]);
    }

    // --- viewport -----------------------------------------------------------------------------------------------------

    [Fact]
    public void The_overlay_maps_a_letterboxed_view_onto_the_canvas()
    {
        // 1000 × 1000 view, 1920 × 1080 canvas: scale 1000 / 1920, the picture 562.5 px high, centred vertically.
        AssertNear(new PointD(0, 0), Canvas(0, (1000 - 562.5) / 2));
        AssertNear(new PointD(1920, 1080), Canvas(1000, (1000 + 562.5) / 2));
        Assert.Equal(new Point(500, 500), ViewOf(new PointD(960, 540)));
    }

    [Fact]
    public void The_overlay_reaches_over_the_margin_and_maps_the_canvas_as_the_composition_does()
    {
        // The overlay is 16 px larger on every side than the composition's 1000 × 1000 area.
        var inset = PreviewTransformOverlay.ViewportIn(Landscape, new Size(1000 + 2 * PreviewTransformOverlay.Inset, 1000 + 2 * PreviewTransformOverlay.Inset));
        var composition = CompositionDrawPlan.Viewport(Landscape, 1000, 1000);
        foreach (var p in new[] { new PointD(0, 0), new PointD(1920, 1080), new PointD(960, 540) })
        {
            var a = inset.Apply(p);
            var b = composition.Apply(p);
            Assert.Equal(b.X + PreviewTransformOverlay.Inset, a.X, 9);
            Assert.Equal(b.Y + PreviewTransformOverlay.Inset, a.Y, 9);
        }
    }

    private static void AssertNear(PointD expected, PointD actual)
    {
        Assert.Equal(expected.X, actual.X, 9);
        Assert.Equal(expected.Y, actual.Y, 9);
    }

    /// <summary>A selected clip of the timeline (with a link partner, the last one listed).</summary>
    private Guid? Primary() => _vm.Timeline.Tracks.SelectMany(t => t.Clips).Where(c => c.IsSelected).Select(c => (Guid?)c.Id).LastOrDefault();

    private sealed class NoAnalysis : IMediaAnalysisService
    {
        public Task<MediaAnalysisResult> AnalyzeAsync(string filePath, CancellationToken ct = default) =>
            Task.FromResult(MediaAnalysisResult.Failure(MediaAnalysisOutcome.ProbeToolUnavailable, "test"));
    }

    private sealed class NoImport : IMediaImportService
    {
        public IReadOnlySet<string> SupportedExtensions { get; } = new HashSet<string>();
        public Task<MediaImportBatchResult> ImportManyAsync(IEnumerable<string> filePaths, CancellationToken ct = default) =>
            Task.FromResult(new MediaImportBatchResult());
    }
}
