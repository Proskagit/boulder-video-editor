using System.Globalization;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.UI.Common;
using AiVideoEditor.UI.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AiVideoEditor.UI.ViewModels;

/// <summary>A canvas size the dialog offers: a preset of a <see cref="Group"/>, or "Custom" (no size of its own).</summary>
public sealed record CanvasPresetOption(string Group, string Name, int Width, int Height)
{
    public bool IsCustom => Width == 0;
    public string Label => IsCustom ? "Custom" : $"{Group} — {Width} × {Height}{(Name.Length > 0 ? $" ({Name})" : "")}";
    public override string ToString() => Label;
}

/// <summary>An export setting the dialog offers (D028, Step 13.9): its value, a plain label and a secondary detail.</summary>
public sealed record ExportChoice<T>(T Value, string Label, string Detail)
{
    public override string ToString() => Label;
}

/// <summary>A frame rate the dialog offers: one of the project rates, or the project's current one kept as it is
/// (<see cref="IsKeep"/>: a provisional rate, or a rate no longer offered — e.g. fixed by the first video).</summary>
public sealed record FrameRateOption(FrameRate Rate, string Label, bool IsKeep)
{
    public override string ToString() => Label;
}

/// <summary>
/// The Project Settings dialog (Phase 13 Step 13.6, D028): the canvas size and the frame rate as a <b>draft</b>. Nothing
/// here changes the project until <see cref="ApplyCommand"/>: then the draft goes to the edit service in one call —
/// <c>SetProjectSettings</c> (canvas and rate together, one Undo step), <c>SetCanvasSize</c> (the rate kept) — and a
/// refusal is shown in <see cref="Error"/> with the dialog left open. <see cref="CancelCommand"/> closes without a change.
/// The export lock is checked again right before the change (<see cref="EditingLock"/>; the service doesn't know it).
/// </summary>
public sealed partial class ProjectSettingsViewModel : ObservableObject
{
    public const string LockedMessage = "The project settings can't be changed while a video is being exported.";

    private readonly ITimelineEditService _edit;
    private readonly StatusService _status;
    private readonly Func<bool> _isLocked;
    private bool _syncing;

    public ProjectSettingsViewModel(ProjectSettings current, ITimelineEditService edit, StatusService status, Func<bool> isLocked)
    {
        _edit = edit;
        _status = status;
        _isLocked = isLocked;
        CurrentWidth = current.FrameWidth;
        CurrentHeight = current.FrameHeight;
        CurrentRate = current.FrameRate;
        IsCurrentRateLocked = current.IsFrameRateLocked;

        Rates = BuildRates(current.FrameRate, current.IsFrameRateLocked);
        _selectedRate = Rates.FirstOrDefault(r => r.IsKeep) ?? Rates.First(r => r.Rate == current.FrameRate);
        _width = current.FrameWidth;
        _height = current.FrameHeight;
        _selectedPreset = PresetFor(current.FrameWidth, current.FrameHeight);
        CurrentExport = current.Export;
        _selectedQuality = QualityOptions.Single(o => o.Value == current.Export.Quality);
        _selectedSpeed = SpeedOptions.Single(o => o.Value == current.Export.Preset);
        _selectedBitrate = BitrateOptions.Single(o => o.Value == current.Export.AudioBitrateKbps);
        Refresh();
    }

    /// <summary>The project's export settings when the dialog opened (D028, Step 13.9).</summary>
    public ExportEncoding CurrentExport { get; }

    public static IReadOnlyList<ExportChoice<ExportQuality>> QualityOptions { get; } = new[]
    {
        new ExportChoice<ExportQuality>(ExportQuality.Maximum, "Maximum", "CRF 14 — largest file"),
        new ExportChoice<ExportQuality>(ExportQuality.High, "High", "CRF 18 — default"),
        new ExportChoice<ExportQuality>(ExportQuality.Standard, "Standard", "CRF 23"),
        new ExportChoice<ExportQuality>(ExportQuality.Compact, "Compact", "CRF 28 — smallest file"),
    };

    public static IReadOnlyList<ExportChoice<ExportSpeedPreset>> SpeedOptions { get; } = new[]
    {
        new ExportChoice<ExportSpeedPreset>(ExportSpeedPreset.Fast, "Fast", "H.264 preset fast — quicker, larger file"),
        new ExportChoice<ExportSpeedPreset>(ExportSpeedPreset.Medium, "Medium", "H.264 preset medium — default"),
        new ExportChoice<ExportSpeedPreset>(ExportSpeedPreset.Slow, "Slow", "H.264 preset slow — slower, smaller file"),
    };

