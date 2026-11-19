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
/// to the user's settings; the heart rate's display offset −70 s by default, whole seconds, reset to the default; the FIT's
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
    public void Each_watch_maker_has_its_own_offset_coros_minus_seventy_garmin_and_others_minus_twenty()
    {
        // D062 amendment 1 (owner, 2026-11-17).
        var settings = new InMemoryUserSettings();
        var vm = new AppSettingsViewModel(settings, null);
        Assert.Equal((-70m, -20m, -20m), (vm.HeartRateOffsetSeconds, vm.GarminHeartRateOffsetSeconds, vm.OtherHeartRateOffsetSeconds));
        Assert.Equal(MediaTime.FromSeconds(-70), settings.Current.HeartRateDisplayOffsetFor(Core.Telemetry.FitVendor.Coros));
        Assert.Equal(MediaTime.FromSeconds(-20), settings.Current.HeartRateDisplayOffsetFor(Core.Telemetry.FitVendor.Garmin));
        Assert.Equal(MediaTime.FromSeconds(-20), settings.Current.HeartRateDisplayOffsetFor(Core.Telemetry.FitVendor.Other));
        Assert.Equal(MediaTime.FromSeconds(-20), settings.Current.HeartRateDisplayOffsetFor(255));
        Assert.Equal(MediaTime.Zero, settings.Current.HeartRateDisplayOffsetFor((int?)null));   // no training

        vm.GarminHeartRateOffsetSeconds = -12.6m;
        vm.OtherHeartRateOffsetSeconds = 5m;
        Assert.Equal(MediaTime.FromSeconds(-13), settings.Current.HeartRateDisplayOffsetFor(1));
        Assert.Equal(MediaTime.FromSeconds(5), settings.Current.HeartRateDisplayOffsetFor(Core.Telemetry.FitVendor.Other));
        Assert.Equal(MediaTime.FromSeconds(-70), settings.Current.HeartRateDisplayOffsetFor(294));   // COROS untouched

        vm.ResetHeartRateOffsetCommand.Execute(null);
        Assert.Equal((-70m, -20m, -20m), (vm.HeartRateOffsetSeconds, vm.GarminHeartRateOffsetSeconds, vm.OtherHeartRateOffsetSeconds));
    }

    [Fact]
    public void The_fit_section_edits_the_projects_training_clock_correction_through_the_toolbars_value()
    {
        var training = new ClockCorrection { HasTraining = true, TrainingClockCorrectionFrames = 3 };
        var vm = new AppSettingsViewModel(new InMemoryUserSettings(), null, training: training);
        Assert.True(vm.HasTraining);
        Assert.Equal(3m, vm.TrainingClockCorrectionFrames);

        vm.TrainingClockCorrectionFrames = -7;
        Assert.Equal(-7m, training.TrainingClockCorrectionFrames);

        var notified = new List<string?>();
        vm.PropertyChanged += (_, e) => notified.Add(e.PropertyName);
        training.TrainingClockCorrectionFrames = 9;                                    // an Undo in the editor
        Assert.Contains(nameof(AppSettingsViewModel.TrainingClockCorrectionFrames), notified);
        Assert.Equal(9m, vm.TrainingClockCorrectionFrames);

        Assert.False(new AppSettingsViewModel(new InMemoryUserSettings(), null).HasTraining);   // no project training
    }

    private sealed class ClockCorrection : CommunityToolkit.Mvvm.ComponentModel.ObservableObject, ITrainingClockCorrection
    {
        private decimal? _frames;

        public bool HasTraining { get; init; }

        public decimal? TrainingClockCorrectionFrames
        {
            get => _frames;
            set => SetProperty(ref _frames, value);
        }
    }

    [Fact]
    public void The_offset_is_minus_sixty_by_default_and_every_change_is_written()
    {
        var settings = new InMemoryUserSettings();
        var vm = new AppSettingsViewModel(settings, null);
        Assert.Equal(-70m, vm.HeartRateOffsetSeconds);

        vm.HeartRateOffsetSeconds = -45.4m;
        Assert.Equal(MediaTime.FromSeconds(-45), settings.Current.HeartRateDisplayOffset);    // whole seconds
        vm.ResetHeartRateOffsetCommand.Execute(null);
        Assert.Equal(UserSettings.DefaultHeartRateDisplayOffset, settings.Current.HeartRateDisplayOffset);

        vm.RouteProposalsEnabled = true;
        Assert.True(vm.TimelineFollowsPlayhead);                                              // D058 §1: on by default
        vm.TimelineFollowsPlayhead = false;
        Assert.False(settings.Current.TimelineFollowsPlayhead);
        vm.LanguageIndex = 0;                                                                  // D068 item 16: «Русский» …
        vm.LanguageIndex = 1;                                                                  // … then «English»
        vm.FfmpegPath = @"D:\ffmpeg\ffmpeg.exe";
        Assert.True(settings.Current.RouteProposalsEnabled);
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
    public void The_window_has_its_four_sections_and_the_fit_section_holds_the_offsets_and_the_clock_correction()
    {
        var vm = new AppSettingsViewModel(new InMemoryUserSettings(), new FitSettingsViewModel(new Store(), new StatusService()));
        var view = new AppSettingsView { DataContext = vm };
        var names = view.GetLogicalDescendants().OfType<Control>().Select(c => c.Name).Where(n => n is not null).ToList();
        Assert.Contains("GeneralSection", names);
        Assert.Contains("FollowPlayheadBox", names);
        Assert.Contains("FfmpegSection", names);
        Assert.Contains("TimelineSection", names);
        Assert.Contains("CorosFitSection", names);
        Assert.Contains("HeartRateOffsetBox", names);
        Assert.Contains("BetaBox", names);
        // D062 amendment 1: the per-maker offsets and the project's training clock correction (shown only with a training).
        Assert.Contains("GarminHeartRateOffsetBox", names);
        Assert.Contains("OtherHeartRateOffsetBox", names);
        Assert.Contains("ClockCorrectionBox", names);
        Assert.False(vm.HasTraining);

        // The left column of the sections (D062 amendment 1: the only way to the FIT section now).
        var list = view.GetLogicalDescendants().OfType<ListBox>().Single(c => c.Name == "SectionList");
        Assert.Equal(5, vm.SectionNames.Count);              // D070 §3: + «ИИ-модели»
        Assert.Same(vm.SectionNames, list.ItemsSource);
        Assert.True(vm.ShowsGeneral);
        vm.SelectedSectionIndex = 3;
        Assert.True(vm.ShowsCorosFit);
        Assert.False(vm.ShowsGeneral);
        Assert.Contains("ModelsSection", names);
        vm.SelectedSectionIndex = 4;
        Assert.True(vm.ShowsModels);
        Assert.False(vm.ShowsCorosFit);
    }
}
