using System.Text.Json.Nodes;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Core.Telemetry;
using AiVideoEditor.Project.Persistence;
using Xunit;

namespace AiVideoEditor.Project.Tests;

/// <summary>
/// D056 §2: <c>project.json</c> v17 stores how a FIT area was laid out — its bounds as source ticks of its anchored clip and
/// its status moment — exactly; a v16 file's FIT areas open without it and are saved as v17; an invalid layout refuses the
/// file; a free template or an area made by hand never has one.
/// </summary>
public class FitLayoutPersistenceTests
{
    private const string Folder = @"C:\Projects\FitLayout";
    private static readonly FrameRate Rate = FrameRate.Fps25;
    private static readonly ClimbIdentity Climb = new(2, 2, new DateTimeOffset(2026, 9, 27, 7, 6, 57, TimeSpan.Zero));

    private static Core.Entities.Project Load(string json) => ProjectSerializer.Deserialize(json, Folder, _ => true);
    private static string Save(Core.Entities.Project project) => ProjectSerializer.Serialize(project, Folder);
    private static JsonObject AreaJson(JsonNode root, int index) => root["timeline"]!["videoTracks"]![1]!["clips"]![index]!.AsObject();

    private static Core.Entities.Project Build(params TemplateClip[] templates)
    {
        var project = new Core.Entities.Project
        {
            Name = "FitLayout",
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
            template.AnchorClipId ??= clip.Id;
            v2.Clips.Add(template);
            at += 300;
        }
        project.Timeline.VideoTracks.Add(v1);
        project.Timeline.VideoTracks.Add(v2);
        project.Timeline.AudioTracks.Add(new Track { Type = TrackType.Audio, Name = "A1" });
        return project;
    }

    private static TemplateClip FitArea(FitAreaLayout? layout) => new()
    {
        Kind = TemplateKind.Boulder, AccentHex = TemplateRules.DefaultAccentHex, AnchorRouteId = Guid.NewGuid(), FitClimb = Climb,
        StatusAt = MediaTime.FromFrame(120, Rate), FitLayout = layout
    };

    private static readonly FitAreaLayout Layout = new(MediaTime.FromFrame(275, Rate), MediaTime.FromFrame(1260, Rate), MediaTime.FromFrame(120, Rate));

    [Fact]
    public void A_fit_areas_layout_survives_a_save_open_save_exactly_as_version_17()
    {
        var hand = new TemplateClip { Kind = TemplateKind.Boulder, AccentHex = TemplateRules.DefaultAccentHex, AnchorRouteId = Guid.NewGuid() };
        var first = Save(Build(FitArea(Layout), hand));
        var root = JsonNode.Parse(first)!;
        Assert.Equal(19, root["formatVersion"]!.GetValue<int>());
        var layout = AreaJson(root, 0)["fitLayout"]!;
        Assert.Equal(MediaTime.FromFrame(275, Rate).Ticks, layout["sourceStartTicks"]!.GetValue<long>());
        Assert.Equal(MediaTime.FromFrame(1260, Rate).Ticks, layout["sourceEndTicks"]!.GetValue<long>());
        Assert.Equal(MediaTime.FromFrame(120, Rate).Ticks, layout["statusAtTicks"]!.GetValue<long>());
        Assert.Null(AreaJson(root, 1)["fitLayout"]);                                   // an area made by hand: none

        var loaded = Load(first);
        var areas = loaded.Timeline.VideoTracks[1].Clips.OfType<TemplateClip>().ToList();
        Assert.Equal(Layout, areas[0].FitLayout);
        Assert.Null(areas[1].FitLayout);
        Assert.Equal(first, Save(loaded));
    }

    [Fact]
    public void A_version_16_files_fit_area_opens_without_a_layout_and_is_saved_as_17()
    {
        var json = JsonNode.Parse(Save(Build(FitArea(Layout))))!;
        json["formatVersion"] = 16;
        AreaJson(json, 0).Remove("fitLayout");
        var v16 = json.ToJsonString();

        var loaded = Load(v16);

        var area = loaded.Timeline.VideoTracks[1].Clips.OfType<TemplateClip>().Single();
        Assert.Equal(Climb, area.FitClimb);
        Assert.Null(area.FitLayout);
        Assert.Equal(19, JsonNode.Parse(Save(loaded))!["formatVersion"]!.GetValue<int>());
    }

    [Fact]
    public void A_layout_in_a_version_16_file_is_ignored()
    {
        var json = JsonNode.Parse(Save(Build(FitArea(Layout))))!;
        json["formatVersion"] = 16;

        Assert.Null(Load(json.ToJsonString()).Timeline.VideoTracks[1].Clips.OfType<TemplateClip>().Single().FitLayout);
    }

    [Fact]
    public void An_empty_layout_refuses_the_file()
    {
        var json = JsonNode.Parse(Save(Build(FitArea(Layout))))!;
        AreaJson(json, 0)["fitLayout"]!["sourceEndTicks"] = MediaTime.FromFrame(275, Rate).Ticks;

        Assert.ThrowsAny<Exception>(() => Load(json.ToJsonString()));
    }

    [Fact]
    public void A_route_areas_T_survives_a_save_open_save_as_version_18_and_a_v17_file_has_none()
    {
        var area = FitArea(Layout);
        area.RouteStart = MediaTime.FromFrame(450, Rate);
        var free = new TemplateClip { Kind = TemplateKind.Boulder, AccentHex = TemplateRules.DefaultAccentHex, RouteStart = MediaTime.FromFrame(9, Rate) };
        var project = Build(area);
        project.Timeline.VideoTracks[1].Clips.Add(free);
        free.TimelineStart = MediaTime.FromFrame(600, Rate);
        free.Duration = MediaTime.FromFrame(100, Rate);
        free.AnchorClipId = null;

        var first = Save(project);
        var root = JsonNode.Parse(first)!;
        Assert.Equal(19, root["formatVersion"]!.GetValue<int>());
        Assert.Equal(MediaTime.FromFrame(450, Rate).Ticks, AreaJson(root, 0)["routeStartTicks"]!.GetValue<long>());
        Assert.Null(AreaJson(root, 1)["routeStartTicks"]);                             // a free template: never
        var loaded = Load(first);
        Assert.Equal(MediaTime.FromFrame(450, Rate), loaded.Timeline.VideoTracks[1].Clips.OfType<TemplateClip>().First().RouteStart);
        Assert.Equal(first, Save(loaded));

        root["formatVersion"] = 17;
        Assert.Null(Load(root.ToJsonString()).Timeline.VideoTracks[1].Clips.OfType<TemplateClip>().First().RouteStart);
    }

    [Fact]
    public void A_layout_without_the_fit_climb_is_not_saved()
    {
        var area = FitArea(Layout);
        area.FitClimb = null;

        Assert.Null(AreaJson(JsonNode.Parse(Save(Build(area)))!, 0)["fitLayout"]);
    }
}
