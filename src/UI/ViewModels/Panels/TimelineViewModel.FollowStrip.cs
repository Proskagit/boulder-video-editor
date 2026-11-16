using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Playback;
using AiVideoEditor.UI.Common;
using AiVideoEditor.UI.Localization;

namespace AiVideoEditor.UI.ViewModels.Panels;

/// <summary>
/// D061 amendment 1 on the timeline: the followed part of a video clip with Follow, a cyan strip along the clip's bottom
/// (the whole clip, or the part «Следить отсюда» / «Остановить слежение здесь» set). Its two edges drag like a trim — the
/// strip follows the pointer, the release is one <c>SetClipFollow</c> (one Undo step); Esc or a lost pointer cancel. An edge
/// dragged to the clip's edge makes that side open again (from the clip's start / to its end). The part is kept in source
/// time, so Trim, Split, Slip and Speed keep it on the same frames; the video's people were analysed whole (D061 §5), so a
/// longer part needs no analysis of its own.
/// </summary>
public sealed partial class TimelineViewModel
{
    public const string FollowHex = "#26C6DA";

    /// <summary>The followed part's edge being dragged: the clip, which edge, the press's x, the strip's edges then.</summary>
    private (TimelineClipViewModel Clip, bool Start, double PressX, double Left, double Right)? _followDrag;

    public bool IsFollowDragging => _followDrag is not null;

    /// <summary>The followed part of every video clip at the current zoom.</summary>
    private void RefreshFollowStrips()
    {
        foreach (var vm in _clipViewModels.Values)
            if (_followDrag?.Clip != vm) ShowFollow(vm);
    }

    private void ShowFollow(TimelineClipViewModel vm)
    {
        if (vm.Clip is not VideoClip { Follow: { } follow } clip || follow.RangeIn(clip.SourceIn, clip.SourceOut) is not { } part)
        {
            vm.HasFollowStrip = false;
            return;
        }
        var left = TimelineCoordinateMapper.TimeToX(OffsetOf(clip, part.From), PixelsPerSecond);
        var right = TimelineCoordinateMapper.TimeToX(OffsetOf(clip, part.To), PixelsPerSecond);
        vm.FollowLeft = Math.Clamp(left, 0, vm.Width);
        vm.FollowWidth = Math.Max(2, Math.Min(right, vm.Width) - vm.FollowLeft);
        vm.FollowTip = string.Format(Strings.Strip_Follow, TimeFormat.ToTimecode(clip.TimelineStart + OffsetOf(clip, part.From), FrameRate),
            TimeFormat.ToTimecode(clip.TimelineStart + OffsetOf(clip, part.To), FrameRate));
        vm.HasFollowStrip = true;
    }

    /// <summary>The timeline offset from the clip's start of the frame showing source time <paramref name="source"/>.</summary>
    private static MediaTime OffsetOf(VideoClip clip, MediaTime source)
    {
        long a = clip.Speed.Numerator, b = clip.Speed.Denominator;
        return new MediaTime((long)((Int128)(source - clip.SourceIn).Ticks * b / a));
    }

    /// <summary>A press on the strip at <paramref name="xInClip"/> (from the clip's left edge): grabs the nearer edge within
    /// reach; false elsewhere (the press is the clip's).</summary>
    public bool BeginFollowDrag(TimelineClipViewModel vm, double xInClip, double contentX)
    {
        if (!vm.HasFollowStrip || !CanEdit()) return false;
        var (left, right) = (vm.FollowLeft, vm.FollowLeft + vm.FollowWidth);
        var toStart = Math.Abs(xInClip - left);
        var toEnd = Math.Abs(xInClip - right);
        if (Math.Min(toStart, toEnd) > StripHandleReachPixels) return false;
        _followDrag = (vm, toStart <= toEnd, contentX, left, right);
        return true;
    }

    public void UpdateFollowDrag(double contentX)
    {
        if (_followDrag is not { } drag) return;
        var min = Math.Max(2, TimelineCoordinateMapper.TimeToX(InspectorViewModel.MinFollowPart, PixelsPerSecond));
        var delta = contentX - drag.PressX;
        var vm = drag.Clip;
        double left = drag.Left, right = drag.Right;
        if (drag.Start) left = Math.Clamp(drag.Left + delta, 0, Math.Max(0, right - min));
        else right = Math.Clamp(drag.Right + delta, Math.Min(vm.Width, left + min), vm.Width);
        vm.FollowLeft = left;
        vm.FollowWidth = right - left;
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
        if (vm.Clip is not VideoClip { Follow: { } follow } clip)
        {
            ShowFollow(vm);
            return;
        }
        var (left, right) = (vm.FollowLeft, vm.FollowLeft + vm.FollowWidth);
        if (!CanEdit() || (Math.Abs(left - drag.Left) < 0.5 && Math.Abs(right - drag.Right) < 0.5))
        {
            ShowFollow(vm);                                  // a click on the edge, or an export started: no edit
            return;
        }
        var changed = drag.Start
            ? follow with { From = left <= 0.5 ? null : SourceAt(clip, FrameAt(vm, left)) }
            : follow with { To = right >= vm.Width - 0.5 ? null : SourceAt(clip, FrameAt(vm, right)) };
        var result = _edit.SetClipFollow(clip.Id, changed);
        if (!result.Success) _status.Report(result.Message ?? "");
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
