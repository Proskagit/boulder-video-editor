using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Project;
using AiVideoEditor.Timeline;
using AiVideoEditor.UI.Services;
using AiVideoEditor.UI.ViewModels.Panels;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiVideoEditor.UI.Tests;

/// <summary>
/// Phase 12 Step 12.5 (D027 §2): Ripple Delete and Close Gap in the timeline header. Ripple Delete works on the
/// selected clips (also on several tracks) and clears the selection; Close Gap needs exactly one selected clip and
/// closes the gap right before it; the edit service's messages are shown when it refuses; both are disabled during an
/// export.
/// </summary>
public sealed class TimelineRippleUiTests
{
    private static readonly FrameRate Rate = FrameRate.Default;

    private readonly UndoRedoService _undo = new();
    private readonly ProjectService _projects;
    private readonly TimelineEditService _edit;
    private readonly StatusService _status = new();
    private readonly EditingLock _lock = new();

    public TimelineRippleUiTests()
    {
        _projects = new ProjectService(_undo, NullLogger<ProjectService>.Instance);
        _edit = new TimelineEditService(_projects, _undo, NullLogger<TimelineEditService>.Instance);
    }

    private TimelineViewModel Timeline() => new(_projects, _edit, _status, NullLogger<TimelineViewModel>.Instance, _lock);

    private Track V1 => _projects.Current.Timeline.VideoTracks[0];
    private static MediaTime F(long n) => MediaTime.FromFrame(n, Rate);
    private static long Start(Clip c) => c.TimelineStart.ToNearestFrame(Rate);

    private static TextClip Text(Track track, long start, long end)
    {
        var clip = new TextClip { Text = "T", TimelineStart = F(start), Duration = F(end - start) };
        track.Clips.Add(clip);
        return clip;
    }

    private static void Select(TimelineViewModel timeline, params Clip[] clips)
    {
        foreach (var clip in clips)
            timeline.OnClipPressed(timeline.Tracks.SelectMany(t => t.Clips).Single(c => c.Id == clip.Id), toggle: true);
    }

    [Fact]
    public void Ripple_delete_needs_a_selection_and_is_disabled_during_an_export()
    {
        var a = Text(V1, 0, 10);
        var timeline = Timeline();
        Assert.False(timeline.RippleDeleteCommand.CanExecute(null));

        Select(timeline, a);
        Assert.True(timeline.RippleDeleteCommand.CanExecute(null));
        using (_lock.Acquire())
            Assert.False(timeline.RippleDeleteCommand.CanExecute(null));
        Assert.True(timeline.RippleDeleteCommand.CanExecute(null));
    }

    [Fact]
    public void Ripple_delete_of_clips_on_two_tracks_closes_each_track_and_clears_the_selection()
    {
        Assert.True(_edit.AddTrack(TrackType.Video).Success);
        var v2 = _projects.Current.Timeline.VideoTracks[^1];
        var a = Text(V1, 0, 10);
        var b = Text(V1, 10, 30);
        var c = Text(v2, 0, 20);
        var d = Text(v2, 20, 25);
        var timeline = Timeline();
        Select(timeline, a, c);

        timeline.RippleDeleteCommand.Execute(null);

        Assert.Equal(new Clip[] { b }, V1.Clips);
        Assert.Equal(new Clip[] { d }, v2.Clips);
        Assert.Equal((0L, 0L), (Start(b), Start(d)));
        Assert.False(timeline.HasSelection);
        Assert.Equal("2 clips ripple deleted", _status.Message);

        _undo.Undo();                                                    // one step
        Assert.Equal(4, V1.Clips.Count + v2.Clips.Count);
        Assert.Equal((10L, 20L), (Start(b), Start(d)));
    }

    [Fact]
    public void A_refused_ripple_delete_shows_the_service_message()
    {
        var a = Text(V1, 0, 10);
        V1.IsLocked = true;
        var timeline = Timeline();
        Select(timeline, a);

        timeline.RippleDeleteCommand.Execute(null);

        Assert.Equal("Track V1 is locked.", _status.Message);
        Assert.Contains(a, V1.Clips);
        Assert.True(timeline.HasSelection);                              // kept when nothing happened
    }

    [Fact]
    public void Close_gap_needs_exactly_one_selected_clip()
    {
        var a = Text(V1, 0, 10);
        var b = Text(V1, 20, 30);
        var timeline = Timeline();
        Assert.False(timeline.CloseGapCommand.CanExecute(null));

        Select(timeline, b);
        Assert.True(timeline.CloseGapCommand.CanExecute(null));
        Select(timeline, a);                                             // two selected
        Assert.False(timeline.CloseGapCommand.CanExecute(null));
        using (_lock.Acquire())
        {
            Select(timeline, a);                                         // back to one, but exporting
            Assert.False(timeline.CloseGapCommand.CanExecute(null));
        }
        Assert.True(timeline.CloseGapCommand.CanExecute(null));
    }

    [Fact]
    public void Close_gap_closes_the_gap_before_the_selected_clip_and_keeps_the_selection()
    {
        Text(V1, 0, 10);
        var b = Text(V1, 25, 30);
        var timeline = Timeline();
        Select(timeline, b);

        timeline.CloseGapCommand.Execute(null);

        Assert.Equal(10L, Start(b));
        Assert.Equal("Gap closed", _status.Message);
        Assert.True(timeline.HasSelection);
    }

    [Fact]
    public void Close_gap_without_a_gap_shows_the_service_message()
    {
        Text(V1, 0, 10);
        var b = Text(V1, 10, 30);
        var timeline = Timeline();
        Select(timeline, b);

        timeline.CloseGapCommand.Execute(null);

        Assert.Equal("There is no gap before this clip.", _status.Message);
        Assert.Equal(10L, Start(b));
    }
}
