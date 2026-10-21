extern alias ProjectPersistence;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Playback;
using AiVideoEditor.Video.Tests;
using ProjectPersistence::AiVideoEditor.Project.Persistence;
using Xunit;
using Xunit.Abstractions;
using static AiVideoEditor.ExportEndToEnd.Tests.EndToEnd;

namespace AiVideoEditor.ExportEndToEnd.Tests;

/// <summary>
/// Phase 29 Step 29.2 (D045 §7, <c>docs/PHASE29_MANUAL_TEST_PLAN.md</c> part B): the audit of the existing music scenario
/// with objective criteria — the existing path only, nothing new. Reference signals generated here: 40 ms 1 kHz bursts at
/// 0.8 every whole second from 0.5 s (a gap, a doubled block, an offset or a drift is measurable on them) and a 440 Hz sine
/// at 0.25 (levels, fades, clicks), as MP3 / AAC (M4A) / FLAC at 44.1 kHz and PCM at 48 kHz. Timing is read on the PCM the
/// service hands the encoder and on the decoded MP4; the Preview's mix equals the export's sample for sample
/// (<c>ExportAudioIntegrationTests</c>), so these hold for the Preview too.
/// </summary>
[Collection(EndToEndCollection.Name)]
public sealed class MusicScenarioAuditTests
{
    private const int Rate = AudioFormat.SampleRate;
    private const double BurstCentre = 0.02;   // a 40 ms cosine burst's energy centroid, from its start
    private readonly E2EMedia _media;
    private readonly ITestOutputHelper _output;

    public MusicScenarioAuditTests(E2EMedia media, ITestOutputHelper output)
    {
        _media = media;
        _output = output;
    }

    private const string Bursts = @"0.8*cos(2*PI*1000*mod(t-0.5\,1))*lt(mod(t-0.5\,1)\,0.04)*gte(t\,0.5)";

    private MediaAsset Clicks(string format, int sampleRate = 44100, int seconds = 8, string? quality = null)
    {
        var codec = format switch
        {
            "mp3" => new[] { "-c:a", "libmp3lame", quality is null ? "-b:a" : "-q:a", quality ?? "192k" },
            "m4a" => new[] { "-c:a", "aac", "-b:a", "192k" },
            "flac" => new[] { "-c:a", "flac" },
            _ => new[] { "-c:a", "pcm_s16le" }
        };
        return _media.Generated($"music-clicks-{sampleRate}-{seconds}s{(quality is null ? "" : "-vbr" + quality)}.{format}", MediaKind.Audio,
            new[] { "-f", "lavfi", "-i", $"aevalsrc={Bursts}|{Bursts}:s={sampleRate}:d={seconds}" }.Concat(codec).ToArray());
    }

    private MediaAsset Sine(string format, double amplitude = 0.25, int sampleRate = 44100)
    {
        var codec = format == "mp3" ? new[] { "-c:a", "libmp3lame", "-b:a", "320k" } : new[] { "-c:a", "pcm_s16le" };
        var a = amplitude.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return _media.Generated($"music-sine-{a}-{sampleRate}.{format}", MediaKind.Audio,
            new[] { "-f", "lavfi", "-i", $"aevalsrc={a}*sin(2*PI*440*t)|{a}*sin(2*PI*440*t):s={sampleRate}:d=8" }.Concat(codec).ToArray());
    }

    private Task<ExportRun> Export(ProjectBuilder p, string name) =>
        ExportProject(p.Project, Path.Combine(_media.OutputFolder(), name + ".mp4"));

    /// <summary>A 25 fps project with the test pattern on V1 for the first 4 s (the video the music goes under).</summary>
    private ProjectBuilder WithVideo(long videoFrames = 100)
    {
        var p = new ProjectBuilder(FrameRate.Fps25);
        p.Video(p.VideoTrack(), _media.Pattern(), 0, Math.Min(videoFrames, 100));
        return p;
    }

    /// <summary>Each burst's offset (ms) from where it should start: <c>first + k</c> seconds on the timeline.</summary>
    private List<double> Offsets(float[] left, double first)
    {
        var starts = EncoderHarness.Centroids(left).Select(c => c / Rate - BurstCentre).ToList();
        return starts.Select((s, k) => (s - (first + k)) * 1000).ToList();
    }

    private static float[] Slice(float[] x, double from, double to) => x[(int)(from * Rate)..(int)Math.Min(x.Length, to * Rate)];

    private static double Db(double ratio) => 20 * Math.Log10(ratio);

    public static TheoryData<string, int> Formats => new() { { "mp3", 44100 }, { "m4a", 44100 }, { "flac", 44100 }, { "wav", 48000 } };

