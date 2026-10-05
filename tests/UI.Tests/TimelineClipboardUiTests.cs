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
/// Phase 12 Step 12.6 (D027 §5): Copy, Paste and Duplicate in the timeline panel. Copy needs a selection and is allowed
/// during an export (it changes nothing); Paste needs something copied in this project and puts it at the playhead;
/// Duplicate puts copies right after the selection; the pasted / duplicated clips become the selection; the service's
/// message is shown when it refuses; the clipboard is emptied when another project becomes current.
/// </summary>
public sealed class TimelineClipboardUiTests
{
    private static readonly FrameRate Rate = FrameRate.Default;

    private readonly UndoRedoService _undo = new();
    private readonly ProjectService _projects;
    private readonly TimelineEditService _edit;
    private readonly StatusService _status = new();
    private readonly EditingLock _lock = new();

    public TimelineClipboardUiTests()
    {
        _projects = new ProjectService(_undo, NullLogger<ProjectService>.Instance);
        _edit = new TimelineEditService(_projects, _undo, NullLogger<TimelineEditService>.Instance);
    }

    private TimelineViewModel Timeline() => new(_projects, _edit, _status, NullLogger<TimelineViewModel>.Instance, _lock);

    private Track V1 => _projects.Current.Timeline.VideoTracks[0];
    private static MediaTime F(long n) => MediaTime.FromFrame(n, Rate);
    private static long Start(Clip c) => c.TimelineStart.ToNearestFrame(Rate);

    private TextClip Text(long start, long end)
    {
        var clip = new TextClip { Text = "T", TimelineStart = F(start), Duration = F(end) - F(start) };
        V1.Clips.Add(clip);
        return clip;
    }

    private static void Select(TimelineViewModel timeline, params Clip[] clips)
    {
        foreach (var clip in clips)
            timeline.OnClipPressed(timeline.Tracks.SelectMany(t => t.Clips).Single(c => c.Id == clip.Id), toggle: true);
    }

    private static List<Guid> Selected(TimelineViewModel timeline) =>
        timeline.Tracks.SelectMany(t => t.Clips).Where(c => c.IsSelected).Select(c => c.Id).ToList();

    [Fact]
    public void Copy_needs_a_selection_paste_needs_a_copy_and_only_copy_runs_during_an_export()
    {
        var a = Text(0, 10);
        var timeline = Timeline();
        Assert.False(timeline.CopyCommand.CanExecute(null));
        Assert.False(timeline.PasteCommand.CanExecute(null));
        Assert.False(timeline.DuplicateCommand.CanExecute(null));

        Select(timeline, a);
        timeline.CopyCommand.Execute(null);
        Assert.Equal("1 clip copied", _status.Message);
        Assert.True(timeline.PasteCommand.CanExecute(null));
        Assert.True(timeline.DuplicateCommand.CanExecute(null));

        using (_lock.Acquire())
        {
            Assert.True(timeline.CopyCommand.CanExecute(null));
            Assert.False(timeline.PasteCommand.CanExecute(null));
            Assert.False(timeline.DuplicateCommand.CanExecute(null));
        }
        Assert.True(timeline.PasteCommand.CanExecute(null));
    }

    [Fact]
    public void Paste_puts_the_copy_at_the_playhead_and_selects_it()
    {
        var a = Text(0, 10);
        var b = Text(10, 15);
        var timeline = Timeline();
        Select(timeline, a, b);
        timeline.CopyCommand.Execute(null);
        Assert.Equal("2 clips copied", _status.Message);
        _projects.Current.Timeline.PlayheadPosition = F(40);

        timeline.PasteCommand.Execute(null);

        Assert.Equal(4, V1.Clips.Count);
        Assert.Equal(new[] { 40L, 50L }, V1.Clips.Skip(2).Select(Start));
        Assert.Equal(V1.Clips.Skip(2).Select(c => c.Id).ToHashSet(), Selected(timeline).ToHashSet());
        Assert.Equal("2 clips pasted", _status.Message);

        timeline.PasteCommand.Execute(null);                              // the same place again: it would overlap
        Assert.StartsWith("Can't paste:", _status.Message);
        Assert.Equal(4, V1.Clips.Count);
    }

    [Fact]
    public void Duplicate_puts_copies_after_the_selection_and_selects_them()
    {
        var a = Text(0, 10);
        var timeline = Timeline();
        Select(timeline, a);

        timeline.DuplicateCommand.Execute(null);

        var copy = Assert.Single(V1.Clips, c => c != a);
        Assert.Equal(10L, Start(copy));
        Assert.Equal(new[] { copy.Id }, Selected(timeline));
        Assert.Equal("1 clip duplicated", _status.Message);

        _undo.Undo();
        Assert.Equal(new Clip[] { a }, V1.Clips);
    }

    [Fact]
    public void A_refused_duplicate_shows_the_service_message_and_keeps_the_selection()
    {
        var a = Text(0, 10);
        V1.IsLocked = true;
        var timeline = Timeline();
        Select(timeline, a);

        timeline.DuplicateCommand.Execute(null);

        Assert.Equal("Track V1 is locked.", _status.Message);
        Assert.Equal(new[] { a.Id }, Selected(timeline));
    }

    [Fact]
    public void Another_project_empties_the_clipboard()
    {
        var a = Text(0, 10);
        var timeline = Timeline();
        Select(timeline, a);
        timeline.CopyCommand.Execute(null);
        Assert.NotNull(timeline.Clipboard);

        _projects.CreateNew("Other");

        Assert.Null(timeline.Clipboard);
        Assert.False(timeline.PasteCommand.CanExecute(null));
    }
}
