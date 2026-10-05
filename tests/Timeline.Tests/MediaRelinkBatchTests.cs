using System.Collections.Concurrent;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Timeline.Commands;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiVideoEditor.Timeline.Tests;

/// <summary>
/// D026 §4 (Step 11.5, PO-4): batch relink search — one chosen folder, its own files only, exact names (case-insensitive
/// as Windows), every candidate checked by the one relink check of 11.4, a name shared by several offline items given to
/// none, nothing applied before <see cref="IMediaRelinkService.ApplyAllAsync"/>, which validates again and applies what
/// is still applicable as one undoable step. The old files don't exist (offline after the re-check); the searched folder
/// is a real temporary folder (listed through a recording, holdable stand-in except where the real listing is used), the
/// probe a fake analysis.
/// </summary>
public sealed class MediaRelinkBatchTests : IDisposable
{
    private static readonly FrameRate Rate = FrameRate.Fps25;

    private readonly TimelineFixture _f = new();
    private readonly FakeProbe _probe = new();
    private readonly ConcurrentQueue<int> _listingThreads = new();
    private readonly List<IReadOnlyList<MediaAsset>> _relinked = new();
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "aive-relink-batch", Guid.NewGuid().ToString("N"));
    private volatile TaskCompletionSource? _listingGate;
    private MediaRelinkService _relink;

    public MediaRelinkBatchTests()
    {
        _f.Settings.FrameRate = Rate;
        _f.Settings.IsFrameRateLocked = true;
        _relink = Service(ListFake);
        _f.Projects.MediaRelinked += (_, e) => _relinked.Add(e.Assets);
    }

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { }
    }

    // --- fakes and helpers -------------------------------------------------------------------------------------------

    private sealed class FakeProbe : IMediaAnalysisService
    {
        public ConcurrentDictionary<string, MediaMetadata> Results { get; } = new(StringComparer.OrdinalIgnoreCase);
        public ConcurrentQueue<string> Calls { get; } = new();

        public Task<MediaAnalysisResult> AnalyzeAsync(string filePath, CancellationToken ct = default)
        {
            Calls.Enqueue(filePath);
            return Task.FromResult(Results.TryGetValue(filePath, out var m)
                ? MediaAnalysisResult.Success(m)
                : MediaAnalysisResult.Failure(MediaAnalysisOutcome.InvalidMedia, "Could not read the file."));
        }
    }

    private MediaRelinkService Service(Func<string, IReadOnlyList<string>?>? listFiles) =>
        new(_f.Projects, _f.UndoRedo, _probe, NullLogger<MediaRelinkService>.Instance,
            path => File.Exists(path) ? new FileInfo(path).Length : null, listFiles);

    /// <summary>The folder's own files (as the real listing), recorded and holdable (a slow share).</summary>
    private IReadOnlyList<string>? ListFake(string folder)
    {
        _listingThreads.Enqueue(Environment.CurrentManagedThreadId);
        _listingGate?.Task.Wait(TimeSpan.FromSeconds(10));
        return Directory.Exists(folder) ? Directory.GetFiles(folder, "*", SearchOption.TopDirectoryOnly) : null;
    }

    private static MediaMetadata Meta(double seconds, int width = 640) => new()
    {
        Duration = MediaTime.FromSeconds(seconds), Width = width, Height = 360, DisplayWidth = width, DisplayHeight = 360,
        DisplayRotation = 0, FrameRate = Rate, AvgFrameRate = Rate, StartTime = MediaTime.Zero, VideoCodec = "h264",
        AudioCodec = "aac", AudioSampleRate = 48000, AudioChannels = 2
    };

    /// <summary>An analysed video whose file is gone, with a clip using its first <paramref name="used"/> seconds.</summary>
    private MediaAsset Offline(string name, double used = 4, double at = 0)
    {
        var asset = _f.AddAsset(name, MediaKind.Video, Meta(10));
        var add = _f.Service.AddClip(asset.Id, null, MediaTime.FromFrame((long)(at * 25), Rate));
        Assert.True(add.Success, add.Message);
        Assert.True(_f.Service.TrimClip(add.ClipIds[0], ClipEdge.End, MediaTime.FromFrame((long)((at + used) * 25), Rate)).Success);
        return asset;
    }

    /// <summary>A file in the searched folder (or <paramref name="subfolder"/> of it), probed as <paramref name="metadata"/>.</summary>
    private string InFolder(string name, MediaMetadata? metadata, long size = 1000, string? subfolder = null)
    {
        var path = subfolder is null ? Path.Combine(_folder, name) : Path.Combine(_folder, subfolder, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[size]);
        if (metadata is not null) _probe.Results[path] = metadata;
        return path;
    }

    private RelinkSearchEntry Entry(RelinkSearch search, MediaAsset asset) => search.Entries.Single(e => e.AssetId == asset.Id);

    // --- matching ------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Only_exact_names_directly_in_the_folder_are_found()
    {
        var a = Offline("a.mp4");
        var b = Offline("b.mp4", at: 5);
        var c = Offline("c.mp4", at: 10);
        var d = Offline("d.mp4", at: 15);
        var candidate = InFolder("a.mp4", Meta(10));
        InFolder("b.mp4", Meta(10), subfolder: "sub");                      // not searched (no recursion)
        InFolder("c.mp4.bak", Meta(10));                                    // not the same name
        InFolder("d (1).mp4", Meta(10));                                    // no similar names

        var search = await _relink.SearchFolderAsync(_folder);

        Assert.Null(search.Problem);
        Assert.Equal(RelinkSearchOutcome.Found, Entry(search, a).Outcome);
        Assert.Equal(candidate, Entry(search, a).CandidatePath);
        Assert.Equal(RelinkSearchOutcome.NotFound, Entry(search, b).Outcome);
        Assert.Equal(RelinkSearchOutcome.NotFound, Entry(search, c).Outcome);
        Assert.Equal(RelinkSearchOutcome.NotFound, Entry(search, d).Outcome);
        Assert.Equal(new[] { candidate }, _probe.Calls);                     // only the match was checked
        Assert.False(_f.UndoRedo.NextUndo is RelinkMediaCommand);           // a search changes nothing
        Assert.True(a.IsMissing);
    }

    [Fact]
    public async Task The_real_folder_is_searched_without_its_subfolders_and_names_match_ignoring_case()
    {
        _relink = Service(null);                                            // the real Directory listing
        var a = Offline("Clip.mp4");
        var b = Offline("deep.mp4", at: 5);
        var upper = InFolder("CLIP.MP4", Meta(10));
        InFolder("deep.mp4", Meta(10), subfolder: "sub");

        var search = await _relink.SearchFolderAsync(_folder + Path.DirectorySeparatorChar);

        Assert.Equal(RelinkSearchOutcome.Found, Entry(search, a).Outcome);
        Assert.Equal(upper, Entry(search, a).CandidatePath, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(RelinkSearchOutcome.NotFound, Entry(search, b).Outcome);

        var result = await _relink.ApplyAllAsync(search.Applicable);
        Assert.Equal(1, result.AppliedCount);
        Assert.False(a.IsMissing);
    }

    [Fact]
    public async Task Nothing_offline_or_no_folder_is_said_and_changes_nothing()
    {
        var empty = await _relink.SearchFolderAsync(_folder);
        Assert.Empty(empty.Entries);
        Assert.Equal("No media is offline.", empty.Summary());

        Offline("a.mp4");
        _relink = Service(null);
        var missing = await _relink.SearchFolderAsync(Path.Combine(_folder, "nowhere"));
        Assert.NotNull(missing.Problem);
        Assert.Empty(missing.Applicable);
        Assert.False(_f.UndoRedo.NextUndo is RelinkMediaCommand);
    }

    [Fact]
    public async Task A_name_shared_by_two_offline_items_is_given_to_neither()
    {
        var first = Offline("take.mp4");
        var second = _f.AddAsset("take.mp4", MediaKind.Video, Meta(10));   // another folder, the same name
        var other = Offline("other.mp4", at: 5);
        InFolder("take.mp4", Meta(10));
        InFolder("other.mp4", Meta(10));

        var search = await _relink.SearchFolderAsync(_folder);

        Assert.Equal(RelinkSearchOutcome.Ambiguous, Entry(search, first).Outcome);
        Assert.Equal(RelinkSearchOutcome.Ambiguous, Entry(search, second).Outcome);
        Assert.Contains("one by one", Entry(search, first).Message);
        Assert.Equal(RelinkSearchOutcome.Found, Entry(search, other).Outcome);
        Assert.Equal(new[] { other.Id }, search.Applicable.Select(c => c.AssetId));
        Assert.DoesNotContain(_probe.Calls, p => p.EndsWith("take.mp4", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task A_file_that_belongs_to_another_asset_is_not_used()
    {
        var offline = Offline("shared.mp4");
        var candidate = InFolder("shared.mp4", Meta(10));
        var owner = _f.AddAsset("owner.mp4", MediaKind.Video, Meta(10));
        owner.FilePath = candidate;                                         // online: its file is in the folder

        var search = await _relink.SearchFolderAsync(_folder);

        var entry = Entry(search, offline);
        Assert.Equal(RelinkSearchOutcome.Rejected, entry.Outcome);
        Assert.Equal(RelinkRejection.PathInUse, entry.Check!.Rejection);
        Assert.Empty(search.Applicable);
    }

    [Fact]
    public async Task A_file_of_the_name_but_another_media_type_is_rejected_by_the_relink_check()
    {
        var asset = Offline("a.mp4");
        var path = InFolder("a.mp4", new MediaMetadata { Duration = MediaTime.FromSeconds(10), AudioCodec = "aac" });

        var search = await _relink.SearchFolderAsync(_folder);

        Assert.Equal(RelinkRejection.WrongMediaType, Entry(search, asset).Check!.Rejection);
        Assert.Equal(path, Entry(search, asset).CandidatePath);
    }

    [Fact]
    public async Task Every_candidate_gets_exactly_the_single_relink_check()
    {
        var ok = Offline("ok.mp4");
        var warned = Offline("warned.mp4", at: 5);
        var shortOne = Offline("short.mp4", used: 6, at: 10);
        InFolder("ok.mp4", Meta(10));
        InFolder("warned.mp4", Meta(10, width: 1280));
        InFolder("short.mp4", Meta(5));

        var search = await _relink.SearchFolderAsync(_folder);

        foreach (var asset in new[] { ok, warned, shortOne })
        {
            var batch = Entry(search, asset).Check!;
            var single = await _relink.CheckAsync(asset.Id, Entry(search, asset).CandidatePath!);
            Assert.Equal(single.Rejection, batch.Rejection);
            Assert.Equal(single.Message, batch.Message);
            Assert.Equal(single.Warnings, batch.Warnings);
            Assert.Equal(single.FileSizeBytes, batch.FileSizeBytes);
        }
    }

    // --- partial batch, summary, undo ----------------------------------------------------------------------------------

    [Fact]
    public async Task A_partial_batch_applies_the_usable_items_as_one_step_and_leaves_the_rest_offline()
    {
        var ok = Offline("ok.mp4");
        var warned = Offline("warned.mp4", at: 5);
        var shortOne = Offline("short.mp4", used: 6, at: 10);
        var absent = Offline("absent.mp4", at: 20);
        var okPath = InFolder("ok.mp4", Meta(10));
        var warnedPath = InFolder("warned.mp4", Meta(10, width: 1280));
        InFolder("short.mp4", Meta(5));
        var snapshot = _f.Snapshot();

        var search = await _relink.SearchFolderAsync(_folder);
        var summary = search.Summary();

        Assert.Contains("Found 2 of 4", summary);
        Assert.Contains("Picture size", summary);                           // the warning, before anything is applied
        Assert.Contains("short.mp4", summary);
        Assert.Contains("absent.mp4", summary);
        Assert.Equal(new[] { ok.Id, warned.Id }, search.Applicable.Select(c => c.AssetId).OrderBy(i => i == ok.Id ? 0 : 1));

        var result = await _relink.ApplyAllAsync(search.Applicable);

        Assert.Equal(2, result.AppliedCount);
        Assert.Equal(okPath, ok.FilePath);
        Assert.Equal(warnedPath, warned.FilePath);
        Assert.True(shortOne.IsMissing);
        Assert.True(absent.IsMissing);
        var step = Assert.IsType<RelinkMediaCommand>(_f.UndoRedo.NextUndo);
        Assert.Equal(2, step.Relinks.Count);
        Assert.Equal("Relink 2 Media Files", step.Description);
        Assert.Equal(2, Assert.Single(_relinked).Count);
        Assert.Equal(snapshot, _f.Snapshot());                              // no clip adapted
        Assert.True(_f.Project.IsDirty);
    }

    [Fact]
    public async Task Undo_and_redo_take_the_whole_batch()
    {
        var a = Offline("a.mp4");
        var b = Offline("b.mp4", at: 5);
        InFolder("a.mp4", Meta(10));
        InFolder("b.mp4", Meta(12));
        await _f.Projects.RecheckMediaAsync();
        var before = new[] { MediaFileState.Capture(a), MediaFileState.Capture(b) };
        await _relink.ApplyAllAsync((await _relink.SearchFolderAsync(_folder)).Applicable);
        var after = new[] { MediaFileState.Capture(a), MediaFileState.Capture(b) };

        _f.UndoRedo.Undo();
        Assert.Equal(before, new[] { MediaFileState.Capture(a), MediaFileState.Capture(b) });
        Assert.False(_f.UndoRedo.NextUndo is RelinkMediaCommand);           // one Undo took both

        _f.UndoRedo.Redo();
        Assert.Equal(after, new[] { MediaFileState.Capture(a), MediaFileState.Capture(b) });
        Assert.All(_relinked, assets => Assert.Equal(2, assets.Count));
    }

    [Fact]
    public async Task A_search_that_is_not_confirmed_changes_nothing_and_a_cancelled_one_neither()
    {
        var a = Offline("a.mp4");
        InFolder("a.mp4", Meta(10));
        var snapshot = _f.Snapshot();

        var search = await _relink.SearchFolderAsync(_folder);              // the user declines the summary
        Assert.Single(search.Applicable);
        Assert.True(a.IsMissing);
        Assert.False(_f.UndoRedo.NextUndo is RelinkMediaCommand);

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _relink.SearchFolderAsync(_folder, cts.Token));
        Assert.True(a.IsMissing);
        Assert.Equal(snapshot, _f.Snapshot());
        Assert.Empty(_relinked);
    }

    [Fact]
    public async Task A_second_search_after_a_partial_one_finds_only_what_is_still_offline()
    {
        var a = Offline("a.mp4");
        var b = Offline("b.mp4", at: 5);
        InFolder("a.mp4", Meta(10));
        await _relink.ApplyAllAsync((await _relink.SearchFolderAsync(_folder)).Applicable);
        Assert.False(a.IsMissing);
        Assert.True(b.IsMissing);

        var bPath = InFolder("b.mp4", Meta(10));                            // put there later
        var again = await _relink.SearchFolderAsync(_folder);

        Assert.Equal(b.Id, Assert.Single(again.Entries).AssetId);
        Assert.Equal(1, (await _relink.ApplyAllAsync(again.Applicable)).AppliedCount);
        Assert.Equal(bPath, b.FilePath);
        _f.UndoRedo.Undo();                                                 // two batches, two steps: the second only
        Assert.True(b.IsMissing);
        Assert.False(a.IsMissing);
    }

    // --- races between the search and Apply ----------------------------------------------------------------------------

    [Fact]
    public async Task Apply_validates_each_item_again_and_applies_the_rest()
    {
        var gone = Offline("gone.mp4");
        var changed = Offline("changed.mp4", at: 5);
        var taken = Offline("taken.mp4", at: 10);
        var fine = Offline("fine.mp4", at: 15);
        var gonePath = InFolder("gone.mp4", Meta(10));
        var changedPath = InFolder("changed.mp4", Meta(10), size: 100);
        var takenPath = InFolder("taken.mp4", Meta(10));
        InFolder("fine.mp4", Meta(10));
        var search = await _relink.SearchFolderAsync(_folder);
        Assert.Equal(4, search.Applicable.Count);

        File.Delete(gonePath);                                              // removed meanwhile
        File.WriteAllBytes(changedPath, new byte[200]);                     // replaced by another file
        var owner = _f.AddAsset("owner.mp4", MediaKind.Video, Meta(10));
        owner.FilePath = takenPath;                                         // imported meanwhile

        var result = await _relink.ApplyAllAsync(search.Applicable);

        RelinkResult Of(MediaAsset asset) => result.Results.Single(r => r.AssetId == asset.Id);
        Assert.Equal(RelinkRejection.FileNotFound, Of(gone).Rejection);
        Assert.Equal(RelinkRejection.Stale, Of(changed).Rejection);
        Assert.Equal(RelinkRejection.PathInUse, Of(taken).Rejection);
        Assert.True(Of(fine).Applied);
        Assert.Equal(1, result.AppliedCount);
        Assert.True(gone.IsMissing && changed.IsMissing && taken.IsMissing);
        Assert.Single(((RelinkMediaCommand)_f.UndoRedo.NextUndo!).Relinks);
    }

    [Fact]
    public async Task One_file_is_never_given_to_two_items_and_one_item_never_twice()
    {
        var first = Offline("a.mp4");
        var second = Offline("b.mp4", at: 5);
        var path = InFolder("a.mp4", Meta(10));
        var checkFirst = await _relink.CheckAsync(first.Id, path);
        var checkSecond = await _relink.CheckAsync(second.Id, path);        // checked one by one, both fine alone
        Assert.True(checkFirst.CanApply && checkSecond.CanApply);

        var result = await _relink.ApplyAllAsync(new[] { checkFirst, checkSecond, checkFirst });

        Assert.True(result.Results[0].Applied);
        Assert.Equal(RelinkRejection.PathInUse, result.Results[1].Rejection);
        Assert.Equal(RelinkRejection.Stale, result.Results[2].Rejection);
        Assert.True(second.IsMissing);
        Assert.Equal(path, first.FilePath);
    }

    [Fact]
    public async Task An_item_whose_old_file_came_back_meanwhile_is_left_alone()
    {
        var back = Offline("back.mp4");
        var other = Offline("other.mp4", at: 5);
        InFolder("back.mp4", Meta(10));
        InFolder("other.mp4", Meta(10));
        var search = await _relink.SearchFolderAsync(_folder);
        Directory.CreateDirectory(Path.GetDirectoryName(back.FilePath)!);
        File.WriteAllBytes(back.FilePath, new byte[1]);
        try
        {
            var result = await _relink.ApplyAllAsync(search.Applicable);

            Assert.Equal(RelinkRejection.NotOffline, result.Results.Single(r => r.AssetId == back.Id).Rejection);
            Assert.True(result.Results.Single(r => r.AssetId == other.Id).Applied);
        }
        finally
        {
            File.Delete(back.FilePath);
        }
    }

    // --- threads -------------------------------------------------------------------------------------------------------

    /// <summary>A UI thread stand-in: one dedicated thread that runs everything posted to its synchronization context, as
    /// the Avalonia dispatcher does (continuations of an await started on it come back to it).</summary>
    private sealed class UiThread : SynchronizationContext, IDisposable
    {
        private readonly System.Collections.Concurrent.BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = new();
        private readonly Thread _thread;

        public UiThread()
        {
            _thread = new Thread(() =>
            {
                SetSynchronizationContext(this);
                foreach (var (callback, state) in _queue.GetConsumingEnumerable()) callback(state);
            }) { IsBackground = true };
            _thread.Start();
        }

        public int ThreadId => _thread.ManagedThreadId;

        public override void Post(SendOrPostCallback d, object? state) => _queue.Add((d, state));

        /// <summary>Starts <paramref name="work"/> on the UI thread; the task completes when it does.</summary>
        public Task<T> Run<T>(Func<Task<T>> work)
        {
            var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            Post(async _ =>
            {
                try { done.SetResult(await work()); }
                catch (Exception ex) { done.SetException(ex); }
            }, null);
            return done.Task;
        }

        public void Dispose() => _queue.CompleteAdding();
    }

    [Fact]
    public async Task The_folder_is_listed_off_the_ui_thread_and_the_ui_thread_is_not_blocked()
    {
        var a = Offline("a.mp4");
        InFolder("a.mp4", Meta(10));
        using var ui = new UiThread();
        _listingGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);   // a slow share

        var search = ui.Run(() => _relink.SearchFolderAsync(_folder));
        await Eventually(() => !_listingThreads.IsEmpty, "the folder was not listed");
        var responsive = ui.Run(() => Task.FromResult(true));                 // the UI thread runs other work meanwhile
        Assert.True(await responsive.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.False(search.IsCompleted);

        _listingGate.SetResult();
        var result = await search.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(RelinkSearchOutcome.Found, Entry(result, a).Outcome);
        Assert.All(_listingThreads, t => Assert.NotEqual(ui.ThreadId, t));
    }

    private static async Task Eventually(Func<bool> condition, string what)
    {
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > until) throw new TimeoutException(what);
            await Task.Delay(5);
        }
    }
}
