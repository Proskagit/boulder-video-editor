using AiVideoEditor.UI.Views.Panels;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Xunit;

namespace AiVideoEditor.UI.Tests;

/// <summary>
/// D079 amendment 1 (the owner's screenshots of 1.13.0): blank gaps between the Inspector's sections — a section's anchor
/// (<c>Panel.category</c>) stayed visible around a hidden section, empty but still taking the list's 16 px spacing.
/// </summary>
[Collection(AvaloniaControlsCollection.Name)]
public sealed class InspectorCategoryGapTests
{
    [Fact]
    public void Every_category_panel_of_the_inspector_is_shown_only_while_its_section_is()
    {
        var view = new InspectorView();
        var panels = view.GetLogicalDescendants().OfType<Panel>().Where(p => p.Classes.Contains("category")).ToList();
        Assert.True(panels.Count >= 16, $"{panels.Count} category panels");
        foreach (var panel in panels)
        {
            var sections = panel.Children.ToList();
            Assert.Equal(sections.Any(s => s.IsVisible), panel.IsVisible);
            foreach (var s in sections) s.IsVisible = false;
            Assert.False(panel.IsVisible);                    // no empty panel to take the spacing
            sections[0].IsVisible = true;
            Assert.True(panel.IsVisible);                     // and back when its section shows again
        }
    }

    [Fact]
    public void A_hidden_section_leaves_no_spacing_in_its_list()
    {
        var list = new StackPanel { Spacing = 16 };
        var hidden = new Border { Height = 30, IsVisible = false };
        var panel = new Panel { Classes = { "category" }, Children = { hidden } };
        list.Children.Add(new Border { Height = 30 });
        list.Children.Add(panel);
        list.Children.Add(new Border { Height = 30 });
        var host = new UserControl { Content = list };
        InspectorView.HideEmptyCategoryPanels(host);
        list.Measure(new Avalonia.Size(200, 1000));
        Assert.Equal(30 + 16 + 30, list.DesiredSize.Height);  // one spacing between the two shown items, none for the hidden one
    }
}
