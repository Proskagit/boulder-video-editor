using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Localization;
using AiVideoEditor.Core.Telemetry;
using AiVideoEditor.Core.Tests.Telemetry;
using AiVideoEditor.Media;
using AiVideoEditor.Project;
using AiVideoEditor.UI.Localization;
using AiVideoEditor.UI.Services;
using AiVideoEditor.UI.ViewModels.Panels;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiVideoEditor.UI.Tests;

/// <summary>
/// «Тренировка COROS…» (Phase Coros C1): a FIT file is read from disk, parsed and held in the session — never in
/// <c>project.json</c>. Refusals show their reason in the status bar and keep the training already held.
/// </summary>
public sealed class TrainingImportTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "aive-training-tests", Guid.NewGuid().ToString("N"));
    private readonly ProjectService _projects = new(new UndoRedoService(), NullLogger<ProjectService>.Instance);
    private readonly StatusService _status = new();
    private readonly ScriptedPicker _picker = new();
    private readonly TrainingSessionService _session;

    public TrainingImportTests()
    {
        Directory.CreateDirectory(_folder);
        _session = new TrainingSessionService(_projects);
    }

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private TrainingImportService Service(long maxFileBytes = TrainingImportService.DefaultMaxFileBytes) =>
        new(NullLogger<TrainingImportService>.Instance, maxFileBytes);

    private TrainingImportWorkflow Workflow(Core.Interfaces.ITrainingImportService? service = null, Func<Task>? showStatus = null) =>
        new(_picker, service ?? Service(), _projects, _session, _status, NullLogger<TrainingImportWorkflow>.Instance,
            showStatus ?? (() => Task.CompletedTask));

    private string Write(string name, byte[] bytes)
    {
        var path = Path.Combine(_folder, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private string CorosFile(string name = "training.fit") => Write(name, CorosFixture.Writer().Build());

    // --- the file service ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_coros_export_is_read_from_disk()
    {
        var workout = await Service().ImportAsync(CorosFile());

        Assert.Equal("COROS APEX 4 42mm", workout.DeviceName);
        Assert.Equal(CorosFixture.Start, workout.StartUtc);
        Assert.Equal(2, workout.Climbs.Count());
        Assert.Equal(TimeSpan.FromHours(3), workout.UtcOffset);
    }

    [Fact]
    public async Task A_missing_file_is_refused()
    {
        var ex = await Assert.ThrowsAsync<TrainingFileException>(() => Service().ImportAsync(Path.Combine(_folder, "gone.fit")));
        Assert.Equal(TrainingFileError.FileNotFound, ex.Error);

        ex = await Assert.ThrowsAsync<TrainingFileException>(() => Service().ImportAsync(Path.Combine(_folder, "no-folder", "a.fit")));
        Assert.Equal(TrainingFileError.FileNotFound, ex.Error);
    }

    [Fact]
    public async Task A_file_over_the_limit_is_refused_before_it_is_read()
    {
        var bytes = CorosFixture.Writer().Build();
        var path = Write("big.fit", bytes);

        var ex = await Assert.ThrowsAsync<TrainingFileException>(() => Service(maxFileBytes: bytes.Length - 1).ImportAsync(path));
        Assert.Equal(TrainingFileError.TooLarge, ex.Error);
        Assert.NotNull(await Service(maxFileBytes: bytes.Length).ImportAsync(path));   // exactly the limit is accepted
    }

    [Fact]
    public async Task A_locked_file_is_unreadable()
    {
        var path = CorosFile();
        using var _ = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var ex = await Assert.ThrowsAsync<TrainingFileException>(() => Service().ImportAsync(path));
        Assert.Equal(TrainingFileError.Unreadable, ex.Error);
    }

    [Fact]
    public async Task Empty_and_damaged_files_keep_the_parsers_reason()
    {
        var ex = await Assert.ThrowsAsync<TrainingFileException>(() => Service().ImportAsync(Write("empty.fit", Array.Empty<byte>())));
        Assert.Equal(TrainingFileError.NotAFitFile, ex.Error);

        var damaged = CorosFixture.Writer().Build();
        damaged[40] ^= 0xFF;
        ex = await Assert.ThrowsAsync<TrainingFileException>(() => Service().ImportAsync(Write("damaged.fit", damaged)));
        Assert.Equal(TrainingFileError.Corrupted, ex.Error);
    }

    // --- the workflow -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task An_imported_training_is_held_in_the_session_and_summarized()
    {
        var path = CorosFile();
        _picker.Files.Enqueue(path);
        var shown = new List<string>();

        await Workflow(showStatus: () => { shown.Add(_status.Message); return Task.CompletedTask; }).RunAsync();

        var request = Assert.Single(_picker.FileRequests);
        Assert.False(request.AllowMultiple);
        Assert.Equal(new[] { "*.fit" }, Assert.Single(request.FileTypeFilters).Patterns);
        Assert.Equal(new[] { "Importing the training…" }, shown);
        Assert.Equal(path, _session.FilePath);
        Assert.NotNull(_session.Workout);

        var local = CorosFixture.Start.ToOffset(TimeSpan.FromHours(3));
        var culture = System.Globalization.CultureInfo.CurrentCulture;
        Assert.Equal(
            $"Training imported: COROS APEX 4 42mm, {local.ToString("d", culture)} {local.ToString("t", culture)}–"
            + $"{local.AddSeconds(60).ToString("t", culture)} (UTC+03:00), 2 climbs",
            _status.Message);
    }

    [Fact]
    public async Task What_was_left_out_follows_the_summary()
    {
        var bytes = CorosFixture.Writer(localOffsetSeconds: 100, extraRecords: w => w
            .Define(7, 20, (253, 4, FitTestWriter.UInt32), (3, 1, FitTestWriter.UInt8))
            .Data(7, CorosFixture.T0 + 61, 10L)        // implausible
            .Data(7, CorosFixture.T0 + 62, 255L)).Build();
        _picker.Files.Enqueue(Write("warnings.fit", bytes));

        await Workflow().RunAsync();

        Assert.StartsWith("Training imported: COROS APEX 4 42mm, ", _status.Message);
        Assert.Contains("(UTC)", _status.Message);
        Assert.EndsWith("; 1 implausible heart-rate value left out; the watch's time zone is not usable; times are shown in UTC",
            _status.Message);
    }

    [Fact]
    public async Task A_refused_file_shows_its_reason_and_keeps_the_training_held()
    {
        _picker.Files.Enqueue(CorosFile());
        await Workflow().RunAsync();
        var held = _session.Workout;

        _picker.Files.Enqueue(Write("garmin.fit", CorosFixture.Writer(manufacturer: 1).Build()));
        await Workflow().RunAsync();

        Assert.Equal("Training not imported: the activity was not recorded by a COROS watch.", _status.Message);
        Assert.Same(held, _session.Workout);
    }

    [Fact]
    public async Task A_cancelled_picker_changes_nothing()
    {
        _status.Report("Earlier.");
        await Workflow().RunAsync();

        Assert.Equal("Earlier.", _status.Message);
        Assert.Null(_session.Workout);
    }

    [Fact]
    public async Task An_unexpected_failure_does_not_leave_importing_behind()
    {
        _picker.Files.Enqueue(@"C:\x.fit");
        var failure = new InvalidOperationException("boom");

        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => Workflow(new FailingService(failure)).RunAsync()));
        Assert.Equal("Training import didn't finish.", _status.Message);
        Assert.Null(_session.Workout);
    }

    [Fact]
    public async Task A_training_read_while_another_project_was_opened_is_not_kept()
    {
        _picker.Files.Enqueue(CorosFile());

        await Workflow(showStatus: () => { _projects.CreateNew("Other"); return Task.CompletedTask; }).RunAsync();

        Assert.Null(_session.Workout);
        Assert.Equal("Training command stopped: another project was opened, so nothing was changed.", _status.Message);
    }

    [Fact]
    public async Task Another_project_drops_the_training()
    {
        _picker.Files.Enqueue(CorosFile());
        await Workflow().RunAsync();
        var changes = 0;
        _session.Changed += (_, _) => changes++;

        _projects.CreateNew("Next");

        Assert.Null(_session.Workout);
        Assert.Null(_session.FilePath);
        Assert.Equal(1, changes);
    }

    [Fact]
    public async Task The_training_is_saved_as_a_reference_to_its_file_never_its_telemetry()
    {
        // Step 35.2 kept the training in the session only; since Step 35.4 (D049, P2 / S1) the project stores a reference
        // to the FIT file — paths, size, SHA-256, activity, clock correction — and never the parsed telemetry.
        var path = CorosFile();
        _picker.Files.Enqueue(path);
        await Workflow().RunAsync();
        Assert.NotNull(_session.Workout);
        Assert.True(_projects.Current.IsDirty);                          // a project change, not an Undo step

        var projectFolder = Path.Combine(_folder, "project");
        await _projects.SaveAsAsync(projectFolder);

        var json = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(projectFolder, "project.json")))!;
        var training = json["training"]!;
        Assert.Equal(Path.GetFullPath(path), training["filePath"]!.GetValue<string>());
        Assert.Equal(new FileInfo(path).Length, training["fileSizeBytes"]!.GetValue<long>());
        Assert.Equal(64, training["sha256"]!.GetValue<string>().Length);
        Assert.Equal(0, training["clockCorrectionTicks"]!.GetValue<long>());
        foreach (var word in new[] { "heartRate", "samples", "bpm", "segments" })
            Assert.DoesNotContain(word, training.ToJsonString(), StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(_session.Workout);   // saving does not drop it
    }

    // --- the toolbar --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_toolbar_imports_and_its_tooltip_shows_the_training_held()
    {
        var toolbar = Toolbar(Workflow(), out var editingLock);
        var notified = new List<string?>();
        toolbar.PropertyChanged += (_, e) => notified.Add(e.PropertyName);

        Assert.Equal("COROS training: import a .fit file, point to its file, the training's clock correction. The project keeps a reference to the file.",
            toolbar.TrainingToolTip);
        Assert.False(toolbar.HasTraining);
        Assert.False(toolbar.RemoveTrainingCommand.CanExecute(null));
        Assert.True(toolbar.ImportTrainingCommand.CanExecute(null));

        _picker.Files.Enqueue(CorosFile("Болдеринг.fit"));
        await toolbar.ImportTrainingCommand.ExecuteAsync(null);

        Assert.Contains(nameof(ToolbarViewModel.TrainingToolTip), notified);
        // The tooltip's resource spans lines: its line ends are the checkout's (CRLF on a Windows CI checkout).
        Assert.Equal(
            $"Project training: {TrainingText.Summary(_session.Workout!)}\nБолдеринг.fit\nFile available.",
            toolbar.TrainingToolTip.ReplaceLineEndings("\n"));
        Assert.True(toolbar.HasTraining);
        Assert.True(toolbar.RemoveTrainingCommand.CanExecute(null));
        Assert.False(toolbar.RelinkTrainingCommand.CanExecute(null));    // only while the file is not available

        using var held = editingLock.Acquire();
        Assert.False(toolbar.ImportTrainingCommand.CanExecute(null));   // not during an export
    }

    [Fact]
    public void Without_the_workflow_the_command_is_disabled()
    {
        var toolbar = Toolbar(null, out _);
        Assert.False(toolbar.ImportTrainingCommand.CanExecute(null));
    }

    private ToolbarViewModel Toolbar(TrainingImportWorkflow? workflow, out EditingLock editingLock)
    {
        editingLock = new EditingLock();
        var undo = new UndoRedoService();
        var analysis = new MediaAnalysisCoordinator(new NoAnalysis(), _projects, NullLogger<MediaAnalysisCoordinator>.Instance);
        var import = new MediaImportWorkflow(_picker, new NoMediaImport(), _projects, analysis, _status, NullLogger<MediaImportWorkflow>.Instance);
        var files = new ProjectFileWorkflow(_projects, analysis, new NullAutosave(), new ScriptedDialogs(), _picker, _status, NullLogger<ProjectFileWorkflow>.Instance);
        return new ToolbarViewModel(undo, files, import, _status, editingLock: editingLock, trainingImport: workflow, trainingSession: _session);
    }

    // --- the texts ----------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("en")]
    [InlineData("ru")]
    public void Every_refusal_reason_and_warning_has_its_own_text(string language)
    {
        using var _ = UiLanguage.Use(language);
        var errors = Enum.GetValues<TrainingFileError>().Select(TrainingText.Error).ToList();
        var warnings = Enum.GetValues<TrainingFileWarningKind>().Select(k => TrainingText.Warning(new TrainingFileWarning(k, 2))).ToList();

        Assert.Equal(errors.Count, errors.Distinct().Count());
        Assert.Equal(warnings.Count, warnings.Distinct().Count());
        Assert.All(errors.Concat(warnings), t => Assert.False(string.IsNullOrWhiteSpace(t)));
        Assert.DoesNotContain(errors.Concat(warnings), t => Enum.GetNames<TrainingFileError>().Contains(t) || Enum.GetNames<TrainingFileWarningKind>().Contains(t));
    }

    [Fact]
    public void Russian_texts_follow_the_count()
    {
        using var _ = UiLanguage.Use("ru");

        Assert.Equal("1 подход", Plural.Format(1, Strings.Training_Climbs));
        Assert.Equal("3 подхода", Plural.Format(3, Strings.Training_Climbs));
        Assert.Equal("14 подходов", Plural.Format(14, Strings.Training_Climbs));
        Assert.Equal("пропущено 5 недостоверных значений пульса",
            TrainingText.Warning(new TrainingFileWarning(TrainingFileWarningKind.ImplausibleHeartRate, 5)));
        Assert.Equal("Тренировка не импортирована: файл не найден.",
            string.Format(Strings.Training_Refused, TrainingText.Error(TrainingFileError.FileNotFound)));
    }

    [Theory]
    [InlineData(null, "UTC")]
    [InlineData(0, "UTC")]
    [InlineData(180, "UTC+03:00")]
    [InlineData(-330, "UTC−05:30")]
    public void Zone_names(int? minutes, string expected) =>
        Assert.Equal(expected, TrainingText.ZoneName(minutes is { } m ? TimeSpan.FromMinutes(m) : null));

    private sealed class FailingService(Exception failure) : Core.Interfaces.ITrainingImportService
    {
        public Task<ClimbingWorkout> ImportAsync(string filePath, CancellationToken ct = default) => Task.FromException<ClimbingWorkout>(failure);
        public Task<Core.Interfaces.TrainingFile> LoadAsync(string filePath, CancellationToken ct = default) =>
            Task.FromException<Core.Interfaces.TrainingFile>(failure);
    }

    private sealed class NoAnalysis : Core.Interfaces.IMediaAnalysisService
    {
        public Task<Core.Interfaces.MediaAnalysisResult> AnalyzeAsync(string filePath, CancellationToken ct = default) =>
            Task.FromResult(Core.Interfaces.MediaAnalysisResult.Failure(Core.Interfaces.MediaAnalysisOutcome.ProbeToolUnavailable, "test"));
    }

    private sealed class NoMediaImport : Core.Interfaces.IMediaImportService
    {
        public IReadOnlySet<string> SupportedExtensions { get; } = new HashSet<string>();
        public Task<Core.Interfaces.MediaImportBatchResult> ImportManyAsync(IEnumerable<string> filePaths, CancellationToken ct = default) =>
            Task.FromResult(new Core.Interfaces.MediaImportBatchResult());
    }
}
