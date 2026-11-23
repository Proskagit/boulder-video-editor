using System.Collections.Immutable;
using System.Globalization;
using AiVideoEditor.Core.Color;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Core.Localization;
using AiVideoEditor.Core.People;
using AiVideoEditor.Core.Playback;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiVideoEditor.UI.Services;

/// <summary>Which people analysis of a video (D061 §5).</summary>
public enum PeopleAnalysisKind
{
    /// <summary>The people and their tracks (Follow; the prompts of the masks).</summary>
    Tracks,

    /// <summary>Every person's mask (Occlusion).</summary>
    Masks
}

/// <summary>The state of one people analysis of a video, for the Inspector, the Preview and the export.</summary>
public enum PeopleAnalysisState
{
    /// <summary>Not wanted: no clip of the video uses Follow / Occlusion, or the media is offline.</summary>
    None,

    /// <summary>Wanted and not started yet (another analysis runs, or the tracks it needs are not ready).</summary>
    Waiting,

    /// <summary>Running (<see cref="PeopleAnalysisStatus.Progress"/>).</summary>
    Analysing,

    /// <summary>Done (from the cache or analysed).</summary>
    Ready,

    /// <summary>Stopped by the user; <see cref="PeopleAnalysisCoordinator.Retry"/> starts it again.</summary>
    Cancelled,

    /// <summary>Failed, or unavailable on this computer (<see cref="PeopleAnalysisStatus.Message"/>): no model, no GPU for the
    /// masks, a decoding error. Never silent: the Inspector shows it and the export warns.</summary>
    Failed
}

/// <summary>One analysis's state, its progress (0..1 while analysing) and, when failed, the message.</summary>
public readonly record struct PeopleAnalysisStatus(PeopleAnalysisState State, double Progress = 0, string? Message = null)
{
    public bool IsBusy => State is PeopleAnalysisState.Waiting or PeopleAnalysisState.Analysing;
}

/// <summary>
/// The people analyses of the project's videos (D061 §5, amendment 1). Only what the clips need is analysed, in chunks of
/// <see cref="PeopleAnalyzer.ChunkLength"/> of source time (<see cref="PeopleAnalysisNeeds"/>): the tracks around each
/// followed part, the masks where hold outlines are shown on a clip that hides them behind people — never a whole long file
/// for a short part. The order: the followed parts' tracks first (from their start, so Follow shows its result while the
/// video plays — each chunk is published when it is done), then the other tracks, then the masks; a masks run gives way as
/// soon as a followed part needs tracks. Every chunk is analysed once per file and shared by every clip of the file and by
/// Follow and Occlusion alike (Trim, Split, Slip, Speed and a longer part reuse the done chunks); the chunks are kept in
/// memory by the file (path, size, last write time) and between sessions in <see cref="IPeopleAnalysisCache"/> by the
/// file's content, the model and the analysis version. Like the camera-motion tracks the results are derived from the video
/// alone, never part of the project (no Undo step).
/// <para>Progress, the user's cancel and retry (<see cref="Cancel"/>, <see cref="Retry"/>), failures with a message (no
/// model, no GPU for the masks, a decoder error) — never silent. A project change cancels everything running. Runs on the
/// caller's (UI) context; the analysis itself on the thread pool.</para>
/// </summary>
public sealed class PeopleAnalysisCoordinator : IDisposable
{
    private readonly IProjectService _projects;
    private readonly IVideoDecoder _decoder;
    private readonly IPeopleVision? _vision;
    private readonly IPeopleAnalysisCache? _cache;
    private readonly ILogger _logger;
    private readonly Dictionary<FileKey, FileState> _files = new();
    private Dictionary<Guid, (FileKey Key, bool Masks)> _assets = new();
    private Dictionary<FileKey, Want> _wanted = new();
    private Running? _running;
    private CancellationTokenSource _projectCancel = new();
    private Task? _worker;
    private bool _disposed;

