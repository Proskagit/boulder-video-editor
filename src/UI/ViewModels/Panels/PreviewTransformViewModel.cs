using System.Globalization;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Composition;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Core.Localization;
using AiVideoEditor.Core.Playback;
using AiVideoEditor.UI.Localization;
using AiVideoEditor.UI.Rendering;
using AiVideoEditor.UI.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AiVideoEditor.UI.ViewModels.Panels;

/// <summary>The Preview's direct-manipulation tools (D038 §1).</summary>
public enum PreviewTool
{
    None,
    Move,
    Crop
}

/// <summary>What a running Preview gesture changes (D038 §3).</summary>
public enum TransformGestureKind
{
    Move,
    Scale,
    Crop,

    /// <summary>D061 amendment 1: on a followed part, the drag moves the person's place in the frame (the part's offset).</summary>
    FollowOffset
}

/// <summary>The clip the tools act on (D038 §2): the primary selected clip under the playhead, its frame on the canvas
/// from the model (a picture's visible part through its layout, or a text box), and whether its track is locked.</summary>
public sealed record TransformTarget(Clip Clip, Track Track, VisualProperties Visual, TransformFrame Frame, LayerGeometry? Geometry)
{
    public bool IsText => Clip is TextClip;
    public bool IsLocked => Track.IsLocked;

    /// <summary>D061 amendment 1: the followed part the camera follows on the shown frame (its index in the clip's Follow) —
    /// the frame is then where Follow puts the picture, and a drag moves the person in the frame; null otherwise.</summary>
    public int? FollowPart { get; init; }

    /// <summary>D063: the timeline frame the target is shown on (the playhead's) — the frame its keyframes are edited on.</summary>
    public long TimelineFrame { get; init; }

    private readonly VisualProperties? _placed;

    /// <summary>Where the picture is drawn on the frame: <see cref="Visual"/>, or with Follow the framing there.</summary>
    public VisualProperties Placed
    {
        get => _placed ?? Visual;
        init => _placed = value;
    }

    /// <summary>The picture's source size (null for a text).</summary>
    public FrameSize? Source { get; init; }
}

/// <summary>
/// The Preview's Move and Crop tools (DECISIONS.md D038). Move drags the selected clip (its position) and, by the frame's
/// corner handles, scales it proportionally around its centre; Crop drags the eight handles of the crop rectangle with an
/// anchored crop — the visible picture keeps its place (<see cref="PreviewTransformMath"/>). A click on another layer selects
/// it (<see cref="SelectClip"/>, the timeline's selection). The model changes live through
/// <see cref="ITimelineEditService.SetClipProperties"/>, one <see cref="ClipPropertyGesture"/> per gesture, so a gesture is
/// one undo step; Esc, a lost pointer, playback, another selection, the Source mode, an export or a save cancel it by
/// writing its start back (no step left). Gestures work only while paused on the timeline, never during an export
/// (<see cref="EditingLock"/>) or on a locked track. Everything here is in canvas pixels; the overlay maps the pointer.
/// </summary>
public sealed partial class PreviewTransformViewModel : ViewModelBase
{
    private readonly ITimelineEditService _edit;
    private readonly IProjectService _projects;
    private readonly StatusService _status;
    private readonly EditingLock _lock;
    private readonly Func<TextProperties, (double Width, double Height)> _measureText;

    private Guid? _clipId;
    private bool _syncingTool;
    private Gesture? _gesture;

    public PreviewTransformViewModel(ITimelineEditService edit, IProjectService projects, StatusService status,
        EditingLock? editingLock = null, Func<TextProperties, (double Width, double Height)>? measureText = null,
        IUserSettingsService? userSettings = null)
    {
        _edit = edit;
        _projects = projects;
        _status = status;
        _lock = editingLock ?? new EditingLock();
        _measureText = measureText ?? MeasureWithPainter;
        _keyframes = new TransformKeyframeEditor(edit, projects);
        if (userSettings is not null) SnapThresholdPixels = () => userSettings.Current.CanvasSnapPixels;

        _projects.TimelineChanged += (_, _) => Refresh();
        _projects.ProjectChanged += (_, _) => { DropGesture(); _clipId = null; Refresh(); };
        _lock.PropertyChanged += (_, _) =>
        {
            if (_lock.IsLocked) TurnOff();
            UpdateAvailability();
        };
        UpdateAvailability();
        Refresh();
    }

    // ---- the shell's wiring -----------------------------------------------------------------------------------------

