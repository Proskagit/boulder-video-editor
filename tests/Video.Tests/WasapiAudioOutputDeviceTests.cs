using System.Diagnostics;
using AiVideoEditor.Audio;
using AiVideoEditor.Core.Playback;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace AiVideoEditor.Video.Tests;

/// <summary>
/// WasapiAudioOutput on the real default device, on an STA thread like the UI thread, playing
/// silence. Passes (with a note) when the machine has no usable audio device.
/// </summary>
public sealed class WasapiAudioOutputDeviceTests
{
    private readonly ITestOutputHelper _output;
    public WasapiAudioOutputDeviceTests(ITestOutputHelper output) => _output = output;

    private sealed class CountingSilence : IAudioSampleSource
    {
        private long _frames;
        public long Frames => Interlocked.Read(ref _frames);
        public void Read(Span<float> interleaved)
        {
            interleaved.Clear();
            Interlocked.Add(ref _frames, interleaved.Length / AudioFormat.Channels);
        }
    }

    [Fact]
    public void Clock_CountsPlayedFrames_FreezesOnStop_AndIsCumulativeAcrossSessions()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { Scenario(); } catch (Exception ex) { failure = ex; }
        });
        if (OperatingSystem.IsWindows()) thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw new Exception("device scenario failed", failure);
    }

    [Fact]
    public void Clock_IsMonotonic_AcrossManyStartStopCycles()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { Cycles(); } catch (Exception ex) { failure = ex; }
        });
        if (OperatingSystem.IsWindows()) thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw new Exception("device scenario failed", failure);
    }

    private void Cycles()
    {
        using var output = new WasapiAudioOutput(NullLogger<WasapiAudioOutput>.Instance);
        var source = new CountingSilence();
        long previous = 0, samples = 0;
        void Sample()
        {
            var now = output.Clock.Now.Value;
            Assert.True(now >= previous, $"clock ran backwards: {previous} → {now}");
            previous = now;
            samples++;
        }

        for (var cycle = 0; cycle < 6; cycle++)
        {
            if (!output.TryStart(source))
            {
                _output.WriteLine("No usable audio device: nothing to verify on this machine.");
                return;
            }
            Sample(); // right after Start: the new session reports 0, the base keeps the total
            var watch = Stopwatch.StartNew();
            var length = 40 + 30 * cycle;
            while (watch.ElapsedMilliseconds < length) { Sample(); Thread.Sleep(2); }

            output.Stop();
            Sample();
            var frozen = output.Clock.Now.Value;
            Thread.Sleep(20);
            Assert.Equal(frozen, output.Clock.Now.Value);
        }

        _output.WriteLine($"6 sessions, {samples} readings, final position {previous} frames ({previous / 48.0:0} ms)");
        Assert.True(previous > 0);
        Assert.False(output.HasFailed);
    }

    private void Scenario()
    {
        using var output = new WasapiAudioOutput(NullLogger<WasapiAudioOutput>.Instance);
        var source = new CountingSilence();
        if (!output.TryStart(source))
        {
            _output.WriteLine("No usable audio device: nothing to verify on this machine.");
            return;
        }

        var watch = Stopwatch.StartNew();
        long previous = 0;
        while (watch.ElapsedMilliseconds < 500)
        {
            var now = output.Clock.Now;
            Assert.Equal(AudioFormat.SampleRate, now.UnitsPerSecond);
            Assert.True(now.Value >= previous, "clock ran backwards");
            previous = now.Value;
            Thread.Sleep(5);
        }
        var played = output.Clock.Now.Value;
        var elapsedFrames = watch.Elapsed.TotalSeconds * AudioFormat.SampleRate;
        _output.WriteLine($"session 1: played {played}, pulled {source.Frames}, wall clock {elapsedFrames:0}");
        Assert.InRange(played, elapsedFrames - 0.25 * AudioFormat.SampleRate, elapsedFrames + 0.02 * AudioFormat.SampleRate);
        Assert.True(played < source.Frames, "the clock must count played frames, not frames handed to the device");

        output.Stop();
        var stopped = output.Clock.Now.Value;
        Assert.True(stopped >= played);
        Thread.Sleep(200);
        Assert.Equal(stopped, output.Clock.Now.Value); // frozen while stopped

        Assert.True(output.TryStart(source));
        Thread.Sleep(300);
        var resumed = output.Clock.Now.Value;
        _output.WriteLine($"after Stop: {stopped}; 300 ms into session 2: {resumed}");
        Assert.InRange(resumed - stopped, (long)(0.05 * AudioFormat.SampleRate), (long)(0.32 * AudioFormat.SampleRate));
        output.Stop();
        Assert.False(output.HasFailed);
    }
}
