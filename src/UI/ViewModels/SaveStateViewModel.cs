using System.Globalization;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.UI.Localization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AiVideoEditor.UI.ViewModels;

/// <summary>
/// D068 item 11: the project's save state on the tool bar, as CapCut and Insta360 Studio show it — «Сохранено в 10:26»,
/// «Изменения не сохранены» (with the time of the last recovery copy in the tooltip: the autosave writes a recovery file,
/// not the project, so it is never called «saved»), «Проект не сохранён» for a project without a folder.
/// </summary>
public sealed partial class SaveStateViewModel : ObservableObject
{
    private readonly IProjectService _projects;
    private readonly Func<DateTime> _now;
    private readonly SynchronizationContext? _ui;
    private DateTime? _savedAt;
    private DateTime? _recoveryAt;

    public SaveStateViewModel(IProjectService projects, IAutosaveService? autosave = null, Func<DateTime>? now = null)
    {
        _projects = projects;
        _now = now ?? (() => DateTime.Now);
        _ui = SynchronizationContext.Current;
        projects.SaveStateChanged += (_, _) => Update();
        projects.ProjectSaved += (_, _) => { _savedAt = _now(); _recoveryAt = null; Update(); };
        projects.ProjectChanged += (_, _) => { _savedAt = null; _recoveryAt = null; Update(); };
        if (autosave is not null)
            autosave.AutosaveCompleted += (_, _) => OnUi(() => { _recoveryAt = _now(); Update(); });
        Update();
    }

    [ObservableProperty]
    private string _text = "";

    [ObservableProperty]
    private string _toolTip = "";

    /// <summary>The project has changes not written to its file (the text is then emphasised).</summary>
    [ObservableProperty]
    private bool _hasUnsavedChanges;

    private void Update()
    {
        var project = _projects.Current;
        HasUnsavedChanges = project.IsDirty;
        if (project.ProjectFolderPath is null)
        {
            Text = Strings.SaveState_NotSaved;
            ToolTip = Strings.SaveState_NotSavedTip;
        }
        else if (project.IsDirty)
        {
            Text = Strings.SaveState_Unsaved;
            ToolTip = _recoveryAt is { } at
                ? string.Format(CultureInfo.CurrentCulture, Strings.SaveState_RecoveryAt, Time(at))
                : Strings.SaveState_UnsavedTip;
        }
        else
        {
            Text = _savedAt is { } at ? string.Format(CultureInfo.CurrentCulture, Strings.SaveState_SavedAt, Time(at)) : Strings.SaveState_Saved;
            ToolTip = project.ProjectFolderPath;
        }
    }

    private static string Time(DateTime at) => at.ToString("HH:mm", CultureInfo.InvariantCulture);

    private void OnUi(Action action)
    {
        if (_ui is null || SynchronizationContext.Current == _ui) action();
        else _ui.Post(_ => action(), null);
    }
}
