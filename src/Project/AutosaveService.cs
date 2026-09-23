using System.ComponentModel;
using System.Diagnostics;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Project.Persistence;
using Microsoft.Extensions.Logging;

namespace AiVideoEditor.Project;

/// <summary>
/// Autosave into recovery files (see <see cref="IAutosaveService"/>). Never touches
/// project.json. Every <see cref="DefaultInterval"/> the current project, if it has unsaved
/// changes, is serialized on the UI thread (so the file reflects exactly the state at that
/// moment) and written atomically in the background by <see cref="RecoveryStore"/>.
/// </summary>
/// <remarks>
/// Save and autosave write different files and can't damage each other. The only interplay is
/// that a successful Save makes the recovery file obsolete: it is deleted on
/// <see cref="IProjectService.ProjectSaved"/>, and <see cref="RecoveryStore"/>'s generation check
/// stops an autosave that was snapshotted before that delete from writing it back.
/// </remarks>
public sealed class AutosaveService : IAutosaveService, IDisposable
{
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromMinutes(2);

    private static readonly int CurrentProcessId = Environment.ProcessId;
    private static readonly DateTimeOffset? CurrentProcessStart = TryGetCurrentProcessStart();

    private readonly IProjectService _projects;
    private readonly RecoveryStore _store;
    private readonly ILogger<AutosaveService> _logger;
    private readonly TimeSpan _interval;

    // Projects this session has written a recovery file for (only those are removed when the
    // project is found clean at an autosave; a file left by a crashed session stays until the
    // user decides about it or the project is saved).
    private readonly HashSet<Guid> _ownRecoveries = new();

    private Timer? _timer;
    private SynchronizationContext? _context;
    private Task _runningTick = Task.CompletedTask;
    private int _tickActive;

    public event EventHandler<string>? AutosaveCompleted;

    public AutosaveService(IProjectService projects, RecoveryStore store, ILogger<AutosaveService> logger)
        : this(projects, store, logger, DefaultInterval)
    {
    }

    internal AutosaveService(IProjectService projects, RecoveryStore store, ILogger<AutosaveService> logger, TimeSpan interval)
    {
        _projects = projects;
        _store = store;
        _logger = logger;
        _interval = interval;
        _projects.ProjectSaved += OnProjectSaved;
    }

    public void Start()
    {
        if (_timer is not null) return;
        _context = SynchronizationContext.Current;
        _timer = new Timer(_ => OnTimer(), null, _interval, _interval);
        _logger.LogInformation("Autosave started (every {Interval}, recovery folder {Folder}).", _interval, _store.RootFolder);
    }

    public void Stop()
    {
        _timer?.Dispose();
        _timer = null;
    }

    private void OnTimer()
    {
        if (_context is { } context)
            context.Post(_ => RunTick(), null);
        else
            RunTick();
    }

    private void RunTick()
    {
        if (Interlocked.Exchange(ref _tickActive, 1) == 1) return; // previous autosave still writing
        _runningTick = TickAsync();
    }

    private async Task TickAsync()
    {
        try
        {
            await AutosaveNowAsync();
        }
        catch (Exception ex)
        {
            // An autosave failure must never disturb editing; the next tick tries again.
            _logger.LogWarning(ex, "Autosave failed.");
        }
        finally
        {
            Volatile.Write(ref _tickActive, 0);
        }
    }

    public async Task<bool> AutosaveNowAsync(CancellationToken ct = default)
    {
        var project = _projects.Current;
        if (!project.IsDirty)
        {
            if (RemoveOwn(project.Id))
                await _store.DeleteAsync(project.Id);
            return false;
        }

        var generation = _store.GenerationOf(project.Id);
        var info = new RecoveryInfo(project.ProjectFolderPath, DateTimeOffset.UtcNow, CurrentProcessId, CurrentProcessStart);
        var json = ProjectSerializer.SerializeRecovery(project, info);

        if (!await _store.WriteAsync(project.Id, generation, json, ct))
        {
            _logger.LogDebug("Autosave of '{Name}' skipped: the project was saved meanwhile.", project.Name);
            return false;
        }

        lock (_ownRecoveries) _ownRecoveries.Add(project.Id);
        var path = _store.PathFor(project.Id);
        _logger.LogDebug("Autosaved '{Name}' to {Path}.", project.Name, path);
        AutosaveCompleted?.Invoke(this, path);
        return true;
    }

    private void OnProjectSaved(object? sender, EventArgs e)
    {
        var project = _projects.Current;
        if (project.IsDirty) return; // edited while saving: the recovery file is still useful
        RemoveOwn(project.Id);
        _ = DeleteQuietlyAsync(project.Id);
    }

