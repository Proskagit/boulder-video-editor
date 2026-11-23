using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Core.People;
using Microsoft.Extensions.Logging;

namespace AiVideoEditor.Media.Caching;

/// <summary>
/// The people analyses kept between sessions (D061 §5) — the same rules as <see cref="CameraMotionCacheStore"/>: one file
/// per analysis in a folder of the application's cache (<c>%LOCALAPPDATA%\BoulderVideoEditor\cache\people</c>, never the
/// project); the file name is the SHA-256 of the key, the file repeats the key and ends with the SHA-256 of its content (a
/// collision, a truncated or a damaged file is never read — it is deleted and the video analysed again); written atomically;
/// above the size limit the least recently used files are removed; any failure is logged and treated as "not cached".
/// <para>The key names the video's content (<see cref="Fingerprint"/>), the model set, the analysis version and its
/// parameters: an edited, replaced or relinked file, another model or another analysis is another key.</para>
/// </summary>
public sealed class PeopleAnalysisCacheStore : IPeopleAnalysisCache
{
    public const string TracksExtension = ".ppt";
    public const string MasksExtension = ".ppm";

    /// <summary>The masks are the bulk (≈ 1–3 MB per minute); 2 GB keeps dozens of hours.</summary>
    public const long DefaultMaxBytes = 2L * 1024 * 1024 * 1024;

    private static readonly byte[] TracksMagic = "AIVEPPT2"u8.ToArray();
    private static readonly byte[] MasksMagic = "AIVEPPM1"u8.ToArray();

    private readonly string _folder;
    private readonly ILogger<PeopleAnalysisCacheStore> _logger;
    private readonly long _maxBytes;
    private readonly CameraMotionCacheStore _fingerprints;

    public PeopleAnalysisCacheStore(string folder, ILogger<PeopleAnalysisCacheStore> logger, long maxBytes = DefaultMaxBytes)
    {
        _folder = Path.GetFullPath(folder);
        _logger = logger;
        _maxBytes = maxBytes;
        _fingerprints = new CameraMotionCacheStore(folder, Microsoft.Extensions.Logging.Abstractions.NullLogger<CameraMotionCacheStore>.Instance);
    }

    public string Folder => _folder;

    /// <summary>The same content identity as the camera-motion cache (size, last write time, SHA-256 of the head and tail).</summary>
    public string? Fingerprint(string filePath) => _fingerprints.Fingerprint(filePath);

    public PersonTracks? TryLoadTracks(string key) => Load(key, TracksExtension, bytes => DecodeTracks(bytes, key));

    public void SaveTracks(string key, PersonTracks tracks) => Save(key, TracksExtension, EncodeTracks(tracks, key));

    public PersonMaskTrack? TryLoadMasks(string key) => Load(key, MasksExtension, bytes => DecodeMasks(bytes, key));

    public void SaveMasks(string key, PersonMaskTrack masks) => Save(key, MasksExtension, EncodeMasks(masks, key));

