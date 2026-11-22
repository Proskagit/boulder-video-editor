using System.Collections.ObjectModel;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Composition;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Core.Localization;
using AiVideoEditor.UI.Rendering;
using AiVideoEditor.Core.Playback;
using AiVideoEditor.Core.Telemetry;
using AiVideoEditor.UI.Common;
using AiVideoEditor.UI.Localization;
using AiVideoEditor.UI.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace AiVideoEditor.UI.ViewModels.Panels;

/// <summary>The move and trim gestures (split out of TimelineViewModel.cs, D075 amendment 1 — no change).</summary>
public sealed partial class TimelineViewModel
{
    // --- Gestures (move / trim) ---------------------------------------------------

    private sealed class Gesture
    {
        public required ClipEdge? Edge { get; init; }           // null = move
        public required double StartX { get; init; }
        public required List<TimelineClipViewModel> Clips { get; init; }
        public required TimelineTrackViewModel? SourceTrack { get; init; } // single-track selection only
        public bool Active { get; set; }
        public long FrameDelta { get; set; }
        public TimelineTrackViewModel? TargetTrack { get; set; }

        /// <summary>D071 amendment 3: the pointer is above the top track — the clips go onto a new video track on top.</summary>
        public bool ToNewTrack { get; set; }

        public MediaTime TrimTime { get; set; }

        /// <summary>A ripple trim (D030 §6): Shift was held when the edge was pressed; fixed for the whole gesture.</summary>
        public bool Ripple { get; init; }

        /// <summary>Alt + edge (D032 AQ3): the trim leaves the link partner as it is.</summary>
        public bool Alone { get; init; }

        /// <summary>A slip (D030 §7): Alt was held when the clip's body was pressed; fixed for the whole gesture.</summary>
        public bool Slip { get; init; }

        /// <summary>The slip's frames so far (positive: later source content).</summary>
        public long SlipFrames { get; set; }

        /// <summary>A strip handle's drag (D048 §4.2): which handle; the frames so far (snapped).</summary>
        public OverlayStripHandle? StripHandle { get; init; }

        /// <summary>D053: the route whose strip is dragged (the plate's, or the lane's).</summary>
        public Guid? StripRouteId { get; init; }

        public long StripFrames { get; set; }
    }

    public void BeginMove(double contentX)
    {
        if (!CanEdit()) return;
        _lastDragRefusal = null;
        var clips = _selection.Where(_clipViewModels.ContainsKey).Select(id => _clipViewModels[id]).ToList();
        if (clips.Count == 0) return;
        var tracks = clips.Select(TrackOf).Distinct().ToList();
        _gesture = new Gesture { Edge = null, StartX = contentX, Clips = clips, SourceTrack = tracks.Count == 1 ? tracks[0] : null };
    }

    /// <summary>Pointer pressed on a clip's edge: a trim — the ordinary one, or with <paramref name="ripple"/> (Shift held at
    /// the press, D030 §6) the ripple trim, for the whole gesture. A linked clip's partner follows (D032 AQ3) unless
    /// <paramref name="alone"/> (Alt): then only this clip is trimmed — a J / L cut — and only it is selected.</summary>
    public void BeginTrim(TimelineClipViewModel clip, ClipEdge edge, double contentX, bool ripple = false, bool alone = false)
    {
        if (!CanEdit()) return;
        alone = alone && PartnerViewModel(clip) is not null;   // AQ3: Alt changes only a linked clip's edge (Shift + Alt ripples as before)
        SelectOnly(clip.Id, withPartner: !alone);
        _gesture = new Gesture
        {
            Edge = edge, StartX = contentX, Clips = new List<TimelineClipViewModel> { clip }, SourceTrack = TrackOf(clip),
            TrimTime = edge == ClipEdge.Start ? clip.Clip.TimelineStart : clip.Clip.TimelineEnd,
            // D069 §6: on the magnetic main track every trim ripples (the clips after it follow, no gap, no overlap).
            Ripple = (ripple || IsOnMagneticMainTrack(clip)) && !alone, Alone = alone
        };
    }

    private bool IsOnMagneticMainTrack(TimelineClipViewModel clip) =>
        Sequence.MagneticMainTrack && Sequence.VideoTracks.OrderBy(t => t.Order).FirstOrDefault() is { } main && main.Clips.Contains(clip.Clip);

