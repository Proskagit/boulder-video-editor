using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Playback;
using Xunit;

namespace AiVideoEditor.Core.Tests;

public class PlaybackClockTests
{
    private sealed class FakeReference : IReferenceClock
    {
        public FakeReference(long unitsPerSecond, long start = 0) => (UnitsPerSecond, Value) = (unitsPerSecond, start);
        public long Value { get; set; }
        public long UnitsPerSecond { get; }
        public ReferenceTime Now => new(Value, UnitsPerSecond);
    }

    [Fact]
    public void Paused_DoesNotAdvance_Running_AdvancesByElapsedReference()
    {
        var reference = new FakeReference(1_000, start: 5_000);
        var clock = new PlaybackClock(reference);

        reference.Value += 700;
        Assert.Equal(MediaTime.Zero, clock.Position);

        clock.Start();
        reference.Value += 1_500;
        Assert.Equal(MediaTime.FromSeconds(1.5), clock.Position);
    }

    [Fact]
    public void AudioSamples_ExactIntegerConversion_NoAccumulatedError()
    {
        // 48 kHz: one sample = 208⅓ ticks. Reading after every sample must not accumulate
        // 208-tick steps: the position is always floor(total · 10⁷ / 48000).
        var audio = new FakeReference(48_000);
        var clock = new PlaybackClock(audio);
        clock.Start();

        for (var i = 1; i <= 1_000_000; i++)
        {
            audio.Value++;
            if (i % 99_991 == 0)
                Assert.Equal((long)i * 10_000_000 / 48_000, clock.Position.Ticks);
        }
        Assert.Equal(208_333_333, clock.Position.Ticks);
    }

    [Fact]
    public void StopwatchFrequencyNotDividingTicks_StaysExactOverADay()
    {
        // 3 579 545 Hz (ACPI-style QPC): one unit = 2.793… ticks. After 24 h the position must be
        // exactly floor(units · 10⁷ / f), and a whole number of seconds must land exactly.
        const long frequency = 3_579_545;
        var reference = new FakeReference(frequency, start: 987_654_321_000);
        var clock = new PlaybackClock(reference);
        clock.Start();

        reference.Value += frequency * 86_400;
        Assert.Equal(MediaTime.FromSeconds(86_400), clock.Position);

        reference.Value += 1;
        Assert.Equal(864_000_000_000L + 10_000_000 / frequency, clock.Position.Ticks);
    }

    [Fact]
    public void Seek_CreatesNewAnchor_WhileRunning_AndWhilePaused()
    {
        var reference = new FakeReference(1_000);
        var clock = new PlaybackClock(reference);
        clock.Start();
        reference.Value += 2_000;

        clock.Seek(MediaTime.FromSeconds(10));
        Assert.Equal(MediaTime.FromSeconds(10), clock.Position);
        reference.Value += 250;
        Assert.Equal(MediaTime.FromSeconds(10.25), clock.Position);

        clock.Pause();
        clock.Seek(MediaTime.FromSeconds(3));
        reference.Value += 999;
        Assert.Equal(MediaTime.FromSeconds(3), clock.Position);
    }

    [Fact]
    public void PauseAndStart_ContinueFromTheSamePosition()
    {
        var reference = new FakeReference(1_000);
        var clock = new PlaybackClock(reference);
        clock.Start();
        reference.Value += 400;
        clock.Pause();
        reference.Value += 10_000;
        clock.Start();
        reference.Value += 100;

        Assert.Equal(MediaTime.FromSeconds(0.5), clock.Position);
    }

    [Fact]
    public void SwitchingBetweenStopwatchAndAudio_KeepsPosition_ThenFollowsTheNewReference()
    {
        var stopwatch = new FakeReference(10_000_000, start: 123_456_789);
        var audio = new FakeReference(48_000, start: 777);
        var clock = new PlaybackClock(stopwatch);
        clock.Start();
        stopwatch.Value += 12_345_678; // 1.2345678 s

        var before = clock.Position;
        clock.SetMaster(audio);
        Assert.True(clock.HasMaster);
        Assert.Equal(before, clock.Position);

        stopwatch.Value += 99_999_999; // ignored now
        audio.Value += 48_000;
        Assert.Equal(before + MediaTime.FromSeconds(1), clock.Position);

        var beforeBack = clock.Position;
        clock.SetMaster(null);
        Assert.Equal(beforeBack, clock.Position);
        stopwatch.Value += 5_000_000;
        Assert.Equal(beforeBack + MediaTime.FromSeconds(0.5), clock.Position);
    }

    [Fact]
    public void SwitchingWhilePaused_KeepsPosition()
    {
        var clock = new PlaybackClock(new FakeReference(1_000));
        clock.Seek(MediaTime.FromSeconds(7));
        clock.SetMaster(new FakeReference(44_100));
        Assert.Equal(MediaTime.FromSeconds(7), clock.Position);
    }
}
