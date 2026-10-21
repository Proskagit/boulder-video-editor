using System.Text.Json.Nodes;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Project.Persistence;
using Xunit;

namespace AiVideoEditor.Project.Tests;

/// <summary>
/// Phase 29 (D045 §3): <c>project.json</c> v9 stores a template — <c>"type": "template"</c> with its kind, the counter's
/// start and every field — exactly (save / open / save byte for byte); a v8 project opens unchanged and is saved again
/// differing only in <c>formatVersion</c>; a template in a v8 file, a missing field, an unknown kind or status, a negative
/// counter, an invalid field, a template on an audio track and a transition next to a template refuse the file.
/// </summary>
public class TemplatePersistenceTests
{
    private const string Folder = @"C:\Projects\Demo";
    private static readonly FrameRate Rate = FrameRate.Fps30;
    private static MediaTime F(long n) => MediaTime.FromFrame(n, Rate);

    private static Core.Entities.Project Load(string json) => ProjectSerializer.Deserialize(json, Folder, _ => true);
    private static string Save(Core.Entities.Project project) => ProjectSerializer.Serialize(project, Folder);
    private static ProjectFileException Rejected(JsonNode root) => Assert.Throws<ProjectFileException>(() => Load(root.ToJsonString()));

    private static Core.Entities.Project Build(bool templates = true)
    {
        var project = new Core.Entities.Project
        {
            Name = "Session",
            Settings = new ProjectSettings { FrameWidth = 1080, FrameHeight = 1920, FrameRate = Rate, IsFrameRateLocked = true, AudioSampleRate = 48000 }
        };
        var v1 = new Track { Type = TrackType.Video, Name = "V1" };
        v1.Clips.Add(new TextClip { Text = "Title", TimelineStart = F(0), Duration = F(90) });
        if (templates)
        {
            v1.Clips.Add(new TemplateClip
            {
                Kind = TemplateKind.Rec, TimelineStart = F(90), Duration = F(300), FadeIn = F(6), FadeOut = F(6), CounterStart = F(45),
                Date = "27.09.2026"
            });
            v1.Clips.Add(new TemplateClip
            {
                Kind = TemplateKind.Boulder, TimelineStart = F(390), Duration = F(240), FadeIn = F(11), FadeOut = F(9),
                Route = "7", Grade = "7A", Status = BoulderStatus.Attempt, AccentHex = "#2979FF", StatusAt = F(75)
            });
            v1.Clips.Add(new TemplateClip
            {
                Kind = TemplateKind.Highlight, TimelineStart = F(630), Duration = F(90), FadeIn = F(18), FadeOut = F(9),
                Title = "ЛУЧШАЯ ПОПЫТКА", Subtitle = "Трасса 3 · 6B+"
            });
            v1.Clips.Add(new TemplateClip { Kind = TemplateKind.Vhs, TimelineStart = F(720), Duration = F(300) });
        }
        project.Timeline.VideoTracks.Add(v1);
        project.Timeline.AudioTracks.Add(new Track { Type = TrackType.Audio, Name = "A1" });
        return project;
    }

    private static JsonObject Clip(JsonNode root, int index) => root["timeline"]!["videoTracks"]![0]!["clips"]![index]!.AsObject();