    public static IReadOnlyList<ExportChoice<int>> BitrateOptions { get; } =
        ExportEncoding.AudioBitratesKbps.Select(k => new ExportChoice<int>(k, $"{k} kbps", k == 192 ? "AAC — default" : "AAC")).ToList();

    public IReadOnlyList<ExportChoice<ExportQuality>> Qualities => QualityOptions;
    public IReadOnlyList<ExportChoice<ExportSpeedPreset>> Speeds => SpeedOptions;
    public IReadOnlyList<ExportChoice<int>> Bitrates => BitrateOptions;

    [ObservableProperty] private ExportChoice<ExportQuality> _selectedQuality;
    [ObservableProperty] private ExportChoice<ExportSpeedPreset> _selectedSpeed;
    [ObservableProperty] private ExportChoice<int> _selectedBitrate;

    partial void OnSelectedQualityChanged(ExportChoice<ExportQuality> value) => Error = null;
    partial void OnSelectedSpeedChanged(ExportChoice<ExportSpeedPreset> value) => Error = null;
    partial void OnSelectedBitrateChanged(ExportChoice<int> value) => Error = null;

    /// <summary>The draft export settings (applied only by Apply).</summary>
    public ExportEncoding DraftExport => new(SelectedQuality.Value, SelectedSpeed.Value, SelectedBitrate.Value);

    public int CurrentWidth { get; }
    public int CurrentHeight { get; }
    public FrameRate CurrentRate { get; }
    public bool IsCurrentRateLocked { get; }

    public static IReadOnlyList<CanvasPresetOption> AllPresets { get; } = new[]
    {
        new CanvasPresetOption("Landscape", "4K UHD", 3840, 2160),
        new CanvasPresetOption("Landscape", "QHD", 2560, 1440),
        new CanvasPresetOption("Landscape", "Full HD", 1920, 1080),
        new CanvasPresetOption("Landscape", "HD", 1280, 720),
        new CanvasPresetOption("Landscape", "", 640, 360),
        new CanvasPresetOption("Portrait", "4K", 2160, 3840),
        new CanvasPresetOption("Portrait", "Full HD", 1080, 1920),
        new CanvasPresetOption("Portrait", "HD", 720, 1280),
        new CanvasPresetOption("Square", "", 1080, 1080),
        new CanvasPresetOption("4:5", "", 1080, 1350),
        new CanvasPresetOption("", "", 0, 0)
    };

    public static CanvasPresetOption Custom => AllPresets[^1];

    /// <summary>The presets for the view (Landscape / Portrait / Square / 4:5, then Custom).</summary>
    public IReadOnlyList<CanvasPresetOption> Presets => AllPresets;

    public IReadOnlyList<FrameRateOption> Rates { get; }

    [ObservableProperty] private CanvasPresetOption _selectedPreset;

    /// <summary>The draft width / height (null while a field is empty).</summary>
    [ObservableProperty] private decimal? _width;
    [ObservableProperty] private decimal? _height;

    [ObservableProperty] private FrameRateOption _selectedRate;

    /// <summary>Why the draft size can't be used (D028 canvas rules), live; null when it can.</summary>
    [ObservableProperty] private string? _canvasError;

    /// <summary>What a size change does to the clips (CS-1 B), e.g. "Positions and text sizes will be scaled by 56.25 %."</summary>
    [ObservableProperty] private string? _scaleNotice;

    /// <summary>What a rate change does to the timeline; null when the rate stays.</summary>
    [ObservableProperty] private string? _rateNotice;

