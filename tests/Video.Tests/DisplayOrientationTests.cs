using System.Buffers.Binary;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Playback;
using AiVideoEditor.Infrastructure;
using AiVideoEditor.Infrastructure.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace AiVideoEditor.Video.Tests;

/// <summary>The orientation rule itself (no ffmpeg): ffmpeg's autorotate behaviour.</summary>
public class DisplayOrientationRuleTests
{
    [Theory]
    [InlineData(0.0, 0, 1920, 1080)]
    [InlineData(-90.0, 90, 1080, 1920)]   // phone portrait: turned 90° clockwise
    [InlineData(90.0, 270, 1080, 1920)]
    [InlineData(-180.0, 180, 1920, 1080)]
    [InlineData(180.0, 180, 1920, 1080)]
    [InlineData(270.0, 90, 1080, 1920)]
    [InlineData(-270.0, 270, 1080, 1920)]
    [InlineData(360.0, 0, 1920, 1080)]
    [InlineData(-90.4, 90, 1080, 1920)]   // within ffmpeg's 1° tolerance
    public void Right_angles_are_supported(double rotation, int expected, int width, int height)
    {
        var result = DisplayOrientation.Resolve(1920, 1080, new OrientationHint(rotation, IsMirrored: false));

        Assert.Equal((expected, width, height), (result.Rotation!.Value, result.DisplayWidth, result.DisplayHeight));
        Assert.Null(result.Unsupported);
    }

    [Fact]
    public void No_hint_is_zero_degrees()
    {
        var result = DisplayOrientation.Resolve(320, 180, null);
        Assert.Equal((0, 320, 180), (result.Rotation!.Value, result.DisplayWidth, result.DisplayHeight));
    }

    [Theory]
    [InlineData(45.0, 320, 180)]   // ffmpeg's rotate filter keeps the coded size
    [InlineData(-30.0, 320, 180)]
    [InlineData(-91.5, 320, 180)]  // outside the 1° tolerance: no transpose
    public void Odd_angles_are_unsupported_and_keep_the_coded_size(double rotation, int width, int height)
    {
        var result = DisplayOrientation.Resolve(320, 180, new OrientationHint(rotation, IsMirrored: false));

        Assert.Null(result.Rotation);
        Assert.Equal((width, height), (result.DisplayWidth, result.DisplayHeight));
        Assert.Contains("not a right angle", result.Unsupported);
    }

    [Theory]
    [InlineData(-180.0, 320, 180)] // a plain horizontal flip reads as −180
    [InlineData(90.0, 180, 320)]   // flip + quarter turn: transposed
    public void Mirrored_matrices_are_unsupported_but_sized_like_the_decoder_output(double rotation, int width, int height)
    {
        var result = DisplayOrientation.Resolve(320, 180, new OrientationHint(rotation, IsMirrored: true));

        Assert.Null(result.Rotation);
        Assert.Equal((width, height), (result.DisplayWidth, result.DisplayHeight));
        Assert.Contains("mirrored", result.Unsupported);
    }

    [Theory]
    [InlineData("\n00000000:            0      -65536           0\n00000001:        65536           0           0\n00000002:            0           0  1073741824\n", false)]
    [InlineData("\n00000000:       -65536           0           0\n00000001:            0       65536           0\n00000002:            0           0  1073741824\n", true)]
    [InlineData("\n00000000:            0      -65536           0\n00000001:       -65536           0           0\n00000002:            0           0  1073741824\n", true)]
    public void Mirror_is_read_from_the_matrix_determinant(string matrix, bool mirrored) =>
        Assert.Equal(mirrored, DisplayOrientation.IsMirrored(matrix));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("00000000: 1 2 3")]
    public void Unreadable_matrices_are_null(string? matrix) => Assert.Null(DisplayOrientation.IsMirrored(matrix));
}

