using System.Collections.Concurrent;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Project;
using AiVideoEditor.Project.Persistence;
using AiVideoEditor.UI.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiVideoEditor.UI.Tests;

/// <summary>
/// D026 §2 (Step 11.3): the re-check on window activation — throttled to one check per
/// <see cref="MediaAvailabilityMonitor.Interval"/>, an activation inside the interval answered by one trailing check,
/// nothing after the window started closing, the changes reported in the status bar. A manual clock and delay; a real
/// project service over a fake file system that counts the checks.
/// </summary>
public sealed class MediaAvailabilityMonitorTests
{
    private static readonly TimeSpan Interval = MediaAvailabilityMonitor.Interval;

    private readonly ConcurrentDictionary<string, byte> _present = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentQueue<string> _checks = new();
    private readonly ProjectService _projects;
    private readonly StatusService _status = new();
    private readonly MediaAvailabilityMonitor _monitor;
    private readonly List<(TimeSpan Wait, TaskCompletionSource Done)> _delays = new();
    private DateTimeOffset _now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
    private readonly MediaAsset _asset;

    public MediaAvailabilityMonitorTests()
    {
        _projects = new ProjectService(new UndoRedoService(), NullLogger<ProjectService>.Instance, new ProjectFileStore(), path =>
        {
            _checks.Enqueue(path);
            return _present.ContainsKey(path);
        });
        _monitor = new MediaAvailabilityMonitor(_projects, _status, NullLogger<MediaAvailabilityMonitor>.Instance,
            () => _now, Delay);
        _asset = new MediaAsset { FilePath = Path.Combine(Path.GetTempPath(), "aive-monitor", "a.wav"), Kind = MediaKind.Audio };
        _present[_asset.FilePath] = 0;
        _projects.AddMediaAssets(new[] { _asset });
    }

    private Task Delay(TimeSpan wait, CancellationToken ct)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ct.Register(() => done.TrySetCanceled(ct));
        lock (_delays) _delays.Add((wait, done));
        return done.Task;
    }

    /// <summary>An activation expected to check at once; waits for that check.</summary>
    private async Task Activate()
    {
        _monitor.OnWindowActivated();
        await _monitor.LastCheck.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task The_first_activation_checks_at_once()
    {
        await Activate();

        Assert.Single(_checks);
        Assert.Empty(_delays);
    }

    [Fact]
    public async Task Activations_inside_the_interval_share_one_trailing_check()
    {
        await Activate();                                                     // check 1 at t0
        _now += TimeSpan.FromSeconds(1);
        _monitor.OnWindowActivated();                                         // inside: a trailing check is scheduled
        _now += TimeSpan.FromSeconds(1);
        _monitor.OnWindowActivated();                                         // inside again: nothing more

        Assert.Single(_checks);
        var (wait, done) = Assert.Single(_delays);
        Assert.Equal(Interval - TimeSpan.FromSeconds(1), wait);               // until the interval has passed

        _present.Clear();                                                     // the file goes meanwhile
        _now += wait;
        done.SetResult();
        await _monitor.LastCheck.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(2, _checks.Count);                                       // the trailing check saw it
        Assert.True(_asset.IsMissing);
    }

    [Fact]
    public async Task An_activation_after_the_interval_checks_at_once()
    {
        await Activate();
        _now += Interval;
        await Activate();

        Assert.Equal(2, _checks.Count);
        Assert.Empty(_delays);
    }

    [Fact]
    public async Task Nothing_is_checked_after_the_window_started_closing()
    {
        await Activate();
        _now += TimeSpan.FromSeconds(1);
        _monitor.OnWindowActivated();                                         // a trailing check waits …
        _monitor.Stop();                                                      // … and is cancelled
        await _monitor.LastCheck.WaitAsync(TimeSpan.FromSeconds(5));

        _now += Interval;
        _monitor.OnWindowActivated();

        Assert.Single(_checks);
    }

    [Fact]
    public async Task Changes_found_by_a_check_are_reported_in_the_status_bar()
    {
        _present.Clear();
        await Activate();
        Assert.Equal("\"a.wav\" is missing now and is shown as offline.", _status.Message);

        _present[_asset.FilePath] = 0;
        _now += Interval;
        await Activate();
        Assert.Equal("\"a.wav\" is available again.", _status.Message);
    }

    [Fact]
    public async Task A_check_that_finds_nothing_new_leaves_the_status_bar_alone()
    {
        _status.Report("Something else.");

        await Activate();

        Assert.Equal("Something else.", _status.Message);
    }
}
