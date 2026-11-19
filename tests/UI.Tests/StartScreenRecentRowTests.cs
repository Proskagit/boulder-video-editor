using System.Xml.Linq;
using Xunit;

namespace AiVideoEditor.UI.Tests;

/// <summary>
/// D070 §1: a click on a recent project disables every row for the open (one action at a time); a disabled row must keep
/// its transparent background, or Fluent paints every row grey and the click seems to select all projects. Read from the
/// AXAML (a test view has no Fluent template to lay out); the look itself is checked in the real app.
/// </summary>
public sealed class StartScreenRecentRowTests
{
    private static readonly XNamespace Ui = "https://github.com/avaloniaui";

    private static XDocument StartScreen()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "AiVideoEditor.sln"))) root = root.Parent;
        return XDocument.Load(Path.Combine(root!.FullName, "src", "UI", "Views", "StartScreenView.axaml"));
    }

    [Fact]
    public void A_disabled_recent_row_keeps_its_transparent_background()
    {
        var xaml = StartScreen();

        var row = Assert.Single(xaml.Descendants(Ui + "DataTemplate").Descendants(Ui + "Button"));   // the recent project's row
        Assert.Equal("recentRow", (string?)row.Attribute("Classes"));
        Assert.Equal("{Binding OpenCommand}", (string?)row.Attribute("Command"));
        Assert.Equal("Transparent", (string?)row.Attribute("Background"));

        var style = Assert.Single(xaml.Descendants(Ui + "Style"),
            s => (string?)s.Attribute("Selector") == "Button.recentRow:disabled /template/ ContentPresenter#PART_ContentPresenter");
        var background = Assert.Single(style.Elements(Ui + "Setter"), s => (string?)s.Attribute("Property") == "Background");
        Assert.Equal("Transparent", (string?)background.Attribute("Value"));
    }
}
