using AiVideoEditor.UI.Common;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AiVideoEditor.UI.ViewModels.Panels;

// --- Select All, Esc and the marquee (Phase 22, D037 §1) ----------------------------------------------------------------
// View state only (D008): nothing here is an undo step or a project change. Clips of locked tracks are never picked by
// Select All or a marquee (Q1); a picked clip brings its link partner (D032 AQ1), as a click does. The primary — the
// Inspector's clip — of such a selection is its earliest clip, a tie going to the topmost track.
public sealed partial class TimelineViewModel
{
    private Marquee? _marquee;

    /// <summary>The marquee's rectangle in content coordinates (X as the clips, Y from the top of the first lane).</summary>
    [ObservableProperty] private bool _isMarqueeVisible;
    [ObservableProperty] private double _marqueeLeft;
    [ObservableProperty] private double _marqueeTop;
    [ObservableProperty] private double _marqueeWidth;
    [ObservableProperty] private double _marqueeHeight;

    private sealed class Marquee
    {
        public required double StartX { get; init; }
        public required double StartY { get; init; }
        public required bool Additive { get; init; }
        public required List<Guid> Before { get; init; }
        public bool Active { get; set; }
    }

    /// <summary>Ctrl+A: every clip of every unlocked track, with the partners (D037 §1).</summary>
    [RelayCommand]
    private void SelectAll()
    {
        var clips = Tracks.Where(t => !t.Track.IsLocked).SelectMany(t => t.Clips).ToList();
        if (clips.Count == 0)
        {
            _status.Report("There is no clip on an unlocked track to select.");
            return;
        }
        SetSelection(clips);
        ReportSelectionCount();
    }

    /// <summary>Esc: a marquee is cancelled (the selection from before it comes back), a drag is cancelled, otherwise the
    /// selection is cleared.</summary>
    [RelayCommand]
    private void Escape()
    {
        if (_marquee is not null) CancelMarquee();
        else if (IsGestureActive) CancelGesture();
        else ClearSelection();
    }

    public bool IsMarqueeActive => _marquee is { Active: true };

    /// <summary>Pointer pressed on an empty place of a lane: a marquee may start (it does once the pointer moves past the
    /// drag threshold). <paramref name="lanesY"/> is measured from the top of the first lane.</summary>
    public void BeginMarquee(double contentX, double lanesY, bool additive)
    {
        _marquee = new Marquee { StartX = contentX, StartY = lanesY, Additive = additive, Before = _selection.ToList() };
    }

    public void UpdateMarquee(double contentX, double lanesY)
    {
        if (_marquee is not { } m) return;
        if (!m.Active && Math.Abs(contentX - m.StartX) < DragThresholdPixels && Math.Abs(lanesY - m.StartY) < DragThresholdPixels)
            return;
        m.Active = true;

        var (left, right) = (Math.Min(m.StartX, contentX), Math.Max(m.StartX, contentX));
        var (top, bottom) = (Math.Min(m.StartY, lanesY), Math.Max(m.StartY, lanesY));
        MarqueeLeft = left;
        MarqueeTop = top;
        MarqueeWidth = right - left;
        MarqueeHeight = bottom - top;
        IsMarqueeVisible = true;

        var hits = ClipsInRectangle(left, right, top, bottom);
        if (m.Additive)
            hits = hits.Concat(m.Before.Where(_clipViewModels.ContainsKey).Select(id => _clipViewModels[id])).Distinct().ToList();
        SetSelection(hits);
    }

    /// <summary>Pointer released: a marquee keeps its selection; a press without a drag clears the selection as before —
    /// with Ctrl it leaves it.</summary>
    public void EndMarquee()
    {
        if (_marquee is not { } m) return;
        _marquee = null;
        IsMarqueeVisible = false;
        if (m.Active) ReportSelectionCount();
        else if (!m.Additive) ClearSelection();
    }

    public void CancelMarquee()
    {
        if (_marquee is not { } m) return;
        _marquee = null;
        IsMarqueeVisible = false;
        if (m.Active) RestoreSelection(m.Before);
    }

