using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Project.Persistence;
using Xunit;

namespace AiVideoEditor.Project.Tests;

public class ProjectFileStoreTests
{
    private static string[] StrayTempFiles(TempFolder temp) =>
        Directory.GetFiles(temp.Path, "*.tmp", SearchOption.AllDirectories);

    [Fact]
    public async Task Write_creates_the_folder_and_file()
    {
        using var temp = new TempFolder();
        var path = ProjectFileStore.ProjectFilePath(temp.Combine("new", "project"));

        await new ProjectFileStore().WriteAtomicAsync(path, "{ \"a\": \"ü\" }");

        Assert.Equal("{ \"a\": \"ü\" }", await new ProjectFileStore().ReadAsync(path));
        Assert.Empty(StrayTempFiles(temp));
    }

    [Fact]
    public async Task Written_file_is_utf8_without_bom()
    {
        using var temp = new TempFolder();
        var path = temp.Combine("project.json");

        await new ProjectFileStore().WriteAtomicAsync(path, "{}");

        Assert.Equal(new byte[] { (byte)'{', (byte)'}' }, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task Write_replaces_an_existing_file()
    {
        using var temp = new TempFolder();
        var path = temp.CreateFile("project.json", "old content that is longer");

        await new ProjectFileStore().WriteAtomicAsync(path, "new");

        Assert.Equal("new", await File.ReadAllTextAsync(path));
        Assert.Empty(StrayTempFiles(temp));
    }

    [Fact]
    public async Task Failure_before_replace_leaves_the_existing_file_intact()
    {
        using var temp = new TempFolder();
        var path = temp.CreateFile("project.json", "original");
        var store = new ProjectFileStore { BeforeCommit = _ => throw new IOException("disk full") };

        var ex = await Assert.ThrowsAsync<ProjectFileException>(() => store.WriteAtomicAsync(path, "new"));

        Assert.Contains("disk full", ex.Message);
        Assert.Equal("original", await File.ReadAllTextAsync(path));
        Assert.Empty(StrayTempFiles(temp));
    }

    [Fact]
    public async Task Failure_when_there_is_no_file_yet_leaves_nothing_behind()
    {
        using var temp = new TempFolder();
        var path = temp.Combine("project.json");
        var store = new ProjectFileStore { BeforeCommit = _ => throw new IOException("disk full") };

        await Assert.ThrowsAsync<ProjectFileException>(() => store.WriteAtomicAsync(path, "new"));

        Assert.False(File.Exists(path));
        Assert.Empty(StrayTempFiles(temp));
    }

    [Fact]
    public async Task Cancellation_leaves_the_existing_file_intact()
    {
        using var temp = new TempFolder();
        var path = temp.CreateFile("project.json", "original");
        using var cts = new CancellationTokenSource();
        var store = new ProjectFileStore { BeforeCommit = _ => { } };
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.WriteAtomicAsync(path, "new", cts.Token));

        Assert.Equal("original", await File.ReadAllTextAsync(path));
        Assert.Empty(StrayTempFiles(temp));
    }

    [Fact]
    public async Task Real_replace_failure_leaves_the_existing_file_intact()
    {
        using var temp = new TempFolder();
        var path = temp.CreateFile("project.json", "original");
        File.SetAttributes(path, FileAttributes.ReadOnly);
        try
        {
            await Assert.ThrowsAsync<ProjectFileException>(() => new ProjectFileStore().WriteAtomicAsync(path, "new"));

            Assert.Equal("original", await File.ReadAllTextAsync(path));
            Assert.Empty(StrayTempFiles(temp));
        }
        finally
        {
            File.SetAttributes(path, FileAttributes.Normal);
        }
    }

    [Fact]
    public async Task Reading_a_missing_file_reports_a_project_file_error()
    {
        using var temp = new TempFolder();

        var ex = await Assert.ThrowsAsync<ProjectFileException>(
            () => new ProjectFileStore().ReadAsync(ProjectFileStore.ProjectFilePath(temp.Combine("nope"))));

        Assert.Contains("project.json", ex.Message);
    }

    [Fact]
    public async Task Saved_project_round_trips_through_disk()
    {
        using var temp = new TempFolder();
        var folder = temp.Combine("Demo");
        var project = ProjectTestData.Build(temp.Combine("media"));
        var store = new ProjectFileStore();
        var json = AiVideoEditor.Project.Persistence.ProjectSerializer.Serialize(project, folder);

        await store.WriteAtomicAsync(ProjectFileStore.ProjectFilePath(folder), json);
        var loaded = AiVideoEditor.Project.Persistence.ProjectSerializer.Deserialize(
            await store.ReadAsync(ProjectFileStore.ProjectFilePath(folder)), folder);

        Assert.Equal(json, AiVideoEditor.Project.Persistence.ProjectSerializer.Serialize(loaded, folder));
    }
}
