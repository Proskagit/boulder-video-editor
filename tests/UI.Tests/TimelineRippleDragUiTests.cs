using System.Text;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Project;
using AiVideoEditor.Timeline;
using AiVideoEditor.UI.Common;
using AiVideoEditor.UI.Services;
using AiVideoEditor.UI.ViewModels.Panels;
using Avalonia.Input;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiVideoEditor.UI.Tests;

/// <summary>
/// Phase 15 Step 15.5 (D030 §6): the Shift + edge drag in the timeline view model. Shift at the press makes the gesture a
/// ripple trim; while dragging the view shows the edit service's plan (the trimmed clip, the moved clips and the dissolve
/// zones) and nothing in the project changes; the release commits one undo step, equal to Shift+Q / Shift+W at the same
/// frame; Esc restores everything. Without Shift the ordinary trim is unchanged.
/// </summary>
public sealed class TimelineRippleDragUiTests
{
    private static readonly FrameRate Rate = FrameRate.Default;

    private readonly UndoRedoService _undo = new();
    private readonly ProjectService _projects;
    private readonly TimelineEditService _edit;
    private readonly StatusService _status = new();
    private readonly EditingLock _lock = new();

    public TimelineRippleDragUiTests()
    {
        _projects = new ProjectService(_undo, NullLogger<ProjectService>.Instance);
        _edit = new TimelineEditService(_projects, _undo, NullLogger<TimelineEditService>.Instance);
    }

    private TimelineViewModel Timeline()
    {
        var timeline = new TimelineViewModel(_projects, _edit, _status, NullLogger<TimelineViewModel>.Instance, _lock);
        timeline.SnappingEnabled = false;                                   // the pointer lands where it is
        return timeline;
    }

    private Track V1 => _projects.Current.Timeline.VideoTracks[0];
    private static MediaTime F(long n) => MediaTime.FromFrame(n, Rate);
    private static (long, long) Frames(Clip c) => (c.TimelineStart.ToNearestFrame(Rate), c.TimelineEnd.ToNearestFrame(Rate));

    private static TextClip Text(Track track, long start, long end)
    {
        var clip = new TextClip { Text = "T", TimelineStart = F(start), Duration = F(end) - F(start) };
        track.Clips.Add(clip);
        return clip;
    }

    private static TimelineClipViewModel Vm(TimelineViewModel t, Clip c) => t.Tracks.SelectMany(x => x.Clips).Single(v => v.Id == c.Id);
    private static double X(TimelineViewModel t, long frame) => TimelineCoordinateMapper.TimeToX(F(frame), t.PixelsPerSecond);

    private string State()
    {
        var sb = new StringBuilder();
        foreach (var track in _projects.Current.Timeline.VideoTracks.Concat(_projects.Current.Timeline.AudioTracks))
        {
            foreach (var c in track.Clips) sb.Append($"{c.Id}:{c.TimelineStart.Ticks},{c.Duration.Ticks},{c.FadeIn.Ticks},{c.FadeOut.Ticks};");
            foreach (var t in track.Transitions) sb.Append($"T{t.Id}:{t.LeftClipId}|{t.RightClipId}:{t.Duration.Ticks};");
            sb.Append('\n');
        }
        return sb.ToString();
    }

    private static void Drag(TimelineViewModel t, TimelineClipViewModel clip, ClipEdge edge, long fromFrame, bool ripple, params long[] path)
    {
        t.BeginTrim(clip, edge, X(t, fromFrame), ripple);
        foreach (var frame in path) t.UpdateGesture(X(t, frame), t.Tracks[0]);
    }

    [Fact]
    public void Shift_and_only_Shift_starts_a_ripple_trim_Ctrl_stays_toggle_select()
    {
        Assert.True(TimelineGestureModifiers.IsRippleTrim(KeyModifiers.Shift));
        Assert.False(TimelineGestureModifiers.IsRippleTrim(KeyModifiers.None));
        Assert.False(TimelineGestureModifiers.IsRippleTrim(KeyModifiers.Alt));
        Assert.False(TimelineGestureModifiers.IsRippleTrim(KeyModifiers.Control));
        Assert.False(TimelineGestureModifiers.IsRippleTrim(KeyModifiers.Control | KeyModifiers.Shift));
        Assert.True(TimelineGestureModifiers.IsToggle(KeyModifiers.Control));
        Assert.True(TimelineGestureModifiers.IsToggle(KeyModifiers.Control | KeyModifiers.Shift));
        Assert.False(TimelineGestureModifiers.IsToggle(KeyModifiers.Shift));
    }

