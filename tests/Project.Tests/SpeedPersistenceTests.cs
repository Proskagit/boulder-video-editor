using System.Text.Json.Nodes;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Project.Persistence;
using Xunit;

namespace AiVideoEditor.Project.Tests;

/// <summary>
/// Phase 7 Step 9 (D022): project.json v2 stores the clip speed as an exact fraction; v1 files are
/// read when their speed is 1 (anything else is damaged) and saved as v2; the timing invariant of
/// clips at other speeds is validated on load (project and recovery files).
/// </summary>
public class SpeedPersistenceTests
{
    private const string Folder = @"C:\Projects\Demo";
    private static readonly Func<string, bool> AllExist = _ => true;
    private static readonly FrameRate Rate = FrameRate.Fps25;
    private static MediaTime F(long n) => MediaTime.FromFrame(n, Rate);
    private static MediaTime S(double seconds) => MediaTime.FromSeconds(seconds);

    /// <summary>A v1 file as Phases 6–7 wrote it (speed as the number 1).</summary>
    private const string V1File = """
        {
          "format": "AiVideoEditor.Project",
          "formatVersion": 1,
          "id": "0b3e4c1a-7f1d-4c52-9a58-2f6f0f3d9a01",
          "name": "Old Project",
          "createdAt": "2026-09-01T10:00:00+00:00",
          "modifiedAt": "2026-09-20T10:00:00+00:00",
          "settings": { "frameWidth": 1920, "frameHeight": 1080, "frameRate": { "numerator": 25, "denominator": 1 }, "isFrameRateLocked": true, "audioSampleRate": 48000 },
          "mediaAssets": [
            {
              "id": "6a1d2f53-1b77-4f1e-8d0e-7c1c5b3e2a10", "filePath": "C:\\Projects\\Demo\\media\\clip.mp4", "relativePath": "media\\clip.mp4",
              "fileSizeBytes": 1000, "kind": "Video", "importedAt": "2026-09-01T10:00:00+00:00", "thumbnailPath": null,
              "metadata": { "durationTicks": 100000000, "width": 1920, "height": 1080, "frameRate": { "numerator": 25, "denominator": 1 },
                            "avgFrameRate": { "numerator": 25, "denominator": 1 }, "startTimeTicks": 0, "videoCodec": "h264", "audioCodec": "aac",
                            "audioChannels": 2, "audioSampleRate": 48000, "bitrateBps": 5000000 }
            }
          ],
          "timeline": {
            "id": "9c0b8a57-3e2d-4d7b-a0f4-1f2e3d4c5b60", "name": "Main Sequence",
            "videoTracks": [
              { "id": "1f0e2d3c-4b5a-4968-8776-655443322110", "name": "V1", "order": 0, "isMuted": false, "isHidden": false, "isLocked": false,
                "clips": [
                  { "type": "video", "mediaAssetId": "6a1d2f53-1b77-4f1e-8d0e-7c1c5b3e2a10", "sourceInTicks": 10000000, "sourceOutTicks": 70000000,
                    "speed": 1, "positionX": 0, "positionY": 0, "scale": 1, "rotationDegrees": 0, "opacity": 1, "volume": 1,
                    "crop": { "left": 0, "top": 0, "right": 0, "bottom": 0 }, "id": "2a3b4c5d-6e7f-4081-92a3-b4c5d6e7f809",
                    "timelineStartTicks": 40000000, "durationTicks": 60000000, "effects": [] }
                ],
                "transitions": [] }
            ],
            "audioTracks": [ { "id": "3b4c5d6e-7f80-4192-a3b4-c5d6e7f8091a", "name": "A1", "order": 0, "isMuted": false, "isHidden": false, "isLocked": false, "clips": [], "transitions": [] } ],
            "markers": [], "playheadTicks": 0, "zoomPixelsPerSecond": 60, "snappingEnabled": true
          },
          "lastExportSettings": null
        }
        """;

    private static Core.Entities.Project Load(string json) => ProjectSerializer.Deserialize(json, Folder, AllExist);
    private static ProjectFileException Rejected(string json) => Assert.Throws<ProjectFileException>(() => Load(json));
    private static JsonObject Clip(JsonNode root) => root["timeline"]!["videoTracks"]![0]!["clips"]![0]!.AsObject();

    // --- v1 → v2 ---------------------------------------------------------------------------------------

