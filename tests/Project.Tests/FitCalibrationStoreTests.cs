using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Core.Telemetry;
using AiVideoEditor.Core.Telemetry.Calibration;
using AiVideoEditor.Project.Persistence;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiVideoEditor.Project.Tests;

/// <summary>
/// D051 §6.1–§6.4 (T2): the calibration file — created on its first write only, Beta on by default, Beta off kept and
/// removing nothing, reset with Beta on and off, damaged / newer / unreadable files, dropped bad records, the lock shared
/// by instances, the atomic write and the estimate's version — all on real files in a temporary folder, never the user's.
/// </summary>
public sealed class FitCalibrationStoreTests : IDisposable
{
    private const long T = 10_000_000;
    private static readonly DateTimeOffset Day = new(2026, 11, 3, 8, 0, 0, TimeSpan.Zero);
    private static readonly TrainingActivity Training = new(Day, Day.AddHours(1), 294, 814);
    private static readonly TrainingActivity Other = new(Day.AddDays(-7), Day.AddDays(-7).AddHours(1), 294, null);
    private static readonly IReadOnlySet<TrainingActivity> InProject = new HashSet<TrainingActivity> { Training };

    private readonly TempFolder _temp = new();
    private readonly string _file;
    private readonly ManualTime _time = new(Day.AddDays(1));

    public FitCalibrationStoreTests() => _file = _temp.Combine("config", "fit-calibration.json");

    public void Dispose() => _temp.Dispose();

    private sealed class ManualTime(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private FitCalibrationStore Store(TimeSpan? lockTimeout = null) =>
        new(_file, NullLogger<FitCalibrationStore>.Instance, new ProjectFileStore(), _time)
        {
            LockTimeout = lockTimeout ?? TimeSpan.FromSeconds(2)
        };

    private static CalibrationKey Key(int climb, TrainingActivity? training = null) =>
        new(training ?? Training, new ClimbIdentity(climb, climb + 1, Day.AddMinutes(climb)));

    private static CalibrationObservation[] Marks(int count, long value, int first = 0) =>
        Enumerable.Range(first, count).Select(i => new CalibrationObservation(Key(i), value, Day.AddSeconds(i))).ToArray();

    private string Write(string json)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
        File.WriteAllText(_file, json);
        return json;
    }

    private string[] Damaged() => Directory.GetFiles(Path.GetDirectoryName(_file)!, "*.damaged");

    // ---- No file, Beta on by default --------------------------------------------------------------------------------

    [Fact]
    public async Task Without_a_file_Beta_is_on_the_estimate_is_12_s_and_nothing_is_created()
    {
        var store = Store();
        Assert.Equal(FitCalibrationSnapshot.Initial, store.Current);
        var loaded = await store.LoadAsync();
        Assert.Equal(new FitCalibrationSnapshot(true, 120_000_000, 0, 0, FitCalibrationFileState.Usable), loaded);
        Assert.False(Directory.Exists(Path.GetDirectoryName(_file)));
    }

    [Fact]
    public async Task A_synchronisation_without_observations_creates_no_file()
    {
        Assert.True(await Store().SynchronizeAsync(InProject, []));
        Assert.False(File.Exists(_file));
    }

    [Fact]
    public async Task A_file_without_the_flag_has_Beta_on()
    {
        Write("""{ "format": "AiVideoEditor.FitCalibration", "formatVersion": 1, "records": [] }""");
        Assert.True((await Store().LoadAsync()).BetaEnabled);
    }

