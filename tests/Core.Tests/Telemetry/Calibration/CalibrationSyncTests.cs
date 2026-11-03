using AiVideoEditor.Core.Telemetry;
using AiVideoEditor.Core.Telemetry.Calibration;
using Xunit;
using static AiVideoEditor.Core.Tests.Telemetry.Calibration.StartCalibrationTests;

namespace AiVideoEditor.Core.Tests.Telemetry.Calibration;

/// <summary>
/// D051 §6.4 (T1a): a save with Beta on — one record per key; the same value and position time write nothing; a move
/// changes the value and both times; a new <c>Z</c> changes the value only; a hidden / declined mark leaves a tombstone;
/// an Undo or «Вернуть» brings the same record back with <c>firstSeenAt</c> kept; no <c>Z</c> leaves the record alone;
/// records of a training not in the project are never touched; records are never trimmed.
/// </summary>
public class CalibrationSyncTests
{
    private const long T = 10_000_000;
    private static readonly DateTimeOffset Day = new(2026, 11, 3, 0, 0, 0, TimeSpan.Zero);
    private static readonly TrainingActivity Training = new(Day, Day.AddHours(1), 294, 814);
    private static readonly TrainingActivity Other = new(Day.AddDays(-30), Day.AddDays(-30).AddHours(1), 294, 814);
    private static readonly IReadOnlySet<TrainingActivity> InProject = new HashSet<TrainingActivity> { Training };

    private static DateTimeOffset At(int minute) => Day.AddMinutes(minute);

    private static CalibrationSyncResult Save(IReadOnlyDictionary<CalibrationKey, CalibrationRecord> records, int minute,
        params CalibrationObservation[] observations) => CalibrationSync.Apply(records, InProject, observations, At(minute));

    private static readonly Dictionary<CalibrationKey, CalibrationRecord> Empty = new();

    [Fact]
    public void A_new_observation_adds_one_record_with_both_times_now()
    {
        var r = Save(Empty, 10, new CalibrationObservation(Key(1), 12 * T, At(9)));
        Assert.True(r.Changed);
        var record = Assert.Single(r.Records.Values);
        Assert.Equal(new CalibrationRecord(Key(1), 12 * T, CalibrationRecordStatus.Used, null, At(10), At(9), At(10)), record);
    }

    [Fact]
    public void The_same_value_and_position_time_write_nothing()
    {
        var first = Save(Empty, 10, new CalibrationObservation(Key(1), 12 * T, At(9))).Records;
        var again = Save(first, 20, new CalibrationObservation(Key(1), 12 * T, At(9)));
        Assert.False(again.Changed);
        Assert.Equal(first[Key(1)], again.Records[Key(1)]);
    }

    [Fact]
    public void A_move_changes_the_value_and_both_times_a_new_zero_the_value_only()
    {
        var records = Save(Empty, 10, new CalibrationObservation(Key(1), 12 * T, At(9))).Records;
        var moved = Save(records, 30, new CalibrationObservation(Key(1), 14 * T, At(28))).Records[Key(1)];
        Assert.Equal((14 * T, At(10), At(28), At(30)), (moved.Value, moved.FirstSeenAt, moved.PositionChangedAt, moved.ValueChangedAt));

        var records2 = Save(Empty, 10, new CalibrationObservation(Key(1), 12 * T, At(9))).Records;
        var newZero = Save(records2, 40, new CalibrationObservation(Key(1), 12 * T + 5_000_000, At(9))).Records[Key(1)];
        Assert.Equal((At(9), At(40)), (newZero.PositionChangedAt, newZero.ValueChangedAt));
    }

    [Fact]
    public void A_hidden_mark_leaves_a_tombstone_and_an_undo_brings_the_same_record_back()
    {
        var records = Save(Empty, 10, new CalibrationObservation(Key(1), 12 * T, At(9))).Records;
        var hidden = Save(records, 20);
        Assert.True(hidden.Changed);
        Assert.Equal(CalibrationRecordStatus.Removed, hidden.Records[Key(1)].Status);
        Assert.Single(hidden.Records);

        var back = Save(hidden.Records, 30, new CalibrationObservation(Key(1), 12 * T, At(9)));
        Assert.True(back.Changed);
        Assert.Equal(new CalibrationRecord(Key(1), 12 * T, CalibrationRecordStatus.Used, null, At(10), At(9), At(10)), back.Records[Key(1)]);
        Assert.Single(back.Records);
        Assert.False(Save(back.Records, 40, new CalibrationObservation(Key(1), 12 * T, At(9))).Changed);
    }

