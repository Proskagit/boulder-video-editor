using System.Text.Json.Nodes;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Project.Persistence;
using Xunit;

namespace AiVideoEditor.Project.Tests;

/// <summary>
/// D028, refined in Step 13.3: the export settings are saved with the project as the optional <c>settings.export</c> of
/// <c>project.json</c> v3 — written only when they differ from the default, absent (or null) read as the default (the
/// Phase 8–12 output), anything not offered is a damaged file (EX-4). The session-only <c>LastExportSettings</c> (the
/// output path) and the ignored top-level <c>lastExportSettings</c> of Phases 6–8 stay as they were.
/// </summary>
public class ExportEncodingPersistenceTests
{
    private const string Folder = @"C:\Projects\Demo";
    private static readonly Func<string, bool> AllExist = _ => true;
    private static readonly RecoveryInfo Info = new(Folder, new DateTimeOffset(2026, 10, 6, 10, 0, 0, TimeSpan.Zero), 1234, null);

    private static readonly ExportEncoding Custom = new(ExportQuality.Compact, ExportSpeedPreset.Slow, 320);

    private static Core.Entities.Project Sample(ExportEncoding? export = null)
    {
        var project = ProjectTestData.Build(Folder + @"\media");
        if (export is not null) project.Settings.Export = export;
        return project;
    }

    private static Core.Entities.Project Load(string json) => ProjectSerializer.Deserialize(json, Folder, AllExist);

    private static JsonObject Json(Core.Entities.Project project) =>
        JsonNode.Parse(ProjectSerializer.Serialize(project, Folder))!.AsObject();

    private static JsonObject Settings(JsonObject root) => root["settings"]!.AsObject();

    public static TheoryData<ExportQuality, ExportSpeedPreset, int> NonDefault => new()
    {
        { ExportQuality.Maximum, ExportSpeedPreset.Medium, 192 },
        { ExportQuality.Standard, ExportSpeedPreset.Medium, 192 },
        { ExportQuality.Compact, ExportSpeedPreset.Medium, 192 },
        { ExportQuality.High, ExportSpeedPreset.Fast, 192 },
        { ExportQuality.High, ExportSpeedPreset.Slow, 192 },
        { ExportQuality.High, ExportSpeedPreset.Medium, 128 },
        { ExportQuality.High, ExportSpeedPreset.Medium, 160 },
        { ExportQuality.High, ExportSpeedPreset.Medium, 256 },
        { ExportQuality.High, ExportSpeedPreset.Medium, 320 },
        { ExportQuality.Compact, ExportSpeedPreset.Slow, 320 },
    };

    [Theory]
    [MemberData(nameof(NonDefault))]
    public void Export_settings_survive_save_and_reopen(ExportQuality quality, ExportSpeedPreset preset, int bitrate)
    {
        var export = new ExportEncoding(quality, preset, bitrate);
        var json = ProjectSerializer.Serialize(Sample(export), Folder);

        var loaded = Load(json);

        Assert.Equal(export, loaded.Settings.Export);
        Assert.False(loaded.IsDirty);
        Assert.Equal(json, ProjectSerializer.Serialize(loaded, Folder));
    }

    [Fact]
    public void Export_settings_are_written_by_name_under_settings_export()
    {
        var root = Json(Sample(Custom));

        var export = Settings(root)["export"]!.AsObject();
        Assert.Equal("Compact", export["quality"]!.GetValue<string>());
        Assert.Equal("Slow", export["preset"]!.GetValue<string>());
        Assert.Equal(320, export["audioBitrateKbps"]!.GetValue<int>());
        Assert.Equal(3, export.Count);
        Assert.Equal(3, root["formatVersion"]!.GetValue<int>());
        Assert.False(root.ContainsKey("export"));
        Assert.False(root.ContainsKey("lastExportSettings"));
    }

    [Fact]
    public void Default_export_settings_are_not_written()
    {
        var root = Json(Sample());

        Assert.False(Settings(root).ContainsKey("export"));
        Assert.DoesNotContain("\"export\"", root.ToJsonString());
    }

    [Fact]
    public void A_project_without_export_settings_reads_as_the_default_and_saves_unchanged()
    {
        // The shape every v3 file of Phases 10–12 has: no "export" in "settings".
        var json = ProjectSerializer.Serialize(Sample(), Folder);
        Assert.False(Settings(JsonNode.Parse(json)!.AsObject()).ContainsKey("export"));

        var loaded = Load(json);

        Assert.Equal(ExportEncoding.Default, loaded.Settings.Export);
        Assert.Equal(json, ProjectSerializer.Serialize(loaded, Folder));
    }

