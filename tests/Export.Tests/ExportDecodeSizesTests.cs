using System.Collections.Immutable;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Composition;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Export;
using AiVideoEditor.Core.Playback;
using AiVideoEditor.Timeline.Tests.Playback;
using Xunit;

namespace AiVideoEditor.Export.Tests;

/// <summary>D077 §2: a video clip is decoded no larger than it is ever drawn on the output; full resolution wherever that
/// can't be known in advance.</summary>
public sealed class ExportDecodeSizesTests
{
    private static readonly FrameRate Rate = FrameRate.Fps25;
    private static readonly FrameSize Uhd = new(3840, 2160);
    private static readonly FrameSize FullHd = new(1920, 1080);
    private const string File = @"C:\media\a.mp4";

    private static MediaTime F(long frame) => MediaTime.FromFrame(frame, Rate);

    private static (PlaybackSnapshot Snapshot, Guid Clip) Snapshot(FrameSize canvas, Func<PictureSpan, PictureSpan>? change = null,
        long frames = 50, FrameSize? source = null, SpanStatus status = SpanStatus.Video)
    {
        var (clip, asset) = (Guid.NewGuid(), Guid.NewGuid());
        var span = new PictureSpan(clip, asset, status, F(0), F(frames), MediaTime.Zero) { SourceSize = source ?? Uhd };
        if (change is not null) span = change(span);
        var snapshot = new PlaybackSnapshot(1, Rate, F(frames),
            ImmutableArray.Create(new VideoLayer(Guid.NewGuid(), ImmutableArray.Create(span))),
            ImmutableArray<AudioSpan>.Empty,
            ImmutableDictionary<Guid, PlaybackAsset>.Empty.Add(asset,
                new PlaybackAsset(asset, File, status == SpanStatus.StillImage ? MediaKind.Image : MediaKind.Video, MediaTime.Zero, Rate)),
            canvas);
        return (snapshot, clip);
    }

    private static IReadOnlyDictionary<Guid, FrameSize> Sizes(PlaybackSnapshot snapshot, FrameSize? output = null, ExportRange? range = null) =>
        ExportDecodeSizes.For(snapshot, ExportOutput.For(snapshot, range, output));

    private static PictureSpan Scaled(PictureSpan span, double scale) => span with { Visual = span.Visual with { Scale = scale } };

    [Fact]
    public void A_4K_source_in_a_Full_HD_export_of_a_4K_project_is_decoded_at_Full_HD()
    {
        var (snapshot, clip) = Snapshot(Uhd);
        Assert.Equal(FullHd, Sizes(snapshot, FullHd)[clip]);
    }

    [Fact]
    public void A_source_drawn_at_its_own_size_is_decoded_as_it_is()
    {
        var (snapshot, _) = Snapshot(Uhd);
        Assert.Empty(Sizes(snapshot));                                       // 4K on a 4K canvas: one source pixel per pixel
        var (enlarged, _) = Snapshot(FullHd, s => Scaled(s, 2));                // 4K at 200 % on Full HD: drawn at 4K again
        Assert.Empty(Sizes(enlarged));
    }

    [Fact]
    public void A_picture_in_picture_is_decoded_at_its_drawn_size()
    {
        var (snapshot, clip) = Snapshot(Uhd, s => Scaled(s, 0.5));
        Assert.Equal(new FrameSize(1920, 1080), Sizes(snapshot)[clip]);
        var (fullHd, small) = Snapshot(FullHd, s => Scaled(s, 0.25));        // 4K source at 25 % of a Full HD canvas
        Assert.Equal(new FrameSize(480, 270), Sizes(fullHd)[small]);
    }

    [Fact]
    public void The_largest_size_over_the_whole_clip_decides_including_keyframes()
    {
        // A zoom from 50 % to 100 % of a Full HD canvas over the clip: at its end 4K is drawn at Full HD — the clip is decoded
        // at Full HD, never at the 50 % of its first frame.
        var keys = TransformKeyframes.Of(new[]
        {
            new TransformKeyframe(Guid.NewGuid(), F(0), 0, 0, 0.5),
            new TransformKeyframe(Guid.NewGuid(), F(49), 0, 0, 1.0)
        });
        var (snapshot, clip) = Snapshot(FullHd, s => s with { Keyframes = keys }, frames: 50);
        Assert.Equal(FullHd, Sizes(snapshot)[clip]);
        // Only the exported range counts: its first ten frames reach at most ~60 %.
        var range = Sizes(snapshot, range: new ExportRange(0, 10));
        Assert.True(range[clip].Width is > 1100 and < 1300, $"{range[clip]}");
    }

