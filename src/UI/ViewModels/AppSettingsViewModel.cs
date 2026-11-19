using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.UI.Localization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AiVideoEditor.UI.ViewModels;

/// <summary>The sections of the settings window (D057 §3.8).</summary>
public enum AppSettingsSection
{
    General,
    Ffmpeg,
    TimelineAndMarkup,
    CorosFit,

    /// <summary>D070 §3: the AI models — their state, the Portable package's installation.</summary>
    Models
}

/// <summary>D062 amendment 1: the project's training clock correction as the settings window edits it — the toolbar's
/// value (one Undo step per change, D049 §2), so it stays a project setting while it is shown with the FIT settings.</summary>
public interface ITrainingClockCorrection : System.ComponentModel.INotifyPropertyChanged
{
    bool HasTraining { get; }

    decimal? TrainingClockCorrectionFrames { get; set; }
}

/// <summary>
/// D057 §3.8: the application's settings window — the global settings of the user, in sections: General (the interface
/// language), FFmpeg (the executables), Timeline and markup (the automatic hold proposals), COROS / FIT (the heart rate's
/// display offset and the start calibration of D051 §6, <see cref="Fit"/>). Every change is written to the per-user file
/// at once; the language and the FFmpeg paths apply at the next start. D062 amendment 1 (owner, 2026-11-17): the FIT section
/// holds the heart rate's display offset per watch maker (COROS, Garmin, others) and — while a project has a training — the
/// training's clock correction <c>CC</c> (D049), which stays the project's value and one Undo step.
/// </summary>
public sealed partial class AppSettingsViewModel : ViewModelBase
{
    private readonly IUserSettingsService _settings;

