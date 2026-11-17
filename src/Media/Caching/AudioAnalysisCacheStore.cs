using System.Security.Cryptography;
using System.Text;
using AiVideoEditor.Core.Audio;
using Microsoft.Extensions.Logging;

namespace AiVideoEditor.Media.Caching;

/// <summary>
/// D064 §5: the cleaned sounds (48 kHz stereo float WAVs aligned to the source's sample 0) and the speech analyses kept between
/// sessions — the rules of <see cref="PeopleAnalysisCacheStore"/>: one file per key in a folder of the application's cache
/// (<c>%LOCALAPPDATA%\AiVideoEditor\cache\audio</c>, never the project); the file name is the SHA-256 of the key (the source's
/// content fingerprint, the settings, the algorithm version); a speech file repeats its key and ends with the SHA-256 of its
/// content (a damaged one is deleted and analysed again); written atomically; above the size limit the least recently used files
/// are removed; any failure is logged and treated as "not cached".
/// </summary>
public sealed class AudioAnalysisCacheStore : IAudioAnalysisCache
{
    public const string CleanedExtension = ".wav";
    public const string SpeechExtension = ".vad";

    /// <summary>About 3 h of cleaned sound (≈ 1.4 GB per hour).</summary>
    public const long DefaultMaxBytes = 4L * 1024 * 1024 * 1024;

    private static readonly byte[] SpeechMagic = "AIVESPA1"u8.ToArray();

    private readonly string _folder;
    private readonly ILogger<AudioAnalysisCacheStore> _logger;
    private readonly long _maxBytes;
    private readonly CameraMotionCacheStore _fingerprints;

    public AudioAnalysisCacheStore(string folder, ILogger<AudioAnalysisCacheStore> logger, long maxBytes = DefaultMaxBytes)
    {
        _folder = Path.GetFullPath(folder);
        _logger = logger;
        _maxBytes = maxBytes;
        _fingerprints = new CameraMotionCacheStore(folder, Microsoft.Extensions.Logging.Abstractions.NullLogger<CameraMotionCacheStore>.Instance);
    }

    public string Folder => _folder;

    public string? Fingerprint(string filePath) => _fingerprints.Fingerprint(filePath);

    public string? TryGetCleaned(string key)
    {
        var path = PathOf(key, CleanedExtension);
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return null;
            if (info.Length < 44)
            {
                TryDelete(path);
                return null;
            }
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Audio clean-up: cannot read the cached file {File}.", Path.GetFileName(path));
            return null;
        }
    }

    public string NewCleanedPath(string key)
    {
        Directory.CreateDirectory(_folder);
        return $"{PathOf(key, CleanedExtension)}.{Guid.NewGuid():N}.tmp";
    }

    public string CommitCleaned(string key, string temporaryPath)
    {
        var path = PathOf(key, CleanedExtension);
        File.Move(temporaryPath, path, overwrite: true);
        Prune(except: path);
        return path;
    }

    public SpeechActivity? TryLoadSpeech(string key)
    {
        var path = PathOf(key, SpeechExtension);
        try
        {
            if (!File.Exists(path)) return null;
            var value = Decode(File.ReadAllBytes(path), key);
            if (value is null)
            {
                _logger.LogWarning("Speech analysis: the cached file {File} is damaged or stale; analysed again.", Path.GetFileName(path));
                TryDelete(path);
                return null;
            }
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
            return value;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Speech analysis: cannot read the cached file {File}.", Path.GetFileName(path));
            return null;
        }
    }

    public void SaveSpeech(string key, SpeechActivity activity)
    {
        var path = PathOf(key, SpeechExtension);
        var temp = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            Directory.CreateDirectory(_folder);
            File.WriteAllBytes(temp, Encode(activity, key));
            File.Move(temp, path, overwrite: true);
            Prune(except: path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Speech analysis: cannot write the cached file {File}.", Path.GetFileName(path));
        }
        finally
        {
            if (File.Exists(temp)) TryDelete(temp);
        }
    }

    private string PathOf(string key, string extension) =>
        Path.Combine(_folder, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant() + extension);

    /// <summary>Magic, key, count, the probabilities, then the SHA-256 of everything before it.</summary>
    internal static byte[] Encode(SpeechActivity activity, string key)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(SpeechMagic);
            writer.Write(key);
            writer.Write(activity.Probabilities.Length);
            foreach (var p in activity.Probabilities) writer.Write(p);
        }
        var body = stream.ToArray();
        return body.Concat(SHA256.HashData(body)).ToArray();
    }

    internal static SpeechActivity? Decode(byte[] bytes, string key)
    {
        if (bytes.Length < SpeechMagic.Length + 32) return null;
        var body = bytes.AsSpan(0, bytes.Length - 32);
        if (!SHA256.HashData(body).AsSpan().SequenceEqual(bytes.AsSpan(bytes.Length - 32))) return null;
        try
        {
            using var reader = new BinaryReader(new MemoryStream(bytes, 0, bytes.Length - 32), Encoding.UTF8);
            if (!reader.ReadBytes(SpeechMagic.Length).AsSpan().SequenceEqual(SpeechMagic)) return null;
            if (reader.ReadString() != key) return null;
            var count = reader.ReadInt32();
            if (count < 0 || count > 100_000_000) return null;
            var values = new float[count];
            for (var i = 0; i < count; i++) values[i] = reader.ReadSingle();
            return new SpeechActivity(values);
        }
        catch (EndOfStreamException)
        {
            return null;
        }
    }

    private void Prune(string except)
    {
        try
        {
            var files = new DirectoryInfo(_folder).GetFiles()
                .Where(f => f.Extension is CleanedExtension or SpeechExtension)
                .OrderByDescending(f => f.LastWriteTimeUtc).ToList();
            long total = 0;
            foreach (var file in files)
            {
                total += file.Length;
                if (total > _maxBytes && !string.Equals(file.FullName, except, StringComparison.OrdinalIgnoreCase)) TryDelete(file.FullName);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Audio cache: cannot prune {Folder}.", _folder);
        }
    }

    private void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _logger.LogDebug(ex, "Audio cache: {File} not deleted (in use).", Path.GetFileName(path)); }
    }
}
