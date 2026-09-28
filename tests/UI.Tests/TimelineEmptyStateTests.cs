using AiVideoEditor.Core.Common;
using AiVideoEditor.Project;
using AiVideoEditor.Timeline;
using AiVideoEditor.UI.Services;
using AiVideoEditor.UI.ViewModels.Panels;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiVideoEditor.UI.Tests;

/// <summary>
/// The empty timeline's hint (D024 Step 9.8) is shown exactly while no track has a clip — after adding, undoing,
/// and replacing the project.
/// </summary>
public sealed class TimelineEmptyStateTests
{
    private readonly UndoRedoService _undo = new();
    private readonly ProjectService _projects;
    private readonly TimelineViewModel _timeline;

    public TimelineEmptyStateTests()
    {
        _projects = new ProjectService(_undo, NullLogger<ProjectService>.Instance);
        var edit = new TimelineEditService(_projects, _undo, NullLogger<TimelineEditService>.Instance);
        _timeline = new TimelineViewModel(_projects, edit, new StatusService(), NullLogger<TimelineViewModel>.Instance);
    }

    [Fact]
    public void Empty_until_a_clip_is_added_and_again_after_undo_and_a_new_project()
    {
        Assert.True(_timeline.IsEmpty);
        Assert.NotEmpty(_timeline.Tracks); // tracks alone don't count

        _timeline.AddTextCommand.Execute(null);
        Assert.False(_timeline.IsEmpty);

        _undo.Undo();
        Assert.True(_timeline.IsEmpty);

        _undo.Redo();
        Assert.False(_timeline.IsEmpty);

        _projects.CreateNew("Fresh");
        Assert.True(_timeline.IsEmpty);
    }

    [Fact]
    public void Adding_a_track_keeps_it_empty()
    {
        _timeline.AddVideoTrackCommand.Execute(null);
        _timeline.AddAudioTrackCommand.Execute(null);
        Assert.True(_timeline.IsEmpty);
    }
}