    /// <summary>Pointer pressed on a clip's body with Alt (D030 §7): a slip of that clip, which becomes the selection.
    /// False — no gesture, the status says why — for an image or a text, which have no source to slip.</summary>
    public bool BeginSlip(TimelineClipViewModel clip, double contentX)
    {
        if (!CanEdit()) return false;
        SelectOnly(clip.Id);
        if (clip.Clip is not (VideoClip or AudioClip))
        {
            _status.ReportWarning(Strings.Slip_OnlyMedia);
            return false;
        }
        _gesture = new Gesture
        {
            Edge = null, StartX = contentX, Clips = new List<TimelineClipViewModel> { clip }, SourceTrack = TrackOf(clip), Slip = true
        };
        return true;
    }

    public void UpdateGesture(double contentX, TimelineTrackViewModel? trackUnderPointer, bool aboveTracks = false)
    {
        SuspendFollowing();   // D058 §1: a drag while playing
        if (_gesture is not { } g) return;
        // D071 amendment 3: above the top track (the ruler) a move of video clips from one track goes onto a new track on top.
        g.ToNewTrack = aboveTracks && trackUnderPointer is null && g.Edge is null && !g.Slip && g.StripHandle is null &&
                       g.SourceTrack is { Track.Type: TrackType.Video };
        if (!g.Active && Math.Abs(contentX - g.StartX) < DragThresholdPixels && trackUnderPointer == g.SourceTrack)
            return;
        g.Active = true;

        var rawDelta = TimelineCoordinateMapper.XToTime(contentX - g.StartX, PixelsPerSecond);
        if (g.StripHandle is { } handle) UpdateStrip(g, handle, rawDelta);
        else if (g.Slip) UpdateSlip(g, rawDelta);
        else if (g.Edge is { } edge) UpdateTrim(g, edge, rawDelta);
        else UpdateMove(g, MagnetDelta(g, contentX, trackUnderPointer) ?? rawDelta, trackUnderPointer);
    }

    /// <summary>D069 amendment 1: a drag onto the magnetic main track takes the pointer as the dragged clips' start (as CapCut
    /// does) — where the pointer is, not where the grabbed point of a long clip is, decides the cut the clips go to. Null:
    /// any other drag (the grabbed point keeps its offset).</summary>
    private MediaTime? MagnetDelta(Gesture g, double contentX, TimelineTrackViewModel? trackUnderPointer)
    {
        if (!Sequence.MagneticMainTrack || Sequence.VideoTracks.OrderBy(t => t.Order).FirstOrDefault() is not { } main) return null;
        var target = g.SourceTrack is not null && trackUnderPointer is not null && trackUnderPointer != g.SourceTrack ? trackUnderPointer : g.SourceTrack;
        if (target?.Track != main) return null;
        var pointer = TimelineCoordinateMapper.XToTime(Math.Max(0, contentX), PixelsPerSecond);
        return pointer - g.Clips.Min(c => c.Clip.TimelineStart);
    }

