using System.Collections.Immutable;
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
            Follow = FollowSettings.Whole(FollowSmoothness.Soft, MediaTime.FromSeconds(1.25), new NormalizedPoint(0.375, 0.625)),
            HidesOutlinesBehindPeople = true
        });
        track.Clips.Add(new VideoClip
        {
            MediaAssetId = asset.Id, TimelineStart = MediaTime.FromSeconds(2), Duration = MediaTime.FromSeconds(2), SourceOut = MediaTime.FromSeconds(2),
            Follow = FollowSettings.Whole()
        });
        track.Clips.Add(new VideoClip { MediaAssetId = asset.Id, TimelineStart = MediaTime.FromSeconds(4), Duration = MediaTime.FromSeconds(2), SourceOut = MediaTime.FromSeconds(2) });
        project.Timeline.VideoTracks.Add(track);
        project.Timeline.AudioTracks.Add(new Track { Type = TrackType.Audio, Name = "A1" });
        return project;
    }

    private static JsonNode Part(JsonNode root, int clip, int part = 0) => Clip(root, clip)["follow"]!["parts"]![part]!;

    [Fact]
    public void Follow_and_Occlusion_survive_a_save_open_save_exactly_and_off_writes_nothing()
    {
        var first = Save(Build());
        var root = JsonNode.Parse(first)!;
        Assert.Equal(27, root["formatVersion"]!.GetValue<int>());
        Assert.Equal(12_500_000, Part(root, 0)["targetTicks"]!.GetValue<long>());
        Assert.Equal("soft", Clip(root, 0)["follow"]!["smoothness"]!.GetValue<string>());
        Assert.True(Clip(root, 0)["hidesOutlinesBehindPeople"]!.GetValue<bool>());
        Assert.Null(Part(root, 1)["targetTicks"]);
        Assert.Null(Part(root, 1)["fromTicks"]);
        Assert.Null(Clip(root, 2)["follow"]);
        Assert.Null(Clip(root, 2)["hidesOutlinesBehindPeople"]);

        var loaded = Load(first);
        var clips = loaded.Timeline.VideoTracks[0].Clips.OfType<VideoClip>().ToList();
        Assert.Equal(FollowSettings.Whole(FollowSmoothness.Soft, MediaTime.FromSeconds(1.25), new NormalizedPoint(0.375, 0.625)), clips[0].Follow);
        Assert.True(clips[0].HidesOutlinesBehindPeople);
        Assert.Equal(FollowSettings.Whole(), clips[1].Follow);
        Assert.Null(clips[2].Follow);
        Assert.False(clips[2].HidesOutlinesBehindPeople);
        Assert.Equal(first, Save(loaded));
    }

    [Fact]
    public void A_version_19_file_opens_without_them_and_is_saved_as_the_current_version()
    {
        var root = JsonNode.Parse(Save(Build()))!;
        root["formatVersion"] = 19;
        var loaded = Load(root.ToJsonString());
        Assert.All(loaded.Timeline.VideoTracks[0].Clips.OfType<VideoClip>(), c =>
        {
            Assert.Null(c.Follow);
            Assert.False(c.HidesOutlinesBehindPeople);
        });
        Assert.Equal(27, JsonNode.Parse(Save(loaded))!["formatVersion"]!.GetValue<int>());
    }

    [Fact]
    public void Several_parts_each_with_its_person_survive_a_save_and_open_in_source_time()
    {
        var project = Build();
        var clip = project.Timeline.VideoTracks[0].Clips.OfType<VideoClip>().First();
        clip.Follow = new FollowSettings(ImmutableArray.Create(
            new FollowPart(MediaTime.FromSeconds(0.5), MediaTime.FromSeconds(1)),
            new FollowPart(MediaTime.FromSeconds(1), null, MediaTime.FromSeconds(1.5), new NormalizedPoint(0.25, 0.5))), FollowSmoothness.Fast);
        var json = Save(project);
        var root = JsonNode.Parse(json)!;
        Assert.Equal(5_000_000, Part(root, 0, 0)["fromTicks"]!.GetValue<long>());
        Assert.Equal(10_000_000, Part(root, 0, 0)["toTicks"]!.GetValue<long>());
        Assert.Null(Part(root, 0, 0)["targetTicks"]);
        Assert.Null(Part(root, 0, 1)["toTicks"]);
        Assert.Equal(15_000_000, Part(root, 0, 1)["targetTicks"]!.GetValue<long>());
        var loaded = Load(json).Timeline.VideoTracks[0].Clips.OfType<VideoClip>().First();
        Assert.Equal(clip.Follow, loaded.Follow);
        Assert.Equal(json, Save(Load(json)));
    }

    [Fact]
    public void A_version_20_file_follows_whole_clips_and_is_saved_as_the_current_version_with_nothing_else_changed()
    {
        var original = Save(Build());
        var root = JsonNode.Parse(original)!;
        root["formatVersion"] = 20;
        foreach (var clip in new[] { 0, 1 }) Legacy(root, clip);
        Clip(root, 0)["follow"]!["fromTicks"] = 5_000_000;                                  // never in a v20 file: ignored
        var loaded = Load(root.ToJsonString());
        var clips = loaded.Timeline.VideoTracks[0].Clips.OfType<VideoClip>().ToList();
        Assert.Equal(FollowSettings.Whole(FollowSmoothness.Soft, MediaTime.FromSeconds(1.25), new NormalizedPoint(0.375, 0.625)), clips[0].Follow);
        Assert.True(clips[0].HidesOutlinesBehindPeople);
        Assert.Equal(FollowSettings.Whole(), clips[1].Follow);
        Assert.Equal(original, Save(loaded));                                              // the current version, nothing else changed
    }

    [Fact]
    public void A_version_21_file_has_one_part_with_its_range()
    {
        var root = JsonNode.Parse(Save(Build()))!;
        root["formatVersion"] = 21;
        Legacy(root, 0);
        Clip(root, 0)["follow"]!["fromTicks"] = 5_000_000;
        Clip(root, 0)["follow"]!["toTicks"] = 15_000_000;
        var follow = Load(root.ToJsonString()).Timeline.VideoTracks[0].Clips.OfType<VideoClip>().First().Follow!;
        Assert.Equal(FollowSettings.Of(new FollowPart(MediaTime.FromSeconds(0.5), MediaTime.FromSeconds(1.5), MediaTime.FromSeconds(1.25),
            new NormalizedPoint(0.375, 0.625)), FollowSmoothness.Soft), follow);
    }

    /// <summary>The clip's Follow as a v20 / v21 file has it: the first part's members on the Follow itself, no parts.</summary>
    private static void Legacy(JsonNode root, int clip)
    {
        var follow = Clip(root, clip)["follow"]!.AsObject();
        var part = Part(root, clip).AsObject();
        foreach (var (key, value) in part.ToList()) follow[key] = value?.DeepClone();
        follow.Remove("parts");
    }

    [Fact]
    public void A_parts_offset_survives_and_a_version_22_part_has_none()
    {
        var project = Build();
        var clip = project.Timeline.VideoTracks[0].Clips.OfType<VideoClip>().First();
        clip.Follow = FollowSettings.Of(new FollowPart(OffsetX: 120.5, OffsetY: -40));
        var json = Save(project);
        Assert.Equal(120.5, Part(JsonNode.Parse(json)!, 0)["offsetX"]!.GetValue<double>());
        Assert.Null(Part(JsonNode.Parse(json)!, 1)["offsetX"]);               // 0: not written
        Assert.Equal(clip.Follow, Load(json).Timeline.VideoTracks[0].Clips.OfType<VideoClip>().First().Follow);

        var root = JsonNode.Parse(json)!;
        root["formatVersion"] = 22;
        Assert.Equal(FollowSettings.Of(new FollowPart()), Load(root.ToJsonString()).Timeline.VideoTracks[0].Clips.OfType<VideoClip>().First().Follow);

        root = JsonNode.Parse(json)!;
        Part(root, 0)["offsetX"] = 1e9;
        Assert.Throws<ProjectFileException>(() => Load(root.ToJsonString()));
    }

    [Fact]
    public void Parts_that_end_before_they_start_or_overlap_refuse_the_file()
    {
        var root = JsonNode.Parse(Save(Build()))!;
        Part(root, 0)["fromTicks"] = 10_000_000;
        Part(root, 0)["toTicks"] = 5_000_000;
        Assert.Throws<ProjectFileException>(() => Load(root.ToJsonString()));

        root = JsonNode.Parse(Save(Build()))!;
        var parts = Clip(root, 1)["follow"]!["parts"]!.AsArray();
        parts[0]!["toTicks"] = 10_000_000;
        parts.Add(JsonNode.Parse("{ \"fromTicks\": 5000000 }"));
        Assert.Throws<ProjectFileException>(() => Load(root.ToJsonString()));
    }

    [Theory]
    [InlineData("targetX", "1.5")]
    [InlineData("targetTicks", "-1")]
    public void A_damaged_Follow_refuses_the_file(string member, string value)
    {
        var root = JsonNode.Parse(Save(Build()))!;
        Part(root, 0)[member] = JsonNode.Parse(value);
        Assert.Throws<ProjectFileException>(() => Load(root.ToJsonString()));
        root = JsonNode.Parse(Save(Build()))!;
        Clip(root, 0)["follow"]!["smoothness"] = "jumpy";
        Assert.Throws<ProjectFileException>(() => Load(root.ToJsonString()));
    }

    [Fact]
    public void An_incomplete_target_refuses_the_file()
    {
        var root = JsonNode.Parse(Save(Build()))!;
        Part(root, 0).AsObject().Remove("targetY");
        Assert.Throws<ProjectFileException>(() => Load(root.ToJsonString()));
    }

    [Fact]
    public void Only_the_users_choice_is_in_the_project_never_the_analysis()
    {
        var root = JsonNode.Parse(Save(Build()))!;
        Assert.Equal(new[] { "parts", "smoothness" }, Clip(root, 0)["follow"]!.AsObject().Select(p => p.Key).OrderBy(k => k, StringComparer.Ordinal));
        Assert.Equal(new[] { "targetTicks", "targetX", "targetY" }, Part(root, 0).AsObject().Select(p => p.Key).OrderBy(k => k, StringComparer.Ordinal));
    }
}
