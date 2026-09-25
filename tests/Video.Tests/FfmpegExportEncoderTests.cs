using System.Collections.Immutable;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Composition;
using AiVideoEditor.Core.Export;
using AiVideoEditor.Core.Playback;
using Xunit;
using Xunit.Abstractions;
using static AiVideoEditor.Video.Tests.EncoderHarness;

namespace AiVideoEditor.Video.Tests;

/// <summary>
/// Phase 8 Step 5 (D023): the ffmpeg export encoder with the real ffmpeg — format and arguments, BGRA input (byte
/// order, stride), colour round trip (BT.709 limited range), exact rational frame timing, AAC (48 kHz stereo LC
/// 192 kbps, priming measured), audio/video synchronisation.
/// </summary>
[Collection(MediaCollection.Name)]
public sealed class FfmpegExportEncoderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "aive-encoder-tests", Guid.NewGuid().ToString("N"));
    private readonly ITestOutputHelper _output;

    public FfmpegExportEncoderTests(ITestOutputHelper output)
    {
        _output = output;
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private string Out(string name = "out.mp4") => Path.Combine(_dir, name);

    // --- arguments ---------------------------------------------------------------------------------------------

    [Fact]
    public void The_command_lines_carry_the_D023_format()
    {
        var output = Output(1920, 1080, FrameRate.Ntsc30, 300);
        var video = string.Join(' ', FfmpegExportEncoder.VideoArguments(output, "a.m4a", "v.mp4"));
        var audio = string.Join(' ', FfmpegExportEncoder.AudioArguments("a.m4a"));

        Assert.Contains("-f rawvideo -pix_fmt bgra -s 1920x1080 -framerate 30000/1001 -i pipe:0", video);
        Assert.Contains("-c:v libx264 -preset medium -crf 18 -pix_fmt yuv420p", video);
        Assert.Contains("scale=out_color_matrix=bt709:out_range=tv,format=yuv420p,setparams=range=tv:color_primaries=bt709:color_trc=bt709:colorspace=bt709", video);
        Assert.Contains("-colorspace bt709 -color_primaries bt709 -color_trc bt709 -color_range tv", video);
        Assert.Contains("-fps_mode passthrough", video);
        Assert.Contains("-i a.m4a -map 0:v:0 -map 1:a:0", video);
        Assert.Contains("-c:a copy -movflags +faststart -f mp4 v.mp4", video);
        Assert.DoesNotContain("hwaccel", video);
        Assert.Contains("-f f32le -ar 48000 -ac 2 -i pipe:0 -c:a aac -profile:a aac_low -b:a 192000 -ar 48000 -ac 2 -f mp4 a.m4a", audio);
    }

    // --- video: format, frame count, exact rational timing ----------------------------------------------------------

    [FfmpegTheory]
    [InlineData(24000, 1001, 72)]
    [InlineData(30000, 1001, 90)]
    [InlineData(25, 1, 75)]
    [InlineData(60000, 1001, 7)]
    public async Task Frames_are_encoded_once_each_at_exact_rational_timestamps(int num, int den, int frames)
    {
        var rate = new FrameRate(num, den);
        var output = Output(320, 180, rate, frames);
        await Encode(output, Out(), _ => 0, (n, px, stride) => NumberFrame(n, px, stride, 320, 180));

        var video = Stream(Out(), "video");
        Assert.Equal(("h264", "High", "yuv420p", "320", "180"), (video.Str("codec_name"), video.Str("profile"), video.Str("pix_fmt"), video.Str("width"), video.Str("height")));
        Assert.Equal(($"{num}/{den}", $"{num}/{den}", frames.ToString()), (video.Str("r_frame_rate"), video.Str("avg_frame_rate"), video.Str("nb_frames")));
        Assert.Equal(("tv", "bt709", "bt709", "bt709"), (video.Str("color_range"), video.Str("color_space"), video.Str("color_primaries"), video.Str("color_transfer")));

        var (pts, tbNum, tbDen) = VideoPts(Out());
        Assert.Equal(frames, pts.Count);
        for (var n = 0; n < frames; n++)
            Assert.Equal((Int128)n * den * tbDen, (Int128)pts[n] * tbNum * num);           // pts·tb = n·den/num exactly
        Assert.Equal((Int128)frames * den * tbDen, (Int128)long.Parse(video.Str("duration_ts")) * tbNum * num);

        // every frame once, in order: no duplicate, no drop
        Assert.Equal(Enumerable.Range(0, frames), DecodeFrames(Out(), 320, 180).Select(f => ReadNumber(f, 320, 180)));
        Assert.DoesNotContain(Directory.GetFiles(_dir), f => f != Out());                  // no temporary file left
    }

    [FfmpegFact]
    public async Task A_project_shorter_than_one_frame_is_one_whole_frame_with_its_audio()
    {
        // A sequence of 1 tick: ExportOutput covers it with one frame and that frame's samples.
        var output = ExportOutput.For(new PlaybackSnapshot(1, FrameRate.Ntsc30, new MediaTime(1), ImmutableArray<VideoLayer>.Empty,
            ImmutableArray<AudioSpan>.Empty, ImmutableDictionary<Guid, PlaybackAsset>.Empty, new FrameSize(64, 36)));
        Assert.Equal(1, output.FrameCount);
        await Encode(output, Out(), _ => 0.25f, (n, px, stride) => NumberFrame(n, px, stride, 64, 36));

        Assert.Equal("1", Stream(Out(), "video").Str("nb_frames"));
        Assert.Equal(1602, output.AudioSampleCount);                                        // ⌈1001/30000 · 48000⌉
        Assert.Equal(1602, DecodeLeft(Out()).Length);
    }

    // --- video: BGRA byte order, stride, colour ------------------------------------------------------------------------

    private static readonly (byte R, byte G, byte B)[] Patches =
    {
        (255, 0, 0), (0, 255, 0), (0, 0, 255), (255, 255, 255), (0, 0, 0), (128, 128, 128), (64, 64, 64), (192, 192, 192),
        (255, 128, 0), (0, 128, 255), (200, 40, 160), (30, 200, 90), (255, 255, 0), (0, 255, 255), (255, 0, 255), (100, 149, 237)
    };

    private static void PatchFrame(byte[] pixels, int stride)
    {
        Array.Fill(pixels, (byte)0x5A);                                                     // stride padding: garbage
        for (var y = 0; y < 180; y++)
        for (var x = 0; x < 320; x++)
        {
            var (r, g, b) = Patches[y / 45 * 4 + x / 80];
            var i = y * stride + x * 4;
            (pixels[i], pixels[i + 1], pixels[i + 2], pixels[i + 3]) = (b, g, r, 255);
        }
    }

    private static (int Y, int U, int V) Bt709Limited((byte R, byte G, byte B) c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        var y = 0.2126 * r + 0.7152 * g + 0.0722 * b;
        return ((int)Math.Round(16 + 219 * y), (int)Math.Round(128 + 224 * (b - y) / 1.8556), (int)Math.Round(128 + 224 * (r - y) / 1.5748));
    }

    [FfmpegTheory]
    [InlineData(0)]
    [InlineData(64)]       // padded rows
    public async Task Colours_survive_the_round_trip_with_the_BT709_matrix_in_limited_range(int stridePadding)
    {
        var output = Output(320, 180, FrameRate.Fps25, 3);
        await Encode(output, Out(), _ => 0, (_, px, stride) => PatchFrame(px, stride), stridePadding);

        // 1. the stored YUV is the BT.709 limited-range conversion of the BGRA input (not BT.601: red would be Y 81)
        var yuv = Run(FfmpegTools.Ffmpeg!, "-v", "error", "-i", Out(), "-frames:v", "1", "-f", "rawvideo", "-pix_fmt", "yuv420p", "-");
        var worstYuv = 0;
        for (var k = 0; k < Patches.Length; k++)
        {
            int cx = k % 4 * 80 + 40, cy = k / 4 * 45 + 22;
            var stored = (Y: yuv[cy * 320 + cx], U: yuv[320 * 180 + cy / 2 * 160 + cx / 2], V: yuv[320 * 180 + 160 * 90 + cy / 2 * 160 + cx / 2]);
            var expected = Bt709Limited(Patches[k]);
            worstYuv = Math.Max(worstYuv, Math.Max(Math.Abs(stored.Y - expected.Y), Math.Max(Math.Abs(stored.U - expected.U), Math.Abs(stored.V - expected.V))));
        }

        // 2. back to BGRA through the stream's tags (as the app's decoder does)
        var bgra = DecodeFrames(Out(), 320, 180)[0];
        var worstRgb = 0;
        var report = new List<string>();
        for (var k = 0; k < Patches.Length; k++)
        {
            int cx = k % 4 * 80 + 40, cy = k / 4 * 45 + 22, i = (cy * 320 + cx) * 4;
            var (r, g, b) = Patches[k];
            var error = Math.Max(Math.Abs(bgra[i + 2] - r), Math.Max(Math.Abs(bgra[i + 1] - g), Math.Abs(bgra[i] - b)));
            worstRgb = Math.Max(worstRgb, error);
            report.Add($"{(r, g, b)}→{(bgra[i + 2], bgra[i + 1], bgra[i])}");
        }
        _output.WriteLine($"YUV vs BT.709 formula: max {worstYuv}; RGB round trip: max {worstRgb}\n{string.Join("; ", report)}");
        Assert.InRange(worstYuv, 0, 1);
        Assert.InRange(worstRgb, 0, 3);
        var red = (bgra[(22 * 320 + 40) * 4 + 2], bgra[(22 * 320 + 40) * 4 + 1], bgra[(22 * 320 + 40) * 4]);
        Assert.True(red.Item1 > 250 && red.Item2 < 5 && red.Item3 < 5, $"red came back as {red}");  // byte order B, G, R, A
    }

    // --- audio: format, silence, gain, priming --------------------------------------------------------------------------

    [FfmpegFact]
    public async Task Audio_is_AAC_LC_48_kHz_stereo_at_about_192_kbps_with_the_gain_unchanged()
    {
        var output = Output(64, 36, FrameRate.Fps25, 100);                                 // 4 s
        await Encode(output, Out(), k => 0.5f * MathF.Sin(2 * MathF.PI * 997 * k / 48_000f) + 0.2f * MathF.Sin(2 * MathF.PI * 5_003 * k / 48_000f),
            (n, px, stride) => NumberFrame(n, px, stride, 64, 36));

        var audio = Stream(Out(), "audio");
        Assert.Equal(("aac", "LC", "48000", "2", "stereo"), (audio.Str("codec_name"), audio.Str("profile"), audio.Str("sample_rate"), audio.Str("channels"), audio.Str("channel_layout")));
        var bitrate = long.Parse(audio.Str("bit_rate"));
        _output.WriteLine($"audio bit rate {bitrate}");
        Assert.InRange(bitrate, 150_000, 200_000);
        var left = DecodeLeft(Out());
        Assert.Equal(output.AudioSampleCount, left.Length);
        var peak = left.Skip(4_800).Take(96_000).Max(Math.Abs);
        Assert.InRange(peak, 0.65f, 0.75f);                                                 // 0.5 + 0.2: no gain change, no normalization
    }

    [FfmpegFact]
    public async Task A_silent_project_still_has_an_audio_track_of_silence()
    {
        var output = Output(64, 36, FrameRate.Fps25, 50);
        await Encode(output, Out(), _ => 0, (n, px, stride) => NumberFrame(n, px, stride, 64, 36));

        Assert.Equal("aac", Stream(Out(), "audio").Str("codec_name"));
        var left = DecodeLeft(Out());
        Assert.Equal(96_000, left.Length);
        Assert.All(left, s => Assert.Equal(0f, s));
    }

    [FfmpegFact]
    public async Task AAC_priming_is_compensated_by_the_container_so_samples_keep_their_position()
    {
        var output = Output(64, 36, FrameRate.Fps25, 75);                                  // 3 s
        await Encode(output, Out(), Bursts(new long[] { 48_000 }), (n, px, stride) => NumberFrame(n, px, stride, 64, 36));

        var audio = Stream(Out(), "audio");
        var first = Probe(Out(), "-select_streams", "a:0", "-show_entries", "packet=pts,duration", "-read_intervals", "%+#1")
            .GetProperty("packets")[0];
        var left = DecodeLeft(Out());
        var centroid = Centroids(left).Single();
        _output.WriteLine($"first packet pts {first.Str("pts")} (priming), start_time {audio.Str("start_time")}, duration_ts {audio.Str("duration_ts")}, " +
                          $"decoded {left.Length} samples, burst centroid {centroid:0.0} (written at 48 000 + 119.5)");

        Assert.Equal("-1024", first.Str("pts"));                                           // the encoder's priming, in the stream…
        Assert.Equal("0.000000", audio.Str("start_time"));                                  // …removed by the edit list
        Assert.Equal(output.AudioSampleCount.ToString(), audio.Str("duration_ts"));
        Assert.Equal(output.AudioSampleCount, left.Length);                                 // every written sample, no more
        Assert.InRange(centroid - (48_000 + 119.5), -2, 2);                                  // no shift (measured 0.8)
    }

    // --- audio/video synchronisation -------------------------------------------------------------------------------------

    public static TheoryData<int, int, long, string> SyncCases => new()
    {
        { 24000, 1001, 48, "4,20,47" },          // bursts at the starts of frames 4, 20 and 47 (the last one)
        { 30000, 1001, 60, "0,31,59" },
        { 25, 1, 50, "7,25" },
        { 25, 1, 5, "1" },                       // short project
        { 25, 1, 50, "30" },                     // audio starting later than the video
    };

    [FfmpegTheory]
    [MemberData(nameof(SyncCases))]
    public async Task Audio_at_the_start_of_frame_k_is_heard_when_frame_k_is_shown(int num, int den, long frames, string markers)
    {
        var rate = new FrameRate(num, den);
        var marked = markers.Split(',').Select(long.Parse).ToList();
        var starts = marked.Select(k => AudioTiming.CeilingSample(MediaTime.FromFrame(k, rate))).ToList();
        var output = Output(160, 90, rate, frames);
        await Encode(output, Out(), Bursts(starts), (n, px, stride) => NumberFrame(n, px, stride, 160, 90));

        var (pts, tbNum, tbDen) = VideoPts(Out());
        var shown = DecodeFrames(Out(), 160, 90).Select(f => ReadNumber(f, 160, 90)).ToList();
        var centroids = Centroids(DecodeLeft(Out()));
        Assert.Equal(marked.Count, centroids.Count);
        for (var i = 0; i < marked.Count; i++)
        {
            var k = (int)marked[i];
            Assert.Equal(k, shown[k]);
            var frameTime = (double)pts[k] * tbNum / tbDen;                                   // when frame k is shown
            var soundTime = (centroids[i] - 119.5) / 48_000;                                  // when its burst starts
            var errorMs = (soundTime - frameTime) * 1000;
            _output.WriteLine($"{num}/{den} frame {k}: video {frameTime * 1000:0.000} ms, audio {soundTime * 1000:0.000} ms, Δ {errorMs:0.000} ms");
            Assert.InRange(errorMs, -0.1, 0.1);                                              // < 1 sample of 48 kHz rounding + AAC
        }
        var videoEnd = (double)long.Parse(Stream(Out(), "video").Str("duration_ts")) * tbNum / tbDen;
        var audioEnd = long.Parse(Stream(Out(), "audio").Str("duration_ts")) / 48_000.0;
        Assert.InRange(audioEnd - videoEnd, 0, 1 / 48_000.0);                                 // ⌈duration · 48 kHz⌉
    }

    [FfmpegFact]
    public async Task Audio_until_the_last_sample_and_audio_ending_earlier_keep_their_ends()
    {
        var output = Output(64, 36, FrameRate.Fps25, 50);                                  // 2 s = 96 000 samples
        await Encode(output, Out(), k => k < 48_000 ? 0.3f * MathF.Sin(2 * MathF.PI * 440 * k / 48_000f) : 0,
            (n, px, stride) => NumberFrame(n, px, stride, 64, 36));
        var early = DecodeLeft(Out());

        await Encode(output, Out("full.mp4"), k => 0.3f * MathF.Sin(2 * MathF.PI * 440 * k / 48_000f),
            (n, px, stride) => NumberFrame(n, px, stride, 64, 36));
        var full = DecodeLeft(Out("full.mp4"));

        Assert.Equal(96_000, early.Length);
        Assert.Equal(96_000, full.Length);
        Assert.True(early.Take(47_500).Max(Math.Abs) > 0.25f);
        Assert.True(early.Skip(48_500).Max(Math.Abs) < 0.01f);                               // silence after the sound ended at 1 s
        Assert.True(full.Skip(95_000).Max(Math.Abs) > 0.25f);                                // sound up to the very end
    }
}
