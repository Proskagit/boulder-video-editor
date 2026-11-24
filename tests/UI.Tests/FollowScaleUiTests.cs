using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Project;
using AiVideoEditor.Timeline;
using AiVideoEditor.UI.Common;
using AiVideoEditor.UI.Services;
using AiVideoEditor.UI.ViewModels.Panels;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiVideoEditor.UI.Tests;

/// <summary>
/// D080, the Inspector's «Динамический масштаб» and «Масштаб, %» with the real edit service: a new followed part has the dynamic
/// scale on unless the clip was scaled by hand (the owner's answer to Q3); the field writes the climber's share (one undo step),
/// «Авто» returns to the automatic one, the check box turns it off.
/// </summary>
public sealed class FollowScaleUiTests
{
    private static readonly FrameRate Rate = FrameRate.Fps25;

    private readonly UndoRedoService _undo = new();
    private readonly ProjectService _projects;
    private readonly TimelineEditService _edit;
    private readonly StatusService _status = new();

    public FollowScaleUiTests()
    {
        _projects = new ProjectService(_undo, NullLogger<ProjectService>.Instance);
        _edit = new TimelineEditService(_projects, _undo, NullLogger<TimelineEditService>.Instance);
    }

    private Action _reshow = () => { };

    /// <summary>The shell shows the clip again after every timeline change.</summary>
    private void Reshow() => _reshow();

    private (InspectorViewModel Inspector, VideoClip Clip) Shown(double scale)
    {
        var asset = new MediaAsset
        {
            FilePath = Path.Combine(Path.GetTempPath(), "follow-scale", Guid.NewGuid().ToString("N"), "climb.mp4"), Kind = MediaKind.Video,
            Metadata = new MediaMetadata { Duration = MediaTime.FromSeconds(20), FrameRate = Rate, Width = 3840, Height = 2160 },
            AnalysisStatus = MediaAnalysisStatus.Completed
        };
        _projects.Current.MediaAssets.Add(asset);
        Assert.True(_edit.AddClip(asset.Id).Success);
        var clip = (VideoClip)_projects.Current.Timeline.VideoTracks[0].Clips[0];
        clip.Scale = scale;
        var inspector = new InspectorViewModel(_edit, _status) { Playhead = () => MediaTime.FromSeconds(4) };
        var selection = new TimelineClipSelection(clip, "climb", asset, Rate);
        _reshow = () => inspector.ShowClip(selection);
        Reshow();
        return (inspector, clip);
    }

    [Fact]
    public void Follow_on_an_unscaled_clip_has_the_dynamic_scale_at_the_automatic_share()
    {
        var (inspector, clip) = Shown(scale: 1);
        inspector.FollowEnabled = true;
        Reshow();
        Assert.True(clip.Follow!.Parts[0].DynamicScale);
        Assert.Null(clip.Follow.Parts[0].ScaleShare);
        Assert.True(inspector.FollowDynamicScale);
        Assert.True(inspector.FollowScaleIsAuto);
        Assert.False(inspector.FollowScaleAutoCommand.CanExecute(null));
    }

    [Fact]
    public void Follow_on_a_clip_scaled_by_hand_keeps_its_scale_and_only_centres()
    {
        var (inspector, clip) = Shown(scale: 1.5);
        inspector.FollowEnabled = true;
        Reshow();
        Assert.False(clip.Follow!.Parts[0].DynamicScale);
        Assert.False(inspector.FollowDynamicScale);
        inspector.FollowFromHereCommand.Execute(null);            // a part added the same way
        Reshow();
        Assert.All(clip.Follow!.Parts, p => Assert.False(p.DynamicScale));
    }

    [Fact]
    public void The_share_is_typed_reset_to_auto_and_turned_off_one_undo_step_each()
    {
        var (inspector, clip) = Shown(scale: 1);
        inspector.FollowEnabled = true;
        Reshow();
        inspector.FollowScalePercent = 40;
        Reshow();
        Assert.Equal(0.4, clip.Follow!.Parts[0].ScaleShare);
        Assert.False(inspector.FollowScaleIsAuto);
        Assert.Equal(40m, inspector.FollowScalePercent);
        Assert.True(inspector.FollowScaleAutoCommand.CanExecute(null));

        inspector.FollowScaleAutoCommand.Execute(null);
        Reshow();
        Assert.Null(clip.Follow!.Parts[0].ScaleShare);
        Assert.True(inspector.FollowScaleIsAuto);

        inspector.FollowDynamicScale = false;
        Reshow();
        Assert.False(clip.Follow!.Parts[0].DynamicScale);

        _undo.Undo();
        Assert.True(clip.Follow!.Parts[0].DynamicScale);
        _undo.Undo();
        Assert.Equal(0.4, clip.Follow!.Parts[0].ScaleShare);
        _undo.Undo();
        Assert.Null(clip.Follow!.Parts[0].ScaleShare);
    }
}