    // ---- Saves ------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_first_save_writes_the_records_and_a_new_instance_reads_them_back()
    {
        var store = Store();
        Assert.True(await store.SynchronizeAsync(InProject, Marks(25, 16 * T)));
        Assert.True(File.Exists(_file));
        Assert.Equal(new FitCalibrationSnapshot(true, 120_000_000 + 25 * 40_000_000 / 100, 1, 25, FitCalibrationFileState.Usable),
            store.Current);

        var again = Store();
        Assert.Equal(store.Current, await again.LoadAsync());
        Assert.Equal(store.LastRecords.OrderBy(p => p.Key).ToList(), again.LastRecords.OrderBy(p => p.Key).ToList());
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(_file)!, "*.tmp"));
    }

    [Fact]
    public async Task A_save_without_a_change_does_not_rewrite_the_file()
    {
        var store = Store();
        await store.SynchronizeAsync(InProject, Marks(3, 16 * T));
        var before = await File.ReadAllTextAsync(_file);
        var written = File.GetLastWriteTimeUtc(_file);
        _time.Now = _time.Now.AddHours(1);

        Assert.True(await store.SynchronizeAsync(InProject, Marks(3, 16 * T)));
        Assert.Equal(before, await File.ReadAllTextAsync(_file));
        Assert.Equal(written, File.GetLastWriteTimeUtc(_file));
    }

    [Fact]
    public async Task The_version_of_the_estimate_increases_only_when_its_value_changes()
    {
        var store = Store();
        await store.SynchronizeAsync(InProject, Marks(5, 16 * T));          // below 20: still 12 s
        Assert.Equal((120_000_000L, 0), (store.Current.EstimateTicks, store.Current.EstimateVersion));
        await store.SynchronizeAsync(InProject, Marks(20, 16 * T));
        Assert.Equal(1, store.Current.EstimateVersion);
        _time.Now = _time.Now.AddHours(1);
        await store.SynchronizeAsync(InProject, Marks(20, 16 * T));
        Assert.Equal(1, store.Current.EstimateVersion);                     // the same records: the same value
        await store.SynchronizeAsync(InProject, Marks(30, 16 * T));
        Assert.Equal(2, store.Current.EstimateVersion);
    }

    [Fact]
    public async Task Beta_off_leaves_the_store_untouched_removes_nothing_and_is_kept()
    {
        var store = Store();
        await store.SynchronizeAsync(InProject, Marks(25, 16 * T));
        var estimate = store.Current.EstimateTicks;
        Assert.True(await store.SetBetaAsync(false));
        var before = await File.ReadAllTextAsync(_file);

        Assert.True(await store.SynchronizeAsync(InProject, Marks(25, 30 * T)));
        Assert.True(await store.SynchronizeAsync(InProject, []));           // would remove every record with Beta on
        Assert.Equal(before, await File.ReadAllTextAsync(_file));
        Assert.Equal((false, estimate, 25), (store.Current.BetaEnabled, store.Current.EstimateTicks, store.Current.UsedCount));

        var restarted = Store();
        Assert.False((await restarted.LoadAsync()).BetaEnabled);
        Assert.True(await restarted.SetBetaAsync(true));
        Assert.True((await Store().LoadAsync()).BetaEnabled);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Reset_removes_every_record_with_Beta_on_or_off(bool beta)
    {
        var store = Store();
        await store.SynchronizeAsync(InProject, Marks(25, 16 * T));
        await store.SetBetaAsync(beta);
        Assert.True(await store.ResetAsync());
        Assert.Equal(new FitCalibrationSnapshot(beta, 120_000_000, 2, 0, FitCalibrationFileState.Usable), store.Current);
        Assert.Empty(store.LastRecords);
        Assert.Equal(store.Current, await Store().LoadAsync());
    }

    // ---- Damaged, newer, unreadable -----------------------------------------------------------------------------------

    [Theory]
    [InlineData("not json at all")]
    [InlineData("""[1, 2]""")]
    [InlineData("""{ "format": "Something.Else", "formatVersion": 1, "records": [] }""")]
    [InlineData("""{ "format": "AiVideoEditor.FitCalibration", "records": [] }""")]
    [InlineData("""{ "format": "AiVideoEditor.FitCalibration", "formatVersion": 1, "beta": false }""")]
    public async Task A_damaged_file_is_set_aside_and_the_calibration_starts_empty_with_Beta_on(string json)
    {
        Write(json);
        var loaded = await Store().LoadAsync();
        Assert.Equal(FitCalibrationSnapshot.Initial, loaded);
        Assert.Single(Damaged());
        Assert.False(File.Exists(_file));

        Assert.True(await Store().SynchronizeAsync(InProject, Marks(2, 16 * T)));
        Assert.True((await Store().LoadAsync()).BetaEnabled);
    }

    [Fact]
    public async Task A_file_of_a_newer_format_version_is_not_used_and_not_overwritten()
    {
        var newer = Write("""{ "format": "AiVideoEditor.FitCalibration", "formatVersion": 2, "records": [], "more": 1 }""");
        var store = Store();
        Assert.Equal(FitCalibrationSnapshot.Initial with { FileState = FitCalibrationFileState.NewerFormat }, await store.LoadAsync());
        Assert.False(await store.SynchronizeAsync(InProject, Marks(25, 16 * T)));
        Assert.False(await store.SetBetaAsync(false));
        Assert.False(await store.ResetAsync());
        Assert.Equal(newer, await File.ReadAllTextAsync(_file));
        Assert.Empty(Damaged());
    }

    [Fact]
    public async Task A_file_that_cannot_be_read_is_left_as_it_is_and_a_save_reports_it()
    {
        await Store().SynchronizeAsync(InProject, Marks(3, 16 * T));
        var before = await File.ReadAllTextAsync(_file);
        var store = Store(TimeSpan.FromMilliseconds(200));
        using (new FileStream(_file, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.Equal(FitCalibrationFileState.Unreadable, (await store.LoadAsync()).FileState);
            Assert.False(await store.SynchronizeAsync(InProject, Marks(5, 16 * T)));
        }
        Assert.Equal(before, await File.ReadAllTextAsync(_file));
    }

    [Fact]
    public async Task Beta_cannot_be_changed_while_the_file_cannot_be_read()
    {
        await Store().SynchronizeAsync(InProject, Marks(3, 16 * T));
        var before = await File.ReadAllTextAsync(_file);
        var store = Store(TimeSpan.FromMilliseconds(200));
        using (new FileStream(_file, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.False(await store.SetBetaAsync(false));
        }
        Assert.Equal(before, await File.ReadAllTextAsync(_file));
        Assert.True((await Store().LoadAsync()).BetaEnabled);
    }

    [Fact]
    public async Task A_reset_is_refused_while_the_file_cannot_be_read()
    {
        await Store().SynchronizeAsync(InProject, Marks(3, 16 * T));
        var before = await File.ReadAllTextAsync(_file);
        var store = Store(TimeSpan.FromMilliseconds(200));
        using (new FileStream(_file, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.False(await store.ResetAsync());
        }
        Assert.Equal(before, await File.ReadAllTextAsync(_file));
        Assert.Equal(3, (await Store().LoadAsync()).UsedCount);
    }

    [Fact]
    public async Task The_stored_initial_value_is_information_only_the_estimate_starts_from_12_s()
    {
        // D051 §6.1 / §6.5: the file holds the initial value, the estimate is computed from the fixed I = 12 s.
        var store = Store();
        await store.SynchronizeAsync(InProject, Marks(20, 16 * T));
        var json = await File.ReadAllTextAsync(_file);
        Assert.Contains("\"initialTicks\": 120000000", json);
        await File.WriteAllTextAsync(_file, json.Replace("\"initialTicks\": 120000000", "\"initialTicks\": 150000000"));

        var loaded = await Store().LoadAsync();
        Assert.Equal(FitCalibrationFileState.Usable, loaded.FileState);
        Assert.Equal(20, loaded.UsedCount);
        Assert.Equal(120_000_000 + 20 * (160_000_000 - 120_000_000) / 100, loaded.EstimateTicks);   // 12.8 s, not 15.2 s
        Assert.Empty(Damaged());
        Assert.True(File.Exists(_file));

        Write("""{ "format": "AiVideoEditor.FitCalibration", "formatVersion": 1, "initialTicks": 150000000, "records": [] }""");
        var empty = await Store().LoadAsync();
        Assert.Equal((FitCalibrationFileState.Usable, 120_000_000L), (empty.FileState, empty.EstimateTicks));
        Assert.Empty(Damaged());
    }

    [Fact]
    public async Task Bad_records_are_dropped_and_the_file_is_rewritten_at_the_next_change()
    {
        var store = Store();
        await store.SynchronizeAsync(InProject, Marks(2, 16 * T));
        var json = await File.ReadAllTextAsync(_file);
        // A used record whose value lies outside the bounds, and a record without a climb.
        json = json.Replace("\"records\": [", """
            "records": [
                { "training": { "startUtc": "2026-11-03T08:00:00+00:00", "endUtc": "2026-11-03T09:00:00+00:00", "manufacturer": 294, "product": 814 },
                  "climb": { "key": 90, "number": 91, "startUtc": "2026-11-03T09:30:00+00:00" }, "value": 950000000, "status": "used",
                  "firstSeenAt": "2026-11-03T08:00:00+00:00", "positionChangedAt": "2026-11-03T08:00:00+00:00", "valueChangedAt": "2026-11-03T08:00:00+00:00" },
                { "training": { "startUtc": "2026-11-03T08:00:00+00:00", "endUtc": "2026-11-03T09:00:00+00:00", "manufacturer": 294 }, "value": 1, "status": "used" },
            """);
        await File.WriteAllTextAsync(_file, json);

        var reader = Store();
        Assert.Equal(2, (await reader.LoadAsync()).UsedCount);
        Assert.True(await reader.SynchronizeAsync(InProject, Marks(2, 16 * T)));     // no change of a record, but a rewrite
        Assert.DoesNotContain("\"key\": 90", await File.ReadAllTextAsync(_file));
    }

    // ---- Several instances --------------------------------------------------------------------------------------------

    [Fact]
    public async Task Two_instances_keep_each_others_records()
    {
        var first = Store();
        var second = Store();
        await first.SynchronizeAsync(InProject, Marks(3, 16 * T));
        var otherProject = new HashSet<TrainingActivity> { Other };
        await second.SynchronizeAsync(otherProject, [new CalibrationObservation(Key(7, Other), 14 * T, Day)]);

        var all = Store();
        await all.LoadAsync();
        Assert.Equal(4, all.LastRecords.Count);
        Assert.Equal(4, all.Current.UsedCount);
    }

    [Fact]
    public async Task A_lock_held_by_another_instance_skips_the_change()
    {
        await Store().SynchronizeAsync(InProject, Marks(1, 16 * T));
        var before = await File.ReadAllTextAsync(_file);
        using (new FileStream(Path.Combine(Path.GetDirectoryName(_file)!, FitCalibrationStore.LockFileName),
                   FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.False(await Store(TimeSpan.FromMilliseconds(150)).SynchronizeAsync(InProject, Marks(5, 16 * T)));
        }
        Assert.Equal(before, await File.ReadAllTextAsync(_file));
    }

    [Fact]
    public async Task Concurrent_saves_of_one_instance_are_serialised()
    {
        var store = Store();
        var otherProject = new HashSet<TrainingActivity> { Other };
        await Task.WhenAll(
            store.SynchronizeAsync(InProject, Marks(10, 16 * T)),
            store.SynchronizeAsync(otherProject, [new CalibrationObservation(Key(3, Other), 14 * T, Day)]));
        await store.LoadAsync();
        Assert.Equal(11, store.LastRecords.Count);
    }
}
