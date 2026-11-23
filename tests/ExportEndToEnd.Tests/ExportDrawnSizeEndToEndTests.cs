using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Video.Tests;
using Xunit;
using static AiVideoEditor.ExportEndToEnd.Tests.EndToEnd;

namespace AiVideoEditor.ExportEndToEnd.Tests;

/// <summary>
/// D077 §2: a clip drawn smaller than its source is decoded at its drawn size — the real ffmpeg decoder and the export's
/// painter. Its canvas is then the source scaled by the decoder's own scaler and drawn 1:1, in the same place as the full-size
/// decode drawn by the painter (<see cref="EndToEnd.PainterParity"/>): the same layout, the same frames, only the resampling
/// changes.
/// </summary>
[Collection(EndToEndCollection.Name)]
public sealed class ExportDrawnSizeEndToEndTests
{
    private readonly E2EMedia _media;

    public ExportDrawnSizeEndToEndTests(E2EMedia media) => _media = media;

    /// <summary>Frame <paramref name="index"/> of <paramref name="path"/> scaled down to <paramref name="width"/> ×
    /// <paramref name="height"/> by the decoder's own filter (<c>FfmpegVideoDecoder</c>), as BGRA.</summary>
    private static byte[] ScaledSourceFrame(string path, long index, int width, int height) =>
        EncoderHarness.Run(FfmpegTools.Ffmpeg!, "-v", "error", "-i", path, "-vf",
            $"select=eq(n\\,{index}),scale=w='min(iw,{width})':h='min(ih,{height})':force_original_aspect_ratio=decrease,format=bgra",
            "-frames:v", "1", "-f", "rawvideo", "-pix_fmt", "bgra", "-");

    [FfmpegFact]
    public async Task A_Full_HD_source_on_a_small_canvas_is_the_decoders_scaled_frame_drawn_one_to_one()
    {
        var p = new ProjectBuilder(FrameRate.Fps25);                                  // 320 × 180
        var source = _media.Pattern1080();
        p.Video(p.VideoTrack(), source, 0, 25);
        var drawn = await ExportProject(p.Project, Path.Combine(_media.OutputFolder(), "drawn.mp4"));
        AssertValidMp4(drawn);
        var full = await ExportProject(p.Project, Path.Combine(_media.OutputFolder(), "full.mp4"), PainterParity);

        Assert.Equal(full.Canvases.Count, drawn.Canvases.Count);
        foreach (var n in new[] { 0, 12, 24 })
        {
            // 1920 × 1080 decoded at 320 × 180 and drawn 1:1: the decoder's scaled frame itself (the canvas is opaque).
            var expected = ScaledSourceFrame(source.FilePath, n, 320, 180);
            Assert.True(MeanAbs(drawn.Canvases[n], expected) < 0.5, $"frame {n}: mean {MeanAbs(drawn.Canvases[n], expected):0.###}");
            // …which the full-size decode is not (the painter resamples it): the check above tells the two paths apart.
            Assert.True(MeanAbs(full.Canvases[n], expected) > 1, $"frame {n}: full-size mean {MeanAbs(full.Canvases[n], expected):0.###}");
            // The full-size decode shrunk by the painter is the same picture, resampled otherwise.
            Assert.True(MeanAbs(drawn.Canvases[n], full.Canvases[n]) < 6, $"frame {n}: mean {MeanAbs(drawn.Canvases[n], full.Canvases[n]):0.###}");
        }
        AssertNoFfmpegLeft();
    }

    [FfmpegFact]
    public async Task A_4K_source_in_a_vertical_project_keeps_its_place_and_the_black_bars()
    {
        var p = new ProjectBuilder(FrameRate.Fps25, 180, 320);                         // 3840 × 2160 contained: 180 × 101.25
        p.Video(p.VideoTrack(), _media.Pattern4K(), 0, 25);
        var drawn = await ExportProject(p.Project, Path.Combine(_media.OutputFolder(), "vertical-drawn.mp4"));
        AssertValidMp4(drawn);
        var full = await ExportProject(p.Project, Path.Combine(_media.OutputFolder(), "vertical-full.mp4"), PainterParity);

        foreach (var n in new[] { 0, 24 })
        {
            var (a, b) = (drawn.Canvases[n], full.Canvases[n]);
            // The bars: every row above and below the picture is black in both (the picture spans rows ≈ 109–210).
            foreach (var y in new[] { 0, 50, 105, 215, 260, 319 })
                for (var x = 0; x < 180; x += 7)
                {
                    Assert.Equal((byte)0, a[(y * 180 + x) * 4]);
                    Assert.Equal(Pixel(a, 180, x, y), Pixel(b, 180, x, y));
                }
            Assert.True(MeanAbs(a, b) < 6, $"frame {n}: mean {MeanAbs(a, b):0.###}");
        }
        AssertNoFfmpegLeft();
    }
}
