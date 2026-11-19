using AiVideoEditor.UI.ViewModels;

namespace AiVideoEditor.UI.Services;

/// <summary>
/// D069 §3: shows our own «Сохранить как» window (<see cref="SaveAsViewModel"/>) modally and completes when it closes; the
/// view model's <see cref="SaveAsViewModel.Accepted"/> and <see cref="SaveAsViewModel.TargetFolder"/> are the answer. The
/// implementation is <see cref="AvaloniaProjectSaveAsDialog"/>; tests use a scripted one.
/// </summary>
public interface IProjectSaveAsDialog
{
    Task ShowAsync(SaveAsViewModel saveAs);
}
