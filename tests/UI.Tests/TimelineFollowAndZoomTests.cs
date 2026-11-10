using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Project;
using AiVideoEditor.Timeline;
using AiVideoEditor.UI.Common;
using AiVideoEditor.UI.Services;
using AiVideoEditor.UI.ViewModels.Panels;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiVideoEditor.UI.Tests;

/// <summary>
/// D058 §1–§2 (Phase 39): the timeline turns its page with the playhead while playing — the playhead to the left edge when
/// it passes the right one, never while paused, paused by the user's scroll / zoom / drag / marquee until the next Play,
/// off by the user's setting — and the initial zoom: about ten minutes across for a project whose zoom was never set, a
/// zoom the user set kept, adding clips never changing it.
/// </summary>
public sealed class TimelineFollowAndZoomTests
{
    private const double Viewport = 1200;

    private readonly UndoRedoService _undo = new();
    private readonly ProjectService _projects;
    private readonly TimelineEditService _edit;
    private readonly List<double> _scrolls = new();

    public TimelineFollowAndZoomTests()
    {
        _projects = new ProjectService(_undo, NullLogger<ProjectService>.Instance);
        _edit = new TimelineEditService(_projects, _undo, NullLogger<TimelineEditService>.Instance);
    }

    private TimelineViewModel Timeline(double viewport = Viewport)
    {
        var timeline = new TimelineViewModel(_projects, _edit, new StatusService(), NullLogger<TimelineViewModel>.Instance);
        timeline.ScrollRequested += (_, offset) =>
        {
            _scrolls.Add(offset);
            timeline.SetViewport(offset, viewport);   // as the view reports the applied offset back
        };
        timeline.SetViewport(0, viewport);
        return timeline;
    }

    private void AddVideo(double seconds)
    {
        var asset = new MediaAsset
        {
            FilePath = Path.Combine(Path.GetTempPath(), "aive-follow", Guid.NewGuid().ToString("N"), "v.mp4"), Kind = MediaKind.Video,
            AnalysisStatus = MediaAnalysisStatus.Completed,
            Metadata = new MediaMetadata { Duration = MediaTime.FromSeconds(seconds), FrameRate = FrameRate.Fps30, Width = 1920, Height = 1080 }
        };
        _projects.AddMediaAssets(new[] { asset });
        Assert.True(_edit.AddClip(asset.Id).Success);
    }

    // --- the initial zoom -----------------------------------------------------------------------------------------------

    [Fact]
    public void A_new_project_shows_about_ten_minutes_across_and_adding_clips_keeps_the_zoom()
    {
        var timeline = Timeline();
        Assert.Equal(Viewport / 600, timeline.PixelsPerSecond, 6);            // 10 min across the viewport
        Assert.Equal(timeline.PixelsPerSecond, _projects.Current.Timeline.ZoomPixelsPerSecond, 6);

        var zoom = timeline.PixelsPerSecond;
        AddVideo(47);
        AddVideo(900);                                                         // 15 min: longer than the view
        Assert.Equal(zoom, timeline.PixelsPerSecond, 6);
        timeline.SetViewport(0, 900);                                          // a resize later never re-applies it
        Assert.Equal(zoom, timeline.PixelsPerSecond, 6);
    }

    [Fact]
    public void A_zoom_the_user_set_stays_and_a_project_saved_at_the_default_gets_the_initial_one()
    {
        _projects.Current.Timeline.ZoomPixelsPerSecond = 37;                   // the user's zoom, saved with the project
        var kept = Timeline();
        Assert.Equal(37, kept.PixelsPerSecond, 6);

        _projects.Current.Timeline.ZoomPixelsPerSecond = Sequence.DefaultZoomPixelsPerSecond;   // never changed
        var initial = Timeline();
        Assert.Equal(Viewport / 600, initial.PixelsPerSecond, 6);

        initial.ZoomInCommand.Execute(null);                                   // the user's choice from then on
        var chosen = initial.PixelsPerSecond;
        AddVideo(30);
        Assert.Equal(chosen, initial.PixelsPerSecond, 6);
    }

    [Fact]
    public void A_narrow_timeline_still_reaches_ten_minutes()
    {
        var timeline = Timeline(viewport: 700);
        Assert.Equal(700.0 / 600, timeline.PixelsPerSecond, 6);              // above the minimum of 1 px/s
        Assert.True(TimelineCoordinateMapper.MinPixelsPerSecond <= 1);
    }

    // --- following the playhead -------------------------------------------------------------------------------------------

    private TimelineViewModel Playing(out double pixelsPerSecond)
    {
        AddVideo(3600);
        var timeline = Timeline();
        pixelsPerSecond = timeline.PixelsPerSecond;                            // 2 px/s: the right edge at 600 s
        _scrolls.Clear();
        timeline.SetPlaying(true);
        return timeline;
    }

    [Fact]
    public void While_playing_the_page_turns_at_the_right_edge_and_the_playhead_goes_to_the_left_edge()
    {
        var timeline = Playing(out var pps);
        timeline.ShowPlaybackPosition(MediaTime.FromSeconds(300));            // inside the view: nothing
        Assert.Empty(_scrolls);
        timeline.ShowPlaybackPosition(MediaTime.FromSeconds(601));            // past the right edge
        var offset = Assert.Single(_scrolls);
        Assert.Equal(601 * pps - TimelineViewModel.FollowLeftMarginPixels, offset, 3);
        timeline.ShowPlaybackPosition(MediaTime.FromSeconds(700));            // on the new page: nothing more
        Assert.Single(_scrolls);
        timeline.ShowPlaybackPosition(MediaTime.FromSeconds(1202));           // the next page
        Assert.Equal(2, _scrolls.Count);
    }

    [Fact]
    public void Paused_the_timeline_never_scrolls_by_itself()
    {
        var timeline = Playing(out _);
        timeline.SetPlaying(false);
        timeline.ShowPlaybackPosition(MediaTime.FromSeconds(900));
        Assert.Empty(_scrolls);
    }

    [Fact]
    public void The_users_scroll_zoom_or_marquee_pause_the_following_until_the_next_play()
    {
        var timeline = Playing(out _);
        timeline.SuspendFollowing();                                           // the wheel / the scroll bar (the view calls it)
        timeline.ShowPlaybackPosition(MediaTime.FromSeconds(700));
        Assert.Empty(_scrolls);
        Assert.False(timeline.IsFollowingPlayhead);

        timeline.SetPlaying(false);
        timeline.SetPlaying(true);                                             // the next Play follows again
        Assert.True(timeline.IsFollowingPlayhead);
        timeline.ShowPlaybackPosition(MediaTime.FromSeconds(1300));
        Assert.Single(_scrolls);

        timeline.ZoomOutCommand.Execute(null);                                 // a zoom while playing pauses it too
        Assert.False(timeline.IsFollowingPlayhead);
        timeline.SetPlaying(false);
        timeline.SetPlaying(true);
        timeline.BeginMarquee(10, 10, additive: false);                        // and a marquee
        Assert.False(timeline.IsFollowingPlayhead);
    }

    [Fact]
    public void The_users_setting_turns_the_following_off()
    {
        var timeline = Playing(out _);
        timeline.FollowsPlayheadSetting = () => false;
        timeline.ShowPlaybackPosition(MediaTime.FromSeconds(900));
        Assert.Empty(_scrolls);
        Assert.True(new UserSettings().TimelineFollowsPlayhead);              // on by default
    }
}