    [Fact]
    public void Every_kind_and_field_survives_a_save_open_save_exactly()
    {
        var first = Save(Build());
        var root = JsonNode.Parse(first)!;
        Assert.Equal(9, root["formatVersion"]!.GetValue<int>());
        var boulder = Clip(root, 2);
        Assert.Equal(("template", "Boulder", "Attempt", "7A"), ((string?)boulder["type"], (string?)boulder["template"], (string?)boulder["status"], (string?)boulder["grade"]));

        var loaded = Load(first);
        var clips = loaded.Timeline.VideoTracks[0].Clips.OfType<TemplateClip>().ToList();
        Assert.Equal(new[] { TemplateKind.Rec, TemplateKind.Boulder, TemplateKind.Highlight, TemplateKind.Vhs }, clips.Select(c => c.Kind));
        Assert.Equal((F(45), "27.09.2026"), (clips[0].CounterStart, clips[0].Date));
        Assert.Equal(("7", "7A", BoulderStatus.Attempt, "#2979FF"), (clips[1].Route, clips[1].Grade, clips[1].Status, clips[1].AccentHex));
        Assert.Equal(F(75).Ticks, (long)boulder["statusAtTicks"]!);                     // D045 §17
        Assert.Equal((F(75), MediaTime.Zero), (clips[1].StatusAt, clips[0].StatusAt));
        Assert.Equal(("ЛУЧШАЯ ПОПЫТКА", "Трасса 3 · 6B+"), (clips[2].Title, clips[2].Subtitle));
        Assert.Equal((F(11), F(9)), (clips[1].FadeIn, clips[1].FadeOut));
        Assert.Equal(first, Save(loaded));
    }

    [Fact]
    public void A_version_8_project_opens_unchanged_and_is_saved_again_differing_only_in_the_version()
    {
        var v9 = Save(Build(templates: false));
        var v8 = v9.Replace("\"formatVersion\": 9", "\"formatVersion\": 8");
        Assert.NotEqual(v9, v8);

        Assert.Equal(v9, Save(Load(v8)));
    }

    [Fact]
    public void A_template_in_a_version_8_file_is_refused()
    {
        var root = JsonNode.Parse(Save(Build()))!;
        root["formatVersion"] = 8;
        Assert.Contains("unknown clip type", Rejected(root).Message);
    }

    [Theory]
    [InlineData("template")]
    [InlineData("date")]
    [InlineData("route")]
    [InlineData("grade")]
    [InlineData("status")]
    [InlineData("accentHex")]
    [InlineData("title")]
    [InlineData("subtitle")]
    [InlineData("statusAtTicks")]
    public void A_missing_member_refuses_the_file(string member)
    {
        var root = JsonNode.Parse(Save(Build()))!;
        Clip(root, 2).Remove(member);
        Assert.IsType<ProjectFileException>(Rejected(root));
    }

    [Fact]
    public void An_unknown_kind_or_status_a_negative_counter_or_an_invalid_field_is_refused()
    {
        JsonNode With(Action<JsonObject> change)
        {
            var root = JsonNode.Parse(Save(Build()))!;
            change(Clip(root, 2));
            return root;
        }
        Rejected(With(c => c["template"] = "Wasted"));
        Rejected(With(c => c["status"] = "Flash"));
        Assert.Contains("counter is negative", Rejected(With(c => c["counterStartTicks"] = -1)).Message);
        Assert.Contains("status moment is negative", Rejected(With(c => c["statusAtTicks"] = -1)).Message);
        Assert.Contains("invalid property", Rejected(With(c => c["grade"] = new string('7', 13))).Message);
        Assert.Contains("invalid property", Rejected(With(c => c["accentHex"] = "blue")).Message);
    }

    [Fact]
    public void A_template_on_an_audio_track_or_next_to_a_transition_is_refused()
    {
        var onAudio = JsonNode.Parse(Save(Build()))!;
        var template = Clip(onAudio, 2).DeepClone();
        onAudio["timeline"]!["videoTracks"]![0]!["clips"]!.AsArray().RemoveAt(2);
        onAudio["timeline"]!["audioTracks"]![0]!["clips"]!.AsArray().Add(template);
        Assert.Contains("wrong kind of track", Rejected(onAudio).Message);

        var project = Build();
        var track = project.Timeline.VideoTracks[0];
        track.Transitions.Add(new Transition
        {
            TransitionTypeId = TransitionRules.CrossDissolve, Duration = F(10), LeftClipId = track.Clips[0].Id, RightClipId = track.Clips[1].Id
        });
        Assert.Contains("next to a template", Rejected(JsonNode.Parse(Save(project))!).Message);
    }
}
