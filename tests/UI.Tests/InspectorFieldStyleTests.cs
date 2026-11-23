using System.Xml.Linq;
using Xunit;

namespace AiVideoEditor.UI.Tests;

/// <summary>
/// FU-25.1 / D079 §2: the numeric fields keep room for their values — the theme stacks small arrows inside the field by
/// styles that carry an activator (Fluent sizes them in theme styles with one, which a plain selector loses to), and every
/// field takes the theme's width for its number. The look itself is checked in the real app (the test cannot lay out the Fluent template).
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

    private static XDocument Theme()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "AiVideoEditor.sln"))) root = root.Parent;
        return XDocument.Load(Path.Combine(root!.FullName, "src", "UI", "Themes", "BoulderTheme.axaml"));
    }

    private static string? Resource(XDocument xaml, string key) =>
        xaml.Descendants().SingleOrDefault(e => (string?)e.Attribute(XNamespace.Get("http://schemas.microsoft.com/winfx/2006/xaml") + "Key") == key)?.Value;

    [Fact]
    public void The_theme_stacks_small_arrows_inside_the_field_and_keeps_the_text_room()
    {
        // D079 §2 (the owner's choice «C»): app-wide in the theme, not per view; FU-25.1's activator still beats Fluent's sizes.
        var theme = Theme();
        var panel = Style(theme, "ButtonSpinner /template/ StackPanel#PART_SpinnerPanel:not(");
        Assert.Equal("Vertical", Setter(panel, "Orientation"));
        Assert.Equal("0", Setter(panel, "Opacity"));                                       // shown on hover / focus
        Assert.Equal("1", Setter(Style(theme, "ButtonSpinner:pointerover /template/ StackPanel"), "Opacity"));
        var buttons = Style(theme, "ButtonSpinner /template/ RepeatButton:not(");
        Assert.Equal("18", Setter(buttons, "Width"));
        Assert.Equal("0", Setter(buttons, "MinWidth"));
        Assert.Equal("0", Setter(buttons, "BorderThickness"));
        var text = Assert.Single(theme.Descendants(Ui + "Style"), x => (string?)x.Attribute("Selector") == "NumericUpDown /template/ TextBox:not(.theme-size)");
        Assert.Equal("0", Setter(text, "MinWidth"));
        Assert.Equal("8", Resource(theme, "ControlCornerRadius"));
        Assert.Equal("60", Resource(theme, "NumericFieldWidth"));
        Assert.Equal("80", Resource(theme, "NumericFieldWideWidth"));
        // The Inspector no longer sizes the spinner itself.
        Assert.DoesNotContain(Inspector().Descendants(Ui + "Style"), s => ((string?)s.Attribute("Selector") ?? "").Contains("RepeatButton"));
    }

    [Fact]
    public void Every_numeric_field_takes_the_themes_width_and_a_fraction_the_wide_one()
    {
        // D079 §2 (the owner): a field as wide as its number — no field sets its own width; a fraction (a format with
        // decimals other than the whole-value «0.####» fields, FU-25.2) or a longer number is `.wide`.
        var fields = Inspector().Descendants().Where(e => e.Name.LocalName == "CommitNumericUpDown").ToList();
        Assert.Equal(62, fields.Count);
        Assert.All(fields, f => Assert.Null(f.Attribute("Width")));
        static bool Wide(XElement f) => ((string?)f.Attribute("Classes") ?? "").Split(' ').Contains("wide");
        foreach (var f in fields)
        {
            var format = (string?)f.Attribute("FormatString") ?? "";
            if (format is "0.##" or "0.#" or "0.###" or "0.00") Assert.True(Wide(f), (string?)f.Attribute("Value"));
            if (format == "0") Assert.False(Wide(f), (string?)f.Attribute("Value"));
        }
        Assert.Equal(26, fields.Count(Wide));
    }

    [Fact]
    public void The_settings_fields_take_the_themes_widths_and_the_canvas_size_fills_its_column()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "AiVideoEditor.sln"))) root = root.Parent;
        XDocument View(string name) => XDocument.Load(Path.Combine(root!.FullName, "src", "UI", "Views", name));
        static string Classes(XElement f) => (string?)f.Attribute("Classes") ?? "";
        static string Bound(XElement f) => ((string)f.Attribute("Value")!).Split(',')[0].Replace("{Binding ", "");

        var settings = View("AppSettingsView.axaml").Descendants().Where(e => e.Name.LocalName == "CommitNumericUpDown").ToList();
        Assert.All(settings, f => Assert.Null(f.Attribute("Width")));
        // D079 §2: a signed number of seconds (±600) or frames is wide; the snapping threshold (0–40 px) is narrow.
        Assert.Equal(new[] { "GarminHeartRateOffsetSeconds", "HeartRateOffsetSeconds", "OtherHeartRateOffsetSeconds", "TrainingClockCorrectionFrames" },
            settings.Where(f => Classes(f).Contains("wide")).Select(Bound).Order().ToArray());
        Assert.Contains(settings, f => Bound(f) == "CanvasSnapPixels" && Classes(f) == "");

        var canvas = View("ProjectSettingsView.axaml").Descendants().Where(e => e.Name.LocalName == "CommitNumericUpDown").ToList();
        Assert.Equal(2, canvas.Count);
        Assert.All(canvas, f => Assert.Equal("fill", Classes(f)));
    }

    /// <summary>FU-25.2: the three whole-value fields step by 1 and show a whole value as whole (a saved fraction as it is);
    /// the sliders under bounded fields land a drag on whole steps. Read from the AXAML: a test view has no styling root, so
    /// the `Slider.under` style is not applied to its sliders (the reason a check of the sliders' properties failed).</summary>
    [Fact]
    public void The_whole_value_fields_and_the_slider_steps()
    {
        var xaml = Inspector();
        var all = xaml.Descendants().Where(e => e.Name.LocalName == "CommitNumericUpDown").ToList();
        // D058 §3: the group's fields (named Group…) step and show alike.
        foreach (var group in all.Where(IsGroupField))
        {
            Assert.Equal("1", (string?)group.Attribute("Increment"));
            Assert.Equal("0.####", (string?)group.Attribute("FormatString"));
        }
        var fields = all.Where(e => !IsGroupField(e))
            .ToDictionary(e => ((string)e.Attribute("Value")!).Split(',')[0].Replace("{Binding ", ""));
        foreach (var name in new[] { "OpacityPercent", "VolumePercent", "StrokeWidth" })
        {
            Assert.Equal("1", (string?)fields[name].Attribute("Increment"));
            Assert.Equal("0.####", (string?)fields[name].Attribute("FormatString"));
        }
        Assert.Equal("0.00", (string?)fields["SpeedValue"].Attribute("FormatString"));   // the others keep their fractions
        Assert.Equal("0.##", (string?)fields["Rotation"].Attribute("FormatString"));
        Assert.Equal("0.##", (string?)fields["ScalePercent"].Attribute("FormatString"));

        var slider = Style(xaml, "Slider.inline");   // D068 amendment 1: on the field's line
        Assert.Equal("True", Setter(slider, "IsSnapToTickEnabled"));
        Assert.Equal("1", Setter(slider, "TickFrequency"));
    }

    /// <summary>D058 §6: the markup is finished only on the Preview's bar; the Inspector shows its state instead.</summary>
    [Fact]
    public void The_markup_is_finished_on_the_preview_bar_only_and_the_inspector_shows_its_state()
    {
        static bool BindsDone(XElement e) => (string?)e.Attribute("Command") is { } command && command.Contains("DoneCommand");
        Assert.DoesNotContain(Inspector().Descendants(), BindsDone);
        Assert.Contains(Inspector().Descendants().Where(e => e.Name.LocalName == "TextBlock"),
            e => (string?)e.Attribute("Text") == "{x:Static l:Strings.Holds_MarkingInPreview}");
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "AiVideoEditor.sln"))) root = root.Parent;
        var preview = XDocument.Load(Path.Combine(root!.FullName, "src", "UI", "Views", "Panels", "PreviewView.axaml"));
        Assert.Single(preview.Descendants().Where(e => e.Name.LocalName == "Button"), BindsDone);
    }

    private static bool IsGroupField(System.Xml.Linq.XElement e) =>
        e.Attributes().FirstOrDefault(a => a.Name.LocalName == "Name")?.Value.StartsWith("Group", StringComparison.Ordinal) == true;
}
