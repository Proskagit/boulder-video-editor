using System.Collections.Immutable;
using System.Text.Json.Nodes;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Project.Persistence;
using Xunit;

namespace AiVideoEditor.Project.Tests;

/// <summary>
/// D048 §3.7: <c>project.json</c> v10 stores the Duration mode, its length, the orders left to right / right to left and a
/// template's anchor — saved and opened exactly; an interval-mode animation is written as in v9; a v9 file opens with every
/// animation in the interval mode and no anchor and is saved as v10; v9 with a v10 value, a duration without its mode and a
/// mode without its duration are refused; a dangling anchor is cleared on open, not a damaged file.
/// </summary>
public class RouteTimingPersistenceTests
{
    private const string Folder = @"C:\Projects\Demo";
    private static readonly FrameRate Rate = FrameRate.Ntsc30;

    private static Core.Entities.Project Load(string json) => ProjectSerializer.Deserialize(json, Folder, _ => true);
    private static string Save(Core.Entities.Project project) => ProjectSerializer.Serialize(project, Folder);
    private static JsonObject AnimationJson(JsonNode root) => root["timeline"]!["videoTracks"]![0]!["clips"]![0]!["holdMarkup"]!["animation"]!.AsObject();
    private static JsonObject TemplateJson(JsonNode root) => root["timeline"]!["videoTracks"]![1]!["clips"]![0]!.AsObject();

    private static readonly HoldAnimation Duration = new()
    {
        Mode = HoldAnimationMode.Duration, Order = HoldAnimationOrder.RightToLeft, StartSourceTime = new MediaTime(123_456_789),
        Duration = new MediaTime(50_000_001), AppearFade = new MediaTime(2_500_000)
    };

    private static Core.Entities.Project Build(HoldAnimation? animation, bool anchored)
    {
        var project = new Core.Entities.Project
        {
            Name = "Route",
            Settings = new ProjectSettings { FrameWidth = 1920, FrameHeight = 1080, FrameRate = Rate, IsFrameRateLocked = true, AudioSampleRate = 48000 }
        };
        var asset = new MediaAsset
        {
            FilePath = Folder + @"\media\boulder.mp4", Kind = MediaKind.Video, AnalysisStatus = MediaAnalysisStatus.Completed,
            Metadata = new MediaMetadata { Duration = MediaTime.FromSeconds(100), FrameRate = Rate }
        };
        project.MediaAssets.Add(asset);
        var clip = new VideoClip
        {
            MediaAssetId = asset.Id, TimelineStart = MediaTime.Zero, Duration = MediaTime.FromFrame(300, Rate), SourceIn = MediaTime.Zero,
            SourceOut = MediaTime.FromFrame(300, Rate),
            HoldMarkup = RouteColor.WithResolved(new HoldMarkup
            {
                Holds = ImmutableArray.Create(new Hold(Guid.NewGuid(), ImmutableArray.Create(new NormalizedPoint(0.1, 0.1),
                    new NormalizedPoint(0.2, 0.1), new NormalizedPoint(0.15, 0.2)), new OkLabColor(0.6, 0.2, 0.1), new OkLabColor(0.9, 0, 0), MediaTime.Zero)),
                Animation = animation
            })
        };
        var v1 = new Track { Type = TrackType.Video, Name = "V1", Order = 0 };
        v1.Clips.Add(clip);
        var v2 = new Track { Type = TrackType.Video, Name = "V2", Order = 1 };
        v2.Clips.Add(new TemplateClip
        {
            Kind = TemplateKind.Boulder, TimelineStart = MediaTime.FromFrame(30, Rate), Duration = MediaTime.FromFrame(270, Rate),
            StatusAt = MediaTime.FromFrame(120, Rate), Status = BoulderStatus.Top, AnchorClipId = anchored ? clip.Id : null
        });
        project.Timeline.VideoTracks.Add(v1);
        project.Timeline.VideoTracks.Add(v2);
        project.Timeline.AudioTracks.Add(new Track { Type = TrackType.Audio, Name = "A1" });
        return project;
    }

