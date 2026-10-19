using System.Xml.Linq;
using Xunit;

namespace AiVideoEditor.UI.Tests;

/// <summary>
/// FU-25.1: the Inspector's numeric fields keep room for their values — the spinner buttons are narrowed by a style that
/// carries an activator (Fluent sizes them in theme styles with one, which a plain selector loses to), and every field
/// keeps the one compact width. The look itself is checked in the real app (the test cannot lay out the Fluent template).
/// </summary>
public sealed class InspectorFieldStyleTests
{
    private static readonly XNamespace Ui = "https://github.com/avaloniaui";

    private static XDocument Inspector()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "AiVideoEditor.sln"))) root = root.Parent;
        return XDocument.Load(Path.Combine(root!.FullName, "src", "UI", "Views", "Panels", "InspectorView.axaml"));
    }

    private static XElement Style(XDocument xaml, string selectorStart) =>
        Assert.Single(xaml.Descendants(Ui + "Style"), s => ((string?)s.Attribute("Selector") ?? "").StartsWith(selectorStart, StringComparison.Ordinal));

    private static string? Setter(XElement style, string property) =>
        (string?)style.Elements(Ui + "Setter").SingleOrDefault(s => (string?)s.Attribute("Property") == property)?.Attribute("Value");

    [Fact]
    public void The_spinner_buttons_are_narrow_and_the_text_keeps_its_room()
    {
        var xaml = Inspector();

        var buttons = Style(xaml, "NumericUpDown /template/ ButtonSpinner /template/ RepeatButton");
        Assert.Contains(":not(", (string)buttons.Attribute("Selector")!);   // the activator that wins over Fluent's sizes
        Assert.Equal("22", Setter(buttons, "Width"));
        Assert.Equal("0", Setter(buttons, "MinWidth"));

        var text = Style(xaml, "NumericUpDown /template/ TextBox");
        Assert.Contains(":not(", (string)text.Attribute("Selector")!);
        Assert.Equal("0", Setter(text, "MinWidth"));
    }

    [Fact]
    public void Every_numeric_field_of_the_Inspector_keeps_the_compact_width()
    {
        var fields = Inspector().Descendants().Where(e => e.Name.LocalName == "CommitNumericUpDown").ToList();

        Assert.Equal(21, fields.Count);
        Assert.All(fields, f => Assert.Equal("104", (string?)f.Attribute("Width")));
    }

    /// <summary>FU-25.2: the three whole-value fields step by 1 and show a whole value as whole (a saved fraction as it is);
    /// the sliders under bounded fields land a drag on whole steps. Read from the AXAML: a test view has no styling root, so
    /// the `Slider.under` style is not applied to its sliders (the reason a check of the sliders' properties failed).</summary>
    [Fact]
    public void The_whole_value_fields_and_the_slider_steps()
    {
        var xaml = Inspector();
        var fields = xaml.Descendants().Where(e => e.Name.LocalName == "CommitNumericUpDown")
            .ToDictionary(e => ((string)e.Attribute("Value")!).Split(',')[0].Replace("{Binding ", ""));
        foreach (var name in new[] { "OpacityPercent", "VolumePercent", "StrokeWidth" })
        {
            Assert.Equal("1", (string?)fields[name].Attribute("Increment"));
            Assert.Equal("0.####", (string?)fields[name].Attribute("FormatString"));
        }
        Assert.Equal("0.00", (string?)fields["SpeedValue"].Attribute("FormatString"));   // the others keep their fractions
        Assert.Equal("0.##", (string?)fields["Rotation"].Attribute("FormatString"));
        Assert.Equal("0.##", (string?)fields["ScalePercent"].Attribute("FormatString"));

        var slider = Style(xaml, "Slider.under");
        Assert.Equal("True", Setter(slider, "IsSnapToTickEnabled"));
        Assert.Equal("1", Setter(slider, "TickFrequency"));
    }
}
