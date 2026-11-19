using System.Security.Cryptography;
using AiVideoEditor.Core.Interfaces;

namespace AiVideoEditor.Vision.Installation;

/// <summary>
/// D070 §3: installs the models of the Portable package (<see cref="ModelCatalog.PackageFolderName"/>) into the models folder
/// — only from local files, never from the network. Every pinned file is taken from the package, copied into a temporary file
/// next to its place while its SHA-256 is computed, and moved into place only when its size and SHA-256 are the pinned ones;
/// a file already in place with them is kept. Nothing else in the models folder is touched (other folders, older models);
/// a cancelled or failed copy leaves no partial file. Running it again repairs what is missing or damaged.
/// </summary>
public sealed class ModelPackageInstaller : IModelInstaller
{
    private const int BufferSize = 1 << 20;
    private const string TemporarySuffix = ".installing";

    /// <summary>Free space kept beyond the files themselves.</summary>
    private const long SpaceMargin = 64L << 20;

    private readonly IReadOnlyList<ModelFolder> _folders;
    private readonly Func<string, long?> _freeSpace;

    public ModelPackageInstaller() : this(ModelCatalog.Folders, FreeSpaceOf)
    {
    }

    /// <summary>For tests: other pinned files (small fakes) and another free-space reading.</summary>
    internal ModelPackageInstaller(IReadOnlyList<ModelFolder> folders, Func<string, long?> freeSpace)
    {
        _folders = folders;
        _freeSpace = freeSpace;
    }

    public long TotalBytes => ModelCatalog.TotalBytes(_folders);

