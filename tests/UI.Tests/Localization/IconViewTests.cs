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

    /// <summary>D040 §5, D068 item 3: every command of the timeline's tool bar shows an icon and has a tooltip. Adding content and
    /// the panels (Text, Template, Keyframe, Mixer) keep their labels; the frequent edits are icon buttons that name their command
    /// to UI Automation; only zoom − / + stay glyphs. Read from the AXAML (the timeline view needs a headless window to be built).</summary>
    [Fact]
    public void Every_command_of_the_timeline_tool_bar_has_an_icon_and_a_tooltip()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "AiVideoEditor.sln"))) root = root.Parent;
        var xaml = System.Xml.Linq.XDocument.Load(Path.Combine(root!.FullName, "src", "UI", "Views", "Panels", "TimelineView.axaml"));
        System.Xml.Linq.XNamespace ui = "https://github.com/avaloniaui";
        var bar = xaml.Descendants(ui + "WrapPanel").First();
        var buttons = bar.Elements().Where(e => e.Name == ui + "Button" || e.Name == ui + "ToggleButton").ToList();
        Assert.All(buttons, b => Assert.StartsWith("{x:Static l:Strings.Timeline_", (string?)b.Attribute("ToolTip.Tip")));

        var glyphs = buttons.Where(b => b.Attribute("Content") is not null).ToList();
        Assert.Equal(new[] { "−", "+" }, glyphs.Select(b => (string)b.Attribute("Content")!));

        var labelled = buttons.Where(b => b.Elements().SingleOrDefault()?.Name == ui + "StackPanel").ToList();
        Assert.Equal(new[] { "Timeline_AddText", "Timeline_Template", "Timeline_Keyframe", "Timeline_Mixer", "Timeline_Transition" },   // D069 am. 1
            labelled.Select(b => ((string)b.Elements().Single().Elements().Last().Attribute("Text")!).Replace("{x:Static l:Strings.", "").TrimEnd('}')));

        var icons = buttons.Except(glyphs).Except(labelled).ToList();
        Assert.Equal(9, icons.Count);                   // + V / A, Split, Delete, Ripple delete, Duplicate, the magnet, D048's Debug switch, Fit (D069 am. 1: Transition labelled)
        Assert.All(icons, b =>
        {
            Assert.Equal("IconPath", b.Elements().Single().Name.LocalName);
            Assert.StartsWith("{x:Static l:Strings.Timeline_", (string?)b.Attribute("AutomationProperties.Name"));
        });
        var all = buttons.Except(glyphs).Select(b => (string?)b.Descendants().First(d => d.Name.LocalName == "IconPath").Attribute("Glyph")).ToList();
        Assert.Equal(all.Count, all.Distinct().Count());
    }
}
