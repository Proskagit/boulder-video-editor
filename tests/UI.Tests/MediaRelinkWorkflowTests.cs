using System.Collections.Concurrent;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Project;
using AiVideoEditor.Project.Persistence;
using AiVideoEditor.Timeline;
using AiVideoEditor.UI.Services;
using AiVideoEditor.UI.ViewModels.Panels;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiVideoEditor.UI.Tests;

/// <summary>
/// Phase 11 Step 11.6 (D026 §3–§5): <see cref="MediaRelinkWorkflow"/> and the Media Browser's Relink / Find Missing —
/// the pickers (kind filter, start folder), the rejection, warning, summary and partial-result dialogs, the offer to
/// search after a relink that was applied (and only then), the <see cref="EditingLock"/> before and after every await,
/// one workflow at a time, the races between the check and Apply, and what the Media Browser shows after relink, undo
/// and redo. Real project, timeline and relink services over a fake file system, probe and folder listing; scripted
/// pickers and dialogs.
/// </summary>
public sealed class MediaRelinkWorkflowTests
{
    private static readonly FrameRate Rate = FrameRate.Fps25;
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "aive-relink-workflow");

    private readonly ConcurrentDictionary<string, long> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, MediaMetadata> _probed = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _folders = new(StringComparer.OrdinalIgnoreCase);
    private readonly UndoRedoService _undo = new();
    private readonly ProjectService _projects;
    private readonly TimelineEditService _edit;
    private readonly MediaRelinkService _relink;
    private readonly ScriptedPicker _picker = new();
    private readonly Dialogs _dialogs = new();
    private readonly StatusService _status = new();
    private readonly EditingLock _lock = new();
    private readonly MediaRelinkWorkflow _workflow;
    private bool _probeUnavailable;
    private Action? _onProbe;

    public MediaRelinkWorkflowTests()
    {
        _projects = new ProjectService(_undo, NullLogger<ProjectService>.Instance, new ProjectFileStore(), path => _files.ContainsKey(path));
        _projects.Current.Settings.FrameRate = Rate;
        _projects.Current.Settings.IsFrameRateLocked = true;
        _edit = new TimelineEditService(_projects, _undo, NullLogger<TimelineEditService>.Instance);
        _relink = new MediaRelinkService(_projects, _undo, new Probe(this), NullLogger<MediaRelinkService>.Instance,
            path => _files.TryGetValue(path, out var size) ? size : null,
            folder => _files.Keys.Where(p => string.Equals(Path.GetDirectoryName(p), folder, StringComparison.OrdinalIgnoreCase)).ToList());
        _workflow = new MediaRelinkWorkflow(_relink, _projects, _picker, _dialogs, _status, _lock,
            NullLogger<MediaRelinkWorkflow>.Instance, folder => _folders.Contains(folder));
    }

    // --- fakes and helpers -------------------------------------------------------------------------------------------

    private sealed class Probe(MediaRelinkWorkflowTests owner) : IMediaAnalysisService
    {
        public Task<MediaAnalysisResult> AnalyzeAsync(string filePath, CancellationToken ct = default)
        {
            owner._onProbe?.Invoke();
            if (owner._probeUnavailable)
                return Task.FromResult(MediaAnalysisResult.Failure(MediaAnalysisOutcome.ProbeToolUnavailable, "ffprobe could not be found."));
            return Task.FromResult(owner._probed.TryGetValue(filePath, out var m)
                ? MediaAnalysisResult.Success(m)
                : MediaAnalysisResult.Failure(MediaAnalysisOutcome.InvalidMedia, "Could not read the file."));
        }
    }

    /// <summary>Answers each dialog by a function of the request (what the user does — also while it is open).</summary>
    private sealed class Dialogs : IDialogService
    {
        public List<DialogRequest> Asked { get; } = new();
        public Func<DialogRequest, int?> Answer { get; set; } = _ => 0;

        public Task<int?> AskAsync(DialogRequest request)
        {
            Asked.Add(request);
            return Task.FromResult(Answer(request));
        }
    }

    private static MediaMetadata Meta(double seconds, int width = 64, string? audio = "aac") => new()
    {
        Duration = MediaTime.FromSeconds(seconds), Width = width, Height = 36, DisplayWidth = width, DisplayHeight = 36,
        DisplayRotation = 0, FrameRate = Rate, AvgFrameRate = Rate, StartTime = MediaTime.Zero, VideoCodec = "h264",
        AudioCodec = audio, AudioSampleRate = audio is null ? null : 48000, AudioChannels = audio is null ? null : 2
    };

    /// <summary>An analysed video with a clip (4 s from <paramref name="at"/>) whose file is gone (offline).</summary>
    private async Task<MediaAsset> Offline(string name, double at = 0, string folder = "old")
    {
        var asset = new MediaAsset
        {
            FilePath = Path.Combine(Root, folder, name), Kind = MediaKind.Video, AnalysisStatus = MediaAnalysisStatus.Completed,
            Metadata = Meta(10)
        };
        _files[asset.FilePath] = 100;
        _projects.AddMediaAssets(new[] { asset });
        var add = _edit.AddClip(asset.Id, null, MediaTime.FromSeconds(at));
        Assert.True(add.Success, add.Message);
        Assert.True(_edit.TrimClip(add.ClipIds[0], ClipEdge.End, MediaTime.FromSeconds(at + 4)).Success);
        _files.TryRemove(asset.FilePath, out _);
        await _projects.RecheckMediaAsync();
        Assert.True(asset.IsMissing);
        return asset;
    }

    private string NewFile(string name, MediaMetadata? metadata, string folder = "new", long size = 200)
    {
        var path = Path.Combine(Root, folder, name);
        _files[path] = size;
        if (metadata is not null) _probed[path] = metadata;
        return path;
    }

    private bool AnyRelinkStep => _undo.NextUndo is Timeline.Commands.RelinkMediaCommand;

    private MediaBrowserViewModel Browser(ThumbnailCoordinator? thumbnails = null) =>
        new(_projects, new MediaImportWorkflow(_picker, new NoImport(), _projects,
                new MediaAnalysisCoordinator(new Probe(this), _projects, NullLogger<MediaAnalysisCoordinator>.Instance), _status,
                NullLogger<MediaImportWorkflow>.Instance),
            NullLogger<MediaBrowserViewModel>.Instance, _lock, thumbnails, _workflow);

    private sealed class NoImport : IMediaImportService
    {
        public IReadOnlySet<string> SupportedExtensions { get; } = new HashSet<string>();
        public Task<MediaImportBatchResult> ImportManyAsync(IEnumerable<string> filePaths, CancellationToken ct = default) =>
            Task.FromResult(new MediaImportBatchResult());
    }

    // --- single relink -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_relink_without_warnings_asks_nothing_and_offers_nothing_when_no_other_media_is_offline()
    {
        var asset = await Offline("a.mp4");
        var path = NewFile("a.mp4", Meta(10));
        _picker.Files.Enqueue(path);

        Assert.True(await _workflow.RelinkAsync(asset.Id));

        Assert.Empty(_dialogs.Asked);
        Assert.Equal(path, asset.FilePath);
        Assert.False(asset.IsMissing);
        Assert.True(AnyRelinkStep);
        Assert.Contains("is linked to", _status.Message);
    }

    [Fact]
    public async Task The_file_picker_offers_the_assets_kind_and_starts_in_its_old_folder_when_that_is_there()
    {
        var asset = await Offline("a.mp4");
        _folders.Add(Path.Combine(Root, "old"));

        await _workflow.RelinkAsync(asset.Id);                             // cancelled in the picker
        var request = Assert.Single(_picker.FileRequests);
        Assert.Equal(Path.Combine(Root, "old"), request.StartFolder);
        Assert.False(request.AllowMultiple);
        var patterns = Assert.Single(request.FileTypeFilters).Patterns;
        Assert.Contains("*.mp4", patterns);
        Assert.Contains("*.mov", patterns);
        Assert.DoesNotContain("*.wav", patterns);
        Assert.DoesNotContain("*.png", patterns);

        _folders.Clear();                                                   // the old folder is gone too
        await _workflow.RelinkAsync(asset.Id);
        Assert.Null(_picker.FileRequests[1].StartFolder);
    }

    [Fact]
    public async Task A_cancelled_picker_changes_nothing_and_asks_nothing()
    {
        var asset = await Offline("a.mp4");
        _onProbe = () => throw new InvalidOperationException("must not probe");

        Assert.False(await _workflow.RelinkAsync(asset.Id));

        Assert.Empty(_dialogs.Asked);
        Assert.True(asset.IsMissing);
        Assert.False(AnyRelinkStep);
    }

    [Fact]
    public async Task A_rejected_file_is_explained_and_nothing_is_applied_or_offered()
    {
        var asset = await Offline("a.mp4");
        await Offline("b.mp4", at: 5);                                      // another offline item: no offer either
        _picker.Files.Enqueue(NewFile("a.mp4", Meta(2)));                   // shorter than the 4 s the clip uses

        Assert.False(await _workflow.RelinkAsync(asset.Id));

        var shown = Assert.Single(_dialogs.Asked);
        Assert.Equal("Can't relink", shown.Title);
        Assert.Contains("0:00:04.000", shown.Message);
        Assert.Equal(new[] { "OK" }, shown.Buttons);
        Assert.True(asset.IsMissing);
        Assert.False(AnyRelinkStep);
    }

    [Fact]
    public async Task Warnings_are_confirmed_before_the_relink_and_cancelling_them_changes_nothing()
    {
        var asset = await Offline("a.mp4");
        await Offline("b.mp4", at: 5);
        var path = NewFile("a.mp4", Meta(10, width: 1280));
        _picker.Files.Enqueue(path);
        _dialogs.Answer = r => r.Title == "Relink with differences?" ? 1 : 0;   // Cancel

        Assert.False(await _workflow.RelinkAsync(asset.Id));
        var warning = Assert.Single(_dialogs.Asked);                        // no offer after a cancel
        Assert.Contains("Picture size", warning.Message);
        Assert.Equal(new[] { "Relink Anyway", "Cancel" }, warning.Buttons);
        Assert.True(asset.IsMissing);
        Assert.Equal("Relink cancelled.", _status.Message);

        _picker.Files.Enqueue(path);
        _dialogs.Answer = r => r.Title == "Find other missing media?" ? 1 : 0;   // Relink Anyway, then Not Now
        Assert.True(await _workflow.RelinkAsync(asset.Id));
        Assert.Equal(path, asset.FilePath);
    }

    [Fact]
    public async Task Without_ffprobe_the_relink_is_confirmed_and_the_row_says_not_analysed_yet()
    {
        var asset = await Offline("a.mp4");
        var browser = Browser();
        _probeUnavailable = true;
        _picker.Files.Enqueue(NewFile("a.mp4", null));

        Assert.True(await _workflow.RelinkAsync(asset.Id));

        var warning = Assert.Single(_dialogs.Asked);
        Assert.Contains("ffprobe is not available", warning.Message);
        Assert.Equal("Relink without a compatibility check?", warning.Title);          // D4: not "differs"
        Assert.StartsWith("The technical compatibility of \"a.mp4\" with \"a.mp4\" was not checked:", warning.Message);
        Assert.DoesNotContain("differs", warning.Message);
        Assert.Equal(MediaAnalysisStatus.Pending, asset.AnalysisStatus);
        Assert.Equal("Not analysed yet", browser.Items.Single(i => i.Asset.Id == asset.Id).TechnicalSummary);
    }

    // --- races between the check and Apply (the warning dialog is open meanwhile) ---------------------------------------

    public static TheoryData<string> Races => new() { "file gone", "file changed", "asset back online", "path taken", "project replaced" };

    [Theory]
    [MemberData(nameof(Races))]
    public async Task What_changed_while_the_user_was_asked_is_explained_and_nothing_is_applied(string race)
    {
        var asset = await Offline("a.mp4");
        await Offline("b.mp4", at: 5);
        var oldPath = asset.FilePath;
        var path = NewFile("a.mp4", Meta(10, width: 1280));                 // a warning: the user is asked
        _picker.Files.Enqueue(path);
        _dialogs.Answer = request =>
        {
            if (request.Title != "Relink with differences?") return 0;
            switch (race)
            {
                case "file gone": _files.TryRemove(path, out _); break;
                case "file changed": _files[path] = 999; break;
                case "asset back online": _files[oldPath] = 100; break;
                case "path taken":
                    var owner = new MediaAsset { FilePath = path, Kind = MediaKind.Video };
                    _projects.AddMediaAssets(new[] { owner });
                    break;
                case "project replaced": _projects.CreateNew("Other"); break;
            }
            return 0;                                                       // Relink Anyway
        };

        Assert.False(await _workflow.RelinkAsync(asset.Id));

        Assert.Equal(2, _dialogs.Asked.Count);                              // the warning, then why — no offer
        Assert.Equal("Relink not applied", _dialogs.Asked[1].Title);
        Assert.False(string.IsNullOrWhiteSpace(_dialogs.Asked[1].Message));
        Assert.Equal(oldPath, asset.FilePath);                             // the UI changed nothing itself
        Assert.False(AnyRelinkStep);
    }

    // --- the offer after a relink, and Find Missing ----------------------------------------------------------------------

    [Fact]
    public async Task After_a_relink_the_other_offline_media_are_looked_for_in_the_chosen_files_folder()
    {
        var a = await Offline("a.mp4");
        var b = await Offline("b.mp4", at: 5);
        var c = await Offline("c.mp4", at: 10);
        _picker.Files.Enqueue(NewFile("a.mp4", Meta(10)));
        var bPath = NewFile("b.mp4", Meta(10));                             // c is not there

        Assert.True(await _workflow.RelinkAsync(a.Id));

        Assert.Equal(new[] { "Find other missing media?", "Find Missing Media" }, _dialogs.Asked.Select(d => d.Title));
        Assert.Contains(Path.Combine(Root, "new"), _dialogs.Asked[0].Message);
        Assert.Contains("2 other media files", _dialogs.Asked[0].Message);
        Assert.Contains("c.mp4", _dialogs.Asked[1].Message);                // not in the folder
        Assert.Equal("Relink 1 File", _dialogs.Asked[1].Buttons[0]);
        Assert.Equal(bPath, b.FilePath);
        Assert.True(c.IsMissing);
        Assert.Empty(_picker.FolderRequests);                               // the folder was not asked for

        _undo.Undo();                                                       // the batch is its own step
        Assert.True(b.IsMissing);
        Assert.False(a.IsMissing);
    }

    [Fact]
    public async Task Not_now_searches_nothing()
    {
        var a = await Offline("a.mp4");
        var b = await Offline("b.mp4", at: 5);
        _picker.Files.Enqueue(NewFile("a.mp4", Meta(10)));
        NewFile("b.mp4", Meta(10));
        _dialogs.Answer = r => r.Title == "Find other missing media?" ? 1 : 0;

        Assert.True(await _workflow.RelinkAsync(a.Id));

        Assert.Single(_dialogs.Asked);
        Assert.True(b.IsMissing);
    }

    [Fact]
    public async Task Find_missing_shows_the_summary_and_applies_the_confirmed_items_as_one_step()
    {
        var found = await Offline("found.mp4");
        var warned = await Offline("warned.mp4", at: 5);
        var tooShort = await Offline("short.mp4", at: 10);
        var absent = await Offline("absent.mp4", at: 15);
        await Offline("twin.mp4", at: 20, folder: "x");
        await Offline("twin.mp4", at: 25, folder: "y");
        _folders.Add(Path.Combine(Root, "old"));
        NewFile("found.mp4", Meta(10), folder: "search");
        NewFile("warned.mp4", Meta(10, width: 1280), folder: "search");
        NewFile("short.mp4", Meta(2), folder: "search");
        NewFile("twin.mp4", Meta(10), folder: "search");
        var picker = new ScriptedPicker(Path.Combine(Root, "search"));
        var workflow = new MediaRelinkWorkflow(_relink, _projects, picker, _dialogs, _status, _lock,
            NullLogger<MediaRelinkWorkflow>.Instance, f => _folders.Contains(f));

        Assert.True(await workflow.FindMissingAsync());

        Assert.Equal(Path.Combine(Root, "old"), Assert.Single(picker.FolderRequests).StartFolder);
        var summary = Assert.Single(_dialogs.Asked);
        Assert.Contains("Found 2 of 6", summary.Message);
        Assert.Contains("Picture size", summary.Message);                   // the warned one, under it
        Assert.Contains("Can't be used:", summary.Message);
        Assert.Contains("short.mp4", summary.Message);
        Assert.Contains("one by one", summary.Message);                     // the twins
        Assert.Contains("Not in the folder (stay offline): absent.mp4", summary.Message);
        Assert.Equal(new[] { "Relink 2 Files", "Cancel" }, summary.Buttons);
        Assert.False(found.IsMissing || warned.IsMissing);
        Assert.True(tooShort.IsMissing && absent.IsMissing);
        Assert.Equal(2, ((Timeline.Commands.RelinkMediaCommand)_undo.NextUndo!).Relinks.Count);
        Assert.Equal("Relinked 2 media files.", _status.Message);
    }

    [Fact]
    public async Task A_cancelled_summary_changes_nothing()
    {
        var a = await Offline("a.mp4");
        NewFile("a.mp4", Meta(10), folder: "search");
        var workflow = new MediaRelinkWorkflow(_relink, _projects, new ScriptedPicker(Path.Combine(Root, "search")), _dialogs, _status,
            _lock, NullLogger<MediaRelinkWorkflow>.Instance, _ => false);
        _dialogs.Answer = _ => 1;                                           // Cancel

        Assert.False(await workflow.FindMissingAsync());

        Assert.True(a.IsMissing);
        Assert.False(AnyRelinkStep);
        Assert.Equal("Nothing was relinked.", _status.Message);
    }

    [Fact]
    public async Task Nothing_to_apply_is_only_reported()
    {
        await Offline("a.mp4");
        var workflow = new MediaRelinkWorkflow(_relink, _projects, new ScriptedPicker(Path.Combine(Root, "empty")), _dialogs, _status,
            _lock, NullLogger<MediaRelinkWorkflow>.Instance, _ => false);

        Assert.False(await workflow.FindMissingAsync());

        Assert.Equal(new[] { "OK" }, Assert.Single(_dialogs.Asked).Buttons);
        Assert.False(AnyRelinkStep);
        Assert.Equal("No offline media file was found in the folder.", _status.Message);
    }

    [Fact]
    public async Task Matches_that_cannot_be_used_are_not_reported_as_nothing_found()
    {
        var a = await Offline("a.mp4");
        NewFile("a.mp4", Meta(2), folder: "search");                        // the name matches, but it is too short
        var workflow = new MediaRelinkWorkflow(_relink, _projects, new ScriptedPicker(Path.Combine(Root, "search")), _dialogs, _status,
            _lock, NullLogger<MediaRelinkWorkflow>.Instance, _ => false);

        Assert.False(await workflow.FindMissingAsync());

        var summary = Assert.Single(_dialogs.Asked);
        Assert.Equal(new[] { "OK" }, summary.Buttons);
        Assert.Contains("Can't be used:", summary.Message);
        Assert.Equal("Files with matching names were found in the folder, but none of them can be used.", _status.Message);  // D5
        Assert.True(a.IsMissing);
    }

    [Fact]
    public async Task A_rejection_names_both_media_kinds_as_files()
    {
        var asset = await Offline("a.mp4");
        _picker.Files.Enqueue(NewFile("a.wav", null));

        Assert.False(await _workflow.RelinkAsync(asset.Id));

        Assert.Equal("\"a.wav\" is an audio file; \"a.mp4\" is a video file.", Assert.Single(_dialogs.Asked).Message);  // D3
    }

    [Fact]
    public async Task Items_refused_at_apply_are_listed_and_the_rest_is_relinked()
    {
        var a = await Offline("a.mp4");
        var b = await Offline("b.mp4", at: 5);
        var aPath = NewFile("a.mp4", Meta(10), folder: "search");
        NewFile("b.mp4", Meta(10), folder: "search");
        var workflow = new MediaRelinkWorkflow(_relink, _projects, new ScriptedPicker(Path.Combine(Root, "search")), _dialogs, _status,
            _lock, NullLogger<MediaRelinkWorkflow>.Instance, _ => false);
        _dialogs.Answer = r =>
        {
            if (r.Title == "Find Missing Media") _files.TryRemove(aPath, out _);   // gone while the summary is read
            return 0;
        };

        Assert.True(await workflow.FindMissingAsync());

        var partial = _dialogs.Asked[1];
        Assert.Equal("Relinked 1 of 2", partial.Title);
        Assert.Contains("a.mp4", partial.Message);
        Assert.Contains("doesn't exist any more", partial.Message);
        Assert.True(a.IsMissing);
        Assert.False(b.IsMissing);
        Assert.Equal("Relinked 1 media file.", _status.Message);
    }

    // --- EditingLock and one workflow at a time ---------------------------------------------------------------------------

    [Fact]
    public async Task The_commands_follow_the_selection_the_offline_media_and_the_editing_lock()
    {
        var browser = Browser();
        Assert.False(browser.HasOfflineMedia);
        Assert.False(browser.FindMissingCommand.CanExecute(null));

        var asset = await Offline("a.mp4");
        var online = new MediaAsset { FilePath = NewFile("online.mp4", Meta(10)), Kind = MediaKind.Video };
        _projects.AddMediaAssets(new[] { online });
        Assert.True(browser.HasOfflineMedia);
        Assert.True(browser.FindMissingCommand.CanExecute(null));
        Assert.False(browser.RelinkCommand.CanExecute(null));               // nothing selected

        browser.SelectedItem = browser.Items.Single(i => i.Asset.Id == online.Id);
        Assert.False(browser.RelinkCommand.CanExecute(null));               // online media is not relinked (PO-6)
        browser.SelectedItem = browser.Items.Single(i => i.Asset.Id == asset.Id);
        Assert.True(browser.RelinkCommand.CanExecute(null));

        var notified = 0;
        browser.RelinkCommand.CanExecuteChanged += (_, _) => notified++;
        browser.FindMissingCommand.CanExecuteChanged += (_, _) => notified++;
        using (_lock.Acquire())                                             // an export
        {
            Assert.True(notified >= 2);                                     // the buttons are told
            notified = 0;
            Assert.False(browser.RelinkCommand.CanExecute(null));
            Assert.False(browser.FindMissingCommand.CanExecute(null));
            Assert.False(await _workflow.RelinkAsync(asset.Id));            // not even by a direct call
            Assert.Empty(_picker.FileRequests);
        }
        Assert.True(notified >= 2);
        Assert.True(browser.RelinkCommand.CanExecute(null));                // available again after the export
        Assert.True(browser.FindMissingCommand.CanExecute(null));
    }

    [Fact]
    public async Task An_export_started_during_the_check_stops_the_relink()
    {
        var asset = await Offline("a.mp4");
        _picker.Files.Enqueue(NewFile("a.mp4", Meta(10, width: 1280)));     // it would have warnings
        IDisposable? export = null;
        _onProbe = () => export ??= _lock.Acquire();                        // the export starts while the file is probed

        Assert.False(await _workflow.RelinkAsync(asset.Id));

        Assert.Empty(_dialogs.Asked);                                       // stopped right after the check
        Assert.True(asset.IsMissing);
        Assert.Equal("An export is running — nothing was relinked.", _status.Message);
        export!.Dispose();
    }

    [Fact]
    public async Task An_export_started_while_the_user_is_asked_stops_the_relink_and_the_batch()
    {
        var asset = await Offline("a.mp4");
        _picker.Files.Enqueue(NewFile("a.mp4", Meta(10, width: 1280)));
        IDisposable? export = null;
        _dialogs.Answer = _ => { export ??= _lock.Acquire(); return 0; };

        Assert.False(await _workflow.RelinkAsync(asset.Id));
        Assert.True(asset.IsMissing);
        export!.Dispose();
        export = null;

        NewFile("a.mp4", Meta(10), folder: "search");
        var workflow = new MediaRelinkWorkflow(_relink, _projects, new ScriptedPicker(Path.Combine(Root, "search")), _dialogs, _status,
            _lock, NullLogger<MediaRelinkWorkflow>.Instance, _ => false);
        Assert.False(await workflow.FindMissingAsync());                    // locked while the summary is read
        Assert.True(asset.IsMissing);
        Assert.False(AnyRelinkStep);
        export!.Dispose();
    }

    [Fact]
    public async Task One_relink_workflow_runs_at_a_time()
    {
        var asset = await Offline("a.mp4");
        var picked = new TaskCompletionSource<IReadOnlyList<string>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var slowPicker = new BlockingPicker(picked.Task);
        var workflow = new MediaRelinkWorkflow(_relink, _projects, slowPicker, _dialogs, _status, _lock,
            NullLogger<MediaRelinkWorkflow>.Instance, _ => false);
        var browser = new MediaBrowserViewModel(_projects, new MediaImportWorkflow(_picker, new NoImport(), _projects,
                new MediaAnalysisCoordinator(new Probe(this), _projects, NullLogger<MediaAnalysisCoordinator>.Instance), _status,
                NullLogger<MediaImportWorkflow>.Instance), NullLogger<MediaBrowserViewModel>.Instance, _lock, null, workflow);
        browser.SelectedItem = browser.Items.Single(i => i.Asset.Id == asset.Id);

        var first = workflow.RelinkAsync(asset.Id);                          // the picker opens (after the start folder check)
        var until = DateTime.UtcNow.AddSeconds(5);
        while (Volatile.Read(ref slowPicker.Calls) == 0 && DateTime.UtcNow < until) await Task.Delay(2);
        Assert.True(workflow.IsRunning);
        Assert.False(browser.RelinkCommand.CanExecute(null));
        var findMissing = workflow.FindMissingAsync();                      // refused at once — never awaited blindly
        var secondRelink = workflow.RelinkAsync(asset.Id);
        Assert.True(findMissing.IsCompleted);
        Assert.True(secondRelink.IsCompleted);
        Assert.False(await findMissing);
        Assert.False(await secondRelink);
        Assert.Equal(1, Volatile.Read(ref slowPicker.Calls));
        Assert.Equal(0, slowPicker.FolderCalls);

        picked.SetResult(Array.Empty<string>());
        Assert.False(await first);
        Assert.False(workflow.IsRunning);
        Assert.True(browser.RelinkCommand.CanExecute(null));
    }

    private sealed class BlockingPicker(Task<IReadOnlyList<string>> files) : IFilePickerService
    {
        public int Calls;
        public Task<IReadOnlyList<string>> PickFilesAsync(FilePickerRequest request, CancellationToken ct = default)
        {
            Interlocked.Increment(ref Calls);
            return files;
        }
        public int FolderCalls;
        public Task<string?> PickFolderAsync(FolderPickerRequest request, CancellationToken ct = default)
        {
            Interlocked.Increment(ref FolderCalls);
            return Task.FromResult<string?>(null);
        }
        public Task<string?> PickSaveFileAsync(SaveFilePickerRequest request, CancellationToken ct = default) => Task.FromResult<string?>(null);
    }

    // --- what the Media Browser shows after relink, undo and redo ------------------------------------------------------

    private sealed class Thumbnails : IThumbnailService
    {
        public Thumbnail? TryGetCached(MediaAsset asset, string cacheFolder) => null;
        public Task<Thumbnail?> GetOrCreateAsync(MediaAsset asset, string cacheFolder, CancellationToken ct = default) =>
            Task.FromResult<Thumbnail?>(new Thumbnail(1, 1, new byte[] { (byte)asset.FilePath.Length, 0, 0, 255 }));
    }

    private sealed class Location : IThumbnailCacheLocation
    {
        public string CurrentFolder => "cache";
        public event EventHandler? Changed { add { } remove { } }
        public Task CleanUpUnsavedAsync() => Task.CompletedTask;
    }

    [Fact]
    public async Task The_media_browser_follows_relink_undo_and_redo_and_keeps_the_selection()
    {
        var thumbnails = new ThumbnailCoordinator(new Thumbnails(), _projects, new Location(), NullLogger<ThumbnailCoordinator>.Instance);
        var a = await Offline("a.mp4");
        var b = await Offline("b.mp4", at: 5);
        var browser = Browser(thumbnails);
        MediaBrowserItemViewModel Row(MediaAsset asset) => browser.Items.Single(i => i.Asset.Id == asset.Id);
        browser.SelectedItem = Row(a);
        _picker.Files.Enqueue(NewFile("a.mp4", Meta(10)));
        NewFile("b.mp4", Meta(10));
        await thumbnails.IdleAsync();
        var aBefore = thumbnails.Get(a.Id);                                 // made while online, kept offline (D024)
        var bBefore = thumbnails.Get(b.Id);

        Assert.True(await _workflow.RelinkAsync(a.Id));                     // and Search → b in the same folder
        await thumbnails.IdleAsync();
        Assert.Same(a, browser.SelectedItem!.Asset);                       // the selection survives the new path
        Assert.NotEqual("Media offline", Row(a).TechnicalSummary);
        Assert.NotEqual("Media offline", Row(b).TechnicalSummary);
        Assert.True(Row(a).HasThumbnail && Row(b).HasThumbnail);
        Assert.False(browser.HasOfflineMedia);
        Assert.False(browser.FindMissingCommand.CanExecute(null));

        _undo.Undo();                                                       // the batch: b offline again
        await thumbnails.IdleAsync();
        Assert.Equal("Media offline", Row(b).TechnicalSummary);
        Assert.Same(bBefore, Row(b).Thumbnail);                             // what it showed before, not the relinked file's
        Assert.True(browser.HasOfflineMedia);

        _undo.Undo();                                                       // the single relink: a offline again
        await thumbnails.IdleAsync();
        Assert.Equal("Media offline", Row(a).TechnicalSummary);
        Assert.Same(aBefore, Row(a).Thumbnail);
        Assert.Same(a, browser.SelectedItem!.Asset);
        Assert.True(browser.RelinkCommand.CanExecute(null));

        _undo.Redo();
        _undo.Redo();
        await thumbnails.IdleAsync();
        Assert.False(a.IsMissing || b.IsMissing);
        Assert.True(Row(a).HasThumbnail && Row(b).HasThumbnail);
        Assert.False(browser.HasOfflineMedia);
    }
}
