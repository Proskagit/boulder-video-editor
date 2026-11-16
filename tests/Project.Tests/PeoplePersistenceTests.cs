using System.Text.Json.Nodes;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Project.Persistence;
using Xunit;

namespace AiVideoEditor.Project.Tests;

/// <summary>
/// D061: <c>project.json</c> v20 stores a video clip's Follow (its picked climber and smoothness; written only when on) and
/// its outlines hidden behind people (written only when true); a v19 file opens with neither and is saved as v20; a damaged
/// Follow refuses the file; the analyses themselves are never in the project.
/// </summary>
public class PeoplePersistenceTests
{
    private const string Folder = @"C:\Projects\People";

    private static Core.Entities.Project Load(string json) => ProjectSerializer.Deserialize(json, Folder, _ => true);
    private static string Save(Core.Entities.Project project) => ProjectSerializer.Serialize(project, Folder);
    private static JsonObject Clip(JsonNode root, int index) => root["timeline"]!["videoTracks"]![0]!["clips"]![index]!.AsObject();

    private static Core.Entities.Project Build()
    {
        var project = new Core.Entities.Project { Name = "People" };
        var asset = new MediaAsset
        {
            FilePath = Folder + @"\media\climb.mp4", Kind = MediaKind.Video, AnalysisStatus = MediaAnalysisStatus.Completed,
            Metadata = new MediaMetadata { Duration = MediaTime.FromSeconds(10), FrameRate = FrameRate.Fps30 }
        };
        project.MediaAssets.Add(asset);
        var track = new Track { Type = TrackType.Video, Name = "V1" };
        track.Clips.Add(new VideoClip
        {
            MediaAssetId = asset.Id, Duration = MediaTime.FromSeconds(2), SourceOut = MediaTime.FromSeconds(2), Scale = 1.5,
            Follow = new FollowSettings(MediaTime.FromSeconds(1.25), new NormalizedPoint(0.375, 0.625), FollowSmoothness.Soft),
            HidesOutlinesBehindPeople = true
        });
        track.Clips.Add(new VideoClip
        {
            MediaAssetId = asset.Id, TimelineStart = MediaTime.FromSeconds(2), Duration = MediaTime.FromSeconds(2), SourceOut = MediaTime.FromSeconds(2),
            Follow = new FollowSettings()
        });
        track.Clips.Add(new VideoClip { MediaAssetId = asset.Id, TimelineStart = MediaTime.FromSeconds(4), Duration = MediaTime.FromSeconds(2), SourceOut = MediaTime.FromSeconds(2) });
        project.Timeline.VideoTracks.Add(track);
        project.Timeline.AudioTracks.Add(new Track { Type = TrackType.Audio, Name = "A1" });
        return project;
    }

    [Fact]
    public void Follow_and_Occlusion_survive_a_save_open_save_exactly_and_off_writes_nothing()
    {
        var first = Save(Build());
        var root = JsonNode.Parse(first)!;
        Assert.Equal(20, root["formatVersion"]!.GetValue<int>());
        var follow = Clip(root, 0)["follow"]!;
        Assert.Equal(12_500_000, follow["targetTicks"]!.GetValue<long>());
        Assert.Equal("soft", follow["smoothness"]!.GetValue<string>());
        Assert.True(Clip(root, 0)["hidesOutlinesBehindPeople"]!.GetValue<bool>());
        Assert.Null(Clip(root, 1)["follow"]!["targetTicks"]);
        Assert.Null(Clip(root, 2)["follow"]);
        Assert.Null(Clip(root, 2)["hidesOutlinesBehindPeople"]);

        var loaded = Load(first);
        var clips = loaded.Timeline.VideoTracks[0].Clips.OfType<VideoClip>().ToList();
        Assert.Equal(new FollowSettings(MediaTime.FromSeconds(1.25), new NormalizedPoint(0.375, 0.625), FollowSmoothness.Soft), clips[0].Follow);
        Assert.True(clips[0].HidesOutlinesBehindPeople);
        Assert.Equal(new FollowSettings(), clips[1].Follow);
        Assert.Null(clips[2].Follow);
        Assert.False(clips[2].HidesOutlinesBehindPeople);
        Assert.Equal(first, Save(loaded));
    }

    [Fact]
    public void A_version_19_file_opens_without_them_and_is_saved_as_20()
    {
        var root = JsonNode.Parse(Save(Build()))!;
        root["formatVersion"] = 19;
        var loaded = Load(root.ToJsonString());
        Assert.All(loaded.Timeline.VideoTracks[0].Clips.OfType<VideoClip>(), c =>
        {
            Assert.Null(c.Follow);
            Assert.False(c.HidesOutlinesBehindPeople);
        });
        Assert.Equal(20, JsonNode.Parse(Save(loaded))!["formatVersion"]!.GetValue<int>());
    }

    [Theory]
    [InlineData("smoothness", "jumpy")]
    [InlineData("targetX", "1.5")]
    [InlineData("targetTicks", "-1")]
    public void A_damaged_Follow_refuses_the_file(string member, string value)
    {
        var root = JsonNode.Parse(Save(Build()))!;
        Clip(root, 0)["follow"]![member] = JsonNode.Parse(member == "smoothness" ? $"\"{value}\"" : value);
        Assert.Throws<ProjectFileException>(() => Load(root.ToJsonString()));
    }

    [Fact]
    public void An_incomplete_target_refuses_the_file()
    {
        var root = JsonNode.Parse(Save(Build()))!;
        Clip(root, 0)["follow"]!.AsObject().Remove("targetY");
        Assert.Throws<ProjectFileException>(() => Load(root.ToJsonString()));
    }

    [Fact]
    public void Only_the_users_choice_is_in_the_project_never_the_analysis()
    {
        var follow = Clip(JsonNode.Parse(Save(Build()))!, 0)["follow"]!.AsObject();
        Assert.Equal(new[] { "smoothness", "targetTicks", "targetX", "targetY" }, follow.Select(p => p.Key).OrderBy(k => k, StringComparer.Ordinal));
    }
}
