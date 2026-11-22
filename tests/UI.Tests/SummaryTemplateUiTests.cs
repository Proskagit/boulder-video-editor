using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Core.Telemetry;
using AiVideoEditor.Project;
using AiVideoEditor.Timeline;
using AiVideoEditor.UI.Services;
using AiVideoEditor.UI.ViewModels.Panels;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiVideoEditor.UI.Tests;

/// <summary>
/// D073 through the edit service and the Inspector: «Итоги» is added by hand at the playhead (10 s, the header «Итоги дня» /
/// «Day summary» by the interface's language, the training's date); the Inspector edits its header, date, language and own
/// background, and a «Боулдер»'s stated attempts and FLASH — each an undo step; a split and a duplicate keep the new fields.
/// </summary>
public sealed class SummaryTemplateUiTests
{
    private static readonly FrameRate Rate = FrameRate.Fps25;
    private static readonly DateTimeOffset W0 = new(2026, 11, 3, 22, 30, 0, TimeSpan.Zero);   // 01:30 on 04.11 at UTC+3
    private readonly UndoRedoService _undo = new();
    private readonly ProjectService _projects;
    private readonly TimelineEditService _edit;
    private readonly StatusService _status = new();

    public SummaryTemplateUiTests()
    {
        _projects = new ProjectService(_undo, NullLogger<ProjectService>.Instance);
        _projects.Current.Settings.FrameRate = Rate;
        _edit = new TimelineEditService(_projects, _undo, NullLogger<TimelineEditService>.Instance);
    }

    private void Training()
    {
        var workout = new ClimbingWorkout
        {
            DeviceName = "COROS", Manufacturer = 294, Product = 814, StartUtc = W0, EndUtc = W0.AddMinutes(10), UtcOffset = TimeSpan.FromHours(3),
            ElapsedTime = MediaTime.FromSeconds(600), TimerTime = MediaTime.FromSeconds(600), Sport = 31, SubSport = 69, AverageHeartRate = null,
            MaxHeartRate = null, HeartRate = [], Pauses = [], Segments = [], Warnings = []
        };
        _projects.SetTraining(new TrainingReference
        {
            FilePath = @"C:\fit\a.fit", FileSizeBytes = 1, Sha256 = new string('a', 64), Activity = TrainingActivity.Of(workout),
            Workout = workout, Availability = TrainingAvailability.Available
        });
    }

    private TemplateClip Add(TemplateKind kind, double seconds = 0)
    {
        var result = _edit.AddTemplateClip(kind, MediaTime.FromSeconds(seconds));
        Assert.True(result.Success, result.Message);
        return _projects.Current.Timeline.VideoTracks.SelectMany(t => t.Clips).OfType<TemplateClip>().Single(t => t.Id == result.ClipIds[0]);
    }

    private InspectorViewModel Inspector(TemplateClip template)
    {
        var inspector = new InspectorViewModel(_edit, _status) { Playhead = () => MediaTime.Zero };
        inspector.ShowClip(new TimelineClipSelection(template, "T", null, Rate));
        return inspector;
    }

    [Fact]
    public void A_new_summary_lasts_ten_seconds_with_the_default_header_and_the_trainings_local_date()
    {
        Training();
        var summary = Add(TemplateKind.Summary);
        Assert.Equal(MediaTime.FromSeconds(10), summary.Duration);
        Assert.Equal(TemplateRules.InterfaceLanguage(), summary.Language);
        Assert.Equal(TemplateWords.Of(summary.Language).SummaryTitle, summary.Title);
        Assert.Equal("04.11.2026", summary.Subtitle);             // the training's local date, not its UTC one
        Assert.False(summary.OwnBackground);
    }

    [Fact]
    public void Without_a_training_the_date_is_empty()
    {
        var summary = Add(TemplateKind.Summary);
        Assert.Equal(string.Empty, summary.Subtitle);
    }

