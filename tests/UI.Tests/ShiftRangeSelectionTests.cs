using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Project;
using AiVideoEditor.Timeline;
using AiVideoEditor.UI.Common;
using AiVideoEditor.UI.Services;
using AiVideoEditor.UI.ViewModels.Panels;
using Avalonia.Input;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiVideoEditor.UI.Tests;

/// <summary>D057 §3.5: Shift+click on a clip's body selects the range from the anchor; on an edge Shift stays the ripple
/// trim; Ctrl+click toggles as before.</summary>
public sealed class ShiftRangeSelectionTests
{
    private static readonly FrameRate Rate = FrameRate.Fps25;
    private readonly ProjectService _projects;
    private readonly TimelineEditService _edit;
    private readonly TimelineViewModel _timeline;

    public ShiftRangeSelectionTests()
    {
        var undo = new UndoRedoService();
        _projects = new ProjectService(undo, NullLogger<ProjectService>.Instance);
        _projects.Current.Settings.FrameRate = Rate;
        _edit = new TimelineEditService(_projects, undo, NullLogger<TimelineEditService>.Instance);
        _timeline = new TimelineViewModel(_projects, _edit, new StatusService(), NullLogger<TimelineViewModel>.Instance);
    }

    private IEnumerable<Guid> SelectedIds => _timeline.Tracks.SelectMany(t => t.Clips).Where(c => c.IsSelected).Select(c => c.Id);

    private TimelineClipViewModel Vm(Clip clip) => _timeline.Tracks.SelectMany(t => t.Clips).Single(c => c.Id == clip.Id);

    private TextClip Text(int track, long startFrame)
    {
        while (_projects.Current.Timeline.VideoTracks.Count <= track) _edit.AddTrack(TrackType.Video);
        var clip = new TextClip { Text = "x", TimelineStart = MediaTime.FromFrame(startFrame, Rate), Duration = MediaTime.FromFrame(50, Rate) };
        _projects.Current.Timeline.VideoTracks[track].Clips.Add(clip);
        _projects.NotifyTimelineChanged();
        return clip;
    }

    [Fact]
    public void On_one_track_the_clips_between_are_selected()
    {
        var a = Text(0, 0); var b = Text(0, 60); var c = Text(0, 120); var d = Text(0, 180); var e = Text(0, 240);
        _timeline.OnClipPressed(Vm(b), toggle: false);
        _timeline.SelectRangeTo(Vm(d));
        Assert.Equal(new[] { b.Id, c.Id, d.Id }.OrderBy(x => x), SelectedIds.OrderBy(x => x));

        _timeline.SelectRangeTo(Vm(a));                                    // the anchor stays: b … a
        Assert.Equal(new[] { a.Id, b.Id }.OrderBy(x => x), SelectedIds.OrderBy(x => x));
        Assert.DoesNotContain(e.Id, SelectedIds);
    }

    [Fact]
    public void Across_tracks_the_clips_of_the_tracks_between_in_the_interval_are_selected()
    {
        var a = Text(0, 0);
        var inside = Text(1, 70);
        var outside = Text(1, 400);
        var c = Text(2, 120);
        _timeline.OnClipPressed(Vm(a), toggle: false);
        _timeline.SelectRangeTo(Vm(c));
        Assert.Contains(inside.Id, SelectedIds);
        Assert.Contains(c.Id, SelectedIds);
        Assert.DoesNotContain(outside.Id, SelectedIds);
    }

    [Fact]
    public void Without_an_anchor_the_clip_is_selected_alone()
    {
        var a = Text(0, 0);
        _timeline.SelectRangeTo(Vm(a));
        Assert.Equal(new[] { a.Id }, SelectedIds);
    }

    [Theory]
    [InlineData(KeyModifiers.Shift, true)]
    [InlineData(KeyModifiers.Shift | KeyModifiers.Control, false)]          // a toggle-select
    [InlineData(KeyModifiers.Shift | KeyModifiers.Alt, false)]
    [InlineData(KeyModifiers.None, false)]
    public void Shift_alone_is_the_range_and_stays_the_ripple_trim_on_an_edge(KeyModifiers modifiers, bool range)
    {
        Assert.Equal(range, TimelineGestureModifiers.IsRange(modifiers));
        Assert.Equal(modifiers.HasFlag(KeyModifiers.Shift) && !modifiers.HasFlag(KeyModifiers.Control),
            TimelineGestureModifiers.IsRippleTrim(modifiers));
    }
}
