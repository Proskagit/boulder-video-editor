using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using Xunit;

namespace AiVideoEditor.Core.Tests;

/// <summary>D028, refined in Step 13.3: the canvas sizes and frame rates a user may choose, and the export settings.</summary>
public class ProjectSettingsRulesTests
{
    [Theory]
    [InlineData(1920, 1080)]
    [InlineData(1080, 1920)]
    [InlineData(1080, 1080)]
    [InlineData(1080, 1350)]
    [InlineData(3840, 2160)]
    [InlineData(2160, 3840)]
    [InlineData(4096, 2160)]
    [InlineData(2560, 1440)]
    [InlineData(1280, 720)]
    [InlineData(720, 1280)]
    [InlineData(640, 360)]
    [InlineData(64, 64)]
    [InlineData(64, 4096)]
    [InlineData(3072, 3072)]
    public void Valid_canvases_are_accepted(int width, int height) =>
        Assert.Null(ProjectSettingsRules.CanvasError(width, height));

    [Theory]
    [InlineData(1921, 1080)]
    [InlineData(1920, 1079)]
    [InlineData(65, 64)]
    public void Odd_sides_are_refused(int width, int height) =>
        Assert.Contains("even", ProjectSettingsRules.CanvasError(width, height));

    [Theory]
    [InlineData(62, 1080)]
    [InlineData(1920, 62)]
    [InlineData(0, 1080)]
    [InlineData(-2, 1080)]
    [InlineData(4098, 1080)]
    [InlineData(1080, 4098)]
    public void Sides_outside_the_limits_are_refused(int width, int height) =>
        Assert.Contains("between 64 and 4096", ProjectSettingsRules.CanvasError(width, height));

    [Theory]
    [InlineData(4096, 4096)]
    [InlineData(3074, 3072)]   // 9 443 328 px, just above the level 5.1 limit
    [InlineData(4096, 2306)]
    public void Canvases_above_the_area_limit_are_refused(int width, int height) =>
        Assert.Contains("too large", ProjectSettingsRules.CanvasError(width, height));

    [Fact]
    public void The_area_limit_is_the_H264_level_5_1_frame_size()
    {
        Assert.Equal(9_437_184L, ProjectSettingsRules.MaxCanvasArea);
        Assert.Null(ProjectSettingsRules.CanvasError(4096, 2304));   // exactly 36 864 macroblocks
    }

    [Fact]
    public void The_default_canvas_is_valid() =>
        Assert.Null(ProjectSettingsRules.CanvasError(new ProjectSettings().FrameWidth, new ProjectSettings().FrameHeight));

    [Fact]
    public void Exactly_the_eight_rates_are_selectable()
    {
        var expected = new[]
        {
            new FrameRate(24000, 1001), new FrameRate(24, 1), new FrameRate(25, 1), new FrameRate(30000, 1001),
            new FrameRate(30, 1), new FrameRate(50, 1), new FrameRate(60000, 1001), new FrameRate(60, 1)
        };
        Assert.Equal(expected, ProjectSettingsRules.SelectableFrameRates);
        Assert.All(expected, rate => Assert.True(ProjectSettingsRules.IsSelectableFrameRate(rate)));
        Assert.True(ProjectSettingsRules.IsSelectableFrameRate(FrameRate.Default));
    }

    [Theory]
    [InlineData(15, 1)]
    [InlineData(120, 1)]
    [InlineData(25, 2)]
    [InlineData(48, 1)]
    [InlineData(2997, 100)]   // a decimal 29.97 is not NTSC 30000/1001
    public void Other_rates_are_not_selectable(int numerator, int denominator) =>
        Assert.False(ProjectSettingsRules.IsSelectableFrameRate(new FrameRate(numerator, denominator)));

    // --- export settings ------------------------------------------------------------------------------------------------

    [Fact]
    public void The_default_export_settings_are_the_D023_format()
    {
        var d = ExportEncoding.Default;
        Assert.Equal(ExportQuality.High, d.Quality);
        Assert.Equal(ExportSpeedPreset.Medium, d.Preset);
        Assert.Equal(192, d.AudioBitrateKbps);
        Assert.Equal(18, d.Crf);                                       // the Phase 8–12 output (D023), now the default
        Assert.Equal("medium", d.PresetName);
        Assert.Equal(192_000, d.AudioBitrateKbps * 1000);
        Assert.Same(d, new ProjectSettings().Export);
        Assert.Null(d.Validate());
    }

    [Theory]
    [InlineData(ExportQuality.Maximum, 14)]
    [InlineData(ExportQuality.High, 18)]
    [InlineData(ExportQuality.Standard, 23)]
    [InlineData(ExportQuality.Compact, 28)]
    public void Each_quality_level_is_one_CRF(ExportQuality quality, int crf) =>
        Assert.Equal(crf, (ExportEncoding.Default with { Quality = quality }).Crf);

    [Theory]
    [InlineData(ExportSpeedPreset.Fast, "fast")]
    [InlineData(ExportSpeedPreset.Medium, "medium")]
    [InlineData(ExportSpeedPreset.Slow, "slow")]
    public void Each_speed_preset_is_one_libx264_preset(ExportSpeedPreset preset, string name) =>
        Assert.Equal(name, (ExportEncoding.Default with { Preset = preset }).PresetName);

    [Fact]
    public void The_offered_values_are_exactly_those_decided()
    {
        Assert.Equal(new[] { ExportQuality.Maximum, ExportQuality.High, ExportQuality.Standard, ExportQuality.Compact }, Enum.GetValues<ExportQuality>());
        Assert.Equal(new[] { ExportSpeedPreset.Fast, ExportSpeedPreset.Medium, ExportSpeedPreset.Slow }, Enum.GetValues<ExportSpeedPreset>());
        Assert.Equal(new[] { 128, 160, 192, 256, 320 }, ExportEncoding.AudioBitratesKbps);
    }

    [Fact]
    public void Every_offered_combination_is_valid()
    {
        foreach (var quality in Enum.GetValues<ExportQuality>())
        foreach (var preset in Enum.GetValues<ExportSpeedPreset>())
        foreach (var bitrate in ExportEncoding.AudioBitratesKbps)
            Assert.Null(new ExportEncoding(quality, preset, bitrate).Validate());
    }

    [Fact]
    public void Values_that_are_not_offered_are_invalid()
    {
        Assert.NotNull((ExportEncoding.Default with { Quality = (ExportQuality)7 }).Validate());
        Assert.NotNull((ExportEncoding.Default with { Preset = (ExportSpeedPreset)7 }).Validate());
        Assert.NotNull((ExportEncoding.Default with { AudioBitrateKbps = 191 }).Validate());
        Assert.NotNull((ExportEncoding.Default with { AudioBitrateKbps = 0 }).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => ExportEncoding.CrfOf((ExportQuality)7));
        Assert.Throws<ArgumentOutOfRangeException>(() => ExportEncoding.PresetNameOf((ExportSpeedPreset)7));
    }

    [Fact]
    public void Export_settings_compare_by_value()
    {
        Assert.Equal(ExportEncoding.Default, new ExportEncoding(ExportQuality.High, ExportSpeedPreset.Medium, 192));
        Assert.NotEqual(ExportEncoding.Default, ExportEncoding.Default with { AudioBitrateKbps = 256 });
    }
}
