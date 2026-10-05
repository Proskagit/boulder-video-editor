using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Project;
using AiVideoEditor.UI.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiVideoEditor.UI.Tests;

/// <summary>
/// Phase 12 Step 12.8 (D027 §7): an import belongs to the project it was started in. When another project becomes
/// current while it runs — while the picker is open, while "Importing N files…" is being shown, or while the picked
/// files are checked — nothing is added to the new project, no analysis is queued, and the status bar says so. An
/// import without a project change works as before. The known issue of Step 9.3 (New during <c>ImportManyAsync</c>).
/// </summary>
public sealed class ImportProjectChangeTests
{
    private readonly ProjectService _projects = new(new UndoRedoService(), NullLogger<ProjectService>.Instance);
    private readonly StatusService _status = new();
    private readonly CountingAnalysis _analysis = new();
    private readonly MediaAnalysisCoordinator _coordinator;

    public ImportProjectChangeTests() =>
        _coordinator = new MediaAnalysisCoordinator(_analysis, _projects, NullLogger<MediaAnalysisCoordinator>.Instance);

    private static readonly string[] Picked = { @"C:\media\a.mp4", @"C:\media\b.wav" };

    private MediaImportWorkflow Workflow(Action? duringPick = null, Action? duringStatus = null, Action? duringCheck = null) =>
        new(new Picker(Picked, duringPick), new Import(duringCheck), _projects, _coordinator, _status,
            NullLogger<MediaImportWorkflow>.Instance,
            showStatus: () => { duringStatus?.Invoke(); return Task.CompletedTask; });

    private void NewProject() => _projects.CreateNew("Other");

    [Fact]
    public async Task New_while_the_files_are_checked_adds_nothing_to_the_new_project()
    {
        var first = _projects.Current;

        await Workflow(duringCheck: NewProject).RunAsync();

        Assert.NotSame(first, _projects.Current);
        Assert.Empty(_projects.Current.MediaAssets);
        Assert.Empty(first.MediaAssets);                                   // nor to the old one
        Assert.False(_projects.Current.IsDirty);
        await _coordinator.IdleAsync();
        Assert.Equal(0, _analysis.Calls);                                  // no analysis queued
        Assert.Equal(MediaImportWorkflow.ProjectChangedMessage, _status.Message);
    }

    [Fact]
    public async Task New_while_the_importing_status_is_shown_adds_nothing()
    {
        await Workflow(duringStatus: NewProject).RunAsync();

        Assert.Empty(_projects.Current.MediaAssets);
        Assert.Equal(MediaImportWorkflow.ProjectChangedMessage, _status.Message);
    }

    [Fact]
    public async Task Another_project_while_the_picker_is_open_adds_nothing()
    {
        var import = new Import(null);
        var workflow = new MediaImportWorkflow(new Picker(Picked, NewProject), import, _projects, _coordinator, _status,
            NullLogger<MediaImportWorkflow>.Instance, showStatus: () => Task.CompletedTask);

        await workflow.RunAsync();

        Assert.Empty(_projects.Current.MediaAssets);
        Assert.Equal(0, import.Calls);                                     // the files are not even checked
        Assert.Equal(MediaImportWorkflow.ProjectChangedMessage, _status.Message);
    }

    [Fact]
    public async Task Without_a_project_change_the_import_works_as_before()
    {
        var project = _projects.Current;

        await Workflow().RunAsync();

        Assert.Same(project, _projects.Current);
        Assert.Equal(Picked, project.MediaAssets.Select(a => a.FilePath));
        Assert.Equal("Imported 2 media files", _status.Message);
        await _coordinator.IdleAsync();
        Assert.Equal(2, _analysis.Calls);                                  // both analysed
    }

    [Fact]
    public async Task The_next_import_into_the_new_project_works()
    {
        await Workflow(duringCheck: NewProject).RunAsync();

        await Workflow().RunAsync();

        Assert.Equal(Picked, _projects.Current.MediaAssets.Select(a => a.FilePath));
    }

    // --- fakes ------------------------------------------------------------------------------------------------------------

    private sealed class Picker(string[] files, Action? whileOpen) : IFilePickerService
    {
        public Task<IReadOnlyList<string>> PickFilesAsync(FilePickerRequest request, CancellationToken ct = default)
        {
            whileOpen?.Invoke();
            return Task.FromResult<IReadOnlyList<string>>(files);
        }
        public Task<string?> PickFolderAsync(FolderPickerRequest request, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task<string?> PickSaveFileAsync(SaveFilePickerRequest request, CancellationToken ct = default) => Task.FromResult<string?>(null);
    }

    /// <summary>"Checks" the picked files: every one becomes an asset; <paramref name="whileChecking"/> runs during the
    /// check, as a New would while <c>ImportManyAsync</c> runs.</summary>
    private sealed class Import(Action? whileChecking) : IMediaImportService
    {
        public int Calls { get; private set; }
        public IReadOnlySet<string> SupportedExtensions { get; } = new HashSet<string>();

        public Task<MediaImportBatchResult> ImportManyAsync(IEnumerable<string> filePaths, CancellationToken ct = default)
        {
            Calls++;
            whileChecking?.Invoke();
            return Task.FromResult(new MediaImportBatchResult
            {
                Imported = filePaths.Select(p => new MediaAsset
                {
                    FilePath = p,
                    Kind = p.EndsWith(".wav", StringComparison.OrdinalIgnoreCase) ? MediaKind.Audio : MediaKind.Video
                }).ToList()
            });
        }
    }

    private sealed class CountingAnalysis : IMediaAnalysisService
    {
        private int _calls;
        public int Calls => _calls;

        public Task<MediaAnalysisResult> AnalyzeAsync(string filePath, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult(MediaAnalysisResult.Failure(MediaAnalysisOutcome.ProbeToolUnavailable, "test"));
        }
    }
}
