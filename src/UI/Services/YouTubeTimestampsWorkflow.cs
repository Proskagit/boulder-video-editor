using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.UI.Localization;
using AiVideoEditor.UI.ViewModels;
using AiVideoEditor.UI.Views;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;

namespace AiVideoEditor.UI.Services;

/// <summary>D057 §3.9: shows the YouTube timestamps window (tests use a scripted one).</summary>
public interface IYouTubeTimestampsDialog
{
    Task ShowAsync(YouTubeTimestampsViewModel timestamps);
}

/// <summary>D057 §3.9: «Таймкоды YouTube…» — the timeline's routes as a text to copy, the In / Out range offered.</summary>
public sealed class YouTubeTimestampsWorkflow(IProjectService projects, IYouTubeTimestampsDialog dialog, InOutRangeService? inOut = null)
{
    public YouTubeTimestampsViewModel Create()
    {
        var project = projects.Current;
        var rate = project.Settings.FrameRate;
        (MediaTime, MediaTime)? range = inOut?.Frames is { } frames
            ? (MediaTime.FromFrame(frames.First, rate), MediaTime.FromFrame(frames.End, rate))
            : null;
        return new YouTubeTimestampsViewModel(project.Timeline, rate, range);
    }

    public Task RunAsync() => dialog.ShowAsync(Create());
}

/// <summary>The window, modal over the main window.</summary>
public sealed class AvaloniaYouTubeTimestampsDialog : IYouTubeTimestampsDialog
{
    public async Task ShowAsync(YouTubeTimestampsViewModel timestamps)
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime { MainWindow: { IsVisible: true } owner })
            return;
        var window = new Window
        {
            Title = Strings.YouTube_Title, Width = 480, Height = 460, MinWidth = 360, MinHeight = 300, ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = new YouTubeTimestampsView { DataContext = timestamps }
        };
        void Close(object? sender, EventArgs e) => window.Close();
        timestamps.CloseRequested += Close;
        try
        {
            await window.ShowDialog(owner);
        }
        finally
        {
            timestamps.CloseRequested -= Close;
        }
    }
}
