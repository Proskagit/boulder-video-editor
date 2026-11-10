using System.Xml.Linq;
using AiVideoEditor.UI.Controls;
using Avalonia.Input;
using Xunit;

namespace AiVideoEditor.UI.Tests;

/// <summary>
/// D058 §5 (Phase 39): Scale, Rotation and Position change by dragging their labels — whole pixels × the step (Shift ×10,
/// Ctrl ×0.1), two decimals, within the field's limits; the fields stay for exact input; the percent sliders land on 5 %
/// steps, the others keep theirs.
/// </summary>
public sealed class ScrubLabelTests
{
    [Theory]
    [InlineData(10, 25.0, 1, KeyModifiers.None, 35)]
    [InlineData(10, -25.4, 1, KeyModifiers.None, -15)]
    [InlineData(100, 12, 0.5, KeyModifiers.None, 106)]
    [InlineData(100, 12, 0.5, KeyModifiers.Shift, 160)]
    [InlineData(100, 12, 0.5, KeyModifiers.Control, 100.6)]
    [InlineData(0, 3, 0.5, KeyModifiers.None, 1.5)]
    public void A_drag_changes_the_value_by_whole_pixels_times_the_step(decimal start, double pixels, decimal step, KeyModifiers modifiers, decimal expected) =>
        Assert.Equal(expected, ScrubLabel.ValueAt(start, pixels, step, modifiers, -10000, 10000));

    [Fact]
    public void The_value_stays_within_the_fields_limits()
    {
        Assert.Equal(1000, ScrubLabel.ValueAt(990, 500, 1, KeyModifiers.None, 1, 1000));
        Assert.Equal(1, ScrubLabel.ValueAt(5, -500, 1, KeyModifiers.None, 1, 1000));
    }

    private static XDocument Inspector()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "AiVideoEditor.sln"))) root = root.Parent;
        return XDocument.Load(Path.Combine(root!.FullName, "src", "UI", "Views", "Panels", "InspectorView.axaml"));
    }

    private static string? Name(XElement e) => e.Attributes().FirstOrDefault(a => a.Name.LocalName == "Name")?.Value;

    [Fact]
    public void Position_scale_and_rotation_have_a_draggable_label_bound_to_their_field()
    {
        var xaml = Inspector();
        var labels = xaml.Descendants().Where(e => e.Name.LocalName == "ScrubLabel").ToList();
        Assert.Equal(4, labels.Count);
        var fields = xaml.Descendants().Where(e => e.Name.LocalName == "CommitNumericUpDown").ToDictionary(e => Name(e) ?? Guid.NewGuid().ToString());
        foreach (var (box, binding, step) in new[] { ("PositionXBox", "PositionX", "1"), ("PositionYBox", "PositionY", "1"), ("ScaleBox", "ScalePercent", "0.5"), ("RotationBox", "Rotation", "0.5") })
        {
            var label = labels.Single(l => (string?)l.Attribute("Target") == "{Binding #" + box + "}");
            Assert.Equal(step, (string?)label.Attribute("StepPerPixel"));
            Assert.StartsWith("{Binding " + binding + ",", (string)fields[box].Attribute("Value")!);   // the exact field stays
        }
    }

    [Fact]
    public void The_percent_sliders_land_on_five_percent_and_the_others_keep_their_steps()
    {
        var sliders = Inspector().Descendants().Where(e => e.Name.LocalName == "Slider").ToDictionary(e => Name(e)!);
        foreach (var name in new[] { "OpacitySlider", "VolumeSlider", "BackgroundOpacitySlider" })
            Assert.Equal("5", (string?)sliders[name].Attribute("TickFrequency"));
        foreach (var name in new[] { "FadeInSlider", "FadeOutSlider", "StrokeWidthSlider", "FontSizeSlider" })
            Assert.Null(sliders[name].Attribute("TickFrequency"));            // the style's 1 (a frame, a pixel, a point)
    }
}
