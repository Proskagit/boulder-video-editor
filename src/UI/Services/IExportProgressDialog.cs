using AiVideoEditor.UI.ViewModels;

namespace AiVideoEditor.UI.Services;

/// <summary>
/// The detailed export window without the workflow knowing about Avalonia windows (<see cref="AvaloniaExportProgressDialog"/>).
/// D060 §5: not modal — the editor stays usable while the export runs. Closing it by the title bar or «Свернуть» only hides it
/// (the export goes on, the main window's compact indicator shows it); <see cref="Activate"/> brings it back; only
/// <see cref="Close"/> — the export has finished — ends it. One window per export.
/// </summary>
public interface IExportProgressDialog
{
    /// <summary>Opens the window for a new export; it starts refreshing <paramref name="progress"/> (also while hidden, for
    /// the compact indicator).</summary>
    void Show(ExportProgressViewModel progress);

    /// <summary>Shows the window again (it may be hidden) in front of the main window. Nothing without an export.</summary>
    void Activate();

    /// <summary>The export has finished: the window closes for good.</summary>
    void Close();
}
