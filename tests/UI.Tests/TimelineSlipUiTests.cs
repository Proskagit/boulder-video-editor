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
/// Phase 15 Step 15.6 (D030 §7, Q12): the Alt + body drag in the timeline view model. Alt at the press makes the gesture a
/// slip for its whole length; while dragging the clip shows its planned Source In / Out, its geometry, the project and the
/// Preview (no timeline notification) stay; the release commits one undo step — the content follows the pointer; Esc, a
/// lost capture or an export starting cancel with nothing changed; images and text don't slip.
/// </summary>
public sealed class TimelineSlipUiTests
{
    private static readonly FrameRate Rate = FrameRate.Fps25;

    private readonly UndoRedoService _undo = new();
    private readonly ProjectService _projects;
    private readonly TimelineEditService _edit;
    private readonly StatusService _status = new();
    private readonly EditingLock _lock = new();
    private int _timelineChanges;

    public TimelineSlipUiTests()
    {
        _projects = new ProjectService(_undo, NullLogger<ProjectService>.Instance);
        _edit = new TimelineEditService(_projects, _undo, NullLogger<TimelineEditService>.Instance);
        _projects.TimelineChanged += (_, _) => _timelineChanges++;
    }

    private TimelineViewModel Timeline() => new(_projects, _edit, _status, NullLogger<TimelineViewModel>.Instance, _lock)
    {
        SnappingEnabled = false
    };

    private Track V1 => _projects.Current.Timeline.VideoTracks[0];
    private static MediaTime F(long n) => MediaTime.FromFrame(n, Rate);

    /// <summary>A 20 s, 25 fps video on V1 at [100, 300) showing source [4 s, 12 s) — room to slip both ways.</summary>
    private MediaBackedClip Video()
    {
        var asset = new MediaAsset
        {
            FilePath = Path.Combine(Path.GetTempPath(), "slip-ui", Guid.NewGuid().ToString("N"), "climb.mp4"), Kind = MediaKind.Video,
            Metadata = new MediaMetadata { Duration = MediaTime.FromSeconds(20), FrameRate = Rate, Width = 1920, Height = 1080 },
            AnalysisStatus = MediaAnalysisStatus.Completed
        };
        _projects.Current.MediaAssets.Add(asset);
        Assert.True(_edit.AddClip(asset.Id).Success);
        var clip = (MediaBackedClip)V1.Clips[0];
        Assert.True(_edit.TrimClip(clip.Id, ClipEdge.Start, F(100)).Success);
        Assert.True(_edit.TrimClip(clip.Id, ClipEdge.End, F(300)).Success);
        _timelineChanges = 0;
        return clip;
    }

    private static TimelineClipViewModel Vm(TimelineViewModel t, Clip c) => t.Tracks.SelectMany(x => x.Clips).Single(v => v.Id == c.Id);
    private static double Px(TimelineViewModel t, double frames) => TimelineCoordinateMapper.TimeToX(MediaTime.FromSeconds(frames / 25.0), t.PixelsPerSecond);

    [Fact]
    public void Alt_and_only_Alt_starts_a_slip_Ctrl_stays_toggle_select()
    {
        Assert.True(TimelineGestureModifiers.IsSlip(KeyModifiers.Alt));
        Assert.True(TimelineGestureModifiers.IsSlip(KeyModifiers.Alt | KeyModifiers.Shift));
        Assert.False(TimelineGestureModifiers.IsSlip(KeyModifiers.None));
        Assert.False(TimelineGestureModifiers.IsSlip(KeyModifiers.Shift));
        Assert.False(TimelineGestureModifiers.IsSlip(KeyModifiers.Control));
        Assert.False(TimelineGestureModifiers.IsSlip(KeyModifiers.Control | KeyModifiers.Alt));
        Assert.True(TimelineGestureModifiers.IsToggle(KeyModifiers.Control | KeyModifiers.Alt));
        Assert.False(TimelineGestureModifiers.IsRippleTrim(KeyModifiers.Alt));
    }

    [Fact]
    public void While_slipping_the_clip_shows_the_planned_source_and_nothing_else_changes_one_step_on_release()
    {
        var clip = Video();
        var t = Timeline();
        var vm = Vm(t, clip);
        var (left, width) = (vm.Left, vm.Width);
        var before = ClipState.Capture(clip);
        var top = _undo.CurrentPosition;
        var dirty = _projects.Current.IsDirty;
        var x0 = vm.Left + vm.Width / 2;

        Assert.True(t.BeginSlip(vm, x0));
        t.UpdateGesture(x0 + Px(t, 3), t.Tracks[0]);
        t.UpdateGesture(x0 + Px(t, 10), t.Tracks[1]);                                     // over another track: still a slip

        var preview = _edit.PreviewSlip(clip.Id, -10)!;
        Assert.Equal($"In {TimeFormat.ToTimecode(preview.SourceIn, Rate)}  Out {TimeFormat.ToTimecode(preview.SourceOut, Rate)}", vm.SlipText);
        Assert.True(vm.IsSlipping);
        Assert.Equal((left, width), (vm.Left, vm.Width));                                    // the geometry stays
        Assert.Equal(before, ClipState.Capture(clip));                                       // the project stays
        Assert.Equal(0, _timelineChanges);                                                   // the Preview is not rebuilt
        Assert.Same(top, _undo.CurrentPosition);
        Assert.Equal(dirty, _projects.Current.IsDirty);

        t.EndGesture();

        Assert.Equal(before.SourceIn - (F(110) - F(100)), clip.SourceIn);                   // pointer right: earlier content
        Assert.Equal((before.Start, before.Duration), (clip.TimelineStart, clip.Duration));
        Assert.Equal(1, _timelineChanges);                                                   // one refresh, on the commit
        Assert.Null(vm.SlipText);
        Assert.Same(clip, V1.Clips[0]);
        _undo.Undo();                                                                        // one step for the gesture
        Assert.Equal(before, ClipState.Capture(clip));
        Assert.Same(top, _undo.CurrentPosition);
    }

