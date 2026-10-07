using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Project;
using AiVideoEditor.Project.Persistence;
using AiVideoEditor.Timeline;
using AiVideoEditor.UI.Common;
using AiVideoEditor.UI.Services;
using AiVideoEditor.UI.ViewModels.Panels;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiVideoEditor.UI.Tests;

/// <summary>
/// Phase 15 Step 15.7 (D030 §8): the In / Out range in the timeline view model and its session service. I / O set the
/// points at the playhead (O includes the playhead's frame); the bar, the In / Out lines and the ✕ follow the range, the
/// zoom, timeline edits and the playhead; ✕ clears it. The range is session state: no dirty state, no undo step, no
/// timeline notification, nothing in <c>project.json</c>; New / Open / Recover start without it; Undo / Redo of edits
/// leave it alone.
/// </summary>
public sealed class InOutRangeUiTests : IDisposable
{
    private static readonly FrameRate Rate = FrameRate.Fps25;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "AiVideoEditorTests", Guid.NewGuid().ToString("N"));
    private readonly UndoRedoService _undo = new();
    private readonly ProjectService _projects;
    private readonly TimelineEditService _edit;
    private readonly StatusService _status = new();
    private readonly InOutRangeService _inOut;
    private int _timelineChanges;

    public InOutRangeUiTests()
    {
        _projects = new ProjectService(_undo, NullLogger<ProjectService>.Instance);
        _edit = new TimelineEditService(_projects, _undo, NullLogger<TimelineEditService>.Instance);
        _inOut = new InOutRangeService(_projects);
        _projects.TimelineChanged += (_, _) => _timelineChanges++;
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private TimelineViewModel Timeline() =>
        new(_projects, _edit, _status, NullLogger<TimelineViewModel>.Instance, inOut: _inOut);

    private Track V1 => _projects.Current.Timeline.VideoTracks[0];
    private static MediaTime F(long n) => MediaTime.FromFrame(n, Rate);
    private static double X(TimelineViewModel t, long frame) => TimelineCoordinateMapper.TimeToX(F(frame), t.PixelsPerSecond);

    /// <summary>A 25 fps project (a video fixes the rate) with 200 frames on V1.</summary>
    private void Sequence200()
    {
        var asset = new MediaAsset
        {
            FilePath = Path.Combine(_root, "a.mp4"), Kind = MediaKind.Video, AnalysisStatus = MediaAnalysisStatus.Completed,
            Metadata = new MediaMetadata { Duration = F(200), FrameRate = Rate, Width = 64, Height = 36 }
        };
        _projects.AddMediaAssets(new[] { asset });
        Assert.True(_edit.AddClip(asset.Id).Success);
        _undo.Clear();
        _projects.Current.IsDirty = false;
        _timelineChanges = 0;
    }

    [Fact]
    public void I_and_O_set_the_points_at_the_playhead_the_frame_at_O_included()
    {
        Sequence200();
        var t = Timeline();

        t.SetPlayhead(F(40));
        t.SetInCommand.Execute(null);
        t.SetPlayhead(F(90));
        t.SetOutCommand.Execute(null);

        Assert.Equal((F(40), F(91)), (_inOut.Range.In!.Value, _inOut.Range.Out!.Value));
        Assert.Equal((40L, 91L), _inOut.Frames);
        Assert.Equal("Out set after 00:00:03:15 (that frame is included).", _status.Message);
        Assert.True(t.HasIn && t.HasOut && t.HasRange && t.CanClearRange);
        Assert.Equal(X(t, 40), t.RangeLeft, 6);
        Assert.Equal(X(t, 91) - X(t, 40), t.RangeWidth, 6);
        Assert.Equal((X(t, 40), X(t, 91)), (t.InX, t.OutX));
        Assert.Equal(X(t, 91), t.RangeRight, 6);
        Assert.True(t.IsPlayheadInRange);                                                // the playhead on frame 90

        t.SetPlayhead(F(91));
        Assert.False(t.IsPlayheadInRange);                                               // Out is exclusive
    }

    [Fact]
    public void Setting_and_clearing_the_range_is_session_state_no_dirty_no_undo_no_timeline_change()
    {
        Sequence200();
        var t = Timeline();
        t.SetPlayhead(F(10));
        t.SetInCommand.Execute(null);
        t.SetPlayhead(F(20));
        t.SetOutCommand.Execute(null);
        t.ClearInOutCommand.Execute(null);

        Assert.False(_inOut.Range.IsSet);
        Assert.False(t.HasRange || t.HasIn || t.HasOut || t.CanClearRange);
        Assert.Equal("In / Out cleared.", _status.Message);
        Assert.False(_projects.Current.IsDirty);
        Assert.False(_undo.CanUndo);
        Assert.Equal(0, _timelineChanges);
    }

    [Fact]
    public void The_bar_follows_zoom_edits_undo_and_redo_and_the_range_is_never_part_of_them()
    {
        Sequence200();
        var t = Timeline();
        t.SetPlayhead(F(150));
        t.SetInCommand.Execute(null);                                                    // In at 150: [150, 200)
        var clip = V1.Clips[0];

        t.ZoomInCommand.Execute(null);
        Assert.Equal(X(t, 150), t.RangeLeft, 6);                                          // laid out at the new zoom
        Assert.Equal(X(t, 200) - X(t, 150), t.RangeWidth, 6);

        Assert.True(_edit.TrimClip(clip.Id, Core.Interfaces.ClipEdge.End, F(170)).Success);   // the sequence shrinks
        Assert.Equal(X(t, 170) - X(t, 150), t.RangeWidth, 6);
        Assert.Equal(F(150), _inOut.Range.In);                                           // edits never move the points

        Assert.True(_edit.TrimClip(clip.Id, Core.Interfaces.ClipEdge.End, F(120)).Success);   // the range leaves the sequence
        Assert.False(t.HasRange);
        Assert.True(t.HasIn && t.CanClearRange);                                         // the In point is still shown

        _undo.Undo();
        _undo.Undo();
        Assert.Equal(F(150), _inOut.Range.In);                                           // undo / redo leave the range alone
        Assert.True(t.HasRange);
        _undo.Redo();
        Assert.Equal(F(150), _inOut.Range.In);
    }

    [Fact]
    public async Task The_range_is_not_saved_and_New_Open_and_Recover_start_without_it()
    {
        Sequence200();
        var folder = Path.Combine(_root, "Project");
        await _projects.SaveAsAsync(folder);
        var file = Path.Combine(folder, "project.json");
        var saved = File.ReadAllText(file);
        _inOut.SetIn(F(30));
        _inOut.SetOut(F(60));

        Assert.False(_projects.Current.IsDirty);                                         // the range doesn't dirty
        await _projects.SaveAsAsync(Path.Combine(_root, "Again"));
        Assert.Equal(saved.Replace("Project", "Again"), File.ReadAllText(Path.Combine(_root, "Again", "project.json"))
            .Replace("Project", "Again"));                                               // nothing of the range in the file
        Assert.Contains("\"formatVersion\": 3", saved);
        Assert.DoesNotContain("\"in", File.ReadAllText(Path.Combine(_root, "Again", "project.json")), StringComparison.OrdinalIgnoreCase);

        _projects.CreateNew("Other");                                                    // New
        Assert.False(_inOut.Range.IsSet);

        _inOut.SetIn(F(5));
        await _projects.OpenAsync(folder);                                               // Open
        Assert.False(_inOut.Range.IsSet);

        _inOut.SetIn(F(5));
        var recovery = Path.Combine(_root, "recovery.json");                             // what the autosave writes
        File.WriteAllText(recovery, ProjectSerializer.SerializeRecovery(_projects.Current,
            new RecoveryInfo(folder, DateTimeOffset.UtcNow, Environment.ProcessId, null)));
        Assert.DoesNotContain("\"in", File.ReadAllText(recovery), StringComparison.OrdinalIgnoreCase);
        await _projects.RestoreRecoveryAsync(recovery);                                  // Recover
        Assert.False(_inOut.Range.IsSet);
    }

    [Fact]
    public void A_frame_rate_change_keeps_the_points_times_on_the_new_grid()
    {
        Sequence200();
        _inOut.SetIn(F(50));                                                             // 2 s
        _inOut.SetOut(F(99));                                                            // Out at 4 s

        Assert.True(_edit.SetFrameRate(FrameRate.Fps30).Success);

        Assert.Equal(MediaTime.FromFrame(60, FrameRate.Fps30), _inOut.Range.In);
        Assert.Equal(MediaTime.FromFrame(120, FrameRate.Fps30), _inOut.Range.Out);
    }
}
