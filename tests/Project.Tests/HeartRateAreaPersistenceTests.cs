using System.Text.Json.Nodes;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Project.Persistence;
using Xunit;

namespace AiVideoEditor.Project.Tests;

/// <summary>
/// D055 §5 / §9: <c>project.json</c> v16 stores a green route area's own heart-rate settings — shown or not, the look, the
/// corner and the heart's colour — exactly; a v15 file's areas open showing the heart rate with the defaults and are saved
/// as v16; a v16 «Боулдер» without them refuses the file; the heart-rate overlay keeps its v13 members.
/// </summary>
public class HeartRateAreaPersistenceTests
{
    private const string Folder = @"C:\Projects\Areas";
    private static readonly FrameRate Rate = FrameRate.Fps25;

    private static Core.Entities.Project Load(string json) => ProjectSerializer.Deserialize(json, Folder, _ => true);
    private static string Save(Core.Entities.Project project) => ProjectSerializer.Serialize(project, Folder);
    private static JsonObject TemplateJson(JsonNode root, int index) => root["timeline"]!["videoTracks"]![1]!["clips"]![index]!.AsObject();

    private static Core.Entities.Project Build(params TemplateClip[] templates)
    {
        var project = new Core.Entities.Project
        {
            Name = "Areas",
            Settings = new ProjectSettings { FrameWidth = 1920, FrameHeight = 1080, FrameRate = Rate, IsFrameRateLocked = true, AudioSampleRate = 48000 }
        };
        var asset = new MediaAsset
        {
            FilePath = Folder + @"\media\climb.mp4", Kind = MediaKind.Video, AnalysisStatus = MediaAnalysisStatus.Completed,
            Metadata = new MediaMetadata { Duration = MediaTime.FromSeconds(100), FrameRate = Rate }
        };
        project.MediaAssets.Add(asset);
        var clip = new VideoClip
        {
            MediaAssetId = asset.Id, TimelineStart = MediaTime.Zero, Duration = MediaTime.FromFrame(1500, Rate), SourceIn = MediaTime.Zero,
            SourceOut = MediaTime.FromFrame(1500, Rate)
        };
        var v1 = new Track { Type = TrackType.Video, Name = "V1", Order = 0 };
        v1.Clips.Add(clip);
        var v2 = new Track { Type = TrackType.Video, Name = "V2", Order = 1 };
        long at = 0;
        foreach (var template in templates)
        {
            template.TimelineStart = MediaTime.FromFrame(at, Rate);
            template.Duration = MediaTime.FromFrame(300, Rate);
            if (template.Kind == TemplateKind.Boulder) template.AnchorClipId = clip.Id;
            v2.Clips.Add(template);
            at += 300;
        }
        project.Timeline.VideoTracks.Add(v1);
        project.Timeline.VideoTracks.Add(v2);
        project.Timeline.AudioTracks.Add(new Track { Type = TrackType.Audio, Name = "A1" });
        return project;
    }

    private static TemplateClip Area(bool show, HeartRateLook look, OverlayCorner corner, string heart) => new()
    {
        Kind = TemplateKind.Boulder, Route = "7", AccentHex = TemplateRules.DefaultAccentHex, AnchorRouteId = Guid.NewGuid(),
        ShowHeartRate = show, Look = look, Corner = corner, HeartHex = heart
    };

    [Fact]
    public void An_areas_heart_rate_settings_survive_a_save_open_save_exactly_as_version_16()
    {
        var project = Build(
            Area(false, HeartRateLook.Plain, OverlayCorner.TopLeft, "#00E676"),
            Area(true, HeartRateLook.Plate, OverlayCorner.BottomRight, TemplateRules.HeartRateAccentHex),
            new TemplateClip { Kind = TemplateKind.HeartRate, AccentHex = "#2979FF", Look = HeartRateLook.Plain, Corner = OverlayCorner.TopRight });

        var first = Save(project);
        var root = JsonNode.Parse(first)!;
        Assert.Equal(17, root["formatVersion"]!.GetValue<int>());
        Assert.False(TemplateJson(root, 0)["showHeartRate"]!.GetValue<bool>());
        Assert.Equal("#00E676", TemplateJson(root, 0)["heartColorHex"]!.GetValue<string>());
        Assert.Null(TemplateJson(root, 2)["showHeartRate"]);                 // the overlay: its v13 members only
        Assert.Null(TemplateJson(root, 2)["heartColorHex"]);

        var loaded = Load(first);
        var templates = loaded.Timeline.VideoTracks[1].Clips.OfType<TemplateClip>().ToList();
        Assert.Equal((false, HeartRateLook.Plain, OverlayCorner.TopLeft, "#00E676"),
            (templates[0].ShowHeartRate, templates[0].Look, templates[0].Corner, templates[0].HeartHex));
        Assert.Equal((HeartRateLook.Plain, OverlayCorner.TopRight, "#2979FF"), (templates[2].Look, templates[2].Corner, templates[2].AccentHex));
        Assert.Equal(first, Save(loaded));
    }

    [Fact]
    public void A_version_15_area_shows_the_heart_rate_with_the_defaults_and_is_saved_as_version_16()
    {
        var v15 = LegacyJson.As(Save(Build(Area(false, HeartRateLook.Plain, OverlayCorner.TopLeft, "#00E676"))), 15);
        Assert.DoesNotContain("showHeartRate", v15);

        var area = Load(v15).Timeline.VideoTracks[1].Clips.OfType<TemplateClip>().Single();
        Assert.Equal((true, HeartRateLook.Plate, OverlayCorner.BottomRight, TemplateRules.HeartRateAccentHex),
            (area.ShowHeartRate, area.Look, area.Corner, area.HeartHex));
        Assert.Equal(17, JsonNode.Parse(Save(Load(v15)))!["formatVersion"]!.GetValue<int>());
    }

    [Fact]
    public void A_version_16_area_without_its_heart_rate_settings_refuses_the_file()
    {
        foreach (var member in new[] { "showHeartRate", "heartColorHex", "look", "corner" })
        {
            var root = JsonNode.Parse(Save(Build(Area(true, HeartRateLook.Plate, OverlayCorner.BottomRight, "#FF1744"))))!;
            TemplateJson(root, 0).Remove(member);
            Assert.Throws<ProjectFileException>(() => Load(root.ToJsonString()));
        }
    }
}
