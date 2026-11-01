namespace AiVideoEditor.Core.Interfaces;

/// <summary>A file's size and last write time (UTC ticks) — the quick check of Step 35.3b1.</summary>
public readonly record struct FileStamp(long SizeBytes, long LastWriteUtcTicks);

/// <summary>A full fingerprint: the SHA-256 (lower-case hex) and the file's stamp read before and after hashing.</summary>
public sealed record FileFingerprint(string Sha256, FileStamp Before, FileStamp After)
{
    /// <summary>The file changed while it was read: the hash proves nothing.</summary>
    public bool ChangedDuringRead => Before != After;
}

/// <summary>Reads files to identify them (Step 35.3b1): a quick stamp and a streamed SHA-256.</summary>
public interface IFileFingerprintService
{
    /// <summary>The file's stamp, or null when it does not exist.</summary>
    FileStamp? Stamp(string path);

    /// <summary>The file's SHA-256, streamed, with its stamp before and after the read; null when it does not exist.
    /// <see cref="IOException"/> when it cannot be read.</summary>
    Task<FileFingerprint?> HashAsync(string path, IProgress<long>? progress = null, CancellationToken ct = default);
}
