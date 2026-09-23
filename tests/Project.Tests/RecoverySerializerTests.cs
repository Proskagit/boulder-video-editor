using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Project.Persistence;
using Xunit;

namespace AiVideoEditor.Project.Tests;

public class RecoverySerializerTests
{
    private const string Folder = @"C:\Projects\Demo";
    private static readonly Func<string, bool> AllExist = _ => true;
    private static readonly RecoveryInfo Info = new(Folder, new DateTimeOffset(2026, 9, 23, 10, 0, 0, TimeSpan.Zero), 1234, new DateTimeOffset(2026, 9, 23, 9, 0, 0, TimeSpan.Zero));

    [Fact]
    public void Recovery_round_trip_keeps_the_project_and_its_info()
    {
        var project = ProjectTestData.Build(Folder + @"\media");
        project.Timeline.PlayheadPosition = new MediaTime(9_007_199_254_740_993);

        var (loaded, info) = ProjectSerializer.DeserializeRecovery(ProjectSerializer.SerializeRecovery(project, Info), AllExist);

        Assert.Equal(ProjectSerializer.Serialize(project, Folder), ProjectSerializer.Serialize(loaded, Folder));
        Assert.Equal(Info, info);
        Assert.Equal(Folder, loaded.ProjectFolderPath);
        Assert.Equal(9_007_199_254_740_993, loaded.Timeline.PlayheadPosition.Ticks);
    }

    [Fact]
    public void Never_saved_project_has_no_folder()
    {
        var project = ProjectTestData.Build(@"C:\media");

        var (loaded, info) = ProjectSerializer.DeserializeRecovery(ProjectSerializer.SerializeRecovery(project, Info with { ProjectFolderPath = null }), AllExist);

        Assert.Null(info.ProjectFolderPath);
        Assert.Null(loaded.ProjectFolderPath);
        Assert.Equal(project.MediaAssets[0].FilePath, loaded.MediaAssets[0].FilePath);
    }

    [Fact]
    public void Recovery_keeps_the_unsaved_project_name()
    {
        var project = ProjectTestData.Build(Folder);
        project.Name = "Renamed but not saved";

        var (loaded, _) = ProjectSerializer.DeserializeRecovery(ProjectSerializer.SerializeRecovery(project, Info), AllExist);

        Assert.Equal("Renamed but not saved", loaded.Name);
    }

    [Fact]
    public void Project_file_and_recovery_file_are_not_interchangeable()
    {
        var project = ProjectTestData.Build(Folder);

        var asRecovery = Assert.Throws<ProjectFileException>(() => ProjectSerializer.DeserializeRecovery(ProjectSerializer.Serialize(project, Folder), AllExist));
        var asProject = Assert.Throws<ProjectFileException>(() => ProjectSerializer.Deserialize(ProjectSerializer.SerializeRecovery(project, Info), Folder, AllExist));

        Assert.Contains("not an AI Video Editor recovery file", asRecovery.Message);
        Assert.Contains("not an AI Video Editor project", asProject.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{")]
    [InlineData("{\"format\":\"AiVideoEditor.Recovery\",\"formatVersion\":1}")]
    [InlineData("{\"format\":\"AiVideoEditor.Recovery\",\"formatVersion\":1,\"project\":{\"format\":\"AiVideoEditor.Project\",\"formatVersion\":1}}")]
    public void Damaged_recovery_files_are_rejected(string json)
    {
        Assert.Throws<ProjectFileException>(() => ProjectSerializer.DeserializeRecovery(json, AllExist));
    }

    [Fact]
    public void Newer_recovery_format_is_rejected()
    {
        var json = ProjectSerializer.SerializeRecovery(ProjectTestData.Build(Folder), Info)
            .Replace("\"formatVersion\": 1,", "\"formatVersion\": 99,", StringComparison.Ordinal);

        var ex = Assert.Throws<ProjectFileException>(() => ProjectSerializer.DeserializeRecovery(json, AllExist));

        Assert.Contains("newer version", ex.Message);
    }

    [Fact]
    public void Project_inside_a_recovery_file_gets_the_same_validation_as_open()
    {
        var project = ProjectTestData.Build(Folder);
        project.Timeline.VideoTracks[0].Clips[0].TimelineStart += new MediaTime(1); // off the frame grid

        var ex = Assert.Throws<ProjectFileException>(() => ProjectSerializer.DeserializeRecovery(ProjectSerializer.SerializeRecovery(project, Info), AllExist));

        Assert.Contains("frame grid", ex.Message);
    }
}
