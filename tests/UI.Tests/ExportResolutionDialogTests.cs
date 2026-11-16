using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Composition;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Export;
using AiVideoEditor.UI.ViewModels;
using Xunit;

namespace AiVideoEditor.UI.Tests;

/// <summary>D060 §3: the resolution in the export dialog — the presets at the canvas's aspect ratio with their sizes, the
/// default that is exactly the earlier output, the output's line and the estimate following the choice, and the project
/// settings keeping the project's resolution untouched. D060 §5: the overall percentage of the compact indicator.</summary>
public class ExportResolutionDialogTests
{
    private static ExportOutput Output(int w, int h) =>
        new(new FrameSize(w, h), FrameRate.Fps25, 1500, MediaTime.FromSeconds(60), 48000 * 60);

    private static ExportDialogViewModel Dialog(int w, int h, ExportEncoding? encoding = null) =>
        new(new Sequence(), FrameRate.Fps25, Output(w, h), @"C:\out\video.mp4", encoding: encoding);

    [Fact]
    public void A_16_9_project_offers_the_four_presets_with_their_sizes_and_opens_on_its_own()
    {
        var dialog = Dialog(1920, 1080);

        Assert.Equal(new[] { ExportResolution.Uhd4K, ExportResolution.FullHd, ExportResolution.Hd, ExportResolution.Sd },
            dialog.Resolutions.Select(r => r.Value));
        Assert.Equal(new[] { "3840 × 2160", "1920 × 1080 · the project's size", "1280 × 720", "854 × 480" },
            dialog.Resolutions.Select(r => r.Detail));
        Assert.Equal(ExportResolution.FullHd, dialog.SelectedResolution.Value);
        Assert.StartsWith("1920 × 1080", dialog.Info);
    }

    [Fact]
    public void A_vertical_project_gets_standing_sizes()
    {
        var dialog = Dialog(1080, 1920);
        Assert.Equal("1080 × 1920 · the project's size", dialog.Resolutions.Single(r => r.Value == ExportResolution.FullHd).Detail);
        Assert.Equal("720 × 1280", dialog.Resolutions.Single(r => r.Value == ExportResolution.Hd).Detail);
        Assert.Equal(ExportResolution.FullHd, dialog.SelectedResolution.Value);
    }

    [Fact]
    public void A_canvas_of_no_preset_s_size_offers_As_in_the_project_first_and_opens_on_it()
    {
        var dialog = Dialog(2560, 1440);
        Assert.Equal(ExportResolution.Project, dialog.Resolutions[0].Value);
        Assert.Equal("2560 × 1440", dialog.Resolutions[0].Detail);
        Assert.Equal(ExportResolution.Project, dialog.SelectedResolution.Value);
        Assert.StartsWith("2560 × 1440", dialog.Info);
    }

    [Fact]
    public void Choosing_a_resolution_changes_the_output_s_line_the_estimate_and_the_result()
    {
        var dialog = Dialog(3840, 2160);
        var estimate4K = dialog.Settings.EstimateText;
        var changed = new List<string?>();
        dialog.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        dialog.SelectedResolution = dialog.Resolutions.Single(r => r.Value == ExportResolution.Hd);

        Assert.Contains(nameof(ExportDialogViewModel.Info), changed);
        Assert.StartsWith("1280 × 720", dialog.Info);
        Assert.Equal(new FrameSize(1280, 720), dialog.Settings.OutputSize);
        Assert.NotEqual(estimate4K, dialog.Settings.EstimateText);
        ExportDialogResult? result = null;
        dialog.Closed += (_, r) => result = r;
        dialog.ExportCommand.Execute(null);
        Assert.Equal(ExportResolution.Hd, result!.Encoding!.Resolution);
    }

    [Fact]
    public void The_canvas_s_own_preset_is_returned_as_the_canvas()
    {
        var dialog = Dialog(1920, 1080);
        ExportDialogResult? result = null;
        dialog.Closed += (_, r) => result = r;
        dialog.ExportCommand.Execute(null);
        Assert.Equal(ExportEncoding.Default, result!.Encoding);
    }

    [Fact]
    public void A_stored_resolution_is_shown()
    {
        var dialog = Dialog(1920, 1080, ExportEncoding.Default with { Resolution = ExportResolution.Sd });
        Assert.Equal(ExportResolution.Sd, dialog.SelectedResolution.Value);
        Assert.StartsWith("854 × 480", dialog.Info);
    }

    [Fact]
    public void The_project_settings_editor_keeps_the_project_s_resolution()
    {
        var editor = new ExportEncodingEditorViewModel(ExportEncoding.Default with { Resolution = ExportResolution.Hd });
        editor.SelectedProfile = editor.Profiles.Single(p => p.Value == ExportProfile.HighQuality);
        Assert.Equal(ExportResolution.Hd, editor.Value.Resolution);
        Assert.Equal(ExportProfile.HighQuality, editor.Value.Profile);
    }

    [Theory]
    [InlineData(ExportStage.Preparing, 50, 0)]
    [InlineData(ExportStage.Audio, 100, 3)]
    [InlineData(ExportStage.Video, 0, 3)]
    [InlineData(ExportStage.Video, 50, 51)]
    [InlineData(ExportStage.Video, 100, 99)]
    [InlineData(ExportStage.Finalizing, 100, 100)]
    public void The_overall_percentage_runs_once_over_the_stages(ExportStage stage, double stagePercent, double overall) =>
        Assert.Equal(overall, ExportProgressViewModel.Overall(stage, stagePercent), 3);
}
