using System.Text.Json;
using System.Text.Json.Nodes;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Project.Persistence;
using Xunit;

namespace AiVideoEditor.Project.Tests;

public class ProjectSerializerRoundTripTests
{
    private const string Folder = @"C:\Projects\Demo";
    private static readonly Func<string, bool> AllExist = _ => true;

    private static Core.Entities.Project Sample() => ProjectTestData.Build(Folder + @"\media");

    private static Core.Entities.Project RoundTrip(Core.Entities.Project project) =>
        ProjectSerializer.Deserialize(ProjectSerializer.Serialize(project, Folder), Folder, AllExist);

    [Fact]
    public void Round_trip_reproduces_the_same_file()
    {
        var original = Sample();
        var json = ProjectSerializer.Serialize(original, Folder);

        var loaded = ProjectSerializer.Deserialize(json, Folder, AllExist);

        Assert.Equal(json, ProjectSerializer.Serialize(loaded, Folder));
    }

    [Fact]
    public void Round_trip_restores_project_settings_and_identity()
    {
        var original = Sample();
        var loaded = RoundTrip(original);

        Assert.Equal(original.Id, loaded.Id);
        Assert.Equal("Round Trip", loaded.Name);
        Assert.Equal(original.CreatedAt, loaded.CreatedAt);
        Assert.Equal(original.ModifiedAt, loaded.ModifiedAt);
        Assert.Equal(Folder, loaded.ProjectFolderPath);
        Assert.Equal(3840, loaded.Settings.FrameWidth);
        Assert.Equal(2160, loaded.Settings.FrameHeight);
        Assert.Equal(FrameRate.Ntsc30, loaded.Settings.FrameRate);
        Assert.True(loaded.Settings.IsFrameRateLocked);
        Assert.Equal(44100, loaded.Settings.AudioSampleRate);
        Assert.Equal(original.LastExportSettings.OutputPath, loaded.LastExportSettings.OutputPath);
        Assert.Equal(29.97, loaded.LastExportSettings.FrameRate);
        Assert.Equal(8_000_000, loaded.LastExportSettings.VideoBitrateBps);
        Assert.Null(loaded.LastExportSettings.AudioBitrateBps);
    }

    [Fact]
    public void Round_trip_restores_media_assets_and_metadata_exactly()
    {
        var original = Sample();
        var loaded = RoundTrip(original);

        Assert.Equal(original.MediaAssets.Select(a => a.Id), loaded.MediaAssets.Select(a => a.Id));
        var o = original.MediaAssets[0];
        var l = loaded.MediaAssets[0];
        Assert.Equal(o.FilePath, l.FilePath);
        Assert.Equal(o.FileSizeBytes, l.FileSizeBytes);
        Assert.Equal(MediaKind.Video, l.Kind);
        Assert.Equal(o.ImportedAt, l.ImportedAt);
        Assert.Equal("thumbnails/clip.png", l.ThumbnailPath);
        Assert.Equal(MediaAnalysisStatus.Completed, l.AnalysisStatus);
        Assert.NotNull(l.Metadata);
        Assert.Equal(1_234_567_891, l.Metadata!.Duration.Ticks);
        Assert.Equal(FrameRate.Ntsc30, l.Metadata.FrameRate);
        Assert.Equal(new FrameRate(2997, 100), l.Metadata.AvgFrameRate);
        Assert.Equal(14_000, l.Metadata.StartTime!.Value.Ticks);
        Assert.Equal("h264", l.Metadata.VideoCodec);
        Assert.Equal("aac", l.Metadata.AudioCodec);
        Assert.Equal(2, l.Metadata.AudioChannels);
        Assert.Equal(48000, l.Metadata.AudioSampleRate);
        Assert.Equal(25_000_000, l.Metadata.BitrateBps);

        var audio = loaded.MediaAssets[1].Metadata!;
        Assert.Null(audio.Width);
        Assert.Null(audio.FrameRate);
        Assert.Null(audio.StartTime);
        Assert.Equal(9_000_000_001, audio.Duration.Ticks);
    }

