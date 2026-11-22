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
        _projects = new ProjectService(_undo, NullLogger<ProjectService>.Instance, applicationDefaultsInNewProjects: true);
        var edit = new TimelineEditService(_projects, _undo, NullLogger<TimelineEditService>.Instance);
        _timeline = new TimelineViewModel(_projects, edit, new StatusService(), NullLogger<TimelineViewModel>.Instance);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    /// <summary>D069 §6: the timeline always snaps; the former «Привязка» (N) is the main track's magnet — on in a new
    /// project, a change of the project with its Undo step, kept by Save / Open.</summary>
    [Fact]
    public void NewProject_TakesZoomFromItsSequence_SnapsAlways_AndHasTheMagnetOn()
    {
        _timeline.ZoomInCommand.Execute(null);
        _timeline.ToggleMagnetCommand.Execute(null);
        Assert.False(_timeline.MagneticMainTrack);

        var fresh = _projects.CreateNew("Fresh");

        Assert.Equal(fresh.Timeline.ZoomPixelsPerSecond, _timeline.PixelsPerSecond);
        Assert.True(_timeline.SnappingEnabled);
        Assert.True(_timeline.MagneticMainTrack);
        Assert.True(fresh.Timeline.MagneticMainTrack);
    }

    [Fact]
    public async Task Open_RestoresSavedZoomAndMagnet()
    {
        _timeline.ZoomInCommand.Execute(null);
        _timeline.ZoomInCommand.Execute(null);
        _timeline.ToggleMagnetCommand.Execute(null);
        var savedZoom = _timeline.PixelsPerSecond;
        var folder = Path.Combine(_root, "Saved");
        await _projects.SaveAsAsync(folder);

        _projects.CreateNew("Other");
        Assert.NotEqual(savedZoom, _timeline.PixelsPerSecond);
        Assert.True(_timeline.MagneticMainTrack);

        var opened = await _projects.OpenAsync(folder);

        Assert.Equal(savedZoom, _timeline.PixelsPerSecond);
        Assert.False(_timeline.MagneticMainTrack);
        Assert.True(_timeline.SnappingEnabled);

        _timeline.ToggleMagnetCommand.Execute(null);
        Assert.True(opened.Timeline.MagneticMainTrack);
        _timeline.ZoomOutCommand.Execute(null);
        Assert.Equal(_timeline.PixelsPerSecond, opened.Timeline.ZoomPixelsPerSecond);
    }

    [Fact]
    public async Task PlayheadAndZoom_DoNotMakeTheProjectDirty_TheMagnetIsAnUndoStep()
    {
        await _projects.SaveAsAsync(Path.Combine(_root, "Clean"));
        Assert.False(_projects.Current.IsDirty);

        _timeline.SetPlayhead(MediaTime.FromSeconds(3));
        _timeline.ZoomInCommand.Execute(null);
        Assert.Equal(MediaTime.FromSeconds(3), _projects.Current.Timeline.PlayheadPosition);
        Assert.False(_projects.Current.IsDirty);
        Assert.False(_undo.CanUndo);

        _timeline.ToggleMagnetCommand.Execute(null);
        Assert.False(_projects.Current.Timeline.MagneticMainTrack);
        Assert.True(_undo.CanUndo);
        _undo.Undo();
        Assert.True(_projects.Current.Timeline.MagneticMainTrack);
        Assert.True(_timeline.MagneticMainTrack);
    }
}
