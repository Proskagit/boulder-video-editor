using System.Text.Json.Nodes;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Project.Persistence;
using Xunit;

namespace AiVideoEditor.Project.Tests;

/// <summary>
/// D057 §3.6 (FU-PF.1): a file of a later format version is reported as «newer», even when it holds values this build cannot
/// parse; a damaged file is still reported as damaged.
/// </summary>
public class ProjectVersionMessageTests
{
    private const string Folder = @"C:\Projects\Version";

    private static JsonObject Saved() =>
        JsonNode.Parse(ProjectSerializer.Serialize(new Core.Entities.Project { Name = "Version" }, Folder))!.AsObject();

    private static ProjectFileException Rejected(string json) =>
        Assert.Throws<ProjectFileException>(() => ProjectSerializer.Deserialize(json, Folder, _ => true));

    [Fact]
    public void A_newer_file_with_a_value_this_build_does_not_know_is_reported_as_newer()
    {
        var root = Saved();
        root["formatVersion"] = ProjectSerializer.CurrentFormatVersion + 1;
        root["settings"]!["frameRate"] = "an unknown kind of value";      // parsing the whole file would fail here
        Assert.Contains("newer version", Rejected(root.ToJsonString()).Message);
    }

    [Fact]
    public void A_newer_file_of_the_same_shape_is_reported_as_newer_as_before()
    {
        var root = Saved();
        root["formatVersion"] = ProjectSerializer.CurrentFormatVersion + 1;
        Assert.Contains("newer version", Rejected(root.ToJsonString()).Message);
    }

    [Fact]
    public void A_damaged_file_of_the_current_version_is_still_reported_as_damaged()
    {
        var root = Saved();
        root["settings"]!["frameRate"] = "an unknown kind of value";
        Assert.DoesNotContain("newer version", Rejected(root.ToJsonString()).Message);
        Assert.Contains("damaged", Rejected("{ not json").Message);
    }

    [Fact]
    public void A_newer_recovery_file_is_reported_as_newer()
    {
        var project = new Core.Entities.Project { Name = "Version" };
        var root = JsonNode.Parse(ProjectSerializer.SerializeRecovery(project,
            new RecoveryInfo(null, DateTimeOffset.UnixEpoch, 1, DateTimeOffset.UnixEpoch)))!.AsObject();
        root["formatVersion"] = ProjectSerializer.CurrentFormatVersion + 1;
        root["project"]!["settings"]!["frameRate"] = "an unknown kind of value";
        var ex = Assert.Throws<ProjectFileException>(() => ProjectSerializer.DeserializeRecovery(root.ToJsonString(), _ => true));
        Assert.Contains("newer", ex.Message);
    }

    [Fact]
    public void The_peek_reads_only_the_top_level()
    {
        var format = ProjectSerializer.FormatId;
        Assert.False(ProjectSerializer.PeekNewerVersion("[1, 2]", format));
        Assert.False(ProjectSerializer.PeekNewerVersion("{\"format\":\"other\",\"formatVersion\":999}", format));
        Assert.False(ProjectSerializer.PeekNewerVersion("{\"format\":\"" + format + "\",\"formatVersion\":999,\"x\":[[[", format));
        Assert.True(ProjectSerializer.PeekNewerVersion("{\"format\":\"" + format + "\",\"formatVersion\":999}", format));
        Assert.False(ProjectSerializer.PeekNewerVersion("{\"format\":\"" + format + "\",\"formatVersion\":" +
            ProjectSerializer.CurrentFormatVersion + "}", format));
    }
}
