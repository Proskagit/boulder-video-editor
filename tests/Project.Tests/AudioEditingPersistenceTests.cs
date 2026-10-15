using System.Text.Json.Nodes;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Project.Persistence;
using Xunit;

namespace AiVideoEditor.Project.Tests;

/// <summary>
/// Phase 17 Step 17.3 (D032 R1, R2): <c>project.json</c> v4 stores links, a video clip's unlinked sound, audio clips of
/// video files and crossfades on audio tracks; a v3 file opens with the same meaning and is saved as v4 differing only in
/// <c>formatVersion</c>; damaged links and transitions refuse the file.
/// </summary>
public class AudioEditingPersistenceTests
{
    private const string Folder = @"C:\Projects\Demo";
    private static readonly Func<string, bool> AllExist = _ => true;
    private static readonly FrameRate Rate = FrameRate.Fps25;

    private static MediaTime F(long n) => MediaTime.FromFrame(n, Rate);
    private static MediaTime S(double seconds) => MediaTime.FromSeconds(seconds);

    private static Core.Entities.Project Load(string json) => ProjectSerializer.Deserialize(json, Folder, AllExist);
    private static ProjectFileException Rejected(JsonNode root) =>
        Assert.Throws<ProjectFileException>(() => Load(root.ToJsonString()));
    private static JsonObject Json(Core.Entities.Project project) =>
        JsonNode.Parse(ProjectSerializer.Serialize(project, Folder))!.AsObject();

    private static JsonArray VideoClips(JsonObject root, int track = 0) => root["timeline"]!["videoTracks"]![track]!["clips"]!.AsArray();
    private static JsonArray AudioClips(JsonObject root, int track = 0) => root["timeline"]!["audioTracks"]![track]!["clips"]!.AsArray();
    private static JsonArray AudioTransitions(JsonObject root, int track = 0) => root["timeline"]!["audioTracks"]![track]!["transitions"]!.AsArray();

    private static MediaAsset Video(string name = "clip.mp4") => new()
    {
        FilePath = Folder + @"\media\" + name, Kind = MediaKind.Video, AnalysisStatus = MediaAnalysisStatus.Completed,
        Metadata = new MediaMetadata { Duration = S(100), FrameRate = Rate, AudioCodec = "aac" }
    };

    private static T Media<T>(T clip, long start, long frames, double sourceIn = 0) where T : MediaBackedClip
    {
        clip.TimelineStart = F(start);
        clip.Duration = F(frames);
        clip.SourceIn = S(sourceIn);
        clip.SourceOut = clip.SourceIn + clip.Duration;
        return clip;
    }

    /// <summary>A Phase 16 (v3) style project: every v3 property — speed, transform, crop, volume, fades, a dissolve,
    /// a text, an image, an audio file, markers, export settings — and nothing of v4.</summary>
    private static Core.Entities.Project BuildV3Content()
    {
        var project = new Core.Entities.Project
        {
            Name = "Phase16",
            Settings = new ProjectSettings
            {
                FrameWidth = 1280, FrameHeight = 720, FrameRate = Rate, IsFrameRateLocked = true, AudioSampleRate = 48000,
                Export = new ExportEncoding(ExportQuality.High, ExportSpeedPreset.Slow, 256)
            }
        };
        var video = Video();
        var image = new MediaAsset { FilePath = Folder + @"\media\still.png", Kind = MediaKind.Image };
        var wav = new MediaAsset
        {
            FilePath = Folder + @"\media\music.wav", Kind = MediaKind.Audio, AnalysisStatus = MediaAnalysisStatus.Completed,
            Metadata = new MediaMetadata { Duration = S(100) }
        };
        project.MediaAssets.AddRange(new[] { video, image, wav });

        var a = Media(new VideoClip { MediaAssetId = video.Id, Opacity = 0.5, Volume = 1.5, IsMuted = true, Scale = 0.75,
            Crop = new CropRect(0.1, 0, 0, 0.2), FadeIn = F(5) }, 0, 50, 1);
        var b = Media(new VideoClip { MediaAssetId = video.Id }, 50, 50, 10);
        var still = Media(new ImageClip { MediaAssetId = image.Id }, 100, 25);
        var text = new TextClip { Text = "Title", TimelineStart = F(10), Duration = F(40), FadeOut = F(6) };
        var music = Media(new AudioClip { MediaAssetId = wav.Id, Volume = 0.25, FadeOut = F(25) }, 0, 125);

        var v1 = new Track { Type = TrackType.Video, Name = "V1", IsLocked = true };
        v1.Clips.AddRange(new Clip[] { a, b, still });
        v1.Transitions.Add(new Transition { TransitionTypeId = TransitionRules.CrossDissolve, Duration = F(10), LeftClipId = a.Id, RightClipId = b.Id });
        var v2 = new Track { Type = TrackType.Video, Name = "V2", Order = 1, IsHidden = true };
        v2.Clips.Add(text);
        var a1 = new Track { Type = TrackType.Audio, Name = "A1", IsMuted = true };
        a1.Clips.Add(music);
        project.Timeline.VideoTracks.AddRange(new[] { v1, v2 });
        project.Timeline.AudioTracks.Add(a1);
        project.Timeline.Markers.Add(new Marker { Position = F(30), Label = "Top" });
        return project;
    }