    /// <summary>The timeline playhead.</summary>
    public Func<MediaTime> Playhead { get; set; } = () => MediaTime.Zero;

    /// <summary>Whether the Preview plays now.</summary>
    public Func<bool> IsPlaying { get; set; } = () => false;

    /// <summary>Whether the Preview shows the Source viewer (no tools there, D038 §1).</summary>
    public Func<bool> IsSourceMode { get; set; } = () => false;

    /// <summary>Selects the clip in the timeline as a click on it there would (D038 §2); null when nothing can select.</summary>
    public Action<Guid>? SelectClip { get; set; }

    /// <summary>D061 amendment 1: the followed part the camera follows on source time of a clip and where Follow puts the
    /// picture there (D063: its position and scale); null — no Follow there (set by the main window).</summary>
    public Func<VideoClip, MediaTime, (int Part, VisualProperties Visual)?>? FollowAt { get; set; }

    /// <summary>D063 §B: the snap threshold in screen pixels (the user's setting).</summary>
    public Func<double> SnapThresholdPixels { get; set; } = () => CanvasSnap.DefaultThresholdPixels;

    /// <summary>D063 §B: screen pixels per canvas pixel of the Preview now (set by the overlay as it draws), so the threshold
    /// stays the same on the screen at any Preview size.</summary>
    public double ScreenPixelsPerCanvasPixel { get; set; } = 1;

    /// <summary>D063 §B: the canvas edges the running gesture snapped the picture to (the overlay highlights them).</summary>
    [ObservableProperty] private CropEdges _snappedEdges;

    private readonly TransformKeyframeEditor _keyframes;

    /// <summary>Another mode of the Preview takes its clicks now (the hold markup, the climber's pick); set by the main window.</summary>
    public Func<bool> OtherModeActive { get; set; } = () => false;

    /// <summary>The clock of the wheel's scale steps (tests).</summary>
    public Func<DateTime> Now { get; set; } = () => DateTime.UtcNow;

    /// <summary>How long after its last notch a wheel scale is still the same undo step.</summary>
    public static readonly TimeSpan WheelStepWindow = TimeSpan.FromMilliseconds(700);

    private ClipPropertyGesture? _wheelUndo;
    private Guid? _wheelClip;
    private DateTime _wheelLast;

