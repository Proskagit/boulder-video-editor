using System.Globalization;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Export;
using AiVideoEditor.UI.Localization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AiVideoEditor.UI.ViewModels;

/// <summary>
/// The export settings as the user chooses them (D059 §4), shared by the export dialog and the project settings: the
/// profile («Быстрый» / «Сбалансированный» / «Высокое качество» / «Пользовательский»), the encoder (libx264 always; NVIDIA
/// NVENC only when <see cref="ExportEncoderAvailability.Nvenc"/> confirmed it), the AAC bitrate, and — for
/// «Пользовательский» — the quality level, the speed, the rate control and the video bitrate. A draft: <see cref="Value"/>
/// is what the host applies.
/// <list type="bullet">
/// <item>Choosing «Быстрый» switches the encoder to NVENC when it is available (speed first); the user can switch back.</item>
/// <item>Changing a «Пользовательский» field (quality, speed, rate control, bitrate) selects «Пользовательский».</item>
/// <item>With an output (the export dialog), <see cref="EstimateText"/> is the expected file size of the current choice
/// (<see cref="ExportSizeEstimate"/> — a range, marked as an estimate), updated on every change.</item>
/// <item>A project whose choice is NVENC on a computer without it keeps the choice (<see cref="Value"/> unchanged) and says
/// that libx264 will encode (<see cref="UnavailableNote"/>).</item>
/// <item>D060 §3: the export resolution is chosen in the export dialog (<see cref="Resolution"/>); the estimate and the plan
/// follow the output's size. The project settings don't show it and keep the project's value unchanged.</item>
/// </list>
/// </summary>
public sealed partial class ExportEncodingEditorViewModel : ObservableObject
{
    private readonly ExportOutput? _output;
    private ExportResolution _resolution;
    private ExportEncoderAvailability _available;
    private bool _syncing;

    public ExportEncodingEditorViewModel(ExportEncoding current, ExportEncoderAvailability? available = null, ExportOutput? output = null)
    {
        ArgumentNullException.ThrowIfNull(current);
        _output = output;
        _resolution = current.Resolution;
        _available = available ?? ExportEncoderAvailability.SoftwareOnly;
        Initial = current;
        Encoders = BuildEncoders(_available, current.Encoder);
        _selectedProfile = ProfileOptions.Single(o => o.Value == current.Profile);
        _selectedEncoder = Encoders.Single(o => o.Value == current.Encoder);
        _selectedQuality = ProjectSettingsViewModel.QualityOptions.Single(o => o.Value == current.Quality);
        _selectedSpeed = ProjectSettingsViewModel.SpeedOptions.Single(o => o.Value == current.Preset);
        _selectedBitrate = ProjectSettingsViewModel.BitrateOptions.Single(o => o.Value == current.AudioBitrateKbps);
        _selectedRateControl = RateControlOptions.Single(o => o.Value == current.RateControl);
        _selectedVideoBitrate = VideoBitrateOptions.Single(o => o.Value == current.VideoBitrateMbps);
        Refresh();
    }

    /// <summary>The settings the editor opened with.</summary>
    public ExportEncoding Initial { get; }

    public static IReadOnlyList<ExportChoice<ExportProfile>> ProfileOptions { get; } = new[]
    {
        new ExportChoice<ExportProfile>(ExportProfile.Fast, Strings.ExportProfile_Fast, Strings.ExportProfile_FastDetail),
        new ExportChoice<ExportProfile>(ExportProfile.Balanced, Strings.ExportProfile_Balanced, Strings.ExportProfile_BalancedDetail),
        new ExportChoice<ExportProfile>(ExportProfile.HighQuality, Strings.ExportProfile_HighQuality, Strings.ExportProfile_HighQualityDetail),
        new ExportChoice<ExportProfile>(ExportProfile.Custom, Strings.ExportProfile_Custom, Strings.ExportProfile_CustomDetail),
    };

    public static IReadOnlyList<ExportChoice<ExportRateControl>> RateControlOptions { get; } = new[]
    {
        new ExportChoice<ExportRateControl>(ExportRateControl.ConstantQuality, Strings.ExportRate_Quality, Strings.ExportRate_QualityDetail),
        new ExportChoice<ExportRateControl>(ExportRateControl.Bitrate, Strings.ExportRate_Bitrate, Strings.ExportRate_BitrateDetail),
    };

    public static IReadOnlyList<ExportChoice<int>> VideoBitrateOptions { get; } =
        ExportEncoding.VideoBitratesMbps.Select(m => new ExportChoice<int>(m, string.Format(Strings.Unit_Mbps, m), "H.264")).ToList();

