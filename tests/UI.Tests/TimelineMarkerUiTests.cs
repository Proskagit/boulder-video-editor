using AiVideoEditor.Core.Common;
using AiVideoEditor.Project;
using AiVideoEditor.Timeline;
using AiVideoEditor.UI.Common;
using AiVideoEditor.UI.Services;
using AiVideoEditor.UI.ViewModels.Panels;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiVideoEditor.UI.Tests;

/// <summary>
/// Phase 12 Step 12.7 (D027 §6): the marker buttons of the timeline panel. Add / Remove work at the playhead (undoable,
/// disabled during an export); the markers are drawn on the ruler at the current zoom and follow Undo; previous / next
/// move the playhead (a seek, like any user move) or say there is none.
/// </summary>
public sealed class TimelineMarkerUiTests
{
    private static readonly FrameRate Rate = FrameRate.Default;

    private readonly UndoRedoService _undo = new();
    private readonly ProjectService _projects;
    private readonly TimelineEditService _edit;
    private readonly StatusService _status = new();
    private readonly EditingLock _lock = new();

    public TimelineMarkerUiTests()
    {
        _projects = new ProjectService(_undo, NullLogger<ProjectService>.Instance);
        _edit = new TimelineEditService(_projects, _undo, NullLogger<TimelineEditService>.Instance);
    }

    private TimelineViewModel Timeline() => new(_projects, _edit, _status, NullLogger<TimelineViewModel>.Instance, _lock);
    private static MediaTime F(long n) => MediaTime.FromFrame(n, Rate);

    [Fact]
    public void Add_marker_draws_it_on_the_ruler_at_the_zoom_and_undo_takes_it_away()
    {
        var timeline = Timeline();
        timeline.SetPlayhead(F(60));                                      // 2 s

        timeline.AddMarkerCommand.Execute(null);

        var marker = Assert.Single(timeline.Markers);
        Assert.Equal(TimelineCoordinateMapper.TimeToX(F(60), timeline.PixelsPerSecond), marker.Left);
        Assert.Equal("#4FC3F7", marker.Color);
        Assert.StartsWith("Marker added at", _status.Message);

        timeline.ZoomInCommand.Execute(null);                              // laid out again at the new zoom
        Assert.Equal(TimelineCoordinateMapper.TimeToX(F(60), timeline.PixelsPerSecond), Assert.Single(timeline.Markers).Left);

        _undo.Undo();
        Assert.Empty(timeline.Markers);
    }

    [Fact]
    public void A_second_marker_on_the_same_frame_is_refused_with_a_message()
    {
        var timeline = Timeline();
        timeline.AddMarkerCommand.Execute(null);

        timeline.AddMarkerCommand.Execute(null);

        Assert.Equal("A marker is already there.", _status.Message);
        Assert.Single(timeline.Markers);
    }

    [Fact]
    public void Remove_marker_takes_the_one_at_the_playhead_or_says_there_is_none()
    {
        var timeline = Timeline();
        timeline.SetPlayhead(F(30));
        timeline.AddMarkerCommand.Execute(null);

        timeline.SetPlayhead(F(31));
        timeline.RemoveMarkerCommand.Execute(null);
        Assert.Equal("There is no marker at the playhead.", _status.Message);
        Assert.Single(timeline.Markers);

        timeline.SetPlayhead(F(30));
        timeline.RemoveMarkerCommand.Execute(null);
        Assert.Equal("Marker removed", _status.Message);
        Assert.Empty(timeline.Markers);
    }

    [Fact]
    public void Previous_and_next_move_the_playhead_with_a_seek_or_say_there_is_none()
    {
        var timeline = Timeline();
        foreach (var frame in new long[] { 30, 90 })
        {
            timeline.SetPlayhead(F(frame));
            timeline.AddMarkerCommand.Execute(null);
        }
        timeline.SetPlayhead(F(60));
        var seeks = new List<MediaTime>();
        timeline.SeekRequested += (_, t) => seeks.Add(t);

        timeline.NextMarkerCommand.Execute(null);
        Assert.Equal(F(90), timeline.Playhead);
        timeline.NextMarkerCommand.Execute(null);
        Assert.Equal("There is no marker after the playhead.", _status.Message);
        Assert.Equal(F(90), timeline.Playhead);

        timeline.PreviousMarkerCommand.Execute(null);
        Assert.Equal(F(30), timeline.Playhead);
        timeline.PreviousMarkerCommand.Execute(null);
        Assert.Equal("There is no marker before the playhead.", _status.Message);
        Assert.Equal(new[] { F(90), F(30) }, seeks);
    }

    [Fact]
    public void Adding_and_removing_are_disabled_during_an_export_while_going_to_a_marker_is_not()
    {
        var timeline = Timeline();

        using (_lock.Acquire())
        {
            Assert.False(timeline.AddMarkerCommand.CanExecute(null));
            Assert.False(timeline.RemoveMarkerCommand.CanExecute(null));
            Assert.True(timeline.NextMarkerCommand.CanExecute(null));
            Assert.True(timeline.PreviousMarkerCommand.CanExecute(null));
        }
        Assert.True(timeline.AddMarkerCommand.CanExecute(null));
    }

    [Fact]
    public void Markers_of_an_opened_project_are_shown_and_another_project_has_its_own()
    {
        _projects.Current.Timeline.Markers.Add(new Core.Entities.Marker { Position = F(15) });
        var timeline = Timeline();
        Assert.Single(timeline.Markers);

        _projects.CreateNew("Other");

        Assert.Empty(timeline.Markers);
    }
}