    [Fact]
    public void A_v1_file_loads_at_normal_speed_and_is_saved_as_v2()
    {
        var loaded = Load(V1File);
        var video = Assert.IsType<VideoClip>(Assert.Single(loaded.Timeline.VideoTracks[0].Clips));
        Assert.Equal(ClipSpeed.Normal, video.Speed);
        Assert.Equal((S(4), S(6), S(1), S(7)), (video.TimelineStart, video.Duration, video.SourceIn, video.SourceOut));

        var saved = JsonNode.Parse(ProjectSerializer.Serialize(loaded, Folder))!;
        Assert.Equal(2, saved["formatVersion"]!.GetValue<int>());
        var clip = Clip(saved);
        Assert.False(clip.ContainsKey("speed"));                           // the v1 number is not written
        Assert.Equal(1, clip["speedRatio"]!["numerator"]!.GetValue<long>());
        Assert.Equal(1, clip["speedRatio"]!["denominator"]!.GetValue<long>());

        var reloaded = Assert.IsType<VideoClip>(Load(saved.ToJsonString()).Timeline.VideoTracks[0].Clips[0]);
        Assert.Equal((video.TimelineStart, video.Duration, video.SourceIn, video.SourceOut, video.Speed),
            (reloaded.TimelineStart, reloaded.Duration, reloaded.SourceIn, reloaded.SourceOut, reloaded.Speed));
    }

    [Theory]
    [InlineData(2.0)]
    [InlineData(0.5)]
    [InlineData(1.0000001)]
    [InlineData(0.0)]
    public void A_v1_file_with_a_speed_other_than_1_is_damaged(double speed)
    {
        var root = JsonNode.Parse(V1File)!;
        Clip(root)["speed"] = speed;
        Assert.Contains("damaged", Rejected(root.ToJsonString()).Message);
    }

    [Fact]
    public void A_v1_clip_without_speed_is_damaged()
    {
        var root = JsonNode.Parse(V1File)!;
        Clip(root).Remove("speed");
        Rejected(root.ToJsonString());
    }

    // --- v2 --------------------------------------------------------------------------------------------

    /// <summary>25 fps; video 1.35× (source 1–7 s → 111 frames from frame 100), audio 0.25× (source
    /// 0–2 s → 200 frames), image at 1×.</summary>
    private static Core.Entities.Project SpeedProject()
    {
        var p = new Core.Entities.Project { Name = "Speed" };
        p.Settings.FrameRate = Rate;
        p.Settings.IsFrameRateLocked = true;
        var video = new MediaAsset
        {
            FilePath = Folder + @"\media\v.mp4", Kind = MediaKind.Video, AnalysisStatus = MediaAnalysisStatus.Completed,
            Metadata = new MediaMetadata { Duration = S(10), FrameRate = Rate, Width = 1920, Height = 1080, AudioCodec = "aac" }
        };
        var audio = new MediaAsset
        {
            FilePath = Folder + @"\media\a.wav", Kind = MediaKind.Audio, AnalysisStatus = MediaAnalysisStatus.Completed,
            Metadata = new MediaMetadata { Duration = S(10), AudioCodec = "pcm_s16le" }
        };
        p.MediaAssets.Add(video);
        p.MediaAssets.Add(audio);
        p.Timeline.VideoTracks.Add(new Track { Type = TrackType.Video, Name = "V1" });
        p.Timeline.AudioTracks.Add(new Track { Type = TrackType.Audio, Name = "A1" });
        p.Timeline.VideoTracks[0].Clips.Add(new VideoClip
        {
            MediaAssetId = video.Id, TimelineStart = F(100), Duration = F(211) - F(100), SourceIn = S(1), SourceOut = S(7),
            Speed = ClipSpeed.FromSteps(27)
        });
        p.Timeline.AudioTracks[0].Clips.Add(new AudioClip
        {
            MediaAssetId = audio.Id, TimelineStart = F(0), Duration = F(200), SourceIn = S(0), SourceOut = S(2), Speed = ClipSpeed.Min
        });
        return p;
    }

    [Fact]
    public void V2_stores_the_exact_speed_and_the_source_range_and_reads_them_back()
    {
        var project = SpeedProject();
        var json = ProjectSerializer.Serialize(project, Folder);
        var root = JsonNode.Parse(json)!;
        Assert.Equal(2, root["formatVersion"]!.GetValue<int>());
        Assert.Equal(27, Clip(root)["speedRatio"]!["numerator"]!.GetValue<long>());
        Assert.Equal(20, Clip(root)["speedRatio"]!["denominator"]!.GetValue<long>());

        var loaded = Load(json);
        var video = (VideoClip)loaded.Timeline.VideoTracks[0].Clips[0];
        var audio = (AudioClip)loaded.Timeline.AudioTracks[0].Clips[0];
        Assert.Equal(ClipSpeed.FromSteps(27), video.Speed);
        Assert.Equal((F(100), F(211) - F(100), S(1), S(7)), (video.TimelineStart, video.Duration, video.SourceIn, video.SourceOut));
        Assert.Equal(ClipSpeed.Min, audio.Speed);
        Assert.Equal((F(200), S(0), S(2)), (audio.Duration, audio.SourceIn, audio.SourceOut));
        Assert.Equal(json, ProjectSerializer.Serialize(loaded, Folder));  // byte-identical round trip
    }