    [Fact]
    public void Null_export_settings_read_as_the_default()
    {
        var root = Json(Sample(Custom));
        Settings(root)["export"] = null;

        Assert.Equal(ExportEncoding.Default, Load(root.ToJsonString()).Settings.Export);
    }

    [Fact]
    public void Explicitly_written_default_values_read_as_the_default()
    {
        var root = Json(Sample(Custom));
        Settings(root)["export"] = new JsonObject { ["quality"] = "High", ["preset"] = "Medium", ["audioBitrateKbps"] = 192 };

        var loaded = Load(root.ToJsonString());

        Assert.Equal(ExportEncoding.Default, loaded.Settings.Export);
        Assert.False(Settings(Json(loaded)).ContainsKey("export"));
    }

    public static TheoryData<string, Action<JsonObject>> InvalidExport => new()
    {
        { "unknown quality", e => e["quality"] = "Ultra" },
        { "quality in another case", e => e["quality"] = "high" },
        { "quality as a number", e => e["quality"] = 1 },
        { "quality as a numeric string", e => e["quality"] = "1" },
        { "combined quality names", e => e["quality"] = "High, Standard" },
        { "quality as a CRF", e => e["quality"] = "18" },
        { "missing quality", e => e.Remove("quality") },
        { "null quality", e => e["quality"] = null },
        { "unknown preset", e => e["preset"] = "veryslow" },
        { "preset as the libx264 name", e => e["preset"] = "slow" },
        { "preset as a number", e => e["preset"] = 2 },
        { "missing preset", e => e.Remove("preset") },
        { "unsupported bitrate", e => e["audioBitrateKbps"] = 191 },
        { "bitrate in bits per second", e => e["audioBitrateKbps"] = 192000 },
        { "zero bitrate", e => e["audioBitrateKbps"] = 0 },
        { "negative bitrate", e => e["audioBitrateKbps"] = -192 },
        { "bitrate as a string", e => e["audioBitrateKbps"] = "192" },
        { "fractional bitrate", e => e["audioBitrateKbps"] = 192.5 },
        { "missing bitrate", e => e.Remove("audioBitrateKbps") },
    };

    [Theory]
    [MemberData(nameof(InvalidExport))]
    public void Invalid_export_settings_make_the_project_damaged(string description, Action<JsonObject> corrupt)
    {
        var root = Json(Sample(Custom));
        corrupt(Settings(root)["export"]!.AsObject());

        var ex = Assert.Throws<ProjectFileException>(() => Load(root.ToJsonString()));

        Assert.Contains("damaged", ex.Message);
        Assert.False(string.IsNullOrWhiteSpace(description));
    }

    [Theory]
    [InlineData("\"High\"")]
    [InlineData("[]")]
    [InlineData("42")]
    public void Export_settings_that_are_not_an_object_make_the_project_damaged(string value)
    {
        var root = Json(Sample(Custom));
        Settings(root)["export"] = JsonNode.Parse(value);

        Assert.Contains("damaged", Assert.Throws<ProjectFileException>(() => Load(root.ToJsonString())).Message);
    }

    [Fact]
    public void Unknown_properties_inside_export_settings_are_ignored_like_elsewhere_in_the_file()
    {
        var root = Json(Sample(Custom));
        Settings(root)["export"]!["comment"] = "from a later build";

        Assert.Equal(Custom, Load(root.ToJsonString()).Settings.Export);
    }

    // --- recovery ------------------------------------------------------------------------------------------------------

    [Fact]
    public void A_recovery_file_keeps_the_export_settings()
    {
        var project = Sample(Custom);

        var json = ProjectSerializer.SerializeRecovery(project, Info);
        var (loaded, _) = ProjectSerializer.DeserializeRecovery(json, AllExist);

        Assert.Equal("Compact", JsonNode.Parse(json)!["project"]!["settings"]!["export"]!["quality"]!.GetValue<string>());
        Assert.Equal(Custom, loaded.Settings.Export);
        Assert.Equal(ProjectSerializer.Serialize(project, Folder), ProjectSerializer.Serialize(loaded, Folder));
    }

