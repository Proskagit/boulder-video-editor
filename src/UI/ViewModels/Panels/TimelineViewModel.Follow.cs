using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.UI.Common;

namespace AiVideoEditor.UI.ViewModels.Panels;

// --- D058 §1, §2: the timeline follows the playhead while playing; the initial zoom -------------------------------------
public partial class TimelineViewModel
{
    /// <summary>The space left of the playhead after a page turn.</summary>
    public const double FollowLeftMarginPixels = 24;

    /// <summary>D058 §2: about this much of the timeline is seen when a project's zoom was never set.</summary>
    public static readonly TimeSpan InitialVisibleSpan = TimeSpan.FromMinutes(10);

    /// <summary>The user's setting «Таймлайн следует за плейхедом» (on by default); set by the shell.</summary>
    public Func<bool> FollowsPlayheadSetting { get; set; } = () => true;

    private bool _isPlaying;
    private bool _followSuspended;
    private bool _initialZoomPending;

    /// <summary>Playback started or stopped. A new start follows the playhead again after a pause of the following.</summary>
    public void SetPlaying(bool playing)
    {
        if (playing && !_isPlaying) _followSuspended = false;
        _isPlaying = playing;
    }

    /// <summary>Whether the timeline follows the playhead now (diagnostics, tests).</summary>
    public bool IsFollowingPlayhead => _isPlaying && !_followSuspended && FollowsPlayheadSetting();

    /// <summary>The user scrolled, zoomed, dragged or drew a marquee while playing: the following pauses until the next Play.</summary>
    public void SuspendFollowing()
    {
        if (_isPlaying) _followSuspended = true;
    }

    /// <summary>A page turn when the playhead left the visible part (past the right edge, or before the left one after a
    /// loop): the playhead goes to the left edge, the view never moves continuously with it.</summary>
    private void FollowPlayhead()
    {
        if (!IsFollowingPlayhead || _viewportWidth <= 2 * FollowLeftMarginPixels) return;
        var x = PlayheadX;
        if (x >= _scrollOffsetX && x <= _scrollOffsetX + _viewportWidth) return;
        var offset = Math.Max(0, x - FollowLeftMarginPixels);
        _scrollOffsetX = offset;   // the view applies it and reports it back through SetViewport
        ScrollRequested?.Invoke(this, offset);
    }

    /// <summary>D058 §2: a project whose zoom is still the default (never changed, or saved untouched) gets the zoom that
    /// shows <see cref="InitialVisibleSpan"/> once the timeline's width is known; a zoom the user set stays.</summary>
    private void PrepareInitialZoom()
    {
        _initialZoomPending = Sequence.ZoomPixelsPerSecond == Sequence.DefaultZoomPixelsPerSecond;
        ApplyInitialZoom();
    }

    private void ApplyInitialZoom()
    {
        if (!_initialZoomPending || _viewportWidth <= 40) return;
        _initialZoomPending = false;
        ApplyZoom(_viewportWidth / InitialVisibleSpan.TotalSeconds);
        ScrollRequested?.Invoke(this, 0);
    }
}
