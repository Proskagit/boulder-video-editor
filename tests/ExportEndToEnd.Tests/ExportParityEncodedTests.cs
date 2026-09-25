using System.Diagnostics;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Playback;
using AiVideoEditor.Timeline.Playback;
using AiVideoEditor.Video;
using AiVideoEditor.Video.Tests;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;
using static AiVideoEditor.ExportEndToEnd.Tests.EndToEnd;

namespace AiVideoEditor.ExportEndToEnd.Tests;

/// <summary>
/// Phase 8 Step 8.5 (A4): Preview ↔ Export parity through the codec — the finished MP4, decoded by ffmpeg (honouring its BT.709
/// tags), against the Preview on the same snapshot.
/// <list type="bullet">
/// <item>Picture, the MP4 → Preview leg (decision 5c): the MP4's frames against the Preview's own pipeline and control at the
/// canvas size (<see cref="ParityMetrics"/>) — geometry (at most 0.01 % of the pixels outside the ±1 px luma mask, the lossy
/// codec's ringing at thin dark detail; decision 5a), bar edges ±1 px and the same source frame (strict). Colour is not a
/// criterion on this leg: the strict Preview ↔ export colour parity is Steps 8.2–8.4, and the MP4 → export canvas leg (the codec's
/// own colour fidelity) needs a numeric tolerance D023 does not give yet. Whole-frame mean |Δ| and PSNR are reported only.</item>
/// <item>Sound: the MP4's AAC track against the Preview's own audio (its <c>AudioPipeline</c> and <c>AudioMixer</c> with the real
/// ffmpeg decoder, playing from the start): the same length, timing within the 10 ms of D022 (whole-signal cross-correlation lag and
/// every burst onset), and SNR ≥ 20 dB as a sanity bound (the AAC round trip; the Step 6 end-to-end tests accepted 20–25 dB).
/// The left channel is compared (the sources are identical on both channels or mono).</item>
/// </list>
/// </summary>
[Collection(EndToEndCollection.Name)]
public sealed class ExportParityEncodedTests
{
    private const int Rate = AudioFormat.SampleRate;
    private const int TimingTolerance = Rate / 100;          // 10 ms (D022)
    private const double MinSnrDb = 20;
    private const string Red = "C83232";
    private readonly E2EMedia _media;
    private readonly ITestOutputHelper _output;

    public ExportParityEncodedTests(E2EMedia media, ITestOutputHelper output)
    {
        _media = media;
        _output = output;
    }

    private async Task<ExportRun> Export(ProjectBuilder p, string name)
    {
        var run = await ExportProject(p.Project, Path.Combine(_media.OutputFolder(), name + ".mp4"));
        AssertValidMp4(run);
        return run;
    }

    // --- Picture ------------------------------------------------------------------------------------------------------

    /// <param name="moving">False for a scene whose frames are identical by construction (still layers): there the same-frame
    /// check can't tell frames apart and is skipped.</param>
    private async Task AssertEncodedPicture(ExportRun run, string scene, long[] frames, bool checkBars, bool moving = true)
    {
        var (w, h) = (run.Output.Size.Width, run.Output.Size.Height);
        var mp4 = EncoderHarness.DecodeFrames(run.Path, w, h);
        Assert.Equal(run.Output.FrameCount, mp4.Count);
        foreach (var n in frames)
        {
            var preview = (await Preview(run.Job.Snapshot, n)).Pixels;
            var encoded = mp4[(int)n];
            var what = $"{scene}, frame {n} (MP4 vs Preview)";

            var geometry = ParityMetrics.AssertGeometryThroughCodec(preview, encoded, w, h, what);
            if (checkBars) ParityMetrics.AssertBarEdges(preview, encoded, w, h, what);
            if (moving) ParityMetrics.AssertSameSourceFrame(preview, mp4, n, w, h, what);

            var (mean, psnr) = ParityMetrics.WholeFrame(preview, encoded);
            _output.WriteLine($"{what}: geometry outside {geometry:P4}; whole frame mean |Δ| {mean:0.00}, PSNR {psnr:0.0} dB (reported only)");
        }
        AssertNoFfmpegLeft();
    }

    [FfmpegFact]
    public async Task Png_alpha_over_video_through_the_codec()
    {
        var p = new ProjectBuilder(FrameRate.Fps25);
        p.Video(p.VideoTrack(), _media.Solid(Red, FrameRate.Fps25), 0, 25);
        p.Image(p.VideoTrack(), _media.PngAlpha(), 0, 25);
        var run = await Export(p, "enc-png-alpha");

        await AssertEncodedPicture(run, "png-alpha", new long[] { 0, 12, 24 }, checkBars: false, moving: false);   // solid colour + still
    }

