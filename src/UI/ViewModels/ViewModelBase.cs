using CommunityToolkit.Mvvm.ComponentModel;

namespace AiVideoEditor.UI.ViewModels;

/// <summary>
/// Base type for all view models. View models expose state and commands only —
/// no FFmpeg calls, no file I/O, no business logic. They call into Core service
/// interfaces (injected via DI) and let the corresponding subsystem do the work.
/// </summary>
public abstract class ViewModelBase : ObservableObject
{
}
