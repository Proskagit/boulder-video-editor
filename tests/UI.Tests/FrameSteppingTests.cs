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
/// D057 §3.14 on the timeline: Right / Left step by the active clip's source frames (a 240 fps clip in a 30 fps project →
/// 1/240 s); the playhead stays there after an edit; every edit bound to the grid rounds to the nearest project frame;
/// Shift+arrows step one second as before.
/// </summary>
public sealed class FrameSteppingTests
{
    private static readonly FrameRate Project = FrameRate.Fps30;
    private static readonly FrameRate Fast = new(240, 1);
    private readonly UndoRedoService _undo = new();
    private readonly ProjectService _projects;
    private readonly TimelineEditService _edit;
    private readonly TimelineViewModel _timeline;

    public FrameSteppingTests()
    {
        _projects = new ProjectService(_undo, NullLogger<ProjectService>.Instance);
        _edit = new TimelineEditService(_projects, _undo, NullLogger<TimelineEditService>.Instance);
        _timeline = new TimelineViewModel(_projects, _edit, new StatusService(), NullLogger<TimelineViewModel>.Instance);
        Assert.True(_edit.AddClip(Video("thirty.mp4", Project).Id).Success);     // fixes 30 fps (D007)
        Assert.True(_edit.AddClip(Video("fast.mp4", Fast).Id).Success);         // from 10 s
        Assert.Equal(Project, _projects.Current.Settings.FrameRate);
    }

    private MediaAsset Video(string name, FrameRate rate)
    {
        var asset = new MediaAsset
        {
            FilePath = Path.Combine(Path.GetTempPath(), "aive-step", name), Kind = MediaKind.Video, AnalysisStatus = MediaAnalysisStatus.Completed,
            Metadata = new MediaMetadata { Duration = MediaTime.FromSeconds(10), FrameRate = rate, AvgFrameRate = rate, Width = 1920, Height = 1080 }
        };
        _projects.AddMediaAssets(new[] { asset });
        return asset;
    }

    [Fact]
    public void Right_on_the_fast_clip_steps_one_of_its_frames_and_left_comes_back()
    {
        _timeline.SetPlayhead(MediaTime.FromSeconds(11));
        _timeline.StepForwardCommand.Execute(null);
        Assert.Equal(MediaTime.FromSeconds(11) + MediaTime.FromFrame(1, Fast), _timeline.Playhead);
        _timeline.StepBackwardCommand.Execute(null);
        Assert.Equal(MediaTime.FromSeconds(11), _timeline.Playhead);
    }

    [Fact]
    public void On_the_thirty_fps_clip_a_step_is_one_thirtieth()
    {
        _timeline.SetPlayhead(MediaTime.FromSeconds(1));
        _timeline.StepForwardCommand.Execute(null);
        Assert.Equal(MediaTime.FromSeconds(1) + MediaTime.FromFrame(1, Project), _timeline.Playhead);
    }

    [Fact]
    public void An_edit_rounds_to_the_project_frame_and_leaves_the_playhead_where_it_is()
    {
        _timeline.SetPlayhead(MediaTime.FromSeconds(11));
        for (var i = 0; i < 5; i++) _timeline.StepForwardCommand.Execute(null);   // 5/240 s: between 30 fps frames
        var exact = MediaTime.FromSeconds(11) + MediaTime.FromFrame(5, Fast);
        Assert.Equal(exact, _timeline.Playhead);
        Assert.Equal(MediaTime.FromSeconds(11) + MediaTime.FromFrame(1, Project), _timeline.EditPlayhead);   // nearest: 1/30

        _timeline.SplitAtPlayheadCommand.Execute(null);
        var parts = _projects.Current.Timeline.VideoTracks[0].Clips.OrderBy(c => c.TimelineStart).ToList();
        Assert.Contains(parts, c => c.TimelineStart == MediaTime.FromSeconds(11) + MediaTime.FromFrame(1, Project));
        Assert.All(parts, c => Assert.Equal(c.TimelineStart, MediaTime.FromFrame(c.TimelineStart.ToNearestFrame(Project), Project)));
        Assert.Equal(exact, _timeline.Playhead);                                  // not snapped by the edit

        _timeline.AddMarkerCommand.Execute(null);
        Assert.Equal(MediaTime.FromSeconds(11) + MediaTime.FromFrame(1, Project), Assert.Single(_projects.Current.Timeline.Markers).Position);
    }

    [Fact]
    public void Shift_arrows_still_step_one_second_on_the_project_grid()
    {
        _timeline.SetPlayhead(MediaTime.FromSeconds(11));
        _timeline.StepForwardCommand.Execute(null);
        _timeline.StepForwardSecondCommand.Execute(null);
        Assert.Equal(MediaTime.FromSeconds(12), _timeline.Playhead);
    }
}