    private T? Load<T>(string key, string extension, Func<byte[], T?> decode) where T : class
    {
        var path = PathOf(key, extension);
        try
        {
            if (!File.Exists(path)) return null;
            var value = decode(File.ReadAllBytes(path));
            if (value is null)
            {
                _logger.LogWarning("People analysis: the cached file {File} is damaged or stale; the video is analysed again.", Path.GetFileName(path));
                TryDelete(path);
                return null;
            }
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
            return value;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "People analysis: cannot read the cached file {File}.", Path.GetFileName(path));
            return null;
        }
    }

    private void Save(string key, string extension, byte[] bytes)
    {
        var path = PathOf(key, extension);
        var temp = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            Directory.CreateDirectory(_folder);
            File.WriteAllBytes(temp, bytes);
            File.Move(temp, path, overwrite: true);
            Prune(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "People analysis: cannot write the cached file {File}.", Path.GetFileName(path));
        }
        finally
        {
            if (File.Exists(temp)) TryDelete(temp);
        }
    }

    private string PathOf(string key, string extension) =>
        Path.Combine(_folder, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant() + extension);

    /// <summary>Magic, key, model id, interval, the samples (time; per person: box, score, torso, appearance, keypoints, track id), then
    /// the SHA-256 of everything before it.</summary>
    internal static byte[] EncodeTracks(PersonTracks tracks, string key) => Sealed(writer =>
    {
        writer.Write(TracksMagic);
        writer.Write(key);
        writer.Write(tracks.ModelId);
        writer.Write(tracks.Interval.Ticks);
        writer.Write(tracks.Samples.Length);
        foreach (var sample in tracks.Samples)
        {
            writer.Write(sample.Time.Ticks);
            writer.Write(sample.People.Length);
            for (var i = 0; i < sample.People.Length; i++)
            {
                var p = sample.People[i];
                writer.Write(p.Box.X0); writer.Write(p.Box.Y0); writer.Write(p.Box.X1); writer.Write(p.Box.Y1);
                writer.Write(p.Score);
                writer.Write(p.Torso is not null);
                if (p.Torso is { } t) { writer.Write(t.X); writer.Write(t.Y); }
                writer.Write(p.Appearance.IsDefault ? 0 : p.Appearance.Length);
                if (!p.Appearance.IsDefault) foreach (var v in p.Appearance) writer.Write(v);
                writer.Write(p.Keypoints.IsDefault ? 0 : p.Keypoints.Length);   // D078 §3 (analysis v3)
                if (!p.Keypoints.IsDefault) foreach (var v in p.Keypoints) writer.Write(v);
                writer.Write(sample.TrackIdOf(i));
            }
        }
    });

    internal static PersonTracks? DecodeTracks(byte[] bytes, string key) => Unsealed(bytes, TracksMagic, key, reader =>
    {
        var model = reader.ReadString();
        var interval = new MediaTime(reader.ReadInt64());
        var count = reader.ReadInt32();
        if (count < 0 || count > 10_000_000) return null;
        var samples = ImmutableArray.CreateBuilder<PersonSample>(count);
        for (var s = 0; s < count; s++)
        {
            var time = new MediaTime(reader.ReadInt64());
            var people = reader.ReadInt32();
            if (people < 0 || people > 10_000) return null;
            var list = ImmutableArray.CreateBuilder<PersonDetection>(people);
            var ids = ImmutableArray.CreateBuilder<int>(people);
            for (var i = 0; i < people; i++)
            {
                var box = new NormalizedBox(reader.ReadDouble(), reader.ReadDouble(), reader.ReadDouble(), reader.ReadDouble());
                var score = reader.ReadSingle();
                NormalizedPoint? torso = reader.ReadBoolean() ? new NormalizedPoint(reader.ReadDouble(), reader.ReadDouble()) : null;
                var n = reader.ReadInt32();
                if (n < 0 || n > 4096) return null;
                var appearance = new float[n];
                for (var k = 0; k < n; k++) appearance[k] = reader.ReadSingle();
                var m = reader.ReadInt32();
                if (m < 0 || m > PersonDetection.KeypointCount * 3) return null;
                var keypoints = new float[m];
                for (var k = 0; k < m; k++) keypoints[k] = reader.ReadSingle();
                list.Add(new PersonDetection(box, score, torso, ImmutableArray.Create(appearance)) { Keypoints = ImmutableArray.Create(keypoints) });
                ids.Add(reader.ReadInt32());
            }
            samples.Add(new PersonSample(time, list.MoveToImmutable(), ids.MoveToImmutable()));
        }
        return PersonTracks.Of(interval, samples.MoveToImmutable(), model);
    });

    /// <summary>Magic, key, model id, the grid, interval, the samples (time; per person: id, rectangle, deflated coverage),
    /// then the SHA-256.</summary>
    internal static byte[] EncodeMasks(PersonMaskTrack masks, string key) => Sealed(writer =>
    {
        writer.Write(MasksMagic);
        writer.Write(key);
        writer.Write(masks.ModelId);
        writer.Write(masks.Width);
        writer.Write(masks.Height);
        writer.Write(masks.Interval.Ticks);
        writer.Write(masks.Samples.Length);
        foreach (var sample in masks.Samples)
        {
            writer.Write(sample.Time.Ticks);
            writer.Write(sample.People.Length);
            foreach (var p in sample.People)
            {
                writer.Write(p.PersonId);
                writer.Write(p.X); writer.Write(p.Y); writer.Write(p.Width); writer.Write(p.Height);
                writer.Write(p.Deflated.Length);
                writer.Write(p.Deflated);
            }
        }
    });

    internal static PersonMaskTrack? DecodeMasks(byte[] bytes, string key) => Unsealed(bytes, MasksMagic, key, reader =>
    {
        var model = reader.ReadString();
        var width = reader.ReadInt32();
        var height = reader.ReadInt32();
        if (width <= 0 || height <= 0 || width > 8192 || height > 8192) return null;
        var interval = new MediaTime(reader.ReadInt64());
        var count = reader.ReadInt32();
        if (count < 0 || count > 10_000_000) return null;
        var samples = ImmutableArray.CreateBuilder<PersonMaskSample>(count);
        for (var s = 0; s < count; s++)
        {
            var time = new MediaTime(reader.ReadInt64());
            var people = reader.ReadInt32();
            if (people < 0 || people > 1000) return null;
            var list = ImmutableArray.CreateBuilder<PersonMaskEntry>(people);
            for (var i = 0; i < people; i++)
            {
                var id = reader.ReadInt32();
                int x = reader.ReadInt32(), y = reader.ReadInt32(), w = reader.ReadInt32(), h = reader.ReadInt32();
                if (x < 0 || y < 0 || w <= 0 || h <= 0 || x + w > width || y + h > height) return null;
                var n = reader.ReadInt32();
                if (n < 0 || n > width * height * 2 + 1024) return null;
                list.Add(new PersonMaskEntry(id, x, y, w, h, reader.ReadBytes(n)));
            }
            samples.Add(new PersonMaskSample(time, list.MoveToImmutable()));
        }
        return new PersonMaskTrack(width, height, interval, samples.MoveToImmutable(), model);
    });

    private static byte[] Sealed(Action<BinaryWriter> write)
    {
        using var memory = new MemoryStream();
        using (var writer = new BinaryWriter(memory, Encoding.UTF8, leaveOpen: true)) write(writer);
        var body = memory.ToArray();
        return [.. body, .. SHA256.HashData(body)];
    }

    private static T? Unsealed<T>(byte[] bytes, byte[] magic, string key, Func<BinaryReader, T?> read) where T : class
    {
        if (bytes.Length < magic.Length + 32) return null;
        var body = bytes.AsSpan(0, bytes.Length - 32);
        if (!SHA256.HashData(body).AsSpan().SequenceEqual(bytes.AsSpan(bytes.Length - 32))) return null;
        try
        {
            using var reader = new BinaryReader(new MemoryStream(bytes, 0, body.Length), Encoding.UTF8);
            if (!reader.ReadBytes(magic.Length).AsSpan().SequenceEqual(magic) || reader.ReadString() != key) return null;
            var value = read(reader);
            return reader.BaseStream.Position == body.Length ? value : null;
        }
        catch (Exception ex) when (ex is EndOfStreamException or ArgumentException or FormatException or InvalidDataException)
        {
            return null;
        }
    }

    /// <summary>Keeps the folder under its limit, the least recently used first out — never <paramref name="saved"/>, the file
    /// just written (files written within the clock's resolution share a write time: the order alone could drop it).</summary>
    private void Prune(string saved)
    {
        var files = new DirectoryInfo(_folder).GetFiles("*.pp?")
            .OrderByDescending(f => string.Equals(f.FullName, Path.GetFullPath(saved), StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(f => f.LastWriteTimeUtc).ToList();
        long total = 0;
        foreach (var file in files)
        {
            total += file.Length;
            if (total > _maxBytes) TryDelete(file.FullName);
        }
    }

    private void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "People analysis: could not delete {File}.", path);
        }
    }
}
