using AiVideoEditor.Core.Color;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Media.Caching;
using AiVideoEditor.Media.Thumbnails;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiVideoEditor.Video.Tests;

/// <summary>
/// Phase 18 Step 18.5 (D033 T3, T4): a thumbnail of an HDR video is decoded with the same conversion as the Preview (its
/// colours equal the rule), under the rule version 2 and a name that carries the conversion — so the thumbnail made while
/// the colour was unknown is a miss once the probe finds HDR.
/// </summary>
public sealed class HdrThumbnailTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "aive-hdr-thumbs", Guid.NewGuid().ToString("N"));

    public HdrThumbnailTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private ThumbnailService Service() => new(
        new FfmpegVideoDecoder(FfmpegTools.FfmpegLocator, NullLogger<FfmpegVideoDecoder>.Instance,
            new FfmpegVideoDecoderSettings { FrameTimeout = FfmpegTools.ProcessTimeout },
            new HdrLutStore(Path.Combine(_dir, "luts"), NullLogger<HdrLutStore>.Instance)),
        NullLogger<ThumbnailService>.Instance);

    private static async Task<MediaAsset> Asset(string path)
    {
        var result = await FfmpegTools.Analysis().AnalyzeAsync(path);
        return new MediaAsset
        {
            FilePath = path, Kind = MediaKind.Video, AnalysisStatus = MediaAnalysisStatus.Completed, Metadata = result.Metadata
        };
    }

    private static double[] Patch(Thumbnail thumbnail, int patch)
    {
        var (x0, y0, w, h) = HdrMedia.Centre(patch % HdrMedia.Columns, patch / HdrMedia.Columns, thumbnail.Width, thumbnail.Height);
        var sum = new double[3];
        var pixels = thumbnail.Pixels.Span;
        for (var y = y0; y < y0 + h; y++)
        for (var x = x0; x < x0 + w; x++)
        {
            var i = (y * thumbnail.Width + x) * 4;
            sum[0] += pixels[i + 2];
            sum[1] += pixels[i + 1];
            sum[2] += pixels[i];
        }
        return sum.Select(s => s / (w * h)).ToArray();
    }

    [FfmpegFact]
    public async Task An_HDR_thumbnail_has_the_rules_colours_and_its_own_cache_name()
    {
        var asset = await Asset(HdrMedia.Hlg);
        var cache = Path.Combine(_dir, "thumbs");

        var hdr = await Service().GetOrCreateAsync(asset, cache);

        Assert.NotNull(hdr);
        var name = Path.GetFileName(Assert.Single(Directory.GetFiles(cache)));
        Assert.Contains($"-v{ThumbnailService.CurrentRuleVersion}-hlg1000.thumb", name);
        Assert.Equal(2, ThumbnailService.CurrentRuleVersion);
        for (var patch = 0; patch < HdrMedia.Patches.Length; patch++)
        {
            var (r, g, b) = HdrMedia.Patches[patch];
            var expected = new double[3];
            HdrToSdr.Convert(HdrTransfer.Hlg, 1000, new[] { r / 255.0, g / 255.0, b / 255.0 }, expected);
            Assert.True(HdrMedia.DeltaE(Patch(hdr!, patch), expected.Select(v => v * 255).ToArray()) <= 1.5,
                $"patch {patch}");   // a 160 × 90 thumbnail: the patches' edges are scaled in
        }
    }

    [FfmpegFact]
    public async Task The_thumbnail_made_while_the_colour_was_unknown_is_a_miss_once_the_probe_finds_HDR()
    {
        var probed = await Asset(HdrMedia.Hlg);
        var unknown = new MediaAsset
        {
            Id = probed.Id, FilePath = probed.FilePath, Kind = MediaKind.Video, AnalysisStatus = MediaAnalysisStatus.Completed,
            Metadata = new MediaMetadata
            {
                Duration = probed.Metadata!.Duration, Width = probed.Metadata.Width, Height = probed.Metadata.Height,
                DisplayWidth = probed.Metadata.DisplayWidth, DisplayHeight = probed.Metadata.DisplayHeight, StartTime = probed.Metadata.StartTime
            }
        };
        var cache = Path.Combine(_dir, "thumbs");
        var service = Service();

        var sdr = await service.GetOrCreateAsync(unknown, cache);
        Assert.NotNull(sdr);
        Assert.Null(service.TryGetCached(probed, cache));             // its name has no conversion: a miss for the HDR asset

        var hdr = await service.GetOrCreateAsync(probed, cache);
        Assert.False(hdr!.Pixels.Span.SequenceEqual(sdr!.Pixels.Span));
        Assert.Single(Directory.GetFiles(cache));                   // the SDR one is replaced, not kept
    }
}
