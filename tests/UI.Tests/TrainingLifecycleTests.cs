using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Telemetry;
using AiVideoEditor.Core.Tests.Telemetry;
using AiVideoEditor.Media;
using AiVideoEditor.Project;
using AiVideoEditor.Project.Persistence;
using AiVideoEditor.UI.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiVideoEditor.UI.Tests;

/// <summary>
/// Step 35.4 (D049 §9): the project's FIT reference is resolved after Open — equal bytes → Available; the same activity in
/// other bytes → Changed (used, the new identity saved with the next save); another activity → OtherActivity; refused →
/// Unreadable; no file → Missing (and Available again when it returns) — never dirty, never blocking. Replace asks and
/// resets the clock correction, remove drops it, relink to another activity asks; the videos' overrides always stay.
/// </summary>
public sealed class TrainingLifecycleTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "aive-training-life", Guid.NewGuid().ToString("N"));
    private readonly ProjectService _projects = new(new UndoRedoService(), NullLogger<ProjectService>.Instance);
    private readonly StatusService _status = new();
    private readonly ScriptedPicker _picker = new();
    private readonly QueuedDialogs _dialogs = new();
    private readonly TrainingImportService _import = new(NullLogger<TrainingImportService>.Instance);
    private readonly TrainingSessionService _session;
    private readonly TrainingImportWorkflow _workflow;
    private static readonly DateTimeOffset Zero = new(2026, 9, 27, 7, 30, 26, TimeSpan.Zero);

    public TrainingLifecycleTests()
    {
        Directory.CreateDirectory(_folder);
        _session = new TrainingSessionService(_projects, _import);
        _workflow = new TrainingImportWorkflow(_picker, _import, _projects, _session, _status, NullLogger<TrainingImportWorkflow>.Instance,
            () => Task.CompletedTask, _dialogs);
    }

    /// <summary>Answers the dialogs in the order queued (none queued = closed) and records them.</summary>
    private sealed class QueuedDialogs : IDialogService
    {
        private readonly Queue<int?> _answers = new();
        public List<DialogRequest> Asked { get; } = new();
        public void Enqueue(int? answer) => _answers.Enqueue(answer);

        public Task<int?> AskAsync(DialogRequest request)
        {
            Asked.Add(request);
            return Task.FromResult(_answers.Count > 0 ? _answers.Dequeue() : null);
        }
    }

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private string Fit(string name, Action<FitTestWriter>? extra = null)
    {
        var path = Path.Combine(_folder, name);
        File.WriteAllBytes(path, CorosFixture.Writer(extraRecords: extra).Build());
        return path;
    }

    private static void ExtraSample(FitTestWriter w) => w
        .Define(7, 20, (253, 4, FitTestWriter.UInt32), (3, 1, FitTestWriter.UInt8))
        .Data(7, CorosFixture.T0 + 61, 120L);

    /// <summary>Saves a project referencing <paramref name="fit"/> (as read from <paramref name="asImported"/>), reopens it and
    /// waits for the resolution.</summary>
    private async Task<TrainingReference> OpenWithTrainingAsync(string fit, string asImported, TrainingActivity? activity = null)
    {
        var file = await _import.LoadAsync(asImported);
        var project = new Core.Entities.Project { Name = "Coros" };
        project.Timeline.VideoTracks.Add(new Track { Type = TrackType.Video, Name = "V1" });
        project.Timeline.AudioTracks.Add(new Track { Type = TrackType.Audio, Name = "A1" });
        project.Training = new TrainingReference
        {
            FilePath = fit, FileSizeBytes = file.FileSizeBytes, Sha256 = file.Sha256,
            Activity = activity ?? TrainingActivity.Of(file.Workout), ClockCorrection = new MediaTime(7)
        };
        var folder = Path.Combine(_folder, "project");
        Directory.CreateDirectory(folder);
        File.WriteAllText(ProjectFileStore.ProjectFilePath(folder), ProjectSerializer.Serialize(project, folder));
        await _projects.OpenAsync(folder);
        await _session.ResolveAsync();
        return _projects.Current.Training!;
    }

    [Fact]
    public async Task The_same_file_is_available_and_used_and_the_project_stays_clean()
    {
        var fit = Fit("a.fit");
        var training = await OpenWithTrainingAsync(fit, fit);

        Assert.Equal(TrainingAvailability.Available, training.Availability);
        Assert.NotNull(_session.Workout);
        Assert.Equal(new MediaTime(7), training.ClockCorrection);
        Assert.False(_projects.Current.IsDirty);
    }

    [Fact]
    public async Task Other_bytes_of_the_same_activity_are_changed_used_and_take_the_new_identity()
    {
        var original = Fit("orig.fit");
        var fit = Fit("a.fit", ExtraSample);                               // same session, other bytes
        var training = await OpenWithTrainingAsync(fit, original);

        Assert.Equal(TrainingAvailability.Changed, training.Availability);
        Assert.NotNull(_session.Workout);
        Assert.Equal(new FileInfo(fit).Length, training.FileSizeBytes);
        Assert.Equal((await _import.LoadAsync(fit)).Sha256, training.Sha256);
    }

    [Fact]
    public async Task Another_activity_is_not_used()
    {
        var fit = Fit("a.fit");
        var referenced = Fit("referenced.fit", ExtraSample);              // the reference was made from other bytes…
        var other = new TrainingActivity(Zero, Zero.AddHours(1), 294, 814); // …of another activity
        var training = await OpenWithTrainingAsync(fit, referenced, other);

        Assert.Equal(TrainingAvailability.OtherActivity, training.Availability);
        Assert.Null(_session.Workout);
    }

    [Fact]
    public async Task A_missing_file_is_missing_and_available_again_when_it_returns()
    {
        var fit = Fit("a.fit");
        var bytes = File.ReadAllBytes(fit);
        var training = await OpenWithTrainingAsync(fit, fit);
        File.Delete(fit);
        await _session.ResolveAsync();
        Assert.Equal(TrainingAvailability.Missing, training.Availability);
        Assert.Null(_session.Workout);
        Assert.False(_projects.Current.IsDirty);

        File.WriteAllBytes(fit, bytes);
        await _session.ResolveAsync();                                     // the window re-check
        Assert.Equal(TrainingAvailability.Available, training.Availability);
    }

    [Fact]
    public async Task An_unreadable_file_is_unreadable()
    {
        var fit = Fit("a.fit");
        var training = await OpenWithTrainingAsync(fit, fit);
        File.WriteAllText(fit, "not a fit file");
        await _session.ResolveAsync();

        Assert.Equal(TrainingAvailability.Unreadable, training.Availability);
    }

    private MediaAsset OverriddenVideo()
    {
        var video = new MediaAsset { FilePath = Path.Combine(_folder, "v.mp4"), Kind = MediaKind.Video, RecordingZeroOverrideUtc = Zero };
        _projects.Current.MediaAssets.Add(video);
        return video;
    }

    [Fact]
    public async Task Replacing_asks_resets_the_clock_correction_and_keeps_the_overrides()
    {
        var video = OverriddenVideo();
        _picker.Files.Enqueue(Fit("a.fit"));
        await _workflow.RunAsync();
        var first = _session.Training!;
        first.ClockCorrection = new MediaTime(99);
        Assert.Empty(_dialogs.Asked);                                      // nothing to replace yet

        _dialogs.Enqueue(1);                                               // «Отмена»
        _picker.Files.Enqueue(Fit("b.fit"));
        await _workflow.RunAsync();
        Assert.Same(first, _session.Training);

        _dialogs.Enqueue(0);                                               // «Заменить»
        _picker.Files.Enqueue(Fit("c.fit"));
        await _workflow.RunAsync();
        Assert.NotSame(first, _session.Training);
        Assert.Equal(MediaTime.Zero, _session.Training!.ClockCorrection);
        Assert.Equal(Zero, video.RecordingZeroOverrideUtc);
        Assert.Equal(2, _dialogs.Asked.Count);
    }

    [Fact]
    public async Task Removing_drops_the_training_and_its_correction_but_not_the_overrides()
    {
        var video = OverriddenVideo();
        _picker.Files.Enqueue(Fit("a.fit"));
        await _workflow.RunAsync();

        _workflow.Remove();

        Assert.Null(_projects.Current.Training);
        Assert.True(_projects.Current.IsDirty);
        Assert.Equal(Zero, video.RecordingZeroOverrideUtc);
        Assert.Equal("The training was removed from the project.", _status.Message);
    }

    [Fact]
    public async Task Relinking_a_missing_file_keeps_the_correction_and_asks_only_for_another_activity()
    {
        var fit = Fit("a.fit");
        var training = await OpenWithTrainingAsync(fit, fit);
        File.Delete(fit);
        await _session.ResolveAsync();

        _picker.Files.Enqueue(Fit("moved.fit"));                           // the same activity
        await _workflow.RelinkAsync();
        Assert.Empty(_dialogs.Asked);
        Assert.Equal(TrainingAvailability.Available, training.Availability);
        Assert.Equal(new MediaTime(7), training.ClockCorrection);
        Assert.EndsWith("moved.fit", training.FilePath);

        training.Availability = TrainingAvailability.Missing;
        training.Activity = new TrainingActivity(Zero, Zero.AddHours(1), 294, 814);   // pretend another activity was referenced
        _dialogs.Enqueue(1);                                               // «Отмена»
        _picker.Files.Enqueue(Fit("other.fit"));
        await _workflow.RelinkAsync();
        Assert.Single(_dialogs.Asked);
        Assert.EndsWith("moved.fit", training.FilePath);
    }
}
