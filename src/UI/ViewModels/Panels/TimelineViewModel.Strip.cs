using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.UI.Common;
using AiVideoEditor.UI.Localization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AiVideoEditor.UI.ViewModels.Panels;

/// <summary>
/// D048 stage 3 (§4) on the timeline: the overlay strip of every video clip (green, orange over it, lilac on top), its
/// handles dragged like a trim — the edit service plans each move (<c>PreviewOverlayStrip</c>, nothing applied) and the
/// strip, the template's rectangle and the changed interval follow the pointer; the release is one
/// <c>EditOverlayStrip</c> (one Undo step, none when nothing changed); Esc or a lost pointer cancel. The boundaries snap
/// as the timeline's (D037). A click on the orange selects it (a strip selection, not the clip's); Delete then removes the
/// clip's whole hold markup (§3.9). Every rule — limits, the two lilac frames, no overlap — stays in the edit service.
/// </summary>
public sealed partial class TimelineViewModel
{
    /// <summary>How near (pixels) a press must be to a segment's edge to grab that edge rather than the body — the snap's
    /// tolerance (4 and then 6 px proved too tight with a mouse on a wide screen in the manual runs).</summary>
    public const double StripHandleReachPixels = 8;

    public const string GreenHex = "#43A047";
    public const string OrangeHex = "#FB8C00";
    public const string LilacHex = "#B39DDB";

    /// <summary>Whether this is a Debug build: the switch back to the lane is shown only there.</summary>
#if DEBUG
    public bool IsDebugBuild => true;
#else
    public bool IsDebugBuild => false;
#endif

    /// <summary>D048 variant B (the owner's decision of 2026-10-28): the strip's segments drawn inside the anchored
    /// «Боулдер»; on by default. A view only — the same strip, the same service edits. Off (Debug switch only) draws the
    /// earlier lane above the clip; a clip without an anchored template uses the lane either way.</summary>
    [ObservableProperty] private bool _segmentsInTemplate = true;

    partial void OnSegmentsInTemplateChanged(bool value) => RefreshStrips();

    /// <summary>The video clip whose orange segment is selected (§3.9), or null.</summary>
    [ObservableProperty] private Guid? _selectedOrangeClipId;

    partial void OnSelectedOrangeClipIdChanged(Guid? value) => DeleteSelectedCommand.NotifyCanExecuteChanged();

    /// <summary>The overlay strip of every video clip at the current zoom, from the edit service's reading of the model.</summary>
    private void RefreshStrips()
    {
        foreach (var vm in _clipViewModels.Values)
        {
            vm.TemplateSegments = Array.Empty<StripSegment>();
            vm.TemplateOverflow = Array.Empty<StripSegment>();
            vm.StripOwner = null;
        }
        foreach (var vm in _clipViewModels.Values)
            ShowStrip(vm, vm.Clip is VideoClip clip ? _edit.OverlayStripOf(clip.Id) : null);
        if (SelectedOrangeClipId is { } id && !(_clipViewModels.TryGetValue(id, out var selected) &&
                                                selected.StripSegments.Any(s => s.Kind == StripSegmentKind.Orange)))
            SelectedOrangeClipId = null;
    }

    private void ShowStrip(TimelineClipViewModel vm, OverlayStripState? strip)
    {
        if (strip is null)
        {
            vm.StripSegments = Array.Empty<StripSegment>();
            return;
        }
        var segments = new List<StripSegment>(3);
        void Add(FrameSpan? span, StripSegmentKind kind, string color, string tip)
        {
            if (span is not { Length: > 0 } s) return;
            var left = TimelineCoordinateMapper.TimeToX(MediaTime.FromFrame(s.Start, FrameRate) - vm.Clip.TimelineStart, PixelsPerSecond);
            var width = Math.Max(1, TimelineCoordinateMapper.TimeToX(MediaTime.FromFrame(s.Length, FrameRate), PixelsPerSecond));
            segments.Add(new StripSegment(left, width, color, string.Format(tip, Timecode(s.Start), Timecode(s.End)), kind, s.Start, s.End,
                kind == StripSegmentKind.Orange && SelectedOrangeClipId == vm.Id));
        }
        Add(strip.Green, StripSegmentKind.Green, GreenHex, Strings.Strip_Green);
        Add(strip.Orange, StripSegmentKind.Orange, OrangeHex, Strings.Strip_Orange);
        Add(strip.Lilac, StripSegmentKind.Lilac, LilacHex, Strings.Strip_Lilac);
        vm.StripSegments = segments;
        // Variant B draws the strip in the plate; a clip without an anchored «Боулдер» keeps the lane (its orange only).
        vm.StripInTemplate = SegmentsInTemplate && strip.TemplateId is { } t && _clipViewModels.ContainsKey(t);
        ShowInTemplate(vm, strip, segments);
    }

