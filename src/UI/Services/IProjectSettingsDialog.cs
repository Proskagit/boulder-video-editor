using AiVideoEditor.UI.ViewModels;

namespace AiVideoEditor.UI.Services;

/// <summary>
/// Shows the Project Settings dialog for <paramref name="settings"/> modally over the main window (Phase 13 Step 13.6)
/// and completes when it closes — after a successful Apply or a Cancel (<see cref="ProjectSettingsViewModel.CloseRequested"/>),
/// or the window closed. The implementation is <see cref="AvaloniaProjectSettingsDialog"/>; tests use a scripted one.
/// </summary>
public interface IProjectSettingsDialog
{
    Task ShowAsync(ProjectSettingsViewModel settings);
}