    [Fact]
    public void A_crop_enlarges_the_picture_so_the_clip_is_decoded_larger()
    {
        // The left half of a 4K source fitted into a Full HD canvas: 1920 source pixels across 1080 / 2160 of the height…
        var (snapshot, clip) = Snapshot(Uhd, s => s with { Visual = s.Visual with { Crop = new CropRect(0, 0, 0.5, 0) } });
        var sizes = Sizes(snapshot, FullHd);
        // …the crop is laid out to fit the canvas as the whole picture is: the same factor 0.5 of the source → Full HD.
        Assert.Equal(FullHd, sizes[clip]);
    }

    [Fact]
    public void Clips_whose_drawn_size_is_unknown_in_advance_are_decoded_at_full_resolution()
    {
        var (unknown, _) = Snapshot(Uhd, s => s with { SourceSize = null });
        Assert.Empty(Sizes(unknown, FullHd));
        var (still, _) = Snapshot(Uhd, status: SpanStatus.StillImage);
        Assert.Empty(Sizes(still, FullHd));
    }

    [Fact]
    public void A_clip_with_a_Follow_camera_is_decoded_at_full_resolution()
    {
        // Its framing is evaluated per source frame (it may zoom in): its layout alone can't tell its drawn size.
        var follow = new AiVideoEditor.Core.People.FollowFraming(Array.Empty<AiVideoEditor.Core.People.FollowFramingPart>(),
            F(0), F(50), VisualProperties.Default, null, FullHd);
        var (snapshot, _) = Snapshot(Uhd, s => s with { Follow = follow });
        Assert.Empty(Sizes(snapshot, FullHd));
    }

    [Fact]
    public void A_source_is_decoded_smaller_only_below_the_threshold()
    {
        var (barely, _) = Snapshot(Uhd, s => Scaled(s, 0.96));                // 4 % less: not worth a scaler
        Assert.Empty(Sizes(barely));
        var (smaller, clip) = Snapshot(Uhd, s => Scaled(s, 0.9));
        Assert.Equal(new FrameSize(3456, 1944), Sizes(smaller)[clip]);
    }

    [Fact]
    public void The_sizes_are_computed_with_the_exports_cancellation()
    {
        var (snapshot, _) = Snapshot(Uhd);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => ExportDecodeSizes.For(snapshot, ExportOutput.For(snapshot, size: FullHd), cancelled.Token));
    }

    [Fact]
    public void The_largest_scale_of_a_transform_is_its_largest_singular_value()
    {
        Assert.Equal(0.5, ExportDecodeSizes.LargestScale(Affine2D.Scaling(0.5)), 12);
        Assert.Equal(0.5, ExportDecodeSizes.LargestScale(Affine2D.Rotation(37).Compose(Affine2D.Scaling(0.5))), 12);
        Assert.Equal(2, ExportDecodeSizes.LargestScale(new Affine2D(2, 0, 0, 0.1, 5, 5)), 12);
        // A non-uniform scale under a rotation: the largest stretch, larger than any column's length alone.
        var skew = Affine2D.Rotation(45).Compose(new Affine2D(2, 0, 0, 0.5, 0, 0)).Compose(Affine2D.Rotation(20));
        Assert.Equal(2, ExportDecodeSizes.LargestScale(skew), 9);
    }

    [Fact]
    public async Task The_frame_source_asks_the_decoder_for_the_drawn_size_and_can_be_told_not_to()
    {
        var (snapshot, _) = Snapshot(Uhd);
        var output = ExportOutput.For(snapshot, size: FullHd);

        var decoder = new FakeVideoDecoder();
        decoder.Add(File, new FakeSource(Rate, 100));
        await using (var source = new ExportFrameSource(snapshot, decoder, output: output))
            await source.GetFrameAsync(0);
        var request = Assert.Single(decoder.Requests);
        Assert.Equal((1920, 1080), (request.MaxWidth, request.MaxHeight));

        var full = new FakeVideoDecoder();
        full.Add(File, new FakeSource(Rate, 100));
        await using (var source = new ExportFrameSource(snapshot, full, new ExportDecodeSettings { DecodeAtDrawnSize = false }, output))
            await source.GetFrameAsync(0);
        var unchanged = Assert.Single(full.Requests);
        Assert.Equal((ExportDecodeSettings.FullResolutionBound, ExportDecodeSettings.FullResolutionBound), (unchanged.MaxWidth, unchanged.MaxHeight));
    }
}