    private void UpdateMove(Gesture g, MediaTime rawDelta, TimelineTrackViewModel? trackUnderPointer)
    {
        var groupStart = g.Clips.Min(c => c.Clip.TimelineStart);
        var groupEnd = g.Clips.Max(c => c.Clip.TimelineEnd);
        var startFrame = groupStart.ToNearestFrame(FrameRate);
        long FramesOf(MediaTime delta) => Math.Max(-startFrame, (groupStart + delta).ToNearestFrame(FrameRate) - startFrame);

        g.TargetTrack = g.SourceTrack is not null && trackUnderPointer is not null && trackUnderPointer != g.SourceTrack
            ? trackUnderPointer
            : null;
        ShowDropTarget(g.TargetTrack);
        IsNewTrackDropTarget = g.ToNewTrack;

        // D037 §2: a snap never makes an overlap — a target that would push a clip (or refuse the move) where the pointer's
        // own place would not is skipped.
        var ids = g.Clips.Select(c => c.Id).ToList();
        var target = MoveTarget(g);
        var rawOverlaps = MoveOverlaps(ids, FramesOf(rawDelta), target);
        var snapped = ApplySnap(new[] { groupStart + rawDelta, groupEnd + rawDelta }, g.Clips, rawDelta,
            accept: rawOverlaps ? null : s => !MoveOverlaps(ids, FramesOf(rawDelta + s.Offset), target));
        g.FrameDelta = FramesOf(snapped);

        // D037 §3: the preview shows every clip the release would change — the dragged ones, the partners that follow and
        // the clips that make room — at its planned place; a refused move shows the dragged clips where the pointer has
        // them, marked invalid.
        var preview = _edit.PreviewMove(ids, g.FrameDelta, target, insertOnDrop: true);
        ReportDragRefusal(preview is null ? _edit.CanMoveClips(ids, g.FrameDelta, target, insertOnDrop: true) : null);
        var planned = preview?.Clips ?? g.Clips.ToDictionary(c => c.Id, c => (
            MediaTime.FromFrame(c.Clip.TimelineStart.ToNearestFrame(FrameRate) + g.FrameDelta, FrameRate),
            MediaTime.FromFrame(c.Clip.TimelineEnd.ToNearestFrame(FrameRate) + g.FrameDelta, FrameRate)));
        foreach (var vm in _clipViewModels.Values)
        {
            if (planned.TryGetValue(vm.Id, out var at)) vm.Layout(PixelsPerSecond, at.Start, at.End);
            else vm.Layout(PixelsPerSecond);
        }
        foreach (var clip in g.Clips) clip.IsInvalid = preview is null;
        IsNewTrackDropInvalid = g.ToNewTrack && preview is null;
        ShowMoveGhost(g, planned, preview is null, preview is { } shown && shown.Clips.Keys.Except(ids).Any(id => !IsPartnerOfDragged(id)));

        // What follows the dragged clips by their own rules — a link partner, a plate anchored to them — is not "moving others".
        bool IsPartnerOfDragged(Guid id) =>
            g.Clips.Any(c => PartnerViewModel(c) is { } partner && partner.Id == id) ||
            _clipViewModels.TryGetValue(id, out var follower) && follower.Clip is TemplateClip { AnchorClipId: { } anchor } && ids.Contains(anchor);

        // Dissolves follow the preview (D025 §5): a zone whose two clips move by the same frames moves along; a zone whose
        // cut the move would open is hidden (the release removes it — never one of clips that only make room, D037 Q9).
        foreach (var zone in _transitionViewModels.Values)
        {
            var (left, right) = (FramesMoved(zone.Transition.LeftClipId), FramesMoved(zone.Transition.RightClipId));
            zone.IsVisible = left == right;
            zone.Layout(PixelsPerSecond, FrameRate, zone.IsVisible ? left : 0);
        }

        long FramesMoved(Guid id) =>
            planned.TryGetValue(id, out var at) && _clipViewModels.TryGetValue(id, out var vm)
                ? at.Start.ToNearestFrame(FrameRate) - vm.Clip.TimelineStart.ToNearestFrame(FrameRate)
                : 0;
    }

    /// <summary>D071 amendment 3: the move's target track — a new one on top while the pointer is above the tracks.</summary>
    private static Guid? MoveTarget(Gesture g) => g.ToNewTrack ? MoveTargets.NewVideoTrack : g.TargetTrack?.Track.Id;

    /// <summary>D071 amendment 3: the bar above the tracks says a release puts the dragged clips onto a new track on top
    /// (red when that is refused).</summary>
    [ObservableProperty] private bool _isNewTrackDropTarget;
    [ObservableProperty] private bool _isNewTrackDropInvalid;

    /// <summary>
    /// D071 amendment 2 (the owner): a clip dragged to another track shows its future place on that track — the span of the dragged
    /// clips where the release puts them; marked when it moves other clips, red when the move is refused. Within one track the
    /// dragged clips themselves show it, as before.
    /// </summary>
    private void ShowMoveGhost(Gesture g, IReadOnlyDictionary<Guid, (MediaTime Start, MediaTime End)> planned, bool refused, bool movesOthers)
    {
        var target = g.TargetTrack;
        var spans = g.Clips.Where(c => planned.ContainsKey(c.Id)).Select(c => planned[c.Id]).ToList();
        foreach (var t in Tracks)
        {
            if (t == target && spans.Count > 0)
            {
                var start = spans.Min(s => s.Start);
                var end = spans.Max(s => s.End);
                t.DropGhostLeft = TimelineCoordinateMapper.TimeToX(start, PixelsPerSecond);
                t.DropGhostWidth = Math.Max(2, TimelineCoordinateMapper.TimeToX(end - start, PixelsPerSecond));
                t.DropGhostMovesOthers = movesOthers && !refused;
                t.DropGhostInvalid = refused;
                t.HasDropGhost = true;
            }
            else t.HasDropGhost = false;
        }
    }

