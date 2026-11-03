using System.Security.Cryptography;
using System.Text;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Core.Telemetry;

namespace AiVideoEditor.Project.Tests.Research;

/// <summary>
/// In-memory files for the study service (Step 35.3b1): each path has content, a size and a write time that a test can
/// change — also between the stamp read before and after a hash, or after a verification.
/// </summary>
internal sealed class StudyTestFiles : IFileFingerprintService
{
    private readonly Dictionary<string, (string Content, long Ticks)> _files = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Called between the read and the second stamp of a hash (to simulate a change during the read).</summary>
    public Action<string>? DuringRead { get; set; }

    public HashSet<string> Locked { get; } = new(StringComparer.OrdinalIgnoreCase);

    public int Hashes { get; private set; }

    public string Put(string path, string content, long ticks = 1_000)
    {
        _files[path] = (content, ticks);
        return path;
    }

    public void Touch(string path, long ticks) => _files[path] = (_files[path].Content, ticks);

    public void Remove(string path) => _files.Remove(path);

    public FileStamp? Stamp(string path) =>
        _files.TryGetValue(path, out var f) ? new FileStamp(Encoding.UTF8.GetByteCount(f.Content), f.Ticks) : null;

    public Task<FileFingerprint?> HashAsync(string path, IProgress<long>? progress = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (Locked.Contains(path)) throw new IOException("locked");
        if (Stamp(path) is not { } before) return Task.FromResult<FileFingerprint?>(null);
        Hashes++;
        var sha = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(_files[path].Content))).ToLowerInvariant();
        progress?.Report(before.SizeBytes);
        DuringRead?.Invoke(path);
        var after = Stamp(path) ?? new FileStamp(-1, -1);
        return Task.FromResult<FileFingerprint?>(new FileFingerprint(sha, before, after));
    }

    public static readonly DateTimeOffset T0 = new(2026, 9, 27, 7, 2, 39, TimeSpan.Zero);

    /// <summary>A workout with three climbs (split indices 0, 2, 4).</summary>
    public static ClimbingWorkout Workout(int secondClimbNumber = 2) => new()
    {
        DeviceName = "COROS APEX 4 42mm", Manufacturer = 294, Product = 814, StartUtc = T0, EndUtc = T0.AddHours(1),
        UtcOffset = TimeSpan.FromHours(3), ElapsedTime = MediaTime.FromSeconds(3600), TimerTime = MediaTime.FromSeconds(3600),
        Sport = 31, SubSport = 69, AverageHeartRate = 130, MaxHeartRate = 169,
        HeartRate = Array.Empty<HeartRateSample>(), Pauses = Array.Empty<WorkoutInterval>(), Warnings = Array.Empty<TrainingFileWarning>(),
        Segments = new[]
        {
            new WorkoutSegment(0, WorkoutSegmentKind.Climb, 1, T0, T0.AddSeconds(21), MediaTime.FromSeconds(20.17), 105, 121),
            new WorkoutSegment(1, WorkoutSegmentKind.Rest, null, T0.AddSeconds(21), T0.AddSeconds(258), MediaTime.FromSeconds(237), 125, 141),
            new WorkoutSegment(2, WorkoutSegmentKind.Climb, secondClimbNumber, T0.AddSeconds(258), T0.AddSeconds(286), MediaTime.FromSeconds(27.3), 119, 123),
            new WorkoutSegment(4, WorkoutSegmentKind.Climb, 3, T0.AddSeconds(542), T0.AddSeconds(565), MediaTime.FromSeconds(22.4), 125, 128),
        }
    };

    public static MediaAsset Video(string path, double seconds = 600, bool probed = true) => new()
    {
        FilePath = path, Kind = MediaKind.Video,
        Metadata = new MediaMetadata
        {
            Duration = MediaTime.FromSeconds(seconds),
            RecordingTimeProbe = probed ? RecordingTimeProbe.Found("2026-09-27T07:07:00.000000Z", RecordingTimeTagOrigin.Format) : null
        }
    };

    /// <summary>Gives the service's study a marking rule — marking needs one (Step 35.3R1).</summary>
    public static Core.Telemetry.Research.SyncStudy WithRule(Core.Telemetry.Research.SyncStudy study, AiVideoEditor.Project.Research.SyncStudyService service)
    {
        service.Editor.SetMarkingRule("test-rule-1", "start: the last foot leaves the floor; end: the last contact with the wall");
        return study;
    }

    /// <summary>A clock that advances one second per reading.</summary>
    public static Func<DateTimeOffset> Clock()
    {
        var t = new DateTimeOffset(2026, 11, 2, 9, 0, 0, TimeSpan.Zero);
        return () => t = t.AddSeconds(1);
    }
}
