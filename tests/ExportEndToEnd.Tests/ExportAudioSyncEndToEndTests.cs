using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Playback;
using AiVideoEditor.Video.Tests;
using Xunit;
using Xunit.Abstractions;
using static AiVideoEditor.ExportEndToEnd.Tests.EndToEnd;

namespace AiVideoEditor.ExportEndToEnd.Tests;

/// <summary>
/// Phase 8 Step 6, the sound and the timing with real parts: project → preflight → <c>ExportService</c> →
/// <c>ExportAudioSource</c> (ffmpeg decoding, the Preview's placement and mix) → <c>FfmpegExportEncoder</c> → MP4 — for
/// one clip, overlapping clips, mute, a hidden video track, speed and silence; A/V synchronisation at 23.976, 29.97 and
/// 25 fps; short and one-frame projects; a project without audio still gets its (silent) AAC track.
/// </summary>
[Collection(EndToEndCollection.Name)]
public sealed class ExportAudioSyncEndToEndTests
{
    private const int Rate = AudioFormat.SampleRate;
    private readonly E2EMedia _media;
    private readonly ITestOutputHelper _output;

    public ExportAudioSyncEndToEndTests(E2EMedia media, ITestOutputHelper output)
    {
        _media = media;
        _output = output;
    }

    private Task<ExportRun> Export(ProjectBuilder p, string name) =>
        ExportProject(p.Project, Path.Combine(_media.OutputFolder(), name + ".mp4"));

    /// <summary>The AAC round trip of the PCM the service encoded: signal-to-error ratio in dB (∞ for digital silence).</summary>
    private static double Snr(float[] encoded, float[] decoded)
    {
        Assert.Equal(encoded.Length, decoded.Length);
        double signal = 0, error = 0;
        for (var i = 0; i < encoded.Length; i++) { signal += (double)encoded[i] * encoded[i]; error += Math.Pow(encoded[i] - decoded[i], 2); }
        return error == 0 ? double.PositiveInfinity : 10 * Math.Log10(signal / error);
    }

    private static float[] Slice(float[] x, double fromSeconds, double toSeconds) =>
        x[(int)(fromSeconds * Rate)..(int)Math.Min(x.Length, toSeconds * Rate)];

    [FfmpegFact]
    public async Task One_audio_clip()
    {
        var p = new ProjectBuilder(FrameRate.Fps25);
        p.Audio(p.AudioTrack(), _media.Tone(), 25, 75);                                        // 1 s … 3 s of a 3 s project
        var run = await Export(p, "one-clip");

        AssertValidMp4(run);
        var pcm = Left(run.Pcm);
        var decoded = EncoderHarness.DecodeLeft(run.Path);
        _output.WriteLine($"SNR {Snr(pcm, decoded):0.0} dB");
        Assert.True(Snr(pcm, decoded) > 25);
        Assert.All(Slice(pcm, 0, 1), v => Assert.Equal(0f, v));                                // silence before the clip
        Assert.InRange(Rms(Slice(pcm, 1.1, 2.9)), 0.17, 0.18);                                // 0.25 / √2
        Assert.InRange(Rms(Slice(decoded, 1.1, 2.9)), 0.17, 0.18);
        Assert.InRange(Rms(Slice(decoded, 0, 0.9)), 0, 0.001);
    }

    [FfmpegFact]
    public async Task Overlapping_clips_mix_and_a_muted_clip_or_track_is_silent()
    {
        var p = new ProjectBuilder(FrameRate.Fps25);
        p.Audio(p.AudioTrack(), _media.Bursts(), 0, 100);                                       // 0 … 4 s
        p.Audio(p.AudioTrack(), _media.Tone(), 50, 100);                                        // 2 … 4 s, overlapping
        p.Audio(p.AudioTrack(), _media.Tone(), 0, 100).IsMuted = true;                          // muted clip
        p.Audio(p.AudioTrack(muted: true), _media.Tone(), 0, 100);                              // muted track
        var run = await Export(p, "overlap");

        AssertValidMp4(run);
        var pcm = Left(run.Pcm);
        var decoded = EncoderHarness.DecodeLeft(run.Path);
        Assert.True(Snr(pcm, decoded) > 25);
        // Before 2 s only the bursts: silence between them (the muted tone would fill it).
        Assert.InRange(Rms(Slice(pcm, 0.1, 0.45)), 0, 1e-6);
        Assert.InRange(Rms(Slice(decoded, 0.15, 0.4)), 0, 0.002);
        // From 2 s the tone between the bursts, and burst + tone within them.
        Assert.InRange(Rms(Slice(pcm, 2.1, 2.45)), 0.17, 0.18);
        Assert.True(Rms(Slice(pcm, 2.5, 2.54)) > Rms(Slice(pcm, 0.5, 0.54)));
        Assert.InRange(Rms(Slice(decoded, 2.1, 2.45)), 0.17, 0.18);
    }

    [FfmpegFact]
    public async Task A_video_clip_on_a_hidden_track_keeps_its_sound_but_not_its_picture()
    {
        var p = new ProjectBuilder(FrameRate.Fps25);
        p.Video(p.VideoTrack(hidden: true), _media.Sync(FrameRate.Fps25), 0, 100);
        var run = await Export(p, "hidden-video-audio");

        AssertValidMp4(run);
        Assert.All(run.Canvases, c => Assert.All(Enumerable.Range(0, c.Length / 4), i => Assert.Equal(0, c[4 * i] + c[4 * i + 1] + c[4 * i + 2])));
        var decoded = EncoderHarness.DecodeLeft(run.Path);
        Assert.Equal(3, EncoderHarness.Centroids(decoded).Count);                               // bursts at 0.6 s, 1.8 s, 3.0 s
    }

