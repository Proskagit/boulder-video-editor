using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Holds;
using AiVideoEditor.Core.Playback;
using AiVideoEditor.Infrastructure;
using AiVideoEditor.Infrastructure.Configuration;
using AiVideoEditor.UI.Services;
using AiVideoEditor.Video;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace AiVideoEditor.Vision.ModelTests;

/// <summary>
/// Phase 19 Step 19.5, local (D034 §1, §7): the application's markup pipeline on a real E1 clip with the real model — the
/// markup frame decoded by <see cref="HoldMarkupFrameSource"/> (the app's decoder, the Preview's frame choice) is the E1
/// reference frame (t = 0.5 s), and a reference click on it gives a visible candidate whose contour and colour samples
/// make a valid hold.
/// </summary>
public sealed class MarkupPipelineTests(ITestOutputHelper output)
{
    [Fact]
    public async Task The_markup_frame_is_the_reference_frame_and_a_reference_click_becomes_a_valid_hold()
    {
        var path = ReferenceData.Path_("clips/12x10/1014(6).mp4");
        var options = Microsoft.Extensions.Options.Options.Create(new FfmpegOptions());
        var analysis = await new FfprobeMediaAnalysisService(new FfprobeLocator(options, NullLogger<FfprobeLocator>.Instance),
            NullLogger<FfprobeMediaAnalysisService>.Instance).AnalyzeAsync(path);
        var metadata = analysis.Metadata ?? throw new InvalidOperationException(analysis.ErrorMessage);
        var rate = metadata.FrameRate ?? FrameRate.Fps25;
        var asset = new MediaAsset { FilePath = path, Kind = MediaKind.Video, Metadata = metadata, AnalysisStatus = MediaAnalysisStatus.Completed };
        var clip = new VideoClip { MediaAssetId = asset.Id, TimelineStart = MediaTime.Zero, Duration = MediaTime.FromSeconds(5), SourceIn = MediaTime.Zero, SourceOut = MediaTime.FromSeconds(5) };

        var decoder = new FfmpegVideoDecoder(new FfmpegLocator(options, NullLogger<FfmpegLocator>.Instance), NullLogger<FfmpegVideoDecoder>.Instance);
        var markup = await new HoldMarkupFrameSource(decoder).GetAsync(clip, asset, MediaTime.FromSeconds(0.5), rate, CancellationToken.None);
        var reference = ReferenceData.Frame("set12/frames/c6.png");
        Assert.Equal((reference.Width, reference.Height), (markup.Frame.Width, markup.Frame.Height));

        var (mean, max) = Difference(markup.Frame, reference);
        output.WriteLine($"markup frame vs reference: mean |Δ| {mean:F3}, max {max}, source time {markup.SourceTime}");
        Assert.True(mean < 1.0, $"The markup frame differs from the reference frame: mean |Δ| {mean:F3}.");

        var click = ReferenceData.Clicks().First(c => c.Clip == "c6");
        using var service = new OnnxHoldSegmentationService(ReferenceData.ModelDir);
        Assert.True((await service.EnsureReadyAsync()).IsReady);
        using var frame = await service.PrepareFrameAsync(markup.Frame);
        var set = await frame.PredictAsync(new[] { new Core.Composition.PointD(click.X, click.Y) });
        var chosen = HoldCandidateRules.Choose(set) ?? throw new InvalidOperationException("No visible candidate.");
        var mask = set.Masks[chosen];
        var (component, _) = MaskContour.LargestComponent(mask.Pixels, mask.Width, mask.Height);
        var contour = MaskContour.Outer(component, mask.Width, mask.Height);
        var (surface, wall) = HoldColorSampler.Sample(markup.Frame, component, Array.Empty<IReadOnlyList<NormalizedPoint>>());
        var hold = new Hold(Guid.NewGuid(), contour, surface, wall, markup.SourceTime);
        Assert.Null(HoldMarkupRules.ValidateHold(hold));
        output.WriteLine($"c6 #{click.Id}: {contour.Length} contour points, surface {surface}, wall {wall}");
    }

    /// <summary>The mean and largest absolute difference of the colour channels (alpha ignored).</summary>
    private static (double Mean, int Max) Difference(DecodedFrame first, DecodedFrame second)
    {
        var a = first.Pixels.Span;
        var b = second.Pixels.Span;
        double sum = 0;
        var max = 0;
        for (var i = 0; i < a.Length; i++)
        {
            if (i % 4 == 3) continue;
            var d = Math.Abs(a[i] - b[i]);
            sum += d;
            max = Math.Max(max, d);
        }
        return (sum / (a.Length / 4 * 3), max);
    }
}
