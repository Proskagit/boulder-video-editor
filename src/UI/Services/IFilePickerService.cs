namespace AiVideoEditor.UI.Services;

/// <summary>
/// Lets a view model ask for one or more files from the user without knowing
/// anything about Avalonia's StorageProvider API. This interface itself has no
/// Avalonia types in it; the concrete implementation
/// (<see cref="AvaloniaFilePickerService"/>) is what actually talks to Avalonia,
/// and lives in this same UI project per the Phase 2 architecture rules
/// ("UI project may contain Avalonia-specific dialog/file-picker abstractions").
/// </summary>
public interface IFilePickerService
{
    /// <summary>Opens a native "open file" dialog. Returns an empty list if the
    /// user cancels — callers must treat that as a normal, silent outcome, not an error.</summary>
    Task<IReadOnlyList<string>> PickFilesAsync(FilePickerRequest request, CancellationToken ct = default);
}

public sealed class FilePickerRequest
{
    public string Title { get; init; } = "Open File";
    public bool AllowMultiple { get; init; } = true;
    public IReadOnlyList<FilePickerFileTypeFilter> FileTypeFilters { get; init; } = Array.Empty<FilePickerFileTypeFilter>();
}

/// <summary>One filter entry in the file picker's type dropdown.</summary>
public sealed class FilePickerFileTypeFilter
{
    /// <summary>Display name, e.g. "Video files".</summary>
    public required string Name { get; init; }

    /// <summary>Glob patterns, e.g. "*.mp4".</summary>
    public required IReadOnlyList<string> Patterns { get; init; }
}