    /// <summary>
    /// Variant B: the strip drawn inside its «Боулдер», from the template's start <c>Ps</c> (the green's start) to its end
    /// <c>Pe</c>. The parts of a segment outside the plate — an orange started before <c>Ps</c> (its start is limited by the
    /// clip, not by the template, §4.2) or ended after <c>Pe</c> without a lilac — are drawn as an overflow beside the plate,
    /// translucent, so nothing of the animation disappears.
    /// </summary>
    private void ShowInTemplate(TimelineClipViewModel owner, OverlayStripState strip, IReadOnlyList<StripSegment> segments)
    {
        if (!SegmentsInTemplate || strip.TemplateId is not { } id || !_clipViewModels.TryGetValue(id, out var template) ||
            strip.Green is not { } green)
            return;
        long ps = green.Start, pe = strip.Lilac?.End ?? green.End;
        double Px(long frames) => TimelineCoordinateMapper.TimeToX(MediaTime.FromFrame(frames, FrameRate), PixelsPerSecond);
        template.TemplateSegments = segments
            .Where(s => s.End > ps && s.Start < pe)
            .Select(s => s with { Left = Px(Math.Max(s.Start, ps) - ps), Width = Math.Max(1, Px(Math.Min(s.End, pe) - Math.Max(s.Start, ps))) })
            .ToList();
        var overflow = new List<StripSegment>();
        foreach (var s in segments)
        {
            if (s.Start < ps) overflow.Add(s with { Left = Px(s.Start - ps), Width = Math.Max(1, Px(Math.Min(s.End, ps) - s.Start)) });
            if (s.End > pe) overflow.Add(s with { Left = Px(Math.Max(s.Start, pe) - ps), Width = Math.Max(1, Px(s.End - Math.Max(s.Start, pe))) });
        }
        template.TemplateOverflow = overflow;
        template.StripOwner = owner;
    }

    /// <summary>
    /// Variant B's hit-testing inside a «Боулдер», at <paramref name="templateX"/> pixels from its left edge — the anchored
    /// clip's strip handle the press takes, or null for the template's own gesture:
    /// <list type="number">
    /// <item>the nearest edge within <see cref="StripHandleReachPixels"/>: the plate's left edge is the green's start
    /// (<c>Ps</c>, §4.2: <c>S</c> and <c>Pe</c> kept), its right edge the lilac's end (<c>Pe</c>); the orange's start / end and
    /// the status boundary inside; on a tie the plate's edge first (§4.2's shared boundary);</item>
    /// <item>the orange's body: the orange (a drag moves it, a click selects it for §3.9) — never the template;</item>
    /// <item>with <paramref name="alt"/> the lilac's body (§4.2: <c>S</c> and <c>Pe</c> by δ);</item>
    /// <item>anything else — the green's and the lilac's body, the right edge without a status — null: the template's own
    /// move or trim.</item>
    /// </list>
    /// </summary>
    public (TimelineClipViewModel Owner, OverlayStripHandle Handle)? TemplatePressAt(TimelineClipViewModel template, double templateX, bool alt = false)
    {
        if (!SegmentsInTemplate || template.StripOwner is not { } owner || template.TemplateSegments.Count == 0) return null;
        var handle = StripHandleAt(owner, templateX + template.Left - owner.Left);
        return handle switch
        {
            OverlayStripHandle.LilacBody when !alt => null,
            null => null,
            _ => (owner, handle.Value)
        };
    }

    /// <summary>Variant B: a press inside a «Боулдер» released without a drag. On the orange it selects the orange alone (the
    /// clips' selection cleared, so Delete means the markup, §3.9); elsewhere it selects the template, as a click on a clip.</summary>
    public void OnTemplateStripClicked(TimelineClipViewModel template, TimelineClipViewModel owner, OverlayStripHandle handle)
    {
        if (handle is OverlayStripHandle.OrangeBody or OverlayStripHandle.OrangeStart or OverlayStripHandle.OrangeEnd)
        {
            ClearSelection();
            SelectOrange(owner.Id);
            return;
        }
        SelectOrange(null);
        SelectOnly(template.Id);
    }
    private string Timecode(long frame) => TimeFormat.ToTimecode(MediaTime.FromFrame(frame, FrameRate), FrameRate);

