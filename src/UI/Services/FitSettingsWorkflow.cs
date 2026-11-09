using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.UI.ViewModels;

namespace AiVideoEditor.UI.Services;

/// <summary>Shows the FIT settings window modally (D051 §8.3, Step 37.6) — over the main window
/// (<see cref="AvaloniaFitSettingsDialog"/>); tests use a scripted one. Completes when the window closes.</summary>
public interface IFitSettingsDialog
{
    Task ShowAsync(FitSettingsViewModel settings);
}

/// <summary>
/// «Настройки FIT…» of the COROS training drop-down (D051 §8.3, B2 / О4): available with or without a training in the
/// project — the calibration is the user's, not the project's. Reads the store again before the window opens.
/// </summary>
/// <remarks>D057 §3.8: with the application's settings window (<see cref="IAppSettingsDialog"/>, the app) it opens that window —
/// at the section «COROS / FIT» for this menu item, at «Общие» for the toolbar's «Настройки…»; the calibration's semantics are
/// unchanged. Without it (tests of D051) the FIT window alone, as before.</remarks>
public sealed class FitSettingsWorkflow(IFitCalibrationStore store, IFitSettingsDialog dialog, StatusService status, IDialogService? dialogs = null,
    IUserSettingsService? settings = null, IAppSettingsDialog? appDialog = null, IFfmpegLocator? ffmpeg = null, IFfprobeLocator? ffprobe = null)
{
    public bool HasAppSettings => settings is not null && appDialog is not null;

    public async Task RunAsync() => await RunAsync(AppSettingsSection.CorosFit);

    public async Task RunAsync(AppSettingsSection section)
    {
        await store.LoadAsync();
        var fit = new FitSettingsViewModel(store, status, dialogs);
        if (settings is null || appDialog is null)
        {
            await dialog.ShowAsync(fit);
            return;
        }
        string? ffmpegPath = null, ffprobePath = null;
        try
        {
            if (ffmpeg is not null) ffmpegPath = await ffmpeg.GetFfmpegPathAsync();
            if (ffprobe is not null) ffprobePath = await ffprobe.GetFfprobePathAsync();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // only shown for information
        }
        await appDialog.ShowAsync(new AppSettingsViewModel(settings, fit, section, ffmpegPath, ffprobePath));
    }
}

/// <summary>D057 §3.8: shows the application's settings window modally.</summary>
public interface IAppSettingsDialog
{
    Task ShowAsync(AppSettingsViewModel settings);
}
