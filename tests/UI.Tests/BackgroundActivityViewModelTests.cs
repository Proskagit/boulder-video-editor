using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.UI.Localization;
using AiVideoEditor.UI.ViewModels;
using Xunit;

namespace AiVideoEditor.UI.Tests;

/// <summary>D078 amendment 1: the toolbar's load text and the idle badge.</summary>
public sealed class BackgroundActivityViewModelTests
{
    [Fact]
    public void The_load_reads_both_percentages_or_a_dash()
    {
        // Each number padded to three digit widths (figure spaces): the text keeps its width as the numbers change.
        Assert.Equal(string.Format(Strings.Activity_Load, " 23 %", "  6 %"), BackgroundActivityViewModel.FormatLoad(new SystemLoad(23.4, 5.6)));
        Assert.Equal(string.Format(Strings.Activity_Load, "100 %", "  — %"), BackgroundActivityViewModel.FormatLoad(new SystemLoad(100, null)));
        Assert.Equal(BackgroundActivityViewModel.FormatLoad(new SystemLoad(5, 5)).Length, BackgroundActivityViewModel.FormatLoad(new SystemLoad(99, 100)).Length);
    }

    [Fact]
    public async Task Without_analyses_the_badge_is_hidden_and_the_load_is_polled()
    {
        var monitor = new Monitor();
        using var badge = new BackgroundActivityViewModel(load: monitor);
        Assert.False(badge.IsBusy);
        Assert.True(badge.HasLoad);
        for (var i = 0; i < 50 && badge.LoadText == ""; i++) await Task.Delay(20);
        Assert.Equal(BackgroundActivityViewModel.FormatLoad(new SystemLoad(10, 20)), badge.LoadText);
        Assert.False(new BackgroundActivityViewModel().HasLoad);
    }

    private sealed class Monitor : ISystemLoadMonitor
    {
        public SystemLoad Sample() => new(10, 20);
    }
}
