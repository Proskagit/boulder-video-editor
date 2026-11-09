using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.UI.Common;
using Avalonia;
using Xunit;

namespace AiVideoEditor.UI.Tests;

/// <summary>D057 §3.4: the stored window is used only where a screen shows its title bar; panels keep sizes inside limits.</summary>
public class WindowLayoutTests
{
    private static readonly PixelRect Primary = new(0, 0, 1920, 1040);
    private static readonly PixelRect Right = new(1920, 0, 2560, 1400);

    [Fact]
    public void A_window_on_a_screen_is_restored()
    {
        Assert.True(WindowLayout.IsOnScreen(new WindowPlacement(100, 50, 1440, 900, false), new[] { Primary }, 1));
        Assert.True(WindowLayout.IsOnScreen(new WindowPlacement(2000, 100, 1440, 900, true), new[] { Primary, Right }, 1));
    }

    [Fact]
    public void A_window_on_a_screen_that_is_gone_opens_at_the_default_place()
    {
        Assert.False(WindowLayout.IsOnScreen(new WindowPlacement(2000, 100, 1440, 900, false), new[] { Primary }, 1));
        Assert.False(WindowLayout.IsOnScreen(new WindowPlacement(-3000, -3000, 1440, 900, false), new[] { Primary }, 1));
        Assert.False(WindowLayout.IsOnScreen(new WindowPlacement(100, 2000, 1440, 900, false), new[] { Primary }, 1));
        Assert.False(WindowLayout.IsOnScreen(new WindowPlacement(1900, 10, 1440, 900, false), new[] { Primary }, 1));  // 20 px of title only
        Assert.False(WindowLayout.IsOnScreen(new WindowPlacement(10, 10, 0, 900, false), new[] { Primary }, 1));
    }

    [Fact]
    public void Sizes_stay_inside_their_limits()
    {
        Assert.Equal(260, WindowLayout.SidePanel(null, 260));
        Assert.Equal(WindowLayout.MinSidePanel, WindowLayout.SidePanel(20, 260));
        Assert.Equal(WindowLayout.MaxSidePanel, WindowLayout.SidePanel(5000, 260));
        Assert.Equal(260, WindowLayout.SidePanel(double.NaN, 260));
        Assert.Equal(330, WindowLayout.Timeline(330, 240));
        Assert.Equal(WindowLayout.MinTimeline, WindowLayout.Timeline(10, 240));
        Assert.Equal(WindowLayout.MinWindowWidth, WindowLayout.Window(300, WindowLayout.MinWindowWidth));
    }
}
