using System.Text.Json.Nodes;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Project.Persistence;
using Xunit;

namespace AiVideoEditor.Project.Tests;

public class ProjectSerializerValidationTests
{
    private const string Folder = @"C:\Projects\Demo";
    private static readonly Func<string, bool> AllExist = _ => true;

    private static JsonObject SampleJson() =>
        JsonNode.Parse(ProjectSerializer.Serialize(ProjectTestData.Build(Folder + @"\media"), Folder))!.AsObject();

    private static JsonObject FirstVideoClip(JsonObject root) => root["timeline"]!["videoTracks"]![0]!["clips"]![0]!.AsObject();

    private static ProjectFileException Load(JsonNode root) => Load(root.ToJsonString());

    private static ProjectFileException Load(string json) =>
        Assert.Throws<ProjectFileException>(() => ProjectSerializer.Deserialize(json, Folder, AllExist));

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("42")]
    public void Garbage_is_rejected(string json) => Load(json);

    [Fact]
    public void Truncated_file_is_rejected()
    {
        var json = SampleJson().ToJsonString();
        Load(json[..(json.Length / 2)]);
    }

    [Fact]
    public void Other_json_documents_are_not_projects()
    {
        var ex = Load("{\"name\":\"x\"}");
        Assert.Contains("not an AI Video Editor project", ex.Message);

        var root = SampleJson();
        root["format"] = "SomethingElse";
        Assert.Contains("not an AI Video Editor project", Load(root).Message);
    }

    [Fact]
    public void Newer_format_version_is_rejected_with_a_clear_message()
    {
        var root = SampleJson();
        root["formatVersion"] = ProjectSerializer.CurrentFormatVersion + 1;

        Assert.Contains("newer version", Load(root).Message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Invalid_format_version_is_rejected(int version)
    {
        var root = SampleJson();
        root["formatVersion"] = version;
        Load(root);
    }

    [Fact]
    public void Missing_format_version_is_rejected()
    {
        var root = SampleJson();
        root.Remove("formatVersion");
        Load(root);
    }

    public static TheoryData<string, Action<JsonObject>> Corruptions => new()
    {
        { "missing settings", r => r.Remove("settings") },
        { "zero frame rate", r => r["settings"]!["frameRate"]!["numerator"] = 0 },
        { "missing frame rate", r => r["settings"]!.AsObject().Remove("frameRate") },
        { "zero frame width", r => r["settings"]!["frameWidth"] = 0 },
        { "missing media list", r => r.Remove("mediaAssets") },
        { "missing timeline", r => r.Remove("timeline") },
        { "missing project id", r => r.Remove("id") },
        { "missing name", r => r.Remove("name") },
        { "duplicate media id", r => r["mediaAssets"]![1]!["id"] = r["mediaAssets"]![0]!["id"]!.GetValue<string>() },
        { "empty media path", r => r["mediaAssets"]![0]!["filePath"] = "" },
        { "unknown media kind", r => r["mediaAssets"]![0]!["kind"] = "Hologram" },
        { "numeric media kind", r => r["mediaAssets"]![0]!["kind"] = 0 },
        { "tick as string", r => FirstVideoClip(r)["timelineStartTicks"] = "100" },
        { "tick as fraction", r => FirstVideoClip(r)["timelineStartTicks"] = 1.5 },
        { "unknown clip type", r => FirstVideoClip(r)["type"] = "hologram" },
        { "missing clip type", r => FirstVideoClip(r).Remove("type") },
        { "clip refers to unknown media", r => FirstVideoClip(r)["mediaAssetId"] = Guid.NewGuid().ToString() },
        { "clip kind doesn't match media kind", r => FirstVideoClip(r)["mediaAssetId"] = r["mediaAssets"]![1]!["id"]!.GetValue<string>() },
        { "zero duration", r => FirstVideoClip(r)["durationTicks"] = 0 },
        { "negative start", r => FirstVideoClip(r)["timelineStartTicks"] = -ProjectTestData.Frame(1).Ticks },
        { "start off the frame grid", r => FirstVideoClip(r)["timelineStartTicks"] = ProjectTestData.Frame(1).Ticks + 1 },
        // v2 (D022): a 1× source range may end up to (not including) one frame after the clip's frames.
        { "source range doesn't match duration", r => FirstVideoClip(r)["sourceOutTicks"] = FirstVideoClip(r)["sourceOutTicks"]!.GetValue<long>() + ProjectTestData.Frame(1).Ticks + 1 },
        { "source range too short", r => FirstVideoClip(r)["sourceOutTicks"] = FirstVideoClip(r)["sourceOutTicks"]!.GetValue<long>() - 1 },
        { "negative source in", r => FirstVideoClip(r)["sourceInTicks"] = -1 },
        { "zero speed", r => FirstVideoClip(r)["speedRatio"]!["numerator"] = 0 },
        { "missing speed", r => FirstVideoClip(r).Remove("speedRatio") },
        { "duplicate clip id", r => r["timeline"]!["audioTracks"]![0]!["clips"]![0]!["id"] = FirstVideoClip(r)["id"]!.GetValue<string>() },
        { "duplicate track id", r => r["timeline"]!["audioTracks"]![0]!["id"] = r["timeline"]!["videoTracks"]![0]!["id"]!.GetValue<string>() },
        { "missing clip id", r => FirstVideoClip(r).Remove("id") },
        { "negative playhead", r => r["timeline"]!["playheadTicks"] = -1 },
        { "zero zoom", r => r["timeline"]!["zoomPixelsPerSecond"] = 0 },
        { "missing clip list", r => r["timeline"]!["videoTracks"]![0]!.AsObject().Remove("clips") },
        { "null clip entry", r => r["timeline"]!["videoTracks"]![0]!["clips"]!.AsArray().Add(null) },
        { "overlapping clips", r => r["timeline"]!["videoTracks"]![0]!["clips"]![1]!["timelineStartTicks"] = ProjectTestData.Frame(500).Ticks },
    };

    [Theory]
    [MemberData(nameof(Corruptions))]
    public void Damaged_projects_are_rejected(string description, Action<JsonObject> corrupt)
    {
        var root = SampleJson();
        corrupt(root);

        var ex = Load(root);

        Assert.False(string.IsNullOrWhiteSpace(ex.Message), description);
    }

    [Fact]
    public void Audio_clip_on_a_video_track_is_rejected()
    {
        var root = SampleJson();
        var audioClip = root["timeline"]!["audioTracks"]![0]!["clips"]![0]!.DeepClone();
        root["timeline"]!["videoTracks"]![1]!["clips"]!.AsArray().Add(audioClip);
        root["timeline"]!["audioTracks"]![0]!["clips"]!.AsArray().Clear();

        var message = Load(root).Message;
        Assert.True(message.Contains("wrong kind of track"), message);
    }

    [Fact]
    public void Video_clip_on_an_audio_track_is_rejected()
    {
        var root = SampleJson();
        var videoClip = FirstVideoClip(root).DeepClone();
        root["timeline"]!["videoTracks"]![0]!["clips"]!.AsArray().RemoveAt(0);
        root["timeline"]!["audioTracks"]![0]!["clips"]!.AsArray().Clear();
        root["timeline"]!["audioTracks"]![0]!["clips"]!.AsArray().Add(videoClip);

        var message = Load(root).Message;
        Assert.True(message.Contains("wrong kind of track"), message);
    }

    [Fact]
    public void Touching_clips_are_valid()
    {
        var root = SampleJson();
        // Image clip starts exactly where the video clip ends (frame 1001) in the sample.
        var loaded = ProjectSerializer.Deserialize(root.ToJsonString(), Folder, AllExist);
        var clips = loaded.Timeline.VideoTracks[0].Clips;
        Assert.Equal(clips[0].TimelineEnd, clips[1].TimelineStart);
    }

    [Fact]
    public void Clips_extending_past_the_source_media_still_load()
    {
        // The media may have been replaced by a shorter file: that's a runtime condition, not damage.
        var root = SampleJson();
        root["mediaAssets"]![0]!["metadata"]!["durationTicks"] = 1;

        var loaded = ProjectSerializer.Deserialize(root.ToJsonString(), Folder, AllExist);

        Assert.Equal(1, loaded.MediaAssets[0].Metadata!.Duration.Ticks);
    }

    [Theory]
    [InlineData("negative duration")]
    [InlineData("zero frame rate")]
    public void Inconsistent_metadata_is_dropped_and_media_is_analysed_again(string kind)
    {
        var root = SampleJson();
        var metadata = root["mediaAssets"]![0]!["metadata"]!;
        if (kind == "negative duration") metadata["durationTicks"] = -5;
        else metadata["frameRate"]!["denominator"] = 0;

        var loaded = ProjectSerializer.Deserialize(root.ToJsonString(), Folder, AllExist);

        Assert.Null(loaded.MediaAssets[0].Metadata);
        Assert.Equal(MediaAnalysisStatus.Pending, loaded.MediaAssets[0].AnalysisStatus);
    }

    [Fact]
    public void Missing_optional_lists_default_to_empty()
    {
        var root = SampleJson();
        root["timeline"]!.AsObject().Remove("markers");
        root["timeline"]!["videoTracks"]![0]!.AsObject().Remove("transitions");
        FirstVideoClip(root).Remove("effects");

        var loaded = ProjectSerializer.Deserialize(root.ToJsonString(), Folder, AllExist);

        Assert.Empty(loaded.Timeline.Markers);
        Assert.Empty(loaded.Timeline.VideoTracks[0].Transitions);
        Assert.Empty(loaded.Timeline.VideoTracks[0].Clips[0].Effects);
    }

    [Fact]
    public void Frame_rate_from_file_defines_the_grid_used_for_validation()
    {
        // Clip edges on the NTSC grid are not on a 25 fps grid.
        var root = SampleJson();
        root["settings"]!["frameRate"]!["numerator"] = 25;
        root["settings"]!["frameRate"]!["denominator"] = 1;

        Load(root);
        Assert.False(ProjectTestData.Frame(1).IsOnFrameGrid(FrameRate.Fps25));
    }
}
