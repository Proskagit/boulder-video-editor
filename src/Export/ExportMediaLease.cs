using AiVideoEditor.Core.Playback;
using Microsoft.Extensions.Logging;

namespace AiVideoEditor.Export;

/// <summary>
/// D060 §2: the source files of a running export, held open for its whole duration — read access, sharing only reads — so
/// that while it runs no program (the user's file manager included) can delete, move, rename or overwrite them. Reading them
/// stays possible: the export's own decoders, the Preview, thumbnails and analysis open them for reading as before. No file
/// is copied, whatever its size.
/// <para>
/// The snapshot names the files by the paths they had when the export started, so a relink or a removal from the project
/// after that never changes what the export reads (the project is not consulted); this lease makes sure the files at those
/// paths stay the ones the preflight checked.
/// </para>
/// <para>
/// Best effort, never a new reason to fail: a file that is already gone or that another program holds without sharing is
/// left out (logged) — reading it fails the export loudly exactly as before (<see cref="Core.Export.ExportFailure.DecodeFailed"/>),
/// it is never replaced by anything. A range export may name files outside its range that are offline; they are not needed.
/// </para>
/// </summary>
public sealed class ExportMediaLease : IDisposable
{
    private readonly List<FileStream> _files;

    private ExportMediaLease(List<FileStream> files) => _files = files;

    /// <summary>The paths held (tests, diagnostics).</summary>
    public IReadOnlyList<string> Paths => _files.Select(f => f.Name).ToList();

    /// <summary>Opens every source file of <paramref name="snapshot"/> that can be opened.</summary>
    public static ExportMediaLease Acquire(PlaybackSnapshot snapshot, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var files = new List<FileStream>();
        foreach (var path in snapshot.Assets.Values.Select(a => a.FilePath).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                files.Add(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 1));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                logger?.LogInformation("The export could not hold {Path} open ({Reason}); it is read as before.", path, ex.Message);
            }
        }
        return new ExportMediaLease(files);
    }

    public void Dispose()
    {
        foreach (var file in _files) file.Dispose();
        _files.Clear();
    }
}
