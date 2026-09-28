using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Project;
using AiVideoEditor.UI.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiVideoEditor.UI.Tests;

/// <summary>
/// While the picked files are checked, the status bar says "Importing N files…" (D024 Step 9.8) — shown before the
/// check runs, then replaced by the result; the import itself is unchanged.
/// </summary>
public sealed class ImportStatusTests
{
    private readonly ProjectService _projects = new(new UndoRedoService(), NullLogger<ProjectService>.Instance);
    private readonly StatusService _status = new();
    private readonly List<string> _log = new();

    private MediaImportWorkflow Workflow(IMediaImportService import, params string[] picked) =>
        new(new FilesPicker(picked), import,
            _projects, new MediaAnalysisCoordinator(new NoAnalysis(), _projects, NullLogger<MediaAnalysisCoordinator>.Instance),
            _status, NullLogger<MediaImportWorkflow>.Instance,
            showStatus: () => { _log.Add($"shown: {_status.Message}"); return Task.CompletedTask; });

    [Theory]
    [InlineData(1, "Importing 1 file…")]
    [InlineData(3, "Importing 3 files…")]
    public async Task Status_is_shown_before_the_check_and_replaced_by_the_result(int count, string importing)
    {
        var import = new RecordingImport(_status, _log);
        var picked = Enumerable.Range(0, count).Select(i => $@"C:\media\{i}.wav").ToArray();

        await Workflow(import, picked).RunAsync();

        Assert.Equal(new[] { $"shown: {importing}", $"import while: {importing}" }, _log);
        Assert.Equal("Nothing imported", _status.Message); // the result, as before
    }

    [Fact]
    public async Task Cancelled_picker_leaves_the_status_alone()
    {
        _status.Report("Earlier.");
        await Workflow(new RecordingImport(_status, _log)).RunAsync();

        Assert.Empty(_log);
        Assert.Equal("Earlier.", _status.Message);
    }

    [Fact]
    public async Task A_failing_check_does_not_leave_importing_behind()
    {
        var failure = new InvalidOperationException("boom");
        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Workflow(new FailingImport(failure), @"C:\media\a.wav").RunAsync());

        Assert.Same(failure, thrown);
        Assert.Equal("Import didn't finish.", _status.Message);
    }

    private sealed class FilesPicker(string[] files) : IFilePickerService
    {
        public Task<IReadOnlyList<string>> PickFilesAsync(FilePickerRequest request, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<string>>(files);
        public Task<string?> PickFolderAsync(FolderPickerRequest request, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task<string?> PickSaveFileAsync(SaveFilePickerRequest request, CancellationToken ct = default) => Task.FromResult<string?>(null);
    }

    private sealed class RecordingImport(StatusService status, List<string> log) : IMediaImportService
    {
        public IReadOnlySet<string> SupportedExtensions { get; } = new HashSet<string>();
        public Task<MediaImportBatchResult> ImportManyAsync(IEnumerable<string> filePaths, CancellationToken ct = default)
        {
            log.Add($"import while: {status.Message}");
            return Task.FromResult(new MediaImportBatchResult());
        }
    }

    private sealed class FailingImport(Exception failure) : IMediaImportService
    {
        public IReadOnlySet<string> SupportedExtensions { get; } = new HashSet<string>();
        public Task<MediaImportBatchResult> ImportManyAsync(IEnumerable<string> filePaths, CancellationToken ct = default) =>
            Task.FromException<MediaImportBatchResult>(failure);
    }

    private sealed class NoAnalysis : IMediaAnalysisService
    {
        public Task<MediaAnalysisResult> AnalyzeAsync(string filePath, CancellationToken ct = default) =>
            Task.FromResult(MediaAnalysisResult.Failure(MediaAnalysisOutcome.ProbeToolUnavailable, "test"));
    }
}