    [Fact]
    public void The_inspector_shows_the_summarys_fields_and_edits_them_in_undo_steps()
    {
        var summary = Add(TemplateKind.Summary);
        var inspector = Inspector(summary);
        Assert.True(inspector.ShowsTemplateSummary);
        Assert.True(inspector.ShowsTemplateLanguage);
        Assert.False(inspector.ShowsTemplateBoulder);

        inspector.TemplateOwnBackground = true;
        Assert.True(summary.OwnBackground);

        // The header still the default follows the language; a header of the user's own does not.
        var other = summary.Language == TemplateLanguage.English ? TemplateLanguage.Russian : TemplateLanguage.English;
        inspector.SetTemplateLanguageCommand.Execute(other);
        Assert.Equal((other, TemplateWords.Of(other).SummaryTitle), (summary.Language, summary.Title));
        inspector.TemplateTitle = "Финал сезона";
        inspector.SetTemplateLanguageCommand.Execute(summary.Language == TemplateLanguage.English ? TemplateLanguage.Russian : TemplateLanguage.English);
        Assert.Equal("Финал сезона", summary.Title);

        while (_undo.CanUndo) _undo.Undo();
        Assert.Empty(_projects.Current.Timeline.VideoTracks.SelectMany(t => t.Clips));
    }

    [Fact]
    public void A_boulders_attempts_are_typed_and_flash_is_a_status()
    {
        var area = Add(TemplateKind.Boulder);
        var inspector = Inspector(area);
        Assert.Contains(inspector.TemplateStatuses, o => o.Status == BoulderStatus.Flash);

        inspector.TemplateAttemptsText = "4";
        Assert.Equal(4, area.Attempts);
        inspector.TemplateAttemptsText = "0";                     // out of range: not applied
        Assert.Equal(4, area.Attempts);
        inspector.TemplateAttemptsText = "x";
        Assert.Equal(4, area.Attempts);
        inspector.TemplateAttemptsText = "";
        Assert.Null(area.Attempts);

        inspector.SetTemplateStatusCommand.Execute(BoulderStatus.Flash);
        Assert.Equal(BoulderStatus.Flash, area.Status);
        _undo.Undo();
        Assert.Equal(BoulderStatus.None, area.Status);
    }

    [Fact]
    public void Each_applied_change_is_one_undo_step_and_a_refused_value_none()
    {
        var area = Add(TemplateKind.Boulder);
        var inspector = Inspector(area);
        var before = _undo.CurrentPosition;
        inspector.TemplateAttemptsText = "0";
        inspector.TemplateAttemptsText = "x";
        Assert.Same(before, _undo.CurrentPosition);               // nothing applied, no step
        inspector.TemplateAttemptsText = "6";
        Assert.NotSame(before, _undo.CurrentPosition);
        _undo.Undo();                                             // one step back: the counted attempts again
        Assert.Same(before, _undo.CurrentPosition);
        Assert.Null(area.Attempts);

        var language = area.Language;
        inspector.SetTemplateLanguageCommand.Execute(language == TemplateLanguage.English ? TemplateLanguage.Russian : TemplateLanguage.English);
        _undo.Undo();
        Assert.Equal(language, area.Language);

        var summary = Add(TemplateKind.Summary, 20);
        var summaryInspector = Inspector(summary);
        var added = _undo.CurrentPosition;
        summaryInspector.TemplateOwnBackground = true;
        _undo.Undo();
        Assert.Same(added, _undo.CurrentPosition);
        Assert.False(summary.OwnBackground);
    }

    [Fact]
    public void A_split_and_a_duplicate_keep_the_attempts_the_language_and_the_background()
    {
        var area = Add(TemplateKind.Boulder);
        area.Attempts = 3;
        area.Language = TemplateLanguage.English;
        var summary = Add(TemplateKind.Summary, 20);
        summary.OwnBackground = true;

        Assert.True(_edit.Split(MediaTime.FromSeconds(4), new[] { area.Id }).Success);
        Assert.True(_edit.DuplicateClips(new[] { summary.Id }).Success);

        var templates = _projects.Current.Timeline.VideoTracks.SelectMany(t => t.Clips).OfType<TemplateClip>().ToList();
        Assert.All(templates.Where(t => t.Kind == TemplateKind.Boulder), t => Assert.Equal((3, TemplateLanguage.English), (t.Attempts!.Value, t.Language)));
        Assert.Equal(2, templates.Count(t => t.Kind == TemplateKind.Summary && t.OwnBackground));
    }
}
