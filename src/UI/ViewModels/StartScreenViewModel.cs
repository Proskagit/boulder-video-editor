using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.UI.ViewModels.Panels;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AiVideoEditor.UI.ViewModels;

/// <summary>
/// D068 item 5: the first screen at startup — «Создать проект», «Открыть…» and the recent projects (as Insta360 Studio,
/// CapCut, DaVinci Resolve and Shotcut open). It lies over the editor's empty new project: «Создать проект» just shows that
/// project; it closes for good once another project is open or the new one changes (a file imported, a clip added).
/// </summary>
public sealed partial class StartScreenViewModel : ViewModelBase
{
    private readonly IAsyncRelayCommand _open;

    /// <param name="recent">The tool bar's «Недавние» list; null: no list.</param>
    /// <param name="open">The tool bar's «Открыть…».</param>
    public StartScreenViewModel(RecentProjectsViewModel? recent, IAsyncRelayCommand open, IProjectService projects)
    {
        Recent = recent;
        _open = open;
        projects.ProjectChanged += (_, _) => IsVisible = false;
        projects.MediaAssetsChanged += (_, _) => IsVisible = false;
        projects.TimelineChanged += (_, _) => IsVisible = false;
        _ = Recent?.RefreshAsync();     // reads the list and checks every entry, as the drop-down does when it opens
    }

    [ObservableProperty]
    private bool _isVisible = true;

    /// <summary>The recent projects (the tool bar's «Недавние» list); null when the app has none.</summary>
    public RecentProjectsViewModel? Recent { get; }

    /// <summary>The editor's new project is already there: the screen only steps aside.</summary>
    [RelayCommand]
    private void NewProject() => IsVisible = false;

    /// <summary>«Открыть…» of the tool bar (the folder picker, the «save changes?» question); the screen closes when a project
    /// opens (<see cref="IProjectService.ProjectChanged"/>).</summary>
    [RelayCommand]
    private Task Open() => _open.ExecuteAsync(null);
}
