using System.Text.Json.Nodes;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Core.Telemetry;
using AiVideoEditor.Project.Persistence;
using Xunit;

namespace AiVideoEditor.Project.Tests;

/// <summary>
/// D049 §7 / Step 35.4: <c>project.json</c> v11 stores the COROS training as a FIT reference (paths, size, SHA-256, activity,
/// clock correction — never the telemetry), a video's recording-zero override and the recording-time probe of its
/// metadata. Saved and opened exactly; a v10 file opens without them and is saved as v11 differing only in the version;
/// members in a v10 file are ignored; malformed members are a damaged file; recovery carries the same.
/// </summary>
public class TrainingPersistenceTests
{
    private const string Folder = @"C:\Projects\Demo";
    private const string Sha = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private static Core.Entities.Project Load(string json) => ProjectSerializer.Deserialize(json, Folder, _ => true);

    private static string Save(Core.Entities.Project project) => ProjectSerializer.Serialize(project, Folder);

    private static Core.Entities.Project Build(bool training, RecordingTimeProbe? probe, DateTimeOffset? zero)
    {
        var project = new Core.Entities.Project { Name = "Coros" };
        project.MediaAssets.Add(new MediaAsset
        {
            FilePath = Folder + @"\media\climb.mp4", Kind = MediaKind.Video, AnalysisStatus = MediaAnalysisStatus.Completed,
            Metadata = new MediaMetadata { Duration = MediaTime.FromSeconds(99), Width = 3840, Height = 2160, PixelFormat = "yuv420p", RecordingTimeProbe = probe },
            RecordingZeroOverrideUtc = zero
        });
        if (training)
        {
            project.Training = new TrainingReference
            {
                FilePath = Folder + @"\fit\Болдеринг.fit",
                FileSizeBytes = 54003,
                Sha256 = Sha,
                Activity = new TrainingActivity(new DateTimeOffset(2026, 9, 27, 7, 2, 39, TimeSpan.Zero),
                    new DateTimeOffset(2026, 9, 27, 8, 39, 14, TimeSpan.Zero), 294, 814),
                ClockCorrection = new MediaTime(-20_000_000)
            };
        }
        return project;
    }

    private static Core.Entities.Project Full() => Build(true,
        RecordingTimeProbe.Found("2026-09-27T07:35:07.000000Z", RecordingTimeTagOrigin.Format),
        new DateTimeOffset(2026, 9, 27, 7, 30, 26, 500, TimeSpan.Zero));

    [Fact]
    public void Every_member_survives_a_save_open_save_exactly_as_version_11()
    {
        var first = Save(Full());
        var root = JsonNode.Parse(first)!;
        Assert.Equal(16, root["formatVersion"]!.GetValue<int>());

        var training = root["training"]!.AsObject();
        Assert.Equal(@"fit\Болдеринг.fit", training["relativePath"]!.GetValue<string>());
        Assert.Equal(54003, training["fileSizeBytes"]!.GetValue<long>());
        Assert.Equal(Sha, training["sha256"]!.GetValue<string>());
        Assert.Equal(-20_000_000, training["clockCorrectionTicks"]!.GetValue<long>());
        Assert.Equal("2026-09-27T07:02:39+00:00", training["activity"]!["startUtc"]!.GetValue<string>());
        Assert.Null(training["heartRate"]);                                          // never the telemetry

        var media = root["mediaAssets"]![0]!;
        Assert.Equal("2026-09-27T07:30:26.5+00:00", media["recordingZeroOverrideUtc"]!.GetValue<string>());
        var probe = media["metadata"]!["recordingTimeProbe"]!;
        Assert.Equal(("found", "2026-09-27T07:35:07.000000Z", "format"),
            (probe["status"]!.GetValue<string>(), probe["tag"]!.GetValue<string>(), probe["origin"]!.GetValue<string>()));

        var loaded = Load(first);
        Assert.Equal(first, Save(loaded));                                            // byte for byte
        Assert.Equal(new MediaTime(-20_000_000), loaded.Training!.ClockCorrection);
        Assert.Equal(TrainingAvailability.Unresolved, loaded.Training.Availability);  // runtime state, resolved after Open
        Assert.Equal(new DateTimeOffset(2026, 9, 27, 7, 30, 26, 500, TimeSpan.Zero), loaded.MediaAssets[0].RecordingZeroOverrideUtc);
    }

    [Fact]
    public void Absent_probe_and_no_training_are_written_minimally_and_read_back()
    {
        var text = Save(Build(false, RecordingTimeProbe.Absent, null));
        var root = JsonNode.Parse(text)!;
        Assert.Null(root["training"]);
        Assert.Null(root["mediaAssets"]![0]!["recordingZeroOverrideUtc"]);
        var probe = root["mediaAssets"]![0]!["metadata"]!["recordingTimeProbe"]!.AsObject();
        Assert.Equal(new[] { "status" }, probe.Select(p => p.Key));

        var loaded = Load(text);
        Assert.Null(loaded.Training);
        Assert.Same(RecordingTimeProbe.Absent, loaded.MediaAssets[0].Metadata!.RecordingTimeProbe);
        Assert.Equal(text, Save(loaded));
    }