    // ---- the tools --------------------------------------------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Tool), nameof(IsActive), nameof(HasCropHandles), nameof(HasScaleHandles))]
    private bool _isMoveOn;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Tool), nameof(IsActive), nameof(HasCropHandles), nameof(HasScaleHandles))]
    private bool _isCropOn;

    /// <summary>Whether the tools can be turned on: the Timeline mode and no export.</summary>
    [ObservableProperty] private bool _isAvailable;

    public PreviewTool Tool => IsMoveOn ? PreviewTool.Move : IsCropOn ? PreviewTool.Crop : PreviewTool.None;

    public bool IsActive => Tool != PreviewTool.None;

    partial void OnIsMoveOnChanged(bool value) => OnToolToggled(value, () => IsCropOn = false);
    partial void OnIsCropOnChanged(bool value) => OnToolToggled(value, () => IsMoveOn = false);

    private void OnToolToggled(bool on, Action turnOffOther)
    {
        if (_syncingTool) return;
        _syncingTool = true;
        try
        {
            CancelGesture();
            if (on && !IsAvailable)
            {
                IsMoveOn = IsCropOn = false;   // the toggle cannot stay on
            }
            else if (on)
            {
                turnOffOther();
            }
        }
        finally
        {
            _syncingTool = false;
        }
        Refresh();
    }

    /// <summary>Turns the tool off (a running gesture is cancelled first).</summary>
    public void TurnOff()
    {
        CancelGesture();
        IsMoveOn = false;
        IsCropOn = false;
    }

    /// <summary>Esc: cancels a running gesture, otherwise turns the tool off.</summary>
    [RelayCommand]
    private void Escape()
    {
        if (_gesture is not null) CancelGesture();
        else TurnOff();
    }

    // ---- the target -------------------------------------------------------------------------------------------------

    /// <summary>The clip the tools act on now, or null (<see cref="ModeText"/> says why).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCropHandles), nameof(HasScaleHandles), nameof(CanEdit))]
    private TransformTarget? _target;

    /// <summary>The project canvas the target's frame is on.</summary>
    [ObservableProperty] private FrameSize _canvas = new(1920, 1080);

    /// <summary>The bar's text while a tool is on: what the tool does, why it cannot act, or the gesture's values.</summary>
    [ObservableProperty] private string? _modeText;

    /// <summary>Whether gestures are possible now (paused on the timeline; the frame and handles are drawn only then).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEdit), nameof(HasCropHandles), nameof(HasScaleHandles))]
    private bool _isPaused = true;

    /// <summary>A gesture may start on the target now.</summary>
    public bool CanEdit => IsActive && IsPaused && !_lock.IsLocked && Target is { IsLocked: false };

    /// <summary>Move shows the frame's four corner handles (Scale, D038 §3).</summary>
    public bool HasScaleHandles => Tool == PreviewTool.Move && CanEdit;

    /// <summary>Crop shows the eight crop handles — pictures only.</summary>
    public bool HasCropHandles => Tool == PreviewTool.Crop && CanEdit && Target is { IsText: false, Geometry: not null, FollowPart: null };

    public bool IsGestureActive => _gesture is not null;

    public TransformGestureKind? GestureKind => _gesture?.Kind;

    /// <summary>The handle of a running Scale / Crop gesture.</summary>
    public CropEdges GestureHandle => _gesture?.Handle ?? CropEdges.None;

    /// <summary>The shell tells: the timeline's primary selected clip (null: none, a dissolve or media).</summary>
    public void SetClip(Clip? clip)
    {
        if (clip?.Id != _clipId) CancelGesture();
        _clipId = clip?.Id;
        Refresh();
    }

    /// <summary>The shell tells: the playhead moved.</summary>
    public void OnPlayheadMoved() => Refresh();

    /// <summary>The shell tells: playback started / stopped or the Preview switched between Timeline and Source.</summary>
    public void OnPreviewStateChanged()
    {
        if (IsSourceMode()) TurnOff();
        else if (IsPlaying()) CancelGesture();
        UpdateAvailability();
        Refresh();
    }

    private void UpdateAvailability() => IsAvailable = !_lock.IsLocked && !IsSourceMode();

    /// <summary>Recomputes the target and its frame from the model.</summary>
    public void Refresh()
    {
        IsPaused = !IsPlaying();
        var settings = _projects.Current.Settings;
        Canvas = new FrameSize(settings.FrameWidth, settings.FrameHeight);
        Target = BuildTarget(out var reason);
        if (_gesture is not null && (Target is null || Target.Clip != _gesture.Clip))
        {
            DropGesture();   // the clip went away under the gesture (cannot normally happen: input is held)
        }
        if (_gesture is null) ModeText = IsActive ? reason ?? ReadyText() : null;
        OnPropertyChanged(nameof(CanEdit));
        OnPropertyChanged(nameof(HasScaleHandles));
        OnPropertyChanged(nameof(HasCropHandles));
    }

    public static string MoveText => Strings.Transform_MoveText;
    public static string CropText => Strings.Transform_CropText;
    public static string NoClipText => Strings.Transform_NoClip;
    public static string NotUnderPlayheadText => Strings.Transform_NotUnderPlayhead;
    public static string NoPictureText => Strings.Transform_NoPicture;
    public static string UnknownSizeText => Strings.Transform_UnknownSize;
    public static string TextHasNoCrop => Strings.Transform_TextNoCrop;
    public static string PlayingText => Strings.Transform_Playing;

    private string ReadyText()
    {
        if (!IsPaused) return PlayingText;
        if (Target is { IsLocked: true } locked) return string.Format(Messages.Edit_TrackLocked, locked.Track.Name);
        if (Tool == PreviewTool.Crop && Target is { IsText: true }) return TextHasNoCrop;
        if (Tool == PreviewTool.Crop && Target is { FollowPart: not null }) return Strings.Transform_CropOnFollow;
        if (Tool == PreviewTool.Move && Target is { FollowPart: not null }) return Strings.Transform_FollowText;
        return Tool == PreviewTool.Crop ? CropText : MoveText;
    }

    /// <summary>D061 amendment 1: a click on a picture in the Preview (paused, no other mode) selects its clip and turns Move
    /// on — the frame shows, the press drags it, Ctrl + the wheel scales it.</summary>
    public bool CanActivateByClick => !IsActive && IsAvailable && !IsPlaying() && !OtherModeActive();

    /// <summary>Turns Move on for <paramref name="clipId"/> (a click on its picture); whether it is the target now.</summary>
    public bool ActivateFor(Guid clipId)
    {
        if (!CanActivateByClick) return false;
        if (Target?.Clip.Id != clipId) SelectClip?.Invoke(clipId);
        IsMoveOn = true;
        return Target?.Clip.Id == clipId;
    }

    /// <summary>
    /// D061 amendment 1: Ctrl + the wheel scales the target around its centre by <paramref name="factor"/> per notch, within
    /// the Scale's limits; the notches of one turn (each within <see cref="WheelStepWindow"/> of the last) are one undo step.
    /// On a followed part the picture keeps following (Follow keeps the clip's scale).
    /// </summary>
    public bool WheelScale(double factor)
    {
        if (!CanEdit || _gesture is not null || Target is not { } target) return false;
        var visual = target.Placed;
        var now = Now();
        if (_wheelUndo is null || _wheelClip != target.Clip.Id || now - _wheelLast > WheelStepWindow) _wheelUndo = new ClipPropertyGesture("Scale Clip");
        _wheelClip = target.Clip.Id;
        _wheelLast = now;
        var scale = Math.Clamp(Math.Round(visual.Scale * factor, PreviewTransformMath.ScaleDecimals, MidpointRounding.AwayFromZero),
            ClipPropertyLimits.MinScale, ClipPropertyLimits.MaxScale);
        if (scale == visual.Scale) return false;
        var result = WriteScale(target, scale, _wheelUndo);
        if (!result.Success)
        {
            _status.ReportWarning(result.Message ?? Strings.Inspector_ClipNotChanged);
            return false;
        }
        ModeText = string.Format(Strings.Transform_ScaleValue, Num(scale * 100));
        return true;
    }

    private TransformTarget? BuildTarget(out string? reason)
    {
        reason = null;
        var project = _projects.Current;
        if (_clipId is not { } id) { reason = NoClipText; return null; }
        Track? track = null;
        Clip? clip = null;
        foreach (var t in project.Timeline.VideoTracks)
        {
            clip = t.Clips.FirstOrDefault(c => c.Id == id);
            if (clip is not null) { track = t; break; }
        }
        if (clip is null || track is null || VisualProperties.Of(clip) is not { } visual)
        {
            reason = clip is null && project.Timeline.AudioTracks.Any(t => t.Clips.Any(c => c.Id == id)) ? NoPictureText : NoClipText;
            return null;
        }
        var playhead = Playhead();
        if (playhead < clip.TimelineStart || playhead >= clip.TimelineEnd) { reason = NotUnderPlayheadText; return null; }
        var frame = playhead.ToFrameFloor(project.Settings.FrameRate);
        // D063: a keyframed clip is where its keyframes put it on this frame.
        if (clip is VideoClip { Keyframes: not null } keyed) visual = VisualProperties.At(keyed, _keyframes.SourceAt(keyed, frame))!.Value;

        var canvas = Canvas;
        if (clip is TextClip text)
        {
            var (w, h) = _measureText(TextProperties.Of(text)!.Value);
            return new TransformTarget(clip, track, visual, PreviewTransformMath.TextFrame(canvas, visual, w, h), null) { TimelineFrame = frame };
        }
        var asset = clip is MediaBackedClip m ? project.MediaAssets.FirstOrDefault(a => a.Id == m.MediaAssetId) : null;
        if (PlaybackSnapshotBuilder.DisplaySize(asset?.Metadata) is not { } size) { reason = UnknownSizeText; return null; }
        // D061 amendment 1: on a followed part the picture is where Follow puts it on the shown frame.
        int? followPart = null;
        var placed = visual;
        if (clip is VideoClip { Follow: not null } video && FollowAt is { } followAt && !IsPlaying())
        {
            var rate = project.Settings.FrameRate;
            var source = HoldAnimationRule.SourceTimeAt(video.TimelineStart, video.SourceIn, video.Speed, playhead.ToFrameFloor(rate), rate);
            if (followAt(video, source) is { } follow)
            {
                followPart = follow.Part;
                placed = follow.Visual;
            }
        }
        var geometry = CompositionMath.Layout(canvas, size, placed);
        return new TransformTarget(clip, track, visual, PreviewTransformMath.PictureFrame(geometry), geometry)
        {
            FollowPart = followPart, TimelineFrame = frame, Placed = placed, Source = size
        };
    }

    private static (double, double) MeasureWithPainter(TextProperties text)
    {
        var layout = CompositionPainter.Layout(text);
        return (layout.MaxTextWidth, layout.Height);
    }

    // ---- selection by a click ---------------------------------------------------------------------------------------

    /// <summary>A press on another layer's picture (D038 §2): it becomes the timeline's selection — the target follows
    /// synchronously. Returns whether it is the target now.</summary>
    public bool SelectLayer(Guid clipId)
    {
        if (!IsActive || !IsPaused || _gesture is not null) return false;
        if (Target?.Clip.Id != clipId) SelectClip?.Invoke(clipId);
        return Target?.Clip.Id == clipId;
    }

    /// <summary>A press on a locked target: says why nothing happens.</summary>
    public void ReportLocked()
    {
        if (Target is { IsLocked: true } t) _status.ReportWarning(string.Format(Messages.Edit_TrackLocked, t.Track.Name));
    }

    // ---- gestures ---------------------------------------------------------------------------------------------------

    private sealed class Gesture
    {
        public required TransformGestureKind Kind { get; init; }
        public required ClipPropertyGesture Undo { get; init; }
        public required Clip Clip { get; init; }
        public required VisualProperties Start { get; init; }
        public required TransformTarget Target { get; init; }
        public required PointD Pointer { get; init; }
        public CropEdges Handle { get; init; }

        /// <summary>FollowOffset: the clip's Follow at the start and the dragged part.</summary>
        public FollowSettings? StartFollow { get; init; }
        public int FollowPart { get; init; }

        /// <summary>D063: the clip's stored values at the start (its static picture properties, its keyframes, its Follow) —
        /// what a cancel writes back.</summary>
        public VisualProperties? StartStatic { get; init; }
        public KeyframeValues? StartKeyframes { get; init; }
        public FollowSettings? StartFollowAll { get; init; }

        /// <summary>Scale: the grabbed corner minus the pointer (canvas); Crop: the dragged edges minus the pointer (source
        /// pixels).</summary>
        public PointD Offset { get; init; }

        /// <summary>Crop: canvas → source pixels at the start (frozen).</summary>
        public Affine2D CanvasToSource { get; init; }
    }

    /// <summary>Starts dragging the target's position from <paramref name="pointer"/> (canvas).</summary>
    public bool BeginMove(PointD pointer)
    {
        if (Tool != PreviewTool.Move || !CanEdit || _gesture is not null || Target is not { } target) return false;
        if (target is { FollowPart: { } part, Clip: VideoClip { Follow: { } follow } } && part < follow.Parts.Length)
            return Start(new Gesture
            {
                Kind = TransformGestureKind.FollowOffset, Undo = new ClipPropertyGesture("Move Climber in Frame"), Clip = target.Clip,
                Start = target.Visual, Target = target, Pointer = pointer, StartFollow = follow, FollowPart = part
            });
        return Start(new Gesture
        {
            Kind = TransformGestureKind.Move, Undo = new ClipPropertyGesture("Move Clip"), Clip = target.Clip, Start = target.Visual,
            Target = target, Pointer = pointer
        });
    }

    /// <summary>Starts scaling the target by its <paramref name="corner"/> handle, grabbed at <paramref name="pointer"/>.</summary>
    public bool BeginScale(CropEdges corner, PointD pointer)
    {
        if (!HasScaleHandles || _gesture is not null || Target is not { } target || !TransformFrame.Corners.Contains(corner)) return false;
        var c = target.Frame.HandlePoint(corner);
        return Start(new Gesture
        {
            Kind = TransformGestureKind.Scale, Undo = new ClipPropertyGesture("Scale Clip"), Clip = target.Clip, Start = target.Placed,
            Target = target, Pointer = pointer, Handle = corner, Offset = new PointD(c.X - pointer.X, c.Y - pointer.Y)
        });
    }

    /// <summary>Starts dragging the crop <paramref name="handle"/> grabbed at <paramref name="pointer"/> (canvas).</summary>
    public bool BeginCrop(CropEdges handle, PointD pointer)
    {
        if (!HasCropHandles || _gesture is not null || Target is not { Geometry: { } geometry } target || handle == CropEdges.None) return false;
        var toSource = PreviewTransformMath.SourceToCanvas(geometry);
        if (Math.Abs(toSource.Determinant) < 1e-12) return false;
        var canvasToSource = toSource.Invert();
        var p = canvasToSource.Apply(pointer);
        var r = geometry.SourceRect;
        var edgeX = handle.HasFlag(CropEdges.Left) ? r.X : handle.HasFlag(CropEdges.Right) ? r.Right : p.X;
        var edgeY = handle.HasFlag(CropEdges.Top) ? r.Y : handle.HasFlag(CropEdges.Bottom) ? r.Bottom : p.Y;
        return Start(new Gesture
        {
            Kind = TransformGestureKind.Crop, Undo = new ClipPropertyGesture("Crop Clip"), Clip = target.Clip, Start = target.Visual,
            Target = target, Pointer = pointer, Handle = handle, Offset = new PointD(edgeX - p.X, edgeY - p.Y),
            CanvasToSource = canvasToSource
        });
    }

    private bool Start(Gesture gesture)
    {
        gesture = new Gesture
        {
            Kind = gesture.Kind, Undo = gesture.Undo, Clip = gesture.Clip, Start = gesture.Start, Target = gesture.Target, Pointer = gesture.Pointer,
            Handle = gesture.Handle, StartFollow = gesture.StartFollow, FollowPart = gesture.FollowPart, Offset = gesture.Offset,
            CanvasToSource = gesture.CanvasToSource,
            StartStatic = VisualProperties.Of(gesture.Clip), StartKeyframes = KeyframeValues.Of(gesture.Clip),
            StartFollowAll = (gesture.Clip as VideoClip)?.Follow
        };
        _gesture = gesture;
        SnappedEdges = CropEdges.None;
        ShowGestureValues(gesture.Start);
        OnPropertyChanged(nameof(IsGestureActive));
        OnPropertyChanged(nameof(GestureKind));
        OnPropertyChanged(nameof(GestureHandle));
        return true;
    }

    /// <summary>The pointer moved to <paramref name="pointer"/> (canvas); <paramref name="keepAxis"/>: Shift (Move only).</summary>
    public void UpdateGesture(PointD pointer, bool keepAxis)
    {
        if (_gesture is not { } g) return;
        if (g.Kind == TransformGestureKind.FollowOffset)
        {
            MoveFollowOffset(g, pointer, keepAxis);
            return;
        }
        var next = g.Kind switch
        {
            TransformGestureKind.Move => PreviewTransformMath.Move(g.Start, pointer.X - g.Pointer.X, pointer.Y - g.Pointer.Y, keepAxis),
            // D063 amendment 1: the opposite corner held — the picture grows towards the dragged corner. On a followed part the
            // camera places the picture, so there the scale keeps the centre (only the scale is written).
            TransformGestureKind.Scale when g.Target.FollowPart is null => PreviewTransformMath.ScaleFromAnchor(g.Start,
                g.Target.Frame.HandlePoint(Opposite(g.Handle)), g.Target.Frame.HandlePoint(g.Handle), new PointD(pointer.X + g.Offset.X, pointer.Y + g.Offset.Y)),
            TransformGestureKind.Scale => PreviewTransformMath.ScaleFromCorner(g.Start, g.Target.Frame.Center, g.Target.Frame.HandlePoint(g.Handle),
                new PointD(pointer.X + g.Offset.X, pointer.Y + g.Offset.Y)),
            _ => Crop(g, pointer)
        };
        next = Snap(g, next);
        if (g.Kind == TransformGestureKind.Scale && g.Target.FollowPart is not null)
        {
            var result = WriteScale(g.Target, next.Scale, g.Undo);
            if (!result.Success)
            {
                _status.ReportWarning(result.Message ?? Strings.Inspector_ClipNotChanged);
                CancelGesture();
                return;
            }
            if (_gesture == g) ShowGestureValues(next);
            return;
        }
        Apply(g, next);
    }

    /// <summary>The corner opposite <paramref name="corner"/>.</summary>
    private static CropEdges Opposite(CropEdges corner) =>
        (corner.HasFlag(CropEdges.Left) ? CropEdges.Right : CropEdges.Left) | (corner.HasFlag(CropEdges.Top) ? CropEdges.Bottom : CropEdges.Top);

    /// <summary>D063 §B: the threshold in canvas pixels — the user's screen pixels at the Preview's zoom now.</summary>
    private double SnapThresholdCanvas() =>
        Math.Max(0, SnapThresholdPixels()) / Math.Max(1e-6, ScreenPixelsPerCanvasPixel);

    /// <summary>D063 §B: Move and Scale of a picture snap its edges to the canvas edges (not a text, not a frame Follow
    /// places — the camera keeps covering the canvas there).</summary>
    private VisualProperties Snap(Gesture g, VisualProperties raw)
    {
        var edges = CropEdges.None;
        if (g.Target is { Source: { } source, IsText: false, FollowPart: null } && g.Kind is TransformGestureKind.Move or TransformGestureKind.Scale)
        {
            var snapped = g.Kind == TransformGestureKind.Move
                ? CanvasSnap.Move(Canvas, source, raw, SnapThresholdCanvas())
                : CanvasSnap.ScaleFromAnchor(Canvas, source, g.Start, raw, SnapThresholdCanvas());
            raw = snapped.Visual;
            edges = snapped.Edges;
        }
        if (_gesture == g) SnappedEdges = edges;
        return raw;
    }

    /// <summary>D063: the target's scale written where it belongs on its frame — a followed part with framing keyframes: its
    /// keyframe; otherwise the clip's (its keyframe on the frame when it has keyframes, else its static Scale).</summary>
    private TimelineEditResult WriteScale(TransformTarget target, double scale, ClipPropertyGesture gesture)
    {
        if (target.Clip is VideoClip video)
        {
            var owner = target.FollowPart is { } p && video.Follow is { } follow && p < follow.Parts.Length && follow.Parts[p].Keyframes is not null
                ? new KeyframeOwner(video.Id, p)
                : new KeyframeOwner(video.Id, null);
            var value = _keyframes.ValueAt(video, owner, target.TimelineFrame);
            if (value.Scale == scale) return TimelineEditResult.Unchanged();
            return _keyframes.SetValue(video, owner, target.TimelineFrame, value with { Scale = scale }, gesture);
        }
        if (VisualProperties.Of(target.Clip) is not { } visual || visual.Scale == scale) return TimelineEditResult.Unchanged();
        return _edit.SetClipProperties(target.Clip.Id, new ClipPropertyChange { Visual = visual with { Scale = scale }, Gesture = gesture });
    }

    /// <summary>The person moves in the frame with the picture: the part's offset grows by the drag (canvas pixels).</summary>
    private void MoveFollowOffset(Gesture g, PointD pointer, bool keepAxis)
    {
        var (dx, dy) = (pointer.X - g.Pointer.X, pointer.Y - g.Pointer.Y);
        if (keepAxis)
        {
            if (Math.Abs(dx) >= Math.Abs(dy)) dy = 0;
            else dx = 0;
        }
        var part = g.StartFollow!.Parts[g.FollowPart];
        // D063 §4: with framing keyframes the climber's place on this frame (its keyframe there, or a new one).
        var owner = new KeyframeOwner(g.Clip.Id, g.FollowPart);
        // The part's framing on the frame at the gesture's start (never the live keyframe, which already holds the drag).
        var framed = part.Keyframes is not null && g.Clip is VideoClip start
            ? part.FramingAt(_keyframes.EditTimeAt(start, owner, g.Target.TimelineFrame),
                VisualProperties.At(start, _keyframes.SourceAt(start, g.Target.TimelineFrame))!.Value.Scale)
            : (TransformValue?)null;
        if (framed is { } f) part = part with { OffsetX = f.X, OffsetY = f.Y };
        var limit = ClipPropertyLimits.MaxPositionMagnitude;
        var moved = part with
        {
            OffsetX = Math.Round(Math.Clamp(part.OffsetX + dx, -limit, limit), 2),
            OffsetY = Math.Round(Math.Clamp(part.OffsetY + dy, -limit, limit), 2)
        };
        if (g.Clip is not VideoClip { Follow: { } current } video) return;
        if (framed is { } shown)
        {
            var keyed = _keyframes.SetValue(video, owner, g.Target.TimelineFrame, shown with { X = moved.OffsetX, Y = moved.OffsetY }, g.Undo);
            if (!keyed.Success)
            {
                _status.ReportWarning(keyed.Message ?? Strings.Inspector_ClipNotChanged);
                CancelGesture();
                return;
            }
            if (_gesture == g) ModeText = string.Format(Strings.Transform_FollowValue, Num(moved.OffsetX), Num(moved.OffsetY));
            return;
        }
        var next = g.StartFollow.WithPart(g.FollowPart, moved)!;
        if (!Equals(next, current))
        {
            var result = _edit.SetClipFollow(video.Id, next, gesture: g.Undo);
            if (!result.Success)
            {
                _status.ReportWarning(result.Message ?? Strings.Inspector_ClipNotChanged);
                CancelGesture();
                return;
            }
        }
        if (_gesture == g) ModeText = string.Format(Strings.Transform_FollowValue, Num(moved.OffsetX), Num(moved.OffsetY));
    }

    private VisualProperties Crop(Gesture g, PointD pointer)
    {
        var p = g.CanvasToSource.Apply(pointer);
        return PreviewTransformMath.AnchoredCrop(Canvas, g.Target.Geometry!.Source, g.Start, g.Handle, new PointD(p.X + g.Offset.X, p.Y + g.Offset.Y));
    }

    private void Apply(Gesture g, VisualProperties next)
    {
        if (VisualProperties.Of(g.Clip) is not { } current) return;
        // D063: a keyframed clip — the position and scale go to its keyframe on the frame, the crop and the rest stay static;
        // one change, so the gesture stays one undo step.
        if (g.Clip is VideoClip { Keyframes: not null } keyed)
        {
            var owner = new KeyframeOwner(keyed.Id, null);
            var shown = _keyframes.ValueAt(keyed, owner, g.Target.TimelineFrame);
            var value = new TransformValue(next.PositionX, next.PositionY, next.Scale);
            var stat = current with { RotationDegrees = next.RotationDegrees, Opacity = next.Opacity, Crop = next.Crop };
            if (value != shown || stat != current)
            {
                var keys = _keyframes.KeysWithValue(keyed, owner, g.Target.TimelineFrame, value);
                var keyedResult = _edit.SetClipProperties(keyed.Id, new ClipPropertyChange
                {
                    Visual = stat, Keyframes = new KeyframeValues(keys), Gesture = g.Undo
                });
                if (!keyedResult.Success)
                {
                    _status.ReportWarning(keyedResult.Message ?? Strings.Inspector_ClipNotChanged);
                    CancelGesture();
                    return;
                }
            }
            if (_gesture == g) ShowGestureValues(next);
            return;
        }
        if (next != current)
        {
            var result = _edit.SetClipProperties(g.Clip.Id, new ClipPropertyChange { Visual = next, Gesture = g.Undo });
            if (!result.Success)
            {
                _status.ReportWarning(result.Message ?? Strings.Inspector_ClipNotChanged);
                CancelGesture();
                return;
            }
        }
        if (_gesture == g) ShowGestureValues(next);
    }

    /// <summary>The pointer was released: the gesture's step stays as it is (one undo step, or none when nothing changed).</summary>
    public void EndGesture()
    {
        if (_gesture is null) return;
        DropGesture();
    }

    /// <summary>Cancels a running gesture: its start is written back — the gesture's step removes itself (D017), so no
    /// undo step and no redo entry remain.</summary>
    public void CancelGesture()
    {
        if (_gesture is not { } g) return;
        // D063: every stored value the gesture may have changed goes back — the static properties, the keyframes, the Follow.
        if (g.Clip is VideoClip video && !Equals(video.Follow, g.StartFollowAll))
            _edit.SetClipFollow(video.Id, g.StartFollowAll, gesture: g.Undo);
        if (g.StartStatic is { } startStatic &&
            (VisualProperties.Of(g.Clip) != startStatic || KeyframeValues.Of(g.Clip) != g.StartKeyframes))
            _edit.SetClipProperties(g.Clip.Id, new ClipPropertyChange { Visual = startStatic, Keyframes = g.StartKeyframes, Gesture = g.Undo });
        DropGesture();
    }

    private void DropGesture()
    {
        if (_gesture is null) return;
        _gesture = null;
        SnappedEdges = CropEdges.None;
        OnPropertyChanged(nameof(IsGestureActive));
        OnPropertyChanged(nameof(GestureKind));
        OnPropertyChanged(nameof(GestureHandle));
        ModeText = IsActive ? ReadyText() : null;
    }

    private void ShowGestureValues(VisualProperties v)
    {
        if (_gesture is not { } g) return;
        if (g.Kind == TransformGestureKind.FollowOffset)
        {
            var part = g.StartFollow!.Parts[g.FollowPart];
            ModeText = string.Format(Strings.Transform_FollowValue, Num(part.OffsetX), Num(part.OffsetY));
            return;
        }
        var snapped = SnappedEdges != CropEdges.None ? " · " + Strings.Transform_Snapped : "";
        ModeText = g.Kind switch
        {
            TransformGestureKind.Move => string.Format(Strings.Transform_MoveValue, Num(v.PositionX), Num(v.PositionY)) + snapped,
            TransformGestureKind.Scale => string.Format(Strings.Transform_ScaleValue, Num(v.Scale * 100)) + snapped,
            _ => string.Format(Strings.Transform_CropValue, Num(v.Crop.Left * 100), Num(v.Crop.Top * 100), Num(v.Crop.Right * 100), Num(v.Crop.Bottom * 100))
        };
    }

    private static string Num(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);
}