    /// <summary>B1 / B2 / B7: a music file of each format under the video at 0 — every burst at its time (± 1 ms) on the
    /// encoder's PCM and in the MP4, one per second, none missing or doubled; the export as long as the timeline.</summary>
    [FfmpegTheory]
    [MemberData(nameof(Formats))]
    public async Task Music_of_each_format_is_placed_on_time(string format, int sampleRate)
    {
        var p = WithVideo();
        p.Audio(p.AudioTrack(), Clicks(format, sampleRate), 0, 150);                 // 6 s of music under 4 s of video
        var run = await Export(p, $"b1-{format}");

        AssertValidMp4(run);
        var encoded = Offsets(Left(run.Pcm), 0.5);
        var decoded = Offsets(EncoderHarness.DecodeLeft(run.Path), 0.5);
        _output.WriteLine($"{format} {sampleRate}: PCM offsets (ms) {string.Join(" ", encoded.Select(d => d.ToString("0.00")))}; MP4 {string.Join(" ", decoded.Select(d => d.ToString("0.00")))}");
        Assert.Equal(6, encoded.Count);
        Assert.Equal(6, decoded.Count);
        Assert.All(encoded, d => Assert.InRange(d, -1.0, 1.0));
        Assert.All(decoded, d => Assert.InRange(d, -1.0, 1.0));
        Assert.InRange(encoded[^1] - encoded[0], -0.5, 0.5);                         // no drift
    }

    /// <summary>B3: the volume — 0 % digital silence, 50 % −6.02 dB, 200 % +6.02 dB (± 0.1 dB) against 100 %, a 44.1 kHz
    /// MP3 resampled to 48 kHz; the overload of 200 % on a loud source is recorded (no limiter exists, D042 §3).</summary>
    [FfmpegFact]
    public async Task Volume_levels_are_exact()
    {
        double RmsAt(ExportRun run) => Rms(Slice(Left(run.Pcm), 1.0, 3.0));
        async Task<ExportRun> At(double volume, MediaAsset music)
        {
            var p = WithVideo();
            p.Audio(p.AudioTrack(), music, 0, 100).Volume = volume;
            return await Export(p, $"b3-{volume}");
        }

        var sine = Sine("mp3");
        var full = RmsAt(await At(1.0, sine));
        var silent = await At(0.0, sine);
        var half = RmsAt(await At(0.5, sine));
        var twice = RmsAt(await At(2.0, sine));
        _output.WriteLine($"100 %: RMS {full:0.0000} (expected 0.25/√2 = 0.1768); 50 %: {Db(half / full):0.000} dB; 200 %: {Db(twice / full):0.000} dB");
        Assert.InRange(full, 0.170, 0.183);
        Assert.All(Left(silent.Pcm), v => Assert.Equal(0f, v));
        Assert.InRange(Db(half / full), -6.12, -5.92);
        Assert.InRange(Db(twice / full), 5.92, 6.12);

        var loud = await At(2.0, Sine("mp3", 0.8));
        var peak = Left(loud.Pcm).Max(Math.Abs);
        var decodedPeak = EncoderHarness.DecodeLeft(loud.Path).Max(Math.Abs);
        _output.WriteLine($"200 % of a 0.8 sine: encoder PCM peak {peak:0.000}, MP4 peak {decodedPeak:0.000} (> 1 means clipped in the file)");
    }

    /// <summary>B4: a trimmed start shifts the music exactly; a split of the music into two clips plays sample-identical to
    /// the unsplit clip (no gap, no doubled block, no click at the cut); a removed middle part leaves exact silence and the
    /// rest on time.</summary>
    [FfmpegFact]
    public async Task Trims_splits_and_removed_parts_are_exact()
    {
        var clicks = Clicks("mp3");

        var trimmed = WithVideo();
        trimmed.Audio(trimmed.AudioTrack(), clicks, 0, 100, sourceInFrame: 8);       // the first 0.32 s cut away
        var t = await Export(trimmed, "b4-trim");
        var trimOffsets = Offsets(Left(t.Pcm), 0.18);
        _output.WriteLine($"trim 0.32 s: offsets (ms) {string.Join(" ", trimOffsets.Select(d => d.ToString("0.00")))}");
        Assert.Equal(4, trimOffsets.Count);
        Assert.All(trimOffsets, d => Assert.InRange(d, -1.0, 1.0));

        var whole = WithVideo();
        whole.Audio(whole.AudioTrack(), clicks, 0, 150);
        var split = WithVideo();
        var a1 = split.AudioTrack();
        split.Audio(a1, clicks, 0, 62);
        split.Audio(a1, clicks, 62, 150, sourceInFrame: 62);
        var w = await Export(whole, "b4-whole");
        var s = await Export(split, "b4-split");
        var differing = Enumerable.Range(0, w.Pcm.Length).Count(i => w.Pcm[i] != s.Pcm[i]);
        _output.WriteLine($"split at 2.48 s: {differing} of {w.Pcm.Length} samples differ from the unsplit music");
        Assert.Equal(w.Pcm.Length, s.Pcm.Length);
        Assert.Equal(0, differing);

        var cut = WithVideo();
        var a = cut.AudioTrack();
        cut.Audio(a, clicks, 0, 50);                                                 // 0 … 2 s
        cut.Audio(a, clicks, 100, 150, sourceInFrame: 100);                          // 4 … 6 s; 2 … 4 s removed
        var c = await Export(cut, "b4-gap");
        var left = Left(c.Pcm);
        Assert.All(Slice(left, 2.0, 4.0), v => Assert.Equal(0f, v));
        var offsets = Offsets(left, 0.5);
        _output.WriteLine($"gap 2–4 s: bursts at offsets from 0.5 + k s (ms): {string.Join(" ", offsets.Select(d => d.ToString("0.00")))}");
        Assert.Equal(new[] { 0.5, 1.5, 4.5, 5.5 }, EncoderHarness.Centroids(left).Select(x => Math.Round(x / Rate - BurstCentre, 2)));
    }

