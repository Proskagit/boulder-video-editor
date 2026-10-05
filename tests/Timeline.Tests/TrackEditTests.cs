using System.Text;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Core.Playback;
using Xunit;

namespace AiVideoEditor.Timeline.Tests;

/// <summary>
/// Phase 12 Step 12.3 (D027 §3): deleting and reordering tracks. A delete takes the track's clips and dissolves with it
/// and Undo puts the same track back at its place; a move changes <see cref="Track.Order"/> only — no clip, timing or
/// dissolve — and the playback snapshot (the Preview's and the export's source) composites by it. A locked track is
/// neither deleted nor moved, nor moved past; the last track of the timeline stays. Rejected edits change nothing and
/// leave no Undo step.
/// </summary>
public class TrackEditTests
{
    private static void Ok(TimelineEditResult r) => Assert.True(r.Success, r.Message);
    private static MediaTime F(TimelineFixture f, long n) => MediaTime.FromFrame(n, f.Rate);
    private static string? Top(TimelineFixture f) => (f.UndoRedo.CurrentPosition as IUndoableCommand)?.Description;

    /// <summary>Everything a track edit may change: the lists (their order), each track's order, name and flags, and the
    /// clips and dissolves (<see cref="TimelineFixture.Snapshot"/>).</summary>
    private static string State(TimelineFixture f)
    {
        var sb = new StringBuilder();
        foreach (var t in f.Project.Timeline.VideoTracks.Concat(f.Project.Timeline.AudioTracks))
            sb.Append($"{t.Id}:{t.Type}:{t.Name}:order={t.Order}:m{t.IsMuted}h{t.IsHidden}l{t.IsLocked}\n");
        return sb.Append(f.Snapshot()).ToString();
    }

    private static TextClip Text(TimelineFixture f, Track track, long start, long end)
    {
        var clip = new TextClip { Text = "T", TimelineStart = F(f, start), Duration = F(f, end - start) };
        track.Clips.Add(clip);
        return clip;
    }

    private static Track AddVideoTrack(TimelineFixture f)
    {
        Ok(f.Service.AddTrack(TrackType.Video));
        return f.Project.Timeline.VideoTracks[^1];
    }

    private static IReadOnlyList<Guid> LayerOrder(TimelineFixture f) =>
        PlaybackSnapshotBuilder.Build(f.Project, 1).VideoLayers.Select(l => l.TrackId).ToList();   // topmost first

    // --- delete ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void An_empty_track_is_deleted_in_one_undo_step_and_comes_back_at_its_place()
    {
        var f = new TimelineFixture();
        var v2 = AddVideoTrack(f);
        var v3 = AddVideoTrack(f);
        var before = State(f);
        var changes = f.TimelineChangedCount;

        Ok(f.Service.DeleteTrack(v2.Id));

        Assert.Equal(new[] { f.V1, v3 }, f.Project.Timeline.VideoTracks);
        Assert.Equal("Delete Track", Top(f));
        Assert.True(f.Project.IsDirty);
        Assert.Equal(changes + 1, f.TimelineChangedCount);
        var after = State(f);

        f.UndoRedo.Undo();
        Assert.Equal(before, State(f));
        Assert.Same(v2, f.Project.Timeline.VideoTracks[1]);              // the same object, in the middle again
        f.UndoRedo.Redo();
        Assert.Equal(after, State(f));
    }

    [Fact]
    public void A_track_with_clips_and_a_dissolve_is_deleted_with_them_and_restored_exactly()
    {
        var f = new TimelineFixture();
        var v2 = AddVideoTrack(f);
        Ok(f.Service.AddClip(f.Video(20, FrameRate.Fps25).Id, v2.Id));
        Ok(f.Service.Split(F(f, 100)));
        var (a, b) = (v2.Clips[0], v2.Clips[1]);
        Ok(f.Service.SetClipProperties(a.Id, new ClipPropertyChange { Fade = new FadeProperties(F(f, 10), MediaTime.Zero) }));
        Ok(f.Service.AddTransition(a.Id, b.Id, F(f, 20)));
        var keep = Text(f, f.V1, 0, 50);
        var before = State(f);

        Ok(f.Service.DeleteTrack(v2.Id));

        Assert.DoesNotContain(v2, f.Project.Timeline.VideoTracks);
        Assert.Equal(new Clip[] { keep }, f.Project.Timeline.VideoTracks.SelectMany(t => t.Clips));
        Assert.DoesNotContain(LayerOrder(f), id => id == v2.Id);
        f.AssertValid();

        f.UndoRedo.Undo();
        Assert.Equal(before, State(f));
        Assert.Equal(new[] { a, b }, v2.Clips);                         // the same clips, the dissolve and the fade
        Assert.Single(v2.Transitions);
        Assert.Equal(F(f, 10), a.FadeIn);
        f.AssertValid();
    }

