using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Core.Telemetry;
using AiVideoEditor.Project;
using AiVideoEditor.Timeline;
using AiVideoEditor.UI.Services;
using AiVideoEditor.UI.ViewModels.Panels;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiVideoEditor.UI.Tests;

/// <summary>D057 §3.2: several files in the Media Browser — Ctrl+A selects all of them (not in the hold markup mode), «Добавить
/// на таймлайн» adds them in the order of recording, one Undo step; the list's order stays.</summary>
public sealed class MediaBrowserMultiAddTests
{
    private static readonly DateTimeOffset W0 = new(2026, 9, 27, 7, 0, 0, TimeSpan.Zero);
    private readonly UndoRedoService _undo = new();
    private readonly ProjectService _projects;
    private readonly TimelineEditService _edit;
    private readonly MediaBrowserViewModel _browser;
    private readonly TimelineViewModel _timeline;

    public MediaBrowserMultiAddTests()
    {
        _projects = new ProjectService(_undo, NullLogger<ProjectService>.Instance);
        _edit = new TimelineEditService(_projects, _undo, NullLogger<TimelineEditService>.Instance);
        _timeline = new TimelineViewModel(_projects, _edit, new StatusService(), NullLogger<TimelineViewModel>.Instance);
        _browser = new MediaBrowserViewModel(_projects, null!, NullLogger<MediaBrowserViewModel>.Instance, edit: _edit);
        _browser.AddManyToTimelineRequested += (_, assets) =>
            _timeline.AddMediaMany(MediaRecordingOrder.Order(assets, _projects.Current.Training?.Workout?.UtcOffset));
        _browser.AddToTimelineRequested += (_, asset) => _timeline.AddMedia(asset);
    }

    private MediaAsset Video(string name, DateTimeOffset tagEnd)
    {
        var asset = new MediaAsset
        {
            FilePath = Path.Combine(Path.GetTempPath(), "aive-multi", name), Kind = MediaKind.Video, AnalysisStatus = MediaAnalysisStatus.Completed,
            Metadata = new MediaMetadata
            {
                Duration = MediaTime.FromSeconds(10), FrameRate = FrameRate.Fps30, AvgFrameRate = FrameRate.Fps30, Width = 1920, Height = 1080,
                RecordingTimeProbe = RecordingTimeProbe.Found(tagEnd.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.ffffffZ"), RecordingTimeTagOrigin.Format)
            }
        };
        _projects.AddMediaAssets(new[] { asset });
        return asset;
    }

    [Fact]
    public void Ctrl_A_selects_every_file_and_add_puts_them_in_the_order_of_recording_in_one_undo_step()
    {
        var late = Video("late.mp4", W0.AddMinutes(30));
        var early = Video("early.mp4", W0.AddMinutes(5));
        var middle = Video("middle.mp4", W0.AddMinutes(15));

        _browser.SelectAllCommand.Execute(null);
        Assert.Equal(3, _browser.SelectedItems.Count);
        Assert.True(_browser.AddToTimelineCommand.CanExecute(null));
        _browser.AddToTimelineCommand.Execute(null);

        var clips = _projects.Current.Timeline.VideoTracks[0].Clips.OfType<VideoClip>().OrderBy(c => c.TimelineStart).ToList();
        Assert.Equal(new[] { early.Id, middle.Id, late.Id }, clips.Select(c => c.MediaAssetId));
        Assert.Equal(new[] { late.Id, early.Id, middle.Id }, _browser.Items.Select(i => i.Asset.Id));   // the list unchanged

        _undo.Undo();
        Assert.Empty(_projects.Current.Timeline.VideoTracks[0].Clips);
    }

    [Fact]
    public void One_selected_file_is_added_as_before()
    {
        var only = Video("only.mp4", W0);
        Video("other.mp4", W0.AddMinutes(1));
        _browser.SelectedItem = _browser.Items.First(i => i.Asset.Id == only.Id);
        _browser.AddToTimelineCommand.Execute(null);
        Assert.Equal(only.Id, Assert.Single(_projects.Current.Timeline.VideoTracks[0].Clips.OfType<VideoClip>()).MediaAssetId);
    }

    [Fact]
    public void The_selection_is_kept_by_identity_when_the_list_reloads()
    {
        Video("a.mp4", W0);
        Video("b.mp4", W0.AddMinutes(1));
        _browser.SelectAllCommand.Execute(null);
        Video("c.mp4", W0.AddMinutes(2));                                // an import reloads the list
        Assert.Equal(2, _browser.SelectedItems.Count);
    }

    [Fact]
    public void In_the_hold_markup_mode_ctrl_a_is_not_the_media_browsers()
    {
        Assert.True(_browser.AllowsSelectAll);
        _browser.CanSelectAllNow = () => false;
        Assert.False(_browser.AllowsSelectAll);
    }

    [Fact]
    public void Dragging_a_file_of_the_group_drags_the_group_and_the_drop_adds_them_in_recording_order_in_one_undo_step()
    {
        var late = Video("late.mp4", W0.AddMinutes(30));
        var early = Video("early.mp4", W0.AddMinutes(5));
        var alone = Video("alone.mp4", W0.AddMinutes(60));
        _browser.SelectedItems.Add(_browser.Items.First(i => i.Asset.Id == late.Id));
        _browser.SelectedItems.Add(_browser.Items.First(i => i.Asset.Id == early.Id));

        var group = _browser.DraggedGroup(_browser.Items.First(i => i.Asset.Id == early.Id));
        Assert.Equal(new[] { late.Id, early.Id }, group.Select(a => a.Id));                  // the list's order
        Assert.Equal(new[] { alone.Id }, _browser.DraggedGroup(_browser.Items.First(i => i.Asset.Id == alone.Id)).Select(a => a.Id));

        var lane = _timeline.Tracks.First(t => t.Track.Type == Core.Entities.TrackType.Video);
        _timeline.ShowDropPreview(group.Select(a => a.Id).ToList(), lane, 0);
        Assert.True(lane.HasDropGhost);                                                     // the ghost of both files
        Assert.False(lane.DropGhostMovesOthers);
        Assert.True(lane.DropGhostWidth > 0);

        _timeline.DropMediaMany(group.Select(a => a.Id).ToList(), lane, 0);
        Assert.False(lane.HasDropGhost);
        var clips = _projects.Current.Timeline.VideoTracks[0].Clips.OfType<VideoClip>().OrderBy(c => c.TimelineStart).ToList();
        Assert.Equal(new[] { early.Id, late.Id }, clips.Select(c => c.MediaAssetId));       // by recording time
        _undo.Undo();
        Assert.Empty(_projects.Current.Timeline.VideoTracks[0].Clips);
    }
}
