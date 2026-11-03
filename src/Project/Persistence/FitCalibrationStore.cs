using System.Text.Json;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Core.Telemetry;
using AiVideoEditor.Core.Telemetry.Calibration;
using Microsoft.Extensions.Logging;

namespace AiVideoEditor.Project.Persistence;

/// <summary>
/// The global start calibration in one JSON file of the application's configuration (D051 §6.1, Step 37.2):
/// <c>{ "format": "AiVideoEditor.FitCalibration", "formatVersion": 1, "beta", "initialTicks", "estimate": { "ticks",
/// "version" }, "records": [ … ] }</c>, the records ordered by their key.
/// </summary>
/// <remarks>
/// <para>Written like the recent projects (<see cref="RecentProjectsStore"/>): a change reads the file again and writes
/// it atomically under a lock shared by every running instance — <c>fit-calibration.lock</c> next to it — and an
/// in-process semaphore. A lock not obtained within <see cref="LockTimeout"/> skips the change (logged).</para>
/// <para>A file that isn't a calibration of this format is set aside as <c>*.damaged</c> and the calibration starts
/// empty with Beta on; single bad records are dropped. A file of a newer format version or one that can't be read is
/// neither used nor overwritten (the estimate is <c>I</c>). The folder and the file are created only on the first write.
/// The records are the source of truth: the estimate is recomputed from them on every read and write, and its version
/// increases whenever its value changes.</para>
/// </remarks>
public sealed class FitCalibrationStore : IFitCalibrationStore
{
    public const string FormatId = "AiVideoEditor.FitCalibration";
    public const int CurrentFormatVersion = 1;
    public const string LockFileName = "fit-calibration.lock";

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private readonly ProjectFileStore _files;
    private readonly ILogger<FitCalibrationStore> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile FitCalibrationSnapshot _current = FitCalibrationSnapshot.Initial;

    public FitCalibrationStore(string filePath, ILogger<FitCalibrationStore> logger)
        : this(filePath, logger, new ProjectFileStore(), TimeProvider.System)
    {
    }