    private async Task DeleteQuietlyAsync(Guid projectId)
    {
        try
        {
            if (await _store.DeleteAsync(projectId))
                _logger.LogDebug("Removed obsolete recovery file for project {Id}.", projectId);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Couldn't remove the recovery file for project {Id}.", projectId);
        }
    }

    public async Task<RecoveryScanResult> FindRecoveryAsync(CancellationToken ct = default)
    {
        var candidates = new List<RecoveryCandidate>();
        var damaged = 0;

        foreach (var file in _store.ListFiles())
        {
            ct.ThrowIfCancellationRequested();

            string json;
            try
            {
                json = await _store.ReadAsync(file, ct);
            }
            catch (ProjectFileException ex)
            {
                _logger.LogWarning(ex, "Couldn't read recovery file {File}; skipping it.", file);
                continue;
            }

            Core.Entities.Project project;
            RecoveryInfo info;
            try
            {
                (project, info) = await Task.Run(() => ProjectSerializer.DeserializeRecovery(json), ct);
            }
            catch (ProjectFileException ex)
            {
                damaged++;
                await SetAsideQuietlyAsync(file, ex);
                continue;
            }

            if (BelongsToRunningProcess(info))
                continue;

            if (IsOlderThanSavedProject(info))
            {
                _logger.LogInformation("Recovery file {File} is older than the saved project; removing it.", file);
                await _store.DeleteFileAsync(file, project.Id);
                continue;
            }

            candidates.Add(new RecoveryCandidate(file, project.Id, project.Name, info.ProjectFolderPath, info.AutosavedAt));
        }

        if (candidates.Count == 0 && damaged == 0) return RecoveryScanResult.None;
        var newest = candidates.OrderByDescending(c => c.AutosavedAt).FirstOrDefault();
        return new RecoveryScanResult(newest, candidates.Count, damaged);
    }

    private async Task SetAsideQuietlyAsync(string file, ProjectFileException reason)
    {
        try
        {
            var target = await _store.SetAsideAsync(file);
            _logger.LogWarning(reason, "Recovery file {File} can't be used; kept as {Target}.", file, target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Recovery file {File} can't be used and couldn't be set aside.", file);
        }
    }

    public async Task DiscardRecoveryAsync(RecoveryCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        RemoveOwn(candidate.ProjectId);
        await _store.DeleteFileAsync(candidate.FilePath, candidate.ProjectId);
        _logger.LogInformation("Discarded recovery file {File}.", candidate.FilePath);
    }

    public async Task DiscardRecoveryAsync(Guid projectId)
    {
        RemoveOwn(projectId);
        if (await _store.DeleteAsync(projectId))
            _logger.LogInformation("Discarded the recovery file of project {Id} (changes not saved on purpose).", projectId);
    }

    public async Task ShutdownAsync(bool keepUnsavedChanges = true)
    {
        Stop();
        try
        {
            await _runningTick;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Autosave failed during shutdown.");
        }

        if (!keepUnsavedChanges)
        {
            await DiscardRecoveryAsync(_projects.Current.Id);
            return;
        }

        try
        {
            // Dirty: keep the latest state recoverable. Clean: remove this session's file.
            if (await AutosaveNowAsync())
                _logger.LogInformation("Unsaved changes at shutdown were kept in a recovery file.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Final autosave at shutdown failed.");
        }
    }

    private bool RemoveOwn(Guid projectId)
    {
        lock (_ownRecoveries) return _ownRecoveries.Remove(projectId);
    }

    /// <summary>The file was written by a process that is still running (another instance, or
    /// this one): it is that instance's live autosave, not a leftover.</summary>
    private static bool BelongsToRunningProcess(RecoveryInfo info)
    {
        if (info.ProcessId <= 0 || info.ProcessStartTime is not { } started) return false;
        try
        {
            using var process = Process.GetProcessById(info.ProcessId);
            return Math.Abs((process.StartTime.ToUniversalTime() - started.UtcDateTime).TotalSeconds) < 2;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception or NotSupportedException)
        {
            return false; // not running (or not inspectable) → a leftover
        }
    }

    private static bool IsOlderThanSavedProject(RecoveryInfo info)
    {
        if (info.ProjectFolderPath is null) return false;
        var projectFile = ProjectFileStore.ProjectFilePath(info.ProjectFolderPath);
        return File.Exists(projectFile) && File.GetLastWriteTimeUtc(projectFile) >= info.AutosavedAt.UtcDateTime;
    }

    private static DateTimeOffset? TryGetCurrentProcessStart()
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            return new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            return null;
        }
    }

    public void Dispose()
    {
        Stop();
        _projects.ProjectSaved -= OnProjectSaved;
    }
}
