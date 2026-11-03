using AiVideoEditor.Core.Telemetry.Calibration;
using Xunit;
using static AiVideoEditor.Core.Tests.Telemetry.Calibration.StartCalibrationTests;

namespace AiVideoEditor.Core.Tests.Telemetry.Calibration;

/// <summary>
/// D051 §10 T1, the deterministic synthetic runs (the appendix of D051): 20 climbs per training, 10 % moved by the user
/// to a true lag N(truth, 4 s) plus 0.5 s of marking noise, the rest kept at the estimate they were proposed with. The
/// estimate reaches within 1 s of the truth and keeps no lasting bias — for a constant 16 s and for 16 → 10 → 16 s.
/// A fixed generator, so every run gives the same numbers.
/// </summary>
public class StartCalibrationSimulationTests
{
    private const double TicksPerSecond = 10_000_000;

    private sealed class Gauss(ulong seed)
    {
        private ulong _state = seed;

        private double Uniform()
        {
            _state ^= _state << 13; _state ^= _state >> 7; _state ^= _state << 17;          // xorshift64
            return ((_state >> 11) + 0.5) / (1UL << 53);
        }

        public double Next(double mean, double sd) =>
            mean + sd * Math.Sqrt(-2 * Math.Log(Uniform())) * Math.Cos(2 * Math.PI * Uniform());

        public bool Chance(double p) => Uniform() < p;
    }

    private static List<long> Run(Func<int, double> truthSeconds, int trainings, ulong seed)
    {
        var random = new Gauss(seed);
        var records = new List<CalibrationRecord>();
        var estimate = StartCalibration.InitialTicks;
        var path = new List<long> { estimate };
        var clock = 0;
        for (var training = 0; training < trainings; training++)
        {
            for (var climb = 0; climb < 20; climb++)
            {
                clock++;
                var value = random.Chance(0.10)
                    ? (long)Math.Round((random.Next(truthSeconds(training), 4) + random.Next(0, 0.5)) * TicksPerSecond)
                    : estimate;
                var (status, reason) = StartCalibration.Classify(value);
                var at = DateTimeOffset.UnixEpoch.AddSeconds(clock);
                records.Add(new CalibrationRecord(Key(clock), value, status, reason, at, at, at));
            }
            estimate = StartCalibration.Estimate(records);
            path.Add(estimate);
        }
        return path;
    }

    private static double Seconds(long ticks) => ticks / TicksPerSecond;

    [Fact]
    public void A_constant_lag_of_16_s_is_reached_within_1_s_without_a_lasting_bias()
    {
        var path = Run(_ => 16, 300, 37);
        var firstWithin = path.FindIndex(e => Math.Abs(Seconds(e) - 16) <= 1);
        Assert.InRange(firstWithin, 1, 150);
        var tail = path.Skip(200).Select(Seconds).ToList();
        Assert.InRange(tail.Average(), 15.5, 16.5);
        Assert.All(tail, e => Assert.InRange(e, 15, 17));
    }

    [Fact]
    public void A_step_16_10_16_s_is_followed_within_1_s_each_time()
    {
        double Truth(int t) => t < 300 || t >= 600 ? 16 : 10;
        var path = Run(Truth, 900, 11);
        Assert.InRange(path.Skip(200).Take(100).Select(Seconds).Average(), 15.5, 16.5);
        Assert.InRange(path.Skip(500).Take(100).Select(Seconds).Average(), 9.5, 10.5);
        Assert.InRange(path.Skip(800).Take(101).Select(Seconds).Average(), 15.5, 16.5);
        Assert.InRange(Seconds(path[600]), 9, 11);
        Assert.InRange(Seconds(path[900]), 15, 17);
    }
}
