using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Composition;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Export;
using AiVideoEditor.UI.ViewModels;
using Xunit;

namespace AiVideoEditor.UI.Tests;

/// <summary>D059 §4: the export settings editor of the export dialog and the project settings.</summary>
public sealed class ExportEncodingEditorTests
{
    private static readonly ExportEncoderAvailability Nvenc = new(true);
    private static readonly ExportEncoderAvailability None = new(false, "no NVIDIA graphics card");

    private static ExportOutput Uhd(double seconds = 60)
    {
        var rate = new FrameRate(60, 1);
        var frames = (long)(seconds * 60);
        return new ExportOutput(new FrameSize(3840, 2160), rate, frames, MediaTime.FromFrame(frames, rate), (long)(seconds * 48_000));
    }

    [Fact]
    public void Four_profiles_balanced_by_default_and_libx264_always_nvenc_only_when_confirmed()
    {
        var withNvenc = new ExportEncodingEditorViewModel(ExportEncoding.Default, Nvenc, Uhd());
        var without = new ExportEncodingEditorViewModel(ExportEncoding.Default, None, Uhd());

        Assert.Equal(new[] { ExportProfile.Fast, ExportProfile.Balanced, ExportProfile.HighQuality, ExportProfile.Custom },
            withNvenc.Profiles.Select(p => p.Value));
        Assert.Equal(ExportProfile.Balanced, withNvenc.SelectedProfile.Value);
        Assert.Equal(ExportVideoEncoder.Libx264, withNvenc.SelectedEncoder.Value);                     // the default stays libx264
        Assert.Equal(new[] { ExportVideoEncoder.Nvenc, ExportVideoEncoder.Libx264 }, withNvenc.Encoders.Select(e => e.Value));
        Assert.Equal(new[] { ExportVideoEncoder.Libx264 }, without.Encoders.Select(e => e.Value));
        Assert.All(withNvenc.Encoders, e => Assert.False(string.IsNullOrWhiteSpace(e.Detail)));          // the short description
        Assert.Equal(ExportEncoding.Default, withNvenc.Value);
        Assert.False(withNvenc.IsCustom);
        Assert.Equal("Parameters: libx264 medium CRF 18", withNvenc.PlanText);
    }

    [Fact]
    public void Choosing_fast_takes_nvenc_when_available_and_the_user_can_switch_back()
    {
        var editor = new ExportEncodingEditorViewModel(ExportEncoding.Default, Nvenc, Uhd());
        editor.SelectedProfile = editor.Profiles.Single(p => p.Value == ExportProfile.Fast);
        Assert.Equal(ExportVideoEncoder.Nvenc, editor.SelectedEncoder.Value);
        Assert.StartsWith("Parameters: NVENC", editor.PlanText);

        editor.SelectedEncoder = editor.Encoders.Single(e => e.Value == ExportVideoEncoder.Libx264);
        Assert.Equal(ExportEncoding.Default with { Profile = ExportProfile.Fast }, editor.Value);
        Assert.Equal($"Parameters: libx264 {ExportVideoPlan.X264FastPreset} CRF {ExportVideoPlan.X264FastCrf}", editor.PlanText);

        var noGpu = new ExportEncodingEditorViewModel(ExportEncoding.Default, None, Uhd());
        noGpu.SelectedProfile = noGpu.Profiles.Single(p => p.Value == ExportProfile.Fast);
        Assert.Equal(ExportVideoEncoder.Libx264, noGpu.SelectedEncoder.Value);                           // a fast libx264
    }