    private static readonly ExportChoice<ExportVideoEncoder> X264 =
        new(ExportVideoEncoder.Libx264, Strings.ExportEncoder_X264, Strings.ExportEncoder_X264Detail);
    private static readonly ExportChoice<ExportVideoEncoder> Nvenc =
        new(ExportVideoEncoder.Nvenc, Strings.ExportEncoder_Nvenc, Strings.ExportEncoder_NvencDetail);

    /// <summary>libx264, and NVENC first when it is available. A saved NVENC choice on a computer without it stays in the
    /// list (marked unavailable) so the project's choice is not lost by opening the dialog.</summary>
    private static IReadOnlyList<ExportChoice<ExportVideoEncoder>> BuildEncoders(ExportEncoderAvailability available, ExportVideoEncoder current) =>
        available.Nvenc ? new[] { Nvenc, X264 }
        : current == ExportVideoEncoder.Nvenc ? new[] { X264, new ExportChoice<ExportVideoEncoder>(ExportVideoEncoder.Nvenc, Strings.ExportEncoder_NvencUnavailable, Strings.ExportEncoder_NvencDetail) }
        : new[] { X264 };

    public IReadOnlyList<ExportChoice<ExportProfile>> Profiles => ProfileOptions;
    public IReadOnlyList<ExportChoice<ExportQuality>> Qualities => ProjectSettingsViewModel.QualityOptions;
    public IReadOnlyList<ExportChoice<ExportSpeedPreset>> Speeds => ProjectSettingsViewModel.SpeedOptions;
    public IReadOnlyList<ExportChoice<int>> Bitrates => ProjectSettingsViewModel.BitrateOptions;
    public IReadOnlyList<ExportChoice<ExportRateControl>> RateControls => RateControlOptions;
    public IReadOnlyList<ExportChoice<int>> VideoBitrates => VideoBitrateOptions;

    [ObservableProperty] private IReadOnlyList<ExportChoice<ExportVideoEncoder>> _encoders;

    [ObservableProperty] private ExportChoice<ExportProfile> _selectedProfile;
    [ObservableProperty] private ExportChoice<ExportVideoEncoder> _selectedEncoder;
    [ObservableProperty] private ExportChoice<ExportQuality> _selectedQuality;
    [ObservableProperty] private ExportChoice<ExportSpeedPreset> _selectedSpeed;
    [ObservableProperty] private ExportChoice<int> _selectedBitrate;
    [ObservableProperty] private ExportChoice<ExportRateControl> _selectedRateControl;
    [ObservableProperty] private ExportChoice<int> _selectedVideoBitrate;

    /// <summary>The «Пользовательский» fields are shown.</summary>
    [ObservableProperty] private bool _isCustom;

    /// <summary>«Пользовательский» with an average bitrate: the bitrate field is shown, the quality field not.</summary>
    [ObservableProperty] private bool _isBitrate;

    /// <summary>The encoding actually used for the current choice, e.g. "libx264 medium CRF 18".</summary>
    [ObservableProperty] private string _planText = "";

    /// <summary>What the chosen encoder means for the speed (one short line).</summary>
    [ObservableProperty] private string _speedNote = "";

    /// <summary>The expected size of the file (export dialog only), always marked as an estimate; null without an output.</summary>
    [ObservableProperty] private string? _estimateText;

    /// <summary>Why NVENC will not encode although chosen; null otherwise.</summary>
    [ObservableProperty] private string? _unavailableNote;

    /// <summary>The draft.</summary>
    public ExportEncoding Value => new(SelectedQuality.Value, SelectedSpeed.Value, SelectedBitrate.Value)
    {
        Profile = SelectedProfile.Value, Encoder = SelectedEncoder.Value, RateControl = SelectedRateControl.Value,
        VideoBitrateMbps = SelectedVideoBitrate.Value, Resolution = _resolution
    };

    /// <summary>D060 §3: the export resolution of the draft (the export dialog sets it; the project settings keep the
    /// project's). Changing it refreshes the plan and the estimate for the new output size.</summary>
    public ExportResolution Resolution
    {
        get => _resolution;
        set
        {
            if (_resolution == value) return;
            _resolution = value;
            OnPropertyChanged();
            Refresh();
        }
    }

    /// <summary>The output's pixel size for the draft: the canvas or the chosen preset (null without an output).</summary>
    public Core.Composition.FrameSize? OutputSize => _output is null ? null : ExportResolutions.SizeFor(_resolution, _output.Size);

    /// <summary>Raised after any change of the draft.</summary>
    public event EventHandler? Changed;

    /// <summary>The NVENC check finished after the dialog opened: the encoder list follows it, the choice is kept.</summary>
    public void SetAvailability(ExportEncoderAvailability available)
    {
        ArgumentNullException.ThrowIfNull(available);
        if (available == _available) return;
        _available = available;
        var current = SelectedEncoder.Value;
        _syncing = true;
        Encoders = BuildEncoders(available, current);
        SelectedEncoder = Encoders.Single(o => o.Value == current);
        _syncing = false;
        Refresh();
    }