    public AppSettingsViewModel(IUserSettingsService settings, FitSettingsViewModel? fit, AppSettingsSection section = AppSettingsSection.General,
        string? ffmpegFound = null, string? ffprobeFound = null, ITrainingClockCorrection? training = null,
        AiVideoEditor.UI.Services.ModelSetupService? models = null)
    {
        Models = models;
        if (models is not null)
        {
            models.PropertyChanged += OnModelsPropertyChanged;
            _ = RefreshModelsAsync(models);
        }
        _settings = settings;
        _training = training;
        if (training is not null) training.PropertyChanged += OnTrainingPropertyChanged;
        Fit = fit;
        Section = section;
        _selectedSectionIndex = (int)section;
        var current = settings.Current;
        _heartRateOffsetSeconds = Seconds(current.HeartRateDisplayOffsetTicks);
        _garminHeartRateOffsetSeconds = Seconds(current.GarminHeartRateDisplayOffsetTicks);
        _otherHeartRateOffsetSeconds = Seconds(current.OtherHeartRateDisplayOffsetTicks);
        _holdProposalsEnabled = current.HoldProposalsEnabled;
        _timelineFollowsPlayhead = current.TimelineFollowsPlayhead;
        _canvasSnapPixels = (decimal)current.CanvasSnapPixels;
        _showPeopleMasks = current.ShowPeopleMasks;
        // D068 item 16: the two languages by name; no choice set yet shows the language the interface has now.
        _languageIndex = (current.Language ?? System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName) == "ru" ? 0 : 1;
        _ffmpegPath = current.FfmpegPath ?? "";
        _ffprobePath = current.FfprobePath ?? "";
        FfmpegFound = ffmpegFound ?? Strings.AppSettings_NotFound;
        FfprobeFound = ffprobeFound ?? Strings.AppSettings_NotFound;
        if (fit is not null) fit.CloseRequested += (_, _) => CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The section the window opens at (the COROS menu's «Настройки FIT…» opens <see cref="AppSettingsSection.CorosFit"/>).</summary>
    public AppSettingsSection Section { get; }

    /// <summary>The start calibration of D051 §6 (Beta, the estimate, the count, reset) — unchanged semantics, here.</summary>
    public FitSettingsViewModel? Fit { get; }

    public bool HasFit => Fit is not null;

    /// <summary>The left column (owner's acceptance of 2026-11-10): one item per section, in the order of
    /// <see cref="AppSettingsSection"/>; the chosen one is shown on the right. New sections are added here.</summary>
    public IReadOnlyList<string> SectionNames { get; } = new[]
    {
        Strings.AppSettings_NavGeneral, Strings.AppSettings_NavFfmpeg, Strings.AppSettings_NavTimelineAndMarkup, Strings.AppSettings_NavCorosFit,
        Strings.AppSettings_NavModels
    };

    /// <summary>D070 §3: the models' state and installation; null without the service (tests).</summary>
    public AiVideoEditor.UI.Services.ModelSetupService? Models { get; }

    /// <summary>The models folder.</summary>
    public string ModelsFolderText => Models is null ? "" : string.Format(Strings.AppSettings_ModelsFolder, Models.ModelsRoot);

    /// <summary>Checking / every model in place / how many files are missing.</summary>
    public string ModelsStateText => Models?.State switch
    {
        null => Strings.AppSettings_ModelsChecking,
        { IsComplete: true } => string.Format(Strings.AppSettings_ModelsComplete, Models.TotalGigabytes),
        var state => string.Format(Strings.AppSettings_ModelsMissing, state.MissingFiles.Count)
    };

    /// <summary>The running installation's percent, for the section.</summary>
    public string ModelsInstallingText => Models is null ? "" :
        string.Format(Strings.AppSettings_ModelsInstalling, Models.Percent.ToString("0", System.Globalization.CultureInfo.CurrentCulture));

    public bool ModelsInstalling => Models?.IsInstalling == true;

    /// <summary>The Portable package is next to the application: the install button and its note.</summary>
    public bool ModelsHavePackage => Models?.HasPackage == true;

    public bool ModelsHaveNoPackage => Models is { HasPackage: false };

    private static async Task RefreshModelsAsync(AiVideoEditor.UI.Services.ModelSetupService models)
    {
        try
        {
            await models.RefreshAsync();
        }
        catch (Exception)
        {
            // the state stays «Проверка…»; the features report a missing model themselves
        }
    }

    private void OnModelsPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        OnPropertyChanged(nameof(ModelsStateText));
        OnPropertyChanged(nameof(ModelsInstallingText));
        OnPropertyChanged(nameof(ModelsInstalling));
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsGeneral), nameof(ShowsFfmpeg), nameof(ShowsTimelineAndMarkup), nameof(ShowsCorosFit), nameof(ShowsModels))]
    private int _selectedSectionIndex;

    public AppSettingsSection SelectedSection => (AppSettingsSection)Math.Clamp(SelectedSectionIndex, 0, SectionNames.Count - 1);

    public bool ShowsGeneral => SelectedSection == AppSettingsSection.General;
    public bool ShowsFfmpeg => SelectedSection == AppSettingsSection.Ffmpeg;
    public bool ShowsTimelineAndMarkup => SelectedSection == AppSettingsSection.TimelineAndMarkup;
    public bool ShowsCorosFit => SelectedSection == AppSettingsSection.CorosFit;
    public bool ShowsModels => SelectedSection == AppSettingsSection.Models;

    public event EventHandler? CloseRequested;

    public IReadOnlyList<string> Languages { get; } = new[] { "Русский", "English" };

    [ObservableProperty] private int _languageIndex;

    partial void OnLanguageIndexChanged(int value) =>
        _settings.Update(s => s with { Language = value == 1 ? "en" : "ru" });

    private readonly ITrainingClockCorrection? _training;

    private void OnTrainingPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ITrainingClockCorrection.TrainingClockCorrectionFrames) or nameof(ITrainingClockCorrection.HasTraining))
        {
            OnPropertyChanged(nameof(TrainingClockCorrectionFrames));
            OnPropertyChanged(nameof(HasTraining));
        }
    }

    /// <summary>The window closed: stops following the long-lived toolbar's training value.</summary>
    public void Detach()
    {
        if (_training is not null) _training.PropertyChanged -= OnTrainingPropertyChanged;
        if (Models is not null) Models.PropertyChanged -= OnModelsPropertyChanged;
    }

    private static decimal Seconds(long ticks) => (decimal)ticks / TimeSpan.TicksPerSecond;

    private static long WholeSecondTicks(decimal seconds) => (long)Math.Round(seconds, MidpointRounding.AwayFromZero) * TimeSpan.TicksPerSecond;

    /// <summary>D057 §3.8: the heart rate's display offset of a COROS training in seconds (negative: earlier); whole seconds.</summary>
    [ObservableProperty] private decimal? _heartRateOffsetSeconds;

    /// <summary>D062 amendment 1: the same for a Garmin training.</summary>
    [ObservableProperty] private decimal? _garminHeartRateOffsetSeconds;

    /// <summary>D062 amendment 1: the same for a training from any other watch.</summary>
    [ObservableProperty] private decimal? _otherHeartRateOffsetSeconds;

    partial void OnGarminHeartRateOffsetSecondsChanged(decimal? value)
    {
        if (value is { } seconds) _settings.Update(s => s with { GarminHeartRateDisplayOffsetTicks = WholeSecondTicks(seconds) });
    }

    partial void OnOtherHeartRateOffsetSecondsChanged(decimal? value)
    {
        if (value is { } seconds) _settings.Update(s => s with { OtherHeartRateDisplayOffsetTicks = WholeSecondTicks(seconds) });
    }

    /// <summary>D062 amendment 1: the project has a training — its clock correction is shown in the FIT section.</summary>
    public bool HasTraining => _training?.HasTraining == true;

    /// <summary>D062 amendment 1: the training's clock correction W − C in whole project frames (the toolbar's value, one Undo
    /// step per change).</summary>
    public decimal? TrainingClockCorrectionFrames
    {
        get => _training?.TrainingClockCorrectionFrames;
        set
        {
            if (_training is null) return;
            _training.TrainingClockCorrectionFrames = value;
            OnPropertyChanged();
        }
    }

    public decimal MinHeartRateOffset => -(decimal)UserSettings.MaxHeartRateDisplayOffset.Ticks / TimeSpan.TicksPerSecond;

    public decimal MaxHeartRateOffset => (decimal)UserSettings.MaxHeartRateDisplayOffset.Ticks / TimeSpan.TicksPerSecond;

    partial void OnHeartRateOffsetSecondsChanged(decimal? value)
    {
        if (value is not { } seconds) return;
        _settings.Update(s => s with { HeartRateDisplayOffsetTicks = WholeSecondTicks(seconds) });
    }

    /// <summary>Every maker's offset back to its default: COROS −70 s, Garmin and others −20 s (D062 amendment 1).</summary>
    [RelayCommand]
    private void ResetHeartRateOffset()
    {
        HeartRateOffsetSeconds = Seconds(UserSettings.DefaultHeartRateDisplayOffset.Ticks);
        GarminHeartRateOffsetSeconds = Seconds(UserSettings.DefaultOtherHeartRateDisplayOffset.Ticks);
        OtherHeartRateOffsetSeconds = Seconds(UserSettings.DefaultOtherHeartRateDisplayOffset.Ticks);
    }

    /// <summary>D057 §3.15: generate hold proposals on entering the markup mode (off by default).</summary>
    [ObservableProperty] private bool _holdProposalsEnabled;

    partial void OnHoldProposalsEnabledChanged(bool value) => _settings.Update(s => s with { HoldProposalsEnabled = value });

    /// <summary>D058 §1: «Таймлайн следует за плейхедом».</summary>
    [ObservableProperty] private bool _timelineFollowsPlayhead;

    partial void OnTimelineFollowsPlayheadChanged(bool value) => _settings.Update(s => s with { TimelineFollowsPlayhead = value });

    /// <summary>D063 §B: the Preview's snap to the canvas edges, screen pixels (0: off).</summary>
    [ObservableProperty] private decimal? _canvasSnapPixels;

    public decimal MinCanvasSnapPixels => (decimal)Core.Composition.CanvasSnap.MinThresholdPixels;
    public decimal MaxCanvasSnapPixels => (decimal)Core.Composition.CanvasSnap.MaxThresholdPixels;

    partial void OnCanvasSnapPixelsChanged(decimal? value)
    {
        if (value is not { } pixels) return;
        _settings.Update(s => s with { CanvasSnapPixels = Math.Clamp((double)Math.Round(pixels), Core.Composition.CanvasSnap.MinThresholdPixels,
            Core.Composition.CanvasSnap.MaxThresholdPixels) });
    }

    /// <summary>D061 amendment 1: the people's masks in the Preview (diagnostics).</summary>
    [ObservableProperty] private bool _showPeopleMasks;

    partial void OnShowPeopleMasksChanged(bool value) => _settings.Update(s => s with { ShowPeopleMasks = value });

    /// <summary>The FFmpeg executable chosen by the user (empty: the application's own), applied at the next start.</summary>
    [ObservableProperty] private string _ffmpegPath;

    [ObservableProperty] private string _ffprobePath;

    partial void OnFfmpegPathChanged(string value) => _settings.Update(s => s with { FfmpegPath = value });

    partial void OnFfprobePathChanged(string value) => _settings.Update(s => s with { FfprobePath = value });

    /// <summary>What the application uses now (diagnostics).</summary>
    public string FfmpegFound { get; }

    public string FfprobeFound { get; }

    [RelayCommand]
    private void Close() => CloseRequested?.Invoke(this, EventArgs.Empty);
}