    /// <summary>The service's refusal of the last Apply (shown under Apply; the dialog stays open).</summary>
    [ObservableProperty] private string? _error;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand))]
    private bool _canApply;

    public string CurrentSummary => Summary(CurrentWidth, CurrentHeight, CurrentRate, IsCurrentRateLocked);

    /// <summary>Raised when the dialog should close: true after a successful Apply, false on Cancel.</summary>
    public event EventHandler<bool>? CloseRequested;

    public static string Summary(int width, int height, FrameRate rate, bool locked) =>
        $"{width} × {height} · {RateFormat.Fps(rate)}{(locked ? "" : " (provisional)")}";

    partial void OnSelectedPresetChanged(CanvasPresetOption value)
    {
        if (_syncing) return;
        if (!value.IsCustom)
        {
            _syncing = true;
            (Width, Height) = (value.Width, value.Height);
            _syncing = false;
        }
        Refresh();
    }

    partial void OnWidthChanged(decimal? value) => OnSizeEdited();
    partial void OnHeightChanged(decimal? value) => OnSizeEdited();
    partial void OnSelectedRateChanged(FrameRateOption value) => Refresh();

    private void OnSizeEdited()
    {
        if (_syncing) return;
        _syncing = true;
        SelectedPreset = DraftSize() is (int w, int h) ? PresetFor(w, h) : Custom;
        _syncing = false;
        Refresh();
    }

    [RelayCommand]
    private void Swap()
    {
        _syncing = true;
        (Width, Height) = (Height, Width);
        _syncing = false;
        OnSizeEdited();
    }

    [RelayCommand(CanExecute = nameof(CanApply))]
    private void Apply()
    {
        if (_isLocked())
        {
            Error = LockedMessage;
            return;
        }
        if (DraftSize() is not (int width, int height) || CanvasError is not null) return;

        // One call: canvas, rate (null = kept as it is) and export settings — one Undo step when several change (13.9).
        var result = _edit.SetProjectSettings(width, height, SelectedRate.IsKeep ? null : SelectedRate.Rate, DraftExport);

        if (!result.Success)
        {
            Error = result.Message ?? "The project settings could not be changed.";
            return;
        }
        Error = null;
        if (!result.NoChange)
            _status.Report(result.Message ?? $"Frame size set to {width} × {height}.");
        CloseRequested?.Invoke(this, true);
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(this, false);

    /// <summary>The draft size as whole pixels, or null while a field is empty or not a whole number.</summary>
    private (int Width, int Height)? DraftSize() =>
        Width is { } w && Height is { } h && w == decimal.Truncate(w) && h == decimal.Truncate(h)
            && w is >= int.MinValue and <= int.MaxValue && h is >= int.MinValue and <= int.MaxValue
            ? ((int)w, (int)h)
            : null;

    private void Refresh()
    {
        Error = null;
        var size = DraftSize();
        CanvasError = size is (int w, int h)
            ? ProjectSettingsRules.CanvasError(w, h)
            : Width is null || Height is null ? "Enter the frame width and height." : "The frame width and height must be whole numbers.";

        ScaleNotice = null;
        if (CanvasError is null && size is (int nw, int nh) && (nw != CurrentWidth || nh != CurrentHeight))
        {
            var factor = ProjectSettingsRules.ContainFactor(CurrentWidth, CurrentHeight, nw, nh);
            ScaleNotice = factor == 1
                ? "Positions and text sizes stay as they are."
                : $"Positions and text sizes will be scaled by {(factor * 100).ToString("0.##", CultureInfo.InvariantCulture)} %.";
        }

        RateNotice = !SelectedRate.IsKeep && SelectedRate.Rate != CurrentRate
            ? $"Clips move to the nearest frames of {RateFormat.Fps(SelectedRate.Rate)}; fades, dissolves and markers keep their time."
            : !SelectedRate.IsKeep && !IsCurrentRateLocked
                ? $"{RateFormat.Fps(SelectedRate.Rate)} will be kept: a video added later won't change it."
                : null;

        CanApply = CanvasError is null;
    }

    private static CanvasPresetOption PresetFor(int width, int height) =>
        AllPresets.FirstOrDefault(p => p.Width == width && p.Height == height) ?? Custom;

    private static IReadOnlyList<FrameRateOption> BuildRates(FrameRate current, bool locked)
    {
        var list = new List<FrameRateOption>();
        if (!locked)
            list.Add(new FrameRateOption(current, $"{RateFormat.Fps(current)} (provisional)", IsKeep: true));
        else if (!ProjectSettingsRules.IsSelectableFrameRate(current))
            list.Add(new FrameRateOption(current, $"{RateFormat.Fps(current)} (current)", IsKeep: true));
        list.AddRange(ProjectSettingsRules.SelectableFrameRates.Select(r => new FrameRateOption(r, RateFormat.Fps(r), IsKeep: false)));
        return list;
    }
}