    [Fact]
    public void A_recovery_file_keeps_the_speed()
    {
        var info = new RecoveryInfo(Folder, DateTimeOffset.UnixEpoch, 1, DateTimeOffset.UnixEpoch);
        var (loaded, _) = ProjectSerializer.DeserializeRecovery(ProjectSerializer.SerializeRecovery(SpeedProject(), info), AllExist);
        Assert.Equal(ClipSpeed.FromSteps(27), ((VideoClip)loaded.Timeline.VideoTracks[0].Clips[0]).Speed);
    }

    public static TheoryData<string, Action<JsonObject>> Damages => new()
    {
        { "not a multiple of 0.05", c => c["speedRatio"] = new JsonObject { ["numerator"] = 1, ["denominator"] = 3 } },
        { "above 4x", c => c["speedRatio"] = new JsonObject { ["numerator"] = 81, ["denominator"] = 20 } },
        { "below 0.25x", c => c["speedRatio"] = new JsonObject { ["numerator"] = 1, ["denominator"] = 5 } },
        { "zero", c => c["speedRatio"] = new JsonObject { ["numerator"] = 0, ["denominator"] = 1 } },
        { "zero denominator", c => c["speedRatio"] = new JsonObject { ["numerator"] = 1, ["denominator"] = 0 } },
        { "negative", c => c["speedRatio"] = new JsonObject { ["numerator"] = -2, ["denominator"] = -1 } },
        { "missing", c => c.Remove("speedRatio") },
        { "v1 number in a v2 file", c => c["speed"] = 1.35 },
        { "one frame too long", c => c["durationTicks"] = (F(212) - F(100)).Ticks },
        { "one frame too short", c => c["durationTicks"] = (F(210) - F(100)).Ticks },
        { "source range too short", c => c["sourceOutTicks"] = S(7).Ticks - 540_001 },
    };

    [Theory]
    [MemberData(nameof(Damages))]
    public void Invalid_speed_data_in_v2_is_damaged(string what, Action<JsonObject> damage)
    {
        var root = JsonNode.Parse(ProjectSerializer.Serialize(SpeedProject(), Folder))!;
        damage(Clip(root));
        Assert.Contains("damaged", Rejected(root.ToJsonString()).Message);
        _ = what;
    }

    [Fact]
    public void An_unused_tail_shorter_than_one_frame_is_valid()
    {
        // 111 frames at 1.35× use 5.994 s of the 6 s range; up to (but excluding) 112 frames' worth fits.
        var root = JsonNode.Parse(ProjectSerializer.Serialize(SpeedProject(), Folder))!;
        Clip(root)["sourceOutTicks"] = S(1).Ticks + 60_479_999;
        Assert.Equal(S(1) + new MediaTime(60_479_999), ((VideoClip)Load(root.ToJsonString()).Timeline.VideoTracks[0].Clips[0]).SourceOut);
    }

    [Fact]
    public void An_image_clip_with_a_speed_is_damaged()
    {
        var project = SpeedProject();
        var image = new MediaAsset { FilePath = Folder + @"\media\i.png", Kind = MediaKind.Image, AnalysisStatus = MediaAnalysisStatus.Completed };
        project.MediaAssets.Add(image);
        project.Timeline.VideoTracks[0].Clips.Add(new ImageClip
        {
            MediaAssetId = image.Id, TimelineStart = F(300), Duration = F(25), SourceIn = MediaTime.Zero, SourceOut = F(25)
        });
        var root = JsonNode.Parse(ProjectSerializer.Serialize(project, Folder))!;
        var imageClip = root["timeline"]!["videoTracks"]![0]!["clips"]![1]!.AsObject();
        Assert.Equal("image", imageClip["type"]!.GetValue<string>());
        imageClip["speedRatio"] = new JsonObject { ["numerator"] = 2, ["denominator"] = 1 };

        Assert.Contains("damaged", Rejected(root.ToJsonString()).Message);
    }
}
