using System.Text;
using AiVideoEditor.Core.Interfaces;

namespace AiVideoEditor.Project.Persistence;

/// <summary>
/// Reads and writes project files on disk. Writes are atomic: the new content goes to a
/// temporary file next to the target, is flushed to disk, and only then replaces the
/// target, so a failed or cancelled write leaves an existing file exactly as it was.
/// </summary>
public sealed class ProjectFileStore
{
    public const string ProjectFileName = "project.json";

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>Test hook: runs after the temporary file is complete and before it replaces
    /// the target. Throwing from it simulates a failure at the last possible moment.</summary>
    internal Action<string>? BeforeCommit { get; init; }

    public static string ProjectFilePath(string projectFolderPath) => Path.Combine(projectFolderPath, ProjectFileName);

    /// <exception cref="ProjectFileException">The file is missing or can't be read.</exception>
    public async Task<string> ReadAsync(string path, CancellationToken ct = default)
    {
        try
        {
            return await File.ReadAllTextAsync(path, Utf8NoBom, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            throw new ProjectFileException($"{Path.GetFileName(path)} was not found in {Path.GetDirectoryName(path)}.", ex);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            throw new ProjectFileException($"The project file couldn't be read: {ex.Message}", ex);
        }
    }

    /// <summary>Atomically replaces (or creates) <paramref name="path"/> with
    /// <paramref name="content"/>. The folder is created if needed. Cancellation is honoured
    /// only before the replace step.</summary>
    /// <exception cref="ProjectFileException">The file couldn't be written; any existing
    /// file at <paramref name="path"/> is unchanged.</exception>
    public async Task WriteAtomicAsync(string path, string content, CancellationToken ct = default)
    {
        var fullPath = Path.GetFullPath(path);
        var tempPath = $"{fullPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);

            var bytes = Utf8NoBom.GetBytes(content);
            await using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                             bufferSize: 4096, FileOptions.Asynchronous))
            {
                await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
                await stream.FlushAsync(ct).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            ct.ThrowIfCancellationRequested();
            BeforeCommit?.Invoke(fullPath);

            if (File.Exists(fullPath))
                File.Replace(tempPath, fullPath, destinationBackupFileName: null, ignoreMetadataErrors: true);
            else
                File.Move(tempPath, fullPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            TryDelete(tempPath);
            throw new ProjectFileException($"The project couldn't be saved: {ex.Message}", ex);
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A stray *.tmp file is harmless; the target file is what matters.
        }
    }
}
