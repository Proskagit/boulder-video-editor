using System.Collections.Immutable;
using System.Globalization;
using AiVideoEditor.Core.Audio;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.UI.Localization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiVideoEditor.UI.Services;

/// <summary>The state of one audio job (a file's clean-up, or the speech of a sound), for the timeline, the Inspector, the
/// track's ducking and the export.</summary>
public enum AudioJobState
{
    /// <summary>Nothing is needed.</summary>
    None,

    /// <summary>Needed, not started yet (another job runs, or the cleaned sound it reads is not ready).</summary>
    Waiting,

    /// <summary>Running (<see cref="AudioJobStatus.Progress"/>).</summary>
    Processing,

    Ready,

    /// <summary>Stopped by the user; <see cref="AudioProcessingCoordinator.Retry"/> starts it again.</summary>
    Cancelled,

    /// <summary>Failed or unavailable here (no model, a decode error) — <see cref="AudioJobStatus.Message"/>; never silent.</summary>
    Failed
}

public readonly record struct AudioJobStatus(AudioJobState State, double Progress = 0, string? Message = null)
{
    public bool IsBusy => State is AudioJobState.Waiting or AudioJobState.Processing;
}

/// <summary>
/// D064 §4–§5: the cleaned sounds and the speech analyses the project needs, computed in the background one job at a time —
/// first every clean-up a clip uses (its whole file once per setting, shared by every clip of the file), then the speech of the
/// clips of every track another track ducks under (of their cleaned sound when they have a clean-up). Results are kept in memory
/// by the file (path, size, last write time) and between sessions in <see cref="IAudioAnalysisCache"/> by the file's content,
/// the setting and <see cref="IAudioCleanupService.Version"/>; they are derived from the files alone, never part of the project
/// (no Undo step). Progress, the user's cancel and retry, failures with a message — never silent. A project change cancels the
/// running job. Runs on the caller's (UI) context; the processing itself on the thread pool.
/// </summary>
public sealed class AudioProcessingCoordinator : IDisposable
{
    private readonly IProjectService _projects;
    private readonly IAudioCleanupService? _service;
    private readonly IAudioAnalysisCache? _cache;
    private readonly ILogger _logger;
    private readonly Dictionary<Job, JobState> _jobs = new();
    private Dictionary<(Guid Asset, AudioCleanup Cleanup), Job> _cleanups = new();
    private Dictionary<(Guid Asset, AudioCleanup Cleanup), Job> _speech = new();
    private Dictionary<Guid, List<(Guid Asset, AudioCleanup Cleanup)>> _duckingNeeds = new();   // ducked track → its speech sounds
    private (Job Job, CancellationTokenSource Cancel)? _running;
    private CancellationTokenSource _projectCancel = new();
    private Task? _worker;
    private CancellationTokenSource? _workerProject;
    private bool _originalSound;

    /// <summary>How long a job waits before it starts (the last of a slider's values wins); tests set 0.</summary>
    public TimeSpan StartDelay { get; set; } = TimeSpan.FromMilliseconds(600);
    private bool _disposed;

    public AudioProcessingCoordinator(IProjectService projects, IAudioCleanupService? service = null, IAudioAnalysisCache? cache = null,
        ILogger<AudioProcessingCoordinator>? logger = null)
    {
        _projects = projects;
        _service = service;
        _cache = cache;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
        _projects.ProjectChanged += (_, _) =>
        {
            _projectCancel.Cancel();
            _projectCancel = new CancellationTokenSource();
            foreach (var state in _jobs.Values) state.Cancelled = false;
            Refresh();
        };
        _projects.TimelineChanged += (_, _) => Refresh();
        _projects.MediaAssetsChanged += (_, _) => Refresh();
        Refresh();
    }

    /// <summary>Raised on the caller's context when a cleaned sound or a speech analysis appeared or went away (the Preview
    /// rebuilds its snapshot).</summary>
    public event EventHandler? AnalysesChanged;

    /// <summary>Raised on the caller's context whenever a state or a progress may have changed.</summary>
    public event EventHandler? StatusChanged;

    /// <summary>What the snapshot builds with: the cleaned sounds and the speech done so far.</summary>
    public AudioAnalyses Analyses { get; private set; } = AudioAnalyses.None;