    /// <summary>V1: a video clip with its sound unlinked [0, 50) and one linked to the audio clip on A1; A1: the
    /// unlinked sound (an audio clip of the video file) [0, 50) and the linked audio clip [50, 100) — a crossfade of 10
    /// frames on their cut.</summary>
    private static Core.Entities.Project BuildV4()
    {
        var project = new Core.Entities.Project
        {
            Name = "Audio",
            Settings = new ProjectSettings { FrameWidth = 640, FrameHeight = 360, FrameRate = Rate, IsFrameRateLocked = true, AudioSampleRate = 48000 }
        };
        var video = Video();
        project.MediaAssets.Add(video);
        var link = Guid.NewGuid();

        var detached = Media(new VideoClip { MediaAssetId = video.Id, IsAudioDetached = true }, 0, 50, 2);
        var linkedVideo = Media(new VideoClip { MediaAssetId = video.Id, LinkId = link }, 50, 50, 20);
        var sound = Media(new AudioClip { MediaAssetId = video.Id, FadeIn = F(4) }, 0, 50, 2);
        var linkedSound = Media(new AudioClip { MediaAssetId = video.Id, LinkId = link }, 50, 50, 20);

        var v1 = new Track { Type = TrackType.Video, Name = "V1" };
        v1.Clips.AddRange(new Clip[] { detached, linkedVideo });
        var a1 = new Track { Type = TrackType.Audio, Name = "A1" };
        a1.Clips.AddRange(new Clip[] { sound, linkedSound });
        a1.Transitions.Add(new Transition { TransitionTypeId = TransitionRules.Crossfade, Duration = F(10), LeftClipId = sound.Id, RightClipId = linkedSound.Id });
        project.Timeline.VideoTracks.Add(v1);
        project.Timeline.AudioTracks.Add(a1);
        return project;
    }

    // --- v3 → v4 ----------------------------------------------------------------------------------------------------

    [Fact]
    public void A_version_3_file_opens_with_the_same_meaning_and_is_saved_as_version_4_differing_only_in_the_version()
    {
        var v4Text = ProjectSerializer.Serialize(BuildV3Content(), Folder);
        Assert.Contains("\"formatVersion\": 5", v4Text);   // saved as the current version (v5 since Phase 19)
        Assert.DoesNotContain("linkId", v4Text);
        Assert.DoesNotContain("isAudioDetached", v4Text);

        var v3Text = v4Text.Replace("\"formatVersion\": 5", "\"formatVersion\": 3");
        var loaded = Load(v3Text);

        Assert.All(loaded.Timeline.VideoTracks.Concat(loaded.Timeline.AudioTracks).SelectMany(t => t.Clips).OfType<MediaBackedClip>(),
            c => Assert.Null(c.LinkId));
        Assert.All(loaded.Timeline.VideoTracks.SelectMany(t => t.Clips).OfType<VideoClip>(), v => Assert.False(v.IsAudioDetached));
        Assert.Equal(v4Text, ProjectSerializer.Serialize(loaded, Folder));   // only "formatVersion" differs from the v3 text
    }

    [Fact]
    public void A_version_3_file_ignores_v4_properties_it_could_not_hold()
    {
        var root = Json(BuildV4());
        root["timeline"]!["audioTracks"]![0]!["clips"] = new JsonArray();         // v3 couldn't hold these clips
        root["timeline"]!["audioTracks"]![0]!["transitions"] = new JsonArray();   // nor a crossfade
        root["formatVersion"] = 3;

        var loaded = Load(root.ToJsonString());

        var clips = loaded.Timeline.VideoTracks[0].Clips.Cast<VideoClip>().ToList();
        Assert.All(clips, c => Assert.Null(c.LinkId));               // the stray "linkId" of a v3 file is not read
        Assert.All(clips, c => Assert.False(c.IsAudioDetached));     // nor "isAudioDetached"
    }

    [Fact]
    public void A_version_3_file_with_an_audio_clip_of_a_video_file_is_damaged()
    {
        var root = Json(BuildV4());
        root["timeline"]!["audioTracks"]![0]!["transitions"] = new JsonArray();
        root["formatVersion"] = 3;
        Assert.Contains("doesn't match the kind", Rejected(root).Message);
    }

    [Fact]
    public void A_version_3_file_with_a_crossfade_is_damaged()
    {
        var root = Json(BuildV3Content());
        root["formatVersion"] = 3;
        root["timeline"]!["videoTracks"]![0]!["transitions"]![0]!["transitionTypeId"] = TransitionRules.Crossfade;
        Assert.Contains("unknown transition type", Rejected(root).Message);
    }

    // --- v4 round trip ----------------------------------------------------------------------------------------------

