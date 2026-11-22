using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Composition;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Export;
using AiVideoEditor.UI.ViewModels;
using AiVideoEditor.UI.Views;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Xunit;

namespace AiVideoEditor.UI.Tests;

/// <summary>D074 §3: the export window's settings and the YouTube timestamps scroll above the buttons, so the opened
/// «Подробные настройки» and «Таймкоды» are reachable without resizing the window; the buttons stay outside the scroll.</summary>
[Collection(AvaloniaControlsCollection.Name)]
public sealed class ExportDialogScrollTests
{
    [Fact]
    public void The_settings_and_the_timestamps_scroll_and_the_buttons_stay_in_view()
    {
        var dialog = new ExportDialogViewModel(new Sequence(), FrameRate.Fps25,
            new ExportOutput(new FrameSize(1920, 1080), FrameRate.Fps25, 1500, MediaTime.FromSeconds(60), 48000 * 60), @"C:\out\video.mp4");
        var view = new ExportDialogView { DataContext = dialog };
        T Named<T>(string name) where T : Control => view.GetLogicalDescendants().OfType<T>().Single(c => c.Name == name);

        var scroll = Named<ScrollViewer>("SettingsScroll");
        Assert.Equal(Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, scroll.VerticalScrollBarVisibility);
        var inside = scroll.GetLogicalDescendants().OfType<Control>().Select(c => c.Name).Where(n => n is not null).ToHashSet();
        Assert.Contains("SettingsEditor", inside);
        Assert.Contains("TimestampsExpander", inside);
        Assert.DoesNotContain("ExportButton", inside);
        Assert.NotNull(Named<Button>("ExportButton"));
    }
}
