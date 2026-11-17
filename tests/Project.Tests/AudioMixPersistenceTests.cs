using System.Collections.Immutable;
using System.Text.Json.Nodes;
using AiVideoEditor.Core.Audio;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Project.Persistence;
using Xunit;

namespace AiVideoEditor.Project.Tests;

/// <summary>
/// D064 §1: <c>project.json</c> v25 stores a track's volume, solo and ducking, the master volume, a clip's volume points, a
/// video clip's sound-only fades and the clean-up — each written only when set; a v24 file opens with none and is saved as v25
/// differing only in the version; damaged values refuse the file; an older build refuses a v25 file as newer.
/// </summary>
public class AudioMixPersistenceTests
{
    private const string Folder = @"C:\Projects\Audio";

    private static Core.Entities.Project Load(string json) => ProjectSerializer.Deserialize(json, Folder, _ => true);
    private static string Save(Core.Entities.Project project) => ProjectSerializer.Serialize(project, Folder);

    private static readonly Guid PointA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid PointB = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static Core.Entities.Project Build(bool withMix)
    {
        var project = new Core.Entities.Project { Name = "Audio" };
        var video = new MediaAsset
        {
            FilePath = Folder + @"\media\climb.mp4", Kind = MediaKind.Video, AnalysisStatus = MediaAnalysisStatus.Completed,
            Metadata = new MediaMetadata { Duration = MediaTime.FromSeconds(10), FrameRate = FrameRate.Fps30, AudioCodec = "aac" }
        };
        var music = new MediaAsset
        {
            FilePath = Folder + @"\media\music.mp3", Kind = MediaKind.Audio, AnalysisStatus = MediaAnalysisStatus.Completed,
            Metadata = new MediaMetadata { Duration = MediaTime.FromSeconds(10), AudioCodec = "mp3" }
        };
        project.MediaAssets.Add(video);
        project.MediaAssets.Add(music);
        var v1 = new Track { Type = TrackType.Video, Name = "V1" };
        var a1 = new Track { Type = TrackType.Audio, Name = "A1" };
        var clip = new VideoClip { MediaAssetId = video.Id, Duration = MediaTime.FromSeconds(2), SourceOut = MediaTime.FromSeconds(2) };
        var sound = new AudioClip { MediaAssetId = music.Id, Duration = MediaTime.FromSeconds(2), SourceOut = MediaTime.FromSeconds(2) };
        if (withMix)
        {
            clip.Volumes = new VolumeEnvelope(ImmutableArray.Create(new VolumePoint(PointA, MediaTime.FromSeconds(0.5), 0.25),
                new VolumePoint(PointB, MediaTime.FromSeconds(1.5), 1.75)));
            clip.SoundFadeIn = MediaTime.FromFrame(6, FrameRate.Fps30);
            clip.SoundFadeOut = MediaTime.FromFrame(9, FrameRate.Fps30);
            clip.Cleanup = new AudioCleanup(35, 80);
            sound.Volumes = new VolumeEnvelope(ImmutableArray.Create(new VolumePoint(PointA, MediaTime.FromSeconds(1), 0)));
            sound.Cleanup = new AudioCleanup(0, 20);
            v1.Volume = 0.75;
            a1.IsSolo = true;
            a1.Volume = 1.5;
            a1.Ducking = new DuckingSettings(v1.Id, -18.5, 120, 900, 65);
            project.Timeline.MasterVolume = 0.9;
        }
        v1.Clips.Add(clip);
        a1.Clips.Add(sound);
        project.Timeline.VideoTracks.Add(v1);
        project.Timeline.AudioTracks.Add(a1);
        return project;
    }

    private static JsonObject VideoClip(JsonNode root) => root["timeline"]!["videoTracks"]![0]!["clips"]![0]!.AsObject();
    private static JsonObject AudioTrack(JsonNode root) => root["timeline"]!["audioTracks"]![0]!.AsObject();

