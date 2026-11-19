using AiVideoEditor.Vision.Audio;
using AiVideoEditor.Vision.People;

namespace AiVideoEditor.Vision.Installation;

/// <summary>One folder of the models (a subfolder of <c>%LOCALAPPDATA%\BoulderVideoEditor\models</c>) and its pinned files.</summary>
public sealed record ModelFolder(string Folder, IReadOnlyList<HoldModelFile> Files);

/// <summary>
/// D070 §3: every model file the application pins (<see cref="HoldModel"/>, <see cref="PeopleModel"/>, <see cref="AudioModel"/>),
/// by folder — what a complete installation holds. The installer of the Portable package copies exactly these files and
/// nothing else (the package's own manifest only marks the package). <c>tools/models/models.json</c> lists the same files
/// (Vision.Tests checks both).
/// </summary>
public static class ModelCatalog
{
    /// <summary>The models package inside the Portable folder: <c>&lt;app&gt;\ai-models</c> — the unpacked models archive
    /// (<c>models.json</c>, <c>models\&lt;folder&gt;\&lt;file&gt;</c>, the notices and licences, Install-Models.cmd).</summary>
    public const string PackageFolderName = "ai-models";

    /// <summary>The package's manifest file; its presence marks a models package.</summary>
    public const string ManifestFileName = "models.json";

    /// <summary>The model files' folder inside a package.</summary>
    public const string PackageModelsFolderName = "models";

    public static IReadOnlyList<ModelFolder> Folders { get; } = new[]
    {
        new ModelFolder(HoldModel.FolderName, new[] { HoldModel.Encoder, HoldModel.Decoder }),
        new ModelFolder(PeopleModel.FolderName, PeopleModel.DetectionFiles.Concat(PeopleModel.SegmentationFiles).ToArray()),
        new ModelFolder(AudioModel.FolderName, new[] { AudioModel.Voice, AudioModel.Speech }),
    };

    /// <summary>The size of a complete installation, in bytes.</summary>
    public static long TotalBytes(IReadOnlyList<ModelFolder> folders) => folders.Sum(f => f.Files.Sum(file => file.Length));
}
