using System.Text.Json.Nodes;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Project.Persistence;
using Xunit;

namespace AiVideoEditor.Project.Tests;

/// <summary>
/// D073 (format v27): «Итоги» (a template kind with its own background), FLASH (a status), a template's language and
/// «Боулдер»'s stated attempts are stored exactly (save / open / save byte for byte); a v26 file holding «Итоги» or FLASH is
/// refused; a v26 file's templates read with Russian words, counted attempts, over the video; attempts out of 1 … 99 or an
/// unknown language refuse the file.
/// </summary>
public class SummaryPersistenceTests
{
    private const string Folder = @"C:\Projects\Demo";
    private static readonly FrameRate Rate = FrameRate.Fps30;
    private static MediaTime F(long n) => MediaTime.FromFrame(n, Rate);

    private static Core.Entities.Project Load(string json) => ProjectSerializer.Deserialize(json, Folder, _ => true);
    private static string Save(Core.Entities.Project project) => ProjectSerializer.Serialize(project, Folder);
    private static ProjectFileException Rejected(JsonNode root) => Assert.Throws<ProjectFileException>(() => Load(root.ToJsonString()));
    private static JsonObject Clip(JsonNode root, int index) => root["timeline"]!["videoTracks"]![0]!["clips"]![index]!.AsObject();

    private static Core.Entities.Project Build()
    {
        var project = new Core.Entities.Project
        {
            Name = "Session",
            Settings = new ProjectSettings { FrameWidth = 1920, FrameHeight = 1080, FrameRate = Rate, IsFrameRateLocked = true, AudioSampleRate = 48000 }
        };
        var v1 = new Track { Type = TrackType.Video, Name = "V1" };
        v1.Clips.Add(new TemplateClip
        {
            Kind = TemplateKind.Boulder, TimelineStart = F(0), Duration = F(240), Route = "7", Grade = "6B", Status = BoulderStatus.Flash,
            StatusAt = F(120), Attempts = 4, Language = TemplateLanguage.English
        });
        v1.Clips.Add(new TemplateClip
        {
            Kind = TemplateKind.Summary, TimelineStart = F(240), Duration = F(300), FadeIn = F(12), FadeOut = F(12),
            Title = "Итоги дня", Subtitle = "22.11.2026", OwnBackground = true
        });
        project.Timeline.VideoTracks.Add(v1);
        project.Timeline.AudioTracks.Add(new Track { Type = TrackType.Audio, Name = "A1" });
        return project;
    }

    [Fact]
    public void Summary_flash_language_and_attempts_round_trip_byte_for_byte()
    {
        var first = Save(Build());
        var root = JsonNode.Parse(first)!;
        Assert.Equal(29, root["formatVersion"]!.GetValue<int>());
        Assert.Equal("Summary", Clip(root, 1)["template"]!.GetValue<string>());
        Assert.True(Clip(root, 1)["ownBackground"]!.GetValue<bool>());
        Assert.Equal("Flash", Clip(root, 0)["status"]!.GetValue<string>());
        Assert.Equal(4, Clip(root, 0)["attempts"]!.GetValue<int>());
        Assert.Equal("English", Clip(root, 0)["language"]!.GetValue<string>());

        var loaded = Load(first);
        var clips = loaded.Timeline.VideoTracks[0].Clips.OfType<TemplateClip>().ToList();
        Assert.Equal((BoulderStatus.Flash, 4, TemplateLanguage.English), (clips[0].Status, clips[0].Attempts, clips[0].Language));
        Assert.Equal((TemplateKind.Summary, true, "Итоги дня", "22.11.2026"), (clips[1].Kind, clips[1].OwnBackground, clips[1].Title, clips[1].Subtitle));
        Assert.Equal(first, Save(loaded));
    }

    [Fact]
    public void Counted_attempts_and_the_video_background_are_not_written()
    {
        var project = Build();
        var templates = project.Timeline.VideoTracks[0].Clips.OfType<TemplateClip>().ToList();
        templates[0].Attempts = null;
        templates[1].OwnBackground = false;
        var root = JsonNode.Parse(Save(project))!;
        Assert.Null(Clip(root, 0)["attempts"]);
        Assert.Null(Clip(root, 1)["ownBackground"]);
    }

    [Fact]
    public void A_v26_file_with_a_summary_or_a_flash_is_refused()
    {
        JsonNode V26(Action<JsonObject> change)
        {
            var root = JsonNode.Parse(Save(Build()))!;
            root["formatVersion"] = 26;
            foreach (var i in new[] { 0, 1 }) Clip(root, i).Remove("language");
            Clip(root, 0).Remove("attempts");
            Clip(root, 1).Remove("ownBackground");
            change(root.AsObject());
            return root;
        }
        Rejected(V26(r => Clip(r, 0)["status"] = "Top"));                                         // the summary stays
        Rejected(V26(r => Clip(r, 1)["template"] = "Highlight"));                                 // the flash stays
        var plain = Load(V26(r => { Clip(r, 0)["status"] = "Top"; Clip(r, 1)["template"] = "Highlight"; }).ToJsonString());
        Assert.All(plain.Timeline.VideoTracks[0].Clips.OfType<TemplateClip>(), t => Assert.Equal(TemplateLanguage.Russian, t.Language));
    }

    [Fact]
    public void A_v26_files_new_fields_are_ignored_and_saved_as_the_current_version()
    {
        var root = JsonNode.Parse(Save(Build()))!;
        root["formatVersion"] = 26;
        Clip(root, 0)["status"] = "Top";
        Clip(root, 1)["template"] = "Highlight";
        var loaded = Load(root.ToJsonString());
        var area = loaded.Timeline.VideoTracks[0].Clips.OfType<TemplateClip>().First();
        Assert.Null(area.Attempts);
        Assert.Equal(TemplateLanguage.Russian, area.Language);
        Assert.False(loaded.Timeline.VideoTracks[0].Clips.OfType<TemplateClip>().Last().OwnBackground);
        Assert.Equal(29, JsonNode.Parse(Save(loaded))!["formatVersion"]!.GetValue<int>());
    }

    [Fact]
    public void Attempts_out_of_range_or_an_unknown_language_are_refused()
    {
        JsonNode With(Action<JsonObject> change)
        {
            var root = JsonNode.Parse(Save(Build()))!;
            change(Clip(root, 0));
            return root;
        }
        Assert.Contains("attempts out of range", Rejected(With(c => c["attempts"] = 0)).Message);
        Assert.Contains("attempts out of range", Rejected(With(c => c["attempts"] = 100)).Message);
        Rejected(With(c => c["language"] = "German"));
    }
}