    [FfmpegFact]
    public async Task Rotated_video_on_a_portrait_canvas_through_the_codec()
    {
        var p = new ProjectBuilder(FrameRate.Fps25, 180, 320);
        p.Video(p.VideoTrack(), _media.Rotated90(), 0, 25);
        var run = await Export(p, "enc-rotated");

        await AssertEncodedPicture(run, "rotated portrait", new long[] { 0, 12, 24 }, checkBars: false);
    }

    [FfmpegFact]
    public async Task Video_at_4x_through_the_codec()
    {
        var p = new ProjectBuilder(FrameRate.Fps25);
        p.Video(p.VideoTrack(), _media.Pattern(FrameRate.Fps25, seconds: 6), 0, 25, sourceInFrame: 10, speed: ClipSpeed.FromSteps(80));
        var run = await Export(p, "enc-speed-4x");

        await AssertEncodedPicture(run, "4×", new long[] { 0, 7, 13, 24 }, checkBars: true);
    }

    [FfmpegFact]
    public async Task Mixed_layers_through_the_codec()
    {
        var p = new ProjectBuilder(FrameRate.Ntsc30);
        p.Video(p.VideoTrack(), _media.Pattern(FrameRate.Fps25, seconds: 6), 0, 30, speed: ClipSpeed.FromSteps(27));
        var rotated = p.Video(p.VideoTrack(), _media.Rotated90(), 0, 30);
        (rotated.Scale, rotated.PositionX, rotated.RotationDegrees) = (0.6, 90, 10);
        var png = p.Image(p.VideoTrack(), _media.PngAlpha(), 5, 30);
        (png.Scale, png.RotationDegrees, png.PositionY) = (0.5, -15, 40);
        var jpeg = p.Image(p.VideoTrack(), _media.Jpeg(), 0, 20);
        (jpeg.Scale, jpeg.PositionX, jpeg.PositionY, jpeg.Opacity) = (0.3, -110, -60, 0.7);
        var text = p.Text(p.VideoTrack(), "Step 8.5", 10, 30);
        (text.FontSize, text.ColorHex) = (24, "#80FF80");
        var run = await Export(p, "enc-mixed");

        await AssertEncodedPicture(run, "mixed", new long[] { 0, 5, 15, 29 }, checkBars: false);
    }

    [FfmpegFact]
    public async Task Source_1080p_through_the_codec()
    {
        var p = new ProjectBuilder(FrameRate.Fps25, 1920, 1080);
        p.Video(p.VideoTrack(), _media.Pattern1080(), 0, 5);
        var run = await Export(p, "enc-1080p");

        await AssertEncodedPicture(run, "1080p full canvas", new long[] { 0, 2, 4 }, checkBars: true);
    }

    // --- Sound --------------------------------------------------------------------------------------------------------

    /// <summary>The Preview's audio of the whole snapshot (left channel): its pipeline and mixer, playing from sample 0.</summary>
    private static async Task<float[]> PreviewAudio(PlaybackSnapshot snapshot, long samples)
    {
        var decoder = new FfmpegAudioDecoder(FfmpegTools.FfmpegLocator, NullLogger<FfmpegAudioDecoder>.Instance);
        var left = new float[samples];
        var mixer = new AudioMixer();
        mixer.Reset(0);
        await using (var pipeline = new AudioPipeline(snapshot, 1, 0, mixer, decoder, new PlaybackSettings(), NullLogger.Instance))
        {
            var chunk = new float[2 * 4_800];
            for (long from = 0; from < samples; from += 4_800)
            {
                var n = (int)Math.Min(4_800, samples - from);
                pipeline.Maintain(from);
                var watch = Stopwatch.StartNew();
                while (!pipeline.HasData(from, from + n))
                {
                    if (watch.Elapsed > TimeSpan.FromSeconds(30)) throw new TimeoutException($"preview audio at {from}");
                    await Task.Delay(1);
                }
                mixer.Read(chunk.AsSpan(0, 2 * n));
                for (var i = 0; i < n; i++) left[from + i] = chunk[2 * i];
            }
        }
        return left;
    }

    private static double Snr(float[] reference, float[] other)
    {
        double signal = 0, error = 0;
        for (var i = 0; i < reference.Length; i++) { signal += (double)reference[i] * reference[i]; error += Math.Pow(reference[i] - other[i], 2); }
        return error == 0 ? double.PositiveInfinity : 10 * Math.Log10(signal / error);
    }

