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
/// Phase 12 Step 12.3 (D027 §3): the track header's ▲ / ▼ / ✕ in the timeline view model. Up / down follow what the
/// timeline shows (video: the top layer first; audio: by order) and are offered only where a track of the same kind is
/// next to it; a track with clips is deleted only after the user confirms, an empty one at once; a deletion the edit
/// service would refuse is not asked about; everything is disabled while an export runs.
/// </summary>
public sealed class TimelineTrackUiTests
{
    private static readonly FrameRate Rate = FrameRate.Fps25;

    private readonly UndoRedoService _undo = new();
    private readonly ProjectService _projects;
    private readonly TimelineEditService _edit;
    private readonly StatusService _status = new();
    private readonly EditingLock _lock = new();

    public TimelineTrackUiTests()
    {
        _projects = new ProjectService(_undo, NullLogger<ProjectService>.Instance);
        _edit = new TimelineEditService(_projects, _undo, NullLogger<TimelineEditService>.Instance);
    }

    private TimelineViewModel Timeline(ScriptedDialogs? dialogs = null) =>
        new(_projects, _edit, _status, NullLogger<TimelineViewModel>.Instance, _lock, dialogs: dialogs ?? new ScriptedDialogs());

    private Sequence Sequence => _projects.Current.Timeline;
    private Track V1 => Sequence.VideoTracks[0];
    private Track A1 => Sequence.AudioTracks[0];
    private static MediaTime F(long n) => MediaTime.FromFrame(n, Rate);

    private static TimelineTrackViewModel Row(TimelineViewModel timeline, Track track) => timeline.Tracks.Single(t => t.Track == track);

    private Track AddTrack(TrackType type)
    {
        Assert.True(_edit.AddTrack(type).Success);
        return type == TrackType.Video ? Sequence.VideoTracks[^1] : Sequence.AudioTracks[^1];
    }

    [Fact]
    public void Arrows_are_offered_only_towards_a_track_of_the_same_kind()
    {
        var v2 = AddTrack(TrackType.Video);
        var a2 = AddTrack(TrackType.Audio);
        var timeline = Timeline();

        Assert.Equal(new[] { v2, V1, A1, a2 }, timeline.Tracks.Select(t => t.Track));   // V2 on top, audio by order
        Assert.Equal(new[] { (false, true), (true, false), (false, true), (true, false) },
            timeline.Tracks.Select(t => (t.HasTrackAbove, t.HasTrackBelow)));
        Assert.False(timeline.MoveTrackUpCommand.CanExecute(Row(timeline, v2)));
        Assert.True(timeline.MoveTrackDownCommand.CanExecute(Row(timeline, v2)));
        Assert.False(timeline.MoveTrackDownCommand.CanExecute(Row(timeline, V1)));      // never into the audio tracks
        Assert.False(timeline.MoveTrackUpCommand.CanExecute(Row(timeline, A1)));
    }

    [Fact]
    public void Up_and_down_move_the_rows_as_shown_for_video_and_audio()
    {
        var v2 = AddTrack(TrackType.Video);
        var a2 = AddTrack(TrackType.Audio);
        var timeline = Timeline();

        timeline.MoveTrackUpCommand.Execute(Row(timeline, V1));      // the bottom video track to the top: a higher order
        Assert.Equal(new[] { V1, v2, A1, a2 }, timeline.Tracks.Select(t => t.Track));
        Assert.True(V1.Order > v2.Order);
        Assert.Equal("Track V1 moved up", _status.Message);

        timeline.MoveTrackUpCommand.Execute(Row(timeline, a2));      // the lower audio row up: a lower order
        Assert.Equal(new[] { V1, v2, a2, A1 }, timeline.Tracks.Select(t => t.Track));
        Assert.True(a2.Order < A1.Order);

        timeline.MoveTrackDownCommand.Execute(Row(timeline, a2));
        Assert.Equal(new[] { V1, v2, A1, a2 }, timeline.Tracks.Select(t => t.Track));
        Assert.Equal("Track A2 moved down", _status.Message);

        _undo.Undo();
        _undo.Undo();
        _undo.Undo();
        Assert.Equal(new[] { v2, V1, A1, a2 }, timeline.Tracks.Select(t => t.Track));   // the view follows Undo
    }

    [Fact]
    public void A_refused_move_shows_the_service_message()
    {
        var v2 = AddTrack(TrackType.Video);
        v2.IsLocked = true;
        var timeline = Timeline();

        timeline.MoveTrackUpCommand.Execute(Row(timeline, V1));

        Assert.Equal("Track V2 is locked, so V1 can't move past it.", _status.Message);
        Assert.Equal(new[] { v2, V1 }, timeline.Tracks.Where(t => t.Type == TrackType.Video).Select(t => t.Track));
    }

    [Fact]
    public async Task An_empty_track_is_deleted_without_asking()
    {
        var v2 = AddTrack(TrackType.Video);
        var dialogs = new ScriptedDialogs();
        var timeline = Timeline(dialogs);

        await timeline.DeleteTrackCommand.ExecuteAsync(Row(timeline, v2));

        Assert.Empty(dialogs.Asked);
        Assert.DoesNotContain(v2, Sequence.VideoTracks);
        Assert.DoesNotContain(timeline.Tracks, t => t.Track == v2);
        Assert.Equal("Track V2 deleted", _status.Message);
    }

