using System.Collections.Concurrent;
using System.Text;
using AiVideoEditor.Core.Color;
using AiVideoEditor.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace AiVideoEditor.Media.Caching;

/// <summary>
/// The HDR → SDR LUT files (D033 §3 "LUT"): one <c>.cube</c> per key (transfer, peak, rule version) in one folder of the
/// application's cache (<c>%LOCALAPPDATA%\AiVideoEditor\cache\color-luts</c>, not the project).
/// <list type="bullet">
/// <item>Once per key and session the LUT is generated from the rule (<see cref="HdrLut.Generate"/>, deterministic) and
/// compared with the file: a missing, damaged or different file is written again — atomically (a temporary file, then a
/// move). Concurrent first uses share the one generation.</item>
/// <item>At the first use in a session, LUT files of other rule versions (and leftover temporary files) are deleted.</item>
/// <item>A failure (e.g. the folder is not writable) is thrown to the decoder, which fails the open: an HDR picture is
/// never decoded without its conversion. The next request tries again.</item>
/// </list>
/// </summary>
public sealed class HdrLutStore : IHdrLutStore
{
    private static readonly Encoding Utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private readonly string _folder;
    private readonly ILogger<HdrLutStore> _logger;
    private readonly ConcurrentDictionary<string, Lazy<Task<string>>> _ready = new();
    private readonly object _cleanupLock = new();
    private bool _cleaned;

    public HdrLutStore(string folder, ILogger<HdrLutStore> logger)
    {
        _folder = folder;
        _logger = logger;
    }

    public string Folder => _folder;

    public Task<string> GetLutPathAsync(HdrConversion conversion, CancellationToken ct = default)
    {
        var name = HdrLut.FileName(conversion);
        var lazy = _ready.GetOrAdd(name, _ => new Lazy<Task<string>>(() => Task.Run(() => Prepare(conversion, name))));
        var task = lazy.Value;
        // A failed preparation is not kept: the next request tries again.
        _ = task.ContinueWith(t => _ready.TryRemove(new KeyValuePair<string, Lazy<Task<string>>>(name, lazy)),
            CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return task.WaitAsync(ct);
    }

    private string Prepare(HdrConversion conversion, string name)
    {
        Directory.CreateDirectory(_folder);
        // The clean-up runs once, before any write of this store: a preparation of another key waits for it here, so it
        // never deletes that preparation's temporary file.
        lock (_cleanupLock)
        {
            if (!_cleaned)
            {
                RemoveStale();
                _cleaned = true;
            }
        }

        var path = Path.Combine(_folder, name);
        var bytes = Utf8.GetBytes(HdrLut.Generate(conversion));
        if (File.Exists(path) && SameContent(path, bytes))
            return path;

        var temp = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllBytes(temp, bytes);
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
                TryDelete(temp);
        }
        _logger.LogInformation("HDR → SDR LUT written: {Path}", path);
        return path;
    }

    private static bool SameContent(string path, byte[] bytes)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Length == bytes.Length && File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes);
        }
        catch (IOException)
        {
            return false;
        }
    }

    /// <summary>LUTs of other rule versions and temporary files left by an interrupted write.</summary>
    private void RemoveStale()
    {
        var current = $"{HdrLut.FilePrefix}{HdrToSdr.RuleVersion}-";
        foreach (var file in Directory.EnumerateFiles(_folder, $"{HdrLut.FilePrefix}*"))
        {
            var fileName = Path.GetFileName(file);
            if (!fileName.StartsWith(current, StringComparison.Ordinal) || fileName.EndsWith(".tmp", StringComparison.Ordinal))
                TryDelete(file);
        }
    }

    private void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Could not delete '{Path}'.", path);
        }
    }
}
