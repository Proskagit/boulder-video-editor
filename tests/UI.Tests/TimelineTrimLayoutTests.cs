using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Project;
using AiVideoEditor.Timeline;
using AiVideoEditor.UI.Common;
using AiVideoEditor.UI.Services;
using AiVideoEditor.UI.ViewModels.Panels;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiVideoEditor.UI.Tests;

/// <summary>
/// Trimming a clip edge on the timeline: the clip rectangle (Left/Width) follows the drag preview
/// and then the committed model timing — also after Phase 7 property edits of the same clip and
/// through undo/redo.
/// </summary>
public sealed class TimelineTrimLayoutTests
{
    private static readonly FrameRate Rate = FrameRate.Fps25;

    private readonly UndoRedoService _undo = new();
    private readonly ProjectService _projects;
    private readonly TimelineEditService _edit;
    private readonly TimelineViewModel _timeline;

    public TimelineTrimLayoutTests()
    {
        _projects = new ProjectService(_undo, NullLogger<ProjectService>.Instance);
        _edit = new TimelineEditService(_projects, _undo, NullLogger<TimelineEditService>.Instance);
        _timeline = new TimelineViewModel(_projects, _edit, new StatusService(), NullLogger<TimelineViewModel>.Instance);
    }

    private static MediaTime F(long frame) => MediaTime.FromFrame(frame, Rate);

    private (VideoClip Clip, TimelineClipViewModel View) AddVideo()
    {
        var asset = new MediaAsset
        {
            FilePath = Path.Combine(Path.GetTempPath(), "aive-trim-layout", Guid.NewGuid().ToString("N"), "v.mp4"),
            Kind = MediaKind.Video,
            AnalysisStatus = MediaAnalysisStatus.Completed,
            Metadata = new MediaMetadata
            {
                Duration = F(250), FrameRate = Rate, AvgFrameRate = Rate, Width = 1920, Height = 1080,
                DisplayRotation = 0, DisplayWidth = 1920, DisplayHeight = 1080
            }
        };
        _projects.AddMediaAssets(new[] { asset });
        var result = _edit.AddClip(asset.Id, null, MediaTime.Zero);
        Assert.True(result.Success, result.Message);
        var view = _timeline.Tracks.SelectMany(t => t.Clips).Single(c => c.Id == result.ClipIds[0]);
        return ((VideoClip)view.Clip, view);
    }

    private void AssertLaidOutFromModel(TimelineClipViewModel view)
    {
        Assert.Equal(TimelineCoordinateMapper.TimeToX(view.Clip.TimelineStart, _timeline.PixelsPerSecond), view.Left, 6);
        Assert.Equal(TimelineCoordinateMapper.TimeToX(view.Clip.Duration, _timeline.PixelsPerSecond), view.Width, 6);
    }

    private void Drag(TimelineClipViewModel view, ClipEdge edge, double dx)
    {
        var x = edge == ClipEdge.Start ? view.Left : view.Left + view.Width;
        var track = _timeline.Tracks.Single(t => t.Clips.Contains(view));
        _timeline.BeginTrim(view, edge, x);
        _timeline.UpdateGesture(x + dx, track);
    }

    [Fact]
    public void End_trim_resizes_the_clip_while_dragging_and_after_commit()
    {
        var (clip, view) = AddVideo();
        var fullWidth = view.Width;

        Drag(view, ClipEdge.End, -fullWidth / 2);
        Assert.Equal(fullWidth / 2, view.Width, 6);          // drag preview
        Assert.Equal(F(250), clip.Duration);                 // model untouched while dragging

        _timeline.EndGesture();
        Assert.Equal(F(125), clip.Duration);
        AssertLaidOutFromModel(view);

        Drag(view, ClipEdge.End, fullWidth / 4);             // grow again
        _timeline.EndGesture();
        Assert.True(clip.Duration > F(125));
        AssertLaidOutFromModel(view);
    }

    [Fact]
    public void Start_trim_moves_and_resizes_the_clip()
    {
        var (clip, view) = AddVideo();
        var fullWidth = view.Width;

        Drag(view, ClipEdge.Start, fullWidth / 5);
        _timeline.EndGesture();

        Assert.Equal(F(50), clip.TimelineStart);
        Assert.Equal(F(200), clip.Duration);
        AssertLaidOutFromModel(view);
    }

    [Fact]
    public void Trim_after_property_edits_of_the_same_clip_still_resizes_it()
    {
        var (clip, view) = AddVideo();
        var visual = VisualProperties.Of(clip)!.Value;
        Assert.True(_edit.SetClipProperties(clip.Id, new ClipPropertyChange { Visual = visual with { Scale = 0.5, Crop = visual.Crop with { Left = 0.2 } } }).Success);
        Assert.True(_edit.SetClipProperties(clip.Id, new ClipPropertyChange { Audio = AudioProperties.Of(clip)!.Value with { Volume = 0.5 } }).Success);

        Drag(view, ClipEdge.End, -view.Width / 2);
        _timeline.EndGesture();

        Assert.Equal(F(125), clip.Duration);
        AssertLaidOutFromModel(view);
        Assert.Equal(0.5, VisualProperties.Of(clip)!.Value.Scale);  // the trim keeps the properties
    }

    [Fact]
    public void Undo_and_redo_of_a_trim_restore_the_clip_width()
    {
        var (clip, view) = AddVideo();
        var fullWidth = view.Width;

        Drag(view, ClipEdge.End, -fullWidth / 2);
        _timeline.EndGesture();
        var trimmedWidth = view.Width;

        _undo.Undo();
        Assert.Equal(F(250), clip.Duration);
        Assert.Equal(fullWidth, view.Width, 6);

        _undo.Redo();
        Assert.Equal(trimmedWidth, view.Width, 6);
        AssertLaidOutFromModel(view);
    }
}
