using AiVideoEditor.UI.Localization;
using AiVideoEditor.UI.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AiVideoEditor.UI.ViewModels;

/// <summary>
/// D069 §3: our own «Сохранить как» window — the project's name and the folder it goes into (changed by the system's folder
/// picker). The project is saved into <c>&lt;folder&gt;\&lt;name&gt;</c> (D067 §6). Windows' save dialog opened a folder whose
/// name was typed instead of returning it, so replacing a project took a second «Сохранить»; here the name is a plain text
/// field and the caller asks «Заменить?» at once. An empty or invalid name, no folder, or a file of that name keeps the window
/// open with the reason.
/// </summary>
public sealed partial class SaveAsViewModel : ObservableObject
{
    private readonly IFilePickerService? _picker;

    public SaveAsViewModel(string name, string folder, IFilePickerService? picker)
    {
        _name = name;
        _folder = folder;
        _picker = picker;
        Validate();
    }

    [ObservableProperty] private string _name;
    [ObservableProperty] private string _folder;

    /// <summary>Why «Сохранить» cannot be pressed (null: it can).</summary>
    [ObservableProperty] private string? _error;

    /// <summary>The folder the project would be written into, or null while the name or the folder is not valid.</summary>
    public string? TargetFolder => Error is null ? Path.Combine(Folder, Name.Trim()) : null;

    /// <summary>D069 amendment 1: said in the window before «Сохранить» — a project of that name is there (the next step asks
    /// to replace it).</summary>
    public string? ReplaceNote => TargetFolder is { } target && File.Exists(Path.Combine(target, "project.json")) ? Strings.SaveAs_WillAskReplace : null;

    /// <summary>Raised by «Сохранить» (true) and «Отмена» (false); the window closes.</summary>
    public event EventHandler<bool>? CloseRequested;

    /// <summary>The answer once closed: true after «Сохранить».</summary>
    public bool Accepted { get; private set; }

    partial void OnNameChanged(string value) => Validate();
    partial void OnFolderChanged(string value) => Validate();

    private void Validate()
    {
        Error = ReasonAgainst(Name, Folder);
        OnPropertyChanged(nameof(TargetFolder));
        OnPropertyChanged(nameof(ReplaceNote));
        SaveCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Why a project named <paramref name="name"/> cannot be saved into <paramref name="folder"/>, or null.</summary>
    internal static string? ReasonAgainst(string? name, string? folder)
    {
        var trimmed = name?.Trim() ?? "";
        if (trimmed.Length == 0) return Strings.SaveAs_NameEmpty;
        if (trimmed is "." or ".." || trimmed.EndsWith('.') || trimmed.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return Strings.SaveAs_NameInvalid;
        if (string.IsNullOrWhiteSpace(folder)) return Strings.SaveAs_NoFolder;
        string target;
        try
        {
            target = Path.Combine(folder, trimmed);
        }
        catch (ArgumentException)
        {
            return Strings.SaveAs_NoFolder;
        }
        if (File.Exists(target)) return string.Format(Strings.Project_SaveAsFileExists, trimmed);
        return null;
    }

    private bool CanSave() => Error is null;

    [RelayCommand(CanExecute = nameof(CanSave))]
    private void Save()
    {
        Validate();    // a file may have appeared since the name was typed
        if (Error is not null) return;
        Accepted = true;
        CloseRequested?.Invoke(this, true);
    }

    [RelayCommand]
    private void Cancel()
    {
        Accepted = false;
        CloseRequested?.Invoke(this, false);
    }

    [RelayCommand]
    private async Task ChangeFolder()
    {
        if (_picker is null) return;
        var picked = await _picker.PickFolderAsync(new FolderPickerRequest
        {
            Title = Strings.SaveAs_FolderPickerTitle,
            StartFolder = Directory.Exists(Folder) ? Folder : null
        });
        if (picked is not null) UsePicked(picked);
    }

    /// <summary>D069 amendment 1: a project's own folder picked by «Изменить…» means that project — its parent becomes the folder
    /// and its name the name (so «Заменить?» is asked), never a project saved inside a project.</summary>
    internal void UsePicked(string picked)
    {
        var folder = Path.TrimEndingDirectorySeparator(picked);
        if (File.Exists(Path.Combine(folder, "project.json")) && Path.GetDirectoryName(folder) is { Length: > 0 } parent)
        {
            Name = Path.GetFileName(folder);
            Folder = parent;
        }
        else Folder = folder;
    }
}
