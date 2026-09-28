using System.Globalization;
using AiVideoEditor.Core.Entities;

namespace AiVideoEditor.Media.Caching;

/// <summary>
/// The cache files made from a media asset's source file (D024 Steps 9.4 / 9.5 — thumbnails, waveforms): one file per
/// asset, named <c>{assetId:N}-{size:x}-{lastWriteUtcTicks:x}-v{rule version}{extension}</c>, so a changed file or
/// changed rules are a miss; written to a temporary name and moved into place, the asset's older files removed once
/// the new one is there. Offline media is looked up by the asset id only, the last one written first.
/// </summary>
internal static class SourceFileCache
{
    /// <summary>The source file's identity for the cache — its size and last-write time — or null when the asset is
    /// offline (marked missing, or the file is not there now).</summary>
    public static (long Size, long LastWriteTicks)? Identity(MediaAsset asset)
    {
        if (asset.IsMissing) return null;
        try
        {
            var file = new FileInfo(asset.FilePath);
            return file.Exists ? (file.Length, file.LastWriteTimeUtc.Ticks) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    public static string FileName(MediaAsset asset, (long Size, long LastWriteTicks) identity, int ruleVersion, string extension) =>
        string.Create(CultureInfo.InvariantCulture,
            $"{asset.Id:N}-{identity.Size:x}-{identity.LastWriteTicks:x}-v{ruleVersion}{extension}");

    /// <summary>Offline media: the newest cache file of the asset that <paramref name="read"/> accepts, whatever file
    /// (or rule version) it was made from.</summary>
    public static T? LastCached<T>(MediaAsset asset, string cacheFolder, string extension, Func<string, T?> read) where T : class
    {
        foreach (var path in FilesOf(asset, cacheFolder, extension).OrderByDescending(SafeLastWriteUtc))
        {
            if (read(path) is { } value)
                return value;
        }
        return null;
    }

    /// <summary>Writes <paramref name="bytes"/> to <paramref name="path"/> atomically (temporary file, then a move) and
    /// removes the asset's other files of this kind. Throws <see cref="IOException"/> /
    /// <see cref="UnauthorizedAccessException"/> when the file could not be written (nothing is left behind).</summary>
    public static void Write(MediaAsset asset, string cacheFolder, string path, string extension, byte[] bytes)
    {
        var temp = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            Directory.CreateDirectory(cacheFolder);
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDelete(temp);
            throw;
        }

        foreach (var older in FilesOf(asset, cacheFolder, extension))
        {
            if (!string.Equals(older, path, StringComparison.OrdinalIgnoreCase))
                TryDelete(older);
        }
    }

    private static IEnumerable<string> FilesOf(MediaAsset asset, string cacheFolder, string extension)
    {
        try
        {
            return Directory.Exists(cacheFolder)
                ? Directory.GetFiles(cacheFolder, $"{asset.Id:N}-*{extension}")
                : Array.Empty<string>();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Array.Empty<string>();
        }
    }

    private static DateTime SafeLastWriteUtc(string path)
    {
        try { return File.GetLastWriteTimeUtc(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return DateTime.MinValue; }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
