using System.Text.Json;
using AiVideoEditor.Vision.Audio;
using AiVideoEditor.Vision.People;
using Xunit;

namespace AiVideoEditor.Vision.Tests;

/// <summary>
/// S1 (D066 §3): the models archive's manifest <c>tools/models/models.json</c> lists exactly the files the application pins —
/// the same folders, names, sizes and SHA-256s as <see cref="HoldModel"/>, <see cref="PeopleModel"/> and
/// <see cref="AudioModel"/> — so the archive packed and installed by it is the one the application loads.
/// </summary>
public class ModelsManifestTests
{
    private static JsonElement Manifest()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "AiVideoEditor.sln"))) root = root.Parent;
        return JsonDocument.Parse(File.ReadAllText(Path.Combine(root!.FullName, "tools", "models", "models.json"))).RootElement;
    }

    [Fact]
    public void The_manifest_lists_exactly_the_pinned_model_files()
    {
        var expected = new (string Folder, IEnumerable<HoldModelFile> Files)[]
        {
            (HoldModel.FolderName, new[] { HoldModel.Encoder, HoldModel.Decoder }),
            (PeopleModel.FolderName, PeopleModel.DetectionFiles.Concat(PeopleModel.SegmentationFiles)),
            (AudioModel.FolderName, new[] { AudioModel.Voice, AudioModel.Speech }),
        }.SelectMany(f => f.Files.Select(file => $"{f.Folder}/{file.Name} {file.Length} {file.Sha256}")).OrderBy(s => s, StringComparer.Ordinal);

        var listed = Manifest().GetProperty("folders").EnumerateArray()
            .SelectMany(f => f.GetProperty("files").EnumerateArray().Select(file =>
                $"{f.GetProperty("folder").GetString()}/{file.GetProperty("name").GetString()} {file.GetProperty("length").GetInt64()} {file.GetProperty("sha256").GetString()}"))
            .OrderBy(s => s, StringComparer.Ordinal);

        Assert.Equal(expected, listed);
    }
}
