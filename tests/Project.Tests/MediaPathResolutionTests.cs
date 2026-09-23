using System.Text.Json.Nodes;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Project.Persistence;
using Xunit;

namespace AiVideoEditor.Project.Tests;

public class MediaPathResolutionTests
{
    private static Core.Entities.Project ProjectWith(string mediaPath)
    {
        var project = new Core.Entities.Project();
        project.MediaAssets.Add(new MediaAsset { FilePath = mediaPath, Kind = MediaKind.Audio });
        return project;
    }

    [Fact]
    public void Media_inside_the_project_folder_gets_a_relative_path()
    {
        var root = JsonNode.Parse(ProjectSerializer.Serialize(ProjectWith(@"C:\P\Demo\media\a.wav"), @"C:\P\Demo"))!;

        var asset = root["mediaAssets"]![0]!;
        Assert.Equal(@"C:\P\Demo\media\a.wav", asset["filePath"]!.GetValue<string>());
        Assert.Equal(@"media\a.wav", asset["relativePath"]!.GetValue<string>());
    }

    [Fact]
    public void Media_next_to_the_project_folder_gets_a_relative_path_with_parent_segments()
    {
        var root = JsonNode.Parse(ProjectSerializer.Serialize(ProjectWith(@"C:\P\Footage\a.wav"), @"C:\P\Demo"))!;

        Assert.Equal(@"..\Footage\a.wav", root["mediaAssets"]![0]!["relativePath"]!.GetValue<string>());
    }

    [Fact]
    public void Media_on_another_volume_has_no_relative_path()
    {
        var root = JsonNode.Parse(ProjectSerializer.Serialize(ProjectWith(@"Z:\Footage\a.wav"), @"C:\P\Demo"))!;

        Assert.Null(root["mediaAssets"]![0]!["relativePath"]);
    }

    [Fact]
    public void Existing_absolute_path_is_used()
    {
        using var temp = new TempFolder();
        var media = temp.CreateFile(@"elsewhere\a.wav");
        var json = ProjectSerializer.Serialize(ProjectWith(media), temp.Combine("project"));

        var loaded = ProjectSerializer.Deserialize(json, temp.Combine("project"));

        Assert.Equal(media, loaded.MediaAssets[0].FilePath);
    }

    [Fact]
    public void Project_moved_together_with_its_media_finds_the_media_by_relative_path()
    {
        using var temp = new TempFolder();
        var media = temp.CreateFile(@"old\project\media\a.wav");
        var json = ProjectSerializer.Serialize(ProjectWith(media), temp.Combine("old", "project"));

        // Move the whole project folder (with media) somewhere else.
        Directory.CreateDirectory(temp.Combine("new"));
        Directory.Move(temp.Combine("old", "project"), temp.Combine("new", "project"));

        var loaded = ProjectSerializer.Deserialize(json, temp.Combine("new", "project"));

        Assert.Equal(temp.Combine("new", "project", "media", "a.wav"), loaded.MediaAssets[0].FilePath);
    }

    [Fact]
    public void Absolute_path_wins_when_both_exist()
    {
        using var temp = new TempFolder();
        var original = temp.CreateFile(@"shared\a.wav");
        var json = ProjectSerializer.Serialize(ProjectWith(original), temp.Combine("p1"));
        // A different folder where the relative path (..\shared\a.wav) also resolves to a file.
        temp.CreateFile(@"x\shared\a.wav");

        var loaded = ProjectSerializer.Deserialize(json, temp.Combine("x", "p2"));

        Assert.Equal(original, loaded.MediaAssets[0].FilePath);
    }

    [Fact]
    public void Missing_media_keeps_its_saved_absolute_path()
    {
        using var temp = new TempFolder();
        var media = temp.Combine("gone", "a.wav");
        var json = ProjectSerializer.Serialize(ProjectWith(media), temp.Combine("project"));

        var loaded = ProjectSerializer.Deserialize(json, temp.Combine("project"));

        Assert.Equal(media, loaded.MediaAssets[0].FilePath);
        Assert.False(loaded.MediaAssets[0].IsMissing); // detection is ProjectService's job
    }
}
