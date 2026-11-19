using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Composition;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Core.Playback;
using Xunit;

namespace AiVideoEditor.Core.Tests;

/// <summary>D071 §2: the Preview's qualities and the bound each gives a picture (amendment 3: no proxies).</summary>
public sealed class PreviewDecodingTests
{
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
    public void A_quality_out_of_range_in_the_settings_file_is_the_default() =>
        Assert.Equal(PreviewDecoding.DefaultQuality, new UserSettings { PreviewQuality = (PreviewQuality)42 }.Normalized().PreviewQuality);
}
