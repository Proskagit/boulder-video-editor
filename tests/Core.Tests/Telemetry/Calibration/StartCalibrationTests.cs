using AiVideoEditor.Core.Telemetry;
using AiVideoEditor.Core.Telemetry.Calibration;
using Xunit;

namespace AiVideoEditor.Core.Tests.Telemetry.Calibration;

/// <summary>
/// D051 §6.5 (T1): the K3 estimate in integer ticks — the bounds, the window of the last 100 <c>used</c> records by
/// <c>positionChangedAt</c> and key, the minimum of 20, the Huber target by the L / U bisection, the weight
/// <c>min(n, 100) / 100</c> and floor division. The expected values are the reference implementation's (`k3r.py`).
/// </summary>
public class StartCalibrationTests
{
    private const long T = 10_000_000;
    private static readonly DateTimeOffset Epoch = new(2026, 11, 3, 0, 0, 0, TimeSpan.Zero);
    private static readonly TrainingActivity Training = new(Epoch, Epoch.AddHours(1), 294, 814);

    internal static CalibrationKey Key(int climb, TrainingActivity? training = null) =>
        new(training ?? Training, new ClimbIdentity(climb, climb + 1, Epoch.AddMinutes(climb)));

    private static CalibrationRecord Used(int climb, long value, long positionSeconds) =>
        new(Key(climb), value, CalibrationRecordStatus.Used, null, Epoch, Epoch.AddSeconds(positionSeconds), Epoch);

    // A deterministic spread of ±4 s around 16 s, the same formula as the reference run.
    private static long Spread(int i) => 16 * T + (i * 7919L * 1000003L % 80000001L) - 40000000L;

    [Theory]
    [InlineData(-300_000_000, CalibrationRecordStatus.Used, null)]
    [InlineData(-300_000_001, CalibrationRecordStatus.Excluded, CalibrationExclusionReason.BelowBounds)]
    [InlineData(900_000_000, CalibrationRecordStatus.Used, null)]
    [InlineData(900_000_001, CalibrationRecordStatus.Excluded, CalibrationExclusionReason.AboveBounds)]
    [InlineData(0, CalibrationRecordStatus.Used, null)]
    public void The_bounds_are_inclusive(long value, CalibrationRecordStatus status, CalibrationExclusionReason? reason)
    {
        Assert.Equal((status, reason), StartCalibration.Classify(value));
    }

    [Theory]
    [InlineData(new long[] { 10, 13 }, 11)]                                    // S(11) = +1, S(12) = −1: L = 12, U = 11
    [InlineData(new long[] { 10, 10, 13, 9, 9 }, 10)]                         // a crossing without an integer zero
    [InlineData(new long[] { -7, -6, -6, -3 }, -6)]                           // negative values
    [InlineData(new long[] { 50_000_000, 50_000_000, 50_000_000 }, 50_000_000)] // one zero
    [InlineData(new long[] { 0, 0, 0, 120_000_000, 120_000_000, 120_000_000 }, 60_000_000)] // a plateau
    [InlineData(new long[] { 0, 0, 100_000_001, 100_000_001 }, 50_000_000)]   // a plateau, the middle rounded down
    [InlineData(new long[] { 120_000_000, 140_000_000, -30_000_000, 200_000_000, 160_000_007, 89_999_995, 400_000_000 }, 140_000_002)]
    public void The_target_is_the_floor_of_the_middle_of_L_and_U(long[] values, long expected)
    {
        Assert.Equal(expected, StartCalibration.Target(values));
    }

    [Fact]
    public void Below_twenty_records_the_estimate_is_the_initial_value()
    {
        var nineteen = Enumerable.Range(0, 19).Select(i => Used(i, 30 * T, i)).ToList();
        Assert.Equal(StartCalibration.InitialTicks, StartCalibration.Estimate(nineteen));
        Assert.Equal(StartCalibration.InitialTicks, StartCalibration.Estimate([]));
    }

    [Fact]
    public void The_weight_is_n_of_one_hundred()
    {
        var records = Enumerable.Range(0, 20).Select(i => Used(i, Spread(i), i)).ToList();
        Assert.Equal(187_605_002, StartCalibration.Target(records.Select(r => r.Value).ToList()));
        Assert.Equal(133_521_000, StartCalibration.Estimate(records));          // 12 s + ⌊20 · 6.7605002 s / 100⌋
    }