    partial void OnSelectedProfileChanged(ExportChoice<ExportProfile> value)
    {
        if (_syncing) return;
        // «Быстрый»: speed first — the hardware encoder when this computer has it.
        if (value.Value == ExportProfile.Fast && _available.Nvenc && SelectedEncoder.Value != ExportVideoEncoder.Nvenc)
        {
            _syncing = true;
            SelectedEncoder = Encoders.Single(o => o.Value == ExportVideoEncoder.Nvenc);
            _syncing = false;
        }
        Refresh();
    }

    /// <summary>D074 amendment 1: «Пользовательский» chosen after a profile starts from that profile's level and speed
    /// («Высокое качество» → «Максимальное» CRF 14, «Медленно»), not from whatever the fields held.</summary>
    partial void OnSelectedProfileChanged(ExportChoice<ExportProfile>? oldValue, ExportChoice<ExportProfile> newValue)
    {
        if (_syncing || newValue.Value != ExportProfile.Custom || oldValue is null || oldValue.Value == ExportProfile.Custom) return;
        var (quality, speed) = ExportEncoding.CustomOf(oldValue.Value);
        _syncing = true;
        SelectedQuality = Qualities.Single(o => o.Value == quality);
        SelectedSpeed = Speeds.Single(o => o.Value == speed);
        _syncing = false;
        Refresh();
    }

    partial void OnSelectedEncoderChanged(ExportChoice<ExportVideoEncoder> value) { if (!_syncing) Refresh(); }
    partial void OnSelectedBitrateChanged(ExportChoice<int> value) { if (!_syncing) Refresh(); }
    partial void OnSelectedQualityChanged(ExportChoice<ExportQuality> value) => CustomFieldChanged();
    partial void OnSelectedSpeedChanged(ExportChoice<ExportSpeedPreset> value) => CustomFieldChanged();
    partial void OnSelectedRateControlChanged(ExportChoice<ExportRateControl> value) => CustomFieldChanged();
    partial void OnSelectedVideoBitrateChanged(ExportChoice<int> value) => CustomFieldChanged();

    private void CustomFieldChanged()
    {
        if (_syncing) return;
        if (SelectedProfile.Value != ExportProfile.Custom)
        {
            _syncing = true;
            SelectedProfile = ProfileOptions.Single(o => o.Value == ExportProfile.Custom);
            _syncing = false;
        }
        Refresh();
    }

    private void Refresh()
    {
        IsCustom = SelectedProfile.Value == ExportProfile.Custom;
        IsBitrate = IsCustom && SelectedRateControl.Value == ExportRateControl.Bitrate;
        var value = Value;
        var size = OutputSize ?? new Core.Composition.FrameSize(1920, 1080);
        var plan = ExportVideoPlan.For(value, size, _available);
        PlanText = string.Format(Strings.ExportPlan, plan.Description);
        SpeedNote = plan.Encoder == ExportVideoEncoder.Nvenc ? Strings.ExportSpeed_Nvenc
            : plan.Preset is "veryfast" or "faster" or "fast" ? Strings.ExportSpeed_X264Fast
            : plan.Preset is "slow" or "slower" ? Strings.ExportSpeed_X264Slow
            : Strings.ExportSpeed_X264;
        UnavailableNote = !plan.FellBack ? null
            : string.Format(Strings.ExportEncoder_Unavailable, _available.Nvenc
                ? string.Format(Strings.ExportEncoder_TooLarge, ExportEncoderAvailability.NvencMaxSide)
                : _available.NvencUnavailableReason ?? Strings.ExportEncoder_NotFound);
        if (_output is { FrameCount: > 0 } output)
        {
            var estimate = ExportSizeEstimate.For(plan, output with { Size = size }, value.AudioBitrateKbps);
            EstimateText = estimate.FromBitrate
                ? string.Format(Strings.ExportEstimate_Bitrate, Size((estimate.LowBytes + estimate.HighBytes) / 2))
                : string.Format(Strings.ExportEstimate, Size(estimate.LowBytes), Size(estimate.HighBytes));
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>"350 МБ", "2,4 ГБ" (binary units, as Windows Explorer shows file sizes).</summary>
    public static string Size(long bytes)
    {
        var mb = bytes / 1048576.0;
        return mb < 1024
            ? string.Format(Strings.Size_MB, mb < 10 ? mb.ToString("0.0", CultureInfo.CurrentCulture) : mb.ToString("0", CultureInfo.CurrentCulture))
            : string.Format(Strings.Size_GB, (mb / 1024).ToString("0.0", CultureInfo.CurrentCulture));
    }
}