    /// <summary>D064 §5 «Исходный звук»: the Preview plays every clip's original sound (never saved, never exported).</summary>
    public bool OriginalSound
    {
        get => _originalSound;
        set
        {
            if (_originalSound == value) return;
            _originalSound = value;
            Analyses = Analyses with { OriginalSound = value };
            AnalysesChanged?.Invoke(this, EventArgs.Empty);
            StatusChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private enum JobKind { Clean, Speech }

    /// <summary>One job: a file (path, size, write time, start, duration), the clean-up, the kind.</summary>
    private sealed record Job(string FilePath, long Length, long WriteTicks, MediaTime Start, MediaTime Duration, AudioCleanup Cleanup, JobKind Kind)
    {
        public bool NeedsCleaned => Kind == JobKind.Speech && !Cleanup.IsNone;
        public Job CleanJob => this with { Kind = JobKind.Clean };
    }

    private sealed class JobState
    {
        public string? CleanedPath { get; set; }
        public SpeechActivity? Speech { get; set; }
        public string? Failure { get; set; }
        public bool Cancelled { get; set; }
        public double Progress { get; set; }
        public bool IsDone => CleanedPath is not null || Speech is not null;
    }

    private JobState StateOf(Job job)
    {
        if (!_jobs.TryGetValue(job, out var state)) _jobs[job] = state = new JobState();
        return state;
    }

    /// <summary>The state of the clean-up <paramref name="cleanup"/> of <paramref name="assetId"/>'s file.</summary>
    public AudioJobStatus CleanupStatus(Guid assetId, AudioCleanup? cleanup) =>
        AudioCleanup.Normalize(cleanup) is { } on && _cleanups.TryGetValue((assetId, on), out var job) ? StatusOf(job) : new AudioJobStatus(AudioJobState.None);

    /// <summary>The state of the speech a ducked track listens to: Failed or Cancelled when any part is, Processing / Waiting
    /// while any part runs or waits (its progress the share done), Ready when all are.</summary>
    public AudioJobStatus DuckingStatus(Guid duckedTrackId)
    {
        if (!_duckingNeeds.TryGetValue(duckedTrackId, out var needs) || needs.Count == 0) return new AudioJobStatus(AudioJobState.None);
        var statuses = needs.Select(n => _speech.TryGetValue(n, out var job) ? StatusOf(job) : new AudioJobStatus(AudioJobState.None)).ToList();
        if (statuses.FirstOrDefault(s => s.State == AudioJobState.Failed) is { State: AudioJobState.Failed } failed) return failed;
        var progress = statuses.Average(s => s.State == AudioJobState.Ready ? 1 : s.Progress);
        if (statuses.Any(s => s.State == AudioJobState.Cancelled)) return new AudioJobStatus(AudioJobState.Cancelled, progress);
        if (statuses.Any(s => s.State == AudioJobState.Processing)) return new AudioJobStatus(AudioJobState.Processing, progress);
        if (statuses.Any(s => s.State == AudioJobState.Waiting)) return new AudioJobStatus(AudioJobState.Waiting, progress);
        return new AudioJobStatus(AudioJobState.Ready, 1);
    }

    /// <summary>D078 amendment 1: the sound processing running now for the toolbar's badge — the running job's progress and the
    /// jobs not done yet; null while none runs. Changes are raised by <see cref="StatusChanged"/>.</summary>
    public BackgroundActivity? Activity => ActivityQueue is { Count: > 0 } q ? q[0] : null;

    /// <summary>D078 amendment 4: the running job (its progress) and the jobs still waiting, with their files' names.</summary>
    public IReadOnlyList<BackgroundActivity> ActivityQueue
    {
        get
        {
            var queue = new List<BackgroundActivity>();
            if (_running is { } running)
                queue.Add(new BackgroundActivity(Strings.Activity_Audio, StateOf(running.Job).Progress, 1, Path.GetFileName(running.Job.FilePath)));
            foreach (var (job, state) in _jobs)
                if ((_running is not { } r || r.Job != job) && !state.IsDone && state.Failure is null && !state.Cancelled)
                    queue.Add(new BackgroundActivity(Strings.Activity_Audio, 0, 1, Path.GetFileName(job.FilePath), Running: false));
            return queue;
        }
    }

    private AudioJobStatus StatusOf(Job job)
    {
        var state = StateOf(job);
        if (state.IsDone) return new AudioJobStatus(AudioJobState.Ready, 1);
        if (state.Failure is not null) return new AudioJobStatus(AudioJobState.Failed, 0, state.Failure);
        if (job.NeedsCleaned && StateOf(job.CleanJob).Failure is { } cleanFailure) return new AudioJobStatus(AudioJobState.Failed, 0, cleanFailure);
        if (state.Cancelled || (job.NeedsCleaned && StateOf(job.CleanJob).Cancelled)) return new AudioJobStatus(AudioJobState.Cancelled, state.Progress);
        return _running is { } running && running.Job == job
            ? new AudioJobStatus(AudioJobState.Processing, state.Progress)
            : new AudioJobStatus(AudioJobState.Waiting, state.Progress);
    }

    /// <summary>Stops the clean-up of <paramref name="assetId"/> with <paramref name="cleanup"/> (and the speech analyses
    /// waiting for it); stays stopped until <see cref="Retry"/>.</summary>
    public void Cancel(Guid assetId, AudioCleanup? cleanup)
    {
        if (AudioCleanup.Normalize(cleanup) is not { } on || !_cleanups.TryGetValue((assetId, on), out var job)) return;
        StateOf(job).Cancelled = true;
        if (_running is { } running && (running.Job == job || running.Job.CleanJob == job)) running.Cancel.Cancel();
        Publish();
    }

    /// <summary>Stops the speech analyses of a ducked track.</summary>
    public void CancelDucking(Guid duckedTrackId)
    {
        if (!_duckingNeeds.TryGetValue(duckedTrackId, out var needs)) return;
        foreach (var need in needs)
            if (_speech.TryGetValue(need, out var job))
            {
                StateOf(job).Cancelled = true;
                if (_running is { } running && running.Job == job) running.Cancel.Cancel();
            }
        Publish();
    }

    /// <summary>Starts every stopped or failed job again (a model installed meanwhile, a file that was busy).</summary>
    public void Retry()
    {
        foreach (var state in _jobs.Values.Where(s => !s.IsDone))
        {
            state.Cancelled = false;
            state.Failure = null;
        }
        Publish();
        StartWorker();
    }

    public ExportAudioAnalyses ForExport()
    {
        var pending = new HashSet<(Guid, AudioCleanup)>();
        var failed = new Dictionary<(Guid, AudioCleanup), string>();
        foreach (var (key, job) in _cleanups)
        {
            var status = StatusOf(job);
            if (status.State == AudioJobState.Failed) failed[key] = status.Message ?? "";
            else if (status.State != AudioJobState.Ready) pending.Add(key);
        }
        var pendingDucking = new HashSet<Guid>();
        var failedDucking = new Dictionary<Guid, string>();
        foreach (var track in _duckingNeeds.Keys)
        {
            var status = DuckingStatus(track);
            if (status.State == AudioJobState.Failed) failedDucking[track] = status.Message ?? "";
            else if (status.State is not (AudioJobState.Ready or AudioJobState.None)) pendingDucking.Add(track);
        }
        return new ExportAudioAnalyses(Analyses with { OriginalSound = false }, pending, failed, pendingDucking, failedDucking);
    }

    private void Refresh()
    {
        if (_disposed) return;
        var project = _projects.Current;
        var assets = project.MediaAssets.ToDictionary(a => a.Id);
        var cleanups = new Dictionary<(Guid, AudioCleanup), Job>();
        var speech = new Dictionary<(Guid, AudioCleanup), Job>();
        var ducking = new Dictionary<Guid, List<(Guid, AudioCleanup)>>();
        var tracks = project.Timeline.VideoTracks.Concat(project.Timeline.AudioTracks).ToList();

        Job? JobOf(MediaBackedClip clip, AudioCleanup cleanup, JobKind kind)
        {
            if (!assets.TryGetValue(clip.MediaAssetId, out var asset) || asset.IsMissing || asset.Metadata is not { AudioCodec: not null } metadata)
                return null;
            var (length, written) = Stat(asset.FilePath);
            return length < 0 ? null : new Job(asset.FilePath, length, written, metadata.StartTime ?? MediaTime.Zero, metadata.Duration, cleanup, kind);
        }

        foreach (var clip in tracks.SelectMany(t => t.Clips).OfType<MediaBackedClip>())
        {
            if (clip is VideoClip { IsAudioDetached: true } || AudioProperties.Of(clip) is not { } sound || AudioCleanup.Normalize(sound.Cleanup) is not { } on)
                continue;
            if (JobOf(clip, on, JobKind.Clean) is { } job) cleanups[(clip.MediaAssetId, on)] = job;
        }
        foreach (var ducked in tracks.Where(t => t.Ducking is not null))
        {
            var needs = new List<(Guid, AudioCleanup)>();
            if (tracks.FirstOrDefault(t => t.Id == ducked.Ducking!.SpeechTrackId) is { } speechTrack)
                foreach (var clip in speechTrack.Clips.OfType<MediaBackedClip>())
                {
                    if (clip is VideoClip { IsAudioDetached: true } || AudioProperties.Of(clip) is not { IsMuted: false } sound) continue;
                    var cleanup = AudioCleanup.Normalize(sound.Cleanup) ?? AudioCleanup.None;
                    if (JobOf(clip, cleanup, JobKind.Speech) is not { } job) continue;
                    speech[(clip.MediaAssetId, cleanup)] = job;
                    if (!cleanup.IsNone) cleanups.TryAdd((clip.MediaAssetId, cleanup), job.CleanJob);
                    if (!needs.Contains((clip.MediaAssetId, cleanup))) needs.Add((clip.MediaAssetId, cleanup));
                }
            ducking[ducked.Id] = needs;
        }
        _cleanups = cleanups;
        _speech = speech;
        _duckingNeeds = ducking;
        if (_running is { } running && !cleanups.ContainsValue(running.Job) && !speech.ContainsValue(running.Job)) running.Cancel.Cancel();
        Publish();
        StartWorker();
    }

    private static (long Length, long WriteTicks) Stat(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? (info.Length, info.LastWriteTimeUtc.Ticks) : (-1, 0);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return (-1, 0);
        }
    }

    private void StartWorker()
    {
        // A worker of a previous project (its token cancelled) is winding down: the new project's jobs start now (D064 §5).
        if (!_disposed && _service is not null && (_worker is null || _worker.IsCompleted || _workerProject?.IsCancellationRequested == true) &&
            NextJob() is not null)
        {
            _workerProject = _projectCancel;
            _worker = RunAsync(_projectCancel);
        }
    }

    /// <summary>The next job: a clean-up a clip uses, then a speech analysis whose cleaned sound (if any) is ready.</summary>
    private Job? NextJob()
    {
        foreach (var job in _cleanups.Values.Distinct())
        {
            var state = StateOf(job);
            if (!state.IsDone && !state.Cancelled && state.Failure is null) return job;
        }
        foreach (var job in _speech.Values.Distinct())
        {
            var state = StateOf(job);
            if (state.IsDone || state.Cancelled || state.Failure is not null) continue;
            if (job.NeedsCleaned && StateOf(job.CleanJob).CleanedPath is null) continue;
            return job;
        }
        return null;
    }

    private async Task RunAsync(CancellationTokenSource project)
    {
        while (!_disposed && !project.IsCancellationRequested && NextJob() is { } job)
        {
            var cancel = CancellationTokenSource.CreateLinkedTokenSource(project.Token);
            _running = (job, cancel);
            var state = StateOf(job);
            state.Progress = 0;
            Publish();
            var progress = new Progress<double>(p =>
            {
                // Throttled: a whole per cent more (the views re-sync on every status change).
                if (p < 1 && Math.Floor(p * 100) <= Math.Floor(state.Progress * 100)) return;
                state.Progress = p;
                StatusChanged?.Invoke(this, EventArgs.Empty);
            });
            try
            {
                // A slider's drag changes the setting many times: wait a moment, and run only what is still wanted then.
                await Task.Delay(StartDelay, cancel.Token);
                if (NextJob() != job) continue;
                if (job.Kind == JobKind.Clean) state.CleanedPath = await CleanAsync(job, progress, cancel.Token);
                else state.Speech = await SpeechAsync(job, progress, cancel.Token);
            }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested)
            {
                // Stopped by the user (the state says so), by a project change or because nobody needs it any more.
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Audio job {Kind} of {File} failed.", job.Kind, Path.GetFileName(job.FilePath));
                state.Failure = ex is AudioCleanupFailure or AudioCleanupException ? ex.Message
                    : string.Format(Core.Localization.Messages.Audio_ProcessingFailed, ex.Message);
            }
            finally
            {
                if (_running is { } current && ReferenceEquals(current.Cancel, cancel)) _running = null;
                cancel.Dispose();
            }
            Publish();
        }
    }

