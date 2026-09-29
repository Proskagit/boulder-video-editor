using System.Text.Json.Nodes;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Project.Persistence;
using Xunit;

namespace AiVideoEditor.Project.Tests;

/// <summary>
/// Phase 10 Step 10.3 (D025): <c>project.json</c> v3 stores clip fades and anchored transitions exactly; v1 / v2 files
/// are read without fades and transitions; a newer version is refused; damaged fades and transitions refuse the file.
/// </summary>
public class FadeTransitionPersistenceTests
{
    private const string Folder = @"C:\Projects\Demo";
    private static readonly Func<string, bool> AllExist = _ => true;
    private static readonly FrameRate Rate = FrameRate.Fps25;

    private static MediaTime F(long n) => MediaTime.FromFrame(n, Rate);
    private static MediaTime S(double seconds) => MediaTime.FromSeconds(seconds);

    private static Core.Entities.Project Load(string json) => ProjectSerializer.Deserialize(json, Folder, AllExist);
    private static ProjectFileException Rejected(JsonNode root) =>
        Assert.Throws<ProjectFileException>(() => Load(root.ToJsonString()));

    /// <summary>V1: video A [0, 50) | video B [50, 100) | image C [100, 125); a 10-frame dissolve A|B and a 7-frame one B|C
    /// (odd: 3 frames in B, 4 in C). V2: a text clip. A1: an audio clip. Fades on A, C, the text and the audio clip.</summary>
    private static Core.Entities.Project Build()
    {
        var project = new Core.Entities.Project
        {
            Name = "Fades",
            Settings = new ProjectSettings { FrameWidth = 1920, FrameHeight = 1080, FrameRate = Rate, IsFrameRateLocked = true, AudioSampleRate = 48000 }
        };
        var video = new MediaAsset
        {
            FilePath = Folder + @"\media\clip.mp4", Kind = MediaKind.Video, AnalysisStatus = MediaAnalysisStatus.Completed,
            Metadata = new MediaMetadata { Duration = S(100), FrameRate = Rate, AudioCodec = "aac" }
        };
        var image = new MediaAsset { FilePath = Folder + @"\media\still.png", Kind = MediaKind.Image };
        var audio = new MediaAsset
        {
            FilePath = Folder + @"\media\music.wav", Kind = MediaKind.Audio, AnalysisStatus = MediaAnalysisStatus.Completed,
            Metadata = new MediaMetadata { Duration = S(100) }
        };
        project.MediaAssets.AddRange(new[] { video, image, audio });

        var a = new VideoClip { MediaAssetId = video.Id, TimelineStart = F(0), Duration = F(50), SourceIn = S(1), FadeIn = F(5) };
        a.SourceOut = a.SourceIn + a.Duration;
        var b = new VideoClip { MediaAssetId = video.Id, TimelineStart = F(50), Duration = F(50), SourceIn = S(10) };
        b.SourceOut = b.SourceIn + b.Duration;
        var c = new ImageClip { MediaAssetId = image.Id, TimelineStart = F(100), Duration = F(25), FadeOut = new MediaTime(12_345) };
        c.SourceOut = c.Duration;
        var text = new TextClip { Text = "Title", TimelineStart = F(10), Duration = F(40), FadeIn = F(3), FadeOut = F(60) };
        var music = new AudioClip { MediaAssetId = audio.Id, TimelineStart = F(0), Duration = F(125), FadeIn = F(25), FadeOut = F(50) };
        music.SourceOut = music.SourceIn + music.Duration;

        var v1 = new Track { Type = TrackType.Video, Name = "V1" };
        v1.Clips.AddRange(new Clip[] { a, b, c });
        v1.Transitions.Add(new Transition { TransitionTypeId = TransitionRules.CrossDissolve, Duration = F(10), LeftClipId = a.Id, RightClipId = b.Id });
        v1.Transitions.Add(new Transition { TransitionTypeId = TransitionRules.CrossDissolve, Duration = F(7), LeftClipId = b.Id, RightClipId = c.Id });
        var v2 = new Track { Type = TrackType.Video, Name = "V2", Order = 1 };
        v2.Clips.Add(text);
        var a1 = new Track { Type = TrackType.Audio, Name = "A1" };
        a1.Clips.Add(music);
        project.Timeline.VideoTracks.AddRange(new[] { v1, v2 });
        project.Timeline.AudioTracks.Add(a1);
        return project;
    }

    private static JsonObject Json(Core.Entities.Project project) =>
        JsonNode.Parse(ProjectSerializer.Serialize(project, Folder))!.AsObject();

    private static JsonArray Clips(JsonObject root, int track = 0) => root["timeline"]!["videoTracks"]![track]!["clips"]!.AsArray();
    private static JsonArray Transitions(JsonObject root, int track = 0) => root["timeline"]!["videoTracks"]![track]!["transitions"]!.AsArray();

