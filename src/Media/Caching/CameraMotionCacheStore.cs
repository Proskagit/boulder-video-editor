using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Core.Motion;
using Microsoft.Extensions.Logging;

namespace AiVideoEditor.Media.Caching;

/// <summary>
/// The camera-motion tracks of the hold tracking kept between sessions (R2 → E, D4): one file per analysis in one folder
/// of the application's cache (<c>%LOCALAPPDATA%\AiVideoEditor\cache\camera-motion</c>, not the project — like the HDR LUTs).
/// <list type="bullet">
/// <item>The file name is the SHA-256 of the key; the file repeats the key and ends with the SHA-256 of its content, so a
/// collision, a truncated or a damaged file is never read as a track — it is deleted and the clip analysed again.</item>
/// <item>The key names the video's content (<see cref="Fingerprint"/>: size, last write time, a hash of the first and the
/// last megabyte), the request and <see cref="CameraMotionSampler.AnalysisVersion"/>: an edited, replaced or relinked file,
/// another range or reference frame, or a changed analysis is another key — a stale track is never found.</item>
/// <item>Written atomically (a temporary file, then a move). Above <see cref="DefaultMaxBytes"/> (or the given limit) the
/// least recently used files are removed.</item>
/// <item>Any failure is logged and treated as "not cached": the track is analysed again; nothing is thrown.</item>
/// </list>
/// </summary>
public sealed class CameraMotionCacheStore : ICameraMotionCache
{
    public const string Extension = ".cmt";
    public const long DefaultMaxBytes = 512L * 1024 * 1024;
    private const int FingerprintChunk = 1024 * 1024;
    private static readonly byte[] Magic = "AIVECMT1"u8.ToArray();

    private readonly string _folder;
    private readonly ILogger<CameraMotionCacheStore> _logger;
    private readonly long _maxBytes;

    public CameraMotionCacheStore(string folder, ILogger<CameraMotionCacheStore> logger, long maxBytes = DefaultMaxBytes)
    {
        _folder = Path.GetFullPath(folder);
        _logger = logger;
        _maxBytes = maxBytes;
    }

    public string Folder => _folder;

    public string? Fingerprint(string filePath)
    {
        try
        {
            var info = new FileInfo(filePath);
            if (!info.Exists) return null;
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[FingerprintChunk];
            sha.AppendData(buffer, 0, ReadFully(stream, buffer));
            if (info.Length > FingerprintChunk)
            {
                stream.Seek(Math.Max(FingerprintChunk, info.Length - FingerprintChunk), SeekOrigin.Begin);
                sha.AppendData(buffer, 0, ReadFully(stream, buffer));
            }
            return $"{info.Length}:{info.LastWriteTimeUtc.Ticks}:{Convert.ToHexString(sha.GetHashAndReset())}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Hold tracking: cannot read {File} for the cache key.", filePath);
            return null;
        }
    }

    public CameraMotionTrack? TryLoad(string key)
    {
        var path = PathOf(key);
        try
        {
            if (!File.Exists(path)) return null;
            var bytes = File.ReadAllBytes(path);
            var track = Decode(bytes, key);
            if (track is null)
            {
                _logger.LogWarning("Hold tracking: the cached track {File} is damaged or stale; it is analysed again.", Path.GetFileName(path));
                TryDelete(path);
                return null;
            }
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow);   // recently used: kept longest
            return track;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Hold tracking: cannot read the cached track {File}.", Path.GetFileName(path));
            return null;
        }
    }

    public void Save(string key, CameraMotionTrack track)
    {
        var path = PathOf(key);
        var temp = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            Directory.CreateDirectory(_folder);
            File.WriteAllBytes(temp, Encode(track, key));
            File.Move(temp, path, overwrite: true);
            Prune();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Hold tracking: cannot write the cached track {File}.", Path.GetFileName(path));
        }
        finally
        {
            if (File.Exists(temp)) TryDelete(temp);
        }
    }

    private string PathOf(string key) =>
        Path.Combine(_folder, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant() + Extension);

    /// <summary>The format: magic, the key (UTF-8, length-prefixed), the model, the gap and edge tolerances, the reference
    /// time, the samples (time, 9 coefficients, reliability), then the SHA-256 of everything before it.</summary>
    internal static byte[] Encode(CameraMotionTrack track, string key)
    {
        using var memory = new MemoryStream();
        using (var writer = new BinaryWriter(memory, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(Magic);
            writer.Write(key);
            writer.Write((byte)track.Model);
            writer.Write(track.MaxInterpolationGap.Ticks);
            writer.Write(track.EdgeTolerance.Ticks);
            writer.Write(track.ReferenceTime.Ticks);
            writer.Write(track.Samples.Length);
            foreach (var s in track.Samples)
            {
                writer.Write(s.SourceTime.Ticks);
                var m = s.ReferenceToFrame;
                foreach (var v in new[] { m.M00, m.M01, m.M02, m.M10, m.M11, m.M12, m.M20, m.M21, m.M22 }) writer.Write(v);
                writer.Write(s.IsReliable);
            }
        }
        var body = memory.ToArray();
        return [.. body, .. SHA256.HashData(body)];
    }

    internal static CameraMotionTrack? Decode(byte[] bytes, string key)
    {
        if (bytes.Length < Magic.Length + 32) return null;
        var body = bytes.AsSpan(0, bytes.Length - 32);
        if (!SHA256.HashData(body).AsSpan().SequenceEqual(bytes.AsSpan(bytes.Length - 32))) return null;
        try
        {
            using var reader = new BinaryReader(new MemoryStream(bytes, 0, body.Length), Encoding.UTF8);
            if (!reader.ReadBytes(Magic.Length).AsSpan().SequenceEqual(Magic) || reader.ReadString() != key) return null;
            var model = (CameraMotionModel)reader.ReadByte();
            if (!Enum.IsDefined(model)) return null;
            var gap = new MediaTime(reader.ReadInt64());
            var edge = new MediaTime(reader.ReadInt64());
            var reference = new MediaTime(reader.ReadInt64());
            var count = reader.ReadInt32();
            if (count < 0 || count > 10_000_000) return null;
            var samples = ImmutableArray.CreateBuilder<CameraMotionSample>(count);
            for (var i = 0; i < count; i++)
            {
                var time = new MediaTime(reader.ReadInt64());
                var c = new double[9];
                for (var j = 0; j < 9; j++) c[j] = reader.ReadDouble();
                samples.Add(new CameraMotionSample(time, new WallTransform(c[0], c[1], c[2], c[3], c[4], c[5], c[6], c[7], c[8]), reader.ReadBoolean()));
            }
            if (reader.BaseStream.Position != body.Length) return null;
            return new CameraMotionTrack(reference, model, samples.MoveToImmutable(), gap, edge);
        }
        catch (Exception ex) when (ex is EndOfStreamException or ArgumentException or FormatException)
        {
            return null;
        }
    }

    /// <summary>Above the limit, the least recently used files go first.</summary>
    private void Prune()
    {
        var files = new DirectoryInfo(_folder).GetFiles("*" + Extension).OrderByDescending(f => f.LastWriteTimeUtc).ToList();
        long total = 0;
        foreach (var file in files)
        {
            total += file.Length;
            if (total > _maxBytes) TryDelete(file.FullName);
        }
    }

    private static int ReadFully(Stream stream, byte[] buffer)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = stream.Read(buffer, read, buffer.Length - read);
            if (n == 0) break;
            read += n;
        }
        return read;
    }

    private void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Hold tracking: could not delete {File}.", path);
        }
    }
}
