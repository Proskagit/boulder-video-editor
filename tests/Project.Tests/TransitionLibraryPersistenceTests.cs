using System.Text.Json.Nodes;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Project.Persistence;
using Xunit;

namespace AiVideoEditor.Project.Tests;

/// <summary>
/// Phase 28 Step 28.2 (D044 §9): <c>project.json</c> v8 stores every library type on its cut and a push's direction
/// (<c>fromRight</c>, <c>fromLeft</c>, <c>fromTop</c>, <c>fromBottom</c>, written for a push only) — a save / open / save
/// is exact; a v7 project with dissolves opens unchanged and is saved again differing only in <c>formatVersion</c>; a
/// library type or a direction in a v7 file, a push without its direction, a direction on another type, an unknown
/// direction and fill peaks closer than 1 s (D044 §4) refuse the file.
/// </summary>
public class TransitionLibraryPersistenceTests
{
    private const string Folder = @"C:\Projects\Demo";
    private static readonly FrameRate Rate = FrameRate.Fps30;
    private static MediaTime F(long n) => MediaTime.FromFrame(n, Rate);

    private static Core.Entities.Project Load(string json) => ProjectSerializer.Deserialize(json, Folder, _ => true);
    private static string Save(Core.Entities.Project project) => ProjectSerializer.Serialize(project, Folder);
    private static JsonObject Json(Core.Entities.Project project) => JsonNode.Parse(Save(project))!.AsObject();
    private static JsonArray Transitions(JsonObject root) => root["timeline"]!["videoTracks"]![0]!["transitions"]!.AsArray();
    private static ProjectFileException Rejected(JsonNode root) => Assert.Throws<ProjectFileException>(() => Load(root.ToJsonString()));

    /// <summary>Six text clips of <paramref name="length"/> frames on V1 with a transition of each given type on the cuts.</summary>
    private static Core.Entities.Project Build(long length, params (string Type, TransitionDirection? Direction)[] transitions)
    {
        var project = new Core.Entities.Project
        {
            Name = "Session",
            Settings = new ProjectSettings { FrameWidth = 1920, FrameHeight = 1080, FrameRate = Rate, IsFrameRateLocked = true, AudioSampleRate = 48000 }
        };
        var track = new Track { Type = TrackType.Video, Name = "V1" };
        for (var i = 0; i <= transitions.Length; i++)
            track.Clips.Add(new TextClip { Text = $"Clip {i}", TimelineStart = F(i * length), Duration = F((i + 1) * length) - F(i * length) });
        for (var i = 0; i < transitions.Length; i++)
            track.Transitions.Add(new Transition
            {
                TransitionTypeId = transitions[i].Type, Duration = F(6), LeftClipId = track.Clips[i].Id, RightClipId = track.Clips[i + 1].Id,
                Direction = transitions[i].Direction
            });
        project.Timeline.VideoTracks.Add(track);
        project.Timeline.AudioTracks.Add(new Track { Type = TrackType.Audio, Name = "A1" });
        return project;
    }

    private static Core.Entities.Project Library() => Build(60,
        (TransitionRules.CrossDissolve, null), (TransitionRules.DipToBlack, null), (TransitionRules.Flash, null),
        (TransitionRules.Zoom, null), (TransitionRules.Push, TransitionDirection.FromBottom));

    [Fact]
    public void Every_type_and_the_push_direction_survive_a_save_open_save_exactly()
    {
        var first = Save(Library());
        Assert.Equal(9, JsonNode.Parse(first)!["formatVersion"]!.GetValue<int>());

        var loaded = Load(first).Timeline.VideoTracks[0].Transitions;
        Assert.Equal(new[] { "crossDissolve", "dipToBlack", "flash", "zoom", "push" }, loaded.Select(t => t.TransitionTypeId));
        Assert.Equal(new TransitionDirection?[] { null, null, null, null, TransitionDirection.FromBottom }, loaded.Select(t => t.Direction));
        Assert.All(loaded, t => Assert.Equal(F(6), t.Duration));
        Assert.Equal(first, Save(Load(first)));
    }