    /// <summary>The package in <paramref name="appFolder"/> (<c>ai-models\models.json</c>), or null.</summary>
    public static string? FindPackage(string appFolder)
    {
        try
        {
            var package = Path.Combine(appFolder, ModelCatalog.PackageFolderName);
            return File.Exists(Path.Combine(package, ModelCatalog.ManifestFileName)) ? Path.GetFullPath(package) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>Every pinned file present in <paramref name="modelsRoot"/> with its size (no hashing: quick, for the start).</summary>
    public ModelInstallState Check(string modelsRoot)
    {
        var missing = new List<string>();
        foreach (var folder in _folders)
            foreach (var file in folder.Files)
                if (LengthOf(Path.Combine(modelsRoot, folder.Folder, file.Name)) != file.Length)
                    missing.Add($"{folder.Folder}\\{file.Name}");
        return new ModelInstallState(missing);
    }

    /// <summary>Installs every pinned file from <paramref name="packageRoot"/> into <paramref name="modelsRoot"/>.</summary>
    /// <exception cref="ModelInstallException">The package lacks or has a damaged file, the disk is full, a file can't be
    /// written.</exception>
    /// <exception cref="OperationCanceledException">Cancelled; no partial file is left.</exception>
    public async Task<ModelInstallResult> InstallAsync(string packageRoot, string modelsRoot, IProgress<ModelInstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        var sourceRoot = Path.Combine(packageRoot, ModelCatalog.PackageModelsFolderName);
        var work = new List<(string Folder, HoldModelFile File, string Source, string Target, bool CheckFirst)>();
        long total = 0, toCopy = 0;
        foreach (var folder in _folders)
        {
            foreach (var file in folder.Files)
            {
                var source = Path.Combine(sourceRoot, folder.Folder, file.Name);
                if (LengthOf(source) != file.Length)
                    throw new ModelInstallException(string.Format(Core.Localization.Messages.ModelInstall_PackageDamaged,
                        $"{folder.Folder}\\{file.Name}", packageRoot));
                var target = Path.Combine(modelsRoot, folder.Folder, file.Name);
                var checkFirst = LengthOf(target) == file.Length;   // probably in place: its SHA-256 decides
                work.Add((folder.Folder, file, source, target, checkFirst));
                total += file.Length + (checkFirst ? file.Length : 0);
                if (!checkFirst) toCopy += file.Length;
            }
        }

        try
        {
            Directory.CreateDirectory(modelsRoot);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ModelInstallException(string.Format(Core.Localization.Messages.ModelInstall_WriteFailed, modelsRoot, ex.Message), ex);
        }
        if (_freeSpace(modelsRoot) is { } free && free < toCopy + SpaceMargin)
            throw new ModelInstallException(string.Format(Core.Localization.Messages.ModelInstall_NoSpace,
                Megabytes(toCopy + SpaceMargin), Megabytes(free), modelsRoot));

        long done = 0;
        int installed = 0, kept = 0;
        var report = new Action<long, string>((bytes, name) =>
        {
            done += bytes;
            progress?.Report(new ModelInstallProgress(done, total, name));
        });
        progress?.Report(new ModelInstallProgress(0, total, null));

        foreach (var item in work)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = $"{item.Folder}\\{item.File.Name}";
            if (item.CheckFirst)
            {
                var hash = await HashAsync(item.Target, b => report(b, name), cancellationToken).ConfigureAwait(false);
                if (hash == item.File.Sha256)
                {
                    kept++;
                    report(item.File.Length, name);   // the copy it needs no more
                    continue;
                }
            }
            await CopyAsync(item.Source, item.Target, item.File, name, packageRoot, b => report(b, name), cancellationToken).ConfigureAwait(false);
            installed++;
        }
        return new ModelInstallResult(installed, kept);
    }

    private static async Task CopyAsync(string source, string target, HoldModelFile file, string name, string packageRoot,
        Action<long> advanced, CancellationToken cancellationToken)
    {
        var folder = Path.GetDirectoryName(target)!;
        string temporary;
        try
        {
            Directory.CreateDirectory(folder);
            RemoveLeftovers(folder, file.Name);
            temporary = Path.Combine(folder, $"{file.Name}.{Guid.NewGuid():N}{TemporarySuffix}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ModelInstallException(string.Format(Core.Localization.Messages.ModelInstall_WriteFailed, folder, ex.Message), ex);
        }

        try
        {
            string hash;
            FileStream input;
            try
            {
                input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, useAsync: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // the package's file can't be read: the package, not the models folder, is at fault
                throw new ModelInstallException(string.Format(Core.Localization.Messages.ModelInstall_PackageDamaged, name, packageRoot), ex);
            }
            try
            {
                await using (input)
                await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize, useAsync: true))
                {
                    using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    var buffer = new byte[BufferSize];
                    int read;
                    while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                    {
                        sha.AppendData(buffer, 0, read);
                        await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                        advanced(read);
                    }
                    await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                    hash = Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
                }
            }
            catch (IOException ex) when (ex is not FileNotFoundException and not DirectoryNotFoundException)
            {
                throw new ModelInstallException(string.Format(Core.Localization.Messages.ModelInstall_WriteFailed, folder, ex.Message), ex);
            }
            catch (UnauthorizedAccessException ex)
            {
                throw new ModelInstallException(string.Format(Core.Localization.Messages.ModelInstall_WriteFailed, folder, ex.Message), ex);
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                throw new ModelInstallException(string.Format(Core.Localization.Messages.ModelInstall_PackageDamaged, name, packageRoot), ex);
            }

            if (hash != file.Sha256 || new FileInfo(temporary).Length != file.Length)
                throw new ModelInstallException(string.Format(Core.Localization.Messages.ModelInstall_PackageDamaged, name, packageRoot));

            try
            {
                File.Move(temporary, target, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new ModelInstallException(string.Format(Core.Localization.Messages.ModelInstall_WriteFailed, target, ex.Message), ex);
            }
        }
        finally
        {
            TryDelete(temporary);
        }
    }

    /// <summary>A temporary file untouched this long belongs to no running installation.</summary>
    internal static readonly TimeSpan LeftoverAge = TimeSpan.FromMinutes(10);

    /// <summary>Temporary files of an installation that ended without cleaning up (a crash, a power cut) — only stale ones, so
    /// another copy of the application installing at the same moment keeps its own.</summary>
    private static void RemoveLeftovers(string folder, string fileName)
    {
        foreach (var leftover in Directory.EnumerateFiles(folder, $"{fileName}.*{TemporarySuffix}"))
        {
            try
            {
                if (DateTime.UtcNow - File.GetLastWriteTimeUtc(leftover) < LeftoverAge) continue;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            TryDelete(leftover);
        }
    }

    private static async Task<string?> HashAsync(string path, Action<long> advanced, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, useAsync: true);
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[BufferSize];
            int read;
            while ((read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                sha.AppendData(buffer, 0, read);
                advanced(read);
            }
            return Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;   // unreadable: copied again
        }
    }

    private static long? LengthOf(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? info.Length : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private static long? FreeSpaceOf(string folder)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(folder));
            return string.IsNullOrEmpty(root) ? null : new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;   // unknown (a network share): the copy reports a full disk itself
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // left for the next installation's clean-up
        }
    }

    private static string Megabytes(long bytes) => (bytes / (1024.0 * 1024.0)).ToString("N0", System.Globalization.CultureInfo.CurrentCulture);
}