    /// <summary>B5 / B6: a 1 s fade-in and a 2 s fade-out are linear (each 10 ms window's level within 2 % of full scale of
    /// the line) and click-free (no sample step above the sine's own); the music trimmed to the video's end with a fade-out
    /// reaches silence on the last frame and the export ends with the picture.</summary>
    [FfmpegFact]
    public async Task Fades_are_linear_click_free_and_reach_silence_at_the_end()
    {
        var p = WithVideo();
        var music = p.Audio(p.AudioTrack(), Sine("wav", 0.25, 48000), 0, 100);       // trimmed to the video's 4 s
        music.FadeIn = p.F(25);
        music.FadeOut = p.F(50);
        var run = await Export(p, "b5-fades");

        AssertValidMp4(run);
        Assert.Equal(p.F(100), run.Output.Duration);
        var left = Left(run.Pcm);
        const int window = Rate / 100;
        double Peak(int from) => Enumerable.Range(from, window).Max(i => Math.Abs(left[i])) / 0.25;
        double Expected(double seconds) => Math.Min(1, Math.Min(seconds / 1.0, (4.0 - seconds) / 2.0));
        var worst = 0.0;
        for (var from = 0; from + window <= left.Length; from += window)
        {
            var mid = (from + window / 2.0) / Rate;
            // The peak of a window lies at its louder end: compare with the line there.
            var expectedMax = Math.Max(Expected(from / (double)Rate), Expected((from + window) / (double)Rate));
            worst = Math.Max(worst, Math.Abs(Peak(from) - expectedMax));
        }
        var maxStep = Enumerable.Range(1, left.Length - 1).Max(i => Math.Abs(left[i] - left[i - 1]));
        var sineStep = 0.25 * 2 * Math.PI * 440 / Rate;
        var tail = Rms(left.AsSpan(left.Length - Rate / 50));
        _output.WriteLine($"fades: worst deviation from the line {worst:0.0000} of full scale; max sample step {maxStep:0.00000} (sine's own {sineStep:0.00000}); last 20 ms RMS {tail:0.000000}; last sample {left[^1]:0.000000}");
        Assert.InRange(worst, 0, 0.02);
        Assert.True(maxStep <= sineStep * 1.02, $"a step of {maxStep} — a click");
        Assert.InRange(tail, 0, 0.002);
    }

    /// <summary>B9: the music's placement, trim, volume and fades saved and reopened export sample-identical audio.</summary>
    [FfmpegFact]
    public async Task Save_and_reopen_keeps_the_music_exactly()
    {
        var p = WithVideo();
        var music = p.Audio(p.AudioTrack(), Clicks("m4a"), 10, 140, sourceInFrame: 12);
        music.Volume = 0.6;
        music.FadeIn = p.F(10);
        music.FadeOut = p.F(30);

        var folder = _media.OutputFolder();
        var reopened = ProjectSerializer.Deserialize(ProjectSerializer.Serialize(p.Project, folder), folder);
        var before = await Export(p, "b9-before");
        var after = await ExportProject(reopened, Path.Combine(_media.OutputFolder(), "b9-after.mp4"));

        Assert.Equal(before.Pcm.Length, after.Pcm.Length);
        Assert.True(before.Pcm.AsSpan().SequenceEqual(after.Pcm), "the reopened project's audio differs");
    }

    /// <summary>B10: a 10-minute VBR MP3 at 44.1 kHz, used from 5:00 for 60 s — every burst on time (± 1 ms), no
    /// drift over the minute.</summary>
    [FfmpegFact]
    public async Task A_long_vbr_mp3_seeks_and_plays_without_drift()
    {
        var p = WithVideo();
        p.Audio(p.AudioTrack(), Clicks("mp3", seconds: 600, quality: "4"), 0, 1500, sourceInFrame: 7500);   // 300 s … 360 s
        var run = await Export(p, "b10-long");

        AssertValidMp4(run);
        var offsets = Offsets(Left(run.Pcm), 0.5);
        _output.WriteLine($"VBR 10 min from 5:00: {offsets.Count} bursts, first {offsets[0]:0.00} ms, last {offsets[^1]:0.00} ms, worst {offsets.Max(Math.Abs):0.00} ms");
        Assert.Equal(60, offsets.Count);
        Assert.All(offsets, d => Assert.InRange(d, -1.0, 1.0));
        Assert.InRange(offsets[^1] - offsets[0], -0.5, 0.5);
    }
}
