using AiVideoEditor.Core.Color;
using Xunit;

namespace AiVideoEditor.Core.Tests;

/// <summary>D069 §7: the colour correction's per-pixel function and «Автоулучшение».</summary>
public class ColorCorrectionTests
{
    private static (int R, int G, int B, int A) Apply(ColorCorrection c, int r, int g, int b, int a = 255)
    {
        var px = new[] { (byte)b, (byte)g, (byte)r, (byte)a };
        ColorAdjust.For(c).ApplyRow(px);
        return (px[2], px[1], px[0], px[3]);
    }

    private static ColorCorrection Only(int index, int value) => ColorCorrection.Neutral.With(index, value);

    [Fact]
    public void A_neutral_correction_changes_no_value()
    {
        for (var v = 0; v < 256; v += 5)
            Assert.Equal((v, 255 - v, v / 2, 128), Apply(ColorCorrection.Neutral, v, 255 - v, v / 2, 128));
    }

    [Theory]
    [InlineData(0)]   // exposure
    [InlineData(1)]   // brightness
    [InlineData(4)]   // shadows
    public void A_positive_lift_makes_a_mid_grey_brighter_and_a_negative_one_darker(int index)
    {
        Assert.True(Apply(Only(index, 60), 100, 100, 100).R > 100);
        Assert.True(Apply(Only(index, -60), 100, 100, 100).R < 100);
    }

    [Fact]
    public void Highlights_work_on_the_bright_values_and_keep_black_and_white()
    {
        Assert.True(Apply(Only(3, -80), 220, 220, 220).R < 220);
        Assert.Equal(0, Apply(Only(3, -80), 0, 0, 0).R);
        Assert.Equal(255, Apply(Only(4, 80), 255, 255, 255).R);
    }

    [Fact]
    public void Contrast_spreads_the_values_about_the_middle()
    {
        var c = Only(2, 50);
        Assert.True(Apply(c, 200, 200, 200).R > 200);
        Assert.True(Apply(c, 50, 50, 50).R < 50);
    }

    [Fact]
    public void Saturation_minus_100_makes_grey_and_plus_widens_the_channels()
    {
        var grey = Apply(Only(5, -100), 200, 100, 50);
        Assert.Equal(grey.R, grey.G);
        Assert.Equal(grey.G, grey.B);
        var vivid = Apply(Only(5, 60), 200, 100, 50);
        Assert.True(vivid.R - vivid.B > 150);
    }

    [Fact]
    public void Temperature_warms_and_tint_moves_green_against_magenta()
    {
        var warm = Apply(Only(6, 70), 128, 128, 128);
        Assert.True(warm.R > warm.B);
        var magenta = Apply(Only(7, 70), 128, 128, 128);
        Assert.True(magenta.G < magenta.R);
    }

    [Fact]
    public void Alpha_is_kept_and_the_function_is_deterministic()
    {
        var c = new ColorCorrection(10, -20, 30, -40, 50, -60, 70, -80);
        Assert.Equal(77, Apply(c, 10, 200, 90, 77).A);
        Assert.Equal(Apply(c, 10, 200, 90), Apply(c, 10, 200, 90));
    }

    [Fact]
    public void The_range_is_minus_100_to_100()
    {
        Assert.True(new ColorCorrection(100, -100, 0, 0, 0, 0, 0, 0).IsValid);
        Assert.False(Only(2, 101).IsValid);
        Assert.Equal(100, Only(2, 150).Clamped().Contrast);
    }

    private static (byte[] Pixels, int Width, int Height, int Stride) Image(int r, int g, int b, Func<int, int>? vary = null)
    {
        const int w = 64, h = 32;
        var px = new byte[w * h * 4];
        for (var i = 0; i < w * h; i++)
        {
            var d = vary?.Invoke(i) ?? 0;
            px[i * 4] = (byte)Math.Clamp(b + d, 0, 255);
            px[i * 4 + 1] = (byte)Math.Clamp(g + d, 0, 255);
            px[i * 4 + 2] = (byte)Math.Clamp(r + d, 0, 255);
            px[i * 4 + 3] = 255;
        }
        return (px, w, h, w * 4);
    }

    [Fact]
    public void Auto_enhance_lifts_a_dark_picture_and_stretches_its_narrow_range()
    {
        var dark = AutoEnhance.Suggest(AutoEnhance.Measure(new[] { Image(40, 40, 40, i => i % 20) }));

        Assert.True(dark.Exposure > 0);
        Assert.True(dark.Contrast > 0);
        Assert.True(dark.IsValid);
    }

    [Fact]
    public void Auto_enhance_warms_a_blue_cast_and_leaves_a_balanced_well_exposed_picture_almost_alone()
    {
        var blue = AutoEnhance.Suggest(AutoEnhance.Measure(new[] { Image(90, 120, 170, i => i % 120 - 60) }));
        Assert.True(blue.Temperature > 10);

        var fine = AutoEnhance.Suggest(AutoEnhance.Measure(new[] { Image(118, 118, 118, i => (i * 7) % 240 - 110) }));
        Assert.InRange(fine.Temperature, -2, 2);
        Assert.InRange(fine.Tint, -2, 2);
        Assert.InRange(fine.Exposure, -15, 15);
    }
}