    private string Key(Job job, string fingerprint) => string.Join('|', _service!.Version, job.Kind == JobKind.Clean ? "clean" : "speech",
        fingerprint, job.Start.Ticks.ToString(CultureInfo.InvariantCulture),
        $"nr{job.Cleanup.NoiseReduction}", $"ve{job.Cleanup.VoiceEnhancement}");

    private async Task<string> CleanAsync(Job job, IProgress<double> progress, CancellationToken ct)
    {
        var service = _service!;
        if (await service.CheckAsync(job.Cleanup, ct) is { } unavailable) throw new AudioCleanupFailure(unavailable);
        var fingerprint = _cache is null ? null : await Task.Run(() => _cache.Fingerprint(job.FilePath), ct);
        var key = fingerprint is null ? null : Key(job, fingerprint);
        if (key is not null && _cache!.TryGetCleaned(key) is { } cached) return cached;

        var temp = key is not null ? _cache!.NewCleanedPath(key) : Path.Combine(Path.GetTempPath(), $"aive-clean-{Guid.NewGuid():N}.wav");
        try
        {
            await service.ProduceAsync(job.FilePath, job.Start, job.Duration, job.Cleanup, temp, progress, ct);
            return key is not null ? _cache!.CommitCleaned(key, temp) : temp;
        }
        catch
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            throw;
        }
    }

    private async Task<SpeechActivity> SpeechAsync(Job job, IProgress<double> progress, CancellationToken ct)
    {
        var service = _service!;
        if (await service.CheckSpeechAsync(ct) is { } unavailable) throw new AudioCleanupFailure(unavailable);
        var fingerprint = _cache is null ? null : await Task.Run(() => _cache.Fingerprint(job.FilePath), ct);
        var key = fingerprint is null ? null : Key(job, fingerprint);
        if (key is not null && _cache!.TryLoadSpeech(key) is { } cached) return cached;

        // The speech of the cleaned sound (start time 0, aligned to the source) when the clip has a clean-up.
        var (path, start) = job.NeedsCleaned ? (StateOf(job.CleanJob).CleanedPath!, MediaTime.Zero) : (job.FilePath, job.Start);
        var activity = await service.AnalyseSpeechAsync(path, start, job.Duration, progress, ct);
        if (key is not null) _cache!.SaveSpeech(key, activity);
        return activity;
    }

    private void Publish()
    {
        var cleaned = ImmutableDictionary.CreateBuilder<(Guid, AudioCleanup), string>();
        var lost = false;
        foreach (var (key, job) in _cleanups)
        {
            var state = StateOf(job);
            if (state.CleanedPath is not { } path) continue;
            if (File.Exists(path)) cleaned[key] = path;
            else { state.CleanedPath = null; state.Progress = 0; lost = true; }   // removed from the cache: computed again, never silent
        }
        var speech = ImmutableDictionary.CreateBuilder<(Guid, AudioCleanup), SpeechActivity>();
        foreach (var (key, job) in _speech)
            if (StateOf(job).Speech is { } activity) speech[key] = activity;
        var next = new AudioAnalyses(new CleanedAudio(cleaned.ToImmutable()), new SpeechAnalyses(speech.ToImmutable())) { OriginalSound = _originalSound };
        var changed = !Same(next.Cleaned.Files, Analyses.Cleaned.Files) || !Same(next.Speech.ByAsset, Analyses.Speech.ByAsset);
        Analyses = next;
        if (changed) AnalysesChanged?.Invoke(this, EventArgs.Empty);
        StatusChanged?.Invoke(this, EventArgs.Empty);
        if (lost) StartWorker();
    }

    private static bool Same<TValue>(ImmutableDictionary<(Guid, AudioCleanup), TValue> a, ImmutableDictionary<(Guid, AudioCleanup), TValue> b) =>
        a.Count == b.Count && a.All(p => b.TryGetValue(p.Key, out var v) && Equals(v, p.Value));

    /// <summary>Tests: until no job runs.</summary>
    internal async Task IdleAsync()
    {
        while (_worker is { IsCompleted: false } worker) await worker;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _projectCancel.Cancel();
    }

    /// <summary>A job that can't run here, with the user-facing reason.</summary>
    private sealed class AudioCleanupFailure(string message) : Exception(message);
}