    [Fact]
    public void A_recovery_file_with_default_export_settings_has_none_and_reads_as_the_default()
    {
        var json = ProjectSerializer.SerializeRecovery(Sample(), Info);

        Assert.False(JsonNode.Parse(json)!["project"]!["settings"]!.AsObject().ContainsKey("export"));
        Assert.Equal(ExportEncoding.Default, ProjectSerializer.DeserializeRecovery(json, AllExist).Project.Settings.Export);
    }

    [Fact]
    public void A_recovery_file_with_invalid_export_settings_is_refused()
    {
        var root = JsonNode.Parse(ProjectSerializer.SerializeRecovery(Sample(Custom), Info))!.AsObject();
        root["project"]!["settings"]!["export"]!["audioBitrateKbps"] = 100;

        Assert.Throws<ProjectFileException>(() => ProjectSerializer.DeserializeRecovery(root.ToJsonString(), AllExist));
    }

    // --- the session-only export state stays session-only ------------------------------------------------------------

    [Fact]
    public void The_output_path_and_lastExportSettings_are_still_never_written()
    {
        var project = Sample(Custom);
        project.LastExportSettings = new ExportSettings { OutputPath = @"C:\Exports\final.mp4" };

        var json = ProjectSerializer.Serialize(project, Folder);
        var recovery = ProjectSerializer.SerializeRecovery(project, Info);

        foreach (var text in new[] { json, recovery })
        {
            Assert.DoesNotContain("final.mp4", text);
            Assert.DoesNotContain("lastExportSettings", text);
            Assert.DoesNotContain("outputPath", text);
        }
        Assert.Equal(string.Empty, Load(json).LastExportSettings.OutputPath);
    }

    [Fact]
    public void An_old_top_level_lastExportSettings_is_still_ignored_and_never_read_as_export_settings()
    {
        // Phases 6–8 wrote "lastExportSettings" with a size, a double rate and bitrates; it must not leak into settings.export.
        var root = Json(Sample());
        root["lastExportSettings"] = JsonNode.Parse("""
            { "outputPath": "C:\\Exports\\final.mp4", "container": "Mp4", "videoCodec": "H264", "audioCodec": "Aac",
              "width": 1280, "height": 720, "frameRate": 29.97, "videoBitrateBps": 8000000, "audioBitrateBps": 128000,
              "quality": "Compact", "preset": "Slow", "audioBitrateKbps": 320 }
            """);

        var loaded = Load(root.ToJsonString());

        Assert.Equal(ExportEncoding.Default, loaded.Settings.Export);
        Assert.Equal(string.Empty, loaded.LastExportSettings.OutputPath);
        Assert.DoesNotContain("lastExportSettings", ProjectSerializer.Serialize(loaded, Folder));
    }

    // --- project settings on load stay as they were (D028 §5, CS-2) ---------------------------------------------------

    [Theory]
    [InlineData(1921, 1081)]   // odd: not exportable, still opens (the export preflight refuses it)
    [InlineData(8192, 4320)]   // above the user limits
    [InlineData(32, 32)]       // below the user limits
    public void A_canvas_outside_the_user_limits_still_opens(int width, int height)
    {
        var root = Json(Sample());
        Settings(root)["frameWidth"] = width;
        Settings(root)["frameHeight"] = height;

        var loaded = Load(root.ToJsonString());

        Assert.NotNull(ProjectSettingsRules.CanvasError(width, height));
        Assert.Equal((width, height), (loaded.Settings.FrameWidth, loaded.Settings.FrameHeight));
    }

    [Theory]
    [InlineData(15, 1)]
    [InlineData(120, 1)]
    [InlineData(25, 2)]
    public void A_frame_rate_that_is_not_selectable_still_opens(int numerator, int denominator)
    {
        var root = Json(Sample());
        Settings(root)["frameRate"] = new JsonObject { ["numerator"] = numerator, ["denominator"] = denominator };
        // The sample's clips sit on its 29.97 grid; a project with this rate and no clips is enough here.
        foreach (var track in root["timeline"]!["videoTracks"]!.AsArray().Concat(root["timeline"]!["audioTracks"]!.AsArray()))
        {
            track!["clips"]!.AsArray().Clear();
            track["transitions"]?.AsArray().Clear();
        }

        var loaded = Load(root.ToJsonString());

        Assert.False(ProjectSettingsRules.IsSelectableFrameRate(loaded.Settings.FrameRate));
        Assert.Equal(new Core.Common.FrameRate(numerator, denominator), loaded.Settings.FrameRate);
    }
}
