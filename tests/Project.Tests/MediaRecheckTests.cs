using System.Collections.Concurrent;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Project.Persistence;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiVideoEditor.Project.Tests;

/// <summary>
/// D026 §2 (Step 11.3): <see cref="ProjectService.RecheckMediaAsync"/> — the missing state follows the file system during
/// the session, off the caller's thread, as runtime state only (never dirty, never undoable), dropped for a replaced
/// project, never overlapping. The file system is a fake: files come and go, a check can be held like a slow drive.
/// </summary>
public sealed class MediaRecheckTests : IDisposable
{
    private readonly TempFolder _temp = new();
    private readonly UndoRedoService _undo = new();
    private readonly FakeFiles _files = new();
    private readonly ProjectService _service;
    private readonly List<MediaAvailabilityChangedEventArgs> _changes = new();
    private int _mediaEvents;
    private int _saveStateEvents;

    public MediaRecheckTests()
    {
        _service = new ProjectService(_undo, NullLogger<ProjectService>.Instance, new ProjectFileStore(), _files.Exists);
        _service.MediaAvailabilityChanged += (_, e) => { lock (_changes) _changes.Add(e); };
        _service.MediaAssetsChanged += (_, _) => Interlocked.Increment(ref _mediaEvents);
        _service.SaveStateChanged += (_, _) => Interlocked.Increment(ref _saveStateEvents);
    }

    public void Dispose() => _temp.Dispose();

    /// <summary>Paths that exist; <see cref="Exists"/> can be held at a gate (a slow or disconnected drive).</summary>
    private sealed class FakeFiles
    {
        private volatile TaskCompletionSource? _gate;
        public ConcurrentDictionary<string, byte> Present { get; } = new(StringComparer.OrdinalIgnoreCase);
        public ConcurrentQueue<(string Path, int Thread)> Calls { get; } = new();
        public Exception? Throw { get; set; }

        public TaskCompletionSource Hold() => _gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Release() { var gate = _gate; _gate = null; gate?.SetResult(); }

        /// <summary>The answer is what the file system was when the call started; a held call returns it late.</summary>
        public bool Exists(string path)
        {
            var present = Present.ContainsKey(path);
            Calls.Enqueue((path, Environment.CurrentManagedThreadId));
            _gate?.Task.Wait(TimeSpan.FromSeconds(10));
            if (Throw is { } ex) throw ex;
            return present;
        }

        public int CallsFor(string path) => Calls.Count(c => c.Path == path);
    }

    private MediaAsset Asset(string name, bool present = true)
    {
        var path = _temp.Combine(name);
        if (present) _files.Present[path] = 0;
        return new MediaAsset { FilePath = path, Kind = MediaKind.Audio };
    }

    /// <summary>A saved project with the assets (clean, empty history), as after Open.</summary>
    private async Task<Core.Entities.Project> OpenWith(params MediaAsset[] assets)
    {
        _service.AddMediaAssets(assets);
        await _service.SaveAsAsync(_temp.Combine("Project"));
        var project = await _service.OpenAsync(_temp.Combine("Project"));
        Interlocked.Exchange(ref _mediaEvents, 0);
        Interlocked.Exchange(ref _saveStateEvents, 0);
        _files.Calls.Clear();
        return project;
    }

    private MediaAsset In(Core.Entities.Project project, string name) => project.MediaAssets.Single(a => a.FileName == name);

    [Fact]
    public async Task A_file_gone_during_the_session_makes_its_asset_offline()
    {
        var project = await OpenWith(Asset("a.wav"), Asset("b.wav"));
        var a = In(project, "a.wav");
        Assert.False(a.IsMissing);

        _files.Present.TryRemove(a.FilePath, out _);
        await _service.RecheckMediaAsync();

        Assert.True(a.IsMissing);
        Assert.False(In(project, "b.wav").IsMissing);
        var change = Assert.Single(_changes);
        Assert.Same(a, Assert.Single(change.Gone));
        Assert.Empty(change.Returned);
        Assert.Equal(1, _mediaEvents);
    }

    [Fact]
    public async Task A_file_that_comes_back_makes_its_asset_online_again()
    {
        var gone = Asset("gone.wav", present: false);
        var project = await OpenWith(Asset("a.wav"), gone);
        var asset = In(project, "gone.wav");
        Assert.True(asset.IsMissing);                                           // missing at Open

        _files.Present[asset.FilePath] = 0;
        await _service.RecheckMediaAsync();

        Assert.False(asset.IsMissing);
        var change = Assert.Single(_changes);
        Assert.Same(asset, Assert.Single(change.Returned));
        Assert.Empty(change.Gone);
        Assert.Equal(1, _mediaEvents);
    }

    [Fact]
    public async Task Nothing_changed_raises_nothing()
    {
        await OpenWith(Asset("a.wav"), Asset("gone.wav", present: false));

        await _service.RecheckMediaAsync();

        Assert.Empty(_changes);
        Assert.Equal(0, _mediaEvents);
    }

