using System.Text.Json.Nodes;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Project.Persistence;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiVideoEditor.Project.Tests;

/// <summary>D069 (format v26): the main track's magnet is saved and opened; a file of an older version opens with it off;
/// the application's new projects have it on.</summary>
public class Phase50PersistenceTests
{
    private const string Folder = @"C:\Projects\Phase50";

    private static Core.Entities.Project Load(string json) => ProjectSerializer.Deserialize(json, Folder, _ => true);
    private static string Save(Core.Entities.Project project) => ProjectSerializer.Serialize(project, Folder);

    private static Core.Entities.Project Build(bool magnet)
    {
        var project = new Core.Entities.Project { Name = "Phase 50" };
        project.Timeline.VideoTracks.Add(new Track { Type = TrackType.Video, Name = "V1" });
        project.Timeline.MagneticMainTrack = magnet;
        return project;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_magnet_survives_a_save_open_save_exactly(bool magnet)
    {
        var first = Save(Build(magnet));
        Assert.Equal(26, JsonNode.Parse(first)!["formatVersion"]!.GetValue<int>());
        Assert.Equal(magnet, JsonNode.Parse(first)!["timeline"]!["magneticMainTrack"]!.GetValue<bool>());

        var loaded = Load(first);

        Assert.Equal(magnet, loaded.Timeline.MagneticMainTrack);
        Assert.Equal(first, Save(loaded));
    }

    [Fact]
    public void A_version_25_file_opens_with_the_magnet_off_even_with_the_member()
    {
        var v25 = Save(Build(magnet: true)).Replace("\"formatVersion\": 26", "\"formatVersion\": 25");

        Assert.False(Load(v25).Timeline.MagneticMainTrack);
    }

    [Fact]
    public void A_version_25_file_without_the_member_opens_with_the_magnet_off()
    {
        var node = JsonNode.Parse(Save(Build(magnet: true)))!;
        node["formatVersion"] = 25;
        node["timeline"]!.AsObject().Remove("magneticMainTrack");

        Assert.False(Load(node.ToJsonString()).Timeline.MagneticMainTrack);
    }

    [Fact]
    public void The_applications_new_projects_have_the_magnet_on_and_a_service_built_for_a_test_does_not()
    {
        var app = new ProjectService(new UndoRedoService(), NullLogger<ProjectService>.Instance, magneticMainTrackInNewProjects: true);
        Assert.True(app.Current.Timeline.MagneticMainTrack);
        Assert.True(app.CreateNew("Next").Timeline.MagneticMainTrack);

        var plain = new ProjectService(new UndoRedoService(), NullLogger<ProjectService>.Instance);
        Assert.False(plain.Current.Timeline.MagneticMainTrack);
    }
}
