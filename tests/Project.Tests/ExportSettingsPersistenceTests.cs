using System.Text.Json.Nodes;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Project.Persistence;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiVideoEditor.Project.Tests;

/// <summary>
/// D023 (corrected at the Phase 8 Step 7 closeout): <see cref="Core.Entities.Project.LastExportSettings"/> is
/// session-only state. It is never written to <c>project.json</c> or a recovery file, a <c>lastExportSettings</c> of an
/// older file (Phases 6–8 wrote one) is ignored like any unknown property — whatever its content — and a loaded project
/// starts with empty export settings. The format version stays 2.
/// </summary>
public sealed class ExportSettingsPersistenceTests : IDisposable
{
    private const string Folder = @"C:\Projects\Demo";
    private static readonly Func<string, bool> AllExist = _ => true;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "AiVideoEditorTests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private static Core.Entities.Project Exported() => new()
    {
        Name = "Export Settings",
        LastExportSettings = new ExportSettings { OutputPath = @"C:\Exports\final.mp4" }
    };

    /// <summary>A project saved by this build with a <c>lastExportSettings</c> property added, as older builds wrote it.</summary>
    private static string OlderProjectJson(JsonNode? exportSettings)
    {
        var root = JsonNode.Parse(ProjectSerializer.Serialize(new Core.Entities.Project { Name = "Older" }, Folder))!.AsObject();
        root["lastExportSettings"] = exportSettings;
        return root.ToJsonString();
    }

    /// <summary>Exactly what Phases 6–7 wrote (ExportSettings with size, double frame rate, bitrates).</summary>
    private static JsonObject Phase7ExportSettings() => JsonNode.Parse("""
        {
          "outputPath": "C:\\Exports\\final.mp4", "container": "Mp4", "videoCodec": "H264", "audioCodec": "Aac",
          "width": 1280, "height": 720, "frameRate": 29.97, "videoBitrateBps": 8000000, "audioBitrateBps": null
        }
        """)!.AsObject();

    private static Core.Entities.Project Load(string json) => ProjectSerializer.Deserialize(json, Folder, AllExist);

    private static void AssertEmpty(ExportSettings settings)
    {
        Assert.Equal(string.Empty, settings.OutputPath);
        Assert.Equal(ExportContainer.Mp4, settings.Container);
        Assert.Equal(ExportVideoCodec.H264, settings.VideoCodec);
        Assert.Equal(ExportAudioCodec.Aac, settings.AudioCodec);
    }

    [Fact]
    public void The_project_file_never_contains_export_settings()
    {
        var json = ProjectSerializer.Serialize(Exported(), Folder);

        var root = JsonNode.Parse(json)!.AsObject();
        Assert.False(root.ContainsKey("lastExportSettings"));
        Assert.DoesNotContain("final.mp4", json);
        Assert.Equal(2, root["formatVersion"]!.GetValue<int>());
    }

    [Fact]
    public void A_recovery_file_never_contains_export_settings()
    {
        var json = ProjectSerializer.SerializeRecovery(Exported(), new RecoveryInfo(Folder, DateTimeOffset.UtcNow, 1, null));

        Assert.False(JsonNode.Parse(json)!["project"]!.AsObject().ContainsKey("lastExportSettings"));
        Assert.DoesNotContain("final.mp4", json);
        AssertEmpty(ProjectSerializer.DeserializeRecovery(json, AllExist).Project.LastExportSettings);
    }

    [Fact]
    public void Export_settings_of_an_older_file_are_not_restored()
    {
        var loaded = Load(OlderProjectJson(Phase7ExportSettings()));

        AssertEmpty(loaded.LastExportSettings);
    }

    public static TheoryData<string> OlderValues => new()
    {
        """{ "outputPath": "C:\\x.mp4", "container": "Mkv", "videoCodec": "Hevc", "audioCodec": "Opus" }""",
        """{ "width": -5, "height": 0, "frameRate": 0, "videoBitrateBps": -1 }""",
        "null",
        "42",
        "\"C:\\\\x.mp4\"",
        "[1, 2]"
    };

    /// <summary>Unknown properties are ignored (D014): no content of an old <c>lastExportSettings</c> makes a file
    /// damaged — also formats that earlier builds refused.</summary>
    [Theory]
    [MemberData(nameof(OlderValues))]
    public void Any_older_value_loads(string value)
    {
        var loaded = Load(OlderProjectJson(JsonNode.Parse(value)));

        AssertEmpty(loaded.LastExportSettings);
    }

    [Fact]
    public async Task Saving_after_an_export_does_not_write_it_and_keeps_it_for_the_session()
    {
        var undo = new UndoRedoService();
        var projects = new ProjectService(undo, NullLogger<ProjectService>.Instance);
        var folder = Path.Combine(_root, "project");
        await projects.SaveAsAsync(folder);

        // What ExportWorkflow does after a successful export: session state, not an edit.
        projects.Current.LastExportSettings.OutputPath = @"C:\Exports\final.mp4";
        Assert.False(projects.Current.IsDirty);
        Assert.False(undo.CanUndo);

        await projects.SaveAsync();

        var json = await File.ReadAllTextAsync(Path.Combine(folder, "project.json"));
        Assert.False(JsonNode.Parse(json)!.AsObject().ContainsKey("lastExportSettings"));
        Assert.DoesNotContain("final.mp4", json);
        Assert.Equal(@"C:\Exports\final.mp4", projects.Current.LastExportSettings.OutputPath);   // still this session's

        var reopened = await projects.OpenAsync(folder);
        AssertEmpty(reopened.LastExportSettings);
    }
}
