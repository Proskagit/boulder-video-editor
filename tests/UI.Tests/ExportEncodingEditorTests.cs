using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Composition;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Export;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.UI.ViewModels;
using Xunit;

namespace AiVideoEditor.UI.Tests;

/// <summary>D059 §4, D076: the export settings editor of the export dialog and the project settings — the presets on top,
/// every field in «Подробные настройки», «Пользовательский» after a change, the user's saved presets, the codecs, the
/// containers, the encoders per codec, the notes and the estimate.</summary>
public sealed class ExportEncodingEditorTests
{
    private static readonly ExportEncoderAvailability Nvenc = new(true) { NvencHevc = true, NvencAv1 = true };
    private static readonly ExportEncoderAvailability None = new(false, "no NVIDIA graphics card");

    private static ExportOutput Uhd(double seconds = 60)
    {
        var rate = new FrameRate(60, 1);
        var frames = (long)(seconds * 60);
        return new ExportOutput(new FrameSize(3840, 2160), rate, frames, MediaTime.FromFrame(frames, rate), (long)(seconds * 48_000));
    }

    private static ExportPresetChoice Preset(ExportEncodingEditorViewModel e, ExportPresetKind kind) =>
        e.Presets.Single(p => p.Kind == ExportPresetChoiceKind.BuiltIn && p.BuiltIn == kind);

    [Fact]
    public void The_four_presets_custom_and_save_are_offered_and_a_new_project_opens_on_maximum()
    {
        var editor = new ExportEncodingEditorViewModel(ExportEncoding.NewProject, Nvenc, Uhd(), userSettings: new InMemoryUserSettings());

        Assert.Equal(new[] { "Fast", "Medium", "High", "Maximum", "Custom", "＋ Save preset…" }, editor.Presets.Select(p => p.Label));
        Assert.Equal(ExportPresetKind.Maximum, editor.SelectedPreset!.BuiltIn);
        Assert.Equal((ExportVideoCodec.Hevc, ExportVideoEncoder.Nvenc, ExportResolution.Project, ExportQuality.Maximum, ExportSpeedPreset.Slow, 320),
            (editor.SelectedCodec.Value, editor.SelectedEncoder.Value, editor.SelectedResolution.Value, editor.SelectedQuality.Value, editor.SelectedSpeed.Value, editor.SelectedBitrate.Value));
        Assert.Equal(ExportEncoding.NewProject, editor.Value);
        Assert.StartsWith("Parameters: NVENC HEVC p7 CQ", editor.PlanText);
        // Without the global settings nothing can be saved.
        Assert.DoesNotContain(new ExportEncodingEditorViewModel(ExportEncoding.NewProject, Nvenc, Uhd()).Presets, p => p.Kind == ExportPresetChoiceKind.SaveNew);
    }

    [Fact]
    public void Choosing_a_preset_shows_all_its_fields_and_keeps_the_file_type()
    {
        var editor = new ExportEncodingEditorViewModel(ExportEncoding.NewProject with { Container = ExportContainer.Mkv }, Nvenc, Uhd());
        editor.SelectedPreset = Preset(editor, ExportPresetKind.Fast);

        Assert.Equal((ExportVideoCodec.H264, ExportResolution.FullHd, ExportQuality.High, ExportSpeedPreset.VeryFast, 192, ExportContainer.Mkv),
            (editor.SelectedCodec.Value, editor.SelectedResolution.Value, editor.SelectedQuality.Value, editor.SelectedSpeed.Value, editor.SelectedBitrate.Value, editor.SelectedContainer.Value));
        Assert.Equal(new FrameSize(1920, 1080), editor.OutputSize);
        Assert.Equal("Parameters: NVENC p2 CQ 25", editor.PlanText);

        editor.SelectedPreset = Preset(editor, ExportPresetKind.Medium);
        Assert.Equal((ExportVideoCodec.Hevc, ExportResolution.Qhd), (editor.SelectedCodec.Value, editor.SelectedResolution.Value));
        Assert.Equal(new FrameSize(2560, 1440), editor.OutputSize);
    }

    [Fact]
    public void Changing_any_field_makes_it_custom_and_changing_it_back_finds_the_preset_again()
    {
        var editor = new ExportEncodingEditorViewModel(ExportEncoding.NewProject, Nvenc, Uhd());
        editor.SelectedBitrate = editor.Bitrates.Single(b => b.Value == 256);
        Assert.Equal(ExportPresetChoiceKind.Custom, editor.SelectedPreset!.Kind);
        editor.SelectedBitrate = editor.Bitrates.Single(b => b.Value == 320);
        Assert.Equal(ExportPresetKind.Maximum, editor.SelectedPreset!.BuiltIn);

        // The file type is not part of a built-in preset.
        editor.SelectedContainer = editor.Containers.Single(c => c.Value == ExportContainer.Mov);
        Assert.Equal(ExportPresetKind.Maximum, editor.SelectedPreset!.BuiltIn);

        editor.SelectedQuality = editor.Qualities.Single(q => q.Value == ExportQuality.Standard);
        Assert.Equal(ExportPresetChoiceKind.Custom, editor.SelectedPreset!.Kind);
        Assert.Equal(ExportQuality.Standard, editor.Value.Quality);
        editor.SelectedPreset = editor.Presets.Single(p => p.Kind == ExportPresetChoiceKind.Custom);   // «Пользовательский» changes nothing
        Assert.Equal(ExportQuality.Standard, editor.Value.Quality);
    }

