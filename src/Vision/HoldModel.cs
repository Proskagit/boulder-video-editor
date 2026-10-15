using System.Security.Cryptography;
using AiVideoEditor.Core.Interfaces;

namespace AiVideoEditor.Vision;

/// <summary>One pinned model file (D034 §2).</summary>
public sealed record HoldModelFile(string Name, long Length, string Sha256);

/// <summary>
/// The pinned hold model "sam2.1-large-v1" (D034 §2): SAM 2.1 Hiera Large, checkpoint 092824 (SHA-256 2647878d…dd318),
/// facebookresearch/sam2 at 2b90b9f, exported with samexporter 0.5.0 under PyTorch 2.14.1, ONNX opset 18. Recipe and
/// licences: docs/AI_MODEL.md. A file with another SHA-256 is never loaded.
/// </summary>
public static class HoldModel
{
    public const string Id = "sam2.1-large-v1";

    /// <summary>The folder name under %LOCALAPPDATA%\AiVideoEditor\models.</summary>
    public const string FolderName = "sam2.1-large";

    public static readonly HoldModelFile Encoder =
        new("sam2.1_large.encoder.onnx", 852_451_097, "5ca0f505138811202a60445427383559fb84afcd80e7a5e2d1ce5d9f546e3362");

    public static readonly HoldModelFile Decoder =
        new("sam2.1_large.decoder.onnx", 16_519_627, "d2ef7f723ed226f424d590cd382bfef135fdfd4cb74f3a76f6bd563a63b28f10");

    /// <summary>The model's input size (square).</summary>
    public const int InputSize = 1024;

    /// <summary>The decoder's low-resolution mask size (square).</summary>
    public const int MaskSize = 256;
}

/// <summary>
/// Checks the model folder (D034 §2): both files present with their pinned size and SHA-256. Nothing is ever downloaded,
/// replaced or repaired. The result of a successful check is kept for the session.
/// </summary>
public sealed class HoldModelVerifier
{
    private readonly IReadOnlyList<HoldModelFile> _files;
    private HoldModelStatus? _verified;

    public HoldModelVerifier(string folder) : this(folder, new[] { HoldModel.Encoder, HoldModel.Decoder })
    {
    }

    /// <summary>For tests: other expected files (small fakes with their own SHA-256).</summary>
    internal HoldModelVerifier(string folder, IReadOnlyList<HoldModelFile> files)
    {
        // Always a full path, so the messages name the real folder (with an unset profile LocalApplicationData can be
        // empty and the app's folders relative — found in the Step 19.8 real-app run, as for the LUTs in 18.8).
        Folder = Path.GetFullPath(folder);
        _files = files;
    }

    public string Folder { get; }

    public string PathOf(HoldModelFile file) => Path.Combine(Folder, file.Name);

    public async Task<HoldModelStatus> VerifyAsync(IProgress<double>? progress, CancellationToken cancellationToken)
    {
        if (_verified is { IsReady: true } ok) return ok;

        var files = _files;
        foreach (var file in files)
        {
            var path = PathOf(file);
            if (!File.Exists(path))
                return new HoldModelStatus(HoldModelState.Missing,
                    $"The hold model {HoldModel.Id} is not installed: {file.Name} is missing in {Folder}. See docs/AI_MODEL.md.");
        }

        long total = files.Sum(f => f.Length), done = 0;
        foreach (var file in files)
        {
            var path = PathOf(file);
            if (new FileInfo(path).Length != file.Length)
                return Mismatch(file);
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, useAsync: true);
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[1 << 20];
            int read;
            while ((read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                sha.AppendData(buffer, 0, read);
                done += read;
                progress?.Report((double)done / total);
            }
            if (!string.Equals(Convert.ToHexString(sha.GetHashAndReset()), file.Sha256, StringComparison.OrdinalIgnoreCase))
                return Mismatch(file);
        }

        _verified = new HoldModelStatus(HoldModelState.Ready, null);
        return _verified;
    }

    private HoldModelStatus Mismatch(HoldModelFile file) => new(HoldModelState.Mismatch,
        $"The hold model file {file.Name} in {Folder} is damaged or another version (expected {HoldModel.Id}, SHA-256 " +
        $"{file.Sha256}). It was not loaded. See docs/AI_MODEL.md.");
}
