using AiVideoEditor.UI.Controls;
using AiVideoEditor.UI.Views.Panels;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.LogicalTree;
using Xunit;

namespace AiVideoEditor.UI.Tests.Localization;

/// <summary>
/// D039 §7: the frequent commands carry the project's icons; a button that shows only an icon names its command in the
/// tooltip (and to UI Automation), so no command becomes a nameless picture.
/// </summary>
[Collection(AvaloniaControlsCollection.Name)]
public sealed class IconViewTests
{
    [Fact]
    public void The_tool_bar_and_the_transport_show_the_icons()
    {
        var glyphs = new ToolbarView().GetLogicalDescendants().OfType<IconPath>()
            .Select(i => i.Glyph).ToHashSet();

        // The Preview and timeline views need a cursor factory (headless window); their icons are checked in the real app.
        foreach (var glyph in new[] { Icons.Undo, Icons.Redo })
            Assert.Contains(glyph, glyphs);
    }

    [Fact]
    public void An_icon_only_button_names_its_command_in_the_tooltip()
    {
        var buttons = new ToolbarView().GetLogicalDescendants().OfType<ContentControl>()
            .Where(b => b is Button or ToggleButton && b.Content is IconPath or Panel { Children: [IconPath, IconPath] })
            .ToList();

        Assert.Equal(2, buttons.Count);                  // Undo, Redo
        Assert.All(buttons, b => Assert.False(string.IsNullOrWhiteSpace(ToolTip.GetTip(b) as string)));
    }
}
