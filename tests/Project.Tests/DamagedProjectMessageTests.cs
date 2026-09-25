using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Project;
using AiVideoEditor.Project.Persistence;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiVideoEditor.Project.Tests;

/// <summary>
/// D024 Step 9.3: what the user reads when a project file is damaged (the status bar shows the
/// <see cref="ProjectFileException"/> message) promises no backup — the app keeps none.
/// </summary>
public sealed class DamagedProjectMessageTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "AiVideoEditorTests", Guid.NewGuid().ToString("N"), "Film");

    public DamagedProjectMessageTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try { Directory.Delete(Path.GetDirectoryName(_folder)!, recursive: true); } catch (IOException) { }
    }

    [Theory]
    [InlineData("{ this is not json")]                                                            // unreadable
    [InlineData("""{ "format": "AiVideoEditor.Project", "formatVersion": 2 }""")]                // parts missing
    public async Task Opening_a_damaged_project_says_so_and_promises_no_backup(string content)
    {
        File.WriteAllText(ProjectFileStore.ProjectFilePath(_folder), content);
        var projects = new ProjectService(new UndoRedoService(), NullLogger<ProjectService>.Instance);

        var error = await Assert.ThrowsAsync<ProjectFileException>(() => projects.OpenAsync(_folder));

        Assert.StartsWith("The project file is damaged and can't be opened", error.Message);
        foreach (var promise in new[] { "backup", "cache", "restore", "copy" })
            Assert.DoesNotContain(promise, error.Message, StringComparison.OrdinalIgnoreCase);
    }
}