    [Fact]
    public async Task A_track_with_clips_is_deleted_only_after_the_user_confirms()
    {
        var v2 = AddTrack(TrackType.Video);
        v2.Clips.Add(new TextClip { Text = "A", TimelineStart = F(0), Duration = F(25) });
        v2.Clips.Add(new TextClip { Text = "B", TimelineStart = F(25), Duration = F(25) });
        var dialogs = new ScriptedDialogs(1, null, 0);                // Cancel, closed, then Delete Track
        var timeline = Timeline(dialogs);

        await timeline.DeleteTrackCommand.ExecuteAsync(Row(timeline, v2));
        await timeline.DeleteTrackCommand.ExecuteAsync(Row(timeline, v2));
        Assert.Contains(v2, Sequence.VideoTracks);                       // neither Cancel nor closing deletes
        Assert.Equal(2, v2.Clips.Count);

        await timeline.DeleteTrackCommand.ExecuteAsync(Row(timeline, v2));

        Assert.Equal(3, dialogs.Asked.Count);
        Assert.Equal("Delete Track", dialogs.Asked[0].Title);
        Assert.Contains("Track V2 has 2 clips", dialogs.Asked[0].Message);
        Assert.Equal(new[] { "Delete Track", "Cancel" }, dialogs.Asked[0].Buttons);
        Assert.DoesNotContain(v2, Sequence.VideoTracks);

        _undo.Undo();                                                    // one step brings the track and its clips back
        Assert.Contains(timeline.Tracks, t => t.Track == v2 && t.Clips.Count == 2);
    }

    [Fact]
    public async Task A_selected_clip_on_a_deleted_track_leaves_the_selection()
    {
        var v2 = AddTrack(TrackType.Video);
        var clip = new TextClip { Text = "A", TimelineStart = F(0), Duration = F(25) };
        v2.Clips.Add(clip);
        var timeline = Timeline(new ScriptedDialogs(0));
        timeline.OnClipPressed(timeline.Tracks.SelectMany(t => t.Clips).Single(c => c.Id == clip.Id), toggle: false);
        Assert.True(timeline.HasSelection);
        var raised = false;
        TimelineClipSelection? selected = null;
        timeline.SelectionChanged += (_, s) => (raised, selected) = (true, s);

        await timeline.DeleteTrackCommand.ExecuteAsync(Row(timeline, v2));

        Assert.False(timeline.HasSelection);
        Assert.True(raised);                                             // the Inspector is told: nothing selected
        Assert.Null(selected);
    }

    [Fact]
    public async Task A_deletion_the_service_refuses_is_not_asked_about()
    {
        var v2 = AddTrack(TrackType.Video);
        v2.IsLocked = true;
        v2.Clips.Add(new TextClip { Text = "A", TimelineStart = F(0), Duration = F(25) });
        var dialogs = new ScriptedDialogs(0);
        var timeline = Timeline(dialogs);

        await timeline.DeleteTrackCommand.ExecuteAsync(Row(timeline, v2));

        Assert.Empty(dialogs.Asked);
        Assert.Equal("Track V2 is locked.", _status.Message);
        Assert.Contains(v2, Sequence.VideoTracks);
    }

    [Fact]
    public async Task The_last_track_is_kept_with_a_message()
    {
        var timeline = Timeline();
        await timeline.DeleteTrackCommand.ExecuteAsync(Row(timeline, A1));

        await timeline.DeleteTrackCommand.ExecuteAsync(Row(timeline, V1));

        Assert.Equal("The timeline needs at least one track.", _status.Message);
        Assert.Single(timeline.Tracks);
    }

    [Fact]
    public async Task An_export_started_while_the_question_is_open_cancels_the_deletion()
    {
        var v2 = AddTrack(TrackType.Video);
        v2.Clips.Add(new TextClip { Text = "A", TimelineStart = F(0), Duration = F(25) });
        IDisposable? held = null;
        var dialogs = new ScriptedDialogs(0) { OnAsk = () => held = _lock.Acquire() };
        var timeline = Timeline(dialogs);

        await timeline.DeleteTrackCommand.ExecuteAsync(Row(timeline, v2));

        Assert.Contains(v2, Sequence.VideoTracks);
        held!.Dispose();
    }

    [Fact]
    public void The_track_commands_are_disabled_during_an_export()
    {
        var v2 = AddTrack(TrackType.Video);
        var timeline = Timeline();

        using (_lock.Acquire())
        {
            Assert.False(timeline.MoveTrackDownCommand.CanExecute(Row(timeline, v2)));
            Assert.False(timeline.DeleteTrackCommand.CanExecute(Row(timeline, v2)));
        }
        Assert.True(timeline.MoveTrackDownCommand.CanExecute(Row(timeline, v2)));
        Assert.True(timeline.DeleteTrackCommand.CanExecute(Row(timeline, v2)));
    }
}
