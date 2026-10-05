using AiVideoEditor.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace AiVideoEditor.UI.Services;

/// <summary>
/// Re-checks the current project's media files when the main window becomes active (D026 §2, PO-5) — the moment a
/// user is back from Explorer, where files are moved, renamed or restored. No <c>FileSystemWatcher</c>.
/// <list type="bullet">
/// <item>Throttled: at most one check per <see cref="Interval"/>. An activation inside the interval is not lost: one
/// trailing check runs when the interval has passed (so a file restored right after a check is still seen without a
/// further window switch).</item>
/// <item>The check itself is <see cref="IProjectService.RecheckMediaAsync"/> (off the UI thread, overlapping requests
/// folded, results for a replaced project dropped). The operations that need the files (the export, the relink)
/// check on their own, independently of this throttle.</item>
/// <item>Every change found — by any check — is reported in the status bar.</item>
/// </list>
/// Members are called on the UI thread.
/// </summary>
public sealed class MediaAvailabilityMonitor
{
    /// <summary>Activations closer together than this share one check. Short enough that a file restored in Explorer is
    /// seen as soon as the user is back (a trailing check covers the rest of the interval), long enough that switching
    /// windows back and forth doesn't stat every media file each time — on a slow share a check can take seconds.</summary>
    internal static readonly TimeSpan Interval = TimeSpan.FromSeconds(3);

    private readonly IProjectService _projects;
    private readonly StatusService _status;
    private readonly ILogger<MediaAvailabilityMonitor> _logger;
    private readonly Func<DateTimeOffset> _now;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly CancellationTokenSource _stop = new();
    private DateTimeOffset? _lastCheck;
    private bool _trailingCheckScheduled;

    public MediaAvailabilityMonitor(IProjectService projects, StatusService status, ILogger<MediaAvailabilityMonitor> logger)
        : this(projects, status, logger, () => DateTimeOffset.UtcNow, (wait, ct) => Task.Delay(wait, ct))
    {
    }

    /// <param name="now">The clock of the throttle.</param>
    /// <param name="delay">Waits until the trailing check (tests complete it by hand).</param>
    internal MediaAvailabilityMonitor(IProjectService projects, StatusService status, ILogger<MediaAvailabilityMonitor> logger,
        Func<DateTimeOffset> now, Func<TimeSpan, CancellationToken, Task> delay)
    {
        _projects = projects;
        _status = status;
        _logger = logger;
        _now = now;
        _delay = delay;
        _projects.MediaAvailabilityChanged += (_, e) => Report(e);
    }

    /// <summary>The last check started by an activation (tests).</summary>
    internal Task LastCheck { get; private set; } = Task.CompletedTask;

    /// <summary>The main window became active: checks now, or once at the end of the interval if one ran recently.</summary>
    public void OnWindowActivated()
    {
        if (_stop.IsCancellationRequested) return;

        var now = _now();
        if (_lastCheck is { } last && now - last < Interval)
        {
            if (_trailingCheckScheduled) return;
            _trailingCheckScheduled = true;
            LastCheck = CheckLaterAsync(last + Interval - now);
            return;
        }

        LastCheck = CheckAsync();
    }

    /// <summary>The window is closing: no further checks start (a running one ends on its own).</summary>
    public void Stop() => _stop.Cancel();

    private async Task CheckLaterAsync(TimeSpan wait)
    {
        try
        {
            await _delay(wait, _stop.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        finally
        {
            _trailingCheckScheduled = false;
        }

        if (!_stop.IsCancellationRequested)
            await CheckAsync();
    }

    private async Task CheckAsync()
    {
        _lastCheck = _now();
        try
        {
            await _projects.RecheckMediaAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Re-checking the media files failed.");
        }
    }

    private void Report(MediaAvailabilityChangedEventArgs e)
    {
        var parts = new List<string>();
        if (e.Gone.Count > 0)
            parts.Add(e.Gone.Count == 1
                ? $"\"{e.Gone[0].FileName}\" is missing now and is shown as offline."
                : $"{e.Gone.Count} media files are missing now and are shown as offline.");
        if (e.Returned.Count > 0)
            parts.Add(e.Returned.Count == 1
                ? $"\"{e.Returned[0].FileName}\" is available again."
                : $"{e.Returned.Count} media files are available again.");
        if (parts.Count > 0)
            _status.Report(string.Join(" ", parts));
    }
}