    public PeopleAnalysisCoordinator(IProjectService projects, IVideoDecoder decoder, IPeopleVision? vision = null,
        IPeopleAnalysisCache? cache = null, ILogger<PeopleAnalysisCoordinator>? logger = null)
    {
        _projects = projects;
        _decoder = decoder;
        _vision = vision;
        _cache = cache;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
        _projects.ProjectChanged += (_, _) =>
        {
            _projectCancel.Cancel();
            _projectCancel = new CancellationTokenSource();
            foreach (var state in _files.Values) state.TracksCancelled = state.MasksCancelled = false;
            Refresh();
        };
        _projects.TimelineChanged += (_, _) => Refresh();
        _projects.MediaAssetsChanged += (_, _) => Refresh();
        Refresh();
    }

    /// <summary>Raised on the caller's context when an analysis result appeared, grew or went away.</summary>
    public event EventHandler? AnalysesChanged;

    /// <summary>Raised on the caller's context whenever a state or a progress may have changed.</summary>
    public event EventHandler? StatusChanged;

    /// <summary>D078 §3: the source time spans the attempt's events need, per video asset (set by the attempt events'
    /// service); analysed as tracks after everything Follow and Occlusion need, on a GPU only.</summary>
    public Func<Project, IEnumerable<(Guid AssetId, MediaTime From, MediaTime To)>>? EventSpans { get; set; }

    /// <summary>D078 §3: the detector runs on the processor on this computer — the attempt's events are not searched.</summary>
    public bool EventsNeedGpu { get; private set; }

    /// <summary>D078 §3: an events' run failed (a model, a decoding error) — no more events' runs this session; the video's own
    /// analysis is not affected.</summary>
    public bool EventsStopped { get; private set; }

    /// <summary>Re-reads what the clips and <see cref="EventSpans"/> need (a setting changed).</summary>
    public void RefreshNeeds() => Refresh();

    /// <summary>Whether every tracks chunk of <c>[from, to]</c> of <paramref name="assetId"/>'s file is done.</summary>
    public bool HasTracks(Guid assetId, MediaTime from, MediaTime to)
    {
        if (!_assets.TryGetValue(assetId, out var asset)) return false;
        var state = StateOf(asset.Key);
        return PeopleAnalysisNeeds.Chunks(new[] { (from, to) }, asset.Key.Duration).All(state.Tracks.ContainsKey);
    }

    /// <summary>The analyses of the project's videos done so far (what the snapshot draws with) — partial while a part is
    /// still analysed: Follow follows on the chunks that are done.</summary>
    public PeopleAnalyses Analyses { get; private set; } = PeopleAnalyses.None;

