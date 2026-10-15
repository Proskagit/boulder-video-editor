using System.Globalization;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Composition;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Core.Playback;
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
    Crop
}

/// <summary>The clip the tools act on (D038 §2): the primary selected clip under the playhead, its frame on the canvas
/// from the model (a picture's visible part through its layout, or a text box), and whether its track is locked.</summary>
public sealed record TransformTarget(Clip Clip, Track Track, VisualProperties Visual, TransformFrame Frame, LayerGeometry? Geometry)
{
    public bool IsText => Clip is TextClip;
    public bool IsLocked => Track.IsLocked;
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
        EditingLock? editingLock = null, Func<TextProperties, (double Width, double Height)>? measureText = null)
    {
        _edit = edit;
        _projects = projects;
        _status = status;
        _lock = editingLock ?? new EditingLock();
        _measureText = measureText ?? MeasureWithPainter;

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
    public bool HasCropHandles => Tool == PreviewTool.Crop && CanEdit && Target is { IsText: false, Geometry: not null };

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

    public const string MoveText = "Move — drag the clip; a corner scales it; Shift keeps an axis; Esc cancels";
    public const string CropText = "Crop — drag a handle; the picture stays in place; Esc cancels";
    public const string NoClipText = "Select a clip under the playhead (click its picture)";
    public const string NotUnderPlayheadText = "The selected clip is not under the playhead";
    public const string NoPictureText = "The selected clip has no picture";
    public const string UnknownSizeText = "The size of this clip's picture is not known yet";
    public const string TextHasNoCrop = "Text clips have no crop — use Move";
    public const string PlayingText = "Pause to move, scale or crop";

    private string ReadyText()
    {
        if (!IsPaused) return PlayingText;
        if (Target is { IsLocked: true } locked) return $"Track {locked.Track.Name} is locked.";
        if (Tool == PreviewTool.Crop && Target is { IsText: true }) return TextHasNoCrop;
        return Tool == PreviewTool.Crop ? CropText : MoveText;
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

        var canvas = Canvas;
        if (clip is TextClip text)
        {
            var (w, h) = _measureText(TextProperties.Of(text)!.Value);
            return new TransformTarget(clip, track, visual, PreviewTransformMath.TextFrame(canvas, visual, w, h), null);
        }
        var asset = clip is MediaBackedClip m ? project.MediaAssets.FirstOrDefault(a => a.Id == m.MediaAssetId) : null;
        if (PlaybackSnapshotBuilder.DisplaySize(asset?.Metadata) is not { } size) { reason = UnknownSizeText; return null; }
        var geometry = CompositionMath.Layout(canvas, size, visual);
        return new TransformTarget(clip, track, visual, PreviewTransformMath.PictureFrame(geometry), geometry);
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
        if (Target is { IsLocked: true } t) _status.Report($"Track {t.Track.Name} is locked.");
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
            Kind = TransformGestureKind.Scale, Undo = new ClipPropertyGesture("Scale Clip"), Clip = target.Clip, Start = target.Visual,
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
        _gesture = gesture;
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
        var next = g.Kind switch
        {
            TransformGestureKind.Move => PreviewTransformMath.Move(g.Start, pointer.X - g.Pointer.X, pointer.Y - g.Pointer.Y, keepAxis),
            TransformGestureKind.Scale => PreviewTransformMath.ScaleFromCorner(g.Start, g.Target.Frame.Center, g.Target.Frame.HandlePoint(g.Handle),
                new PointD(pointer.X + g.Offset.X, pointer.Y + g.Offset.Y)),
            _ => Crop(g, pointer)
        };
        Apply(g, next);
    }

    private VisualProperties Crop(Gesture g, PointD pointer)
    {
        var p = g.CanvasToSource.Apply(pointer);
        return PreviewTransformMath.AnchoredCrop(Canvas, g.Target.Geometry!.Source, g.Start, g.Handle, new PointD(p.X + g.Offset.X, p.Y + g.Offset.Y));
    }

    private void Apply(Gesture g, VisualProperties next)
    {
        if (VisualProperties.Of(g.Clip) is not { } current) return;
        if (next != current)
        {
            var result = _edit.SetClipProperties(g.Clip.Id, new ClipPropertyChange { Visual = next, Gesture = g.Undo });
            if (!result.Success)
            {
                _status.Report(result.Message ?? "The clip could not be changed.");
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
        if (VisualProperties.Of(g.Clip) is { } current && current != g.Start)
            _edit.SetClipProperties(g.Clip.Id, new ClipPropertyChange { Visual = g.Start, Gesture = g.Undo });
        DropGesture();
    }

    private void DropGesture()
    {
        if (_gesture is null) return;
        _gesture = null;
        OnPropertyChanged(nameof(IsGestureActive));
        OnPropertyChanged(nameof(GestureKind));
        OnPropertyChanged(nameof(GestureHandle));
        ModeText = IsActive ? ReadyText() : null;
    }

    private void ShowGestureValues(VisualProperties v)
    {
        if (_gesture is not { } g) return;
        ModeText = g.Kind switch
        {
            TransformGestureKind.Move => $"Position X {Num(v.PositionX)} px · Y {Num(v.PositionY)} px",
            TransformGestureKind.Scale => $"Scale {Num(v.Scale * 100)} %",
            _ => $"Crop L {Num(v.Crop.Left * 100)} % · T {Num(v.Crop.Top * 100)} % · R {Num(v.Crop.Right * 100)} % · B {Num(v.Crop.Bottom * 100)} %"
        };
    }

    private static string Num(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);
}