    /// <summary>Whether moving <paramref name="ids"/> by <paramref name="frames"/> would push a clip that is not moved with
    /// them (D037 §3) or be refused — what a snap must not cause on its own (D037 §2).</summary>
    private bool MoveOverlaps(IReadOnlyCollection<Guid> ids, long frames, Guid? targetTrackId)
    {
        if (_edit.PreviewMove(ids, frames, targetTrackId, insertOnDrop: true) is not { } preview) return true;
        var moving = ids.ToHashSet();
        foreach (var id in ids)
            if (_clipViewModels.TryGetValue(id, out var vm) && PartnerViewModel(vm) is { } partner)
                moving.Add(partner.Id);
        return preview.Clips.Keys.Any(id => !moving.Contains(id));
    }

    private void UpdateTrim(Gesture g, ClipEdge edge, MediaTime rawDelta)
    {
        var clip = g.Clips[0];
        var origin = edge == ClipEdge.Start ? clip.Clip.TimelineStart : clip.Clip.TimelineEnd;
        var proposed = origin + ApplySnap(new[] { origin + rawDelta }, g.Clips, rawDelta);
        g.TrimTime = proposed;

        if (g.Ripple)
        {
            PreviewRipple(g, clip, edge, proposed);
            return;
        }

        if (_edit.PreviewTrim(clip.Id, edge, proposed, g.Alone) is { } preview)
        {
            clip.Layout(PixelsPerSecond, preview.Start, preview.End);
            clip.IsInvalid = false;

            // D032 AQ3: the partner's same edge follows by the same time (the release plans it by the trim rule).
            if (!g.Alone && PartnerViewModel(clip) is { } partner)
            {
                var moved = (edge == ClipEdge.Start ? preview.Start : preview.End) - origin;
                partner.Layout(PixelsPerSecond,
                    edge == ClipEdge.Start ? partner.Clip.TimelineStart + moved : partner.Clip.TimelineStart,
                    edge == ClipEdge.End ? partner.Clip.TimelineEnd + moved : partner.Clip.TimelineEnd);
            }

            // Trimming the cut edge opens the cut: its dissolve is hidden in the preview (the release removes it). A
            // far-edge trim keeps the cut, its zone stays where it is.
            var cutEdge = edge == ClipEdge.End ? clip.Clip.TimelineEnd : clip.Clip.TimelineStart;
            var opened = (edge == ClipEdge.End ? preview.End : preview.Start) != cutEdge;
            foreach (var zone in _transitionViewModels.Values)
                if (edge == ClipEdge.End ? zone.Transition.LeftClipId == clip.Id : zone.Transition.RightClipId == clip.Id)
                    zone.IsVisible = !opened;
        }
        else
        {
            clip.IsInvalid = true;
        }
    }

    /// <summary>The slip's feedback (D030 §7, Q12): the content follows the pointer (the pointer to the right brings in
    /// earlier source: a negative slip), in whole timeline frames; the edit service plans it (nothing applied, so the
    /// timeline geometry and the Preview stay) and the clip shows the planned Source In / Out and a limit's note.</summary>
    private void UpdateSlip(Gesture g, MediaTime rawDelta)
    {
        var clip = g.Clips[0];
        g.SlipFrames = -rawDelta.ToNearestFrame(FrameRate);
        var preview = _edit.PreviewSlip(clip.Id, g.SlipFrames);
        clip.IsInvalid = preview is null;
        clip.SlipText = preview is null ? Strings.Slip_Cannot
            : string.Format(Strings.Slip_Range, TimeFormat.ToTimecode(preview.SourceIn, FrameRate), TimeFormat.ToTimecode(preview.SourceOut, FrameRate)) +
              (preview.Note is null ? "" : "  " + Strings.Slip_Limit);
    }

