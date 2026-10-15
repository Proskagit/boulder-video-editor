using System.Collections.Immutable;
using System.Text.Json.Nodes;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Project.Persistence;
using Xunit;

namespace AiVideoEditor.Project.Tests;

/// <summary>
/// Phase 21 (D036 §2): <c>project.json</c> v6 stores the outline animation — every member survives a save / open / save
/// exactly (odd tick values); none is written without animation; a v5 file opens without animation (a stray member there
/// is ignored, as every older version ignores newer properties) and is saved as v6; a file newer than v6 is refused —
/// the same check by which a v5 build refuses a v6 file; a missing member or a value outside the limits refuses the file.
/// </summary>
public class HoldAnimationPersistenceTests
{
    private const string Folder = @"C:\Projects\Demo";
    private static readonly FrameRate Rate = FrameRate.Ntsc30;

    private static Core.Entities.Project Load(string json) => ProjectSerializer.Deserialize(json, Folder, _ => true);
    private static string Save(Core.Entities.Project project) => ProjectSerializer.Serialize(project, Folder);
    private static JsonObject Json(Core.Entities.Project project) => JsonNode.Parse(Save(project))!.AsObject();
    private static JsonObject MarkupJson(JsonObject root) => root["timeline"]!["videoTracks"]![0]!["clips"]![0]!["holdMarkup"]!.AsObject();
    private static ProjectFileException Rejected(JsonNode root) => Assert.Throws<ProjectFileException>(() => Load(root.ToJsonString()));

    private static readonly HoldAnimation Animation = new()
    {
        Order = HoldAnimationOrder.MarkingOrder, StartSourceTime = new MediaTime(123_456_789), Interval = new MediaTime(7_777_777),
        AppearFade = new MediaTime(1_234_567), StayAfterLast = new MediaTime(31_415_926), StayUntilClipEnd = true,
        DisappearFade = new MediaTime(2_718_281)
    };

    private static Core.Entities.Project Build(HoldAnimation? animation)
    {
        var project = new Core.Entities.Project
        {
            Name = "Animated",
            Settings = new ProjectSettings { FrameWidth = 1920, FrameHeight = 1080, FrameRate = Rate, IsFrameRateLocked = true, AudioSampleRate = 48000 }
        };
        var asset = new MediaAsset
        {
            FilePath = Folder + @"\media\boulder.mp4", Kind = MediaKind.Video, AnalysisStatus = MediaAnalysisStatus.Completed,
            Metadata = new MediaMetadata { Duration = MediaTime.FromSeconds(100), FrameRate = Rate }
        };
        project.MediaAssets.Add(asset);
        var markup = RouteColor.WithResolved(new HoldMarkup
        {
            Holds = ImmutableArray.Create(new Hold(Guid.NewGuid(), ImmutableArray.Create(new NormalizedPoint(0.1, 0.1),
                new NormalizedPoint(0.2, 0.1), new NormalizedPoint(0.15, 0.2)), new OkLabColor(0.6, 0.2, 0.1), new OkLabColor(0.9, 0, 0), MediaTime.Zero)),
            Animation = animation
        });
        var clip = new VideoClip
        {
            MediaAssetId = asset.Id, TimelineStart = MediaTime.Zero, Duration = MediaTime.FromFrame(300, Rate), SourceIn = MediaTime.Zero,
            SourceOut = MediaTime.FromFrame(300, Rate), HoldMarkup = markup
        };
        var track = new Track { Type = TrackType.Video, Name = "V1" };
        track.Clips.Add(clip);
        project.Timeline.VideoTracks.Add(track);
        project.Timeline.AudioTracks.Add(new Track { Type = TrackType.Audio, Name = "A1" });
        return project;
    }

    private static HoldAnimation? LoadedAnimation(Core.Entities.Project project) =>
        ((VideoClip)project.Timeline.VideoTracks[0].Clips[0]).HoldMarkup!.Animation;

    [Fact]
    public void Every_member_survives_a_save_open_save_exactly_as_version_6()
    {
        var first = Save(Build(Animation));
        Assert.Equal(6, JsonNode.Parse(first)!["formatVersion"]!.GetValue<int>());
        var reopened = Load(first);
        Assert.Equal(Animation, LoadedAnimation(reopened));
        Assert.Equal(first, Save(reopened));

        var json = MarkupJson(JsonNode.Parse(first)!.AsObject())["animation"]!;
        Assert.Equal("MarkingOrder", json["order"]!.GetValue<string>());
        Assert.Equal(123_456_789, json["startSourceTimeTicks"]!.GetValue<long>());
    }

    [Fact]
    public void Without_animation_nothing_is_written_and_nothing_is_read()
    {
        var root = Json(Build(null));
        Assert.Null(MarkupJson(root)["animation"]);
        Assert.Null(LoadedAnimation(Load(root.ToJsonString())));
    }

    [Fact]
    public void A_version_5_file_opens_without_animation_and_is_saved_as_version_6()
    {
        var root = Json(Build(null));
        root["formatVersion"] = 5;
        var project = Load(root.ToJsonString());
        Assert.Null(LoadedAnimation(project));
        Assert.Single(((VideoClip)project.Timeline.VideoTracks[0].Clips[0]).HoldMarkup!.Holds);
        Assert.Equal(6, JsonNode.Parse(Save(project))!["formatVersion"]!.GetValue<int>());

        var stray = Json(Build(Animation));
        stray["formatVersion"] = 5;
        Assert.Null(LoadedAnimation(Load(stray.ToJsonString())));
    }

    [Fact]
    public void A_file_newer_than_version_6_is_refused_the_check_a_version_5_build_applies_to_a_version_6_file()
    {
        Assert.Equal(6, ProjectSerializer.CurrentFormatVersion);
        var root = Json(Build(Animation));
        root["formatVersion"] = 7;
        Assert.Contains("newer version", Rejected(root).Message);
    }

    [Theory]
    [InlineData("order")]
    [InlineData("startSourceTimeTicks")]
    [InlineData("intervalTicks")]
    [InlineData("appearFadeTicks")]
    [InlineData("stayAfterLastTicks")]
    [InlineData("stayUntilClipEnd")]
    [InlineData("disappearFadeTicks")]
    public void An_animation_missing_a_member_refuses_the_file(string member)
    {
        var root = Json(Build(Animation));
        MarkupJson(root)["animation"]!.AsObject().Remove(member);
        Assert.Contains("damaged", Rejected(root).Message);
    }

    [Theory]
    [InlineData("intervalTicks", 0L)]
    [InlineData("intervalTicks", 100_000_001L)]
    [InlineData("appearFadeTicks", -1L)]
    [InlineData("disappearFadeTicks", 50_000_001L)]
    [InlineData("stayAfterLastTicks", 600_000_001L)]
    [InlineData("startSourceTimeTicks", -1L)]
    public void A_value_outside_the_limits_refuses_the_file(string member, long ticks)
    {
        var root = Json(Build(Animation));
        MarkupJson(root)["animation"]![member] = ticks;
        Assert.Contains("damaged", Rejected(root).Message);
    }

    [Fact]
    public void An_unknown_order_refuses_the_file()
    {
        var root = Json(Build(Animation));
        MarkupJson(root)["animation"]!["order"] = "TopToBottom";
        Assert.Throws<ProjectFileException>(() => Load(root.ToJsonString()));
    }
}
