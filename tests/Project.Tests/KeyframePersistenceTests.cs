using System.Collections.Immutable;
using System.Text.Json.Nodes;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Composition;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Project.Persistence;
using Xunit;

namespace AiVideoEditor.Project.Tests;

/// <summary>
/// D063: <c>project.json</c> v24 stores a video clip's transform keyframes and a followed part's framing keyframes (their
/// ids, source times, values and easings; written only when there are some) and «Боулдер»'s route plate corner (written only
/// when not bottom left); a v23 file opens with none and is saved as v24 differing only in the version; damaged keyframes
/// refuse the file; an older build refuses a v24 file as newer.
/// </summary>
public class KeyframePersistenceTests
{
    private const string Folder = @"C:\Projects\Keyframes";

    private static Core.Entities.Project Load(string json) => ProjectSerializer.Deserialize(json, Folder, _ => true);
    private static string Save(Core.Entities.Project project) => ProjectSerializer.Serialize(project, Folder);
    private static JsonObject Clip(JsonNode root, int index) => root["timeline"]!["videoTracks"]![0]!["clips"]![index]!.AsObject();

    private static readonly TransformKeyframes ClipKeys = new(ImmutableArray.Create(
        new TransformKeyframe(Guid.Parse("11111111-1111-1111-1111-111111111111"), MediaTime.FromSeconds(0.5), 12.5, -40, 1.25, KeyframeEasing.EaseInOut),
        new TransformKeyframe(Guid.Parse("22222222-2222-2222-2222-222222222222"), MediaTime.FromSeconds(1.75), -300.125, 60, 2)));

    private static readonly TransformKeyframes PartKeys = new(ImmutableArray.Create(
        new TransformKeyframe(Guid.Parse("33333333-3333-3333-3333-333333333333"), MediaTime.FromSeconds(1), 20, 0, 2.5, KeyframeEasing.EaseOut)));

    private static Core.Entities.Project Build()
    {
        var project = new Core.Entities.Project { Name = "Keyframes" };
        var asset = new MediaAsset
        {
            FilePath = Folder + @"\media\climb.mp4", Kind = MediaKind.Video, AnalysisStatus = MediaAnalysisStatus.Completed,
            Metadata = new MediaMetadata { Duration = MediaTime.FromSeconds(10), FrameRate = FrameRate.Fps30 }
        };
        project.MediaAssets.Add(asset);
        var track = new Track { Type = TrackType.Video, Name = "V1" };
        track.Clips.Add(new VideoClip
        {
            MediaAssetId = asset.Id, Duration = MediaTime.FromSeconds(2), SourceOut = MediaTime.FromSeconds(2), Keyframes = ClipKeys,
            Follow = FollowSettings.Of(new FollowPart(MediaTime.FromSeconds(0.5), MediaTime.FromSeconds(1.5), OffsetX: 7, Keyframes: PartKeys))
        });
        track.Clips.Add(new VideoClip { MediaAssetId = asset.Id, TimelineStart = MediaTime.FromSeconds(2), Duration = MediaTime.FromSeconds(2), SourceOut = MediaTime.FromSeconds(2) });
        var templates = new Track { Type = TrackType.Video, Name = "V2", Order = 1 };
        templates.Clips.Add(new TemplateClip { Kind = TemplateKind.Boulder, Duration = MediaTime.FromSeconds(1), Route = "7", PlateCorner = OverlayCorner.TopRight });
        templates.Clips.Add(new TemplateClip { Kind = TemplateKind.Boulder, TimelineStart = MediaTime.FromSeconds(2), Duration = MediaTime.FromSeconds(1), Route = "8" });
        project.Timeline.VideoTracks.Add(track);
        project.Timeline.VideoTracks.Add(templates);
        project.Timeline.AudioTracks.Add(new Track { Type = TrackType.Audio, Name = "A1" });
        return project;
    }

    private static JsonObject Template(JsonNode root, int index) => root["timeline"]!["videoTracks"]![1]!["clips"]![index]!.AsObject();

