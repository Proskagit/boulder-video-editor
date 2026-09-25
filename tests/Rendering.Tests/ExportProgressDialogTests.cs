using AiVideoEditor.Core.Export;
using AiVideoEditor.UI.Services;
using AiVideoEditor.UI.ViewModels;
using Avalonia.Controls;
using Avalonia.Threading;
using Xunit;

namespace AiVideoEditor.Rendering.Tests;

/// <summary>
/// Phase 8 Step 7: the real Avalonia export progress window — it shows the latest <see cref="ExportProgress"/> pulled by
/// its timer, closing it by the title bar only requests cancellation (the window stays until the export has finished),
/// and <see cref="IExportProgressDialog.Close"/> closes it.
/// </summary>
[Collection(AvaloniaCollection.Name)]
public sealed class ExportProgressDialogTests
{
    private static T OnUi<T>(Func<T> action) => Dispatcher.UIThread.InvokeAsync(action).GetTask().GetAwaiter().GetResult();

    private static void WaitUntil(Func<bool> condition)
    {
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!OnUi(condition))
        {
            if (DateTime.UtcNow > until) throw new TimeoutException();
            Thread.Sleep(20);
        }
    }

    [Fact]
    public void The_window_shows_progress_and_closing_it_only_requests_cancellation()
    {
        var cancelled = 0;
        var progress = new ExportProgressViewModel(@"C:\out\video.mp4", () => cancelled++);
        var dialog = new AvaloniaExportProgressDialog();

        var window = OnUi(() => { dialog.Show(progress); return dialog.Window!; });
        Assert.True(OnUi(() => window.IsVisible));

        progress.Report(new ExportProgress(ExportStage.Video, 30, 120));        // from the export's thread
        WaitUntil(() => progress.Stage == ExportStage.Video);
        Assert.Equal((30L, 120L, 25.0, "Video"), OnUi(() => (progress.Done, progress.Total, progress.Percent, progress.StageText)));
        Assert.Contains("30 / 120 frames (25 %)", OnUi(() => progress.DetailText));

        OnUi(() => { window.Close(); return 0; });                              // title bar close / Alt+F4
        Assert.Equal(1, cancelled);
        Assert.True(OnUi(() => window.IsVisible));
        Assert.Equal("Cancelling…", OnUi(() => progress.StageText));
        progress.Report(new ExportProgress(ExportStage.Video, 31, 120));
        WaitUntil(() => progress.Done == 31);
        Assert.Equal("Cancelling…", OnUi(() => progress.StageText));

        OnUi(() => { dialog.Close(); return 0; });                              // the export has finished
        Assert.False(OnUi(() => window.IsVisible));
        Assert.Null(dialog.Window);
        Assert.Equal(1, cancelled);
    }

    [Fact]
    public void The_cancel_button_cancels_once()
    {
        var cancelled = 0;
        var progress = new ExportProgressViewModel(@"C:\out\video.mp4", () => cancelled++);
        var dialog = new AvaloniaExportProgressDialog();
        OnUi(() => { dialog.Show(progress); return 0; });

        var button = OnUi(() => ((StackPanel)dialog.Window!.Content!).Children.OfType<Button>().Single());
        OnUi(() => { button.Command!.Execute(null); return 0; });
        Assert.False(OnUi(() => button.Command!.CanExecute(null)));
        OnUi(() => { progress.Cancel(); return 0; });

        Assert.Equal(1, cancelled);
        OnUi(() => { dialog.Close(); return 0; });
    }
}
