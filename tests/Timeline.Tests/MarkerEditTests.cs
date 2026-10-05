using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using Xunit;

namespace AiVideoEditor.Timeline.Tests;

/// <summary>
/// Phase 12 Step 12.7 (D027 §6): markers on the timeline. Adding one (on the frame grid, at most one per frame) and
/// removing one are undoable project changes saved in v3; going to the next / previous marker is a query; markers are
/// snap targets. Rejected edits change nothing and leave no Undo step.
/// </summary>
public class MarkerEditTests
{
    private static void Ok(TimelineEditResult r) => Assert.True(r.Success, r.Message);
    private static MediaTime F(TimelineFixture f, long n) => MediaTime.FromFrame(n, f.Rate);
    private static string? Top(TimelineFixture f) => (f.UndoRedo.CurrentPosition as IUndoableCommand)?.Description;
    private static List<Marker> Markers(TimelineFixture f) => f.Project.Timeline.Markers;

    [Fact]
    public void A_marker_is_added_on_the_frame_grid_in_one_undo_step_with_the_default_look()
    {
        var f = new TimelineFixture();
        var changes = f.TimelineChangedCount;

        var result = f.Service.AddMarker(new MediaTime(F(f, 30).Ticks + 1));    // inside frame 30

        Ok(result);
        var marker = Assert.Single(Markers(f));
        Assert.Equal(result.MarkerId, marker.Id);
        Assert.Equal(F(f, 30), marker.Position);
        Assert.Equal((string.Empty, "#4FC3F7"), (marker.Label, marker.ColorHex));
        Assert.Equal("Add Marker", Top(f));
        Assert.True(f.Project.IsDirty);
        Assert.Equal(changes + 1, f.TimelineChangedCount);

        f.UndoRedo.Undo();
        Assert.Empty(Markers(f));
        Assert.False(f.Project.IsDirty);
        f.UndoRedo.Redo();
        Assert.Same(marker, Assert.Single(Markers(f)));                      // the very same marker
    }

    [Fact]
    public void Markers_stay_sorted_and_one_frame_takes_one_marker()
    {
        var f = new TimelineFixture();
        Ok(f.Service.AddMarker(F(f, 50)));
        Ok(f.Service.AddMarker(F(f, 10)));
        Ok(f.Service.AddMarker(F(f, 30)));
        var top = f.UndoRedo.CurrentPosition;

        var again = f.Service.AddMarker(new MediaTime(F(f, 30).Ticks + 2));

        Assert.False(again.Success);
        Assert.Equal("A marker is already there.", again.Message);
        Assert.Same(top, f.UndoRedo.CurrentPosition);
        Assert.Equal(new[] { F(f, 10), F(f, 30), F(f, 50) }, Markers(f).Select(m => m.Position));
    }

    [Fact]
    public void A_marker_before_zero_goes_to_zero()
    {
        var f = new TimelineFixture();
        Ok(f.Service.AddMarker(new MediaTime(-1000)));
        Assert.Equal(MediaTime.Zero, Assert.Single(Markers(f)).Position);
    }

    [Fact]
    public void The_marker_on_a_frame_is_removed_and_undo_puts_it_back_in_its_place()
    {
        var f = new TimelineFixture();
        Ok(f.Service.AddMarker(F(f, 10)));
        Ok(f.Service.AddMarker(F(f, 20)));
        Ok(f.Service.AddMarker(F(f, 30)));
        var middle = Markers(f)[1];

        var result = f.Service.RemoveMarkerAt(F(f, 20));

        Ok(result);
        Assert.Equal(middle.Id, result.MarkerId);
        Assert.Equal(new[] { F(f, 10), F(f, 30) }, Markers(f).Select(m => m.Position));
        Assert.Equal("Remove Marker", Top(f));

        f.UndoRedo.Undo();
        Assert.Same(middle, Markers(f)[1]);
    }

    [Fact]
    public void Removing_where_there_is_no_marker_is_refused()
    {
        var f = new TimelineFixture();
        Ok(f.Service.AddMarker(F(f, 10)));
        var top = f.UndoRedo.CurrentPosition;

        var result = f.Service.RemoveMarkerAt(F(f, 11));

        Assert.False(result.Success);
        Assert.Equal("There is no marker at the playhead.", result.Message);
        Assert.Single(Markers(f));
        Assert.Same(top, f.UndoRedo.CurrentPosition);
    }

    [Fact]
    public void Next_and_previous_are_the_nearest_markers_strictly_after_and_before()
    {
        var f = new TimelineFixture();
        Ok(f.Service.AddMarker(F(f, 10)));
        Ok(f.Service.AddMarker(F(f, 40)));
        Ok(f.Service.AddMarker(F(f, 25)));

        Assert.Equal(F(f, 10), f.Service.NextMarker(MediaTime.Zero));
        Assert.Equal(F(f, 25), f.Service.NextMarker(F(f, 10)));             // from a marker: the next one
        Assert.Equal(F(f, 25), f.Service.NextMarker(new MediaTime(F(f, 10).Ticks + 1)));   // same frame as 10
        Assert.Null(f.Service.NextMarker(F(f, 40)));
        Assert.Equal(F(f, 25), f.Service.PreviousMarker(F(f, 40)));
        Assert.Equal(F(f, 40), f.Service.PreviousMarker(F(f, 100)));
        Assert.Null(f.Service.PreviousMarker(F(f, 10)));
        Assert.Null(new TimelineFixture().Service.NextMarker(MediaTime.Zero));
    }

    [Fact]
    public void Markers_are_snap_targets()
    {
        var f = new TimelineFixture();
        Ok(f.Service.AddMarker(F(f, 40)));

        var snap = f.Service.Snap(new[] { F(f, 41) }, F(f, 2), Array.Empty<Guid>());

        Assert.True(snap.Snapped);
        Assert.Equal(F(f, 40), snap.Target);
    }

    [Fact]
    public void Markers_do_not_change_the_clips_or_the_playback_snapshot_timing()
    {
        var f = new TimelineFixture();
        Ok(f.Service.AddClip(f.Video(4, FrameRate.Fps25).Id));
        var before = f.Snapshot();

        Ok(f.Service.AddMarker(F(f, 20)));

        Assert.Equal(before, f.Snapshot());
        var snapshot = Core.Playback.PlaybackSnapshotBuilder.Build(f.Project, 1);
        Assert.Equal(F(f, 100), snapshot.Duration);                          // a marker never lengthens the timeline
    }

    [Fact]
    public async Task Markers_survive_save_and_reopen_in_format_3()
    {
        var root = Path.Combine(Path.GetTempPath(), "AiVideoEditorTests", Guid.NewGuid().ToString("N"));
        try
        {
            var f = new TimelineFixture();
            Ok(f.Service.AddMarker(F(f, 12)));
            Ok(f.Service.AddMarker(F(f, 48)));
            var expected = Markers(f).Select(m => (m.Id, m.Position, m.ColorHex)).ToList();
            var folder = Path.Combine(root, "Project");
            await f.Projects.SaveAsAsync(folder);
            Assert.Contains("\"formatVersion\": 3", File.ReadAllText(Path.Combine(folder, "project.json")));

            var reopened = await f.Projects.OpenAsync(folder);

            Assert.Equal(expected, reopened.Timeline.Markers.Select(m => (m.Id, m.Position, m.ColorHex)));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }
}
