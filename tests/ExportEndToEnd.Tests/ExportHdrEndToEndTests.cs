using AiVideoEditor.Core.Color;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Media.Thumbnails;
using AiVideoEditor.Video;
using AiVideoEditor.Video.Tests;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;
using static AiVideoEditor.ExportEndToEnd.Tests.EndToEnd;

namespace AiVideoEditor.ExportEndToEnd.Tests;

/// <summary>
/// Phase 18 Step 18.6 (D033 §1, T4; DEVELOPMENT_PLAN 18.6): an HLG and a PQ scene exported for real — the export canvas
/// is byte for byte the Preview's (the same decode graph and LUT), its patches equal the HDR → SDR rule within ΔE76 ≤ 1,
/// and the Media Browser thumbnail of the same file has the same flat colours within 1 code value. The SDR scenes of the
/// parity suite are unchanged (they run as before).
/// </summary>
[Collection(EndToEndCollection.Name)]
public sealed class ExportHdrEndToEndTests
{
    private readonly E2EMedia _media;
    private readonly ITestOutputHelper _output;

    public ExportHdrEndToEndTests(E2EMedia media, ITestOutputHelper output)
    {
        _media = media;
        _output = output;
    }

    private static MediaAsset Asset(string path)
    {
        var result = FfmpegTools.Analysis().AnalyzeAsync(path).GetAwaiter().GetResult();
        Assert.NotNull(result.Metadata);
        return new MediaAsset { FilePath = path, Kind = MediaKind.Video, AnalysisStatus = MediaAnalysisStatus.Completed, Metadata = result.Metadata };
    }

    /// <summary>The mean R, G, B of a patch's centre in a BGRA image of <paramref name="width"/> × <paramref name="height"/>.</summary>
    private static double[] Mean(ReadOnlySpan<byte> bgra, int width, int height, int patch)
    {
        var (x0, y0, w, h) = HdrMedia.Centre(patch % HdrMedia.Columns, patch / HdrMedia.Columns, width, height);
        var sum = new double[3];
        for (var y = y0; y < y0 + h; y++)
        for (var x = x0; x < x0 + w; x++)
        {
            var i = (y * width + x) * 4;
            sum[0] += bgra[i + 2];
            sum[1] += bgra[i + 1];
            sum[2] += bgra[i];
        }
        return sum.Select(s => s / (w * h)).ToArray();
    }

    private static double[] Rule(HdrConversion hdr, int patch)
    {
        var (r, g, b) = HdrMedia.Patches[patch];
        var output = new double[3];
        HdrToSdr.Convert(hdr.Transfer, hdr.PeakNits, new[] { r / 255.0, g / 255.0, b / 255.0 }, output);
        return output.Select(v => v * 255).ToArray();
    }

    public static IEnumerable<object[]> Scenes() => new[]
    {
        new object[] { "hlg", new HdrConversion(HdrTransfer.Hlg, 1000, false) },
        new object[] { "pq-meta", new HdrConversion(HdrTransfer.Pq, 1500, false) }
    };

    [FfmpegTheory]
    [MemberData(nameof(Scenes))]
    public async Task An_HDR_scene_exports_as_the_Preview_shows_it_with_the_rules_colours_and_the_thumbnails(string name,
        HdrConversion hdr)
    {
        var asset = Asset(name == "hlg" ? HdrMedia.Hlg : HdrMedia.PqWithMetadata);
        Assert.Equal(hdr, HdrColor.Classify(asset.Kind, asset.Metadata).Conversion);
        var p = new ProjectBuilder(FrameRate.Fps25, HdrMedia.Width, HdrMedia.Height);
        p.Video(p.VideoTrack(), asset, 0, 8);

        var run = await ExportProject(p.Project, Path.Combine(_media.OutputFolder(), $"hdr-{name}.mp4"));
        AssertValidMp4(run);
        Assert.Equal(hdr, run.Job.Snapshot.Assets[asset.Id].Hdr);

        // The Preview draws exactly the export's canvas: the same graph, the same LUT.
        foreach (var n in new long[] { 0, 5 })
        {
            var preview = await Preview(run.Job.Snapshot, n);
            Assert.True(preview.Pixels.AsSpan().SequenceEqual(run.Canvases[(int)n]), $"{name} frame {n}: the Preview differs from the export");
        }

        // The export's colours are the rule's.
        var canvas = run.Canvases[5];
        for (var patch = 0; patch < HdrMedia.Patches.Length; patch++)
        {
            var de = HdrMedia.DeltaE(Mean(canvas, HdrMedia.Width, HdrMedia.Height, patch), Rule(hdr, patch));
            _output.WriteLine($"{name} {HdrMedia.Describe(patch)}: export vs rule ΔE76 {de:0.00}");
            Assert.True(de <= 1.0, $"{name} patch {patch}: ΔE76 {de:0.00}");
        }

        // The thumbnail of the same file: the same flat colours within 1 code value.
        var thumbnails = new ThumbnailService(
            new FfmpegVideoDecoder(FfmpegTools.FfmpegLocator, NullLogger<FfmpegVideoDecoder>.Instance, luts: Luts),
            NullLogger<ThumbnailService>.Instance);
        var thumbnail = await thumbnails.GetOrCreateAsync(asset, Path.Combine(_media.OutputFolder(), "thumbs"));
        Assert.NotNull(thumbnail);
        for (var patch = 0; patch < HdrMedia.Patches.Length; patch++)
        {
            var exported = Mean(canvas, HdrMedia.Width, HdrMedia.Height, patch);
            var thumb = Mean(thumbnail!.Pixels.Span, thumbnail.Width, thumbnail.Height, patch);
            var worst = exported.Zip(thumb, (a, b) => Math.Abs(a - b)).Max();
            _output.WriteLine($"{name} {HdrMedia.Describe(patch)}: thumbnail vs export {worst:0.00} code values");
            Assert.True(worst <= 1.0, $"{name} patch {patch}: the thumbnail differs by {worst:0.00}");
        }
    }
}
