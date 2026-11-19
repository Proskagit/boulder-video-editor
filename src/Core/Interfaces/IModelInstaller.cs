namespace AiVideoEditor.Core.Interfaces;

/// <summary>The progress of a models installation: bytes read so far of <see cref="TotalBytes"/> (copied or checked).</summary>
public sealed record ModelInstallProgress(long DoneBytes, long TotalBytes, string? CurrentFile)
{
    public double Percent => TotalBytes <= 0 ? 100 : Math.Clamp(DoneBytes * 100.0 / TotalBytes, 0, 100);
}

/// <summary>How an installation ended: files copied, files already in place (same size and SHA-256).</summary>
public sealed record ModelInstallResult(int Installed, int Kept);

/// <summary>The models folder's state by a quick look (every pinned file present with its size; the SHA-256 is checked by
/// the features before their first use and by the installer).</summary>
public sealed record ModelInstallState(IReadOnlyList<string> MissingFiles)
{
    public bool IsComplete => MissingFiles.Count == 0;
}

/// <summary>Why an installation could not finish; the message is the user's (localized).</summary>
public sealed class ModelInstallException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// D070 §3: installs the pinned AI models from a local models package (the Portable folder's <c>ai-models</c>) into the
/// models folder — never from the network (Vision's <c>ModelPackageInstaller</c>).
/// </summary>
public interface IModelInstaller
{
    /// <summary>The size of a complete installation, in bytes.</summary>
    long TotalBytes { get; }

    /// <summary>Every pinned file present with its size (no hashing: quick).</summary>
    ModelInstallState Check(string modelsRoot);

    /// <summary>Copies every pinned file not already in place (same size and SHA-256) from <paramref name="packageRoot"/>.</summary>
    /// <exception cref="ModelInstallException">The package lacks or has a damaged file, the disk is full, a file can't be written.</exception>
    /// <exception cref="OperationCanceledException">Cancelled; no partial file is left.</exception>
    Task<ModelInstallResult> InstallAsync(string packageRoot, string modelsRoot, IProgress<ModelInstallProgress>? progress,
        CancellationToken cancellationToken);
}