    [Fact]
    public void The_keyframes_and_the_plate_corner_survive_a_save_open_save_exactly()
    {
        var first = Save(Build());
        var root = JsonNode.Parse(first)!;
        Assert.Equal(26, root["formatVersion"]!.GetValue<int>());
        var keys = Clip(root, 0)["keyframes"]!.AsArray();
        Assert.Equal(2, keys.Count);
        Assert.Equal(5_000_000, keys[0]!["timeTicks"]!.GetValue<long>());
        Assert.Equal("EaseInOut", keys[0]!["easing"]!.GetValue<string>());
        Assert.Null(keys[1]!["easing"]);                                       // linear: not written
        Assert.Null(Clip(root, 1)["keyframes"]);                               // none: nothing written
        Assert.Single(Clip(root, 0)["follow"]!["parts"]![0]!["keyframes"]!.AsArray());
        Assert.Equal("TopRight", Template(root, 0)["plateCorner"]!.GetValue<string>());
        Assert.Null(Template(root, 1)["plateCorner"]);                         // bottom left: not written

        var loaded = Load(first);
        var clips = loaded.Timeline.VideoTracks[0].Clips.OfType<VideoClip>().ToList();
        Assert.Equal(ClipKeys, clips[0].Keyframes);
        Assert.Equal(PartKeys, clips[0].Follow!.Parts[0].Keyframes);
        Assert.Equal(7, clips[0].Follow!.Parts[0].OffsetX);
        Assert.Null(clips[1].Keyframes);
        var plates = loaded.Timeline.VideoTracks[1].Clips.OfType<TemplateClip>().ToList();
        Assert.Equal(OverlayCorner.TopRight, plates[0].PlateCorner);
        Assert.Equal(OverlayCorner.BottomLeft, plates[1].PlateCorner);
        Assert.Equal(first, Save(loaded));
    }

    [Fact]
    public void A_version_23_file_opens_without_keyframes_and_is_saved_as_version_24_differing_only_in_the_version()
    {
        var project = Build();
        foreach (var clip in project.Timeline.VideoTracks[0].Clips.OfType<VideoClip>())
        {
            clip.Keyframes = null;
            if (clip.Follow is { } follow) clip.Follow = follow.WithPart(0, follow.Parts[0] with { Keyframes = null });
        }
        foreach (var plate in project.Timeline.VideoTracks[1].Clips.OfType<TemplateClip>()) plate.PlateCorner = OverlayCorner.BottomLeft;
        var current = Save(project);
        var v23 = current.Replace("\"formatVersion\": 26", "\"formatVersion\": 23");
        var loaded = Load(v23);
        Assert.All(loaded.Timeline.VideoTracks[0].Clips.OfType<VideoClip>(), c => Assert.Null(c.Keyframes));
        Assert.Equal(current, Save(loaded));

        // Members a v23 file cannot have are ignored there (never read as keyframes).
        var root = JsonNode.Parse(Save(Build()))!;
        root["formatVersion"] = 23;
        var old = Load(root.ToJsonString());
        Assert.Null(old.Timeline.VideoTracks[0].Clips.OfType<VideoClip>().First().Keyframes);
        Assert.Equal(OverlayCorner.BottomLeft, old.Timeline.VideoTracks[1].Clips.OfType<TemplateClip>().First().PlateCorner);
    }

    [Theory]
    [InlineData("order")]
    [InlineData("scale")]
    [InlineData("easing")]
    [InlineData("corner")]
    public void Damaged_keyframes_or_a_corner_refuse_the_file(string damage)
    {
        var root = JsonNode.Parse(Save(Build()))!;
        var keys = Clip(root, 0)["keyframes"]!.AsArray();
        switch (damage)
        {
            case "order": keys[1]!["timeTicks"] = 1_000; break;               // before the first
            case "scale": keys[0]!["scale"] = 0.0001; break;
            case "easing": keys[0]!["easing"] = "Bounce"; break;
            default: Template(root, 0)["plateCorner"] = "Middle"; break;
        }
        Assert.ThrowsAny<Exception>(() => Load(root.ToJsonString()));
    }

    [Fact]
    public void A_version_24_file_is_newer_for_a_version_23_build()
    {
        var json = Save(Build());
        Assert.False(ProjectSerializer.PeekNewerVersion(json, ProjectSerializer.FormatId));
        Assert.True(ProjectSerializer.PeekNewerVersion(json.Replace("\"formatVersion\": 26", "\"formatVersion\": 27"), ProjectSerializer.FormatId));
    }
}
