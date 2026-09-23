using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Project;
using AiVideoEditor.Project.Persistence;
using AiVideoEditor.UI.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiVideoEditor.UI.Tests;

/// <summary>Startup recovery offer and closing, through <see cref="ProjectFileWorkflow"/> with the
/// real ProjectService / AutosaveService and scripted dialog answers.</summary>
public sealed class RecoveryWorkflowTests : IDisposable
{
    private const int Recover = 0, Discard = 1, NotNow = 2;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "AiVideoEditorTests", Guid.NewGuid().ToString("N"));
    private readonly UndoRedoService _undo = new();
    private readonly ProjectService _projects;
    private readonly RecoveryStore _store;
    private readonly AutosaveService _autosave;
    private readonly List<string> _analysed = new();
    private readonly StatusService _status = new();

    public RecoveryWorkflowTests()
    {
        Directory.CreateDirectory(_root);
        _projects = new ProjectService(_undo, NullLogger<ProjectService>.Instance);
        _store = new RecoveryStore(Path.Combine(_root, "recovery"));
        _autosave = new AutosaveService(_projects, _store, NullLogger<AutosaveService>.Instance);
    }

    public void Dispose()
    {
        _autosave.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private ProjectFileWorkflow Workflow(ScriptedDialogs dialogs)
    {
        var coordinator = new MediaAnalysisCoordinator(new RecordingAnalysis(_analysed), _projects, NullLogger<MediaAnalysisCoordinator>.Instance);
        return new ProjectFileWorkflow(_projects, coordinator, _autosave, dialogs, new ScriptedPicker(), _status, NullLogger<ProjectFileWorkflow>.Instance);
    }

    private sealed class RecordingAnalysis(List<string> calls) : IMediaAnalysisService
    {
        public Task<MediaAnalysisResult> AnalyzeAsync(string filePath, CancellationToken ct = default)
        {
            calls.Add(filePath);
            return Task.FromResult(MediaAnalysisResult.Success(new MediaMetadata { Duration = new MediaTime(1) }));
        }
    }

    /// <summary>A recovery file left by a crashed session (its process isn't running).</summary>
    private string WriteLeftover(string name, string? folder = null, Action<Core.Entities.Project>? configure = null)
    {
        var project = new Core.Entities.Project { Name = name };
        project.Timeline.VideoTracks.Add(new Track { Type = TrackType.Video, Name = "V1" });
        project.Timeline.AudioTracks.Add(new Track { Type = TrackType.Audio, Name = "A1" });
        project.Timeline.Name = "recovered timeline";
        configure?.Invoke(project);
        Directory.CreateDirectory(_store.RootFolder);
        var path = _store.PathFor(project.Id);
        File.WriteAllText(path, ProjectSerializer.SerializeRecovery(project, new RecoveryInfo(folder, DateTimeOffset.UtcNow, 0, null)));
        return path;
    }

    [Fact]
    public async Task Startup_without_recovery_asks_nothing()
    {
        var dialogs = new ScriptedDialogs();

        await Workflow(dialogs).StartSessionAsync();

        Assert.Empty(dialogs.Asked);
        Assert.Equal("Ready.", _status.Message);
    }

    [Fact]
    public async Task Recover_restores_the_autosaved_project_as_unsaved_work()
    {
        var media = Path.Combine(_root, "a.wav");
        File.WriteAllText(media, "x");
        var path = WriteLeftover("Film", Path.Combine(_root, "Film"),
            p => p.MediaAssets.Add(new MediaAsset { FilePath = media, Kind = MediaKind.Audio }));
        var dialogs = new ScriptedDialogs(Recover);

        await Workflow(dialogs).StartSessionAsync();

        var asked = Assert.Single(dialogs.Asked);
        Assert.Contains("\"Film\"", asked.Message);
        Assert.Contains(Path.Combine(_root, "Film"), asked.Message);
        Assert.Equal(new[] { "Recover", "Discard", "Not now" }, asked.Buttons);

        Assert.Equal("Film", _projects.Current.Name);
        Assert.Equal("recovered timeline", _projects.Current.Timeline.Name);
        Assert.True(_projects.Current.IsDirty);
        Assert.Equal(new[] { media }, _analysed);                 // media without metadata analysed
        Assert.StartsWith("Recovered unsaved changes to \"Film\"", _status.Message);
        Assert.True(File.Exists(path));                          // kept until the project is saved
    }

    [Fact]
    public async Task Discard_deletes_the_recovery_file_and_keeps_the_empty_project()
    {
        var current = _projects.Current;
        var path = WriteLeftover("Film");

        await Workflow(new ScriptedDialogs(Discard)).StartSessionAsync();

        Assert.False(File.Exists(path));
        Assert.Same(current, _projects.Current);
        Assert.StartsWith("Discarded the autosaved changes", _status.Message);
    }

    [Theory]
    [InlineData(NotNow)]
    [InlineData(null)] // dialog closed
    public async Task Not_now_keeps_the_recovery_file_for_next_time(int? answer)
    {
        var current = _projects.Current;
        var path = WriteLeftover("Film");

        await Workflow(new ScriptedDialogs(answer)).StartSessionAsync();

        Assert.True(File.Exists(path));
        Assert.Same(current, _projects.Current);
        Assert.Contains("you'll be asked again", _status.Message);
    }

    [Fact]
    public async Task Damaged_recovery_file_does_not_break_startup()
    {
        Directory.CreateDirectory(_store.RootFolder);
        File.WriteAllText(_store.PathFor(Guid.NewGuid()), "{ damaged");
        var current = _projects.Current;
        var dialogs = new ScriptedDialogs();

        await Workflow(dialogs).StartSessionAsync();

        Assert.Empty(dialogs.Asked);
        Assert.Same(current, _projects.Current);
        Assert.Equal("1 recovery file couldn't be used and was set aside.", _status.Message);
    }

    [Fact]
    public async Task Recovery_that_fails_validation_at_restore_leaves_the_current_project()
    {
        var path = WriteLeftover("Film");
        var current = _projects.Current;
        // The file is damaged after it was found (e.g. by another program) — restore re-validates.
        var dialogs = new ScriptedDialogs(Recover) { OnAsk = () => File.WriteAllText(path, "{ damaged") };

        await Workflow(dialogs).StartSessionAsync();

        Assert.Same(current, _projects.Current);
        Assert.StartsWith("Couldn't recover \"Film\".", _status.Message);
    }

    [Fact]
    public async Task Closing_a_clean_project_leaves_no_recovery_file()
    {
        _undo.Execute(new NamedEdit(_projects, "x"));
        await _autosave.AutosaveNowAsync();
        await _projects.SaveAsAsync(Path.Combine(_root, "Film"));

        Assert.True(await Workflow(new ScriptedDialogs()).PrepareToCloseAsync());

        Assert.Empty(Directory.GetFiles(_store.RootFolder));
    }

    private sealed class NamedEdit(IProjectService projects, string name) : IUndoableCommand
    {
        private string _old = "";
        public string Description => "Rename";
        public void Execute() { _old = projects.Current.Timeline.Name; projects.Current.Timeline.Name = name; projects.NotifyTimelineChanged(); }
        public void Undo() { projects.Current.Timeline.Name = _old; projects.NotifyTimelineChanged(); }
    }
}