    [Fact]
    public void A_tombstone_is_never_revived_by_a_value_alone_and_stays_without_a_mark()
    {
        var removed = Save(Save(Empty, 10, new CalibrationObservation(Key(1), 12 * T, At(9))).Records, 20).Records;
        var later = Save(removed, 30);
        Assert.False(later.Changed);
        Assert.Equal(CalibrationRecordStatus.Removed, later.Records[Key(1)].Status);
    }

    [Fact]
    public void The_bounds_move_a_record_between_used_and_excluded()
    {
        var records = Save(Empty, 10, new CalibrationObservation(Key(1), 95 * T, At(9))).Records;
        Assert.Equal((CalibrationRecordStatus.Excluded, CalibrationExclusionReason.AboveBounds), (records[Key(1)].Status, records[Key(1)].Reason));
        var back = Save(records, 20, new CalibrationObservation(Key(1), 20 * T, At(19))).Records[Key(1)];
        Assert.Equal((CalibrationRecordStatus.Used, (CalibrationExclusionReason?)null), (back.Status, back.Reason));
        var excludedRemoved = Save(records, 30).Records[Key(1)];
        Assert.Equal((CalibrationRecordStatus.Removed, (CalibrationExclusionReason?)null), (excludedRemoved.Status, excludedRemoved.Reason));
    }

    [Fact]
    public void A_mark_without_a_zero_leaves_its_record_unchanged_and_never_removes_it()
    {
        var records = Save(Empty, 10, new CalibrationObservation(Key(1), 12 * T, At(9))).Records;
        var noZero = Save(records, 20, new CalibrationObservation(Key(1), null, At(9)));
        Assert.False(noZero.Changed);
        Assert.Equal(records[Key(1)], noZero.Records[Key(1)]);
        Assert.False(Save(Empty, 20, new CalibrationObservation(Key(2), null, At(9))).Changed);
    }

    [Fact]
    public void Records_of_a_training_not_in_the_project_are_never_touched()
    {
        var foreign = new CalibrationRecord(Key(1, Other), 12 * T, CalibrationRecordStatus.Used, null, At(1), At(1), At(1));
        var records = new Dictionary<CalibrationKey, CalibrationRecord> { [foreign.Key] = foreign };
        var r = Save(records, 20);
        Assert.False(r.Changed);
        Assert.Equal(foreign, r.Records[foreign.Key]);
    }

    [Fact]
    public void Records_are_never_trimmed_to_the_window()
    {
        var observations = Enumerable.Range(0, 250).Select(i => new CalibrationObservation(Key(i), 12 * T + i, At(i))).ToArray();
        var r = Save(Empty, 300, observations);
        Assert.Equal(250, r.Records.Count);
        Assert.Equal(StartCalibration.Window, StartCalibration.WindowOf(r.Records.Values).Count);
    }

    [Fact]
    public void Two_observations_for_one_climb_or_a_foreign_training_are_refused()
    {
        Assert.Throws<ArgumentException>(() => Save(Empty, 1,
            new CalibrationObservation(Key(1), 12 * T, At(0)), new CalibrationObservation(Key(1), 13 * T, At(0))));
        Assert.Throws<ArgumentException>(() => Save(Empty, 1, new CalibrationObservation(Key(1, Other), 12 * T, At(0))));
    }

    [Fact]
    public void Any_grouping_of_the_saves_ends_in_the_same_estimate()
    {
        var all = Enumerable.Range(0, 140).Select(i => new CalibrationObservation(Key(i), 16 * T + (i * 7919L % 30_000_001L) - 15_000_000L, At(i))).ToList();
        var finals = new HashSet<long>();
        foreach (var group in new[] { 1, 7, 40, 140 })
        {
            IReadOnlyDictionary<CalibrationKey, CalibrationRecord> records = Empty;
            for (var i = 0; i < all.Count; i += group)
                records = Save(records, 1000 + i, all.Take(i + group).ToArray()).Records;   // a save holds every active mark so far
            finals.Add(StartCalibration.Estimate(records.Values));
        }
        Assert.Single(finals);
    }
}