    /// <summary>
    /// The handle under a press at <paramref name="clipX"/> pixels from the clip's left edge, or null (the green's body is
    /// not movable). An edge within <see cref="StripHandleReachPixels"/> wins over a body; on a shared left boundary of the
    /// green and the orange the green's start is taken first (§4.2); where the orange's end meets the lilac's start the
    /// side of the press decides.
    /// </summary>
    public OverlayStripHandle? StripHandleAt(TimelineClipViewModel clip, double clipX)
    {
        var segments = clip.StripSegments;
        StripSegment? Of(StripSegmentKind kind) => segments.FirstOrDefault(s => s.Kind == kind);
        var (green, orange, lilac) = (Of(StripSegmentKind.Green), Of(StripSegmentKind.Orange), Of(StripSegmentKind.Lilac));

        var edges = new List<(double X, OverlayStripHandle Handle, int Priority)>();
        if (green is not null) edges.Add((green.Left, OverlayStripHandle.GreenStart, 0));
        if (orange is not null)
        {
            edges.Add((orange.Left, OverlayStripHandle.OrangeStart, 1));
            edges.Add((orange.Left + orange.Width, OverlayStripHandle.OrangeEnd, clipX < orange.Left + orange.Width ? 0 : 1));
        }
        if (lilac is not null)
        {
            edges.Add((lilac.Left, OverlayStripHandle.Status, clipX >= lilac.Left ? 0 : 1));
            edges.Add((lilac.Left + lilac.Width, OverlayStripHandle.LilacEnd, 0));
        }
        var nearest = edges.Where(e => Math.Abs(e.X - clipX) <= StripHandleReachPixels)
            .OrderBy(e => Math.Round(Math.Abs(e.X - clipX), 3)).ThenBy(e => e.Priority).ToList();
        if (nearest.Count > 0) return nearest[0].Handle;

        if (orange is not null && clipX >= orange.Left && clipX < orange.Left + orange.Width) return OverlayStripHandle.OrangeBody;
        if (lilac is not null && clipX >= lilac.Left && clipX < lilac.Left + lilac.Width) return OverlayStripHandle.LilacBody;
        return null;
    }

    /// <summary>A press on the strip at <paramref name="handle"/>: the drag starts (it becomes active past the drag threshold,
    /// like a trim). The clip is not selected by it — the strip is the overlays' view.</summary>
    public bool BeginStripDrag(TimelineClipViewModel clip, OverlayStripHandle handle, double contentX, TimelineClipViewModel? pressedOn = null)
    {
        if (!CanEdit() || clip.Clip is not VideoClip) return false;
        _gesture = new Gesture
        {
            Edge = null, StartX = contentX, Clips = new List<TimelineClipViewModel> { clip }, SourceTrack = TrackOf(pressedOn ?? clip),
            StripHandle = handle
        };
        return true;
    }

    /// <summary>A press on the strip released without a drag: a click on the orange selects it (§3.9); elsewhere it clears
    /// that selection.</summary>
    public void OnStripClicked(TimelineClipViewModel clip, OverlayStripHandle? handle) =>
        SelectOrange(handle is OverlayStripHandle.OrangeBody or OverlayStripHandle.OrangeStart or OverlayStripHandle.OrangeEnd ? clip.Id : null);

    private void SelectOrange(Guid? clipId)
    {
        if (SelectedOrangeClipId == clipId) return;
        var previous = SelectedOrangeClipId;
        SelectedOrangeClipId = clipId;
        foreach (var id in new[] { previous, clipId })
            if (id is { } changed && _clipViewModels.TryGetValue(changed, out var vm) && vm.Clip is VideoClip video)
                ShowStrip(vm, _edit.OverlayStripOf(video.Id));
    }

    private void DeleteSelectedOrange(Guid clipId)
    {
        var result = _edit.DeleteHoldMarkup(clipId);
        if (result.Success) SelectOrange(null);
        Report(result, successMessage: Strings.Strip_MarkupDeleted);
    }

