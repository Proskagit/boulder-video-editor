using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Core.Playback;
using AiVideoEditor.Infrastructure;
using AiVideoEditor.Infrastructure.Configuration;
using AiVideoEditor.Media.Thumbnails;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace AiVideoEditor.Video.Tests;

/// <summary>
/// D024 Step 9.4a: <see cref="ThumbnailService"/> with the real ffmpeg decoder on generated media whose frames carry
/// their number in 16 bit columns. The expected frame comes from the generation recipe (<see cref="TestMedia"/>'s ideal
/// frame times) and D009 — the last frame whose source time is at or before T, the first before any, the last after
/// the end —, not from the decoder under test. Also: VFR and jittered timestamps, a container start time, a clip
/// shorter than T, T after the video's end, an image, a rotated video, and the 160 × 90 bound.
/// </summary>
[Collection(MediaCollection.Name)]
public sealed class ThumbnailIntegrationTests : IDisposable
{
    // TestMedia's pattern: frame number N as 16 black/white columns (bit k = column k, 16 px wide at 256 px).
    private const string Bits = @"geq=lum='if(mod(floor(N/pow(2\,floor(X/16)))\,2)\,235\,16)':cb=128:cr=128";

    private readonly TestMedia _media;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "aive-thumbnail-e2e", Guid.NewGuid().ToString("N"));
    private readonly CountingDecoder _decoder = new(new FfmpegVideoDecoder(FfmpegTools.FfmpegLocator, NullLogger<FfmpegVideoDecoder>.Instance));

    public ThumbnailIntegrationTests(TestMedia media)
    {
        _media = media;
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private sealed class CountingDecoder(IVideoDecoder inner) : IVideoDecoder
    {
        public int Opens;
        public Task<IVideoFrameStream> OpenAsync(VideoDecodeRequest request, CancellationToken ct = default)
        {
            Interlocked.Increment(ref Opens);
            return inner.OpenAsync(request, ct);
        }
    }

    private ThumbnailService Service() => new(_decoder, NullLogger<ThumbnailService>.Instance);
    private string Cache => Path.Combine(_dir, "cache", "thumbnails");

    private static MediaAsset Asset(string path, MediaMetadata metadata, MediaKind kind = MediaKind.Video) => new()
    {
        FilePath = path, Kind = kind, Metadata = metadata, AnalysisStatus = MediaAnalysisStatus.Completed
    };

    private static MediaMetadata Analyse(string path)
    {
        var result = FfmpegTools.Analysis().AnalyzeAsync(path).GetAwaiter().GetResult();
        Assert.True(result.Metadata is not null, $"analysis failed for {path}: {result.ErrorMessage}");
        return result.Metadata!;
    }

    /// <summary>The frame number drawn into the thumbnail (read like the decoder tests read full frames).</summary>
    private static long NumberIn(Thumbnail t) =>
        FfmpegVideoDecoderIntegrationTests.ReadNumber(new DecodedFrame(t.Width, t.Height, t.Stride, t.Pixels, new SourceTimestamp(0, new TimeBase(1, 25))));

    /// <summary>D009 on the recipe: the last ideal frame at or before T, the first one if all start later.</summary>
    private static long Expected(MediaFile file)
    {
        var t = Rational.FromTicks(ThumbnailService.SourceTime(file.Metadata).Ticks);
        var chosen = file.Frames[0];
        foreach (var frame in file.Frames)
            if (frame.Time <= t) chosen = frame;
        return chosen.Number;
    }

    private string Encode(string name, params string[] args)
    {
        var path = Path.Combine(_dir, name);
        TestMedia.Run(FfmpegTools.Ffmpeg!, new[] { "-hide_banner", "-loglevel", "error", "-y" }.Concat(args).Append(path));
        return path;
    }

    [FfmpegTheory]
    [InlineData("cfr25.mp4")]      // 12 s: T = 1.2 s, exactly frame 30's start
    [InlineData("cfr2997.mp4")]    // NTSC rate, T between frames
    [InlineData("vfr.mkv")]        // variable frame rate
    [InlineData("jitter.mp4")]     // timestamps up to ±0.35 frame off the grid
    [InlineData("offset.ts")]      // MPEG-TS: the container starts at ≈ 1.4 s, the video 0.3 s after the audio
    [InlineData("hevc2997.mp4")]   // H.265
    public async Task The_thumbnail_shows_the_D009_frame_at_T_and_fits_160_by_90(string name)
    {
        var file = _media.Get(name);

        var thumbnail = await Service().GetOrCreateAsync(Asset(file.Path, file.Metadata), Cache);

        Assert.NotNull(thumbnail);
        Assert.Equal(Expected(file), NumberIn(thumbnail!));
        Assert.Equal((160, 90), (thumbnail.Width, thumbnail.Height));     // 256 × 144 sources, aspect kept
    }

    [FfmpegFact]
    public async Task A_cached_thumbnail_is_used_again_without_starting_ffmpeg()
    {
        var file = _media.Get("cfr25.mp4");
        var asset = Asset(file.Path, file.Metadata);
        var first = await Service().GetOrCreateAsync(asset, Cache);
        var opens = _decoder.Opens;

        var again = await Service().GetOrCreateAsync(asset, Cache);

        Assert.Equal(opens, _decoder.Opens);
        Assert.Equal(first!.Pixels.ToArray(), again!.Pixels.ToArray());
    }

    [FfmpegFact]
    public async Task A_clip_shorter_than_T_shows_its_first_frame_and_T_after_the_video_shows_its_last()
    {
        // 3 frames (0.12 s): T = 12 ms → frame 0.
        var shortClip = Encode("short.mp4", "-f", "lavfi", "-i", $"nullsrc=s=256x144:r=25:d=0.12,{Bits}", "-c:v", "libx264", "-pix_fmt", "yuv420p");
        // 5 frames of video, 10 s of sound: T = 1 s lies after the last frame (0.16 s) → frame 4.
        var videoEndsEarly = Encode("video-ends-early.mp4",
            "-f", "lavfi", "-i", $"nullsrc=s=256x144:r=25:d=0.2,{Bits}", "-f", "lavfi", "-i", "sine=f=440:d=10",
            "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac");

        var first = await Service().GetOrCreateAsync(Asset(shortClip, Analyse(shortClip)), Cache);
        var endsEarly = Analyse(videoEndsEarly);
        var last = await Service().GetOrCreateAsync(Asset(videoEndsEarly, endsEarly), Cache);

        Assert.Equal(0, NumberIn(first!));
        Assert.True(endsEarly.Duration > MediaTime.FromSeconds(9), $"container duration {endsEarly.Duration}");
        Assert.Equal(4, NumberIn(last!));
    }

    [FfmpegFact]
    public async Task An_image_is_scaled_into_160_by_90_with_its_aspect()
    {
        var image = Encode("still.png", "-f", "lavfi", "-i", "testsrc2=s=320x240", "-frames:v", "1");

        var thumbnail = await Service().GetOrCreateAsync(Asset(image, Analyse(image), MediaKind.Image), Cache);

        Assert.Equal((120, 90), (thumbnail!.Width, thumbnail.Height));
    }

    [FfmpegFact]
    public async Task A_rotated_video_is_upright_like_in_the_preview()
    {
        // 320 × 180, left half red, right half blue, tagged "rotate 90° clockwise to show" (display matrix −90°).
        var coded = Encode("halves.mp4", "-f", "lavfi", "-i", "color=c=red:s=160x180:d=1,format=yuv420p", "-f", "lavfi",
            "-i", "color=c=blue:s=160x180:d=1,format=yuv420p", "-filter_complex", "[0][1]hstack", "-c:v", "libx264", "-pix_fmt", "yuv420p");
        var rotated = Path.Combine(_dir, "rotated.mp4");
        TestMedia.Run(FfmpegTools.Ffmpeg!, new[] { "-hide_banner", "-loglevel", "error", "-y", "-display_rotation", "-90", "-i", coded, "-c", "copy", rotated });
        var metadata = Analyse(rotated);
        Assert.Equal(90, metadata.DisplayRotation);

        var t = (await Service().GetOrCreateAsync(Asset(rotated, metadata), Cache))!;

        Assert.Equal(90, t.Height);                                       // portrait: 180 × 320 fitted into 160 × 90
        Assert.True(t.Width < t.Height, $"{t.Width} × {t.Height}");
        (byte B, byte G, byte R) Pixel(int x, int y)
        {
            var i = y * t.Stride + x * Thumbnail.BytesPerPixel;
            return (t.Pixels.Span[i], t.Pixels.Span[i + 1], t.Pixels.Span[i + 2]);
        }
        var top = Pixel(t.Width / 2, 5);
        var bottom = Pixel(t.Width / 2, t.Height - 6);
        Assert.True(top.R > 200 && top.B < 60, $"top {top}");            // the left (red) half, turned clockwise, is on top
        Assert.True(bottom.B > 200 && bottom.R < 60, $"bottom {bottom}");
    }
}
