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
    CorosFit
}

/// <summary>
/// D057 §3.8: the application's settings window — the global settings of the user, in sections: General (the interface
/// language), FFmpeg (the executables), Timeline and markup (the automatic hold proposals), COROS / FIT (the heart rate's
/// display offset and the start calibration of D051 §6, <see cref="Fit"/>). Every change is written to the per-user file
/// at once; the language and the FFmpeg paths apply at the next start. Project settings (the training's clock correction
/// <c>CC</c>, D049) are never here.
/// </summary>
public sealed partial class AppSettingsViewModel : ViewModelBase
{
    private readonly IUserSettingsService _settings;

    public AppSettingsViewModel(IUserSettingsService settings, FitSettingsViewModel? fit, AppSettingsSection section = AppSettingsSection.General,
        string? ffmpegFound = null, string? ffprobeFound = null)
    {
        _settings = settings;
        Fit = fit;
        Section = section;
        _selectedSectionIndex = (int)section;
        var current = settings.Current;
        _heartRateOffsetSeconds = (decimal)current.HeartRateDisplayOffsetTicks / TimeSpan.TicksPerSecond;
        _holdProposalsEnabled = current.HoldProposalsEnabled;
        _timelineFollowsPlayhead = current.TimelineFollowsPlayhead;
        _languageIndex = current.Language switch { "en" => 2, "ru" => 1, _ => 0 };
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
        Strings.AppSettings_NavGeneral, Strings.AppSettings_NavFfmpeg, Strings.AppSettings_NavTimelineAndMarkup, Strings.AppSettings_NavCorosFit
    };

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsGeneral), nameof(ShowsFfmpeg), nameof(ShowsTimelineAndMarkup), nameof(ShowsCorosFit))]
    private int _selectedSectionIndex;

    public AppSettingsSection SelectedSection => (AppSettingsSection)Math.Clamp(SelectedSectionIndex, 0, SectionNames.Count - 1);

    public bool ShowsGeneral => SelectedSection == AppSettingsSection.General;
    public bool ShowsFfmpeg => SelectedSection == AppSettingsSection.Ffmpeg;
    public bool ShowsTimelineAndMarkup => SelectedSection == AppSettingsSection.TimelineAndMarkup;
    public bool ShowsCorosFit => SelectedSection == AppSettingsSection.CorosFit;

    public event EventHandler? CloseRequested;

    public IReadOnlyList<string> Languages { get; } = new[] { Strings.AppSettings_LanguageDefault, "Русский", "English" };

    [ObservableProperty] private int _languageIndex;

    partial void OnLanguageIndexChanged(int value) =>
        _settings.Update(s => s with { Language = value switch { 1 => "ru", 2 => "en", _ => null } });

    /// <summary>D057 §3.8: the heart rate's display offset in seconds (negative: earlier); whole seconds.</summary>
    [ObservableProperty] private decimal? _heartRateOffsetSeconds;

    public decimal MinHeartRateOffset => -(decimal)UserSettings.MaxHeartRateDisplayOffset.Ticks / TimeSpan.TicksPerSecond;

    public decimal MaxHeartRateOffset => (decimal)UserSettings.MaxHeartRateDisplayOffset.Ticks / TimeSpan.TicksPerSecond;

    partial void OnHeartRateOffsetSecondsChanged(decimal? value)
    {
        if (value is not { } seconds) return;
        var whole = Math.Round(seconds, MidpointRounding.AwayFromZero);
        _settings.Update(s => s with { HeartRateDisplayOffsetTicks = (long)whole * TimeSpan.TicksPerSecond });
    }

    [RelayCommand]
    private void ResetHeartRateOffset() => HeartRateOffsetSeconds = (decimal)UserSettings.DefaultHeartRateDisplayOffset.Ticks / TimeSpan.TicksPerSecond;

    /// <summary>D057 §3.15: generate hold proposals on entering the markup mode (off by default).</summary>
    [ObservableProperty] private bool _holdProposalsEnabled;

    partial void OnHoldProposalsEnabledChanged(bool value) => _settings.Update(s => s with { HoldProposalsEnabled = value });

    /// <summary>D058 §1: «Таймлайн следует за плейхедом».</summary>
    [ObservableProperty] private bool _timelineFollowsPlayhead;

    partial void OnTimelineFollowsPlayheadChanged(bool value) => _settings.Update(s => s with { TimelineFollowsPlayhead = value });

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
