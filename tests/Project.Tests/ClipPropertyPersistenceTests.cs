using System.Text.Json.Nodes;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Project.Persistence;
using Xunit;

namespace AiVideoEditor.Project.Tests;

/// <summary>Phase 7 clip properties in project.json v1: exact round trip, validation on load
/// with the same rules as editing (D017), and compatibility with v1 files written before
/// <c>VideoClip.isMuted</c> existed.</summary>
public class ClipPropertyPersistenceTests
{
    private const string Folder = @"C:\Projects\Demo";
    private static readonly Func<string, bool> AllExist = _ => true;

    private const double Third = 1.0 / 3.0;
    private static readonly double PointThree = 0.1 + 0.2;

    private static Core.Entities.Project Sample() => ProjectTestData.Build(Folder + @"\media");

    private static Core.Entities.Project Load(string json) => ProjectSerializer.Deserialize(json, Folder, AllExist);

    private static ProjectFileException Rejected(string json) => Assert.Throws<ProjectFileException>(() => Load(json));

    private static JsonObject SampleJson() => JsonNode.Parse(ProjectSerializer.Serialize(Sample(), Folder))!.AsObject();

    private static JsonObject VideoClip(JsonObject r) => r["timeline"]!["videoTracks"]![0]!["clips"]![0]!.AsObject();
    private static JsonObject ImageClip(JsonObject r) => r["timeline"]!["videoTracks"]![0]!["clips"]![1]!.AsObject();
    private static JsonObject TextClip(JsonObject r) => r["timeline"]!["videoTracks"]![1]!["clips"]![0]!.AsObject();
    private static JsonObject AudioClip(JsonObject r) => r["timeline"]!["audioTracks"]![0]!["clips"]![0]!.AsObject();

    private static string Bits(double value) => BitConverter.DoubleToInt64Bits(value).ToString("X16");

    // --- Round trip --------------------------------------------------------------------------