    [Fact]
    public void The_pointer_rounds_to_whole_frames()
    {
        var clip = Video();
        var t = Timeline();
        var vm = Vm(t, clip);
        var x0 = vm.Left + vm.Width / 2;
        var sourceIn = clip.SourceIn;

        t.BeginSlip(vm, x0);
        t.UpdateGesture(x0 - Px(t, 2.4), t.Tracks[0]);                                      // 2.4 frames left → +2
        t.EndGesture();
        Assert.Equal(sourceIn + (F(102) - F(100)), clip.SourceIn);
        _undo.Undo();

        t.BeginSlip(vm, x0);
        t.UpdateGesture(x0 - Px(t, 2.6), t.Tracks[0]);                                      // 2.6 → +3
        t.EndGesture();
        Assert.Equal(sourceIn + (F(103) - F(100)), clip.SourceIn);
    }

    [Fact]
    public void Esc_a_lost_capture_or_an_export_starting_cancel_with_nothing_changed()
    {
        var clip = Video();
        var t = Timeline();
        var vm = Vm(t, clip);
        var before = ClipState.Capture(clip);
        var top = _undo.CurrentPosition;
        var dirty = _projects.Current.IsDirty;
        t.SetPlayhead(F(150));
        var x0 = vm.Left + vm.Width / 2;

        foreach (var cancel in new Action[] { t.CancelGesture, t.CancelGesture, () => _lock.Acquire().Dispose() })
        {
            t.BeginSlip(vm, x0);
            t.UpdateGesture(x0 + Px(t, 30), t.Tracks[0]);
            Assert.True(vm.IsSlipping);
            cancel();                                                                        // Esc / lost capture / export
            t.EndGesture();                                                                  // the release after it: nothing

            Assert.False(vm.IsSlipping);
            Assert.Equal(before, ClipState.Capture(clip));
            Assert.Same(top, _undo.CurrentPosition);
            Assert.Equal(dirty, _projects.Current.IsDirty);
            Assert.Equal(F(150), t.Playhead);
            Assert.Equal(0, _timelineChanges);
        }
    }

    [Fact]
    public void Images_and_text_do_not_slip_and_no_gesture_starts()
    {
        var text = new TextClip { Text = "T", TimelineStart = F(0), Duration = F(50) };
        V1.Clips.Add(text);
        var t = Timeline();

        Assert.False(t.BeginSlip(Vm(t, text), 10));
        Assert.Equal("Only video and audio clips can be slipped.", _status.Message);
        t.UpdateGesture(200, t.Tracks[0]);
        t.EndGesture();
        Assert.Equal(F(0), text.TimelineStart);                                             // no move either
        Assert.False(_undo.CanUndo);
    }

    [Fact]
    public void A_locked_track_shows_the_slip_invalid_and_the_release_changes_nothing()
    {
        var clip = Video();
        Assert.True(_edit.SetTrackLocked(V1.Id, true).Success);
        var t = Timeline();
        var vm = Vm(t, clip);
        var before = ClipState.Capture(clip);
        var top = _undo.CurrentPosition;

        t.BeginSlip(vm, vm.Left + 5);
        t.UpdateGesture(vm.Left + 5 + Px(t, 8), t.Tracks[0]);
        Assert.True(vm.IsInvalid);
        t.EndGesture();

        Assert.Equal("Track V1 is locked.", _status.Message);
        Assert.Equal(before, ClipState.Capture(clip));
        Assert.Same(top, _undo.CurrentPosition);
    }

    [Fact]
    public void A_slip_held_at_the_source_start_says_so_and_the_clip_shows_the_limit()
    {
        var clip = Video();
        var t = Timeline();
        var vm = Vm(t, clip);
        var x0 = vm.Left + vm.Width / 2;

        t.BeginSlip(vm, x0);
        t.UpdateGesture(x0 + Px(t, 500), t.Tracks[0]);                                      // far more than 100 frames before
        Assert.EndsWith("(limit)", vm.SlipText);
        t.EndGesture();

        Assert.Equal(MediaTime.Zero, clip.SourceIn);
        Assert.Equal("The slip stopped at the start of the source.", _status.Message);
    }
}