    /// <summary>A video file: its path, size, last write time, start, HDR rule, nominal rate and duration.</summary>
    private sealed record FileKey(string FilePath, long FileLength, long FileWriteTicks, MediaTime FileStart, HdrConversion? Hdr,
        FrameRate? NominalRate, MediaTime Duration)
    {
        public PeopleAnalyzer.Source Source => new(FilePath, FileStart, Hdr, NominalRate, Duration);

        /// <summary>The key of one chunk between sessions: the file's content instead of its path, the model, the analysis.</summary>
        public string CacheKey(string fingerprint, string modelId, PeopleAnalysisKind kind, int chunk) => string.Join('|',
            kind == PeopleAnalysisKind.Tracks ? $"tracks-v{PersonTracks.AnalysisVersion}" : $"masks-v{PersonMaskTrack.AnalysisVersion}-t{PersonTracks.AnalysisVersion}",
            modelId, fingerprint, FileStart.Ticks.ToString(CultureInfo.InvariantCulture), Hdr?.ToString() ?? "sdr",
            PeopleAnalyzer.AnalysisLongSide.ToString(CultureInfo.InvariantCulture),
            (kind == PeopleAnalysisKind.Tracks ? PeopleAnalyzer.DetectionInterval : PeopleAnalyzer.MaskInterval).Ticks.ToString(CultureInfo.InvariantCulture),
            PeopleAnalyzer.MaskLongSide.ToString(CultureInfo.InvariantCulture),
            $"chunk{PeopleAnalyzer.ChunkLength.Ticks}", chunk.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>The chunks the clips need of one file: the followed parts' tracks (first), every track, the masks.</summary>
    private sealed class Want
    {
        public SortedSet<int> FollowTracks { get; } = new();
        public SortedSet<int> Tracks { get; } = new();
        public SortedSet<int> Masks { get; } = new();

        /// <summary>D078 §3: the attempt's events' windows — tracks, after all the others, on a GPU only.</summary>
        public SortedSet<int> EventTracks { get; } = new();
    }

    /// <summary>What is done for one file (kept across projects): the chunks, their merge, failures, the user's stop.</summary>
    private sealed class FileState
    {
        public Dictionary<int, PersonTracks> Tracks { get; } = new();
        public Dictionary<int, PersonMaskTrack> Masks { get; } = new();
        public PersonTracks? MergedTracks { get; set; }
        public PersonMaskTrack? MergedMasks { get; set; }
        public bool TracksChanged { get; set; }
        public bool MasksChanged { get; set; }
        public string? TracksFailure { get; set; }
        public string? MasksFailure { get; set; }
        public bool TracksCancelled { get; set; }
        public bool MasksCancelled { get; set; }
    }

    /// <summary>The run being analysed: a contiguous series of chunks of one file and kind.</summary>
    private sealed record Running(FileKey File, PeopleAnalysisKind Kind, int[] Chunks, bool ForFollow, bool ForEvents, CancellationTokenSource Cancel)
    {
        public HashSet<int> Done { get; } = new();
    }

    private sealed record Work(FileKey File, PeopleAnalysisKind Kind, int[] Chunks, bool ForFollow, bool ForEvents = false);

    /// <summary>The detector is on the processor: the events' run stops (D078 §2 Q2).</summary>
    private sealed class EventsNeedGpuException : Exception;

    private FileState StateOf(FileKey key)
    {
        if (!_files.TryGetValue(key, out var state)) _files[key] = state = new FileState();
        return state;
    }

    /// <summary>The state of <paramref name="kind"/> for <paramref name="assetId"/>; its progress is the share of the chunks
    /// the clips need that are done.</summary>
    public PeopleAnalysisStatus StatusOf(Guid assetId, PeopleAnalysisKind kind)
    {
        if (!_assets.TryGetValue(assetId, out var asset) || (kind == PeopleAnalysisKind.Masks && !asset.Masks))
            return new PeopleAnalysisStatus(PeopleAnalysisState.None);
        var state = StateOf(asset.Key);
        var want = _wanted.GetValueOrDefault(asset.Key) ?? new Want();
        var tracks = kind == PeopleAnalysisKind.Tracks;
        var failure = tracks ? state.TracksFailure : state.MasksFailure ?? state.TracksFailure;
        if (failure is not null) return new PeopleAnalysisStatus(PeopleAnalysisState.Failed, 0, failure);
        var chunks = tracks ? want.Tracks : want.Masks;
        var missing = tracks ? chunks.Count(k => !state.Tracks.ContainsKey(k)) : chunks.Count(k => !state.Masks.ContainsKey(k));
        if (missing == 0) return new PeopleAnalysisStatus(PeopleAnalysisState.Ready, 1);
        var progress = (chunks.Count - missing) / (double)chunks.Count;
        if (state.TracksCancelled || (!tracks && state.MasksCancelled)) return new PeopleAnalysisStatus(PeopleAnalysisState.Cancelled, progress);
        return _running is { } running && running.File == asset.Key && running.Kind == kind
            ? new PeopleAnalysisStatus(PeopleAnalysisState.Analysing, progress)
            : new PeopleAnalysisStatus(PeopleAnalysisState.Waiting, progress);
    }

    /// <summary>Stops the analyses of <paramref name="assetId"/>'s file (running or waiting); they stay stopped until
    /// <see cref="Retry"/>. The chunks already done are kept.</summary>
    public void Cancel(Guid assetId)
    {
        if (!_assets.TryGetValue(assetId, out var asset)) return;
        var state = StateOf(asset.Key);
        state.TracksCancelled = state.MasksCancelled = true;
        if (_running is { } running && running.File == asset.Key) running.Cancel.Cancel();
        Publish();
    }

    /// <summary>Starts the stopped or failed analyses of <paramref name="assetId"/>'s file again (a model installed meanwhile,
    /// a file that was busy).</summary>
    public void Retry(Guid assetId)
    {
        if (!_assets.TryGetValue(assetId, out var asset)) return;
        var state = StateOf(asset.Key);
        state.TracksCancelled = state.MasksCancelled = false;
        state.TracksFailure = state.MasksFailure = null;
        Publish();
        StartWorker();
    }

    /// <summary>What the export's preflight uses: the Preview's own analyses, the assets still analysed (waiting, running or
    /// stopped — the export never draws without them), the failed ones.</summary>
    public Core.Export.ExportPeople ForExport()
    {
        var pending = new HashSet<Guid>();
        var failedTracks = new Dictionary<Guid, string>();
        var failedMasks = new Dictionary<Guid, string>();
        foreach (var (assetId, asset) in _assets)
        {
            var tracks = StatusOf(assetId, PeopleAnalysisKind.Tracks);
            if (tracks.State == PeopleAnalysisState.Failed) failedTracks[assetId] = tracks.Message ?? "";
            else if (tracks.State != PeopleAnalysisState.Ready) pending.Add(assetId);
            if (!asset.Masks) continue;
            var masks = StatusOf(assetId, PeopleAnalysisKind.Masks);
            if (masks.State == PeopleAnalysisState.Failed) failedMasks[assetId] = masks.Message ?? "";
            else if (masks.State != PeopleAnalysisState.Ready && tracks.State != PeopleAnalysisState.Failed) pending.Add(assetId);
        }
        return new Core.Export.ExportPeople(Analyses, pending) { FailedTracks = failedTracks, FailedMasks = failedMasks };
    }

    private void Refresh()
    {
        if (_disposed) return;
        var project = _projects.Current;
        var rate = project.Settings.FrameRate;
        var assets = project.MediaAssets.ToDictionary(a => a.Id);
        var byAsset = new Dictionary<Guid, (FileKey, bool)>();
        var wanted = new Dictionary<FileKey, Want>();
        FileKey? KeyOf(Guid assetId)
        {
            if (!assets.TryGetValue(assetId, out var asset) || asset.IsMissing || asset.Kind != MediaKind.Video || asset.Metadata is not { } metadata)
                return null;
            var (length, written) = Stat(asset.FilePath);
            return new FileKey(asset.FilePath, length, written, metadata.StartTime ?? MediaTime.Zero,
                HdrColor.Classify(asset.Kind, metadata).Conversion, SourceFrameSelector.NominalRate(metadata), metadata.Duration);
        }
        foreach (var (clip, track) in project.Timeline.VideoTracks.SelectMany(t => t.Clips.OfType<VideoClip>().Select(c => (c, t))))
        {
            if (clip.Follow is null && !clip.HidesOutlinesBehindPeople) continue;
            if (KeyOf(clip.MediaAssetId) is not { } key) continue;
            var asset = assets[clip.MediaAssetId];
            var metadata = asset.Metadata!;
            if (!wanted.TryGetValue(key, out var want)) wanted[key] = want = new Want();
            var (before, after) = PeopleAnalysisNeeds.PastTheCut(clip, track.Transitions);
            foreach (var k in PeopleAnalysisNeeds.Chunks(PeopleAnalysisNeeds.FollowSpans(clip, before, after), metadata.Duration)) want.FollowTracks.Add(k);
            foreach (var k in PeopleAnalysisNeeds.Chunks(PeopleAnalysisNeeds.MaskSpans(clip, rate, before, after), metadata.Duration)) want.Masks.Add(k);
            byAsset[asset.Id] = (key, (byAsset.TryGetValue(asset.Id, out var earlier) && earlier.Item2) || clip.HidesOutlinesBehindPeople);
        }
        // D078 §3: the attempt's events' windows (after the clips' own needs; the asset is known without Follow too).
        foreach (var (assetId, from, to) in EventSpans?.Invoke(project) ?? Enumerable.Empty<(Guid, MediaTime, MediaTime)>())
        {
            if (KeyOf(assetId) is not { } key) continue;
            if (!wanted.TryGetValue(key, out var want)) wanted[key] = want = new Want();
            foreach (var k in PeopleAnalysisNeeds.Chunks(new[] { (from, to) }, key.Duration)) want.EventTracks.Add(k);
            if (!byAsset.ContainsKey(assetId)) byAsset[assetId] = (key, false);
        }
        foreach (var (key, want) in wanted)
        {
            var count = PeopleAnalyzer.ChunkCount(key.Duration);
            want.Tracks.UnionWith(want.FollowTracks);
            // The masks' prompts: the tracks of their chunks and of the neighbouring ones.
            foreach (var k in want.Masks)
                for (var n = k - 1; n <= k + 1; n++)
                    if (n >= 0 && n < count) want.Tracks.Add(n);
        }
        _assets = byAsset;
        _wanted = wanted;
        CheckRunning();
        Publish();
        StartWorker();
    }

    /// <summary>A run nobody needs any more stops — none of its chunks left is wanted (Follow switched off, a part ended by
    /// «Остановить», the clip deleted), checked on every change and after every chunk, so it never analyses past the part;
    /// a run that is not for Follow gives way to a followed part that waits for its tracks (what the user watches first).</summary>
    private void CheckRunning()
    {
        if (_running is not { } running || running.Cancel.IsCancellationRequested) return;
        var want = _wanted.GetValueOrDefault(running.File);
        var set = want is null ? null : running.ForEvents ? want.EventTracks : running.Kind == PeopleAnalysisKind.Tracks ? want.Tracks : want.Masks;
        var stillWanted = set is not null && running.Chunks.Any(k => !running.Done.Contains(k) && set.Contains(k));
        // The events' run gives way to anything the clips need (D078 §3), a run not for Follow to the followed parts.
        if (!stillWanted || (!running.ForFollow && NextWork() is { ForFollow: true }) || (running.ForEvents && NextWork() is { ForEvents: false }))
            running.Cancel.Cancel();
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
        if (!_disposed && (_worker is null || _worker.IsCompleted) && NextWork() is not null)
            _worker = RunAsync(_projectCancel);
    }

    /// <summary>The next run: the followed parts' tracks (from their first missing chunk on), the other tracks, then the masks
    /// whose tracks (their chunk and its wanted neighbours) are done.</summary>
    private Work? NextWork()
    {
        foreach (var forFollow in new[] { true, false })
            foreach (var (key, want) in _wanted)
            {
                var state = StateOf(key);
                if (state.TracksCancelled || state.TracksFailure is not null) continue;
                var run = FirstRun(forFollow ? want.FollowTracks : want.Tracks, k => !state.Tracks.ContainsKey(k));
                if (run.Length > 0) return new Work(key, PeopleAnalysisKind.Tracks, run, forFollow);
            }
        foreach (var (key, want) in _wanted)
        {
            var state = StateOf(key);
            if (state.TracksCancelled || state.MasksCancelled || state.TracksFailure is not null || state.MasksFailure is not null) continue;
            bool Ready(int k) => !state.Masks.ContainsKey(k) && Enumerable.Range(k - 1, 3)
                .All(n => !want.Tracks.Contains(n) || state.Tracks.ContainsKey(n));
            var run = FirstRun(want.Masks, Ready);
            if (run.Length > 0) return new Work(key, PeopleAnalysisKind.Masks, run, false);
        }
        if (EventsNeedGpu || EventsStopped) return null;
        foreach (var (key, want) in _wanted)
        {
            var state = StateOf(key);
            if (state.TracksCancelled || state.TracksFailure is not null) continue;
            var run = FirstRun(want.EventTracks, k => !state.Tracks.ContainsKey(k));
            if (run.Length > 0) return new Work(key, PeopleAnalysisKind.Tracks, run, false, ForEvents: true);
        }
        return null;
    }

    /// <summary>The first series of consecutive chunks of <paramref name="wanted"/> that <paramref name="missing"/> holds for.</summary>
    private static int[] FirstRun(SortedSet<int> wanted, Func<int, bool> missing)
    {
        var run = new List<int>();
        foreach (var k in wanted)
        {
            if (run.Count > 0 && (k != run[^1] + 1 || !missing(k))) break;
            if (run.Count == 0 && !missing(k)) continue;
            run.Add(k);
        }
        return run.ToArray();
    }

    private void Publish()
    {
        var tracks = new Dictionary<Guid, PersonTracks>();
        var masks = new Dictionary<Guid, PersonMaskTrack>();
        foreach (var (assetId, asset) in _assets)
        {
            var state = StateOf(asset.Key);
            if (state.TracksChanged)
            {
                state.MergedTracks = state.Tracks.Count == 0 ? null
                    : PersonTracks.Merge(state.Tracks.OrderBy(c => c.Key).Select(c => c.Value), PeopleAnalyzer.DetectionInterval,
                        state.Tracks.Values.First().ModelId);
                state.TracksChanged = false;
            }
            if (state.MasksChanged)
            {
                state.MergedMasks = state.Masks.Count == 0 ? null : PersonMaskTrack.Merge(state.Masks.OrderBy(c => c.Key).Select(c => c.Value).ToList());
                state.MasksChanged = false;
            }
            if (state.MergedTracks is { } t) tracks[assetId] = t;
            if (asset.Masks && state.MergedMasks is { } m) masks[assetId] = m;
        }
        var changed = !Same(tracks, Analyses.Tracks) || !Same(masks, Analyses.Masks);
        if (changed) Analyses = new PeopleAnalyses(tracks, masks);
        if (changed) AnalysesChanged?.Invoke(this, EventArgs.Empty);
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    private static bool Same<T>(IReadOnlyDictionary<Guid, T> a, IReadOnlyDictionary<Guid, T> b) where T : class =>
        a.Count == b.Count && a.All(p => b.TryGetValue(p.Key, out var other) && ReferenceEquals(other, p.Value));

    /// <summary>A chunk done (on the caller's context): kept, merged, published at once.</summary>
    private void Store(FileKey file, PeopleAnalysisKind kind, int chunk, object result, Running? running)
    {
        if (_disposed) return;
        var state = StateOf(file);
        if (kind == PeopleAnalysisKind.Tracks && result is PersonTracks tracks && state.Tracks.TryAdd(chunk, tracks)) state.TracksChanged = true;
        else if (kind == PeopleAnalysisKind.Masks && result is PersonMaskTrack masks && state.Masks.TryAdd(chunk, masks)) state.MasksChanged = true;
        else return;
        running?.Done.Add(chunk);
        CheckRunning();
        Publish();
    }

    /// <summary>Runs the wanted analyses one run at a time until none can start. Hands over to a new worker when work arrived
    /// meanwhile or the project changed.</summary>
    private async Task RunAsync(CancellationTokenSource project)
    {
        while (!_disposed && !project.IsCancellationRequested && NextWork() is { } work)
        {
            using var cancel = CancellationTokenSource.CreateLinkedTokenSource(project.Token);
            var running = new Running(work.File, work.Kind, work.Chunks, work.ForFollow, work.ForEvents, cancel);
            _running = running;
            Publish();
            // Each chunk reaches this (UI) context as soon as it is done: the worker never touches the coordinator's state.
            var chunks = new Progress<(int Chunk, object Result)>(r => { if (project == _projectCancel) Store(work.File, work.Kind, r.Chunk, r.Result, running); });
            var tracks = work.Kind == PeopleAnalysisKind.Masks ? StateOf(work.File).MergedTracks : null;
            List<(int, object)>? results = null;
            string? failure = null;
            try
            {
                results = await Task.Run(() => AnalyseAsync(work, tracks, chunks, cancel.Token), cancel.Token);
            }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested)
            {
            }
            catch (EventsNeedGpuException)
            {
                EventsNeedGpu = true;   // D078 §2 Q2: never a failure of the video's own analysis
            }
            catch (Exception ex) when (work.ForEvents)
            {
                // Never a failure of the video's own analysis (Follow / Occlusion keep their state): the events stop for the session.
                _logger.LogWarning(ex, "The attempt's events are not searched for {File}.", work.File.FilePath);
                EventsStopped = true;
            }
            catch (PeopleModelException ex)
            {
                failure = ex.Message;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "People analysis ({Kind}) failed for {File}.", work.Kind, work.File.FilePath);
                failure = string.Format(Messages.People_AnalysisFailed, ex.Message);
            }
            _running = null;
            if (project != _projectCancel) break;
            foreach (var (chunk, result) in results ?? new List<(int, object)>()) Store(work.File, work.Kind, chunk, result, null);
            if (failure is not null)
            {
                var state = StateOf(work.File);
                if (work.Kind == PeopleAnalysisKind.Tracks) state.TracksFailure = failure;
                else state.MasksFailure = failure;
            }
            Publish();
        }
        _worker = null;
        StartWorker();
    }

    /// <summary>One run on the thread pool: the chunks found in the cache are handed on at once; from the first one that is
    /// not, the rest is analysed in one decoding pass, each chunk saved and handed on when done.</summary>
    private async Task<List<(int, object)>> AnalyseAsync(Work work, PersonTracks? tracksForMasks, IProgress<(int Chunk, object Result)> report,
        CancellationToken cancellationToken)
    {
        if (_vision is null) throw new PeopleModelException(string.Format(Messages.People_ModelLoadFailed, nameof(IPeopleVision)));
        var results = new List<(int, object)>();
        var fingerprint = _cache?.Fingerprint(work.File.FilePath);
        string? Key(int chunk) => fingerprint is null ? null : work.File.CacheKey(fingerprint, _vision.ModelId, work.Kind, chunk);
        void Done(int chunk, object result)
        {
            results.Add((chunk, result));
            report.Report((chunk, result));
        }

        var index = 0;
        for (; index < work.Chunks.Length; index++)
        {
            var key = Key(work.Chunks[index]);
            object? cached = key is null ? null
                : work.Kind == PeopleAnalysisKind.Tracks ? _cache!.TryLoadTracks(key) : _cache!.TryLoadMasks(key);
            if (cached is null) break;
            Done(work.Chunks[index], cached);
        }
        if (index == work.Chunks.Length) return results;
        if (work.ForEvents && _vision.PrepareDetector(cancellationToken) != InferenceDevice.Gpu) throw new EventsNeedGpuException();

        var from = PeopleAnalyzer.ChunkStart(work.Chunks[index]);
        var to = PeopleAnalyzer.ChunkStart(work.Chunks[^1] + 1);
        if (to > work.File.Duration) to = work.File.Duration;
        var analyzer = new PeopleAnalyzer(_decoder, _vision);
        var started = System.Diagnostics.Stopwatch.StartNew();
        if (work.Kind == PeopleAnalysisKind.Tracks)
        {
            await analyzer.DetectRangeAsync(work.File.Source, from, to, (chunk, tracks) =>
            {
                if (Key(chunk) is { } key) _cache!.SaveTracks(key, tracks);
                Done(chunk, tracks);
            }, null, cancellationToken);
        }
        else
        {
            await analyzer.SegmentRangeAsync(work.File.Source, tracksForMasks ?? throw new InvalidOperationException("The masks need the tracks."),
                from, to, (chunk, masks) =>
                {
                    if (Key(chunk) is { } key) _cache!.SaveMasks(key, masks);
                    Done(chunk, masks);
                }, null, cancellationToken);
        }
        _logger.LogInformation("People {Kind}: {File}: {From:F1}–{To:F1} s ({Chunks} chunks), {Seconds:F1} s.", work.Kind,
            Path.GetFileName(work.File.FilePath), from.TotalSeconds, to.TotalSeconds, work.Chunks.Length - index, started.Elapsed.TotalSeconds);
        return results;
    }

    /// <summary>Completes when no analysis is running or waiting (tests).</summary>
    internal async Task IdleAsync()
    {
        while (_worker is { IsCompleted: false } worker) await worker;
    }

    public void Dispose()
    {
        _disposed = true;
        _projectCancel.Cancel();
        _running?.Cancel.Cancel();
    }
}
