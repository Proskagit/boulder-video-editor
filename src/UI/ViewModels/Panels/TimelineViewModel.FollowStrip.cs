using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Playback;
using AiVideoEditor.UI.Common;
using AiVideoEditor.UI.Localization;

namespace AiVideoEditor.UI.ViewModels.Panels;

/// <summary>One followed part of a clip on the timeline: its index in the clip's Follow, pixels from the clip's left edge.</summary>
public sealed record FollowStripSegment(int Index, double Left, double Width, string Tip);

/// <summary>
/// D061 amendment 1 on the timeline: the followed parts of a video clip, cyan strips along the clip's bottom (one for Follow
/// on the whole clip). Each part's edges drag like a trim — the strip follows the pointer, limited by the clip and the
/// neighbouring parts; the release is one <c>SetClipFollow</c> (one Undo step; a click without a move edits nothing); Esc,
/// a lost pointer or the export's editing lock cancel. An edge dragged to the clip's edge opens that side again. The parts
/// are kept in source time, so Trim, Split, Slip and Speed keep them on the same frames; a part made longer is analysed
/// only where it grew (the people analysis works in chunks, D061 amendment 1).
/// </summary>
public sealed partial class TimelineViewModel
{
    public const string FollowHex = "#26C6DA";

    /// <summary>The edge being dragged: the clip, the part, which edge, the press's x, the part's edges then, its limits.</summary>
    private (TimelineClipViewModel Clip, int Index, bool Start, double PressX, double Left, double Right, double Min, double Max)? _followDrag;

    public bool IsFollowDragging => _followDrag is not null;

    /// <summary>The followed parts of every video clip at the current zoom.</summary>
    private void RefreshFollowStrips()
    {
        foreach (var vm in _clipViewModels.Values)
            if (_followDrag?.Clip != vm) ShowFollow(vm);
    }

    private void ShowFollow(TimelineClipViewModel vm)
    {
        var segments = new List<FollowStripSegment>();
        if (vm.Clip is VideoClip { Follow: { } follow } clip)
            for (var i = 0; i < follow.Parts.Length; i++)
            {
                if (follow.Parts[i].RangeIn(clip.SourceIn, clip.SourceOut) is not { } part) continue;
                var left = Math.Clamp(TimelineCoordinateMapper.TimeToX(OffsetOf(clip, part.From), PixelsPerSecond), 0, vm.Width);
                var right = Math.Min(TimelineCoordinateMapper.TimeToX(OffsetOf(clip, part.To), PixelsPerSecond), vm.Width);
                segments.Add(new FollowStripSegment(i, left, Math.Max(2, right - left),
                    string.Format(Strings.Strip_Follow, TimeFormat.ToTimecode(clip.TimelineStart + OffsetOf(clip, part.From), FrameRate),
                        TimeFormat.ToTimecode(clip.TimelineStart + OffsetOf(clip, part.To), FrameRate))));
            }
        vm.FollowSegments = segments;
    }

    /// <summary>The timeline offset from the clip's start of the frame showing source time <paramref name="source"/>.</summary>
    private static MediaTime OffsetOf(VideoClip clip, MediaTime source)
    {
        long a = clip.Speed.Numerator, b = clip.Speed.Denominator;
        return new MediaTime((long)((Int128)(source - clip.SourceIn).Ticks * b / a));
    }

    /// <summary>A press on the strips at <paramref name="xInClip"/> (from the clip's left edge): grabs the nearest part edge
    /// within reach; false elsewhere (the press is the clip's).</summary>
    public bool BeginFollowDrag(TimelineClipViewModel vm, double xInClip, double contentX)
    {
        if (!vm.HasFollowStrip || !CanEdit()) return false;
        var segments = vm.FollowSegments;
        (int Position, bool Start, double Distance)? best = null;
        for (var i = 0; i < segments.Count; i++)
            foreach (var (start, x) in new[] { (true, segments[i].Left), (false, segments[i].Left + segments[i].Width) })
            {
                var distance = Math.Abs(xInClip - x);
                if (distance <= StripHandleReachPixels && (best is null || distance < best.Value.Distance)) best = (i, start, distance);
            }
        if (best is not { } grab) return false;
        var segment = segments[grab.Position];
        var min = grab.Position > 0 ? segments[grab.Position - 1].Left + segments[grab.Position - 1].Width : 0;
        var max = grab.Position + 1 < segments.Count ? segments[grab.Position + 1].Left : vm.Width;
        _followDrag = (vm, grab.Position, grab.Start, contentX, segment.Left, segment.Left + segment.Width, min, max);
        return true;
    }