    /// <summary>The ripple drag's preview (D030 §6): the edit service plans the trim (nothing is applied) and the view
    /// shows the trimmed clip and every clip it would move at their planned places, the dissolve zones with their clips;
    /// the rest of the timeline as it is. A refused plan (a locked track) marks the clip invalid.</summary>
    private void PreviewRipple(Gesture g, TimelineClipViewModel clip, ClipEdge edge, MediaTime proposed)
    {
        var preview = _edit.PreviewRippleTrim(clip.Id, edge, proposed);
        var planned = preview?.Clips ?? new Dictionary<Guid, (MediaTime Start, MediaTime End)>();
        foreach (var vm in _clipViewModels.Values)
        {
            if (planned.TryGetValue(vm.Id, out var at)) vm.Layout(PixelsPerSecond, at.Start, at.End);
            else vm.Layout(PixelsPerSecond);
        }
        clip.IsInvalid = preview is null;

        // A zone sits on its cut: it moves with B (the clip starting at the cut) — a ripple never opens a cut.
        foreach (var zone in _transitionViewModels.Values)
        {
            var right = zone.Track.Clips.FirstOrDefault(c => c.Id == zone.Transition.RightClipId);
            var delta = right is not null && planned.TryGetValue(right.Id, out var at)
                ? at.Start.ToNearestFrame(FrameRate) - right.TimelineStart.ToNearestFrame(FrameRate)
                : 0;
            zone.IsVisible = true;
            zone.Layout(PixelsPerSecond, FrameRate, delta);
        }
    }

    /// <summary>Returns <paramref name="rawDelta"/> adjusted so the nearest candidate edge
    /// lands on a snap target (when snapping is on), and shows the snap indicator.</summary>
    private MediaTime ApplySnap(IReadOnlyList<MediaTime> candidates, IEnumerable<TimelineClipViewModel> dragged, MediaTime rawDelta,
        Func<SnapResult, bool>? accept = null)
    {
        IsSnapIndicatorVisible = false;
        if (!SnappingEnabled) return rawDelta;

        var tolerance = TimelineCoordinateMapper.XToTime(SnapTolerancePixels, PixelsPerSecond);
        var snap = _edit.Snap(candidates, tolerance, dragged.Select(c => c.Id).ToList(), accept);
        if (!snap.Snapped) return rawDelta;

        SnapIndicatorX = TimelineCoordinateMapper.TimeToX(snap.Target, PixelsPerSecond);
        IsSnapIndicatorVisible = true;
        return rawDelta + snap.Offset;
    }

    public void EndGesture()
    {
        if (_gesture is not { Active: true } g)
        {
            _gesture = null;
            return;
        }

        _gesture = null;
        ResetGestureVisuals(g);
        if (g.StripHandle is { } stripHandle)
        {
            EndStrip(g, stripHandle);
            return;
        }

        var result = g.Slip
            ? _edit.SlipClip(g.Clips[0].Id, g.SlipFrames)
            : g.Edge is { } edge
            ? g.Ripple
                ? _edit.RippleTrimClip(g.Clips[0].Id, edge, g.TrimTime)
                : _edit.TrimClip(g.Clips[0].Id, edge, g.TrimTime, g.Alone)
            : _edit.MoveClips(g.Clips.Select(c => c.Id).ToList(), g.FrameDelta, MoveTarget(g), insertOnDrop: true);
        Report(result);
        // Restore the pre-drag positions — also when nothing changed (D069 amendment 1: a drag the magnet took back left the
        // carried plates where the preview had them).
        if (!result.Success || result.NoChange) Relayout();
    }

    public void CancelGesture()
    {
        if (_gesture is not { } g) return;
        _gesture = null;
        ResetGestureVisuals(g);
        Relayout();
    }

    public bool IsGestureActive => _gesture is { Active: true } || IsTransitionResizing;

    private void ResetGestureVisuals(Gesture g)
    {
        IsSnapIndicatorVisible = false;
        IsNewTrackDropTarget = false;
        IsNewTrackDropInvalid = false;
        ClearDropTargets();
        foreach (var clip in g.Clips)
        {
            clip.IsInvalid = false;
            clip.SlipText = null;
        }
        foreach (var vm in _clipViewModels.Values)   // a strip drag may show its interval on the template (variant B)
        {
            vm.StripText = null;
            vm.IsStripInvalid = false;
        }
        foreach (var zone in _transitionViewModels.Values) zone.IsVisible = true;
    }

    private TimelineTrackViewModel? TrackOf(TimelineClipViewModel clip) =>
        Tracks.FirstOrDefault(t => t.Clips.Contains(clip));
}
