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
public sealed class FitSettingsWorkflow(IFitCalibrationStore store, IFitSettingsDialog dialog, StatusService status, IDialogService? dialogs = null)
{
    public async Task RunAsync()
    {
        await store.LoadAsync();
        await dialog.ShowAsync(new FitSettingsViewModel(store, status, dialogs));
    }
}
