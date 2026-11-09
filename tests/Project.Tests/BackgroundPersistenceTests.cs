using System.Text.Json.Nodes;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Project.Persistence;
using Xunit;

namespace AiVideoEditor.Project.Tests;

/// <summary>
/// D057 §3.7: <c>project.json</c> v19 stores a video clip's blurred background («background»: «blur», only then); a v18 file
/// opens with black backgrounds and is saved as v19; an unknown value refuses the file; a v19 file is newer for a v18 build.
/// </summary>
public class BackgroundPersistenceTests
{
    private const string Folder = @"C:\Projects\Background";

    private static Core.Entities.Project Load(string json) => ProjectSerializer.Deserialize(json, Folder, _ => true);
    private static string Save(Core.Entities.Project project) => ProjectSerializer.Serialize(project, Folder);
    private static JsonObject Clip(JsonNode root, int index) => root["timeline"]!["videoTracks"]![0]!["clips"]![index]!.AsObject();

    private static Core.Entities.Project Build()
    {
        var project = new Core.Entities.Project { Name = "Background" };
        var asset = new MediaAsset
        {
            FilePath = Folder + @"\media\wide.mp4", Kind = MediaKind.Video, AnalysisStatus = MediaAnalysisStatus.Completed,
            Metadata = new MediaMetadata { Duration = MediaTime.FromSeconds(10), FrameRate = FrameRate.Fps30 }
        };
        project.MediaAssets.Add(asset);
        var track = new Track { Type = TrackType.Video, Name = "V1" };
        track.Clips.Add(new VideoClip { MediaAssetId = asset.Id, Duration = MediaTime.FromSeconds(2), SourceOut = MediaTime.FromSeconds(2), Background = ClipBackground.Blur });
        track.Clips.Add(new VideoClip { MediaAssetId = asset.Id, TimelineStart = MediaTime.FromSeconds(2), Duration = MediaTime.FromSeconds(2),
            SourceOut = MediaTime.FromSeconds(2) });
        project.Timeline.VideoTracks.Add(track);
        project.Timeline.AudioTracks.Add(new Track { Type = TrackType.Audio, Name = "A1" });
        return project;
    }

    [Fact]
    public void A_blurred_background_survives_a_save_open_save_exactly_and_black_writes_nothing()
    {
        var first = Save(Build());
        var root = JsonNode.Parse(first)!;
        Assert.Equal(19, root["formatVersion"]!.GetValue<int>());
        Assert.Equal("blur", Clip(root, 0)["background"]!.GetValue<string>());
        Assert.Null(Clip(root, 1)["background"]);

        var loaded = Load(first);
        var clips = loaded.Timeline.VideoTracks[0].Clips.OfType<VideoClip>().ToList();
        Assert.Equal(new[] { ClipBackground.Blur, ClipBackground.Black }, clips.Select(c => c.Background));
        Assert.Equal(first, Save(loaded));
    }

    [Fact]
    public void A_version_18_file_opens_with_black_backgrounds_and_is_saved_as_19()
    {
        var root = JsonNode.Parse(Save(Build()))!;
        root["formatVersion"] = 18;
        var loaded = Load(root.ToJsonString());                                 // a v18 file never has the member: ignored
        Assert.All(loaded.Timeline.VideoTracks[0].Clips.OfType<VideoClip>(), c => Assert.Equal(ClipBackground.Black, c.Background));
        Assert.Equal(19, JsonNode.Parse(Save(loaded))!["formatVersion"]!.GetValue<int>());
    }

    [Fact]
    public void An_unknown_background_refuses_the_file()
    {
        var root = JsonNode.Parse(Save(Build()))!;
        Clip(root, 0)["background"] = "rainbow";
        Assert.Throws<ProjectFileException>(() => Load(root.ToJsonString()));
    }

    [Fact]
    public void A_v19_file_is_newer_for_a_v18_build()
    {
        // FU-PF.1 reads the version first: what a v18 build does with this file is «newer», never «damaged».
        var json = Save(Build());
        Assert.True(ProjectSerializer.PeekNewerVersion(json.Replace("\"formatVersion\": 19", "\"formatVersion\": 20"), ProjectSerializer.FormatId));
    }
}
