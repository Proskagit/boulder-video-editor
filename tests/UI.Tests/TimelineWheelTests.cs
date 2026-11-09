using AiVideoEditor.UI.Common;
using Avalonia;
using Avalonia.Input;
using Xunit;

namespace AiVideoEditor.UI.Tests;

/// <summary>D057 §3.11: the wheel scrolls the timeline horizontally, Ctrl+wheel zooms, Shift+wheel scrolls the tracks.</summary>
public class TimelineWheelTests
{
    [Theory]
    [InlineData(KeyModifiers.None, 0, -1, TimelineWheelAction.Horizontal)]
    [InlineData(KeyModifiers.None, -1, 0, TimelineWheelAction.Horizontal)]      // a touchpad's horizontal swipe
    [InlineData(KeyModifiers.Control, 0, 1, TimelineWheelAction.Zoom)]
    [InlineData(KeyModifiers.Control | KeyModifiers.Shift, 0, 1, TimelineWheelAction.Zoom)]
    [InlineData(KeyModifiers.Shift, 0, -1, TimelineWheelAction.Vertical)]
    [InlineData(KeyModifiers.Shift, -1, 0, TimelineWheelAction.Vertical)]       // Shift+wheel reported as horizontal
    [InlineData(KeyModifiers.None, 0, 0, TimelineWheelAction.None)]
    public void Each_modifier_has_its_action(KeyModifiers modifiers, double x, double y, TimelineWheelAction expected) =>
        Assert.Equal(expected, TimelineWheel.ActionFor(modifiers, new Vector(x, y)));

    [Fact]
    public void The_deltas_take_the_axis_that_moved()
    {
        Assert.Equal(-1, TimelineWheel.HorizontalDelta(new Vector(0, -1)));
        Assert.Equal(2, TimelineWheel.HorizontalDelta(new Vector(2, -1)));
        Assert.Equal(-1, TimelineWheel.VerticalDelta(new Vector(0, -1)));
        Assert.Equal(-3, TimelineWheel.VerticalDelta(new Vector(-3, 0)));
    }
}