    [FfmpegFact]
    public async Task Speed_changes_the_spacing_of_the_sound()
    {
        var p = new ProjectBuilder(FrameRate.Fps25);
        p.Audio(p.AudioTrack(), _media.Bursts(), 0, 45, speed: ClipSpeed.FromSteps(40));        // 2×: 3.6 s of source in 1.8 s
        var run = await Export(p, "speed");

        AssertValidMp4(run);
        var decoded = EncoderHarness.DecodeLeft(run.Path);
        // Source bursts every 0.5 s → every 0.25 s on the timeline. The pitch-preserving tempo change (atempo, D022) smears a
        // 40 ms burst, sometimes into two energy runs, so each run is matched to its grid point (Step 4: within ±10 ms).
        var centroids = EncoderHarness.Centroids(Left(run.Pcm)).Select(c => c / Rate).ToList();
        var grid = centroids.Select(c => (K: Math.Round((c - 0.01) / 0.25), Ms: (c - 0.01 - Math.Round((c - 0.01) / 0.25) * 0.25) * 1000)).ToList();
        _output.WriteLine($"2×: {centroids.Count} runs, offset from k·0.25 s (ms): {string.Join(" ", grid.Select(g => $"{g.K}:{g.Ms:0.0}"))}");
        Assert.Equal(Enumerable.Range(0, 8).Select(k => (double)k), grid.Select(g => g.K).Distinct());
        Assert.All(grid, g => Assert.InRange(g.Ms, -10.0, 10.0));
        Assert.True(Snr(Left(run.Pcm), decoded) > 20);
    }

    [FfmpegFact]
    public async Task A_project_without_audio_gets_a_silent_aac_track()
    {
        var p = new ProjectBuilder(FrameRate.Fps25);
        p.Video(p.VideoTrack(), _media.Pattern(), 0, 25);
        var run = await Export(p, "silent");

        AssertValidMp4(run);                                                                    // includes the AAC track and its sample count
        Assert.All(run.Pcm, v => Assert.Equal(0f, v));
        Assert.All(EncoderHarness.DecodeLeft(run.Path), v => Assert.Equal(0f, v));
    }

    public static TheoryData<int, int> Rates => new() { { 24000, 1001 }, { 30000, 1001 }, { 25, 1 } };

    /// <summary>The sync source placed at timeline frame 10 (from its frame 0): every white frame of the MP4 and the
    /// start of the burst that belongs to it (the energy centroid − 20 ms) agree within 1 ms, over the whole export.</summary>
    [FfmpegTheory]
    [MemberData(nameof(Rates))]
    public async Task Audio_and_video_stay_in_sync(int numerator, int denominator)
    {
        var rate = new FrameRate(numerator, denominator);
        var p = new ProjectBuilder(rate);
        p.Video(p.VideoTrack(), _media.Sync(rate), 10, 100);
        var run = await Export(p, $"sync-{numerator}");

        AssertValidMp4(run);
        var o = run.Output;
        var frames = EncoderHarness.DecodeFrames(run.Path, o.Size.Width, o.Size.Height);
        var flashes = frames.Select((f, n) => (f, n)).Where(x => x.f[4 * (o.Size.Width * 90 + 160) + 1] > 128).Select(x => (long)x.n).ToList();
        Assert.Equal(new long[] { 25, 55, 85 }, flashes);                                       // source frames 15, 45, 75 at +10

        var bursts = EncoderHarness.Centroids(EncoderHarness.DecodeLeft(run.Path)).Select(c => (c - 959.5) / Rate).ToList();
        Assert.Equal(flashes.Count, bursts.Count);
        var offsetsMs = flashes.Zip(bursts, (n, b) => (b - MediaTime.FromFrame(n, rate).TotalSeconds) * 1000).ToList();
        _output.WriteLine($"{numerator}/{denominator}: audio − video at the flashes (ms): {string.Join(" ", offsetsMs.Select(d => d.ToString("0.000")))}");
        Assert.All(offsetsMs, d => Assert.InRange(d, -1.0, 1.0));
        Assert.InRange(offsetsMs[^1] - offsetsMs[0], -0.5, 0.5);                                // no drift
    }

    [FfmpegTheory]
    [InlineData(3)]
    [InlineData(1)]
    public async Task Short_and_one_frame_projects(long frames)
    {
        var rate = FrameRate.Ntsc30;
        var p = new ProjectBuilder(rate);
        p.Video(p.VideoTrack(), _media.Solid("3050C8", rate), 0, frames);
        p.Audio(p.AudioTrack(), _media.Tone(), 0, frames);
        var run = await Export(p, $"short-{frames}");

        AssertValidMp4(run);
        Assert.Equal(frames, run.Output.FrameCount);
        Assert.Equal(AudioTiming.CeilingSample(MediaTime.FromFrame(frames, rate)), run.Output.AudioSampleCount);   // 1 602 for one frame
        Assert.True(Snr(Left(run.Pcm), EncoderHarness.DecodeLeft(run.Path)) > 15);
    }
}
