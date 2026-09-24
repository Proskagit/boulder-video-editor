using System.Text.Json.Nodes;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Project.Persistence;
using Xunit;

namespace AiVideoEditor.Project.Tests;

/// <summary>Display orientation in project.json v1 (Phase 7 Step 6): optional fields, older metadata
/// kept but flagged for a background re-probe, inconsistent values dropped (D014).</summary>
public class MediaOrientationPersistenceTests
{
    private const string Folder = @"C:\Projects\Demo";
    private static readonly Func<string, bool> AllExist = _ => true;

    private static Core.Entities.Project Sample()
    {
        var project = ProjectTestData.Build(Folder + @"\media");
        var video = project.MediaAssets[0].Metadata!; // 3840 × 2160 coded
        (video.DisplayRotation, video.DisplayWidth, video.DisplayHeight) = (90, 2160, 3840);
        var image = project.MediaAssets[2].Metadata!;  // 640 × 480
        (image.DisplayRotation, image.DisplayWidth, image.DisplayHeight) = (0, 640, 480);
        return project;
    }

    private static JsonObject SampleJson() => JsonNode.Parse(ProjectSerializer.Serialize(Sample(), Folder))!.AsObject();

    private static JsonObject VideoMetadata(JsonObject root) => root["mediaAssets"]![0]!["metadata"]!.AsObject();

    private static Core.Entities.Project Load(JsonNode root) => ProjectSerializer.Deserialize(root.ToJsonString(), Folder, AllExist);

    [Fact]
    public void Orientation_round_trips_and_keeps_the_coded_size()
    {
        var loaded = ProjectSerializer.Deserialize(ProjectSerializer.Serialize(Sample(), Folder), Folder, AllExist);
        var m = loaded.MediaAssets[0].Metadata!;

        Assert.Equal((3840, 2160), (m.Width!.Value, m.Height!.Value));
        Assert.Equal((90, 2160, 3840), (m.DisplayRotation!.Value, m.DisplayWidth!.Value, m.DisplayHeight!.Value));
        Assert.False(m.NeedsDisplaySizeProbe);
        Assert.Equal((0, 640, 480), (loaded.MediaAssets[2].Metadata!.DisplayRotation!.Value,
            loaded.MediaAssets[2].Metadata!.DisplayWidth!.Value, loaded.MediaAssets[2].Metadata!.DisplayHeight!.Value));
    }

    [Fact]
    public void Unsupported_orientation_round_trips_as_null_rotation_with_a_display_size()
    {
        var project = Sample();
        (project.MediaAssets[0].Metadata!.DisplayRotation, project.MediaAssets[0].Metadata!.DisplayWidth,
            project.MediaAssets[0].Metadata!.DisplayHeight) = (null, 3840, 2160);

        var m = ProjectSerializer.Deserialize(ProjectSerializer.Serialize(project, Folder), Folder, AllExist).MediaAssets[0].Metadata!;

        Assert.Null(m.DisplayRotation);
        Assert.Equal((3840, 2160), (m.DisplayWidth!.Value, m.DisplayHeight!.Value));
        Assert.False(m.NeedsDisplaySizeProbe); // not re-probed on every open
    }

    [Fact]
    public void Metadata_saved_before_orientation_is_kept_and_flagged_for_a_re_probe()
    {
        var root = SampleJson();
        var json = VideoMetadata(root);
        json.Remove("displayRotation");
        json.Remove("displayWidth");
        json.Remove("displayHeight");

        var asset = Load(root).MediaAssets[0];

        Assert.Equal(MediaAnalysisStatus.Completed, asset.AnalysisStatus);  // still usable: duration etc. kept
        Assert.Equal(1_234_567_891, asset.Metadata!.Duration.Ticks);
        Assert.Equal((3840, 2160), (asset.Metadata.Width!.Value, asset.Metadata.Height!.Value));
        Assert.Null(asset.Metadata.DisplayWidth);
        Assert.True(asset.Metadata.NeedsDisplaySizeProbe);
    }

    [Fact]
    public void Audio_metadata_never_needs_a_display_size()
    {
        var asset = Load(SampleJson()).MediaAssets[1];
        Assert.Null(asset.Metadata!.DisplayWidth);
        Assert.False(asset.Metadata.NeedsDisplaySizeProbe);
    }

    public static TheoryData<string, Action<JsonObject>> Inconsistent => new()
    {
        { "odd rotation", m => m["displayRotation"] = 45 },
        { "negative rotation", m => m["displayRotation"] = -90 },
        { "width without height", m => m.Remove("displayHeight") },
        { "height without width", m => m.Remove("displayWidth") },
        { "zero width", m => m["displayWidth"] = 0 },
        { "negative height", m => m["displayHeight"] = -1 },
    };

    [Theory]
    [MemberData(nameof(Inconsistent))]
    public void Inconsistent_orientation_drops_the_metadata_and_the_media_is_analysed_again(string description, Action<JsonObject> corrupt)
    {
        var root = SampleJson();
        corrupt(VideoMetadata(root));

        var asset = Load(root).MediaAssets[0];

        Assert.True(asset.Metadata is null, description);
        Assert.Equal(MediaAnalysisStatus.Pending, asset.AnalysisStatus);
    }
}
