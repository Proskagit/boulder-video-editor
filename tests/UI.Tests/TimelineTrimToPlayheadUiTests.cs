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
/// Phase 15 Step 15.4 (D030 §5): the timeline view model's trim-to-playhead commands (Q / W, Shift+Q / Shift+W through
/// <c>ShortcutRouter</c>). They work on the selection and the playhead, report the edit service's message, move the
/// playhead to the clip's start after a ripple trim of the start (Q5, a seek), keep the selection, and are disabled while
/// an export runs.
/// </summary>
public sealed class TimelineTrimToPlayheadUiTests
{
    private static readonly FrameRate Rate = FrameRate.Default;

    private readonly UndoRedoService _undo = new();
    private readonly ProjectService _projects;
    private readonly TimelineEditService _edit;
    private readonly StatusService _status = new();
    private readonly EditingLock _lock = new();

    public TimelineTrimToPlayheadUiTests()
    {
        _projects = new ProjectService(_undo, NullLogger<ProjectService>.Instance);
        _edit = new TimelineEditService(_projects, _undo, NullLogger<TimelineEditService>.Instance);
    }

    private TimelineViewModel Timeline() => new(_projects, _edit, _status, NullLogger<TimelineViewModel>.Instance, _lock);

    private Track V1 => _projects.Current.Timeline.VideoTracks[0];
    private static MediaTime F(long n) => MediaTime.FromFrame(n, Rate);
    private static (long, long) Frames(Clip c) => (c.TimelineStart.ToNearestFrame(Rate), c.TimelineEnd.ToNearestFrame(Rate));

    private static TextClip Text(Track track, long start, long end)
    {
        var clip = new TextClip { Text = "T", TimelineStart = F(start), Duration = F(end) - F(start) };
        track.Clips.Add(clip);
        return clip;
    }

    private static void Select(TimelineViewModel timeline, params Clip[] clips)
    {
        foreach (var clip in clips)
            timeline.OnClipPressed(timeline.Tracks.SelectMany(t => t.Clips).Single(c => c.Id == clip.Id), toggle: true);
    }

    [Fact]
    public void Q_and_W_trim_the_selected_clip_to_the_playhead_and_keep_the_selection()
    {
        var a = Text(V1, 0, 100);
        var b = Text(V1, 100, 200);
        var timeline = Timeline();
        Select(timeline, a);
        timeline.SetPlayhead(F(30));

        timeline.TrimStartToPlayheadCommand.Execute(null);
        Assert.Equal((30L, 100L), Frames(a));
        Assert.Equal("Trimmed the start of the clip to the playhead", _status.Message);
        Assert.Equal(F(30), timeline.Playhead);

        timeline.SetPlayhead(F(80));
        timeline.TrimEndToPlayheadCommand.Execute(null);
        Assert.Equal((30L, 80L), Frames(a));
        Assert.Equal((100L, 200L), Frames(b));                                          // plain: no ripple
        Assert.Equal("Trimmed the end of the clip to the playhead", _status.Message);
        Assert.True(timeline.Tracks.SelectMany(t => t.Clips).Single(c => c.Id == a.Id).IsSelected);
    }

    [Fact]
    public void Shift_Q_ripples_and_moves_the_playhead_to_the_clip_start_with_a_seek_Shift_W_leaves_it()
    {
        var a = Text(V1, 20, 120);
        var b = Text(V1, 120, 220);
        var timeline = Timeline();
        Select(timeline, a);
        timeline.SetPlayhead(F(70));
        var seeks = new List<MediaTime>();
        timeline.SeekRequested += (_, at) => seeks.Add(at);

        timeline.RippleTrimStartToPlayheadCommand.Execute(null);

        Assert.Equal((20L, 70L), Frames(a));
        Assert.Equal((70L, 170L), Frames(b));
        Assert.Equal(F(20), timeline.Playhead);                                           // Q5
        Assert.Equal(new[] { F(20) }, seeks);
        Assert.Equal("Ripple trimmed the start of the clip to the playhead", _status.Message);

        timeline.SetPlayhead(F(40));
        seeks.Clear();
        timeline.RippleTrimEndToPlayheadCommand.Execute(null);
        Assert.Equal((20L, 40L), Frames(a));
        Assert.Equal((40L, 140L), Frames(b));
        Assert.Equal(F(40), timeline.Playhead);
        Assert.Empty(seeks);

        _undo.Undo();
        _undo.Undo();
        Assert.Equal(((20L, 120L), (120L, 220L)), (Frames(a), Frames(b)));
    }

    [Fact]
    public void Without_a_selected_clip_under_the_playhead_nothing_changes_and_the_status_says_why()
    {
        var a = Text(V1, 0, 100);
        var timeline = Timeline();
        timeline.SetPlayhead(F(50));

        timeline.TrimStartToPlayheadCommand.Execute(null);                              // nothing selected
        Assert.Equal("Select the clip to trim: the playhead must be inside it.", _status.Message);

        Select(timeline, a);
        timeline.SetPlayhead(F(100));
        timeline.RippleTrimEndToPlayheadCommand.Execute(null);                          // the playhead at its end
        Assert.Equal("The playhead is not inside the selected clip(s).", _status.Message);

        Assert.Equal((0L, 100L), Frames(a));
        Assert.False(_undo.CanUndo);
        Assert.False(_projects.Current.IsDirty);
    }

    [Fact]
    public void The_trim_commands_are_disabled_while_an_export_runs()
    {
        var timeline = Timeline();
        var commands = new[]
        {
            timeline.TrimStartToPlayheadCommand, timeline.TrimEndToPlayheadCommand,
            timeline.RippleTrimStartToPlayheadCommand, timeline.RippleTrimEndToPlayheadCommand
        };
        Assert.All(commands, c => Assert.True(c.CanExecute(null)));
        using (_lock.Acquire())
            Assert.All(commands, c => Assert.False(c.CanExecute(null)));
        Assert.All(commands, c => Assert.True(c.CanExecute(null)));
    }
}
