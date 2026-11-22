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
/// D074 §5: a «Боулдер» anchored to a video clip, selected on the timeline, carries that clip — the Inspector's «Слежение и
/// люди» edits the clip, and «Зацепки» shows the clip's holds of the plate's route; a free plate carries none.
/// </summary>
public sealed class AnchoredPlateInspectorTests
{
    private static readonly FrameRate Rate = FrameRate.Default;
    private readonly UndoRedoService _undo = new();
    private readonly ProjectService _projects;
    private readonly TimelineEditService _edit;
    private readonly StatusService _status = new();

    public AnchoredPlateInspectorTests()
    {
        _projects = new ProjectService(_undo, NullLogger<ProjectService>.Instance);
        _edit = new TimelineEditService(_projects, _undo, NullLogger<TimelineEditService>.Instance);
    }

    private (VideoClip Video, TemplateClip Anchored, TemplateClip Free, Guid Route) Scene()
    {
        var asset = new MediaAsset { FilePath = "C:/v.mp4", Kind = MediaKind.Video };
        _projects.AddMediaAssets(new[] { asset });
        var video = new VideoClip { MediaAssetId = asset.Id, TimelineStart = MediaTime.Zero, Duration = MediaTime.FromSeconds(20), SourceOut = MediaTime.FromSeconds(20) };
        _projects.Current.Timeline.VideoTracks[0].Clips.Add(video);
        var plates = new Track { Type = TrackType.Video, Name = "V2", Order = 1 };
        _projects.Current.Timeline.VideoTracks.Add(plates);
        var route = Guid.NewGuid();
        var anchored = new TemplateClip { Kind = TemplateKind.Boulder, TimelineStart = MediaTime.FromSeconds(2), Duration = MediaTime.FromSeconds(5),
            AnchorClipId = video.Id, AnchorRouteId = route };
        var free = new TemplateClip { Kind = TemplateKind.Boulder, TimelineStart = MediaTime.FromSeconds(10), Duration = MediaTime.FromSeconds(5) };
        plates.Clips.Add(anchored);
        plates.Clips.Add(free);
        return (video, anchored, free, route);
    }

    private static TimelineClipSelection? Press(TimelineViewModel timeline, Clip clip)
    {
        TimelineClipSelection? selection = null;
        timeline.SelectionChanged += (_, s) => selection = s;
        timeline.OnClipPressed(timeline.Tracks.SelectMany(t => t.Clips).Single(c => c.Id == clip.Id), toggle: false);
        return selection;
    }

    [Fact]
    public void An_anchored_plate_carries_its_clip_and_the_people_section_edits_the_clip()
    {
        var (video, anchored, free, route) = Scene();
        var timeline = new TimelineViewModel(_projects, _edit, _status, NullLogger<TimelineViewModel>.Instance);

        var selection = Press(timeline, anchored);
        Assert.Same(video, selection!.Anchor);
        Assert.Equal(route, selection.AnchorRouteId);
        Assert.Null(Press(timeline, free)!.Anchor);
        Assert.Null(Press(timeline, video)!.Anchor);

        var inspector = new InspectorViewModel(_edit, _status);
        inspector.ShowClip(Press(timeline, anchored)!);
        inspector.HidesOutlinesBehindPeople = true;
        Assert.True(video.HidesOutlinesBehindPeople);                  // the plate's clip, one undo step
        _undo.Undo();
        Assert.False(video.HidesOutlinesBehindPeople);
    }

    [Fact]
    public void An_anchored_plates_follow_part_under_the_playhead_survives_a_playhead_move()   // the review's finding
    {
        var (video, anchored, _, _) = Scene();
        video.Follow = FollowSettings.Of(new FollowPart(MediaTime.FromSeconds(1), MediaTime.FromSeconds(9)));
        var timeline = new TimelineViewModel(_projects, _edit, _status, NullLogger<TimelineViewModel>.Instance);
        var inspector = new InspectorViewModel(_edit, _status) { Playhead = () => MediaTime.FromSeconds(4) };
        inspector.ShowClip(Press(timeline, anchored)!);
        inspector.OnPlayheadMoved();
        Assert.True(inspector.HasFollowPartHere);
        Assert.True(inspector.DeleteFollowPartCommand.CanExecute(null));
        inspector.Playhead = () => MediaTime.FromSeconds(12);
        inspector.OnPlayheadMoved();
        Assert.False(inspector.HasFollowPartHere);
    }

    [Fact]
    public void The_holds_category_is_offered_for_an_anchored_plate_not_for_a_free_one()
    {
        var (video, anchored, free, route) = Scene();
        var timeline = new TimelineViewModel(_projects, _edit, _status, NullLogger<TimelineViewModel>.Instance);
        var markup = new HoldMarkupViewModel(_edit, _projects, _status);
        var inspector = new InspectorViewModel(_edit, _status) { HoldMarkup = markup };

        var selection = Press(timeline, anchored)!;
        markup.SetClip(selection.Anchor ?? selection.Clip, selection.Anchor is null ? null : selection.AnchorRouteId);   // as the main window wires it
        inspector.ShowClip(selection);
        Assert.True(markup.HasClip);
        Assert.True(inspector.IsCategoryAvailable(InspectorCategory.Holds));
        Assert.True(inspector.IsCategoryAvailable(InspectorCategory.Text));          // the plate's own fields stay

        selection = Press(timeline, free)!;
        markup.SetClip(selection.Anchor ?? selection.Clip, null);
        inspector.ShowClip(selection);
        Assert.False(markup.HasClip);
        Assert.False(inspector.IsCategoryAvailable(InspectorCategory.Holds));
    }
}