    // --- v3 round trip ----------------------------------------------------------------------------------------------

    [Fact]
    public void Fades_and_transitions_round_trip_exactly()
    {
        var original = Build();
        var json = ProjectSerializer.Serialize(original, Folder);
        var loaded = Load(json);

        var allOriginal = original.Timeline.VideoTracks.Concat(original.Timeline.AudioTracks).SelectMany(t => t.Clips).ToList();
        var allLoaded = loaded.Timeline.VideoTracks.Concat(loaded.Timeline.AudioTracks).SelectMany(t => t.Clips).ToList();
        Assert.Equal(allOriginal.Select(c => (c.Id, c.FadeIn.Ticks, c.FadeOut.Ticks)), allLoaded.Select(c => (c.Id, c.FadeIn.Ticks, c.FadeOut.Ticks)));
        Assert.Equal(12_345, allLoaded.Single(c => c is ImageClip).FadeOut.Ticks);   // not a whole number of frames: kept

        var expected = original.Timeline.VideoTracks[0].Transitions;
        var actual = loaded.Timeline.VideoTracks[0].Transitions;
        Assert.Equal(expected.Select(t => (t.Id, t.TransitionTypeId, t.Duration.Ticks, t.LeftClipId, t.RightClipId)),
            actual.Select(t => (t.Id, t.TransitionTypeId, t.Duration.Ticks, t.LeftClipId, t.RightClipId)));

        Assert.Equal(json, ProjectSerializer.Serialize(loaded, Folder));   // byte-identical
    }

    [Fact]
    public void The_file_is_version_3_with_fade_ticks_and_transition_anchors()
    {
        var project = Build();
        var root = Json(project);

        Assert.Equal(3, root["formatVersion"]!.GetValue<int>());
        Assert.Equal(F(5).Ticks, Clips(root)[0]!["fadeInTicks"]!.GetValue<long>());
        Assert.Equal(0, Clips(root)[0]!["fadeOutTicks"]!.GetValue<long>());
        var transition = Transitions(root)[0]!;
        Assert.Equal("crossDissolve", transition["transitionTypeId"]!.GetValue<string>());
        Assert.Equal(project.Timeline.VideoTracks[0].Clips[0].Id, transition["leftClipId"]!.GetValue<Guid>());
        Assert.Equal(project.Timeline.VideoTracks[0].Clips[1].Id, transition["rightClipId"]!.GetValue<Guid>());
    }

    [Fact]
    public void A_project_without_fades_and_transitions_loads_as_before()
    {
        var project = Build();
        foreach (var track in project.Timeline.VideoTracks.Concat(project.Timeline.AudioTracks))
        {
            track.Transitions.Clear();
            foreach (var clip in track.Clips) (clip.FadeIn, clip.FadeOut) = (MediaTime.Zero, MediaTime.Zero);
        }

        var loaded = Load(ProjectSerializer.Serialize(project, Folder));

        Assert.All(loaded.Timeline.VideoTracks, t => Assert.Empty(t.Transitions));
        Assert.All(loaded.Timeline.VideoTracks.Concat(loaded.Timeline.AudioTracks).SelectMany(t => t.Clips),
            c => Assert.Equal((MediaTime.Zero, MediaTime.Zero), (c.FadeIn, c.FadeOut)));
    }

    // --- older and newer versions -----------------------------------------------------------------------------------

    [Fact]
    public void A_version_2_file_is_read_without_fades_and_transitions_and_saved_as_version_3()
    {
        // A v2 build never wrote these fields; a v2 file that has them (edited by hand) is still read without them.
        var root = Json(Build());
        root["formatVersion"] = 2;

        var loaded = Load(root.ToJsonString());

        Assert.All(loaded.Timeline.VideoTracks, t => Assert.Empty(t.Transitions));
        Assert.All(loaded.Timeline.VideoTracks.Concat(loaded.Timeline.AudioTracks).SelectMany(t => t.Clips),
            c => Assert.Equal((MediaTime.Zero, MediaTime.Zero), (c.FadeIn, c.FadeOut)));
        Assert.Equal(5, loaded.Timeline.VideoTracks.Concat(loaded.Timeline.AudioTracks).SelectMany(t => t.Clips).Count());

        var resaved = Json(loaded);
        Assert.Equal(3, resaved["formatVersion"]!.GetValue<int>());
        Assert.Empty(Transitions(resaved));
        Assert.Equal(0, Clips(resaved)[0]!["fadeInTicks"]!.GetValue<long>());
    }