    /// <summary>The drag's feedback: the boundary (or both, for a body) follows the pointer in whole frames, snapped like
    /// the timeline's edges; the edit service plans it and the strip, the template's rectangle and the interval show the
    /// planned values — a refused plan marks the strip invalid.</summary>
    private void UpdateStrip(Gesture g, OverlayStripHandle handle, MediaTime rawDelta)
    {
        var clip = g.Clips[0];
        if (_edit.OverlayStripOf(clip.Id) is not { } strip) return;
        var moved = MovedFrames(strip, handle).Select(f => MediaTime.FromFrame(f, FrameRate)).ToList();
        if (moved.Count == 0) return;
        var excluded = strip.TemplateId is { } templateId && _clipViewModels.TryGetValue(templateId, out var template)
            ? new[] { template }
            : Array.Empty<TimelineClipViewModel>();
        var delta = ApplySnap(moved.Select(m => m + rawDelta).ToList(), excluded, rawDelta);
        g.StripFrames = (moved[0] + delta).ToNearestFrame(FrameRate) - moved[0].ToNearestFrame(FrameRate);

        if (_edit.PreviewOverlayStrip(clip.Id, handle, g.StripFrames) is not { } preview) return;
        ShowStrip(clip, preview.Strip);
        var text = (IntervalOf(preview.Strip, handle) is { } span ? string.Format(TipOf(handle), Timecode(span.Start), Timecode(span.End)) : "") +
                   (preview.Error is { } error ? "  " + error : "");
        // The interval is shown where the segments are: on the plate in variant B, else over the clip.
        var shownOn = clip.StripInTemplate && strip.TemplateId is { } plate && _clipViewModels.TryGetValue(plate, out var plateVm) ? plateVm : clip;
        shownOn.IsStripInvalid = !preview.IsValid;
        shownOn.StripText = text;

        // The anchored template's rectangle follows its green / lilac.
        if (strip.TemplateId is { } id && _clipViewModels.TryGetValue(id, out var templateVm) && preview.Strip.Green is { } green)
        {
            var end = preview.Strip.Lilac?.End ?? green.End;
            templateVm.Layout(PixelsPerSecond, MediaTime.FromFrame(green.Start, FrameRate), MediaTime.FromFrame(end, FrameRate));
            if (preview.Strip.Lilac is { } lilac)
                templateVm.StatusMarkX = TimelineCoordinateMapper.TimeToX(MediaTime.FromFrame(lilac.Start - green.Start, FrameRate), PixelsPerSecond);
        }
    }

    /// <summary>The release: one edit (no step when the boundaries are where they were); a refusal reported, the strip back.</summary>
    private void EndStrip(Gesture g, OverlayStripHandle handle)
    {
        if (g.StripFrames != 0) Report(_edit.EditOverlayStrip(g.Clips[0].Id, handle, g.StripFrames));
        Relayout();
    }

    /// <summary>The frames a handle moves now (the first one is the one the snap's offset is measured on).</summary>
    private static long[] MovedFrames(OverlayStripState strip, OverlayStripHandle handle) => handle switch
    {
        OverlayStripHandle.GreenStart when strip.Green is { } g => new[] { g.Start },
        OverlayStripHandle.Status when strip.Lilac is { } l => new[] { l.Start },
        OverlayStripHandle.LilacEnd when strip.Lilac is { } l => new[] { l.End },
        OverlayStripHandle.LilacBody when strip.Lilac is { } l => new[] { l.Start, l.End },
        OverlayStripHandle.OrangeStart when strip.Orange is { } o => new[] { o.Start },
        OverlayStripHandle.OrangeEnd when strip.Orange is { } o => new[] { o.End },
        OverlayStripHandle.OrangeBody when strip.Orange is { } o => new[] { o.Start, o.End },
        _ => Array.Empty<long>()
    };

    private static FrameSpan? IntervalOf(OverlayStripState strip, OverlayStripHandle handle) => handle switch
    {
        OverlayStripHandle.GreenStart => strip.Green,
        OverlayStripHandle.Status or OverlayStripHandle.LilacEnd or OverlayStripHandle.LilacBody => strip.Lilac,
        _ => strip.Orange
    };

    private static string TipOf(OverlayStripHandle handle) => handle switch
    {
        OverlayStripHandle.GreenStart => Strings.Strip_Green,
        OverlayStripHandle.Status or OverlayStripHandle.LilacEnd or OverlayStripHandle.LilacBody => Strings.Strip_Lilac,
        _ => Strings.Strip_Orange
    };
}
