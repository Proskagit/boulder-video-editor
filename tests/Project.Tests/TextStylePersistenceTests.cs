using System.Text.Json.Nodes;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Project.Persistence;
using Xunit;

namespace AiVideoEditor.Project.Tests;

/// <summary>
/// Phase 27 (D043 §5): <c>project.json</c> v7 stores a text's bold, outline, background and motions — every member
/// survives a save / open / save exactly (odd values; an outline or background switched off keeps its values); a text
/// without them writes none of the new members, so a v6 file opens with none and is saved again differing only in
/// <c>formatVersion</c>; members in a v6 file are ignored (an older version knows nothing newer); a v8 file is refused;
/// a missing member or a value outside the limits refuses the file.
/// </summary>
public class TextStylePersistenceTests
{
    private const string Folder = @"C:\Projects\Demo";
    private static readonly FrameRate Rate = FrameRate.Fps25;

    private static Core.Entities.Project Load(string json) => ProjectSerializer.Deserialize(json, Folder, _ => true);
    private static string Save(Core.Entities.Project project) => ProjectSerializer.Serialize(project, Folder);
    private static JsonObject Json(Core.Entities.Project project) => JsonNode.Parse(Save(project))!.AsObject();
    private static JsonObject TextJson(JsonObject root) => root["timeline"]!["videoTracks"]![0]!["clips"]![0]!.AsObject();
    private static ProjectFileException Rejected(JsonNode root) => Assert.Throws<ProjectFileException>(() => Load(root.ToJsonString()));
    private static TextClip Text(Core.Entities.Project project) => (TextClip)project.Timeline.VideoTracks[0].Clips[0];

    private static Core.Entities.Project Build(Action<TextClip>? style = null)
    {
        var project = new Core.Entities.Project
        {
            Name = "Titles",
            Settings = new ProjectSettings { FrameWidth = 1080, FrameHeight = 1920, FrameRate = Rate, IsFrameRateLocked = true, AudioSampleRate = 48000 }
        };
        var clip = new TextClip
        {
            Text = "Маршрут 6B+", FontFamily = "Segoe UI", FontSize = 85.5, ColorHex = "#FFEE00", PositionY = 700.25,
            TimelineStart = MediaTime.Zero, Duration = MediaTime.FromFrame(125, Rate),
            FadeIn = MediaTime.FromFrame(10, Rate), FadeOut = MediaTime.FromFrame(8, Rate)
        };
        style?.Invoke(clip);
        var track = new Track { Type = TrackType.Video, Name = "V1" };
        track.Clips.Add(clip);
        project.Timeline.VideoTracks.Add(track);
        project.Timeline.AudioTracks.Add(new Track { Type = TrackType.Audio, Name = "A1" });
        return project;
    }

    private static void Styled(TextClip t)
    {
        t.Bold = true;
        t.Outline = new TextOutline(false, "#12AB34", 0.137);          // off: its values are kept
        t.Background = new TextBackground(true, "#000000", 0.6125);
        t.MotionIn = new TextMotion(TextMotionKind.Slide, TextMotionSide.Left);
        t.MotionOut = new TextMotion(TextMotionKind.Zoom, TextMotionSide.Top);
    }

    [Fact]
    public void Every_member_survives_a_save_open_save_exactly_as_version_7()
    {
        var first = Save(Build(Styled));
        Assert.Equal(7, JsonNode.Parse(first)!["formatVersion"]!.GetValue<int>());

        var loaded = Text(Load(first));
        Assert.True(loaded.Bold);
        Assert.Equal(new TextOutline(false, "#12AB34", 0.137), loaded.Outline);
        Assert.Equal(new TextBackground(true, "#000000", 0.6125), loaded.Background);
        Assert.Equal(new TextMotion(TextMotionKind.Slide, TextMotionSide.Left), loaded.MotionIn);
        Assert.Equal(new TextMotion(TextMotionKind.Zoom, TextMotionSide.Top), loaded.MotionOut);
        Assert.Equal(MediaTime.FromFrame(10, Rate), loaded.FadeIn);
        Assert.Equal(first, Save(Load(first)));
    }

