using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;

namespace AiVideoEditor.UI.Services;

/// <summary>
/// Real implementation of <see cref="IFilePickerService"/> using Avalonia's
/// StorageProvider API against the application's main window. This is the one
/// place in the whole solution that touches Avalonia's file-dialog types —
/// everything upstream of it (view models, Core) only ever sees <see cref="IFilePickerService"/>.
/// </summary>
/// D068 item 12: a picker the caller gives no folder opens where the user picked last in this session, else in the user's
/// Videos (files to import) or Documents (projects, exports) — never the program's own folder, Windows' fallback.
public sealed class AvaloniaFilePickerService : IFilePickerService
{
    private string? _lastFolder;

    private string? StartFolder(string? requested, Environment.SpecialFolder fallback)
    {
        foreach (var candidate in new[] { requested, _lastFolder, Environment.GetFolderPath(fallback),
                     Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) })
            if (!string.IsNullOrEmpty(candidate) && Directory.Exists(candidate)) return candidate;
        return null;
    }

    private void Remember(string? path, bool isFolder)
    {
        var folder = isFolder ? path : Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(folder)) _lastFolder = folder;
    }

    public async Task<IReadOnlyList<string>> PickFilesAsync(FilePickerRequest request, CancellationToken ct = default)
    {
        var topLevel = GetTopLevel();
        var storageProvider = topLevel?.StorageProvider;
        if (storageProvider is null)
            return Array.Empty<string>();

        var options = new FilePickerOpenOptions
        {
            Title = request.Title,
            AllowMultiple = request.AllowMultiple,
            FileTypeFilter = request.FileTypeFilters
                .Select(f => new FilePickerFileType(f.Name) { Patterns = f.Patterns.ToArray() })
                .ToArray()
        };
        if (StartFolder(request.StartFolder, Environment.SpecialFolder.MyVideos) is { } start)
            options.SuggestedStartLocation = await storageProvider.TryGetFolderFromPathAsync(start);

        var files = await storageProvider.OpenFilePickerAsync(options);

        var paths = new List<string>(files.Count);
        foreach (var file in files)
        {
            var localPath = file.TryGetLocalPath();
            if (localPath is not null)
                paths.Add(localPath);
        }
        if (paths.Count > 0) Remember(paths[0], isFolder: false);
        return paths;
    }

    public async Task<string?> PickFolderAsync(FolderPickerRequest request, CancellationToken ct = default)
    {
        var storageProvider = GetTopLevel()?.StorageProvider;
        if (storageProvider is null)
            return null;

        var options = new FolderPickerOpenOptions { Title = request.Title, AllowMultiple = false };
        if (StartFolder(request.StartFolder, Environment.SpecialFolder.MyDocuments) is { } start)
            options.SuggestedStartLocation = await storageProvider.TryGetFolderFromPathAsync(start);

        var folders = await storageProvider.OpenFolderPickerAsync(options);
        var picked = folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
        Remember(picked, isFolder: true);
        return picked;
    }

    public async Task<string?> PickSaveFileAsync(SaveFilePickerRequest request, CancellationToken ct = default)
    {
        var storageProvider = GetTopLevel()?.StorageProvider;
        if (storageProvider is null)
            return null;

        var options = new FilePickerSaveOptions
        {
            Title = request.Title,
            SuggestedFileName = request.SuggestedFileName,
            DefaultExtension = request.DefaultExtension,
            ShowOverwritePrompt = false, // the caller confirms replacing a file itself
            FileTypeChoices = request.FileTypeFilters
                .Select(f => new FilePickerFileType(f.Name) { Patterns = f.Patterns.ToArray() })
                .ToArray()
        };
        if (StartFolder(request.StartFolder, Environment.SpecialFolder.MyDocuments) is { } start)
            options.SuggestedStartLocation = await storageProvider.TryGetFolderFromPathAsync(start);

        var file = await storageProvider.SaveFilePickerAsync(options);
        var path = file?.TryGetLocalPath();
        Remember(path, isFolder: false);
        return path;
    }

    private static TopLevel? GetTopLevel()
    {
        return Application.Current?.ApplicationLifetime switch
        {
            IClassicDesktopStyleApplicationLifetime desktop => desktop.MainWindow,
            _ => null
        };
    }
}
