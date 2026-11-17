using AiVideoEditor.Core.Audio;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Project;
using AiVideoEditor.Timeline;
using AiVideoEditor.UI.Services;
using AiVideoEditor.UI.ViewModels.Panels;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiVideoEditor.UI.Tests;

/// <summary>
/// D064 amendment 1: the Inspector's «Громкость» with volume points — it shows what plays on the playhead's frame; set between two
/// points it raises the whole segment (both points alike), on a point's frame that point, before the first / after the last that
/// edge point; without points the clip's volume as before.
/// </summary>
public sealed class InspectorVolumeSegmentTests
{
    private static readonly FrameRate Rate = FrameRate.Fps25;
    private readonly UndoRedoService _undo = new();
    private readonly ProjectService _projects;
    private readonly TimelineEditService _edit;
    private readonly StatusService _status = new();
    private MediaTime _playhead;

    public InspectorVolumeSegmentTests()
    {
        _projects = new ProjectService(_undo, NullLogger<ProjectService>.Instance);
        _projects.Current.Settings.FrameRate = Rate;
        _edit = new TimelineEditService(_projects, _undo, NullLogger<TimelineEditService>.Instance);
    }

    private static MediaTime S(double s) => MediaTime.FromSeconds(s);

    private (AudioClip Clip, InspectorViewModel Inspector) Setup(params (double Seconds, double Gain)[] points)
    {
        var asset = new MediaAsset
        {
            FilePath = @"C:\media\music.wav", Kind = MediaKind.Audio, AnalysisStatus = MediaAnalysisStatus.Completed,
            Metadata = new MediaMetadata { Duration = S(10), AudioCodec = "pcm" }
        };
        _projects.Current.MediaAssets.Add(asset);
        Assert.True(_edit.AddClip(asset.Id).Success);
        var clip = (AudioClip)_projects.Current.Timeline.AudioTracks[0].Clips[0];
        if (points.Length > 0)
            Assert.True(_edit.SetClipProperties(clip.Id, new ClipPropertyChange
            {
                Audio = AudioProperties.Of(clip)!.Value with
                {
                    Volumes = VolumeEnvelope.Of(points.Select(p => new VolumePoint(Guid.NewGuid(), S(p.Seconds), p.Gain)))
                }
            }).Success);
        var inspector = new InspectorViewModel(_edit, _status) { Playhead = () => _playhead };
        inspector.ShowClip(new TimelineClipSelection(clip, "music", asset, Rate));
        return (clip, inspector);
    }

    [Fact]
    public void Between_two_points_the_whole_segment_is_raised()
    {
        var (clip, inspector) = Setup((1, 0.5), (3, 0.5), (5, 1));
        _playhead = S(2);
        inspector.OnPlayheadMoved();
        Assert.Equal(50m, inspector.VolumePercent);                               // what plays there

        inspector.VolumePercent = 100;
        Assert.Equal(new[] { 1.0, 1.0, 1.0 }, clip.Volumes!.Points.Select(p => p.Gain));
        Assert.Equal(1.0, clip.Volume);                                           // the clip's volume is not touched
    }

    [Fact]
    public void On_a_points_frame_only_that_point_changes_and_before_the_first_the_first()
    {
        var (clip, inspector) = Setup((1, 0.5), (3, 0.5));
        _playhead = S(3);
        inspector.OnPlayheadMoved();
        inspector.VolumePercent = 150;
        Assert.Equal(new[] { 0.5, 1.5 }, clip.Volumes!.Points.Select(p => p.Gain));

        _playhead = S(0.2);
        inspector.OnPlayheadMoved();
        inspector.VolumePercent = 25;
        Assert.Equal(new[] { 0.25, 1.5 }, clip.Volumes!.Points.Select(p => p.Gain));
    }

    [Fact]
    public void Without_points_the_field_is_the_clips_volume()
    {
        var (clip, inspector) = Setup();
        inspector.VolumePercent = 70;
        Assert.Equal(0.7, clip.Volume, 9);
        Assert.Null(clip.Volumes);
    }
}
