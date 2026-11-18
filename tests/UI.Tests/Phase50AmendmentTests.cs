using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Project;
using AiVideoEditor.Timeline;
using AiVideoEditor.UI.Common;
using AiVideoEditor.UI.Services;
using AiVideoEditor.UI.ViewModels;
using AiVideoEditor.UI.ViewModels.Panels;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiVideoEditor.UI.Tests;

/// <summary>D069 amendment 1 (the owner's check of 2026-11-18): a drag on the magnetic main track goes by the pointer; a drag the
/// magnet takes back leaves nothing displaced; the playhead lands on a cut; Save As over a project picked by its own folder.</summary>
public sealed class Phase50AmendmentTests : IDisposable
{
    private static readonly FrameRate Rate = FrameRate.Fps25;
    private readonly UndoRedoService _undo = new();
    private readonly ProjectService _projects;
    private readonly TimelineEditService _edit;
    private readonly TimelineViewModel _t;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "aive-p50a-" + Guid.NewGuid().ToString("N"));

    public Phase50AmendmentTests()
    {
        _projects = new ProjectService(_undo, NullLogger<ProjectService>.Instance, magneticMainTrackInNewProjects: true);
        _edit = new TimelineEditService(_projects, _undo, NullLogger<TimelineEditService>.Instance);
        _t = new TimelineViewModel(_projects, _edit, new StatusService(), NullLogger<TimelineViewModel>.Instance);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private static MediaTime F(long n) => MediaTime.FromFrame(n, Rate);
    private double X(long frames) => TimelineCoordinateMapper.TimeToX(F(frames), _t.PixelsPerSecond);
    private TimelineClipViewModel Vm(Clip c) => _t.Tracks.SelectMany(x => x.Clips).Single(v => v.Id == c.Id);
    private TimelineTrackViewModel RowOf(Clip c) => _t.Tracks.Single(r => r.Clips.Contains(Vm(c)));
    private static (long, long) Span(Clip c) => (c.TimelineStart.ToNearestFrame(Rate), c.TimelineEnd.ToNearestFrame(Rate));

    /// <summary>Clips of the given lengths (frames) appended on V1, the magnet on.</summary>
    private List<VideoClip> MainTrack(params long[] lengths)
    {
        foreach (var length in lengths)
        {
            var asset = new MediaAsset
            {
                FilePath = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".mp4"), Kind = MediaKind.Video,
                AnalysisStatus = MediaAnalysisStatus.Completed,
                Metadata = new MediaMetadata { Duration = F(length), FrameRate = Rate, AvgFrameRate = Rate, Width = 1920, Height = 1080 }
            };
            _projects.AddMediaAssets(new[] { asset });
            Assert.True(_edit.AddClip(asset.Id).Success);
        }
        Assert.True(_projects.Current.Timeline.MagneticMainTrack);
        return _projects.Current.Timeline.VideoTracks[0].Clips.OfType<VideoClip>().OrderBy(c => c.TimelineStart).ToList();
    }

    [Fact]
    public void A_long_clip_grabbed_far_from_its_start_goes_where_the_pointer_is()
    {
        var c = MainTrack(400, 50, 50, 50);                                   // A long: 0–400, then B, C, D up to 550
        Assert.True(_t.OnClipPressed(Vm(c[0]), toggle: false));
        _t.BeginMove(X(300));                                                 // grabbed 300 frames after its start
        _t.UpdateGesture(X(540), RowOf(c[0]));                                // the pointer over the second half of D
        _t.EndGesture();

        Assert.Equal((0L, 50L), Span(c[1]));
        Assert.Equal((100L, 150L), Span(c[3]));
        Assert.Equal((150L, 550L), Span(c[0]));                               // after every clip
    }

    [Fact]
    public void A_drag_the_magnet_takes_back_shows_every_clip_where_it_is()
    {
        var c = MainTrack(100, 100);
        _projects.Current.Timeline.VideoTracks.Add(new Track { Type = TrackType.Video, Name = "V2", Order = 1 });
        var plate = new TemplateClip { Kind = TemplateKind.Boulder, TimelineStart = F(110), Duration = F(50), AnchorClipId = c[1].Id };
        _projects.Current.Timeline.VideoTracks[1].Clips.Add(plate);
        _projects.NotifyTimelineChanged();
        var plateVm = Vm(plate);
        var before = plateVm.Left;

        Assert.True(_t.OnClipPressed(Vm(c[1]), toggle: false));
        _t.BeginMove(X(150));
        _t.UpdateGesture(X(130), RowOf(c[1]));                                // 20 frames into B's own place: taken back
        _t.EndGesture();

        Assert.Equal((100L, 200L), Span(c[1]));
        Assert.Equal((110L, 160L), Span(plate));
        Assert.Equal(before, plateVm.Left);                                   // the plate is shown where it is
    }

    [Fact]
    public void A_click_on_the_ruler_near_a_cut_puts_the_playhead_on_it()
    {
        MainTrack(100, 100);
        var nearCut = X(100) + TimelineViewModel.SnapTolerancePixels / 2;

        _t.SetPlayheadFromX(nearCut);
        Assert.Equal(F(100), _t.Playhead);

        _t.SetPlayheadFromX(X(150));
        Assert.Equal(F(150), _t.Playhead);                                     // far from a cut: where clicked
    }

    [Fact]
    public void Save_as_with_a_projects_own_folder_picked_means_that_project()
    {
        var project = Path.Combine(_root, "Climb");
        Directory.CreateDirectory(project);
        File.WriteAllText(Path.Combine(project, "project.json"), "{}");
        var window = new SaveAsViewModel("New", _root, null);
        Assert.Null(window.ReplaceNote);

        window.UsePicked(project);

        Assert.Equal(("Climb", _root), (window.Name, window.Folder));
        Assert.Equal(project, window.TargetFolder);
        Assert.NotNull(window.ReplaceNote);                                    // said before «Сохранить»
    }

    // --- D069 amendment 2 -----------------------------------------------------------------------------------------------

    [Fact]
    public void A_transition_s_edge_dragged_on_the_timeline_sets_its_length_in_one_undo_step()
    {
        var c = MainTrack(200, 200);
        Assert.True(_edit.AddTransition(c[0].Id, c[1].Id, TransitionRules.CrossDissolve).Success);   // 1 s = 25 frames
        var zone = _t.Tracks.SelectMany(t => t.Transitions).Single();
        var transition = zone.Transition;

        Assert.True(_t.IsTransitionEdge(zone, zone.Left + 3) && _t.IsTransitionEdge(zone, zone.Left + zone.Width));   // the resize cursor
        Assert.False(_t.IsTransitionEdge(zone, zone.Left + zone.Width / 2));
        Assert.False(_t.BeginTransitionResize(zone, zone.Left + zone.Width / 2));                 // the middle: no drag
        Assert.True(_t.BeginTransitionResize(zone, zone.Left + zone.Width));                       // the right edge
        _t.UpdateTransitionResize(X(200 + 30));                                                     // 30 frames from the cut
        Assert.Equal(F(25), transition.Duration);                                                   // shown, not yet set
        _t.EndTransitionResize();

        Assert.Equal(F(60), transition.Duration);
        _undo.Undo();
        Assert.Equal(F(25), transition.Duration);

        Assert.True(_t.BeginTransitionResize(zone, zone.Left));
        _t.UpdateTransitionResize(X(200 + 1000));                                                   // far: at most 5 s
        _t.EndTransitionResize();
        Assert.Equal(F(125), transition.Duration);
    }

    [Fact]
    public void A_cancelled_edge_drag_leaves_the_transition_and_adds_no_undo_step()
    {
        var c = MainTrack(200, 200);
        Assert.True(_edit.AddTransition(c[0].Id, c[1].Id, TransitionRules.CrossDissolve).Success);
        var zone = _t.Tracks.SelectMany(t => t.Transitions).Single();
        var (left, width) = (zone.Left, zone.Width);
        var undoable = _undo.CanUndo;

        Assert.True(_t.BeginTransitionResize(zone, zone.Left + zone.Width));
        Assert.True(_t.IsGestureActive);                                                            // Esc reaches it
        _t.UpdateTransitionResize(X(200 + 40));
        Assert.NotEqual(width, zone.Width);                                                         // drawn longer
        _t.CancelTransitionResize();                                                                // Esc / capture lost
        _t.EndTransitionResize();                                                                   // the release after: nothing

        Assert.False(_t.IsGestureActive);
        Assert.Equal(F(25), zone.Transition.Duration);
        Assert.Equal((left, width), (zone.Left, zone.Width));
        _undo.Undo();                                                                               // undoes the add, not a resize
        Assert.Empty(_projects.Current.Timeline.VideoTracks[0].Transitions);
        Assert.True(undoable);
    }

    [Fact]
    public void One_selected_clip_takes_the_cut_nearest_the_playhead()
    {
        var c = MainTrack(100, 100, 100);
        Assert.True(_t.OnClipPressed(Vm(c[1]), toggle: false));

        _t.SetPlayhead(F(105));                                                                      // near B's start
        _t.AddTransitionCommand.Execute(TransitionRules.Flash);
        var first = Assert.Single(_projects.Current.Timeline.VideoTracks[0].Transitions);
        Assert.Equal((c[0].Id, c[1].Id), (first.LeftClipId, first.RightClipId));

        _undo.Undo();
        Assert.True(_t.OnClipPressed(Vm(c[1]), toggle: false));
        _t.SetPlayhead(F(190));                                                                      // near B's end
        _t.AddTransitionCommand.Execute(TransitionRules.Flash);
        var second = Assert.Single(_projects.Current.Timeline.VideoTracks[0].Transitions);
        Assert.Equal((c[1].Id, c[2].Id), (second.LeftClipId, second.RightClipId));
    }
}