    [Fact]
    public void The_estimate_is_marked_as_one_and_follows_the_encoder_the_profile_and_the_bitrate()
    {
        var editor = new ExportEncodingEditorViewModel(ExportEncoding.Default, Nvenc, Uhd());
        var x264 = editor.EstimateText;
        Assert.NotNull(x264);
        Assert.Contains("estimate", x264);

        editor.SelectedEncoder = editor.Encoders.Single(e => e.Value == ExportVideoEncoder.Nvenc);
        Assert.NotEqual(x264, editor.EstimateText);

        editor.SelectedProfile = editor.Profiles.Single(p => p.Value == ExportProfile.Custom);
        editor.SelectedRateControl = editor.RateControls.Single(r => r.Value == ExportRateControl.Bitrate);
        editor.SelectedVideoBitrate = editor.VideoBitrates.Single(b => b.Value == 40);
        Assert.True(editor.IsBitrate);
        // 40 Mbit/s + 192 kbit/s for a minute ≈ 301 MB (binary megabytes: ≈ 289 MB).
        Assert.Contains("289 MB", editor.EstimateText);
        Assert.Contains("±7 %", editor.EstimateText);

        Assert.Null(new ExportEncodingEditorViewModel(ExportEncoding.Default, Nvenc).EstimateText);      // no output: no estimate
    }

    [Fact]
    public void Changing_a_custom_field_selects_custom_and_the_audio_bitrate_does_not()
    {
        var editor = new ExportEncodingEditorViewModel(ExportEncoding.Default, None, Uhd());
        editor.SelectedBitrate = editor.Bitrates.Single(b => b.Value == 256);
        Assert.Equal(ExportProfile.Balanced, editor.SelectedProfile.Value);

        editor.SelectedQuality = editor.Qualities.Single(q => q.Value == ExportQuality.Compact);
        Assert.Equal(ExportProfile.Custom, editor.SelectedProfile.Value);
        Assert.True(editor.IsCustom);
        Assert.Equal(new ExportEncoding(ExportQuality.Compact, ExportSpeedPreset.Medium, 256), editor.Value);
    }

    [Fact]
    public void A_saved_nvenc_choice_on_a_computer_without_it_is_kept_and_explained()
    {
        var saved = ExportEncoding.Default with { Profile = ExportProfile.Fast, Encoder = ExportVideoEncoder.Nvenc };
        var editor = new ExportEncodingEditorViewModel(saved, None, Uhd());

        Assert.Equal(saved, editor.Value);
        Assert.NotNull(editor.UnavailableNote);
        Assert.Contains("no NVIDIA graphics card", editor.UnavailableNote);
        Assert.Contains("libx264", editor.PlanText);

        editor.SetAvailability(Nvenc);   // the check finished later: NVENC is offered and used, the choice kept
        Assert.Null(editor.UnavailableNote);
        Assert.Equal(saved, editor.Value);
        Assert.StartsWith("Parameters: NVENC", editor.PlanText);
    }

    [Fact]
    public void Sizes_read_in_megabytes_and_gigabytes()
    {
        Assert.Equal("5.0 MB", ExportEncodingEditorViewModel.Size(5L << 20).Replace(',', '.'));
        Assert.Equal("350 MB", ExportEncodingEditorViewModel.Size(350L << 20));
        Assert.Equal("2.5 GB", ExportEncodingEditorViewModel.Size((5L << 30) / 2).Replace(',', '.'));
    }

    [Fact]
    public void Custom_after_high_quality_starts_at_crf_14_and_slow()   // D074 amendment 1 (the owner)
    {
        var editor = new ExportEncodingEditorViewModel(ExportEncoding.NewProject, Nvenc, Uhd());
        Assert.Equal(ExportProfile.HighQuality, editor.SelectedProfile.Value);
        editor.SelectedProfile = ExportEncodingEditorViewModel.ProfileOptions.Single(o => o.Value == ExportProfile.Custom);
        Assert.Equal((ExportQuality.Maximum, ExportSpeedPreset.Slow), (editor.SelectedQuality.Value, editor.SelectedSpeed.Value));
        Assert.Equal(14, editor.Value.Crf);

        var balanced = new ExportEncodingEditorViewModel(ExportEncoding.Default, Nvenc, Uhd());
        balanced.SelectedProfile = ExportEncodingEditorViewModel.ProfileOptions.Single(o => o.Value == ExportProfile.Custom);
        Assert.Equal((ExportQuality.High, ExportSpeedPreset.Medium), (balanced.SelectedQuality.Value, balanced.SelectedSpeed.Value));
        Assert.Equal((ExportQuality.Maximum, ExportSpeedPreset.Slow), (ExportEncoding.NewProject.Quality, ExportEncoding.NewProject.Preset));
    }
}
