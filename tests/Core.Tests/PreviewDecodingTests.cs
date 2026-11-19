using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Composition;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Core.Playback;
using Xunit;

namespace AiVideoEditor.Core.Tests;

/// <summary>D071 §2–§3: the Preview's qualities, which videos get a proxy and the proxy's size.</summary>
public sealed class PreviewDecodingTests
{
    private static MediaAsset Video(int width, int height, string codec = "h264", bool missing = false, MediaKind kind = MediaKind.Video,
        MediaAnalysisStatus status = MediaAnalysisStatus.Completed) => new()
    {
        FilePath = "C:/v.mp4", Kind = kind, IsMissing = missing, AnalysisStatus = status,
        Metadata = new MediaMetadata { Width = width, Height = height, DisplayWidth = width, DisplayHeight = height, VideoCodec = codec, PixelFormat = "yuv420p", Duration = MediaTime.FromSeconds(10) }
    };

    [Theory]
    [InlineData(PreviewQuality.High, 3840, 2160, 2560, 1440)]
    [InlineData(PreviewQuality.Medium, 3840, 2160, 1920, 1080)]
    [InlineData(PreviewQuality.Low, 3840, 2160, 1280, 720)]
    [InlineData(PreviewQuality.Low, 2160, 3840, 720, 1280)]      // a portrait picture gets the box upright
    [InlineData(PreviewQuality.Medium, 1080, 1920, 1080, 1920)]
    public void The_bound_is_the_quality_box_turned_like_the_picture(PreviewQuality quality, int w, int h, int bw, int bh) =>
        Assert.Equal(new FrameSize(bw, bh), PreviewDecoding.Bound(quality, w, h));

    [Fact]
    public void The_original_and_an_unknown_size_have_no_bound()
    {
        Assert.Null(PreviewDecoding.Bound(PreviewQuality.Original, 3840, 2160));
        Assert.Null(PreviewDecoding.Bound(PreviewQuality.Medium, null, 1080));
    }

    [Fact]
    public void Medium_is_the_default_quality() =>
        Assert.Equal(PreviewQuality.Medium, new UserSettings().PreviewQuality);

    [Fact]
    public void A_proxy_is_made_for_4K_or_HEVC_below_the_original()
    {
        Assert.True(PreviewDecoding.NeedsProxy(Video(3840, 2160), PreviewQuality.Medium));
        Assert.True(PreviewDecoding.NeedsProxy(Video(2160, 3840), PreviewQuality.Low));
        Assert.True(PreviewDecoding.NeedsProxy(Video(1920, 1080, "hevc"), PreviewQuality.High));
        Assert.True(PreviewDecoding.NeedsProxy(Video(2704, 1520), PreviewQuality.High));     // 2.7K is above 1920 too

        Assert.False(PreviewDecoding.NeedsProxy(Video(1920, 1080), PreviewQuality.Low));      // a light 1080p H.264
        Assert.False(PreviewDecoding.NeedsProxy(Video(3840, 2160), PreviewQuality.Original));
        Assert.False(PreviewDecoding.NeedsProxy(Video(3840, 2160, missing: true), PreviewQuality.Medium));
        Assert.False(PreviewDecoding.NeedsProxy(Video(3840, 2160, kind: MediaKind.Image), PreviewQuality.Medium));
        Assert.False(PreviewDecoding.NeedsProxy(Video(3840, 2160, status: MediaAnalysisStatus.Pending), PreviewQuality.Medium));
    }

    [Fact]
    public void No_proxy_while_the_colour_is_unknown_but_a_refresh_alone_does_not_stop_it()
    {
        // D033 T3: metadata saved before the colour probe (no pixel format) may still turn out HDR.
        var unknown = Video(3840, 2160);
        unknown.Metadata!.PixelFormat = null;
        Assert.True(unknown.Metadata.NeedsColorProbe);
        Assert.False(PreviewDecoding.NeedsProxy(unknown, PreviewQuality.Medium));

        var refreshing = Video(3840, 2160);
        refreshing.IsMetadataRefreshing = true;   // transient; the refresh may end without an event
        Assert.True(PreviewDecoding.NeedsProxy(refreshing, PreviewQuality.Medium));
    }

    [Theory]
    [InlineData(PreviewQuality.Low, 3840, 2160, 1280, 720)]
    [InlineData(PreviewQuality.Medium, 2160, 3840, 1080, 1920)]
    [InlineData(PreviewQuality.High, 1920, 1080, 1920, 1080)]     // never enlarged
    [InlineData(PreviewQuality.Low, 4096, 2160, 1280, 676)]       // DCI 4K: even sides
    [InlineData(PreviewQuality.Medium, 2704, 1520, 1920, 1080)]
    public void The_proxy_fits_the_bound_with_even_sides(PreviewQuality quality, int w, int h, int pw, int ph)
    {
        var size = PreviewDecoding.ProxySize(quality, w, h);
        Assert.Equal(new FrameSize(pw, ph), size);
        Assert.Equal(0, size.Width % 2);
        Assert.Equal(0, size.Height % 2);
    }

    [Fact]
    public void A_quality_out_of_range_in_the_settings_file_is_the_default() =>
        Assert.Equal(PreviewDecoding.DefaultQuality, new UserSettings { PreviewQuality = (PreviewQuality)42 }.Normalized().PreviewQuality);
}
