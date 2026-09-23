namespace AiVideoEditor.UI.Services;

/// <summary>
/// Asks the user a question in a modal dialog without the view model knowing about Avalonia
/// windows. The implementation (<see cref="AvaloniaDialogService"/>) shows it over the main window.
/// </summary>
public interface IDialogService
{
    /// <summary>Shows <paramref name="request"/> and returns the index of the button the user
    /// chose, or null if the dialog was closed without choosing (or couldn't be shown).</summary>
    Task<int?> AskAsync(DialogRequest request);
}

public sealed class DialogRequest
{
    public required string Title { get; init; }
    public required string Message { get; init; }

    /// <summary>Button captions, left to right. The first one is the default (Enter).</summary>
    public required IReadOnlyList<string> Buttons { get; init; }
}