    [Fact]
    public void Round_trip_restores_timeline_tracks_and_clips_exactly()
    {
        var original = Sample();
        var loaded = RoundTrip(original);
        var seq = loaded.Timeline;

        Assert.Equal(original.Timeline.Id, seq.Id);
        Assert.Equal("Main", seq.Name);
        Assert.Equal(123_456_789, seq.PlayheadPosition.Ticks);
        Assert.Equal(87.25, seq.ZoomPixelsPerSecond);
        Assert.False(seq.SnappingEnabled);
        Assert.Equal(ProjectTestData.Frame(90), Assert.Single(seq.Markers).Position);

        Assert.Equal(new[] { "V1", "V2" }, seq.VideoTracks.Select(t => t.Name));
        Assert.All(seq.VideoTracks, t => Assert.Equal(TrackType.Video, t.Type));
        Assert.Equal(TrackType.Audio, Assert.Single(seq.AudioTracks).Type);
        var v2 = seq.VideoTracks[1];
        Assert.Equal(1, v2.Order);
        Assert.True(v2.IsHidden);
        Assert.True(v2.IsLocked);
        Assert.True(seq.AudioTracks[0].IsMuted);

        var origVideo = (VideoClip)original.Timeline.VideoTracks[0].Clips[0];
        var video = Assert.IsType<VideoClip>(seq.VideoTracks[0].Clips[0]);
        Assert.Equal(origVideo.Id, video.Id);
        Assert.Equal(origVideo.MediaAssetId, video.MediaAssetId);
        Assert.Equal(origVideo.TimelineStart.Ticks, video.TimelineStart.Ticks);
        Assert.Equal(origVideo.Duration.Ticks, video.Duration.Ticks);
        Assert.Equal(origVideo.SourceIn.Ticks, video.SourceIn.Ticks);
        Assert.Equal(origVideo.SourceOut.Ticks, video.SourceOut.Ticks);
        Assert.Equal(1.0, video.Speed);
        Assert.Equal(0.3333333333333333, video.Volume);
        Assert.Equal(new CropRect(0.1, 0.2, 0.3, 0.05), video.Crop);
        Assert.Equal(12.75, video.RotationDegrees);

        var effect = Assert.Single(video.Effects);
        Assert.Equal("blur", effect.EffectTypeId);
        Assert.False(effect.IsEnabled);
        Assert.Equal(2.5, effect.Parameters["radius"]);
        Assert.Equal(3L, Assert.IsType<long>(effect.Parameters["passes"]));
        Assert.IsType<double>(effect.Parameters["radius"]);
        Assert.Equal("gauss", effect.Parameters["mode"]);
        Assert.Equal(true, effect.Parameters["enabled"]);
        Assert.Null(effect.Parameters["extra"]);

        Assert.IsType<ImageClip>(seq.VideoTracks[0].Clips[1]);
        Assert.Equal("fade", Assert.Single(seq.VideoTracks[0].Transitions).TransitionTypeId);

        var text = Assert.IsType<TextClip>(Assert.Single(v2.Clips));
        Assert.Equal("Hello, \"world\" — ünïcode", text.Text);
        Assert.Equal(TextAlignment.Right, text.Alignment);
        Assert.Equal(36.5, text.FontSize);

        var audio = Assert.IsType<AudioClip>(Assert.Single(seq.AudioTracks[0].Clips));
        Assert.True(audio.IsMuted);
        Assert.Equal(7, audio.SourceIn.Ticks);
    }

