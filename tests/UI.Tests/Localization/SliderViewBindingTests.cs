using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Project;
using AiVideoEditor.Timeline;
using AiVideoEditor.UI.Services;
using AiVideoEditor.UI.ViewModels.Panels;
using AiVideoEditor.UI.Views.Panels;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiVideoEditor.UI.Tests.Localization;

/// <summary>
/// D039 §8: the Opacity slider in the real Inspector view — it shows the field's value, a drag (several values in a row)
/// writes the clip and is one undo step (D017's merge of consecutive changes of one field), the field follows the slider.
/// </summary>
[Collection(AvaloniaControlsCollection.Name)]
public sealed class SliderViewBindingTests
{
    private static readonly FrameRate Rate = FrameRate.Fps25;

    [Fact]
    public void The_opacity_slider_follows_the_field_and_a_drag_is_one_undo_step()
    {
        var undo = new UndoRedoService();
        var projects = new ProjectService(undo, NullLogger<ProjectService>.Instance);
        var edit = new TimelineEditService(projects, undo, NullLogger<TimelineEditService>.Instance);
        var inspector = new InspectorViewModel(edit, new StatusService());
        var clip = new TextClip { Text = "Title", TimelineStart = MediaTime.Zero, Duration = MediaTime.FromFrame(50, Rate) };
        projects.Current.Timeline.VideoTracks[0].Clips.Add(clip);
        inspector.ShowClip(new TimelineClipSelection(clip, "Title", null, Rate, DissolveAtStart: false, DissolveAtEnd: false));

        var view = new InspectorView { DataContext = inspector };
        var slider = view.GetLogicalDescendants().OfType<Slider>().Single(s => s.Name == "OpacitySlider");
        Assert.Equal(100, slider.Value);
        Assert.Equal(0, slider.Minimum);
        Assert.Equal(100, slider.Maximum);
        Assert.False(slider.Focusable);                                          // Space / arrows stay the editor's keys

        foreach (var value in new[] { 90.0, 75.0, 60.0 }) slider.Value = value;   // a drag
        Assert.Equal(0.6, VisualProperties.Of(clip)!.Value.Opacity, 6);
        Assert.Equal(60m, inspector.OpacityPercent);

        undo.Undo();                                                             // the whole drag at once
        Assert.Equal(1.0, VisualProperties.Of(clip)!.Value.Opacity, 6);
        Assert.False(undo.CanUndo);

        inspector.OpacityPercent = 35m;                                          // typing moves the slider
        Assert.Equal(35, slider.Value);
    }
}