    [Fact]
    public async Task A_recheck_never_makes_the_project_dirty_and_never_enters_the_history()
    {
        var project = await OpenWith(Asset("a.wav"), Asset("b.wav", present: false));
        var json = File.ReadAllText(ProjectFileStore.ProjectFilePath(project.ProjectFolderPath!));

        _files.Present.TryRemove(In(project, "a.wav").FilePath, out _);
        _files.Present[In(project, "b.wav").FilePath] = 0;
        await _service.RecheckMediaAsync();

        Assert.Single(_changes);
        Assert.False(project.IsDirty);
        Assert.False(_undo.CanUndo);
        Assert.Equal(0, _saveStateEvents);
        Assert.Equal(json, File.ReadAllText(ProjectFileStore.ProjectFilePath(project.ProjectFolderPath!)));
    }

    [Fact]
    public async Task The_file_system_is_read_off_the_callers_thread_and_the_caller_is_not_blocked()
    {
        var project = await OpenWith(Asset("a.wav"));
        _files.Present.TryRemove(In(project, "a.wav").FilePath, out _);
        _files.Hold();                                                         // a drive that doesn't answer

        var caller = Environment.CurrentManagedThreadId;
        var check = _service.RecheckMediaAsync();

        Assert.False(check.IsCompleted);                                       // returned while the drive hangs
        Assert.False(In(project, "a.wav").IsMissing);
        _files.Release();
        await check.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.All(_files.Calls, c => Assert.NotEqual(caller, c.Thread));
        Assert.True(In(project, "a.wav").IsMissing);
    }

    [Fact]
    public async Task A_check_for_a_project_that_was_replaced_meanwhile_is_dropped()
    {
        var project = await OpenWith(Asset("a.wav"));
        var asset = In(project, "a.wav");
        _files.Present.TryRemove(asset.FilePath, out _);
        _files.Hold();
        var check = _service.RecheckMediaAsync();

        _service.CreateNew("Other");                                           // New while the drive hangs
        _files.Release();
        await check.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.False(asset.IsMissing);                                         // the old project's asset is left alone
        Assert.Empty(_changes);
    }

    [Fact]
    public async Task An_asset_given_another_path_during_the_check_is_left_alone()
    {
        var project = await OpenWith(Asset("a.wav"));
        var asset = In(project, "a.wav");
        _files.Present.Clear();                                                // the old path is gone…
        _files.Hold();
        var check = _service.RecheckMediaAsync();

        asset.FilePath = _temp.Combine("elsewhere.wav");                        // …but the asset has another one now (a relink, 11.4)
        _files.Release();
        await check.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.False(asset.IsMissing);
        Assert.Empty(_changes);
    }

    [Fact]
    public async Task Requests_during_a_check_are_folded_into_one_more_check_after_it()
    {
        var project = await OpenWith(Asset("a.wav"));
        var path = In(project, "a.wav").FilePath;
        _files.Hold();
        var first = _service.RecheckMediaAsync();
        await Eventually(() => _files.CallsFor(path) == 1);

        var second = _service.RecheckMediaAsync();
        var third = _service.RecheckMediaAsync();
        _files.Present.TryRemove(path, out _);                                 // only a check started after the request sees it
        _files.Release();
        await Task.WhenAll(first, second, third).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(2, _files.CallsFor(path));                                // the held one + one folded follow-up
        Assert.True(In(project, "a.wav").IsMissing);
        Assert.Single(_changes);
    }

    [Fact]
    public async Task A_new_check_starts_after_the_previous_one_has_ended()
    {
        var project = await OpenWith(Asset("a.wav"));
        var path = In(project, "a.wav").FilePath;

        await _service.RecheckMediaAsync();
        await _service.RecheckMediaAsync();

        Assert.Equal(2, _files.CallsFor(path));
    }

    [Fact]
    public async Task A_file_system_error_counts_as_missing_and_never_throws()
    {
        var project = await OpenWith(Asset("a.wav"));
        _files.Throw = new UnauthorizedAccessException("denied");

        await _service.RecheckMediaAsync();

        Assert.True(In(project, "a.wav").IsMissing);
    }

    [Fact]
    public async Task Open_uses_the_same_file_check()
    {
        var project = await OpenWith(Asset("a.wav"), Asset("b.wav", present: false));

        Assert.False(In(project, "a.wav").IsMissing);
        Assert.True(In(project, "b.wav").IsMissing);
    }

    [Fact]
    public async Task A_project_without_media_checks_nothing()
    {
        await _service.RecheckMediaAsync();

        Assert.Empty(_files.Calls);
        Assert.Empty(_changes);
    }

    private static async Task Eventually(Func<bool> condition)
    {
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > until) throw new TimeoutException();
            await Task.Delay(5);
        }
    }
}
