using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Project;
using AiVideoEditor.Project.Persistence;
using AiVideoEditor.Timeline;
using AiVideoEditor.UI.Services;
using AiVideoEditor.UI.ViewModels.Panels;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiVideoEditor.UI.Tests;

/// <summary>
/// D058 §4 (Phase 39): a marker's label (the existing <see cref="Marker.Label"/>) shown beside its flag and edited in place
/// by a double click on the flag — Enter sets it as one undo step, Esc keeps it; trimmed and bounded; saved with the
/// project and read back.
/// </summary>
public sealed class MarkerLabelTests
{
    private readonly UndoRedoService _undo = new();
    private readonly ProjectService _projects;
    private readonly TimelineEditService _edit;
    private readonly TimelineViewModel _timeline;

    public MarkerLabelTests()
    {
        _projects = new ProjectService(_undo, NullLogger<ProjectService>.Instance);
        _edit = new TimelineEditService(_projects, _undo, NullLogger<TimelineEditService>.Instance);
        _timeline = new TimelineViewModel(_projects, _edit, new StatusService(), NullLogger<TimelineViewModel>.Instance);
        _projects.Current.Timeline.ZoomPixelsPerSecond = 30;                 // a set zoom (no initial zoom here)
        _timeline.SetViewport(0, 1200);
    }

    private Marker AddMarker(double seconds)
    {
        var result = _edit.AddMarker(MediaTime.FromSeconds(seconds));
        Assert.True(result.Success, result.Message);
        return _projects.Current.Timeline.Markers.Single(m => m.Id == result.MarkerId);
    }

    [Fact]
    public void A_label_is_set_in_one_undo_step_trimmed_and_bounded_and_shown_on_the_flag()
    {
        var marker = AddMarker(10);
        Assert.True(_edit.SetMarkerLabel(marker.Id, "  Трасса 7  ").Success);
        Assert.Equal("Трасса 7", marker.Label);
        var flag = Assert.Single(_timeline.Markers);
        Assert.True(flag.HasLabel);
        Assert.Equal("Трасса 7", flag.Label);

        Assert.True(_edit.SetMarkerLabel(marker.Id, "Трасса 7").NoChange);
        Assert.Equal(ITimelineEditService.MaxMarkerLabelLength,
            (_edit.SetMarkerLabel(marker.Id, new string('x', 200)) is { Success: true } ? marker.Label : "").Length);
        _undo.Undo();
        Assert.Equal("Трасса 7", marker.Label);
        _undo.Undo();
        Assert.Equal("", marker.Label);
        Assert.False(Assert.Single(_timeline.Markers).HasLabel);
        _undo.Redo();
        Assert.Equal("Трасса 7", marker.Label);
    }

    [Fact]
    public void A_double_click_on_a_flag_opens_its_label_enter_sets_it_and_esc_keeps_it()
    {
        var marker = AddMarker(10);
        var x = _timeline.Markers.Single().Left;
        Assert.False(_timeline.BeginMarkerLabelEdit(x + 40));               // away from the flag: no edit
        Assert.True(_timeline.BeginMarkerLabelEdit(x + 3));                 // near it
        Assert.True(_timeline.IsEditingMarkerLabel);
        Assert.Equal(x, _timeline.MarkerLabelLeft);

        _timeline.MarkerLabelText = "Старт";
        _timeline.CommitMarkerLabelCommand.Execute(null);
        Assert.False(_timeline.IsEditingMarkerLabel);
        Assert.Equal("Старт", marker.Label);

        Assert.True(_timeline.BeginMarkerLabelEdit(x));
        Assert.Equal("Старт", _timeline.MarkerLabelText);
        _timeline.MarkerLabelText = "другое";
        _timeline.CancelMarkerLabelCommand.Execute(null);
        Assert.Equal("Старт", marker.Label);
        _timeline.CommitMarkerLabelCommand.Execute(null);                   // the field's late LostFocus does nothing then
        Assert.Equal("Старт", marker.Label);
    }

    [Fact]
    public void A_label_is_saved_with_the_project_and_read_back()
    {
        var marker = AddMarker(10);
        Assert.True(_edit.SetMarkerLabel(marker.Id, "Финал").Success);
        var folder = Path.Combine(Path.GetTempPath(), "aive-marker-label");
        var json = ProjectSerializer.Serialize(_projects.Current, folder);
        var read = ProjectSerializer.Deserialize(json, folder);
        Assert.Equal("Финал", Assert.Single(read.Timeline.Markers).Label);
    }
}