    [Fact]
    public void All_phase7_properties_round_trip_bit_exactly()
    {
        var project = Sample();
        var v1 = project.Timeline.VideoTracks[0];
        var video = (VideoClip)v1.Clips[0];
        var image = (ImageClip)v1.Clips[1];
        var text = (TextClip)project.Timeline.VideoTracks[1].Clips[0];
        var audio = (AudioClip)project.Timeline.AudioTracks[0].Clips[0];

        (video.PositionX, video.PositionY, video.Scale, video.RotationDegrees, video.Opacity) = (-Third, 1e-7 + 123.456, PointThree, -359.999999999, Third);
        (video.Volume, video.IsMuted, video.Crop) = (1.9999999999999998, true, new CropRect(PointThree, Third, 0.1, 1e-12));
        (image.PositionX, image.PositionY, image.Scale, image.RotationDegrees, image.Opacity) = (100_000, -100_000, 0.01, 360, 0);
        image.Crop = new CropRect(0.5, 0.25, 0.4999999999999999, 0.75 - 1e-15); // sums just below 1
        (text.Text, text.FontFamily, text.FontSize, text.ColorHex, text.Alignment) =
            ("Line one\r\nLine two\n\tindented  ", "Times New Roman", 999.5, "#a0b1C2", TextAlignment.Left);
        (text.PositionX, text.PositionY, text.Scale, text.RotationDegrees, text.Opacity) = (Third, -PointThree, 10, -0.5, PointThree);
        (audio.Volume, audio.IsMuted) = (0, false);

        var json = ProjectSerializer.Serialize(project, Folder);
        var loaded = Load(json);

        var lv1 = loaded.Timeline.VideoTracks[0];
        var clipsBefore = new Clip[] { video, image, text, audio };
        var clipsAfter = new[] { lv1.Clips[0], lv1.Clips[1], loaded.Timeline.VideoTracks[1].Clips[0], loaded.Timeline.AudioTracks[0].Clips[0] };
        for (var i = 0; i < clipsBefore.Length; i++)
        {
            Assert.Equal(clipsBefore[i].GetType(), clipsAfter[i].GetType());
            Assert.Equal(VisualProperties.Of(clipsBefore[i]), VisualProperties.Of(clipsAfter[i]));
            Assert.Equal(AudioProperties.Of(clipsBefore[i]), AudioProperties.Of(clipsAfter[i]));
            Assert.Equal(TextProperties.Of(clipsBefore[i]), TextProperties.Of(clipsAfter[i]));
        }

        var loadedVideo = (VideoClip)lv1.Clips[0];
        Assert.Equal(Bits(Third), Bits(loadedVideo.Opacity));
        Assert.Equal(Bits(PointThree), Bits(loadedVideo.Crop.Left));
        Assert.Equal(Bits(1.9999999999999998), Bits(loadedVideo.Volume));
        Assert.True(loadedVideo.IsMuted);
        Assert.Equal("Line one\r\nLine two\n\tindented  ", ((TextClip)loaded.Timeline.VideoTracks[1].Clips[0]).Text);

        Assert.Equal(json, ProjectSerializer.Serialize(loaded, Folder)); // same file again
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Video_mute_is_written_and_read(bool muted)
    {
        var project = Sample();
        var video = (VideoClip)project.Timeline.VideoTracks[0].Clips[0];
        video.IsMuted = muted;
        video.Volume = 0.75;

        var root = JsonNode.Parse(ProjectSerializer.Serialize(project, Folder))!.AsObject();
        Assert.Equal(muted, VideoClip(root)["isMuted"]!.GetValue<bool>());

        var loaded = (VideoClip)Load(root.ToJsonString()).Timeline.VideoTracks[0].Clips[0];
        Assert.Equal(muted, loaded.IsMuted);
        Assert.Equal(0.75, loaded.Volume); // mute never touches the volume
    }

    // --- Compatibility -----------------------------------------------------------------------

    /// <summary>A v1 project.json exactly as Phase 6 wrote it: the video clip has no
    /// <c>isMuted</c> property.</summary>
    private const string Phase6File = """
        {
          "format": "AiVideoEditor.Project",
          "formatVersion": 1,
          "id": "0b3e4c1a-7f1d-4c52-9a58-2f6f0f3d9a01",
          "name": "Old Project",
          "createdAt": "2026-09-01T10:00:00+00:00",
          "modifiedAt": "2026-09-20T10:00:00+00:00",
          "settings": {
            "frameWidth": 1920,
            "frameHeight": 1080,
            "frameRate": { "numerator": 25, "denominator": 1 },
            "isFrameRateLocked": true,
            "audioSampleRate": 48000
          },
          "mediaAssets": [
            {
              "id": "6a1d2f53-1b77-4f1e-8d0e-7c1c5b3e2a10",
              "filePath": "C:\\Projects\\Demo\\media\\clip.mp4",
              "relativePath": "media\\clip.mp4",
              "fileSizeBytes": 1000,
              "kind": "Video",
              "importedAt": "2026-09-01T10:00:00+00:00",
              "thumbnailPath": null,
              "metadata": {
                "durationTicks": 100000000,
                "width": 1920,
                "height": 1080,
                "frameRate": { "numerator": 25, "denominator": 1 },
                "avgFrameRate": { "numerator": 25, "denominator": 1 },
                "startTimeTicks": 0,
                "videoCodec": "h264",
                "audioCodec": "aac",
                "audioChannels": 2,
                "audioSampleRate": 48000,
                "bitrateBps": 5000000
              }
            }
          ],
          "timeline": {
            "id": "9c0b8a57-3e2d-4d7b-a0f4-1f2e3d4c5b60",
            "name": "Main Sequence",
            "videoTracks": [
              {
                "id": "1f0e2d3c-4b5a-4968-8776-655443322110",
                "name": "V1",
                "order": 0,
                "isMuted": false,
                "isHidden": false,
                "isLocked": false,
                "clips": [
                  {
                    "type": "video",
                    "mediaAssetId": "6a1d2f53-1b77-4f1e-8d0e-7c1c5b3e2a10",
                    "sourceInTicks": 0,
                    "sourceOutTicks": 40000000,
                    "speed": 1,
                    "positionX": 0,
                    "positionY": 0,
                    "scale": 1,
                    "rotationDegrees": 0,
                    "opacity": 1,
                    "volume": 0.5,
                    "crop": { "left": 0, "top": 0, "right": 0, "bottom": 0 },
                    "id": "2a3b4c5d-6e7f-4081-92a3-b4c5d6e7f809",
                    "timelineStartTicks": 0,
                    "durationTicks": 40000000,
                    "effects": []
                  }
                ],
                "transitions": []
              }
            ],
            "audioTracks": [
              { "id": "3b4c5d6e-7f80-4192-a3b4-c5d6e7f8091a", "name": "A1", "order": 0, "isMuted": false, "isHidden": false, "isLocked": false, "clips": [], "transitions": [] }
            ],
            "markers": [],
            "playheadTicks": 0,
            "zoomPixelsPerSecond": 60,
            "snappingEnabled": true
          },
          "lastExportSettings": null
        }
        """;

    [Fact]
    public void Phase6_v1_file_without_isMuted_opens_unmuted()
    {
        var clipNode = JsonNode.Parse(Phase6File)!["timeline"]!["videoTracks"]![0]!["clips"]![0]!.AsObject();
        Assert.False(clipNode.ContainsKey("isMuted"));

        var loaded = Load(Phase6File);

        var video = Assert.IsType<VideoClip>(Assert.Single(loaded.Timeline.VideoTracks[0].Clips));
        Assert.False(video.IsMuted);
        Assert.Equal(0.5, video.Volume);
        Assert.Equal(new VisualProperties(0, 0, 1, 0, 1, CropRect.None), VisualProperties.Of(video));

        // Saved again, it is format v2 (Step 9, D022) and now carries the field.
        var resaved = JsonNode.Parse(ProjectSerializer.Serialize(loaded, Folder))!.AsObject();
        Assert.Equal(2, resaved["formatVersion"]!.GetValue<int>());
        Assert.False(VideoClip(resaved)["isMuted"]!.GetValue<bool>());
    }

    [Fact]
    public void Current_sample_without_isMuted_on_the_video_clip_opens_unmuted()
    {
        var root = SampleJson();
        Assert.True(VideoClip(root).Remove("isMuted"));

        var loaded = Load(root.ToJsonString());

        Assert.False(((VideoClip)loaded.Timeline.VideoTracks[0].Clips[0]).IsMuted);
        Assert.True(((AudioClip)loaded.Timeline.AudioTracks[0].Clips[0]).IsMuted); // audio clips kept theirs
    }

    // --- Damaged values ----------------------------------------------------------------------

    public static TheoryData<string, Action<JsonObject>> InvalidValues => new()
    {
        { "video opacity < 0", r => VideoClip(r)["opacity"] = -0.001 },
        { "video opacity > 1", r => VideoClip(r)["opacity"] = 1.5 },
        { "video scale 0", r => VideoClip(r)["scale"] = 0 },
        { "video scale above max", r => VideoClip(r)["scale"] = 10.5 },
        { "video rotation above max", r => VideoClip(r)["rotationDegrees"] = 361 },
        { "video position x too far", r => VideoClip(r)["positionX"] = 100_000.5 },
        { "video position y too far", r => VideoClip(r)["positionY"] = -1e9 },
        { "video volume above 200 %", r => VideoClip(r)["volume"] = 2.01 },
        { "video volume negative", r => VideoClip(r)["volume"] = -1 },
        { "video crop edge negative", r => VideoClip(r)["crop"]!["left"] = -0.1 },
        { "video crop edge 1", r => VideoClip(r)["crop"]!["bottom"] = 1 },
        { "video crop left+right = 1", r => { VideoClip(r)["crop"]!["left"] = 0.6; VideoClip(r)["crop"]!["right"] = 0.4; } },
        { "video crop top+bottom > 1", r => { VideoClip(r)["crop"]!["top"] = 0.7; VideoClip(r)["crop"]!["bottom"] = 0.5; } },
        { "image opacity > 1", r => ImageClip(r)["opacity"] = 2 },
        { "image scale negative", r => ImageClip(r)["scale"] = -1 },
        { "image crop edge above 1", r => ImageClip(r)["crop"] = new JsonObject { ["left"] = 0, ["top"] = 1.2, ["right"] = 0, ["bottom"] = 0 } },
        { "image scale missing (→ 0)", r => ImageClip(r).Remove("scale") },
        { "audio volume above 200 %", r => AudioClip(r)["volume"] = 3 },
        { "audio volume negative", r => AudioClip(r)["volume"] = -0.5 },
        { "text opacity negative", r => TextClip(r)["opacity"] = -1 },
        { "text rotation below min", r => TextClip(r)["rotationDegrees"] = -720 },
        { "text font size 0", r => TextClip(r)["fontSize"] = 0 },
        { "text font size above max", r => TextClip(r)["fontSize"] = 1001 },
        { "text empty font", r => TextClip(r)["fontFamily"] = "  " },
        { "text missing font", r => TextClip(r).Remove("fontFamily") },
        { "text color name", r => TextClip(r)["colorHex"] = "red" },
        { "text color with alpha", r => TextClip(r)["colorHex"] = "#FF00FF00" },
        { "text missing color", r => TextClip(r).Remove("colorHex") },
        { "text too long", r => TextClip(r)["text"] = new string('x', ClipPropertyLimits.MaxTextLength + 1) },
        { "text unknown alignment", r => TextClip(r)["alignment"] = "Justify" },
    };

    [Theory]
    [MemberData(nameof(InvalidValues))]
    public void Out_of_range_or_malformed_values_are_rejected(string description, Action<JsonObject> corrupt)
    {
        var root = SampleJson();
        corrupt(root);

        var ex = Rejected(root.ToJsonString());

        Assert.StartsWith("The project file is damaged", ex.Message);
        Assert.False(string.IsNullOrWhiteSpace(ex.Message), description);
    }

    [Fact]
    public void Range_errors_name_the_problem()
    {
        var root = SampleJson();
        VideoClip(root)["opacity"] = 1.5;

        Assert.Contains("Opacity must be between 0 % and 100 %", Rejected(root.ToJsonString()).Message);
    }

    /// <summary>Non-finite numbers can't be written as JSON numbers; whatever form they take in a
    /// damaged file (named literal, string, overflowing exponent), the file is rejected.</summary>
    [Theory]
    [InlineData("opacity", "NaN")]
    [InlineData("opacity", "\"NaN\"")]
    [InlineData("scale", "Infinity")]
    [InlineData("scale", "\"Infinity\"")]
    [InlineData("scale", "1e999")]
    [InlineData("rotationDegrees", "-1e999")]
    [InlineData("positionX", "\"-Infinity\"")]
    [InlineData("volume", "1e400")]
    public void Non_finite_values_are_rejected(string property, string literal)
    {
        const double Marker = 0.123456789;
        var root = SampleJson();
        VideoClip(root)[property] = Marker;
        var json = root.ToJsonString();
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(json, "0\\.123456789"));

        Rejected(json.Replace("0.123456789", literal));
    }

    [Fact]
    public void Properties_of_another_clip_kind_are_not_read()
    {
        // Each clip type has its own DTO, so a property of another kind can't reach the clip:
        // unknown properties are ignored (D014), and the clip keeps its own valid values.
        var root = SampleJson();
        TextClip(root)["crop"] = new JsonObject { ["left"] = 0.9, ["top"] = 0, ["right"] = 0.9, ["bottom"] = 0 };
        TextClip(root)["volume"] = 99;
        ImageClip(root)["isMuted"] = true;
        AudioClip(root)["opacity"] = -5;

        var loaded = Load(root.ToJsonString());

        Assert.Null(AudioProperties.Of(loaded.Timeline.VideoTracks[1].Clips[0]));
        Assert.Null(VisualProperties.Of(loaded.Timeline.AudioTracks[0].Clips[0]));
    }
}
