using AiVideoEditor.Infrastructure.Configuration;
using Xunit;

namespace AiVideoEditor.UI.Tests;

/// <summary>D068 §1: 1.0's per-user folder moves under the new name — whole when the new one is absent, merged entry by
/// entry when it exists (nothing overwritten, nothing deleted that was not moved).</summary>
public sealed class AppDataMigrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "aive-migration-" + Guid.NewGuid().ToString("N"));
    private string Old => Path.Combine(_root, "AiVideoEditor");
    private string New => Path.Combine(_root, "BoulderVideoEditor");

    public AppDataMigrationTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private static void Write(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    [Fact]
    public void Nothing_happens_without_the_old_folder()
    {
        var result = AppDataMigration.Run(Old, New);

        Assert.Same(AppDataMigration.Result.Nothing, result);
        Assert.False(Directory.Exists(New));
    }

    [Fact]
    public void The_old_folder_is_renamed_when_the_new_one_is_absent()
    {
        Write(Path.Combine(Old, "config", "settings.json"), "settings");
        Write(Path.Combine(Old, "models", "sam2.1-large", "encoder.onnx"), "model");

        var result = AppDataMigration.Run(Old, New);

        Assert.True(result.OldFolderRemoved);
        Assert.Empty(result.Failures);
        Assert.False(Directory.Exists(Old));
        Assert.Equal("settings", File.ReadAllText(Path.Combine(New, "config", "settings.json")));
        Assert.Equal("model", File.ReadAllText(Path.Combine(New, "models", "sam2.1-large", "encoder.onnx")));
    }

    [Fact]
    public void An_existing_new_folder_is_merged_and_its_files_win()
    {
        // 1.1's models installed first (the new folder exists), the settings and recovery still in 1.0's folder.
        Write(Path.Combine(New, "models", "people-v2", "model.onnx"), "new people");
        Write(Path.Combine(New, "logs", "app.log"), "new log");
        Write(Path.Combine(Old, "models", "people-v2", "model.onnx"), "old people");
        Write(Path.Combine(Old, "models", "sam2.1-large", "encoder.onnx"), "old sam");
        Write(Path.Combine(Old, "config", "recent-projects.json"), "recent");
        Write(Path.Combine(Old, "logs", "app.log"), "old log");

        var result = AppDataMigration.Run(Old, New);

        Assert.Empty(result.Failures);
        Assert.Equal("new people", File.ReadAllText(Path.Combine(New, "models", "people-v2", "model.onnx")));
        Assert.Equal("new log", File.ReadAllText(Path.Combine(New, "logs", "app.log")));
        Assert.Equal("old sam", File.ReadAllText(Path.Combine(New, "models", "sam2.1-large", "encoder.onnx")));
        Assert.Equal("recent", File.ReadAllText(Path.Combine(New, "config", "recent-projects.json")));
        // The two files present in both stay in the old folder — never deleted, so it is not removed.
        Assert.Equal(2, result.Kept);
        Assert.False(result.OldFolderRemoved);
        Assert.Equal("old people", File.ReadAllText(Path.Combine(Old, "models", "people-v2", "model.onnx")));
        Assert.False(Directory.Exists(Path.Combine(Old, "config")));
    }

    [Fact]
    public void A_file_in_use_stays_for_the_next_start()
    {
        Directory.CreateDirectory(New);
        Write(Path.Combine(Old, "logs", "app.log"), "locked");
        Write(Path.Combine(Old, "config", "settings.json"), "settings");

        AppDataMigration.Result result;
        using (new FileStream(Path.Combine(Old, "logs", "app.log"), FileMode.Open, FileAccess.Read, FileShare.None))
            result = AppDataMigration.Run(Old, New);

        Assert.Single(result.Failures);
        Assert.True(File.Exists(Path.Combine(Old, "logs", "app.log")));
        Assert.Equal("settings", File.ReadAllText(Path.Combine(New, "config", "settings.json")));

        var second = AppDataMigration.Run(Old, New);
        Assert.Empty(second.Failures);
        Assert.True(second.OldFolderRemoved);
        Assert.Equal("locked", File.ReadAllText(Path.Combine(New, "logs", "app.log")));
    }
}
