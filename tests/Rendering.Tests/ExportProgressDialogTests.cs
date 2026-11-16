using AiVideoEditor.Core.Export;
using AiVideoEditor.UI.Services;
using AiVideoEditor.UI.ViewModels;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Xunit;

namespace AiVideoEditor.Rendering.Tests;

/// <summary>
/// Phase 8 Step 7, D060 §5: the real Avalonia export progress window — it shows the latest <see cref="ExportProgress"/> pulled
/// by its timer (also while hidden, for the compact indicator); closing it by the title bar or «Свернуть» only hides it (the
/// export goes on), <see cref="IExportProgressDialog.Activate"/> shows it again, «Отменить экспорт» cancels once, and
/// <see cref="IExportProgressDialog.Close"/> closes it.
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

    private static Button ButtonNamed(Window window, string name) =>
        window.GetLogicalDescendants().OfType<Button>().Single(b => b.Name == name);

    [Fact]
    public void The_window_shows_progress_and_closing_it_only_hides_it()
    {
        var cancelled = 0;
        var progress = new ExportProgressViewModel(@"C:\out\video.mp4", () => cancelled++) { SnapshotText = "Exporting «Demo» as it was at 12:00:00" };
        var dialog = new AvaloniaExportProgressDialog();

        var window = OnUi(() => { dialog.Show(progress); return dialog.Window!; });
        Assert.True(OnUi(() => window.IsVisible));
        Assert.Contains(OnUi(() => window.GetLogicalDescendants().OfType<TextBlock>().Select(t => t.Text).ToList()),
            t => t == "Exporting «Demo» as it was at 12:00:00");

        progress.Report(new ExportProgress(ExportStage.Video, 30, 120));        // from the export's thread
        WaitUntil(() => progress.Stage == ExportStage.Video);
        Assert.Equal((30L, 120L, 25.0, "Video"), OnUi(() => (progress.Done, progress.Total, progress.Percent, progress.StageText)));
        Assert.Contains("30 / 120 frames (25 %)", OnUi(() => progress.DetailText));

        OnUi(() => { window.Close(); return 0; });                              // title bar close / Alt+F4
        Assert.Equal(0, cancelled);
        Assert.False(OnUi(() => window.IsVisible));
        progress.Report(new ExportProgress(ExportStage.Video, 60, 120));       // refreshed while hidden (the indicator)
        WaitUntil(() => progress.Done == 60);

        OnUi(() => { dialog.Activate(); return 0; });
        Assert.True(OnUi(() => window.IsVisible));
        Assert.Same(window, dialog.Window);

        OnUi(() => { ButtonNamed(window, "HideButton").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent)); return 0; });
        Assert.False(OnUi(() => window.IsVisible));
        Assert.Equal(0, cancelled);

        OnUi(() => { dialog.Close(); return 0; });                              // the export has finished
        Assert.False(OnUi(() => window.IsVisible));
        Assert.Null(dialog.Window);
        OnUi(() => { dialog.Activate(); return 0; });                           // nothing without an export
        Assert.Equal(0, cancelled);
    }

    [Fact]
    public void The_cancel_button_cancels_once()
    {
        var cancelled = 0;
        var progress = new ExportProgressViewModel(@"C:\out\video.mp4", () => cancelled++);
        var dialog = new AvaloniaExportProgressDialog();
        OnUi(() => { dialog.Show(progress); return 0; });

        var button = OnUi(() => ButtonNamed(dialog.Window!, "CancelExportButton"));
        OnUi(() => { button.Command!.Execute(null); return 0; });
        Assert.False(OnUi(() => button.Command!.CanExecute(null)));
        OnUi(() => { progress.Cancel(); return 0; });

        Assert.Equal(1, cancelled);
        Assert.Equal("Cancelling…", OnUi(() => progress.StageText));
        OnUi(() => { dialog.Close(); return 0; });
    }
}
