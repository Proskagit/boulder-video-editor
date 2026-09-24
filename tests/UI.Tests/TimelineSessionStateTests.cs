using AiVideoEditor.Core.Common;
using AiVideoEditor.Project;
using AiVideoEditor.Timeline;
using AiVideoEditor.UI.Services;
using AiVideoEditor.UI.ViewModels.Panels;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiVideoEditor.UI.Tests;

/// <summary>
/// Playhead, zoom and snapping are session state of the current sequence (D015): they follow the
/// project that is current, are saved with it, and never make it dirty or enter undo history.
/// </summary>
public sealed class TimelineSessionStateTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "AiVideoEditorTests", Guid.NewGuid().ToString("N"));
    private readonly UndoRedoService _undo = new();
    private readonly ProjectService _projects;
    private readonly TimelineViewModel _timeline;

    public TimelineSessionStateTests()
    {
        Directory.CreateDirectory(_root);
        _projects = new ProjectService(_undo, NullLogger<ProjectService>.Instance);
        var edit = new TimelineEditService(_projects, _undo, NullLogger<TimelineEditService>.Instance);
        _timeline = new TimelineViewModel(_projects, edit, new StatusService(), NullLogger<TimelineViewModel>.Instance);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void NewProject_TakesZoomAndSnappingFromItsSequence()
    {
        _timeline.ZoomInCommand.Execute(null);
        _timeline.ToggleSnappingCommand.Execute(null);
        Assert.False(_timeline.SnappingEnabled);

        var fresh = _projects.CreateNew("Fresh");

        Assert.Equal(fresh.Timeline.ZoomPixelsPerSecond, _timeline.PixelsPerSecond);
        Assert.True(_timeline.SnappingEnabled);
        Assert.True(fresh.Timeline.SnappingEnabled); // not overwritten by the previous project's toggle
    }

    [Fact]
    public async Task Open_RestoresSavedZoomAndSnapping()
    {
        _timeline.ZoomInCommand.Execute(null);
        _timeline.ZoomInCommand.Execute(null);
        _timeline.ToggleSnappingCommand.Execute(null);
        var savedZoom = _timeline.PixelsPerSecond;
        var folder = Path.Combine(_root, "Saved");
        await _projects.SaveAsAsync(folder);

        var other = _projects.CreateNew("Other");
        Assert.NotEqual(savedZoom, _timeline.PixelsPerSecond);
        Assert.True(_timeline.SnappingEnabled);

        var opened = await _projects.OpenAsync(folder);

        Assert.Equal(savedZoom, _timeline.PixelsPerSecond);
        Assert.False(_timeline.SnappingEnabled);
        Assert.True(other.Timeline.SnappingEnabled);

        // Later changes go to the opened sequence.
        _timeline.ToggleSnappingCommand.Execute(null);
        Assert.True(opened.Timeline.SnappingEnabled);
        _timeline.ZoomOutCommand.Execute(null);
        Assert.Equal(_timeline.PixelsPerSecond, opened.Timeline.ZoomPixelsPerSecond);
    }

    [Fact]
    public async Task PlayheadZoomAndSnapping_DoNotMakeTheProjectDirty_NorEnterUndoHistory()
    {
        await _projects.SaveAsAsync(Path.Combine(_root, "Clean"));
        Assert.False(_projects.Current.IsDirty);

        _timeline.SetPlayhead(MediaTime.FromSeconds(3));
        _timeline.ZoomInCommand.Execute(null);
        _timeline.ToggleSnappingCommand.Execute(null);

        Assert.Equal(MediaTime.FromSeconds(3), _projects.Current.Timeline.PlayheadPosition);
        Assert.False(_projects.Current.Timeline.SnappingEnabled);
        Assert.False(_projects.Current.IsDirty);
        Assert.False(_undo.CanUndo);
    }
}
