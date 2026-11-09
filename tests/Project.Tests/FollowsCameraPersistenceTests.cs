using System.Collections.Immutable;
using System.Text.Json.Nodes;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Project.Persistence;
using Xunit;

namespace AiVideoEditor.Project.Tests;

/// <summary>
/// R2 → E, K3: <c>project.json</c> v14 stores a hold markup's "Follow the camera" off — written only then — exactly
/// (save / open / save byte for byte); a markup that follows the camera writes nothing new; a v13 file (every markup
/// follows the camera) opens unchanged and is saved as v14; a v13 file never reads the field.
/// </summary>
public class FollowsCameraPersistenceTests
{
    private const string Folder = @"C:\Projects\Demo";
    private static readonly FrameRate Rate = FrameRate.Fps30;

    private static Core.Entities.Project Load(string json) => ProjectSerializer.Deserialize(json, Folder, _ => true);
    private static string Save(Core.Entities.Project project) => ProjectSerializer.Serialize(project, Folder);

    private static Core.Entities.Project Build(bool follows)
    {
        var project = new Core.Entities.Project
        {
            Name = "Session",
            Settings = new ProjectSettings { FrameWidth = 1920, FrameHeight = 1080, FrameRate = Rate, IsFrameRateLocked = true, AudioSampleRate = 48000 }
        };
        var asset = new MediaAsset { FilePath = Path.Combine(Folder, "media", "climb.mp4"), Kind = MediaKind.Video };
        project.MediaAssets.Add(asset);
        var v1 = new Track { Type = TrackType.Video, Name = "V1" };
        var clip = new VideoClip
        {
            MediaAssetId = asset.Id, Duration = MediaTime.FromFrame(90, Rate),
            HoldMarkup = RouteColor.WithResolved(new HoldMarkup
            {
                Holds = ImmutableArray.Create(new Hold(Guid.NewGuid(),
                    ImmutableArray.Create(new NormalizedPoint(0.4, 0.4), new NormalizedPoint(0.6, 0.4), new NormalizedPoint(0.5, 0.6)),
                    RouteColor.HexToOkLab("#1E88E5"), RouteColor.HexToOkLab("#EEEEEE"), MediaTime.Zero)),
                FollowsCamera = follows
            })
        };
        clip.SourceOut = clip.Duration;
        v1.Clips.Add(clip);
        project.Timeline.VideoTracks.Add(v1);
        project.Timeline.AudioTracks.Add(new Track { Type = TrackType.Audio, Name = "A1" });
        return project;
    }

    private static JsonObject Markup(JsonNode root) => root["timeline"]!["videoTracks"]![0]!["clips"]![0] is JsonObject clip && clip["routes"] is JsonArray routes
        ? routes[0]!.AsObject()
        : root["timeline"]!["videoTracks"]![0]!["clips"]![0]!["holdMarkup"]!.AsObject();   // an older file (LegacyJson)
    private static HoldMarkup MarkupOf(Core.Entities.Project p) => p.Timeline.VideoTracks[0].Clips.OfType<VideoClip>().Single().HoldMarkup!;

    [Fact]
    public void Off_survives_a_save_open_save_exactly_and_on_writes_nothing()
    {
        var off = Save(Build(follows: false));
        var root = JsonNode.Parse(off)!;
        Assert.Equal(19, root["formatVersion"]!.GetValue<int>());
        Assert.False((bool)Markup(root)["followsCamera"]!);
        Assert.False(MarkupOf(Load(off)).FollowsCamera);
        Assert.Equal(off, Save(Load(off)));

        var on = Save(Build(follows: true));
        Assert.False(Markup(JsonNode.Parse(on)!).ContainsKey("followsCamera"));
        Assert.True(MarkupOf(Load(on)).FollowsCamera);
        Assert.Equal(on, Save(Load(on)));
    }

    [Fact]
    public void A_v13_file_follows_the_camera_and_is_saved_as_v14()
    {
        var v13 = LegacyJson.As(Save(Build(follows: true)), 13);
        var loaded = Load(v13);
        Assert.True(MarkupOf(loaded).FollowsCamera);
        Assert.Equal(19, JsonNode.Parse(Save(loaded))!["formatVersion"]!.GetValue<int>());

        // A v13 file never carries the field: if one does, it is not read (the older format had no such setting).
        var forged = JsonNode.Parse(v13)!;
        Markup(forged)["followsCamera"] = false;
        Assert.True(MarkupOf(Load(forged.ToJsonString())).FollowsCamera);
    }
}