    /// <summary>The lag (in samples, −max … max) at which <paramref name="b"/> best matches <paramref name="a"/>.</summary>
    private static int BestLag(float[] a, float[] b, int max)
    {
        var best = 0; var bestValue = double.MinValue;
        for (var lag = -max; lag <= max; lag++)
        {
            double sum = 0;
            for (var i = Math.Max(0, -lag); i < Math.Min(a.Length, b.Length - lag); i++) sum += (double)a[i] * b[i + lag];
            if (sum > bestValue) { bestValue = sum; best = lag; }
        }
        return best;
    }

    /// <summary>First sample of every burst: |x| &gt; 0.3 after at least 20 ms below it.</summary>
    private static List<int> Onsets(float[] x)
    {
        var onsets = new List<int>();
        var quiet = int.MaxValue;
        for (var i = 0; i < x.Length; i++)
        {
            if (Math.Abs(x[i]) > 0.3)
            {
                if (quiet >= Rate / 50) onsets.Add(i);
                quiet = 0;
            }
            else if (quiet != int.MaxValue) quiet++;
        }
        return onsets;
    }

    private async Task AssertEncodedSound(ExportRun run, string scene, int minBursts)
    {
        var samples = run.Output.AudioSampleCount;
        var preview = await PreviewAudio(run.Job.Snapshot, samples);
        var mp4 = EncoderHarness.DecodeLeft(run.Path);
        Assert.Equal(samples, mp4.Length);

        var snr = Snr(preview, mp4);
        var lag = BestLag(preview, mp4, 2 * TimingTolerance);
        var p = Onsets(preview);
        var e = Onsets(mp4);
        var pairs = p.Select(o => (Preview: o, Mp4: e.Count == 0 ? int.MaxValue : e.MinBy(x => Math.Abs(x - o)))).ToList();
        _output.WriteLine($"{scene}: SNR {snr:0.0} dB, lag {lag} samples ({lag * 1000.0 / Rate:0.00} ms), bursts Preview {p.Count} / MP4 {e.Count}, " +
                          $"onset Δ (ms): {string.Join(" ", pairs.Select(x => ((x.Mp4 - x.Preview) * 1000.0 / Rate).ToString("0.00")))}");

        Assert.True(snr >= MinSnrDb, $"{scene}: SNR {snr:0.0} dB");
        Assert.InRange(lag, -TimingTolerance, TimingTolerance);
        Assert.True(p.Count >= minBursts, $"{scene}: only {p.Count} bursts in the Preview's audio");
        Assert.Equal(p.Count, e.Count);
        Assert.All(pairs, x => Assert.InRange(x.Mp4 - x.Preview, -TimingTolerance, TimingTolerance));
        AssertNoFfmpegLeft();
    }

    [FfmpegFact]
    public async Task Sound_of_a_video_clip_through_the_codec()
    {
        var p = new ProjectBuilder(FrameRate.Fps25);
        p.Video(p.VideoTrack(), _media.Sync(FrameRate.Fps25), 5, 100);
        var run = await Export(p, "enc-sound-video");

        await AssertEncodedSound(run, "video clip at 1×", minBursts: 3);
    }

    [FfmpegTheory]
    [InlineData(5)]     // 0.25×
    [InlineData(40)]    // 2×
    public async Task Sound_at_speed_through_the_codec(int steps)
    {
        var p = new ProjectBuilder(FrameRate.Fps25);
        var frames = steps == 5 ? 100 : 45;                // 1 s / 3.6 s of source
        p.Audio(p.AudioTrack(), _media.Bursts(), 0, frames, speed: ClipSpeed.FromSteps(steps));
        var run = await Export(p, $"enc-sound-speed-{steps}");

        await AssertEncodedSound(run, $"bursts at {ClipSpeed.FromSteps(steps)}", minBursts: 2);
    }

    [FfmpegFact]
    public async Task Mixed_sound_through_the_codec()
    {
        var p = new ProjectBuilder(FrameRate.Fps25);
        p.Audio(p.AudioTrack(), _media.Bursts(), 0, 100).Volume = 0.8;
        p.Audio(p.AudioTrack(), _media.Tone(), 50, 100).Volume = 0.5;
        p.Audio(p.AudioTrack(), _media.Tone(), 0, 100).IsMuted = true;
        p.Video(p.VideoTrack(hidden: true), _media.Sync(FrameRate.Fps25), 10, 100);
        var run = await Export(p, "enc-sound-mixed");

        await AssertEncodedSound(run, "mixed", minBursts: 4);
    }
}