    [Fact]
    public void Links_unlinked_sound_audio_clips_of_video_and_crossfades_round_trip_exactly()
    {
        var original = BuildV4();
        var json = ProjectSerializer.Serialize(original, Folder);
        var loaded = Load(json);

        var v1 = loaded.Timeline.VideoTracks[0].Clips.Cast<VideoClip>().ToList();
        var a1 = loaded.Timeline.AudioTracks[0].Clips.Cast<AudioClip>().ToList();
        Assert.True(v1[0].IsAudioDetached);
        Assert.False(v1[1].IsAudioDetached);
        Assert.Null(v1[0].LinkId);
        Assert.Equal(original.Timeline.VideoTracks[0].Clips.Cast<VideoClip>().ElementAt(1).LinkId, v1[1].LinkId);
        Assert.Equal(v1[1].LinkId, a1[1].LinkId);
        Assert.Null(a1[0].LinkId);
        Assert.All(a1, c => Assert.Equal(loaded.MediaAssets[0].Id, c.MediaAssetId));   // the video file's sound
        Assert.Equal(new MediaTime(S(2).Ticks), a1[0].SourceIn);

        var crossfade = Assert.Single(loaded.Timeline.AudioTracks[0].Transitions);
        Assert.Equal((TransitionRules.Crossfade, F(10), a1[0].Id, a1[1].Id),
            (crossfade.TransitionTypeId, crossfade.Duration, crossfade.LeftClipId, crossfade.RightClipId));

        Assert.Equal(json, ProjectSerializer.Serialize(loaded, Folder));   // byte-identical
    }

    [Fact]
    public void The_v4_properties_are_written_only_where_set()
    {
        var root = Json(BuildV4());
        var v1 = VideoClips(root);
        Assert.True(v1[0]!["isAudioDetached"]!.GetValue<bool>());
        Assert.Null(v1[0]!["linkId"]);
        Assert.Null(v1[1]!["isAudioDetached"]);
        Assert.NotNull(v1[1]!["linkId"]);
        Assert.Null(AudioClips(root)[0]!["linkId"]);
        Assert.Equal(TransitionRules.Crossfade, AudioTransitions(root)[0]!["transitionTypeId"]!.GetValue<string>());
    }

    [Fact]
    public void A_version_6_file_is_refused_as_newer()
    {
        var root = Json(BuildV4());
        root["formatVersion"] = 6;
        Assert.Contains("newer version", Rejected(root).Message);
    }

    // --- damaged v4 files -------------------------------------------------------------------------------------------

    public static TheoryData<string, Action<JsonObject>> Damage => new()
    {
        { "a link of one clip", r => AudioClips(r)[1]!.AsObject().Remove("linkId") },
        { "a link of two video clips", r => { VideoClips(r)[0]!["linkId"] = VideoClips(r)[1]!["linkId"]!.GetValue<Guid>(); AudioClips(r)[1]!.AsObject().Remove("linkId"); } },
        { "a link of three clips", r => VideoClips(r)[0]!["linkId"] = VideoClips(r)[1]!["linkId"]!.GetValue<Guid>() },
        { "an empty link id", r => { VideoClips(r)[1]!["linkId"] = Guid.Empty; AudioClips(r)[1]!["linkId"] = Guid.Empty; } },
        { "a dissolve on an audio track", r => AudioTransitions(r)[0]!["transitionTypeId"] = TransitionRules.CrossDissolve },
        { "a crossfade too long for its clips", r => AudioTransitions(r)[0]!["durationTicks"] = F(120).Ticks },
    };

    [Theory]
    [MemberData(nameof(Damage))]
    public void A_damaged_v4_file_is_refused(string what, Action<JsonObject> damage)
    {
        var root = Json(BuildV4());
        damage(root);
        Assert.IsType<ProjectFileException>(Rejected(root));
        Assert.False(string.IsNullOrEmpty(what));
    }

    [Fact]
    public void A_crossfade_on_a_video_track_is_refused()
    {
        var root = Json(BuildV3Content());
        root["timeline"]!["videoTracks"]![0]!["transitions"]![0]!["transitionTypeId"] = TransitionRules.Crossfade;
        Assert.Contains("crossfades", Rejected(root).Message);
    }

    [Fact]
    public void An_audio_clip_of_an_image_is_refused()
    {
        var project = BuildV4();
        var image = new MediaAsset { FilePath = Folder + @"\media\still.png", Kind = MediaKind.Image };
        project.MediaAssets.Add(image);
        var root = Json(project);
        AudioClips(root)[0]!["mediaAssetId"] = image.Id;
        Assert.Contains("doesn't match the kind", Rejected(root).Message);
    }

    [Fact]
    public void A_linked_image_is_refused()
    {
        var project = BuildV3Content();
        var root = Json(project);
        var link = Guid.NewGuid();
        root["timeline"]!["videoTracks"]![0]!["clips"]![2]!["linkId"] = link;   // the image
        root["timeline"]!["audioTracks"]![0]!["clips"]![0]!["linkId"] = link;
        Assert.Contains("link", Rejected(root).Message);
    }
}
