using System.Security.Cryptography;
using System.Text.Json;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Vision.Installation;
using Xunit;

namespace AiVideoEditor.Vision.Tests;

/// <summary>
/// D070 §3: the Portable package's models installed into the models folder — only pinned files, each checked by its size and
/// SHA-256 before it takes its place; a correct file kept; a damaged package, a full disk, a cancel leave no partial file and
/// touch nothing else in the folder. Small fake model files stand for the real ones.
/// </summary>
public sealed class ModelPackageInstallerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "aive-models-" + Guid.NewGuid().ToString("N"));
    private readonly string _package;
    private readonly string _models;
    private readonly IReadOnlyList<ModelFolder> _folders;
    private readonly Dictionary<string, byte[]> _content = new();

    public ModelPackageInstallerTests()
    {
        _package = Path.Combine(_root, "app", ModelCatalog.PackageFolderName);
        _models = Path.Combine(_root, "LocalAppData", "BoulderVideoEditor", "models");
        _folders = new[]
        {
            new ModelFolder("hold", new[] { Fake("hold", "encoder.onnx", 3_000_000), Fake("hold", "decoder.onnx", 1_000) }),
            new ModelFolder("people", new[] { Fake("people", "detector.onnx", 2_500_000) }),
        };
        File.WriteAllText(Path.Combine(_package, ModelCatalog.ManifestFileName), "{}");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private HoldModelFile Fake(string folder, string name, int length)
    {
        var bytes = new byte[length];
        new Random(name.GetHashCode()).NextBytes(bytes);
        var dir = Directory.CreateDirectory(Path.Combine(_package, ModelCatalog.PackageModelsFolderName, folder));
        File.WriteAllBytes(Path.Combine(dir.FullName, name), bytes);
        _content[$"{folder}\\{name}"] = bytes;
        return new HoldModelFile(name, length, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
    }

    private ModelPackageInstaller Installer(long? freeSpace = long.MaxValue) => new(_folders, _ => freeSpace);

    private string Target(string folder, string name) => Path.Combine(_models, folder, name);

    private IEnumerable<string> Leftovers() =>
        Directory.Exists(_models) ? Directory.EnumerateFiles(_models, "*.installing", SearchOption.AllDirectories) : Array.Empty<string>();

    private void AssertInstalled()
    {
        foreach (var (key, bytes) in _content)
        {
            var parts = key.Split('\\');
            Assert.Equal(bytes, File.ReadAllBytes(Target(parts[0], parts[1])));
        }
        Assert.Empty(Leftovers());
    }

    [Fact]
    public async Task Every_pinned_file_is_installed_into_an_empty_models_folder_and_reported_complete()
    {
        var installer = Installer();
        Assert.Equal(3, installer.Check(_models).MissingFiles.Count);

        var reports = new List<ModelInstallProgress>();
        var result = await installer.InstallAsync(_package, _models, new SyncProgress(reports.Add), CancellationToken.None);

        Assert.Equal(new ModelInstallResult(3, 0), result);
        AssertInstalled();
        Assert.True(installer.Check(_models).IsComplete);
        Assert.Equal(100, reports[^1].Percent);
        Assert.True(reports.Select(r => r.DoneBytes).SequenceEqual(reports.Select(r => r.DoneBytes).Order()));   // never goes back
    }

    [Fact]
    public async Task A_second_installation_keeps_the_correct_files_and_replaces_a_damaged_one()
    {
        var installer = Installer();
        await installer.InstallAsync(_package, _models, null, CancellationToken.None);
        var damaged = Target("hold", "encoder.onnx");
        var bytes = File.ReadAllBytes(damaged);
        bytes[1234] ^= 0xFF;                                    // the same size, other content
        File.WriteAllBytes(damaged, bytes);

        var reports = new List<ModelInstallProgress>();
        var result = await installer.InstallAsync(_package, _models, new SyncProgress(reports.Add), CancellationToken.None);

        Assert.Equal(new ModelInstallResult(1, 2), result);
        AssertInstalled();
        Assert.Equal(100, reports[^1].Percent);
        Assert.True(reports.Select(r => r.DoneBytes).SequenceEqual(reports.Select(r => r.DoneBytes).Order()));
    }

    [Fact]
    public async Task Nothing_else_in_the_models_folder_is_touched()
    {
        var older = Directory.CreateDirectory(Path.Combine(_models, "people-v1")).FullName;
        File.WriteAllText(Path.Combine(older, "old.onnx"), "older model");
        File.WriteAllText(Path.Combine(_models, "notes.txt"), "the user's");
        Directory.CreateDirectory(Path.Combine(_models, "hold"));
        File.WriteAllText(Path.Combine(_models, "hold", "extra.bin"), "kept");

        await Installer().InstallAsync(_package, _models, null, CancellationToken.None);

        Assert.Equal("older model", File.ReadAllText(Path.Combine(older, "old.onnx")));
        Assert.Equal("the user's", File.ReadAllText(Path.Combine(_models, "notes.txt")));
        Assert.Equal("kept", File.ReadAllText(Path.Combine(_models, "hold", "extra.bin")));
        AssertInstalled();
    }

    [Fact]
    public async Task A_damaged_package_file_never_takes_the_place_of_a_model_and_leaves_no_partial_file()
    {
        var source = Path.Combine(_package, "models", "people", "detector.onnx");
        var bytes = File.ReadAllBytes(source);
        bytes[^1] ^= 0x01;
        File.WriteAllBytes(source, bytes);

        var error = await Assert.ThrowsAsync<ModelInstallException>(() => Installer().InstallAsync(_package, _models, null, CancellationToken.None));

        Assert.Contains("people\\detector.onnx", error.Message);
        Assert.False(File.Exists(Target("people", "detector.onnx")));
        Assert.Empty(Leftovers());
    }

    [Fact]
    public async Task A_missing_package_file_is_reported_before_anything_is_written()
    {
        File.Delete(Path.Combine(_package, "models", "hold", "decoder.onnx"));

        var error = await Assert.ThrowsAsync<ModelInstallException>(() => Installer().InstallAsync(_package, _models, null, CancellationToken.None));

        Assert.Contains("hold\\decoder.onnx", error.Message);
        Assert.False(Directory.Exists(_models));
    }

    [Fact]
    public async Task Too_little_free_space_is_reported_before_copying()
    {
        var error = await Assert.ThrowsAsync<ModelInstallException>(() => Installer(freeSpace: 1_000_000).InstallAsync(_package, _models, null, CancellationToken.None));

        Assert.Contains(_models, error.Message);
        Assert.False(File.Exists(Target("hold", "encoder.onnx")));
        Assert.Empty(Leftovers());
    }

    [Fact]
    public async Task A_cancelled_installation_leaves_no_partial_file_and_a_later_one_completes()
    {
        using var cancel = new CancellationTokenSource();
        var progress = new SyncProgress(p =>
        {
            if (p.DoneBytes > 1_000_000) cancel.Cancel();     // in the middle of the first file
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Installer().InstallAsync(_package, _models, progress, cancel.Token));

        Assert.False(File.Exists(Target("hold", "encoder.onnx")));
        Assert.Empty(Leftovers());

        await Installer().InstallAsync(_package, _models, null, CancellationToken.None);
        AssertInstalled();
    }

    [Fact]
    public async Task A_temporary_file_left_by_a_crash_is_removed()
    {
        var folder = Directory.CreateDirectory(Path.Combine(_models, "hold")).FullName;
        var leftover = Path.Combine(folder, "encoder.onnx.0123456789abcdef.installing");
        File.WriteAllText(leftover, "half");

        await Installer().InstallAsync(_package, _models, null, CancellationToken.None);

        Assert.False(File.Exists(leftover));
        AssertInstalled();
    }

    [Fact]
    public void The_package_is_found_only_by_its_manifest()
    {
        var app = Path.GetDirectoryName(_package)!;
        Assert.Equal(Path.GetFullPath(_package), ModelPackageInstaller.FindPackage(app));

        File.Delete(Path.Combine(_package, ModelCatalog.ManifestFileName));
        Assert.Null(ModelPackageInstaller.FindPackage(app));
        Assert.Null(ModelPackageInstaller.FindPackage(Path.Combine(_root, "nowhere")));
    }

    [Fact]
    public void The_catalog_is_the_manifest_of_the_models_archive()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "AiVideoEditor.sln"))) root = root.Parent;
        var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(root!.FullName, "tools", "models", "models.json"))).RootElement;

        var listed = manifest.GetProperty("folders").EnumerateArray()
            .SelectMany(f => f.GetProperty("files").EnumerateArray().Select(file =>
                $"{f.GetProperty("folder").GetString()}/{file.GetProperty("name").GetString()} {file.GetProperty("length").GetInt64()} {file.GetProperty("sha256").GetString()}"))
            .Order(StringComparer.Ordinal);
        var catalog = ModelCatalog.Folders
            .SelectMany(f => f.Files.Select(file => $"{f.Folder}/{file.Name} {file.Length} {file.Sha256}"))
            .Order(StringComparer.Ordinal);

        Assert.Equal(listed, catalog);
        Assert.Equal(new ModelPackageInstaller().TotalBytes, ModelCatalog.Folders.Sum(f => f.Files.Sum(file => file.Length)));
    }

    /// <summary>Reports on the calling thread (Progress&lt;T&gt; posts asynchronously).</summary>
    private sealed class SyncProgress(Action<ModelInstallProgress> report) : IProgress<ModelInstallProgress>
    {
        public void Report(ModelInstallProgress value) => report(value);
    }
}