    [Fact]
    public void A_locked_track_is_not_deleted()
    {
        var f = new TimelineFixture();
        var v2 = AddVideoTrack(f);
        v2.IsLocked = true;
        Text(f, v2, 0, 25);
        var before = State(f);
        var top = f.UndoRedo.CurrentPosition;

        Assert.Equal("Track V2 is locked.", f.Service.GetDeleteTrackBlockReason(v2.Id));
        var result = f.Service.DeleteTrack(v2.Id);

        Assert.False(result.Success);
        Assert.Equal("Track V2 is locked.", result.Message);
        Assert.Equal(before, State(f));
        Assert.Same(top, f.UndoRedo.CurrentPosition);
    }

    [Fact]
    public void The_last_track_of_the_timeline_stays()
    {
        var f = new TimelineFixture();
        Ok(f.Service.DeleteTrack(f.A1.Id));                              // V1 is still there
        Assert.Empty(f.Project.Timeline.AudioTracks);
        var before = State(f);
        var top = f.UndoRedo.CurrentPosition;

        Assert.Equal("The timeline needs at least one track.", f.Service.GetDeleteTrackBlockReason(f.V1.Id));
        var result = f.Service.DeleteTrack(f.V1.Id);

        Assert.False(result.Success);
        Assert.Equal("The timeline needs at least one track.", result.Message);
        Assert.Equal(before, State(f));
        Assert.Same(top, f.UndoRedo.CurrentPosition);
    }

    [Fact]
    public void Without_an_audio_track_audio_is_refused_with_a_message_and_a_new_one_can_be_added()
    {
        var f = new TimelineFixture();
        var music = f.Audio(10);
        Ok(f.Service.DeleteTrack(f.A1.Id));

        var refused = f.Service.AddClip(music.Id);
        Assert.False(refused.Success);
        Assert.Equal("The timeline has no audio track.", refused.Message);

        Ok(f.Service.AddTrack(TrackType.Audio));
        Ok(f.Service.AddClip(music.Id));
    }

    [Fact]
    public void A_track_that_no_longer_exists_is_reported()
    {
        var f = new TimelineFixture();
        Assert.Equal("That track no longer exists.", f.Service.DeleteTrack(Guid.NewGuid()).Message);
        Assert.Equal("That track no longer exists.", f.Service.MoveTrack(Guid.NewGuid(), 1).Message);
    }

    // --- move -----------------------------------------------------------------------------------------------------------

    [Fact]
    public void Moving_a_track_swaps_only_the_orders_and_the_snapshot_composites_by_them()
    {
        var f = new TimelineFixture();
        var v2 = AddVideoTrack(f);
        Text(f, f.V1, 0, 50);
        Text(f, v2, 10, 60);
        var clipsBefore = f.Snapshot();
        var before = State(f);
        Assert.Equal(new[] { v2.Id, f.V1.Id }, LayerOrder(f));          // V2 on top

        Ok(f.Service.MoveTrack(f.V1.Id, 1));

        Assert.Equal((1, 0), (f.V1.Order, v2.Order));
        Assert.Equal(new[] { f.V1, v2 }, f.Project.Timeline.VideoTracks); // the list itself is untouched
        Assert.Equal(clipsBefore, f.Snapshot());                        // no clip, timing or dissolve changed
        Assert.Equal(new[] { f.V1.Id, v2.Id }, LayerOrder(f));          // V1 on top now
        Assert.Equal("Move Track", Top(f));
        Assert.True(f.Project.IsDirty);
        var after = State(f);

        f.UndoRedo.Undo();
        Assert.Equal(before, State(f));
        Assert.Equal(new[] { v2.Id, f.V1.Id }, LayerOrder(f));
        f.UndoRedo.Redo();
        Assert.Equal(after, State(f));
    }

    [Fact]
    public void Audio_tracks_move_among_audio_tracks_only()
    {
        var f = new TimelineFixture();
        Ok(f.Service.AddTrack(TrackType.Audio));
        var a2 = f.Project.Timeline.AudioTracks[^1];
        var videoBefore = f.V1.Order;

        Ok(f.Service.MoveTrack(a2.Id, -1));

        Assert.Equal((1, 0), (f.A1.Order, a2.Order));
        Assert.Equal(videoBefore, f.V1.Order);
    }

