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
public sealed class AvaloniaFilePickerService : IFilePickerService
{
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

        var files = await storageProvider.OpenFilePickerAsync(options);

        var paths = new List<string>(files.Count);
        foreach (var file in files)
        {
            var localPath = file.TryGetLocalPath();
            if (localPath is not null)
                paths.Add(localPath);
        }
        return paths;
    }

    public async Task<string?> PickFolderAsync(FolderPickerRequest request, CancellationToken ct = default)
    {
        var storageProvider = GetTopLevel()?.StorageProvider;
        if (storageProvider is null)
            return null;

        var options = new FolderPickerOpenOptions { Title = request.Title, AllowMultiple = false };
        if (request.StartFolder is { } start && Directory.Exists(start))
            options.SuggestedStartLocation = await storageProvider.TryGetFolderFromPathAsync(start);

        var folders = await storageProvider.OpenFolderPickerAsync(options);
        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
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