    [Fact]
    public void An_unprobed_video_has_no_probe_member_and_stays_unprobed()
    {
        var text = Save(Build(false, null, null));
        Assert.Null(JsonNode.Parse(text)!["mediaAssets"]![0]!["metadata"]!["recordingTimeProbe"]);
        Assert.Null(Load(text).MediaAssets[0].Metadata!.RecordingTimeProbe);
    }

    [Fact]
    public void A_version_10_file_opens_without_the_new_members_and_is_saved_as_version_11_differing_only_in_the_version()
    {
        var v11 = Save(Build(false, null, null));
        var v10 = v11.Replace("\"formatVersion\": 16", "\"formatVersion\": 10");
        Assert.NotEqual(v11, v10);

        var loaded = Load(v10);
        Assert.Null(loaded.Training);
        Assert.Equal(v11, Save(loaded));
    }

    [Fact]
    public void New_members_in_a_version_10_file_are_ignored()
    {
        var v10 = Save(Full()).Replace("\"formatVersion\": 16", "\"formatVersion\": 10");

        var loaded = Load(v10);
        Assert.Null(loaded.Training);
        Assert.Null(loaded.MediaAssets[0].RecordingZeroOverrideUtc);
        Assert.Null(loaded.MediaAssets[0].Metadata!.RecordingTimeProbe);
    }

    [Theory]
    [InlineData("sha", "\"" + "xyz" + "\"")]
    [InlineData("size", "-1")]
    [InlineData("path", "\"\"")]
    [InlineData("activity", "{ \"startUtc\": \"2026-09-27T08:00:00+00:00\", \"endUtc\": \"2026-09-27T07:00:00+00:00\", \"manufacturer\": 294 }")]
    public void A_malformed_training_is_a_damaged_file(string member, string value)
    {
        var root = JsonNode.Parse(Save(Full()))!;
        var training = root["training"]!.AsObject();
        var key = member switch { "sha" => "sha256", "size" => "fileSizeBytes", "path" => "filePath", _ => "activity" };
        training[key] = JsonNode.Parse(value);

        Assert.Throws<ProjectFileException>(() => Load(root.ToJsonString()));
    }

    [Theory]
    [InlineData("{ \"status\": \"found\" }")]
    [InlineData("{ \"status\": \"found\", \"tag\": \"x\", \"origin\": \"audio\" }")]
    [InlineData("{ \"status\": \"absent\", \"tag\": \"x\" }")]
    [InlineData("{ \"status\": \"maybe\" }")]
    public void A_malformed_probe_is_a_damaged_file(string probe)
    {
        var root = JsonNode.Parse(Save(Full()))!;
        root["mediaAssets"]![0]!["metadata"]!["recordingTimeProbe"] = JsonNode.Parse(probe);

        Assert.Throws<ProjectFileException>(() => Load(root.ToJsonString()));
    }

    [Fact]
    public void An_override_on_a_non_video_media_is_a_damaged_file()
    {
        var root = JsonNode.Parse(Save(Full()))!;
        root["mediaAssets"]![0]!["kind"] = "Audio";

        Assert.Throws<ProjectFileException>(() => Load(root.ToJsonString()));
    }

    [Fact]
    public void An_override_written_with_an_offset_is_read_as_the_same_instant_and_saved_in_utc()
    {
        var root = JsonNode.Parse(Save(Full()))!;
        root["mediaAssets"]![0]!["recordingZeroOverrideUtc"] = "2026-09-27T10:30:26.5+03:00";

        var loaded = Load(root.ToJsonString());
        Assert.Equal(new DateTimeOffset(2026, 9, 27, 7, 30, 26, 500, TimeSpan.Zero), loaded.MediaAssets[0].RecordingZeroOverrideUtc);
        Assert.Equal(TimeSpan.Zero, loaded.MediaAssets[0].RecordingZeroOverrideUtc!.Value.Offset);
        Assert.Contains("\"2026-09-27T07:30:26.5+00:00\"", Save(loaded));
    }

    [Fact]
    public void A_recovery_file_carries_the_training()
    {
        var text = ProjectSerializer.SerializeRecovery(Full(), new RecoveryInfo(Folder, DateTimeOffset.UnixEpoch, 1, DateTimeOffset.UnixEpoch));
        var (project, _) = ProjectSerializer.DeserializeRecovery(text, _ => true);

        Assert.Equal(Sha, project.Training!.Sha256);
        Assert.NotNull(project.MediaAssets[0].RecordingZeroOverrideUtc);
        Assert.True(project.MediaAssets[0].Metadata!.RecordingTimeProbe!.IsFound);
    }
}