    public void UpdateFollowDrag(double contentX)
    {
        if (_followDrag is not { } drag) return;
        var shortest = Math.Max(2, TimelineCoordinateMapper.TimeToX(InspectorViewModel.MinFollowPart, PixelsPerSecond));
        var delta = contentX - drag.PressX;
        var vm = drag.Clip;
        double left = drag.Left, right = drag.Right;
        if (drag.Start) left = Math.Clamp(drag.Left + delta, drag.Min, Math.Max(drag.Min, right - shortest));
        else right = Math.Clamp(drag.Right + delta, Math.Min(drag.Max, left + shortest), drag.Max);
        var segments = vm.FollowSegments.ToList();
        segments[drag.Index] = segments[drag.Index] with { Left = left, Width = right - left };
        vm.FollowSegments = segments;
        vm.StripText = string.Format(Strings.Strip_Follow, TimeFormat.ToTimecode(FrameAt(vm, left), FrameRate),
            TimeFormat.ToTimecode(FrameAt(vm, right), FrameRate));
        vm.IsStripInvalid = false;
    }

    /// <summary>The release: the dragged edge's frame becomes the part's start / end (one Undo step); at the clip's edge, open.</summary>
    public void EndFollowDrag()
    {
        if (_followDrag is not { } drag) return;
        _followDrag = null;
        var vm = drag.Clip;
        vm.StripText = null;
        if (vm.Clip is not VideoClip { Follow: { } follow } clip || drag.Index >= vm.FollowSegments.Count)
        {
            ShowFollow(vm);
            return;
        }
        var segment = vm.FollowSegments[drag.Index];
        var (left, right) = (segment.Left, segment.Left + segment.Width);
        if (!CanEdit() || (Math.Abs(left - drag.Left) < 0.5 && Math.Abs(right - drag.Right) < 0.5) || segment.Index >= follow.Parts.Length)
        {
            ShowFollow(vm);                                  // a click on the edge, or an export started: no edit
            return;
        }
        var part = follow.Parts[segment.Index];
        // An edge on the neighbouring part's edge meets it exactly (the camera passes from one person to the other).
        MediaTime? Neighbour(bool start) => start
            ? (segment.Index > 0 ? follow.Parts[segment.Index - 1].To : null)
            : (segment.Index + 1 < follow.Parts.Length ? follow.Parts[segment.Index + 1].From : null);
        // At the clip's edge the side opens — up to a part beyond the clip's edge, if any (after a Split or a Trim).
        var changed = drag.Start
            ? part with { From = left <= 0.5 ? follow.OpenStart(segment.Index) : left <= drag.Min + 0.5 && Neighbour(true) is { } n ? n : SourceAt(clip, FrameAt(vm, left)) }
            : part with { To = right >= vm.Width - 0.5 ? follow.OpenEnd(segment.Index) : right >= drag.Max - 0.5 && Neighbour(false) is { } m ? m : SourceAt(clip, FrameAt(vm, right)) };
        var result = _edit.SetClipFollow(clip.Id, follow.WithPart(segment.Index, changed));
        if (!result.Success) _status.ReportWarning(result.Message ?? "");
        ShowFollow(vm);
    }

    public void CancelFollowDrag()
    {
        if (_followDrag is not { } drag) return;
        _followDrag = null;
        drag.Clip.StripText = null;
        ShowFollow(drag.Clip);
    }

    /// <summary>The timeline frame start nearest to <paramref name="xInClip"/> pixels into the clip.</summary>
    private MediaTime FrameAt(TimelineClipViewModel vm, double xInClip)
    {
        var time = vm.Clip.TimelineStart + TimelineCoordinateMapper.XToTime(xInClip, PixelsPerSecond);
        return MediaTime.FromFrame(time.ToNearestFrame(FrameRate), FrameRate);
    }

    private MediaTime SourceAt(VideoClip clip, MediaTime timelineFrame)
    {
        var source = HoldAnimationRule.SourceTimeAt(clip.TimelineStart, clip.SourceIn, clip.Speed, timelineFrame.ToFrameFloor(FrameRate), FrameRate);
        return source < clip.SourceIn ? clip.SourceIn : source > clip.SourceOut ? clip.SourceOut : source;
    }
}