    [Theory]
    [InlineData(TransitionDirection.FromRight, "fromRight")]
    [InlineData(TransitionDirection.FromLeft, "fromLeft")]
    [InlineData(TransitionDirection.FromTop, "fromTop")]
    [InlineData(TransitionDirection.FromBottom, "fromBottom")]
    public void The_direction_is_written_for_a_push_only(TransitionDirection direction, string name)
    {
        var root = Json(Build(60, (TransitionRules.Zoom, null), (TransitionRules.Push, direction)));
        var transitions = Transitions(root);
        Assert.False(transitions[0]!.AsObject().ContainsKey("direction"));
        Assert.Equal(name, transitions[1]!["direction"]!.GetValue<string>());
        Assert.Equal(direction, Load(root.ToJsonString()).Timeline.VideoTracks[0].Transitions[1].Direction);
    }

    [Fact]
    public void A_version_7_project_with_dissolves_opens_unchanged_and_is_saved_again_differing_only_in_the_version()
    {
        var v7 = Json(Build(60, (TransitionRules.CrossDissolve, null), (TransitionRules.CrossDissolve, null)));
        v7["formatVersion"] = 7;
        var original = v7.ToJsonString();

        var project = Load(original);
        Assert.All(project.Timeline.VideoTracks[0].Transitions, t => Assert.Null(t.Direction));

        var resaved = JsonNode.Parse(Save(project))!.AsObject();
        Assert.Equal(9, resaved["formatVersion"]!.GetValue<int>());
        resaved["formatVersion"] = 7;
        Assert.Equal(JsonNode.Parse(original)!.ToJsonString(), resaved.ToJsonString());
    }

    [Theory]
    [InlineData("dipToBlack")]
    [InlineData("flash")]
    [InlineData("zoom")]
    [InlineData("push")]
    public void A_library_type_in_a_version_7_file_refuses_it(string type)
    {
        var root = Json(Build(60, (TransitionRules.CrossDissolve, null)));
        root["formatVersion"] = 7;
        Transitions(root)[0]!["transitionTypeId"] = type;
        Assert.Contains("unknown transition type", Rejected(root).Message);
    }

    [Fact]
    public void A_direction_in_a_version_7_file_is_ignored()
    {
        var root = Json(Build(60, (TransitionRules.CrossDissolve, null)));
        root["formatVersion"] = 7;
        Transitions(root)[0]!["direction"] = "fromLeft";
        Assert.Null(Load(root.ToJsonString()).Timeline.VideoTracks[0].Transitions[0].Direction);
    }

    [Fact]
    public void A_push_without_its_direction_or_another_type_with_one_refuses_the_file()
    {
        var push = Json(Build(60, (TransitionRules.Push, TransitionDirection.FromRight)));
        Transitions(push)[0]!.AsObject().Remove("direction");
        Assert.Contains("invalid", Rejected(push).Message);

        var zoom = Json(Build(60, (TransitionRules.Zoom, null)));
        Transitions(zoom)[0]!["direction"] = "fromTop";
        Assert.Contains("invalid", Rejected(zoom).Message);
    }

    [Theory]
    [InlineData("FromRight")]
    [InlineData("right")]
    [InlineData("")]
    public void An_unknown_direction_refuses_the_file(string value)
    {
        var root = Json(Build(60, (TransitionRules.Push, TransitionDirection.FromRight)));
        Transitions(root)[0]!["direction"] = value;
        Assert.Contains("unknown transition direction", Rejected(root).Message);
    }

    [Theory]
    [InlineData(29, false)]
    [InlineData(30, true)]
    public void Fill_peaks_closer_than_one_second_refuse_the_file(long clipFrames, bool opens)
    {
        var root = Json(Build(clipFrames, (TransitionRules.Flash, null), (TransitionRules.DipToBlack, null)));
        if (opens)
            Assert.Equal(2, Load(root.ToJsonString()).Timeline.VideoTracks[0].Transitions.Count);
        else
            Assert.Contains("at least 1 second apart", Rejected(root).Message);
    }
}