    [Fact]
    public void Media_times_are_stored_as_integer_ticks()
    {
        var project = Sample();
        var clip = project.Timeline.VideoTracks[0].Clips[0];
        var root = JsonNode.Parse(ProjectSerializer.Serialize(project, Folder))!;

        var clipNode = root["timeline"]!["videoTracks"]![0]!["clips"]![0]!;
        var startNode = clipNode["timelineStartTicks"]!.GetValue<JsonElement>();
        Assert.Equal(JsonValueKind.Number, startNode.ValueKind);
        Assert.Equal(clip.TimelineStart.Ticks.ToString(), startNode.GetRawText());
        Assert.Equal(clip.Duration.Ticks.ToString(), clipNode["durationTicks"]!.ToJsonString());
        Assert.Equal("123456789", root["timeline"]!["playheadTicks"]!.ToJsonString());
        Assert.Equal("1234567891", root["mediaAssets"]![0]!["metadata"]!["durationTicks"]!.ToJsonString());

        // No time is ever written in seconds.
        Assert.DoesNotContain("seconds", root.ToJsonString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Extreme_tick_values_survive_without_precision_loss()
    {
        var project = Sample();
        project.Timeline.PlayheadPosition = new MediaTime(long.MaxValue - 1);
        project.MediaAssets[0].Metadata!.Duration = new MediaTime(9_007_199_254_740_993); // 2^53 + 1: not a double

        var loaded = RoundTrip(project);

        Assert.Equal(long.MaxValue - 1, loaded.Timeline.PlayheadPosition.Ticks);
        Assert.Equal(9_007_199_254_740_993, loaded.MediaAssets[0].Metadata!.Duration.Ticks);
    }

    [Fact]
    public void Frame_rates_are_stored_as_exact_fractions()
    {
        var root = JsonNode.Parse(ProjectSerializer.Serialize(Sample(), Folder))!;

        var rate = root["settings"]!["frameRate"]!;
        Assert.Equal(30000, rate["numerator"]!.GetValue<int>());
        Assert.Equal(1001, rate["denominator"]!.GetValue<int>());
    }

    [Fact]
    public void File_has_format_marker_and_version()
    {
        var root = JsonNode.Parse(ProjectSerializer.Serialize(Sample(), Folder))!;

        Assert.Equal(ProjectSerializer.FormatId, root["format"]!.GetValue<string>());
        Assert.Equal(1, root["formatVersion"]!.GetValue<int>());
    }

    [Fact]
    public void Runtime_and_ui_state_is_not_stored()
    {
        var project = Sample();
        project.IsDirty = true;
        project.MediaAssets[0].IsMissing = true;
        project.MediaAssets[1].AnalysisStatus = MediaAnalysisStatus.Failed;
        project.MediaAssets[1].AnalysisError = "boom";
        project.Timeline.VideoTracks[0].Clips[0].IsSelected = true;

        var json = ProjectSerializer.Serialize(project, Folder);
        var loaded = ProjectSerializer.Deserialize(json, Folder, AllExist);

        foreach (var name in new[] { "isSelected", "isDirty", "isMissing", "analysisStatus", "analysisError", "projectFolderPath", "timelineEnd", "fileName" })
            Assert.DoesNotContain($"\"{name}\"", json);
        Assert.False(loaded.IsDirty);
        Assert.False(loaded.MediaAssets[0].IsMissing);
        Assert.Null(loaded.MediaAssets[1].AnalysisError);
        Assert.False(loaded.Timeline.VideoTracks[0].Clips[0].IsSelected);
    }

    [Theory]
    [InlineData(MediaAnalysisStatus.Pending)]
    [InlineData(MediaAnalysisStatus.Analyzing)]
    [InlineData(MediaAnalysisStatus.Failed)]
    public void Metadata_is_only_kept_for_a_completed_analysis(MediaAnalysisStatus status)
    {
        var project = Sample();
        project.MediaAssets[1].AnalysisStatus = status;

        var loaded = RoundTrip(project);

        Assert.Null(loaded.MediaAssets[1].Metadata);
        Assert.Equal(MediaAnalysisStatus.Pending, loaded.MediaAssets[1].AnalysisStatus);
        Assert.Equal(MediaAnalysisStatus.Completed, loaded.MediaAssets[0].AnalysisStatus);
    }

    [Fact]
    public void Serialize_does_not_modify_the_project()
    {
        var project = Sample();
        project.IsDirty = true;
        project.ProjectFolderPath = null;
        var modified = project.ModifiedAt;

        ProjectSerializer.Serialize(project, Folder);

        Assert.True(project.IsDirty);
        Assert.Null(project.ProjectFolderPath);
        Assert.Equal(modified, project.ModifiedAt);
    }

    [Fact]
    public void Clips_are_ordered_by_start_after_load()
    {
        var project = Sample();
        project.Timeline.VideoTracks[0].Clips.Reverse();

        var loaded = RoundTrip(project);

        var starts = loaded.Timeline.VideoTracks[0].Clips.Select(c => c.TimelineStart.Ticks).ToList();
        Assert.Equal(starts.OrderBy(t => t), starts);
    }

    [Fact]
    public void Unknown_properties_are_ignored()
    {
        var root = JsonNode.Parse(ProjectSerializer.Serialize(Sample(), Folder))!.AsObject();
        root["somethingNew"] = 42;
        root["timeline"]!["videoTracks"]![0]!["clips"]![0]!["futureField"] = "x";

        var loaded = ProjectSerializer.Deserialize(root.ToJsonString(), Folder, AllExist);

        Assert.Equal(2, loaded.Timeline.VideoTracks[0].Clips.Count);
    }

    [Fact]
    public void Empty_new_project_round_trips()
    {
        var project = new Core.Entities.Project { Name = "Empty" };
        project.Timeline.VideoTracks.Add(new Track { Type = TrackType.Video, Name = "V1" });
        project.Timeline.AudioTracks.Add(new Track { Type = TrackType.Audio, Name = "A1" });

        var loaded = RoundTrip(project);

        Assert.Empty(loaded.MediaAssets);
        Assert.Single(loaded.Timeline.VideoTracks);
        Assert.Single(loaded.Timeline.AudioTracks);
        Assert.Equal(FrameRate.Default, loaded.Settings.FrameRate);
        Assert.False(loaded.Settings.IsFrameRateLocked);
    }
}