    [Fact]
    public void While_dragging_the_preview_moves_the_later_clips_and_the_project_does_not_change()
    {
        var a = Text(V1, 0, 100);
        var b = Text(V1, 100, 150);
        var c = Text(V1, 200, 250);
        var t = Timeline();
        var before = State();
        var (aVm, bVm, cVm) = (Vm(t, a), Vm(t, b), Vm(t, c));
        var playhead = t.Playhead;

        Drag(t, aVm, ClipEdge.End, 100, ripple: true, 95, 80, 60);

        Assert.Equal(before, State());                                                   // nothing applied yet
        Assert.False(_undo.CanUndo);
        Assert.Equal(X(t, 60), aVm.Left + aVm.Width, 6);                                 // the planned places
        Assert.Equal(X(t, 60), bVm.Left, 6);
        Assert.Equal(X(t, 160), cVm.Left, 6);                                            // the gap kept
        Assert.False(aVm.IsInvalid);

        t.EndGesture();

        Assert.Equal(((0L, 60L), (60L, 110L), (160L, 210L)), (Frames(a), Frames(b), Frames(c)));
        Assert.Equal(playhead, t.Playhead);                                              // a drag never moves it
        Assert.True(_undo.CanUndo);
        _undo.Undo();                                                                    // one step for the whole drag
        Assert.Equal(before, State());
        Assert.False(_undo.CanUndo);
        Assert.False(_projects.Current.IsDirty);
    }

    [Fact]
    public void Esc_after_moving_restores_everything_and_changes_nothing()
    {
        var a = Text(V1, 0, 100);
        var b = Text(V1, 100, 150);
        var t = Timeline();
        var before = State();
        var (aVm, bVm) = (Vm(t, a), Vm(t, b));
        var layout = (aVm.Left, aVm.Width, bVm.Left, bVm.Width);
        t.SetPlayhead(F(42));

        Drag(t, aVm, ClipEdge.Start, 0, ripple: true, 10, 40);
        Assert.True(t.IsGestureActive);
        t.CancelGesture();                                                               // what Esc does (TimelineView)

        Assert.False(t.IsGestureActive);
        Assert.Equal(before, State());
        Assert.Equal(layout, (aVm.Left, aVm.Width, bVm.Left, bVm.Width));
        Assert.False(_undo.CanUndo);
        Assert.False(_projects.Current.IsDirty);
        Assert.Equal(F(42), t.Playhead);
        t.EndGesture();                                                                  // a release after Esc: nothing
        Assert.Equal(before, State());
    }

    [Fact]
    public void A_ripple_drag_equals_Shift_Q_and_Shift_W_at_the_same_frame()
    {
        foreach (var edge in new[] { ClipEdge.Start, ClipEdge.End })
        {
            var a = Text(V1, 0, 100);
            Text(V1, 100, 150);
            Text(V1, 200, 260);
            var t = Timeline();
            var before = State();

            Drag(t, Vm(t, a), edge, edge == ClipEdge.Start ? 0 : 100, ripple: true, 50, 33);
            t.EndGesture();
            var dragged = State();
            _undo.Undo();
            Assert.Equal(before, State());

            t.OnClipPressed(Vm(t, a), toggle: false);
            t.SetPlayhead(F(33));
            (edge == ClipEdge.Start ? t.RippleTrimStartToPlayheadCommand : t.RippleTrimEndToPlayheadCommand).Execute(null);
            Assert.Equal(dragged, State());

            _undo.Undo();
            V1.Clips.Clear();
        }
    }

    [Fact]
    public void Without_Shift_the_ordinary_trim_is_unchanged_it_stops_at_the_neighbour_and_moves_nothing_else()
    {
        var a = Text(V1, 0, 100);
        var b = Text(V1, 120, 150);
        var t = Timeline();
        var bVm = Vm(t, b);
        var bLeft = bVm.Left;

        Drag(t, Vm(t, a), ClipEdge.End, 100, ripple: false, 110, 140);
        Assert.Equal(bLeft, bVm.Left);
        t.EndGesture();

        Assert.Equal((0L, 120L), Frames(a));                                              // clamped at B
        Assert.Equal((120L, 150L), Frames(b));
        _undo.Undo();
        Assert.False(_undo.CanUndo);
    }

    [Fact]
    public void A_locked_track_shows_the_drag_invalid_and_the_release_changes_nothing()
    {
        var a = Text(V1, 0, 100);
        Text(V1, 100, 150);
        Assert.True(_edit.SetTrackLocked(V1.Id, true).Success);
        var t = Timeline();
        var before = State();
        var top = _undo.CurrentPosition;
        var aVm = Vm(t, a);

        Drag(t, aVm, ClipEdge.End, 100, ripple: true, 60);
        Assert.True(aVm.IsInvalid);
        t.EndGesture();

        Assert.Equal("Track V1 is locked.", _status.Message);
        Assert.Equal(before, State());
        Assert.Same(top, _undo.CurrentPosition);
    }

    [Fact]
    public void The_dissolve_zone_moves_with_its_clips_in_the_preview_and_stays_after_the_release()
    {
        var a = Text(V1, 0, 100);
        var b = Text(V1, 100, 200);
        Assert.True(_edit.AddTransition(a.Id, b.Id, F(20)).Success);
        var t = Timeline();
        var zoneLeft = t.Tracks[0].Transitions.Single().Left;

        Drag(t, Vm(t, a), ClipEdge.End, 100, ripple: true, 70);                        // the cut moves to 70
        var zone = t.Tracks[0].Transitions.Single();
        Assert.True(zone.IsVisible);
        Assert.Equal(zoneLeft - X(t, 30), zone.Left, 6);

        t.EndGesture();
        Assert.Single(V1.Transitions);
        Assert.Equal((70L, 170L), Frames(b));
        Assert.Equal(zoneLeft - X(t, 30), t.Tracks[0].Transitions.Single().Left, 6);
    }
}