    [Fact]
    public void An_old_projects_settings_open_as_custom_with_exactly_their_values()   // D076 Q7 A
    {
        var editor = new ExportEncodingEditorViewModel(ExportEncoding.Default, Nvenc, Uhd());
        Assert.Equal(ExportPresetChoiceKind.Custom, editor.SelectedPreset!.Kind);
        Assert.Equal(ExportEncoding.Default, editor.Value);
        Assert.Equal("Parameters: libx264 medium CRF 18", editor.PlanText);
    }

    [Fact]
    public void Av1_offers_no_mov_and_moves_a_mov_choice_to_mp4()
    {
        var editor = new ExportEncodingEditorViewModel(ExportEncoding.Default with { Container = ExportContainer.Mov }, Nvenc, Uhd());
        Assert.Equal(new[] { ExportContainer.Mp4, ExportContainer.Mov, ExportContainer.Mkv }, editor.Containers.Select(c => c.Value));

        editor.SelectedCodec = editor.Codecs.Single(c => c.Value == ExportVideoCodec.Av1);

        Assert.Equal(new[] { ExportContainer.Mp4, ExportContainer.Mkv }, editor.Containers.Select(c => c.Value));
        Assert.Equal(ExportContainer.Mp4, editor.Value.Container);
        Assert.Null(editor.Value.Validate());
        Assert.Contains("AV1", editor.CodecNote);
    }

    [Fact]
    public void The_encoders_follow_the_codec_nvenc_only_where_confirmed()
    {
        var h264Only = new ExportEncoderAvailability(true);
        var editor = new ExportEncodingEditorViewModel(ExportEncoding.Default, h264Only, Uhd());
        Assert.Equal(new[] { ExportVideoEncoder.Nvenc, ExportVideoEncoder.Software }, editor.Encoders.Select(e => e.Value));
        Assert.Equal("Processor (libx264)", editor.Encoders[1].Label);

        editor.SelectedCodec = editor.Codecs.Single(c => c.Value == ExportVideoCodec.Hevc);
        Assert.Equal(new[] { ExportVideoEncoder.Software }, editor.Encoders.Select(e => e.Value));
        Assert.Equal("Processor (libx265)", editor.SelectedEncoder.Label);
        Assert.Equal("Parameters: libx265 medium CRF " + ExportVideoPlan.X265CrfOf(ExportQuality.High), editor.PlanText);
    }

    [Fact]
    public void A_saved_nvenc_choice_on_a_computer_without_it_is_kept_and_explained()
    {
        var editor = new ExportEncodingEditorViewModel(ExportEncoding.NewProject, None, Uhd());

        Assert.Equal(ExportEncoding.NewProject, editor.Value);
        Assert.Equal(ExportPresetKind.Maximum, editor.SelectedPreset!.BuiltIn);
        Assert.Contains("no NVIDIA graphics card", editor.UnavailableNote);
        Assert.Contains("libx265", editor.UnavailableNote);
        Assert.Contains("libx265", editor.PlanText);

        editor.SetAvailability(Nvenc);   // the check finished later: NVENC is offered and used, the choice kept
        Assert.Null(editor.UnavailableNote);
        Assert.Equal(ExportEncoding.NewProject, editor.Value);
        Assert.StartsWith("Parameters: NVENC HEVC", editor.PlanText);
    }

    [Fact]
    public void A_codec_this_ffmpeg_cannot_encode_is_said_before_the_export()
    {
        var editor = new ExportEncodingEditorViewModel(ExportEncoding.Default with { Codec = ExportVideoCodec.Av1 },
            ExportEncoderAvailability.SoftwareOnly with { SoftwareAv1 = false }, Uhd());
        Assert.Contains("SVT-AV1", editor.UnavailableNote);
    }

