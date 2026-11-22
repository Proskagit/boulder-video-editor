using System.Text.Json.Nodes;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Project.Persistence;
using Xunit;

namespace AiVideoEditor.Project.Tests;

/// <summary>
/// D075 amendment 1 (format v28): a text's italic, shadow, line spacing and block width survive a save / open / save byte for
/// byte; a text without them writes none of them; the members in a v27 file are ignored; a shadow missing a member refuses the
/// file.
/// </summary>
public class TextStyle2PersistenceTests
{
    private const string Folder = @"C:\Projects\Demo";
    private static readonly FrameRate Rate = FrameRate.Fps25;

    private static Core.Entities.Project Load(string json) => ProjectSerializer.Deserialize(json, Folder, _ => true);
    private static string Save(Core.Entities.Project project) => ProjectSerializer.Serialize(project, Folder);
    private static TextClip Text(Core.Entities.Project project) => (TextClip)project.Timeline.VideoTracks[0].Clips[0];

    private static Core.Entities.Project Build(Action<TextClip>? style = null)
    {
        var project = new Core.Entities.Project
        {
            Name = "Titles",
            Settings = new ProjectSettings { FrameWidth = 1920, FrameHeight = 1080, FrameRate = Rate, IsFrameRateLocked = true, AudioSampleRate = 48000 }
        };
        var clip = new TextClip { Text = "Трасса 7", TimelineStart = MediaTime.Zero, Duration = MediaTime.FromFrame(100, Rate) };
        style?.Invoke(clip);
        var track = new Track { Type = TrackType.Video, Name = "V1" };
        track.Clips.Add(clip);
        project.Timeline.VideoTracks.Add(track);
        project.Timeline.AudioTracks.Add(new Track { Type = TrackType.Audio, Name = "A1" });
        return project;
    }

    private static void Styled(TextClip t)
    {
        t.Italic = true;
        t.Shadow = new TextShadow(false, "#123456", 0.45, 0.137, -0.05, 0.11);   // off: its values kept
        t.LineSpacing = 1.35;
        t.BoxWidth = 812;
    }

    [Fact]
    public void Every_member_survives_a_save_open_save_exactly()
    {
        var first = Save(Build(Styled));
        Assert.Equal(28, JsonNode.Parse(first)!["formatVersion"]!.GetValue<int>());
        var loaded = Text(Load(first));
        Assert.True(loaded.Italic);
        Assert.Equal(new TextShadow(false, "#123456", 0.45, 0.137, -0.05, 0.11), loaded.Shadow);
        Assert.Equal((1.35, (double?)812), (loaded.LineSpacing, loaded.BoxWidth));
        Assert.Equal(first, Save(Load(first)));
    }

    [Fact]
    public void A_plain_text_writes_none_and_a_v27_file_ignores_them()
    {
        var plain = JsonNode.Parse(Save(Build()))!["timeline"]!["videoTracks"]![0]!["clips"]![0]!.AsObject();
        foreach (var member in new[] { "italic", "shadow", "lineSpacing", "boxWidth" }) Assert.False(plain.ContainsKey(member), member);

        var v27 = Save(Build(Styled)).Replace("\"formatVersion\": 28", "\"formatVersion\": 27");
        var loaded = Text(Load(v27));
        Assert.False(loaded.Italic);
        Assert.Equal(TextShadow.Default, loaded.Shadow);
        Assert.Equal((1.0, (double?)null), (loaded.LineSpacing, loaded.BoxWidth));
    }

    [Fact]
    public void A_shadow_without_a_member_refuses_the_file()
    {
        var root = JsonNode.Parse(Save(Build(t => t.Shadow = TextShadow.Default with { IsEnabled = true })))!;
        root["timeline"]!["videoTracks"]![0]!["clips"]![0]!["shadow"]!.AsObject().Remove("blur");
        Assert.Throws<ProjectFileException>(() => Load(root.ToJsonString()));
    }
}