    [Fact]
    public void A_version_2_transition_without_an_anchor_is_dropped_but_its_old_checks_still_apply()
    {
        var root = Json(Build());
        root["formatVersion"] = 2;
        var transition = Transitions(root)[0]!.AsObject();
        transition.Remove("leftClipId");
        transition.Remove("rightClipId");
        transition["transitionTypeId"] = "fade";   // what an unanchored v2 transition looked like
        Assert.Empty(Load(root.ToJsonString()).Timeline.VideoTracks[0].Transitions);

        transition["durationTicks"] = -1;          // rejected in v2 before Phase 10, still rejected
        Assert.Contains("damaged", Rejected(root).Message);
    }

    [Fact]
    public void A_version_4_file_is_refused_as_newer()
    {
        var root = Json(Build());
        root["formatVersion"] = 4;

        Assert.Contains("newer version", Rejected(root).Message);
    }

    // --- damaged fades and transitions ------------------------------------------------------------------------------

    public static TheoryData<string, Action<JsonObject>> Damage => new()
    {
        { "negative fade in", r => Clips(r)[0]!["fadeInTicks"] = -1 },
        { "negative fade out", r => Clips(r)[1]!["fadeOutTicks"] = -1 },
        { "unknown transition type", r => Transitions(r)[0]!["transitionTypeId"] = "wipe" },
        { "missing transition type", r => Transitions(r)[0]!.AsObject().Remove("transitionTypeId") },
        { "missing transition id", r => Transitions(r)[0]!["id"] = Guid.Empty.ToString() },
        { "negative transition duration", r => Transitions(r)[0]!["durationTicks"] = -1 },
        { "one-frame transition", r => Transitions(r)[0]!["durationTicks"] = F(1).Ticks },
        { "zero transition", r => Transitions(r)[0]!["durationTicks"] = 0 },
        { "missing anchor", r => Transitions(r)[0]!.AsObject().Remove("leftClipId") },
        { "anchor not in the project", r => Transitions(r)[0]!["rightClipId"] = Guid.NewGuid().ToString() },
        { "anchor on another track", r => Transitions(r)[0]!["rightClipId"] = Clips(r, 1)[0]!["id"]!.GetValue<string>() },
        { "same clip on both sides", r => Transitions(r)[0]!["rightClipId"] = Transitions(r)[0]!["leftClipId"]!.GetValue<string>() },
        { "clips in the wrong order", r =>
            {
                var t = Transitions(r)[0]!;
                (t["leftClipId"], t["rightClipId"]) = (t["rightClipId"]!.GetValue<string>(), t["leftClipId"]!.GetValue<string>());
            } },
        { "clips that don't touch", r => Transitions(r)[0]!["rightClipId"] = Clips(r)[2]!["id"]!.GetValue<string>() },
        { "two transitions on one cut", r =>
            {
                var copy = Transitions(r)[0]!.DeepClone();
                copy["id"] = Guid.NewGuid().ToString();
                Transitions(r).Add(copy);
            } },
        { "duplicate transition id", r => Transitions(r)[1]!["id"] = Transitions(r)[0]!["id"]!.GetValue<string>() },
        { "zone longer than the left clip", r => Transitions(r)[0]!["durationTicks"] = F(102).Ticks },   // 51 frames in A (50)
        { "zone longer than the right clip", r => Transitions(r)[1]!["durationTicks"] = F(52).Ticks },   // 26 frames in C (25)
        { "zones of both edges overlap", r =>
            {
                Transitions(r)[0]!["durationTicks"] = F(60).Ticks;   // 30 frames in B
                Transitions(r)[1]!["durationTicks"] = F(45).Ticks;   // 22 frames in B: 52 > 50
            } },
        { "transition on an audio track", r =>
            {
                var audio = r["timeline"]!["audioTracks"]![0]!;
                var t = Transitions(r)[0]!.DeepClone();
                t["id"] = Guid.NewGuid().ToString();
                audio["transitions"]!.AsArray().Add(t);
            } },
    };

    [Theory]
    [MemberData(nameof(Damage))]
    public void Damaged_fades_and_transitions_refuse_the_file(string what, Action<JsonObject> corrupt)
    {
        var root = Json(Build());
        corrupt(root);

        var ex = Rejected(root);
        Assert.True(ex.Message.Contains("damaged", StringComparison.Ordinal), $"{what}: {ex.Message}");
    }

    [Fact]
    public void Zones_that_exactly_fill_a_clip_are_valid()
    {
        var root = Json(Build());
        Transitions(root)[0]!["durationTicks"] = F(56).Ticks;   // 28 frames in B …
        Transitions(root)[1]!["durationTicks"] = F(45).Ticks;   // … + 22 frames in B = 50 = B's length

        var loaded = Load(root.ToJsonString());

        Assert.Equal(2, loaded.Timeline.VideoTracks[0].Transitions.Count);
    }
}