    [Fact]
    public void The_estimate_is_marked_as_one_and_follows_the_encoder_the_codec_and_the_bitrate()
    {
        var editor = new ExportEncodingEditorViewModel(ExportEncoding.Default, Nvenc, Uhd());
        var x264 = editor.EstimateText;
        Assert.NotNull(x264);
        Assert.Contains("estimate", x264);

        editor.SelectedEncoder = editor.Encoders.Single(e => e.Value == ExportVideoEncoder.Nvenc);
        var nvenc = editor.EstimateText;
        Assert.NotEqual(x264, nvenc);
        editor.SelectedCodec = editor.Codecs.Single(c => c.Value == ExportVideoCodec.Hevc);
        Assert.NotEqual(nvenc, editor.EstimateText);

        editor.SelectedRateControl = editor.RateControls.Single(r => r.Value == ExportRateControl.Bitrate);
        editor.SelectedVideoBitrate = editor.VideoBitrates.Single(b => b.Value == 40);
        Assert.True(editor.IsBitrate);
        // 40 Mbit/s + 192 kbit/s for a minute ≈ 301 MB (binary megabytes: ≈ 289 MB).
        Assert.Contains("289 MB", editor.EstimateText);
        Assert.Contains("±7 %", editor.EstimateText);

        Assert.Null(new ExportEncodingEditorViewModel(ExportEncoding.Default, Nvenc).EstimateText);      // no output: no estimate
    }

    [Fact]
    public void A_saved_preset_is_offered_in_every_project_chosen_matched_replaced_by_name_and_deleted()
    {
        var settings = new InMemoryUserSettings();
        var editor = new ExportEncodingEditorViewModel(ExportEncoding.NewProject, Nvenc, Uhd(), userSettings: settings);
        editor.SelectedCodec = editor.Codecs.Single(c => c.Value == ExportVideoCodec.Av1);
        editor.SelectedContainer = editor.Containers.Single(c => c.Value == ExportContainer.Mkv);
        var mine = editor.Value;

        editor.SelectedPreset = editor.Presets.Single(p => p.Kind == ExportPresetChoiceKind.SaveNew);
        Assert.True(editor.IsSavingPreset);
        Assert.Equal("AV1 · As in the project · MKV · AAC 320", editor.NewPresetName);   // Q5 A: a suggestion to edit
        Assert.Equal(ExportPresetChoiceKind.Custom, editor.SelectedPreset!.Kind);          // «＋ Сохранить…» is not a choice
        editor.NewPresetName = "Archive";
        editor.SavePresetCommand.Execute(null);

        Assert.False(editor.IsSavingPreset);
        Assert.Equal(new SavedExportPreset("Archive", mine), Assert.Single(settings.Current.ExportPresets));
        Assert.Equal("Archive", editor.SelectedPreset!.Label);
        Assert.True(editor.IsSavedPreset);

        // Another project: offered, matched with its file type, applied with every field.
        var other = new ExportEncodingEditorViewModel(ExportEncoding.Default, Nvenc, Uhd(), userSettings: settings);
        Assert.Contains(other.Presets, p => p.Label == "Archive");
        other.SelectedPreset = other.Presets.Single(p => p.Label == "Archive");
        Assert.Equal(mine, other.Value);
        other.SelectedContainer = other.Containers.Single(c => c.Value == ExportContainer.Mp4);
        Assert.Equal(ExportPresetChoiceKind.Custom, other.SelectedPreset!.Kind);            // a saved preset's file type is its own

        // The same name replaces it.
        other.SelectedPreset = other.Presets.Single(p => p.Kind == ExportPresetChoiceKind.SaveNew);
        other.NewPresetName = "archive";
        other.SavePresetCommand.Execute(null);
        Assert.Equal(ExportContainer.Mp4, Assert.Single(settings.Current.ExportPresets).Settings.Container);

        // ✕ deletes it; the settings stay.
        var value = other.Value;
        other.DeletePresetCommand.Execute(null);
        Assert.Empty(settings.Current.ExportPresets);
        Assert.Equal(value, other.Value);
        Assert.DoesNotContain(other.Presets, p => p.Kind == ExportPresetChoiceKind.Saved);
        editor.Detach();
        other.Detach();
    }

    [Fact]
    public void A_preset_never_enlarges_a_smaller_canvas()   // Q4 A
    {
        var rate = new FrameRate(30, 1);
        var hd = new ExportOutput(new FrameSize(1920, 1080), rate, 300, MediaTime.FromFrame(300, rate), 480_000);
        var editor = new ExportEncodingEditorViewModel(ExportEncoding.Default, Nvenc, hd);
        editor.SelectedPreset = Preset(editor, ExportPresetKind.High);
        Assert.Equal(ExportResolution.Project, editor.SelectedResolution.Value);
        Assert.Equal(new FrameSize(1920, 1080), editor.OutputSize);
        Assert.Equal(ExportPresetKind.High, editor.SelectedPreset!.BuiltIn);
    }

    [Fact]
    public void Sizes_read_in_megabytes_and_gigabytes()
    {
        Assert.Equal("5.0 MB", ExportEncodingEditorViewModel.Size(5L << 20).Replace(',', '.'));
        Assert.Equal("350 MB", ExportEncodingEditorViewModel.Size(350L << 20));
        Assert.Equal("2.5 GB", ExportEncodingEditorViewModel.Size((5L << 30) / 2).Replace(',', '.'));
    }
}