    [Fact]
    public void A_full_window_takes_the_last_hundred_and_its_target()
    {
        var records = Enumerable.Range(0, 150).Select(i => Used(i, Spread(i), i)).ToList();
        Assert.Equal(157_253_972, StartCalibration.Estimate(records));
        Assert.Equal(Enumerable.Range(50, 100).Select(i => Key(i)), StartCalibration.WindowOf(records).Select(r => r.Key));
    }

    [Fact]
    public void A_negative_numerator_is_rounded_towards_minus_infinity()
    {
        // Target = I − 3 ticks with n = 20: ⌊20 · (−3) / 100⌋ = ⌊−0.6⌋ = −1 (truncation would give 0).
        var records = Enumerable.Range(0, 20).Select(i => Used(i, StartCalibration.InitialTicks - 3, i)).ToList();
        Assert.Equal(StartCalibration.InitialTicks - 1, StartCalibration.Estimate(records));
        var low = Enumerable.Range(0, 50).Select(i => Used(i, 8 * T + i * 1000L, i)).ToList();
        Assert.Equal(100_012_250, StartCalibration.Estimate(low));
    }

    [Fact]
    public void Ties_of_the_position_time_are_ordered_by_the_key()
    {
        var records = Enumerable.Range(0, 120).Select(i => Used(i, Spread(i), 0)).ToList();
        var window = StartCalibration.WindowOf(records).Select(r => r.Key).ToList();
        Assert.Equal(records.Select(r => r.Key).OrderBy(k => k).Skip(20), window);
    }

    [Fact]
    public void Only_used_records_enter_and_a_removal_lets_older_ones_back()
    {
        var records = Enumerable.Range(0, 150).Select(i => Used(i, Spread(i), i)).ToList();
        var removedNewest = records.Select((r, i) => i >= 120 ? r with { Status = CalibrationRecordStatus.Removed } : r).ToList();
        var fresh = records.Take(120).ToList();
        Assert.Equal(StartCalibration.Estimate(fresh), StartCalibration.Estimate(removedNewest));
        var excluded = records.Select((r, i) => i >= 120 ? r with { Status = CalibrationRecordStatus.Excluded, Reason = CalibrationExclusionReason.AboveBounds } : r).ToList();
        Assert.Equal(StartCalibration.Estimate(fresh), StartCalibration.Estimate(excluded));
    }

    [Fact]
    public void A_record_with_an_older_position_time_takes_its_place_by_that_time()
    {
        // D051 §5.8 / §6.5.1 (Q2): a clamped mark linked without a change becomes used with its old positionChangedAt.
        var old = Used(1000, 40 * T, 0);
        var hundredNewer = Enumerable.Range(0, 100).Select(i => Used(i, Spread(i), 10 + i)).ToList();
        Assert.Equal(StartCalibration.Estimate(hundredNewer), StartCalibration.Estimate(hundredNewer.Append(old)));
        Assert.DoesNotContain(old, StartCalibration.WindowOf(hundredNewer.Append(old)));

        var ninetyNineNewer = hundredNewer.Take(99).ToList();
        Assert.Contains(old, StartCalibration.WindowOf(ninetyNineNewer.Append(old)));
        Assert.NotEqual(StartCalibration.Estimate(ninetyNineNewer), StartCalibration.Estimate(ninetyNineNewer.Append(old)));
    }

    [Fact]
    public void Any_order_of_the_records_gives_the_same_estimate()
    {
        var records = Enumerable.Range(0, 180).Select(i => Used(i, Spread(i), i % 37)).ToList();
        records.Add(new CalibrationRecord(Key(500), -160 * T, CalibrationRecordStatus.Excluded, CalibrationExclusionReason.BelowBounds, Epoch, Epoch, Epoch));
        var expected = StartCalibration.Estimate(records);
        var random = new Random(37);
        for (var run = 0; run < 50; run++)
            Assert.Equal(expected, StartCalibration.Estimate(records.OrderBy(_ => random.Next()).ToList()));
    }

    [Fact]
    public void The_key_text_orders_like_its_numbers()
    {
        Assert.True(Key(2).CompareTo(Key(10)) < 0);
        var earlier = new TrainingActivity(Epoch.AddDays(-1), Epoch, 294, null);
        Assert.True(Key(99, earlier).CompareTo(Key(0)) < 0);
        Assert.Equal(Key(3), Key(3));
    }
}
