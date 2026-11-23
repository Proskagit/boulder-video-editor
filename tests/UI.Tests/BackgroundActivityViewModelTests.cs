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
        Assert.Equal(string.Format(Strings.Activity_Load, "23 %", "61 %"), BackgroundActivityViewModel.FormatLoad(new SystemLoad(23.4, 60.6)));
        Assert.Equal(string.Format(Strings.Activity_Load, "—", "—"), BackgroundActivityViewModel.FormatLoad(new SystemLoad(null, null)));
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