    /// <summary>The clips of unlocked tracks the rectangle intersects (Q2): a lane the rectangle reaches, a clip whose
    /// drawn span overlaps it.</summary>
    private List<TimelineClipViewModel> ClipsInRectangle(double left, double right, double top, double bottom)
    {
        var height = TimelineTrackViewModel.Height;
        var result = new List<TimelineClipViewModel>();
        for (var i = 0; i < Tracks.Count; i++)
        {
            var (laneTop, laneBottom) = (i * height, (i + 1) * height);
            if (laneBottom <= top || laneTop > bottom || Tracks[i].Track.IsLocked) continue;
            result.AddRange(Tracks[i].Clips.Where(c => c.Left <= right && c.Left + c.Width >= left));
        }
        return result;
    }

    /// <summary>Makes <paramref name="picked"/> (with their partners) the selection; the primary (the last entry) is the
    /// earliest picked clip, a tie going to the topmost track.</summary>
    private void SetSelection(IReadOnlyCollection<TimelineClipViewModel> picked)
    {
        var next = new List<Guid>();
        if (picked.Count > 0)
        {
            var ordered = picked.OrderBy(c => c.Clip.TimelineStart.Ticks).ThenBy(LaneIndex).ToList();
            var primary = ordered[0];
            foreach (var clip in ordered.Skip(1).Append(primary))
                foreach (var id in SelectionGroup(clip.Id))
                {
                    next.Remove(id);
                    next.Add(id);
                }
            next.Remove(primary.Id);
            next.Add(primary.Id);
        }
        RestoreSelection(next);
    }

    private void RestoreSelection(IReadOnlyList<Guid> ids)
    {
        DropTransitionSelection();
        var live = ids.Where(_clipViewModels.ContainsKey).ToList();
        if (live.SequenceEqual(_selection)) return;
        _selection.Clear();
        _selection.AddRange(live);
        UpdateSelectionVisuals();
        RaiseSelectionChanged();
    }

    private int LaneIndex(TimelineClipViewModel clip) => TrackOf(clip) is { } track ? Tracks.IndexOf(track) : int.MaxValue;

    private void ReportSelectionCount() =>
        _status.Report(_selection.Count switch
        {
            0 => "No clip selected.",
            1 => "1 clip selected",
            var n => $"{n} clips selected"
        });

    // --- Nudge and auto-scroll (D037 §4) ----------------------------------------------------------------------------------

    private bool CanNudge() => CanEdit() && HasSelection;

    /// <summary>Alt+← / Alt+→: the selection one frame earlier / later; with Shift one second (the step of Shift+← / →).
    /// The move's rules (D037 §3): room is made on the right, a collision on the left or the timeline's start refuses
    /// with the reason; each press its own undo step (Q4); never snapped.</summary>
    [RelayCommand(CanExecute = nameof(CanNudge))] private void NudgeLeft() => Nudge(-1);
    [RelayCommand(CanExecute = nameof(CanNudge))] private void NudgeRight() => Nudge(1);
    [RelayCommand(CanExecute = nameof(CanNudge))] private void NudgeLeftSecond() => Nudge(-TimeFormat.NominalFps(FrameRate));
    [RelayCommand(CanExecute = nameof(CanNudge))] private void NudgeRightSecond() => Nudge(TimeFormat.NominalFps(FrameRate));

    private void Nudge(long frames)
    {
        if (_selection.Count == 0 || IsGestureActive) return;
        Report(_edit.MoveClips(_selection.ToList(), frames));
    }

    /// <summary>Within this many pixels of the view's left or right edge a drag scrolls the view.</summary>
    public const double AutoScrollZonePixels = 32;

    /// <summary>The largest scroll per tick (about 60 per second), reached three zone widths past the edge.</summary>
    public const double AutoScrollMaxStepPixels = 30;

    /// <summary>How far a drag at <paramref name="viewportX"/> (pixels from the view's left edge) scrolls the view in one
    /// tick: negative to the left, positive to the right, 0 outside the edge zones — faster the deeper in (D037 §4).</summary>
    public static double AutoScrollStep(double viewportX, double viewportWidth)
    {
        if (viewportWidth <= 2 * AutoScrollZonePixels) return 0;
        var depth = viewportX < AutoScrollZonePixels ? AutoScrollZonePixels - viewportX
            : viewportX > viewportWidth - AutoScrollZonePixels ? viewportX - (viewportWidth - AutoScrollZonePixels)
            : 0;
        if (depth <= 0) return 0;
        var step = Math.Min(AutoScrollMaxStepPixels, AutoScrollMaxStepPixels * depth / (3 * AutoScrollZonePixels));
        step = Math.Max(1, step);
        return viewportX < AutoScrollZonePixels ? -step : step;
    }
}
