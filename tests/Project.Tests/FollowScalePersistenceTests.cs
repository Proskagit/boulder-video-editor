using System.Collections.Immutable;
using System.Text.Json.Nodes;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Project.Persistence;
using Xunit;

namespace AiVideoEditor.Project.Tests;

/// <summary>
/// D080: <c>project.json</c> v30 stores a followed part's «Динамический масштаб» (written only when on) and the user's share
/// (absent: automatic); a v29 file opens with it off everywhere and is saved as v30 differing only in the version; a share out of
/// its limits refuses the file.
/// </summary>
public class FollowScalePersistenceTests
{
    private const string Folder = @"C:\Projects\FollowScale";

    private static Core.Entities.Project Load(string json) => ProjectSerializer.Deserialize(json, Folder, _ => true);
    private static string Save(Core.Entities.Project project) => ProjectSerializer.Serialize(project, Folder);
    private static JsonArray Parts(JsonNode root) => root["timeline"]!["videoTracks"]![0]!["clips"]![0]!["follow"]!["parts"]!.AsArray();

    private static Core.Entities.Project Build(bool dynamic)
    {
        var project = new Core.Entities.Project { Name = "Follow scale" };
        var asset = new MediaAsset
        {
            FilePath = Folder + @"\media\climb.mp4", Kind = MediaKind.Video, AnalysisStatus = MediaAnalysisStatus.Completed,
            Metadata = new MediaMetadata { Duration = MediaTime.FromSeconds(10), FrameRate = FrameRate.Fps30 }
        };
        project.MediaAssets.Add(asset);
        var track = new Track { Type = TrackType.Video, Name = "V1" };
        track.Clips.Add(new VideoClip
        {
            MediaAssetId = asset.Id, Duration = MediaTime.FromSeconds(4), SourceOut = MediaTime.FromSeconds(4),
            Follow = new FollowSettings(ImmutableArray.Create(
                new FollowPart(null, MediaTime.FromSeconds(1), DynamicScale: dynamic),
                new FollowPart(MediaTime.FromSeconds(2), null, OffsetX: 5, DynamicScale: dynamic, ScaleShare: dynamic ? 0.375 : null)))
        });
        project.Timeline.VideoTracks.Add(track);
        project.Timeline.AudioTracks.Add(new Track { Type = TrackType.Audio, Name = "A1" });
        return project;
    }

    [Fact]
    public void The_dynamic_scale_and_the_share_survive_a_save_open_save_exactly()
    {
        var first = Save(Build(dynamic: true));
        var parts = Parts(JsonNode.Parse(first)!);
        Assert.True(parts[0]!["dynamicScale"]!.GetValue<bool>());
        Assert.Null(parts[0]!["scaleShare"]);                                  // automatic: not written
        Assert.Equal(0.375, parts[1]!["scaleShare"]!.GetValue<double>());

        var loaded = Load(first);
        var follow = loaded.Timeline.VideoTracks[0].Clips.OfType<VideoClip>().Single().Follow!;
        Assert.True(follow.Parts[0].DynamicScale);
        Assert.Null(follow.Parts[0].ScaleShare);
        Assert.Equal(0.375, follow.Parts[1].ScaleShare);
        Assert.Equal(first, Save(loaded));
    }

    [Fact]
    public void Off_nothing_is_written()
    {
        var parts = Parts(JsonNode.Parse(Save(Build(dynamic: false)))!);
        Assert.All(parts, p => { Assert.Null(p!["dynamicScale"]); Assert.Null(p!["scaleShare"]); });
    }

    [Fact]
    public void A_users_share_is_kept_while_the_scale_is_off()
    {
        var project = Build(dynamic: true);
        var clip = project.Timeline.VideoTracks[0].Clips.OfType<VideoClip>().Single();
        clip.Follow = clip.Follow!.WithPart(1, clip.Follow.Parts[1] with { DynamicScale = false });
        var part = Load(Save(project)).Timeline.VideoTracks[0].Clips.OfType<VideoClip>().Single().Follow!.Parts[1];
        Assert.False(part.DynamicScale);
        Assert.Equal(0.375, part.ScaleShare);
    }

    [Fact]
    public void A_version_29_file_opens_with_the_scale_as_before_and_is_saved_as_version_30_differing_only_in_the_version()
    {
        var current = Save(Build(dynamic: false));
        var loaded = Load(current.Replace("\"formatVersion\": 30", "\"formatVersion\": 29"));
        Assert.All(loaded.Timeline.VideoTracks[0].Clips.OfType<VideoClip>().Single().Follow!.Parts, p => Assert.False(p.DynamicScale));
        Assert.Equal(current, Save(loaded));

        // Members a v29 file cannot have are ignored there.
        var root = JsonNode.Parse(Save(Build(dynamic: true)))!;
        root["formatVersion"] = 29;
        var old = Load(root.ToJsonString()).Timeline.VideoTracks[0].Clips.OfType<VideoClip>().Single().Follow!;
        Assert.All(old.Parts, p => { Assert.False(p.DynamicScale); Assert.Null(p.ScaleShare); });
    }

    [Theory]
    [InlineData(0.01)]
    [InlineData(1.5)]
    public void A_share_out_of_its_limits_refuses_the_file(double share)
    {
        var root = JsonNode.Parse(Save(Build(dynamic: true)))!;
        Parts(root)[1]!["scaleShare"] = share;
        Assert.ThrowsAny<Exception>(() => Load(root.ToJsonString()));
    }
}
