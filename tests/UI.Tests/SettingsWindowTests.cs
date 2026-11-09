using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.UI.Services;
using AiVideoEditor.UI.ViewModels;
using AiVideoEditor.UI.Views;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Xunit;

namespace AiVideoEditor.UI.Tests;

/// <summary>
/// D057 §3.8: the application's settings window — General, FFmpeg, Timeline and markup, COROS / FIT; every change written
/// to the user's settings; the heart rate's display offset −60 s by default, whole seconds, reset to the default; the FIT's
/// calibration in its section and the COROS menu opening it; project settings never here.
/// </summary>
[Collection(AvaloniaControlsCollection.Name)]
public sealed class SettingsWindowTests
{
    private sealed class ScriptedAppDialog : IAppSettingsDialog
    {
        public AppSettingsViewModel? Shown { get; private set; }

        public Task ShowAsync(AppSettingsViewModel settings)
        {
            Shown = settings;
            return Task.CompletedTask;
        }
    }

    private sealed class NoFitDialog : IFitSettingsDialog
    {
        public bool Shown { get; private set; }

        public Task ShowAsync(FitSettingsViewModel settings)
        {
            Shown = true;
            return Task.CompletedTask;
        }
    }

    private sealed class Store : IFitCalibrationStore
    {
        public FitCalibrationSnapshot Current { get; set; } = FitCalibrationSnapshot.Initial;
        public Task<FitCalibrationSnapshot> LoadAsync(CancellationToken ct = default) => Task.FromResult(Current);
        public Task<bool> SynchronizeAsync(IReadOnlySet<Core.Telemetry.TrainingActivity> trainings,
            IReadOnlyCollection<Core.Telemetry.Calibration.CalibrationObservation> observations, CancellationToken ct = default) => Task.FromResult(true);
        public Task<bool> SetBetaAsync(bool enabled, CancellationToken ct = default) => Task.FromResult(true);
        public Task<bool> ResetAsync(CancellationToken ct = default) => Task.FromResult(true);
    }

    [Fact]
    public void The_offset_is_minus_sixty_by_default_and_every_change_is_written()
    {
        var settings = new InMemoryUserSettings();
        var vm = new AppSettingsViewModel(settings, null);
        Assert.Equal(-60m, vm.HeartRateOffsetSeconds);

        vm.HeartRateOffsetSeconds = -45.4m;
        Assert.Equal(MediaTime.FromSeconds(-45), settings.Current.HeartRateDisplayOffset);    // whole seconds
        vm.ResetHeartRateOffsetCommand.Execute(null);
        Assert.Equal(UserSettings.DefaultHeartRateDisplayOffset, settings.Current.HeartRateDisplayOffset);

        vm.HoldProposalsEnabled = true;
        vm.LanguageIndex = 2;
        vm.FfmpegPath = @"D:\ffmpeg\ffmpeg.exe";
        Assert.True(settings.Current.HoldProposalsEnabled);
        Assert.Equal("en", settings.Current.Language);
        Assert.Equal(@"D:\ffmpeg\ffmpeg.exe", settings.Current.FfmpegPath);
        vm.FfmpegPath = "";
        Assert.Null(settings.Current.FfmpegPath);                                             // empty: the application's own
    }

    [Fact]
    public async Task The_coros_menu_opens_the_window_at_its_section_and_the_toolbar_at_general()
    {
        var dialog = new ScriptedAppDialog();
        var workflow = new FitSettingsWorkflow(new Store(), new NoFitDialog(), new StatusService(), settings: new InMemoryUserSettings(), appDialog: dialog);
        Assert.True(workflow.HasAppSettings);

        await workflow.RunAsync();
        Assert.Equal(AppSettingsSection.CorosFit, dialog.Shown!.Section);
        Assert.True(dialog.Shown.HasFit);

        await workflow.RunAsync(AppSettingsSection.General);
        Assert.Equal(AppSettingsSection.General, dialog.Shown!.Section);
    }

    [Fact]
    public async Task Without_the_settings_window_the_fit_window_opens_as_before()
    {
        var fit = new NoFitDialog();
        var workflow = new FitSettingsWorkflow(new Store(), fit, new StatusService());
        Assert.False(workflow.HasAppSettings);
        await workflow.RunAsync();
        Assert.True(fit.Shown);
    }

    [Fact]
    public void The_window_has_its_four_sections_and_no_project_setting()
    {
        var vm = new AppSettingsViewModel(new InMemoryUserSettings(), new FitSettingsViewModel(new Store(), new StatusService()));
        var view = new AppSettingsView { DataContext = vm };
        var names = view.GetLogicalDescendants().OfType<Control>().Select(c => c.Name).Where(n => n is not null).ToList();
        Assert.Contains("GeneralSection", names);
        Assert.Contains("FfmpegSection", names);
        Assert.Contains("TimelineSection", names);
        Assert.Contains("CorosFitSection", names);
        Assert.Contains("HeartRateOffsetBox", names);
        Assert.Contains("BetaBox", names);
        Assert.DoesNotContain(names, n => n!.Contains("ClockCorrection"));
    }

    [Fact]
    public void The_youtube_window_offers_the_range_and_copies_the_text()
    {
        var sequence = new Core.Entities.Sequence();
        var track = new Core.Entities.Track { Type = Core.Entities.TrackType.Video, Name = "V1" };
        sequence.VideoTracks.Add(track);
        track.Clips.Add(new Core.Entities.TemplateClip
        {
            Kind = Core.Entities.TemplateKind.Boulder, TimelineStart = MediaTime.FromSeconds(70), Duration = MediaTime.FromSeconds(10),
            Route = "3", Grade = "6B", Status = Core.Entities.BoulderStatus.Top
        });
        var vm = new YouTubeTimestampsViewModel(sequence, FrameRate.Fps30, (MediaTime.FromSeconds(60), MediaTime.FromSeconds(120)));
        Assert.True(vm.HasRange && vm.UseRange);
        Assert.Equal("00:00 Intro\n00:10 Трасса 3 · 6B 🟢", vm.Text);
        vm.UseRange = false;
        Assert.Equal("00:00 Intro\n01:10 Трасса 3 · 6B 🟢", vm.Text);
        string? copied = null;
        vm.CopyRequested += (_, text) => copied = text;
        vm.CopyCommand.Execute(null);
        Assert.Equal(vm.Text, copied);
    }
}
