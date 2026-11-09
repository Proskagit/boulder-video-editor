using System.Collections.Immutable;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Core.Playback;
using AiVideoEditor.Project;
using AiVideoEditor.Timeline;
using AiVideoEditor.UI.Common;
using AiVideoEditor.UI.Services;
using AiVideoEditor.UI.ViewModels.Panels;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiVideoEditor.UI.Tests;

/// <summary>
/// D053 in the view models, on a 60 s clip at 25 fps with route A laid out at T = 250 (green [75, 950)): the «Зацепки»
/// section works on the route whose green holds the playhead and on a new route outside every green — an edit there
/// creates it and never touches A; on the timeline each route's «Боулдер» shows its own strip and a drag on it edits that
/// route only.
/// </summary>
public sealed class RouteOverlayUiTests
{
    private static readonly FrameRate Rate = FrameRate.Fps25;
    private readonly UndoRedoService _undo = new();
    private readonly ProjectService _projects;
    private readonly TimelineEditService _edit;
    private readonly StatusService _status = new();
    private MediaTime _playhead;

    public RouteOverlayUiTests()
    {
        _projects = new ProjectService(_undo, NullLogger<ProjectService>.Instance);
        _edit = new TimelineEditService(_projects, _undo, NullLogger<TimelineEditService>.Instance);
    }

    private static MediaTime F(long n) => MediaTime.FromFrame(n, Rate);

    private static Hold NewHold(double x, double y) => new(Guid.NewGuid(), ImmutableArray.Create(new NormalizedPoint(x - 0.02, y - 0.02),
        new NormalizedPoint(x + 0.02, y - 0.02), new NormalizedPoint(x, y + 0.02)), RouteColor.HexToOkLab("#E53935"), RouteColor.HexToOkLab("#E8DCC8"), MediaTime.Zero);

    private (VideoClip Clip, Guid A) WithRouteA()
    {
        var asset = new MediaAsset
        {
            FilePath = Path.Combine(Path.GetTempPath(), "routes-ui", Guid.NewGuid().ToString("N"), "climb.mp4"), Kind = MediaKind.Video,
            Metadata = new MediaMetadata { Duration = MediaTime.FromSeconds(60), FrameRate = Rate, Width = 1920, Height = 1080 },
            AnalysisStatus = MediaAnalysisStatus.Completed
        };
        _projects.Current.MediaAssets.Add(asset);
        Assert.True(_edit.AddClip(asset.Id).Success);
        var clip = (VideoClip)_projects.Current.Timeline.VideoTracks[0].Clips[0];
        var a = Guid.NewGuid();
        Assert.True(_edit.AddHolds(clip.Id, new[] { NewHold(0.3, 0.8), NewHold(0.4, 0.4) }, a).Success);
        Assert.True(_edit.LayOutIntroAndFinal(clip.Id, F(250), a).Success);
        return (clip, a);
    }

    [Fact]
    public void The_section_follows_the_route_under_the_playhead_and_an_edit_outside_every_green_makes_a_new_route()
    {
        var (clip, a) = WithRouteA();
        var markup = new HoldMarkupViewModel(_edit, _projects, _status) { Playhead = () => _playhead };
        _playhead = F(300);
        markup.SetClip(clip);
        Assert.Equal(a, markup.CurrentRouteId);
        Assert.Equal(2, markup.HoldCount);

        _playhead = F(1200);                                         // after A's green: a new route
        markup.OnPlayheadMoved();
        Assert.Null(markup.CurrentRouteId);
        Assert.Equal(0, markup.HoldCount);

        var routeA = RouteOverlays.Find(clip, a);
        markup.StrokeWidth = 12;                                      // an edit here: the new route gets its id
        var b = Assert.IsType<Guid>(markup.CurrentRouteId);
        Assert.NotEqual(a, b);
        Assert.Equal(12, RouteOverlays.Find(clip, b)!.StrokeWidth1080);
        Assert.Same(routeA, RouteOverlays.Find(clip, a));             // A untouched

        _playhead = F(300);                                           // back in A's green
        markup.OnPlayheadMoved();
        Assert.Equal(a, markup.CurrentRouteId);
        Assert.Equal(2, markup.HoldCount);
        _playhead = F(1200);
        markup.OnPlayheadMoved();
        Assert.Equal(b, markup.CurrentRouteId);                      // B: marked (styled) without a green yet
    }

    [Fact]
    public void Each_routes_plate_shows_its_own_strip_and_a_drag_on_it_edits_that_route_only()
    {
        var (clip, a) = WithRouteA();
        var b = Guid.NewGuid();
        Assert.True(_edit.AddHolds(clip.Id, new[] { NewHold(0.6, 0.6) }, b).Success);
        Assert.True(_edit.LayOutIntroAndFinal(clip.Id, F(1100), b).Success);
        var timeline = new TimelineViewModel(_projects, _edit, _status, NullLogger<TimelineViewModel>.Instance) { SnappingEnabled = false };
        TimelineClipViewModel Vm(Clip c) => timeline.Tracks.SelectMany(x => x.Clips).Single(v => v.Id == c.Id);
        double X(double frames) => TimelineCoordinateMapper.TimeToX(F((long)frames), timeline.PixelsPerSecond);

        var templates = _projects.Current.Timeline.VideoTracks.SelectMany(t => t.Clips).OfType<TemplateClip>().ToList();
        var plateA = Vm(templates.Single(t => t.AnchorRouteId == a));
        var plateB = Vm(templates.Single(t => t.AnchorRouteId == b));
        Assert.Equal(a, plateA.StripRouteId);
        Assert.Equal(b, plateB.StripRouteId);
        Assert.Contains(plateA.RouteSegments, s => s.Kind == StripSegmentKind.Orange && s.Start == 125);
        Assert.Contains(plateB.RouteSegments, s => s.Kind == StripSegmentKind.Orange && s.Start == 975);

        var routeA = RouteOverlays.Find(clip, a);
        // B's orange body, pressed on B's plate (x from its left edge at Ps = 925), dragged 25 frames left.
        var press = timeline.TemplatePressAt(plateB, X(1000 - 925));
        Assert.Equal(OverlayStripHandle.OrangeBody, press?.Handle);
        Assert.True(timeline.BeginStripDrag(press!.Value.Owner, press.Value.Handle, X(1000), pressedOn: plateB));
        timeline.UpdateGesture(X(975), null);
        timeline.EndGesture();
        Assert.Equal(new FrameSpan(950, 1075), _edit.OverlayStripOf(clip.Id, b)!.Orange);
        Assert.Same(routeA, RouteOverlays.Find(clip, a));
        Assert.Equal(new FrameSpan(125, 250), _edit.OverlayStripOf(clip.Id, a)!.Orange);
    }
}