    [Fact]
    public void A_text_without_style_writes_none_of_the_new_members()
    {
        var text = TextJson(Json(Build()));
        foreach (var member in new[] { "bold", "outline", "background", "motionIn", "motionOut" })
            Assert.False(text.ContainsKey(member), member);
    }

    [Fact]
    public void A_version_6_text_opens_without_style_and_is_saved_again_differing_only_in_the_version()
    {
        var v6 = Json(Build());
        v6["formatVersion"] = 6;
        var original = v6.ToJsonString();

        var project = Load(original);
        var text = Text(project);
        Assert.Equal(TextProperties.Of(new TextClip()) is { } none ? (none.Bold, none.Outline, none.Background, none.MotionIn, none.MotionOut) : default,
            (text.Bold, text.Outline, text.Background, text.MotionIn, text.MotionOut));
        Assert.True(TextProperties.Of(text)!.Value.IsPlain);

        var resaved = JsonNode.Parse(Save(project))!.AsObject();
        Assert.Equal(7, resaved["formatVersion"]!.GetValue<int>());
        resaved["formatVersion"] = 6;
        Assert.Equal(JsonNode.Parse(original)!.ToJsonString(), resaved.ToJsonString());
    }

    [Fact]
    public void Members_in_a_version_6_file_are_ignored()
    {
        var root = Json(Build(Styled));
        root["formatVersion"] = 6;
        var text = Text(Load(root.ToJsonString()));
        Assert.False(text.Bold);
        Assert.Equal(TextOutline.Default, text.Outline);
        Assert.Equal(TextBackground.Default, text.Background);
        Assert.Equal(TextMotion.None, text.MotionIn);
        Assert.Equal(TextMotion.None, text.MotionOut);
    }

    [Fact]
    public void A_file_newer_than_version_7_is_refused()
    {
        var root = Json(Build(Styled));
        root["formatVersion"] = 8;
        Assert.Contains("newer version", Rejected(root).Message);
    }

    [Theory]
    [InlineData("outline", "enabled")]
    [InlineData("outline", "colorHex")]
    [InlineData("outline", "thickness")]
    [InlineData("background", "enabled")]
    [InlineData("background", "colorHex")]
    [InlineData("background", "opacity")]
    [InlineData("motionIn", "kind")]
    [InlineData("motionIn", "side")]
    [InlineData("motionOut", "kind")]
    public void A_missing_member_refuses_the_file(string group, string member)
    {
        var root = Json(Build(t => { Styled(t); t.Outline = t.Outline with { IsEnabled = true }; }));
        Assert.True(TextJson(root)[group]!.AsObject().Remove(member));
        Rejected(root);
    }

    [Theory]
    [InlineData("outline", "thickness", 0.01)]
    [InlineData("outline", "thickness", 0.21)]
    [InlineData("background", "opacity", -0.1)]
    [InlineData("background", "opacity", 1.01)]
    public void A_value_outside_the_limits_refuses_the_file(string group, string member, double value)
    {
        var root = Json(Build(t => { Styled(t); t.Outline = t.Outline with { IsEnabled = true }; }));
        TextJson(root)[group]![member] = value;
        Rejected(root);
    }

    [Theory]
    [InlineData("outline", "colorHex", "red")]
    [InlineData("background", "colorHex", "#12345")]
    [InlineData("motionIn", "kind", "Typewriter")]
    [InlineData("motionOut", "side", "Middle")]
    public void A_wrong_value_refuses_the_file(string group, string member, string value)
    {
        var root = Json(Build(t => { Styled(t); t.Outline = t.Outline with { IsEnabled = true }; }));
        TextJson(root)[group]![member] = value;
        Rejected(root);
    }
}
