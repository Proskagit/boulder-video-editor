using System.Text.Json.Nodes;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Project.Persistence;
using Xunit;

namespace AiVideoEditor.Project.Tests;

/// <summary>
/// Phase 35.5: <c>project.json</c> v13 stores the heart-rate overlay — the template kind <c>"HeartRate"</c> with its look and
/// corner — exactly (save / open / save byte for byte); the other kinds carry neither field; a v12 file opens unchanged and
/// is saved as v13; an overlay in a v12 file, or one without its look or corner, or with an unknown one, refuses the file.
/// </summary>
public class HeartRateOverlayPersistenceTests
{
    private const string Folder = @"C:\Projects\Demo";
    private static readonly FrameRate Rate = FrameRate.Fps30;
    private static MediaTime F(long n) => MediaTime.FromFrame(n, Rate);

    private static Core.Entities.Project Load(string json) => ProjectSerializer.Deserialize(json, Folder, _ => true);
    private static string Save(Core.Entities.Project project) => ProjectSerializer.Serialize(project, Folder);
    private static ProjectFileException Rejected(JsonNode root) => Assert.Throws<ProjectFileException>(() => Load(root.ToJsonString()));

    private static Core.Entities.Project Build(bool overlay = true)
    {
        var project = new Core.Entities.Project
        {
            Name = "Session",
            Settings = new ProjectSettings { FrameWidth = 1920, FrameHeight = 1080, FrameRate = Rate, IsFrameRateLocked = true, AudioSampleRate = 48000 }
        };
        var v1 = new Track { Type = TrackType.Video, Name = "V1" };
        v1.Clips.Add(new TemplateClip { Kind = TemplateKind.Rec, TimelineStart = F(0), Duration = F(90), Date = "27.09.2026" });
        if (overlay)
            v1.Clips.Add(new TemplateClip
            {
                Kind = TemplateKind.HeartRate, TimelineStart = F(90), Duration = F(300), FadeIn = F(9), FadeOut = F(9),
                AccentHex = "#2979FF", Look = HeartRateLook.Plain, Corner = OverlayCorner.TopLeft
            });
        project.Timeline.VideoTracks.Add(v1);
        project.Timeline.AudioTracks.Add(new Track { Type = TrackType.Audio, Name = "A1" });
        return project;
    }

    private static JsonObject Clip(JsonNode root, int index) => root["timeline"]!["videoTracks"]![0]!["clips"]![index]!.AsObject();

    [Fact]
    public void The_overlay_survives_a_save_open_save_exactly()
    {
        var first = Save(Build());
        var root = JsonNode.Parse(first)!;
        Assert.Equal(13, root["formatVersion"]!.GetValue<int>());
        var overlay = Clip(root, 1);
        Assert.Equal(("HeartRate", "Plain", "TopLeft"), ((string?)overlay["template"], (string?)overlay["look"], (string?)overlay["corner"]));
        Assert.False(Clip(root, 0).ContainsKey("look"));             // the other kinds carry neither field
        Assert.False(Clip(root, 0).ContainsKey("corner"));

        var loaded = Load(first);
        var clip = loaded.Timeline.VideoTracks[0].Clips.OfType<TemplateClip>().Single(c => c.Kind == TemplateKind.HeartRate);
        Assert.Equal((HeartRateLook.Plain, OverlayCorner.TopLeft, "#2979FF"), (clip.Look, clip.Corner, clip.AccentHex));
        Assert.Equal(first, Save(loaded));
    }

    [Fact]
    public void A_v12_file_opens_unchanged_and_is_saved_as_v13()
    {
        var v13 = Save(Build(overlay: false));
        var v12 = v13.Replace("\"formatVersion\": 13", "\"formatVersion\": 12");
        Assert.Equal(v13, Save(Load(v12)));
    }

    [Fact]
    public void An_overlay_in_a_v12_file_refuses_it()
    {
        var root = JsonNode.Parse(Save(Build()))!;
        root["formatVersion"] = 12;
        Rejected(root);
    }

    [Theory]
    [InlineData("look")]
    [InlineData("corner")]
    public void An_overlay_without_its_look_or_corner_refuses_the_file(string field)
    {
        var root = JsonNode.Parse(Save(Build()))!;
        Clip(root, 1).Remove(field);
        Rejected(root);
    }

    [Theory]
    [InlineData("look", "Neon")]
    [InlineData("corner", "Centre")]
    public void An_unknown_look_or_corner_refuses_the_file(string field, string value)
    {
        var root = JsonNode.Parse(Save(Build()))!;
        Clip(root, 1)[field] = value;
        Rejected(root);
    }
}