/// <summary>
/// Real ffprobe + ffmpeg on generated files: the probed display size must be exactly the size of
/// the frames our decoder delivers (it relies on ffmpeg's automatic rotation).
/// </summary>
public sealed class DisplayOrientationIntegrationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "aive-orientation-tests", Guid.NewGuid().ToString("N"));

    public DisplayOrientationIntegrationTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static readonly FfprobeMediaAnalysisService Probe = new(
        new FfprobeLocator(Options.Create(new FfmpegOptions()), NullLogger<FfprobeLocator>.Instance),
        NullLogger<FfprobeMediaAnalysisService>.Instance);

    private string Plain()
    {
        var path = Path.Combine(_dir, "plain.mp4");
        if (!File.Exists(path))
            TestMedia.Run(FfmpegTools.Ffmpeg!, new[] { "-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i",
                "testsrc2=s=320x180:r=25:d=1", "-c:v", "libx264", "-pix_fmt", "yuv420p", path });
        return path;
    }

    /// <summary>The plain file with a display matrix attached (no re-encode), like a phone recording.</summary>
    private string WithDisplayMatrix(string name, params string[] displayArgs)
    {
        var path = Path.Combine(_dir, name);
        var args = new List<string> { "-hide_banner", "-loglevel", "error", "-y" };
        args.AddRange(displayArgs);
        args.AddRange(new[] { "-i", Plain(), "-c", "copy", path });
        TestMedia.Run(FfmpegTools.Ffmpeg!, args);
        return path;
    }

    /// <summary>A 320 × 180 JPEG with EXIF Orientation = 6 ("rotate 90° clockwise to show"), as a
    /// phone camera writes it. Only the first decoded frame reveals it; the stream does not.</summary>
    private string ExifJpeg()
    {
        var baseJpeg = Path.Combine(_dir, "base.jpg");
        TestMedia.Run(FfmpegTools.Ffmpeg!, new[] { "-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i",
            "testsrc2=s=320x180", "-frames:v", "1", baseJpeg });
        var jpeg = File.ReadAllBytes(baseJpeg);

        var tiff = new byte[26];
        "II*\0"u8.CopyTo(tiff);
        BinaryPrimitives.WriteUInt32LittleEndian(tiff.AsSpan(4), 8);      // IFD offset
        BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(8), 1);      // one entry
        BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(10), 0x0112); // Orientation
        BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(12), 3);      // SHORT
        BinaryPrimitives.WriteUInt32LittleEndian(tiff.AsSpan(14), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(18), 6);      // value 6
        var payload = "Exif\0\0"u8.ToArray().Concat(tiff).ToArray();
        var app1 = new byte[] { 0xFF, 0xE1, (byte)((payload.Length + 2) >> 8), (byte)((payload.Length + 2) & 0xFF) }.Concat(payload);

        var path = Path.Combine(_dir, "exif6.jpg");
        File.WriteAllBytes(path, jpeg.Take(2).Concat(app1).Concat(jpeg.Skip(2)).ToArray());
        return path;
    }

    private static async Task<(int Width, int Height)> DecodedSize(string path, MediaMetadata metadata)
    {
        var decoder = new FfmpegVideoDecoder(FfmpegTools.FfmpegLocator, NullLogger<FfmpegVideoDecoder>.Instance);
        await using var stream = await decoder.OpenAsync(new VideoDecodeRequest
        {
            FilePath = path,
            StartTime = metadata.StartTime ?? default,
            FirstSamplePoint = new SourceSamplePoint(0, 1),
            MaxWidth = 4096,
            MaxHeight = 4096,
            Hardware = HardwareDecoding.Disabled
        });
        var frame = await stream.ReadFrameAsync() ?? throw new InvalidOperationException("no frame");
        return (frame.Width, frame.Height);
    }

    private async Task AssertProbeMatchesDecoder(string path, int? rotation, int displayWidth, int displayHeight)
    {
        var result = await Probe.AnalyzeAsync(path);
        var m = result.Metadata!;

        Assert.Equal((320, 180), (m.Width!.Value, m.Height!.Value));   // coded size unchanged
        Assert.Equal(rotation, m.DisplayRotation);
        Assert.Equal((displayWidth, displayHeight), (m.DisplayWidth!.Value, m.DisplayHeight!.Value));
        Assert.False(m.NeedsDisplaySizeProbe);
        Assert.Equal((displayWidth, displayHeight), await DecodedSize(path, m));
    }

    [FfmpegFact]
    public Task Plain_video_is_zero_degrees() => AssertProbeMatchesDecoder(Plain(), 0, 320, 180);

    [FfmpegFact]
    public Task Phone_portrait_rotation_minus_90_is_portrait() =>
        AssertProbeMatchesDecoder(WithDisplayMatrix("rotm90.mp4", "-display_rotation", "-90"), 90, 180, 320);

    [FfmpegFact]
    public Task Rotation_90_is_portrait() =>
        AssertProbeMatchesDecoder(WithDisplayMatrix("rot90.mp4", "-display_rotation", "90"), 270, 180, 320);

    [FfmpegFact]
    public Task Rotation_180_keeps_the_size() =>
        AssertProbeMatchesDecoder(WithDisplayMatrix("rot180.mp4", "-display_rotation", "180"), 180, 320, 180);

    [FfmpegFact]
    public Task Odd_angle_is_unsupported_with_the_decoder_size() =>
        AssertProbeMatchesDecoder(WithDisplayMatrix("rot45.mp4", "-display_rotation", "45"), null, 320, 180);

    [FfmpegFact]
    public Task Mirror_is_unsupported_with_the_decoder_size() =>
        AssertProbeMatchesDecoder(WithDisplayMatrix("hflip.mp4", "-display_hflip"), null, 320, 180);

    [FfmpegFact]
    public Task Mirrored_quarter_turn_is_unsupported_but_transposed() =>
        AssertProbeMatchesDecoder(WithDisplayMatrix("rot90hflip.mp4", "-display_rotation", "90", "-display_hflip"), null, 180, 320);

    [FfmpegFact]
    public Task Exif_orientation_of_a_jpeg_comes_from_the_first_frame() =>
        AssertProbeMatchesDecoder(ExifJpeg(), 90, 180, 320);

    [FfmpegFact]
    public async Task Audio_only_media_has_no_display_size()
    {
        var path = Path.Combine(_dir, "tone.wav");
        TestMedia.Run(FfmpegTools.Ffmpeg!, new[] { "-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i", "sine=d=1", path });

        var m = (await Probe.AnalyzeAsync(path)).Metadata!;

        Assert.Null(m.Width);
        Assert.Null(m.DisplayWidth);
        Assert.Null(m.DisplayRotation);
        Assert.False(m.NeedsDisplaySizeProbe);
    }
}