    internal FitCalibrationStore(string filePath, ILogger<FitCalibrationStore> logger, ProjectFileStore files, TimeProvider time)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        FilePath = Path.GetFullPath(filePath);
        LockFilePath = Path.Combine(Path.GetDirectoryName(FilePath)!, LockFileName);
        _logger = logger;
        _files = files;
        Time = time;
    }

    public string FilePath { get; }

    public string LockFilePath { get; }

    /// <summary>The clock of <c>firstSeenAt</c> / <c>valueChangedAt</c> (D051 §6.3).</summary>
    internal TimeProvider Time { get; }

    /// <summary>How long a change waits for the lock of another instance (or another change of this one).</summary>
    internal TimeSpan LockTimeout { get; init; } = TimeSpan.FromSeconds(2);

    public FitCalibrationSnapshot Current => _current;

    /// <summary>The records as last read or written (tests and diagnostics).</summary>
    internal IReadOnlyDictionary<CalibrationKey, CalibrationRecord> LastRecords { get; private set; } =
        new Dictionary<CalibrationKey, CalibrationRecord>();

    public Task<FitCalibrationSnapshot> LoadAsync(CancellationToken ct = default) =>
        Task.Run(async () =>
        {
            if (!File.Exists(FilePath)) return Publish(Calibration.Empty, FitCalibrationFileState.Usable);

            // Under the lock, so a damaged file is set aside only if no other instance replaced it meanwhile.
            using var held = await TryLockAsync(ct).ConfigureAwait(false);
            var read = await ReadAsync(setAsideDamaged: held is not null, ct).ConfigureAwait(false);
            return Publish(read.Calibration, read.State);
        }, ct);

    public Task<bool> SynchronizeAsync(IReadOnlySet<TrainingActivity> trainings, IReadOnlyCollection<CalibrationObservation> observations,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(trainings);
        ArgumentNullException.ThrowIfNull(observations);
        return ChangeAsync("synchronise the observations", calibration =>
        {
            if (!calibration.Beta) return calibration;                       // Beta off: the store is untouched
            var result = CalibrationSync.Apply(calibration.Records, trainings, observations, Time.GetUtcNow());
            return result.Changed ? calibration with { Records = result.Records } : calibration;
        }, ct);
    }

    public Task<bool> SetBetaAsync(bool enabled, CancellationToken ct = default) =>
        ChangeAsync(enabled ? "turn the collection on" : "turn the collection off",
            calibration => calibration.Beta == enabled ? calibration : calibration with { Beta = enabled }, ct);

    public Task<bool> ResetAsync(CancellationToken ct = default) =>
        ChangeAsync("reset the calibration", calibration => calibration.Records.Count == 0
            ? calibration
            : calibration with { Records = new Dictionary<CalibrationKey, CalibrationRecord>() }, ct);

    // ---- The calibration in memory --------------------------------------------------------------------------------

    /// <param name="Beta">The collection of observations; on by default (D051 §6.2).</param>
    /// <param name="Records">One per key.</param>
    /// <param name="StoredEstimate">The estimate the file held, to tell whether the version increases.</param>
    /// <param name="StoredVersion">The version the file held.</param>
    private sealed record Calibration(bool Beta, IReadOnlyDictionary<CalibrationKey, CalibrationRecord> Records,
        long StoredEstimate, int StoredVersion)
    {
        public static Calibration Empty { get; } =
            new(true, new Dictionary<CalibrationKey, CalibrationRecord>(), StartCalibration.InitialTicks, 0);

        public long Estimate => StartCalibration.Estimate(Records.Values);

        public int Version => Estimate == StoredEstimate ? StoredVersion : StoredVersion + 1;
    }

    private FitCalibrationSnapshot Publish(Calibration calibration, FitCalibrationFileState state)
    {
        LastRecords = calibration.Records;
        var snapshot = state == FitCalibrationFileState.Usable
            ? new FitCalibrationSnapshot(calibration.Beta, calibration.Estimate, calibration.Version,
                calibration.Records.Values.Count(r => r.Status == CalibrationRecordStatus.Used), state)
            : FitCalibrationSnapshot.Initial with { FileState = state };
        _current = snapshot;
        return snapshot;
    }

    // ---- Changing the calibration ---------------------------------------------------------------------------------

    /// <summary>Reads the calibration again under the lock, applies <paramref name="change"/> (the same instance =
    /// nothing to write) and writes the result.</summary>
    private Task<bool> ChangeAsync(string what, Func<Calibration, Calibration> change, CancellationToken ct) =>
        Task.Run(async () =>
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                using var held = await TryLockAsync(ct).ConfigureAwait(false);
                if (held is null)
                {
                    _logger.LogWarning("FIT calibration: couldn't {What} — the file is in use by another instance.", what);
                    return false;
                }

                var read = await ReadAsync(setAsideDamaged: true, ct).ConfigureAwait(false);
                if (read.State != FitCalibrationFileState.Usable)
                {
                    Publish(read.Calibration, read.State);
                    return false; // logged by ReadAsync
                }

                var changed = change(read.Calibration);
                if (ReferenceEquals(changed, read.Calibration) && !read.NeedsRewrite)
                {
                    Publish(read.Calibration, read.State);     // nothing to write (no file stays uncreated)
                    return true;
                }

                var written = changed with { StoredEstimate = changed.Estimate, StoredVersion = changed.Version };
                await _files.WriteAtomicAsync(FilePath, Serialize(written), ct).ConfigureAwait(false);
                Publish(written, FitCalibrationFileState.Usable);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ProjectFileException
                                           or ArgumentException or NotSupportedException)
            {
                _logger.LogWarning(ex, "FIT calibration: couldn't {What}; the file was left as it was.", what);
                return false;
            }
        }, ct);

    private static string Serialize(Calibration calibration) => JsonSerializer.Serialize(new FileDto
    {
        Format = FormatId,
        FormatVersion = CurrentFormatVersion,
        Beta = calibration.Beta,
        InitialTicks = StartCalibration.InitialTicks,
        Estimate = new EstimateDto { Ticks = calibration.StoredEstimate, Version = calibration.StoredVersion },
        Records = calibration.Records.Values.OrderBy(r => r.Key).Select(r => new RecordDto
        {
            Training = new TrainingDto
            {
                StartUtc = r.Key.Training.StartUtc.ToUniversalTime(), EndUtc = r.Key.Training.EndUtc.ToUniversalTime(),
                Manufacturer = r.Key.Training.Manufacturer, Product = r.Key.Training.Product
            },
            Climb = new ClimbDto { Key = r.Key.Climb.ClimbKey, Number = r.Key.Climb.ClimbNumber, StartUtc = r.Key.Climb.ClimbStartUtc.ToUniversalTime() },
            Value = r.Value,
            Status = StatusName(r.Status),
            Reason = r.Reason is { } reason ? ReasonName(reason) : null,
            FirstSeenAt = r.FirstSeenAt.ToUniversalTime(),
            PositionChangedAt = r.PositionChangedAt.ToUniversalTime(),
            ValueChangedAt = r.ValueChangedAt.ToUniversalTime()
        }).ToList()
    }, Options);

    private static string StatusName(CalibrationRecordStatus status) => status switch
    {
        CalibrationRecordStatus.Used => "used",
        CalibrationRecordStatus.Excluded => "excluded",
        _ => "removed"
    };

    private static string ReasonName(CalibrationExclusionReason reason) =>
        reason == CalibrationExclusionReason.BelowBounds ? "belowBounds" : "aboveBounds";

    private sealed class FileDto
    {
        public string Format { get; set; } = "";
        public int FormatVersion { get; set; }
        public bool Beta { get; set; }
        public long InitialTicks { get; set; }
        public EstimateDto Estimate { get; set; } = new();
        public List<RecordDto> Records { get; set; } = new();
    }

    private sealed class EstimateDto
    {
        public long Ticks { get; set; }
        public int Version { get; set; }
    }

    private sealed class RecordDto
    {
        public TrainingDto Training { get; set; } = new();
        public ClimbDto Climb { get; set; } = new();
        public long Value { get; set; }
        public string Status { get; set; } = "";
        public string? Reason { get; set; }
        public DateTimeOffset FirstSeenAt { get; set; }
        public DateTimeOffset PositionChangedAt { get; set; }
        public DateTimeOffset ValueChangedAt { get; set; }
    }

    private sealed class TrainingDto
    {
        public DateTimeOffset StartUtc { get; set; }
        public DateTimeOffset EndUtc { get; set; }
        public int Manufacturer { get; set; }
        public int? Product { get; set; }
    }

    private sealed class ClimbDto
    {
        public int Key { get; set; }
        public int Number { get; set; }
        public DateTimeOffset StartUtc { get; set; }
    }

    // ---- Reading ----------------------------------------------------------------------------------------------------

    /// <param name="NeedsRewrite">The file held records that were dropped (bad or duplicate).</param>
    private sealed record ReadResult(Calibration Calibration, FitCalibrationFileState State, bool NeedsRewrite);

    private async Task<ReadResult> ReadAsync(bool setAsideDamaged, CancellationToken ct)
    {
        string json;
        try
        {
            json = await File.ReadAllTextAsync(FilePath, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return new ReadResult(Calibration.Empty, FitCalibrationFileState.Usable, NeedsRewrite: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "FIT calibration: {File} couldn't be read; it is left as it is.", FilePath);
            return new ReadResult(Calibration.Empty, FitCalibrationFileState.Unreadable, NeedsRewrite: false);
        }

        var parsed = Parse(json);
        switch (parsed.Problem)
        {
            case null:
                if (parsed.Dropped > 0)
                    _logger.LogWarning("FIT calibration: {Count} bad record(s) of {File} were dropped.", parsed.Dropped, FilePath);
                return new ReadResult(parsed.Calibration!, FitCalibrationFileState.Usable, NeedsRewrite: parsed.Dropped > 0);
            case NewerVersion:
                _logger.LogWarning("FIT calibration: {File} is of a newer format version; it is not used or overwritten.", FilePath);
                return new ReadResult(Calibration.Empty, FitCalibrationFileState.NewerFormat, NeedsRewrite: false);
            default:
                _logger.LogWarning("FIT calibration: {File} is damaged ({Problem}); the calibration starts empty.", FilePath,
                    parsed.Problem);
                if (setAsideDamaged)
                    SetAside();
                return new ReadResult(Calibration.Empty, FitCalibrationFileState.Usable, NeedsRewrite: true);
        }
    }

    private const string NewerVersion = "a newer format version";

    private sealed record Parsed(Calibration? Calibration, int Dropped, string? Problem);

    private static Parsed Parse(string json)
    {
        static Parsed Damaged(string problem) => new(null, 0, problem);

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return Damaged("not JSON");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return Damaged("not an object");
            if (!root.TryGetProperty("format", out var format) || format.ValueKind != JsonValueKind.String ||
                format.GetString() != FormatId)
                return Damaged("not a FIT calibration");
            if (!root.TryGetProperty("formatVersion", out var version) || !version.TryGetInt32(out var v) || v < 1)
                return Damaged("no valid format version");
            if (v > CurrentFormatVersion)
                return new Parsed(null, 0, NewerVersion);
            if (!root.TryGetProperty("records", out var records) || records.ValueKind != JsonValueKind.Array)
                return Damaged("no list of records");

            // D051 §6.2: no flag means on.
            var beta = !root.TryGetProperty("beta", out var b) || b.ValueKind != JsonValueKind.False;
            long storedEstimate = StartCalibration.InitialTicks;
            var storedVersion = 0;
            if (root.TryGetProperty("estimate", out var estimate) && estimate.ValueKind == JsonValueKind.Object &&
                estimate.TryGetProperty("ticks", out var ticks) && ticks.TryGetInt64(out var t) &&
                estimate.TryGetProperty("version", out var ver) && ver.TryGetInt32(out var n) && n >= 0)
            {
                storedEstimate = t;
                storedVersion = n;
            }

            var list = new Dictionary<CalibrationKey, CalibrationRecord>();
            var dropped = 0;
            foreach (var item in records.EnumerateArray())
            {
                if (RecordOrNull(item) is { } record && list.TryAdd(record.Key, record)) continue;
                dropped++;
            }
            return new Parsed(new Calibration(beta, list, storedEstimate, storedVersion), dropped, null);
        }
    }

    private static CalibrationRecord? RecordOrNull(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object) return null;
        if (!item.TryGetProperty("training", out var training) || training.ValueKind != JsonValueKind.Object ||
            !ReadTime(training, "startUtc", out var trainingStart) || !ReadTime(training, "endUtc", out var trainingEnd) ||
            !training.TryGetProperty("manufacturer", out var m) || !m.TryGetInt32(out var manufacturer)) return null;
        int? product = null;
        if (training.TryGetProperty("product", out var p) && p.ValueKind != JsonValueKind.Null)
        {
            if (!p.TryGetInt32(out var value)) return null;
            product = value;
        }
        if (!item.TryGetProperty("climb", out var climb) || climb.ValueKind != JsonValueKind.Object ||
            !climb.TryGetProperty("key", out var k) || !k.TryGetInt32(out var climbKey) ||
            !climb.TryGetProperty("number", out var num) || !num.TryGetInt32(out var number) ||
            !ReadTime(climb, "startUtc", out var climbStart)) return null;
        if (!item.TryGetProperty("value", out var v) || !v.TryGetInt64(out var observation)) return null;
        if (!ReadTime(item, "firstSeenAt", out var firstSeen) || !ReadTime(item, "positionChangedAt", out var positionChanged) ||
            !ReadTime(item, "valueChangedAt", out var valueChanged)) return null;

        var status = item.TryGetProperty("status", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
        var reason = item.TryGetProperty("reason", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() : null;
        var (byBounds, boundsReason) = StartCalibration.Classify(observation);
        CalibrationRecordStatus recordStatus;
        CalibrationExclusionReason? recordReason;
        switch (status)
        {
            case "used" or "excluded":
                // The status of an active record must match its value against the bounds.
                if (StatusName(byBounds) != status || (boundsReason is { } br ? ReasonName(br) : null) != reason) return null;
                (recordStatus, recordReason) = (byBounds, boundsReason);
                break;
            case "removed" when reason is null:
                (recordStatus, recordReason) = (CalibrationRecordStatus.Removed, null);
                break;
            default:
                return null;
        }

        var key = new CalibrationKey(new TrainingActivity(trainingStart, trainingEnd, manufacturer, product),
            new ClimbIdentity(climbKey, number, climbStart));
        return new CalibrationRecord(key, observation, recordStatus, recordReason, firstSeen, positionChanged, valueChanged);
    }

    private static bool ReadTime(JsonElement parent, string name, out DateTimeOffset value)
    {
        value = default;
        if (!parent.TryGetProperty(name, out var e) || e.ValueKind != JsonValueKind.String || !e.TryGetDateTimeOffset(out var t))
            return false;
        value = t.ToUniversalTime();
        return true;
    }

    private void SetAside()
    {
        try
        {
            var target = Path.ChangeExtension(FilePath, $".{DateTime.UtcNow:yyyyMMddHHmmssfff}.damaged");
            File.Move(FilePath, target);
            _logger.LogWarning("FIT calibration: the damaged file was kept as {File}.", target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "FIT calibration: the damaged file couldn't be set aside.");
        }
    }

    // ---- Lock -------------------------------------------------------------------------------------------------------

    /// <summary>The in-process semaphore and then the lock file, both within <see cref="LockTimeout"/>; null if not
    /// obtained (the lock file's folder must exist).</summary>
    private async Task<IDisposable?> TryLockAsync(CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + LockTimeout;
        if (!await _gate.WaitAsync(LockTimeout, ct).ConfigureAwait(false))
            return null;

        try
        {
            while (true)
            {
                try
                {
                    var stream = new FileStream(LockFilePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                    return new Held(stream, _gate);
                }
                catch (IOException) when (DateTime.UtcNow < deadline)
                {
                    // Held by another instance (a sharing violation): try again shortly.
                }

                await Task.Delay(25, ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _gate.Release();
            return null;
        }
        catch
        {
            _gate.Release();
            throw;
        }
    }

    private sealed class Held(FileStream stream, SemaphoreSlim gate) : IDisposable
    {
        public void Dispose()
        {
            stream.Dispose();
            gate.Release();
        }
    }
}