    private static VideoClip Video(Core.Entities.Project p) => (VideoClip)p.Timeline.VideoTracks[0].Clips[0];
    private static TemplateClip Template(Core.Entities.Project p) => (TemplateClip)p.Timeline.VideoTracks[1].Clips[0];

    [Fact]
    public void The_duration_mode_and_the_anchor_survive_a_save_open_save_exactly_as_version_10()
    {
        var project = Build(Duration, anchored: true);
        var first = Save(project);
        var root = JsonNode.Parse(first)!;
        Assert.Equal(10, root["formatVersion"]!.GetValue<int>());
        Assert.Equal("Duration", AnimationJson(root)["mode"]!.GetValue<string>());
        Assert.Equal(50_000_001, AnimationJson(root)["durationTicks"]!.GetValue<long>());
        Assert.Equal(Video(project).Id, TemplateJson(root)["anchorClipId"]!.GetValue<Guid>());

        var loaded = Load(first);
        Assert.Equal(Duration, Video(loaded).HoldMarkup!.Animation);
        Assert.Equal(Video(loaded).Id, Template(loaded).AnchorClipId);
        Assert.Equal(first, Save(loaded));
    }

    [Fact]
    public void An_interval_animation_and_a_free_template_are_written_without_the_new_members()
    {
        var root = JsonNode.Parse(Save(Build(new HoldAnimation(), anchored: false)))!;
        Assert.False(AnimationJson(root).ContainsKey("mode"));
        Assert.False(AnimationJson(root).ContainsKey("durationTicks"));
        Assert.False(TemplateJson(root).ContainsKey("anchorClipId"));
    }

    [Fact]
    public void A_version_9_file_opens_in_the_interval_mode_without_anchors_and_is_saved_as_version_10()
    {
        var root = JsonNode.Parse(Save(Build(new HoldAnimation(), anchored: false)))!;
        root["formatVersion"] = 9;
        TemplateJson(root)["anchorClipId"] = Guid.NewGuid().ToString();   // a stray member of a newer version is ignored by v9
        var loaded = Load(root.ToJsonString());
        Assert.Equal(HoldAnimationMode.Interval, Video(loaded).HoldMarkup!.Animation!.Mode);
        Assert.Null(Template(loaded).AnchorClipId);
        Assert.Equal(10, JsonNode.Parse(Save(loaded))!["formatVersion"]!.GetValue<int>());
    }

    [Fact]
    public void A_v10_value_in_a_v9_file_and_a_half_written_duration_mode_are_refused()
    {
        var newOrder = JsonNode.Parse(Save(Build(Duration with { Mode = HoldAnimationMode.Interval, Duration = MediaTime.Zero }, anchored: false)))!;
        newOrder["formatVersion"] = 9;
        Assert.Throws<ProjectFileException>(() => Load(newOrder.ToJsonString()));            // right to left is v10's

        var noDuration = JsonNode.Parse(Save(Build(Duration, anchored: false)))!;
        AnimationJson(noDuration).Remove("durationTicks");
        Assert.Throws<ProjectFileException>(() => Load(noDuration.ToJsonString()));

        var noMode = JsonNode.Parse(Save(Build(Duration, anchored: false)))!;
        AnimationJson(noMode).Remove("mode");
        Assert.Throws<ProjectFileException>(() => Load(noMode.ToJsonString()));
    }

    [Fact]
    public void A_dangling_anchor_is_cleared_on_open_not_a_damaged_file()
    {
        var root = JsonNode.Parse(Save(Build(Duration, anchored: true)))!;
        TemplateJson(root)["anchorClipId"] = Guid.NewGuid().ToString();
        Assert.Null(Template(Load(root.ToJsonString())).AnchorClipId);
    }
}
