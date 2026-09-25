using AiVideoEditor.UI.ViewModels;

namespace AiVideoEditor.UI.Services;

/// <summary>
/// Shows the modal export progress window without the workflow knowing about Avalonia windows
/// (<see cref="AvaloniaExportProgressDialog"/>). The window stays until <see cref="Close"/>: closing it by the
/// title bar only requests cancellation (<see cref="ExportProgressViewModel.Cancel"/>).
/// </summary>
public interface IExportProgressDialog
{
    void Show(ExportProgressViewModel progress);

    void Close();
}
