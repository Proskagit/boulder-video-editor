using AiVideoEditor.UI.Localization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AiVideoEditor.UI.Services;

/// <summary>D068 item 4: what a status message is — so an error never looks like an ordinary message.</summary>
public enum StatusKind
{
    /// <summary>Something happened as asked (neutral).</summary>
    Info,

    /// <summary>The action was refused or limited — nothing broke, but the user has to do something else.</summary>
    Warning,

    /// <summary>Something failed.</summary>
    Error
}

/// <summary>
/// Holds the current status-bar message. Registered as a singleton so any part of
/// the UI can call <see cref="Report"/> without needing a direct reference to
/// <see cref="ViewModels.MainWindowViewModel"/> — that view model just binds to
/// this for display. Deliberately a plain concrete class, not a Core interface:
/// it's presentation state, not a domain abstraction, so there's no reason for it
/// to be swappable/mockable the way <c>IUndoRedoService</c> is.
/// D068 item 4: each message has a <see cref="Kind"/>; a warning or an error stays until the next message replaces it.
/// </summary>
public sealed partial class StatusService : ObservableObject
{
    [ObservableProperty]
    private string _message = Strings.Status_Ready;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsWarning), nameof(IsError))]
    private StatusKind _kind = StatusKind.Info;

    public bool IsWarning => Kind == StatusKind.Warning;

    public bool IsError => Kind == StatusKind.Error;

    /// <summary>An ordinary message.</summary>
    public void Report(string message) => Set(message, StatusKind.Info);

    /// <summary>The action was refused or limited.</summary>
    public void ReportWarning(string message) => Set(message, StatusKind.Warning);

    /// <summary>Something failed.</summary>
    public void ReportError(string message) => Set(message, StatusKind.Error);

    private void Set(string message, StatusKind kind)
    {
        Kind = kind;
        Message = message;
    }
}
