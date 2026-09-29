using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Project;
using AiVideoEditor.Timeline;
using AiVideoEditor.UI.Services;
using AiVideoEditor.UI.ViewModels.Panels;
using AiVideoEditor.UI.Views.Panels;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiVideoEditor.UI.Tests;

/// <summary>
/// Phase 10 Step 10.5: the Inspector's fade fields are bound in the real view (its XAML, reflection bindings): the two
/// NumericUpDowns show the view model's frames and write edits back; the inactive notes follow PO-8.
/// </summary>
[Collection(AvaloniaControlsCollection.Name)]
public sealed class FadeViewBindingTests
{
    private static readonly FrameRate Rate = FrameRate.Fps25;
    private static MediaTime F(long n) => MediaTime.FromFrame(n, Rate);

    [Fact]
    public void The_inspector_view_binds_the_fade_fields_and_the_inactive_notes()
    {
        var undo = new UndoRedoService();
        var projects = new ProjectService(undo, NullLogger<ProjectService>.Instance);
        var edit = new TimelineEditService(projects, undo, NullLogger<TimelineEditService>.Instance);
        var inspector = new InspectorViewModel(edit, new StatusService());

        var clip = new TextClip { Text = "Title", TimelineStart = F(0), Duration = F(50), FadeIn = F(12), FadeOut = F(7) };
        projects.Current.Timeline.VideoTracks[0].Clips.Add(clip);
        inspector.ShowClip(new TimelineClipSelection(clip, "Title", null, Rate, DissolveAtStart: false, DissolveAtEnd: true));

        var view = new InspectorView { DataContext = inspector };
        var fields = view.GetLogicalDescendants().OfType<NumericUpDown>()
            .Where(n => n.Maximum == 50m && n.FormatString == "0").ToList();
        Assert.Equal(2, fields.Count);                                           // Fade In, Fade Out
        Assert.Equal(new decimal?[] { 12m, 7m }, fields.Select(f => f.Value));

        var notes = view.GetLogicalDescendants().OfType<TextBlock>().Where(t => t.Text == "Not applied: a dissolve is on this edge.").ToList();
        Assert.Equal(new[] { false, true }, notes.Select(t => t.IsVisible));   // fade out: a dissolve on the end

        fields[0].Value = 20m;                                                   // typing in the view edits the clip
        Assert.Equal(F(20), clip.FadeIn);
    }
}
