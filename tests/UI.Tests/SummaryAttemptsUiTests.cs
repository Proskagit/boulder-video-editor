using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Project;
using AiVideoEditor.Timeline;
using AiVideoEditor.UI.Services;
using AiVideoEditor.UI.ViewModels.Panels;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiVideoEditor.UI.Tests;

/// <summary>D074 amendment 1: the Inspector of «Итоги» lists its routes; the attempts typed there are stated on the route's
/// «Боулдер» plates in one undo step (the last attempt takes the number, the earlier ones are cleared); empty — counted again;
/// a flash is not editable.</summary>
public sealed class SummaryAttemptsUiTests
{
    private readonly UndoRedoService _undo = new();
    private readonly ProjectService _projects;
    private readonly TimelineEditService _edit;
    private readonly StatusService _status = new();

    public SummaryAttemptsUiTests()
    {
        _projects = new ProjectService(_undo, NullLogger<ProjectService>.Instance);
        _edit = new TimelineEditService(_projects, _undo, NullLogger<TimelineEditService>.Instance);
    }

    private TemplateClip Plate(Track track, double start, string route, BoulderStatus status, int? attempts = null)
    {
        var plate = new TemplateClip { Kind = TemplateKind.Boulder, TimelineStart = MediaTime.FromSeconds(start), Duration = MediaTime.FromSeconds(10),
            Route = route, Grade = "6B", Status = status, StatusAt = MediaTime.FromSeconds(8), Attempts = attempts };
        track.Clips.Add(plate);
        return plate;
    }

    [Fact]
    public void Typing_the_attempts_states_them_on_the_routes_last_plate()
    {
        var track = new Track { Type = TrackType.Video, Name = "V2", Order = 1 };
        _projects.Current.Timeline.VideoTracks.Add(track);
        var first = Plate(track, 0, "7", BoulderStatus.Attempt, attempts: 3);
        var last = Plate(track, 20, "7", BoulderStatus.Top);
        Plate(track, 40, "9", BoulderStatus.Flash);
        var summary = new TemplateClip { Kind = TemplateKind.Summary, TimelineStart = MediaTime.FromSeconds(60), Duration = MediaTime.FromSeconds(10) };
        track.Clips.Add(summary);

        var timeline = new TimelineViewModel(_projects, _edit, _status, NullLogger<TimelineViewModel>.Instance);
        var inspector = new InspectorViewModel(_edit, _status);
        timeline.SelectionChanged += (_, s) => { if (s is not null) inspector.ShowClip(s); };
        timeline.OnClipPressed(timeline.Tracks.SelectMany(t => t.Clips).Single(c => c.Id == summary.Id), toggle: false);

        Assert.Equal(2, inspector.SummaryAttempts.Count);
        var row = inspector.SummaryAttempts[0];
        Assert.Equal(("7 · 6B", "3"), (row.Label, row.AttemptsText));
        Assert.False(inspector.SummaryAttempts[1].IsEditable);              // a flash: one attempt

        inspector.SummaryAttempts[0].AttemptsText = "6";
        Assert.Equal((null, (int?)6), (first.Attempts, last.Attempts));
        Assert.Equal("6", inspector.SummaryAttempts[0].AttemptsText);
        _undo.Undo();                                                         // one step
        Assert.Equal(((int?)3, (int?)null), (first.Attempts, last.Attempts));

        inspector.SummaryAttempts[0].AttemptsText = "";                       // empty: counted from the video
        Assert.Equal(((int?)null, (int?)null), (first.Attempts, last.Attempts));
        inspector.SummaryAttempts[0].AttemptsText = "0";                      // refused
        Assert.Equal(((int?)null, (int?)null), (first.Attempts, last.Attempts));
    }
}
