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
/// Phase 15 Step 15.3 (D030 §4): the track header's mute / hide / lock toggles in the timeline view model. Video rows
/// offer all three, audio rows mute and lock only (no hide); each click is one undoable edit and the row follows the
/// model, also after Undo / Redo; mute and hide stay available on a locked track; everything is disabled while an
/// export runs.
/// </summary>
public sealed class TimelineTrackStateUiTests
{
    private readonly UndoRedoService _undo = new();
    private readonly ProjectService _projects;
    private readonly TimelineEditService _edit;
    private readonly StatusService _status = new();
    private readonly EditingLock _lock = new();

    public TimelineTrackStateUiTests()
    {
        _projects = new ProjectService(_undo, NullLogger<ProjectService>.Instance);
        _edit = new TimelineEditService(_projects, _undo, NullLogger<TimelineEditService>.Instance);
    }

    private TimelineViewModel Timeline() =>
        new(_projects, _edit, _status, NullLogger<TimelineViewModel>.Instance, _lock, dialogs: new ScriptedDialogs());

    private Sequence Sequence => _projects.Current.Timeline;
    private Track V1 => Sequence.VideoTracks[0];
    private Track A1 => Sequence.AudioTracks[0];

    private static TimelineTrackViewModel Row(TimelineViewModel timeline, Track track) => timeline.Tracks.Single(t => t.Track == track);

    [Fact]
    public void Video_rows_offer_mute_hide_and_lock_audio_rows_mute_and_lock_only()
    {
        var timeline = Timeline();
        var video = Row(timeline, V1);
        var audio = Row(timeline, A1);

        Assert.True(video.CanHide);
        Assert.False(audio.CanHide);
        Assert.True(timeline.ToggleTrackMuteCommand.CanExecute(video));
        Assert.True(timeline.ToggleTrackHiddenCommand.CanExecute(video));
        Assert.True(timeline.ToggleTrackLockCommand.CanExecute(video));
        Assert.True(timeline.ToggleTrackMuteCommand.CanExecute(audio));
        Assert.False(timeline.ToggleTrackHiddenCommand.CanExecute(audio));
        Assert.True(timeline.ToggleTrackLockCommand.CanExecute(audio));

        timeline.ToggleTrackHiddenCommand.Execute(audio);                       // even if invoked: nothing happens
        Assert.False(A1.IsHidden);
        Assert.False(_undo.CanUndo);
    }

    [Fact]
    public void Each_toggle_is_one_undo_step_and_the_row_follows_the_model_through_undo_and_redo()
    {
        var timeline = Timeline();
        var video = Row(timeline, V1);
        var audio = Row(timeline, A1);

        timeline.ToggleTrackMuteCommand.Execute(video);
        Assert.True(V1.IsMuted);
        Assert.True(video.IsMuted);
        Assert.Equal("Track V1 muted", _status.Message);
        Assert.True(_projects.Current.IsDirty);

        timeline.ToggleTrackHiddenCommand.Execute(video);
        Assert.True(video.IsHidden);
        Assert.Equal("Track V1 hidden", _status.Message);

        timeline.ToggleTrackLockCommand.Execute(video);
        Assert.True(video.IsLocked);
        Assert.Equal("Track V1 locked", _status.Message);

        timeline.ToggleTrackMuteCommand.Execute(audio);
        timeline.ToggleTrackLockCommand.Execute(audio);
        Assert.True(audio is { IsMuted: true, IsLocked: true });

        for (var i = 0; i < 5; i++) _undo.Undo();
        Assert.True(video is { IsMuted: false, IsHidden: false, IsLocked: false });
        Assert.True(audio is { IsMuted: false, IsLocked: false });
        Assert.False(_projects.Current.IsDirty);
        Assert.False(_undo.CanUndo);

        for (var i = 0; i < 5; i++) _undo.Redo();
        Assert.True(video is { IsMuted: true, IsHidden: true, IsLocked: true });
        Assert.True(audio is { IsMuted: true, IsLocked: true });

        timeline.ToggleTrackLockCommand.Execute(video);                        // a second click toggles back
        Assert.False(V1.IsLocked);
        Assert.False(video.IsLocked);
        Assert.Equal("Track V1 unlocked", _status.Message);
    }

    [Fact]
    public void Mute_and_hide_stay_available_on_a_locked_track()
    {
        var timeline = Timeline();
        var video = Row(timeline, V1);
        timeline.ToggleTrackLockCommand.Execute(video);

        Assert.True(timeline.ToggleTrackMuteCommand.CanExecute(video));
        Assert.True(timeline.ToggleTrackHiddenCommand.CanExecute(video));
        timeline.ToggleTrackMuteCommand.Execute(video);
        timeline.ToggleTrackHiddenCommand.Execute(video);

        Assert.True(V1 is { IsLocked: true, IsMuted: true, IsHidden: true });
        Assert.Equal("Track V1 hidden", _status.Message);
    }

    [Fact]
    public void A_locked_track_refuses_a_track_edit_from_the_header()
    {
        Assert.True(_edit.AddTrack(TrackType.Video).Success);
        var timeline = Timeline();
        var video = Row(timeline, V1);
        timeline.ToggleTrackLockCommand.Execute(video);

        timeline.DeleteTrackCommand.Execute(video);

        Assert.Contains(V1, Sequence.VideoTracks);
        Assert.Equal("Track V1 is locked.", _status.Message);
    }

    [Fact]
    public void The_toggles_are_disabled_while_an_export_runs()
    {
        var timeline = Timeline();
        var video = Row(timeline, V1);
        var audio = Row(timeline, A1);
        var raised = 0;
        timeline.ToggleTrackMuteCommand.CanExecuteChanged += (_, _) => raised++;

        using (_lock.Acquire())
        {
            Assert.True(raised > 0);
            Assert.False(timeline.ToggleTrackMuteCommand.CanExecute(video));
            Assert.False(timeline.ToggleTrackHiddenCommand.CanExecute(video));
            Assert.False(timeline.ToggleTrackLockCommand.CanExecute(video));
            Assert.False(timeline.ToggleTrackMuteCommand.CanExecute(audio));
            Assert.False(timeline.ToggleTrackLockCommand.CanExecute(audio));
        }

        Assert.True(timeline.ToggleTrackMuteCommand.CanExecute(video));
        Assert.True(timeline.ToggleTrackLockCommand.CanExecute(audio));
    }

    [Fact]
    public async Task A_reopened_project_shows_its_saved_flags_in_the_headers()
    {
        var root = Path.Combine(Path.GetTempPath(), "AiVideoEditorTests", Guid.NewGuid().ToString("N"));
        try
        {
            var timeline = Timeline();
            timeline.ToggleTrackHiddenCommand.Execute(Row(timeline, V1));
            timeline.ToggleTrackLockCommand.Execute(Row(timeline, V1));
            timeline.ToggleTrackMuteCommand.Execute(Row(timeline, A1));
            var folder = Path.Combine(root, "Project");
            await _projects.SaveAsAsync(folder);
            _projects.CreateNew("Other");
            Assert.True(Row(timeline, V1) is { IsHidden: false, IsLocked: false });

            await _projects.OpenAsync(folder);

            Assert.True(Row(timeline, V1) is { IsHidden: true, IsLocked: true, IsMuted: false });
            Assert.True(Row(timeline, A1) is { IsMuted: true, IsLocked: false });
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }
}
