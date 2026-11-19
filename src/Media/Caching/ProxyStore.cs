using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using AiVideoEditor.Core.Color;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Core.Playback;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiVideoEditor.Media.Caching;

/// <summary>
/// D071 §3: the Preview's proxies, one MP4 per source content (1080p, amendment 2) in one folder of the application's cache
/// (<c>%LOCALAPPDATA%\BoulderVideoEditor\cache\proxies</c>):
/// <list type="bullet">
/// <item>The name is a hash of the source's content (<see cref="CameraMotionCacheStore.Fingerprint"/>: size, last write
/// time, its first and last megabyte), its colour and size and <see cref="ProxyVersion"/> — an edited, replaced or relinked file is
/// another key; one file in two projects has one proxy.</item>
/// <item>Made into a temporary file and moved into place only when the builder checked it; one making per key at a time.</item>
/// <item>Above <see cref="DefaultMaxBytes"/> (or the given limit) the least recently used proxies are removed.</item>
/// </list>
/// </summary>
public sealed class ProxyStore : IProxyStore
{
    /// <summary>The proxies' rules (D071 §3); a change makes every proxy another key.</summary>
    public const int ProxyVersion = 2;   // 2: one 1080p proxy per file (D071 amendment 2)
    public const string Extension = ".mp4";
    public const long DefaultMaxBytes = 50L * 1024 * 1024 * 1024;
    private const string TempMarker = ".tmp";

    private readonly string _folder;
    private readonly IProxyBuilder _builder;
    private readonly ILogger<ProxyStore> _logger;
    private readonly long _maxBytes;
    private readonly CameraMotionCacheStore _fingerprints;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _making = new(StringComparer.OrdinalIgnoreCase);

    public ProxyStore(string folder, IProxyBuilder builder, ILogger<ProxyStore> logger, long maxBytes = DefaultMaxBytes)
    {
        _folder = Path.GetFullPath(folder);
        _builder = builder;
        _logger = logger;
        _maxBytes = maxBytes;
        _fingerprints = new CameraMotionCacheStore(_folder, NullLogger<CameraMotionCacheStore>.Instance);
    }

    public string Folder => _folder;

    public string? TryGet(MediaAsset asset)
    {
        if (PathOf(asset) is not { } path || !File.Exists(path)) return null;
        Touch(path);
        return path;
    }

    public async Task<string?> GetOrCreateAsync(MediaAsset asset, IProgress<double>? progress, CancellationToken ct)
    {
        if (!PreviewDecoding.NeedsProxy(asset) || asset.Metadata is not { } m) return null;
        if (PathOf(asset) is not { } path) return null;
        var gate = _making.GetOrAdd(path, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            if (File.Exists(path))
            {
                Touch(path);
                return path;
            }
            Directory.CreateDirectory(_folder);
            RemoveStaleTemporaryFiles();
            var temp = Path.Combine(_folder, $"{Path.GetFileNameWithoutExtension(path)}.{Guid.NewGuid():N}{TempMarker}{Extension}");
            var width = m.DisplayWidth ?? m.Width ?? 0;
            var height = m.DisplayHeight ?? m.Height ?? 0;
            await _builder.BuildAsync(new ProxyBuildRequest
            {
                SourcePath = asset.FilePath,
                OutputPath = temp,
                Size = PreviewDecoding.ProxySize(width, height),
                Duration = asset.Metadata.Duration,
                Hdr = HdrColor.Classify(asset.Kind, asset.Metadata).Conversion
            }, progress, ct);
            try
            {
                File.Move(temp, path, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                TryDelete(temp);
                _logger.LogWarning(ex, "The proxy of '{Path}' could not be stored.", asset.FilePath);
                return null;
            }
            _logger.LogInformation("Proxy of '{Path}' ready: {Proxy}.", asset.FilePath, Path.GetFileName(path));
            Trim(path);
            return path;
        }
        finally
        {
            gate.Release();
        }
    }

    public long SizeBytes() => Files().Sum(f => Length(f));

    public long Clear()
    {
        long freed = 0;
        foreach (var file in Files())
        {
            var length = Length(file);
            if (TryDelete(file)) freed += length;
        }
        return freed;
    }

    /// <summary>The proxy's file name for <paramref name="asset"/>'s content now, or null when the file can't be read.</summary>
    internal string? PathOf(MediaAsset asset)
    {
        if (asset.IsMissing || _fingerprints.Fingerprint(asset.FilePath) is not { } fingerprint) return null;
        // The picture's conversion is part of the key (D033): a proxy made as SDR is never taken for the file found HDR later.
        var colour = HdrColor.Classify(asset.Kind, asset.Metadata).Conversion is { } hdr
            ? string.Create(CultureInfo.InvariantCulture, $"{hdr.Transfer}-{hdr.PeakNits}-{(hdr.FullRange ? "full" : "limited")}")
            : "sdr";
        // So is the picture's size: metadata without a display size (before Phase 7) refreshed later gives another proxy.
        var m = asset.Metadata;
        var (w, h) = (m?.DisplayWidth ?? m?.Width ?? 0, m?.DisplayHeight ?? m?.Height ?? 0);
        var size = w > 0 && h > 0 ? PreviewDecoding.ProxySize(w, h) : default;
        var key = string.Create(CultureInfo.InvariantCulture, $"{fingerprint}|{colour}|{size.Width}x{size.Height}|v{ProxyVersion}");
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..32].ToLowerInvariant();
        return Path.Combine(_folder, $"{hash}-{size.Height}p{Extension}");
    }

    /// <summary>A temporary file older than <see cref="StaleTemporaryAge"/> is a making that never ended (the app was killed,
    /// the machine stopped): removed. A younger one may be another running copy's, kept.</summary>
    private void RemoveStaleTemporaryFiles()
    {
        foreach (var file in Files().Where(f => Path.GetFileName(f).Contains(TempMarker, StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                if (DateTime.UtcNow - File.GetLastWriteTimeUtc(file) > StaleTemporaryAge) TryDelete(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    internal static readonly TimeSpan StaleTemporaryAge = TimeSpan.FromHours(12);

    /// <summary>Removes the least recently used proxies above the limit, never <paramref name="keep"/>.</summary>
    private void Trim(string keep)
    {
        var files = Files().Where(f => !Path.GetFileName(f).Contains(TempMarker, StringComparison.OrdinalIgnoreCase))
            .Select(f => new FileInfo(f)).Where(f => f.Exists).OrderBy(f => f.LastWriteTimeUtc).ToList();
        var total = files.Sum(f => f.Length);
        foreach (var file in files)
        {
            if (total <= _maxBytes) break;
            if (string.Equals(file.FullName, keep, StringComparison.OrdinalIgnoreCase)) continue;
            if (TryDelete(file.FullName)) total -= file.Length;
        }
    }

    private IEnumerable<string> Files()
    {
        try
        {
            return Directory.Exists(_folder) ? Directory.GetFiles(_folder, "*" + Extension) : Array.Empty<string>();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Array.Empty<string>();
        }
    }

    private static long Length(string path)
    {
        try { return new FileInfo(path).Length; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return 0; }
    }

    /// <summary>The proxy was used: the least recently used ones go first (<see cref="Trim"/>).</summary>
    private static void Touch(string path)
    {
        try { File.SetLastWriteTimeUtc(path, DateTime.UtcNow); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static bool TryDelete(string path)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;   // being read by the Preview or made now
        }
    }
}
