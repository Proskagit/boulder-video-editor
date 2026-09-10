using CommunityToolkit.Mvvm.ComponentModel;

namespace AiVideoEditor.UI.Services;

/// <summary>
/// Holds the current status-bar message. Registered as a singleton so any part of
/// the UI can call <see cref="Report"/> without needing a direct reference to
/// <see cref="ViewModels.MainWindowViewModel"/> — that view model just binds to
/// this for display. Deliberately a plain concrete class, not a Core interface:
/// it's presentation state, not a domain abstraction, so there's no reason for it
/// to be swappable/mockable the way <c>IUndoRedoService</c> is.
/// </summary>
public sealed partial class StatusService : ObservableObject
{
    [ObservableProperty]
    private string _message = "Ready.";

    public void Report(string message) => Message = message;
}
