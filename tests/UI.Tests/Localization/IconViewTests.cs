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

    /// <summary>D040 §5: every command of the timeline's tool bar shows an icon and its localized label, with a tooltip —
    /// only zoom − / + stay glyphs. Read from the AXAML (the timeline view needs a headless window to be built).</summary>
    [Fact]
    public void Every_command_of_the_timeline_tool_bar_has_an_icon_a_label_and_a_tooltip()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "AiVideoEditor.sln"))) root = root.Parent;
        var xaml = System.Xml.Linq.XDocument.Load(Path.Combine(root!.FullName, "src", "UI", "Views", "Panels", "TimelineView.axaml"));
        System.Xml.Linq.XNamespace ui = "https://github.com/avaloniaui";
        var bar = xaml.Descendants(ui + "WrapPanel").First();
        var buttons = bar.Elements().Where(e => e.Name == ui + "Button" || e.Name == ui + "ToggleButton").ToList();

        var withIcons = buttons.Where(b => b.Attribute("Content") is null).ToList();
        Assert.Equal(new[] { "−", "+" }, buttons.Except(withIcons).Select(b => (string)b.Attribute("Content")!));
        Assert.Equal(14, withIcons.Count);                     // + V / A / Text, Split … Link, Snap, Fit
        foreach (var button in withIcons)
        {
            var panel = Assert.Single(button.Elements());
            var parts = panel.Elements().ToList();
            Assert.Equal(2, parts.Count);
            Assert.Equal("IconPath", parts[0].Name.LocalName);
            Assert.StartsWith("{x:Static controls:Icons.", (string?)parts[0].Attribute("Glyph"));
            Assert.Equal("TextBlock", parts[1].Name.LocalName);
            Assert.Matches(@"^\{(x:Static l:Strings\.Timeline_|Binding UnlinkLabel)", (string?)parts[1].Attribute("Text"));
            Assert.StartsWith("{x:Static l:Strings.Timeline_", (string?)button.Attribute("ToolTip.Tip"));
        }
        Assert.Equal(withIcons.Count, withIcons.Select(b => (string?)b.Elements().Single().Elements().First().Attribute("Glyph")).Distinct().Count());
    }
}