    [Fact]
    public void A_track_at_the_end_of_its_kind_does_not_move()
    {
        var f = new TimelineFixture();
        var v2 = AddVideoTrack(f);
        var before = State(f);
        var top = f.UndoRedo.CurrentPosition;

        var up = f.Service.MoveTrack(v2.Id, 1);        // already on top of the video tracks
        var down = f.Service.MoveTrack(f.V1.Id, -1);   // already at the bottom

        Assert.True(up.Success && up.NoChange);
        Assert.True(down.Success && down.NoChange);
        Assert.True(f.Service.MoveTrack(f.A1.Id, 1).NoChange);   // the only audio track: never past the video tracks
        Assert.Equal(before, State(f));
        Assert.Same(top, f.UndoRedo.CurrentPosition);
    }

    [Fact]
    public void A_locked_track_is_not_moved_and_nothing_moves_past_it()
    {
        var f = new TimelineFixture();
        var v2 = AddVideoTrack(f);
        v2.IsLocked = true;
        var before = State(f);
        var top = f.UndoRedo.CurrentPosition;

        var locked = f.Service.MoveTrack(v2.Id, -1);
        var past = f.Service.MoveTrack(f.V1.Id, 1);

        Assert.Equal("Track V2 is locked.", locked.Message);
        Assert.Equal("Track V2 is locked, so V1 can't move past it.", past.Message);
        Assert.False(locked.Success || past.Success);
        Assert.Equal(before, State(f));
        Assert.Same(top, f.UndoRedo.CurrentPosition);
    }

    [Fact]
    public void Equal_orders_are_numbered_anew_so_the_move_takes_effect_and_undo_restores_them()
    {
        var f = new TimelineFixture();
        var v2 = AddVideoTrack(f);
        var v3 = AddVideoTrack(f);
        (f.V1.Order, v2.Order, v3.Order) = (0, 0, 5);                   // a file from elsewhere: V1 and V2 share an order
        Assert.Equal(new[] { v3.Id, v2.Id, f.V1.Id }, LayerOrder(f));    // equal orders: the later one on top
        var before = State(f);

        Ok(f.Service.MoveTrack(f.V1.Id, 1));

        Assert.Equal(new[] { v3.Id, f.V1.Id, v2.Id }, LayerOrder(f));    // V1 above V2, V3 still on top
        Assert.Equal((1, 0, 2), (f.V1.Order, v2.Order, v3.Order));

        f.UndoRedo.Undo();
        Assert.Equal(before, State(f));
    }

    [Fact]
    public void A_dissolve_stays_with_its_track_when_the_track_moves()
    {
        var f = new TimelineFixture();
        Ok(f.Service.AddClip(f.Video(20, FrameRate.Fps25).Id));
        Ok(f.Service.Split(F(f, 100)));
        Ok(f.Service.AddTransition(f.V1.Clips[0].Id, f.V1.Clips[1].Id, F(f, 20)));
        var v2 = AddVideoTrack(f);
        var clipsBefore = f.Snapshot();

        Ok(f.Service.MoveTrack(f.V1.Id, 1));

        Assert.Equal(clipsBefore, f.Snapshot());
        Assert.Single(f.V1.Transitions);
        Assert.Equal(new[] { f.V1.Id, v2.Id }, LayerOrder(f));
        f.AssertValid();
    }

    // --- persistence ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Order_and_deleted_tracks_survive_save_and_reopen_in_format_3()
    {
        var root = Path.Combine(Path.GetTempPath(), "AiVideoEditorTests", Guid.NewGuid().ToString("N"));
        try
        {
            var f = new TimelineFixture();
            var v2 = AddVideoTrack(f);
            var v3 = AddVideoTrack(f);
            Text(f, v3, 0, 25);
            Ok(f.Service.DeleteTrack(v2.Id));
            Ok(f.Service.MoveTrack(v3.Id, -1));                             // V3 below V1
            var folder = Path.Combine(root, "Project");
            await f.Projects.SaveAsAsync(folder);
            Assert.Contains("\"formatVersion\": 3", File.ReadAllText(Path.Combine(folder, "project.json")));

            var v1 = f.V1;
            var expected = new[] { (v1.Id, v1.Order), (v3.Id, v3.Order) };

            var reopened = await f.Projects.OpenAsync(folder);

            var tracks = reopened.Timeline.VideoTracks;
            Assert.Equal(expected, tracks.Select(t => (t.Id, t.Order)));
            Assert.Equal(new[] { v1.Id, v3.Id }, PlaybackSnapshotBuilder.Build(reopened, 1).VideoLayers.Select(l => l.TrackId)); // V1 on top
            Assert.Single(tracks.Single(t => t.Id == v3.Id).Clips);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }
}