    [Fact]
    public void Every_member_survives_a_save_open_save_exactly()
    {
        var first = Save(Build(withMix: true));
        var root = JsonNode.Parse(first)!;
        Assert.Equal(25, root["formatVersion"]!.GetValue<int>());
        Assert.Equal(2, VideoClip(root)["volumePoints"]!.AsArray().Count);
        Assert.Equal(35, VideoClip(root)["cleanup"]!["noiseReduction"]!.GetValue<int>());
        Assert.Equal(-18.5, AudioTrack(root)["ducking"]!["depthDb"]!.GetValue<double>());
        Assert.Equal(0.9, root["timeline"]!["masterVolume"]!.GetValue<double>());

        var loaded = Load(first);
        var clip = (VideoClip)loaded.Timeline.VideoTracks[0].Clips[0];
        Assert.Equal(new[] { PointA, PointB }, clip.Volumes!.Points.Select(p => p.Id));
        Assert.Equal(1.75, clip.Volumes.Points[1].Gain);
        Assert.Equal(MediaTime.FromFrame(6, FrameRate.Fps30), clip.SoundFadeIn);
        Assert.Equal(new AudioCleanup(35, 80), clip.Cleanup);
        var a1 = loaded.Timeline.AudioTracks[0];
        Assert.True(a1.IsSolo);
        Assert.Equal(1.5, a1.Volume);
        Assert.Equal(loaded.Timeline.VideoTracks[0].Id, a1.Ducking!.SpeechTrackId);
        Assert.Equal(0.75, loaded.Timeline.VideoTracks[0].Volume);
        Assert.Equal(0, ((AudioClip)a1.Clips[0]).Volumes!.Points[0].Gain);
        Assert.Equal(first, Save(loaded));
    }

    [Fact]
    public void Without_a_mix_nothing_new_is_written()
    {
        var json = Save(Build(withMix: false));
        foreach (var name in new[] { "volumePoints", "soundFadeInTicks", "soundFadeOutTicks", "cleanup", "isSolo", "ducking", "masterVolume" })
            Assert.DoesNotContain($"\"{name}\"", json);
        Assert.Null(AudioTrack(JsonNode.Parse(json)!)["volume"]);                         // a track's volume 1 is not written
    }

    [Fact]
    public void A_version_24_file_opens_without_the_mix_and_is_saved_as_version_25_differing_only_in_the_version()
    {
        var current = Save(Build(withMix: false));
        var v24 = current.Replace("\"formatVersion\": 25", "\"formatVersion\": 24");
        var loaded = Load(v24);
        Assert.Equal(1.0, loaded.Timeline.MasterVolume);
        Assert.All(loaded.Timeline.AudioTracks, t => Assert.Equal((1.0, false, (DuckingSettings?)null), (t.Volume, t.IsSolo, t.Ducking)));
        Assert.Equal(current, Save(loaded));
    }

    [Fact]
    public void Members_in_a_version_24_file_are_ignored()
    {
        var v24 = Save(Build(withMix: true)).Replace("\"formatVersion\": 25", "\"formatVersion\": 24");
        var loaded = Load(v24);
        var clip = (VideoClip)loaded.Timeline.VideoTracks[0].Clips[0];
        Assert.Null(clip.Volumes);
        Assert.Null(clip.Cleanup);
        Assert.Equal(MediaTime.Zero, clip.SoundFadeIn);
        Assert.Null(loaded.Timeline.AudioTracks[0].Ducking);
        Assert.False(loaded.Timeline.AudioTracks[0].IsSolo);
    }

    [Theory]
    [InlineData("points")]
    [InlineData("gain")]
    [InlineData("cleanup")]
    [InlineData("ducking")]
    [InlineData("duckingSelf")]
    [InlineData("duckingMissing")]
    [InlineData("trackVolume")]
    [InlineData("master")]
    [InlineData("soundFade")]
    public void Damaged_audio_values_refuse_the_file(string damage)
    {
        var root = JsonNode.Parse(Save(Build(withMix: true)))!;
        var clip = VideoClip(root);
        var track = AudioTrack(root);
        switch (damage)
        {
            case "points": clip["volumePoints"]![1]!["timeTicks"] = 1; break;          // before the first
            case "gain": clip["volumePoints"]![0]!["gain"] = 2.5; break;
            case "cleanup": clip["cleanup"]!["voiceEnhancement"] = 101; break;
            case "ducking": track["ducking"]!["releaseMs"] = 10; break;
            case "duckingSelf": track["ducking"]!["speechTrackId"] = track["id"]!.GetValue<Guid>(); break;
            case "duckingMissing": track["ducking"]!["speechTrackId"] = Guid.NewGuid(); break;
            case "trackVolume": track["volume"] = -0.5; break;
            case "master": root["timeline"]!["masterVolume"] = 3; break;
            case "soundFade": clip["soundFadeInTicks"] = -1; break;
        }
        Assert.ThrowsAny<Exception>(() => Load(root.ToJsonString()));
    }

    [Fact]
    public void A_version_25_file_is_newer_for_a_version_24_build()
    {
        var json = Save(Build(withMix: true));
        Assert.False(ProjectSerializer.PeekNewerVersion(json, ProjectSerializer.FormatId));
        Assert.True(ProjectSerializer.PeekNewerVersion(json.Replace("\"formatVersion\": 25", "\"formatVersion\": 26"), ProjectSerializer.FormatId));
    }
}
