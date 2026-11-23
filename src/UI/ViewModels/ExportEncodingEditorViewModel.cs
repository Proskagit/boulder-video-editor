using System.Collections.ObjectModel;
using System.Globalization;
using AiVideoEditor.Core.Composition;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Export;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.UI.Localization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AiVideoEditor.UI.ViewModels;

/// <summary>What an item of «Пресет» is (D076).</summary>
public enum ExportPresetChoiceKind
{
    /// <summary>One of the built-in presets (<see cref="ExportPresets"/>).</summary>
    BuiltIn,
    /// <summary>One of the user's own (<see cref="UserSettings.ExportPresets"/>).</summary>
    Saved,
    /// <summary>«Пользовательский» — the settings are none of the presets.</summary>
    Custom,
    /// <summary>«＋ Сохранить пресет…» — not a choice: opens the name field.</summary>
    SaveNew
}

/// <summary>An item of «Пресет»: a built-in or saved preset, «Пользовательский» or «＋ Сохранить пресет…».</summary>
public sealed record ExportPresetChoice(ExportPresetChoiceKind Kind, string Label, string Detail, ExportEncoding? Settings = null,
    ExportPresetKind? BuiltIn = null)
{
    public override string ToString() => Label;
}

/// <summary>
/// The export settings as the user chooses them (D059 §4, D076), shared by the export dialog and the project settings.
/// <list type="bullet">
/// <item>On top: «Пресет» — the built-in presets («Быстрый», «Средний», «Высокий», «Максимальный»), the user's saved ones,
/// «Пользовательский» (shown when the settings are none of them) and «＋ Сохранить пресет…».</item>
/// <item>«Подробные настройки» show every field of the chosen preset, always: the file type, the codec, the encoder, the
/// resolution, the rate control, the quality or the video bitrate, the speed and the sound. Changing one makes the settings
/// «Пользовательский» — or the preset they now equal.</item>
/// <item>A preset's resolution is a ceiling at the canvas (Q4 A); choosing a built-in preset keeps the file type (AV1 never in
/// MOV); a saved preset states its file type too.</item>
/// <item>The encoder: NVENC where the check confirmed the codec's encoder, the processor always (the same codec). A choice of
/// NVENC on a computer without it is kept and the note says the processor will encode.</item>
/// <item>With an output (the export dialog), <see cref="EstimateText"/> is the expected file size — a range, marked as an
/// estimate, per codec and encoder.</item>
/// </list>
/// </summary>
public sealed partial class ExportEncodingEditorViewModel : ObservableObject
{
    private static readonly FrameSize FallbackCanvas = new(1920, 1080);

    private readonly ExportOutput? _output;
    private readonly FrameSize _canvas;
    private readonly IUserSettingsService? _userSettings;
    private ExportEncoderAvailability _available;
    private ExportPresetChoice? _lastPreset;
    private bool _syncing;

    /// <param name="current">The settings the editor opens with.</param>
    /// <param name="available">Which encoders work here; null = the processor only.</param>
    /// <param name="output">The export's output (the export dialog: the size estimate); its size is the canvas.</param>
    /// <param name="canvas">The canvas without an output (the project settings); null = the output's size or Full HD.</param>
    /// <param name="userSettings">Where the user's saved presets are kept; null = none offered (tests, no settings).</param>
    public ExportEncodingEditorViewModel(ExportEncoding current, ExportEncoderAvailability? available = null, ExportOutput? output = null,
        FrameSize? canvas = null, IUserSettingsService? userSettings = null)
    {
        ArgumentNullException.ThrowIfNull(current);
        _output = output;
        _canvas = canvas ?? output?.Size ?? FallbackCanvas;
        if (!_canvas.IsValid) _canvas = FallbackCanvas;
        _userSettings = userSettings;
        _available = available ?? ExportEncoderAvailability.SoftwareOnly;
        Initial = current;
        Resolutions = BuildResolutions(_canvas);
        _selectedCodec = CodecOptions.Single(o => o.Value == current.Codec);
        _containers = BuildContainers(current.Codec);
        _selectedContainer = _containers.FirstOrDefault(o => o.Value == current.Container) ?? _containers[0];
        _encoders = BuildEncoders(_available, current.Codec, current.Encoder);
        _selectedEncoder = _encoders.Single(o => o.Value == current.Encoder);
        var resolution = ExportResolutions.Normalize(current, _canvas).Resolution;
        _selectedResolution = Resolutions.FirstOrDefault(r => r.Value == resolution) ?? Resolutions.First(r => r.Value == ExportResolution.Project);
        _selectedQuality = ProjectSettingsViewModel.QualityOptions.Single(o => o.Value == current.Quality);
        _selectedSpeed = ProjectSettingsViewModel.SpeedOptions.Single(o => o.Value == current.Preset);
        _selectedBitrate = ProjectSettingsViewModel.BitrateOptions.Single(o => o.Value == current.AudioBitrateKbps);
        _selectedRateControl = RateControlOptions.Single(o => o.Value == current.RateControl);
        _selectedVideoBitrate = VideoBitrateOptions.Single(o => o.Value == current.VideoBitrateMbps);
        _presets = new ObservableCollection<ExportPresetChoice>();
        _newPresetName = "";
        RebuildPresets();
        if (_userSettings is not null) _userSettings.Changed += OnUserSettingsChanged;
        Refresh();
    }

    /// <summary>The settings the editor opened with.</summary>
    public ExportEncoding Initial { get; }

    // --- The offered values ------------------------------------------------------------------------------------------------

    public static IReadOnlyList<ExportChoice<ExportVideoCodec>> CodecOptions { get; } = new[]
    {
        new ExportChoice<ExportVideoCodec>(ExportVideoCodec.H264, "H.264", Strings.ExportCodec_H264Detail),
        new ExportChoice<ExportVideoCodec>(ExportVideoCodec.Hevc, "HEVC (H.265)", Strings.ExportCodec_HevcDetail),
        new ExportChoice<ExportVideoCodec>(ExportVideoCodec.Av1, "AV1", Strings.ExportCodec_Av1Detail),
    };

    private static readonly ExportChoice<ExportContainer>[] AllContainers =
    {
        new(ExportContainer.Mp4, "MP4", Strings.ExportContainer_Mp4Detail),
        new(ExportContainer.Mov, "MOV", Strings.ExportContainer_MovDetail),
        new(ExportContainer.Mkv, "MKV", Strings.ExportContainer_MkvDetail),
    };

    public static IReadOnlyList<ExportChoice<ExportRateControl>> RateControlOptions { get; } = new[]
    {
        new ExportChoice<ExportRateControl>(ExportRateControl.ConstantQuality, Strings.ExportRate_Quality, Strings.ExportRate_QualityDetail),
        new ExportChoice<ExportRateControl>(ExportRateControl.Bitrate, Strings.ExportRate_Bitrate, Strings.ExportRate_BitrateDetail),
    };

    public static IReadOnlyList<ExportChoice<int>> VideoBitrateOptions { get; } =
        ExportEncoding.VideoBitratesMbps.Select(m => new ExportChoice<int>(m, string.Format(Strings.Unit_Mbps, m), "")).ToList();

    /// <summary>D076: the file types that can hold <paramref name="codec"/> (no MOV for AV1).</summary>
    public static IReadOnlyList<ExportChoice<ExportContainer>> BuildContainers(ExportVideoCodec codec) =>
        AllContainers.Where(c => ExportEncoding.Holds(c.Value, codec)).ToList();

    /// <summary>The encoders of <paramref name="codec"/>: NVENC first where available; the processor always. A saved NVENC
    /// choice on a computer without it stays in the list (marked unavailable), so opening the dialog loses nothing.</summary>
    public static IReadOnlyList<ExportChoice<ExportVideoEncoder>> BuildEncoders(ExportEncoderAvailability available, ExportVideoCodec codec,
        ExportVideoEncoder current)
    {
        var software = new ExportChoice<ExportVideoEncoder>(ExportVideoEncoder.Software,
            string.Format(Strings.ExportEncoder_Software, SoftwareName(codec)), Strings.ExportEncoder_SoftwareDetail);
        var nvenc = new ExportChoice<ExportVideoEncoder>(ExportVideoEncoder.Nvenc, Strings.ExportEncoder_Nvenc, Strings.ExportEncoder_NvencDetail);
        return available.NvencFor(codec) ? new[] { nvenc, software }
            : current == ExportVideoEncoder.Nvenc ? new[] { software, nvenc with { Label = Strings.ExportEncoder_NvencUnavailable } }
            : new[] { software };
    }

    private static string SoftwareName(ExportVideoCodec codec) => codec switch
    {
        ExportVideoCodec.Hevc => "libx265",
        ExportVideoCodec.Av1 => "SVT-AV1",
        _ => "libx264"
    };

    /// <summary>The resolutions for <paramref name="canvas"/>, largest first, each with its size; «Как в проекте» first.</summary>
    public static IReadOnlyList<ExportChoice<ExportResolution>> BuildResolutions(FrameSize canvas)
    {
        var list = new List<ExportChoice<ExportResolution>>
        {
            new(ExportResolution.Project, Strings.ExportResolution_Project, string.Format(Strings.ExportResolution_Size, canvas.Width, canvas.Height))
        };
        foreach (var preset in ExportResolutions.Presets)
        {
            var size = ExportResolutions.SizeFor(preset, canvas);
            if (size == canvas) continue;   // «Как в проекте» is that size
            list.Add(new ExportChoice<ExportResolution>(preset, ResolutionName(preset), string.Format(Strings.ExportResolution_Size, size.Width, size.Height)));
        }
        return list;
    }

    public static string ResolutionName(ExportResolution resolution) => resolution switch
    {
        ExportResolution.Uhd4K => Strings.ExportResolution_Uhd4K,
        ExportResolution.Qhd => Strings.ExportResolution_Qhd,
        ExportResolution.FullHd => Strings.ExportResolution_FullHd,
        ExportResolution.Hd => Strings.ExportResolution_Hd,
        ExportResolution.Sd => Strings.ExportResolution_Sd,
        _ => Strings.ExportResolution_Project
    };

    public static string PresetName(ExportPresetKind kind) => kind switch
    {
        ExportPresetKind.Fast => Strings.ExportPreset_Fast,
        ExportPresetKind.Medium => Strings.ExportPreset_Medium,
        ExportPresetKind.High => Strings.ExportPreset_High,
        _ => Strings.ExportPreset_Maximum
    };

    private static string PresetDetail(ExportPresetKind kind) => kind switch
    {
        ExportPresetKind.Fast => Strings.ExportPreset_FastDetail,
        ExportPresetKind.Medium => Strings.ExportPreset_MediumDetail,
        ExportPresetKind.High => Strings.ExportPreset_HighDetail,
        _ => Strings.ExportPreset_MaximumDetail
    };

    public IReadOnlyList<ExportChoice<ExportVideoCodec>> Codecs => CodecOptions;
    public IReadOnlyList<ExportChoice<ExportQuality>> Qualities => ProjectSettingsViewModel.QualityOptions;
    public IReadOnlyList<ExportChoice<ExportSpeedPreset>> Speeds => ProjectSettingsViewModel.SpeedOptions;
    public IReadOnlyList<ExportChoice<int>> Bitrates => ProjectSettingsViewModel.BitrateOptions;
    public IReadOnlyList<ExportChoice<ExportRateControl>> RateControls => RateControlOptions;
    public IReadOnlyList<ExportChoice<int>> VideoBitrates => VideoBitrateOptions;
    public IReadOnlyList<ExportChoice<ExportResolution>> Resolutions { get; }

    [ObservableProperty] private ObservableCollection<ExportPresetChoice> _presets;
    [ObservableProperty] private IReadOnlyList<ExportChoice<ExportContainer>> _containers;
    [ObservableProperty] private IReadOnlyList<ExportChoice<ExportVideoEncoder>> _encoders;

    [ObservableProperty] private ExportPresetChoice? _selectedPreset;
    [ObservableProperty] private ExportChoice<ExportVideoCodec> _selectedCodec;
    [ObservableProperty] private ExportChoice<ExportContainer> _selectedContainer;
    [ObservableProperty] private ExportChoice<ExportVideoEncoder> _selectedEncoder;
    [ObservableProperty] private ExportChoice<ExportResolution> _selectedResolution;
    [ObservableProperty] private ExportChoice<ExportQuality> _selectedQuality;
    [ObservableProperty] private ExportChoice<ExportSpeedPreset> _selectedSpeed;
    [ObservableProperty] private ExportChoice<int> _selectedBitrate;
    [ObservableProperty] private ExportChoice<ExportRateControl> _selectedRateControl;
    [ObservableProperty] private ExportChoice<int> _selectedVideoBitrate;

    /// <summary>An average bitrate: the bitrate field is shown, the quality field not.</summary>
    [ObservableProperty] private bool _isBitrate;

    /// <summary>The encoding actually used for the current choice, e.g. "NVENC HEVC p7 CQ 21".</summary>
    [ObservableProperty] private string _planText = "";

    /// <summary>What the chosen encoder means for the speed (one short line).</summary>
    [ObservableProperty] private string _speedNote = "";

    /// <summary>Where the chosen codec plays (one short line).</summary>
    [ObservableProperty] private string _codecNote = "";

    /// <summary>The expected size of the file (export dialog only), always marked as an estimate; null without an output.</summary>
    [ObservableProperty] private string? _estimateText;

    /// <summary>Why NVENC will not encode although chosen, or why the codec can't be encoded here; null otherwise.</summary>
    [ObservableProperty] private string? _unavailableNote;

    /// <summary>D076: the chosen codec can be encoded here (NVENC or this FFmpeg's software encoder) — the export may start.</summary>
    [ObservableProperty] private bool _canEncode = true;

    /// <summary>The settings are one of the user's saved presets (✕ deletes it).</summary>
    [ObservableProperty] private bool _isSavedPreset;

    /// <summary>The name field of «＋ Сохранить пресет…» is open.</summary>
    [ObservableProperty] private bool _isSavingPreset;

    /// <summary>The name the shown settings are saved under.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SavePresetCommand))]
    private string _newPresetName;

    /// <summary>Saved presets can be kept (the global settings exist).</summary>
    public bool CanSavePresets => _userSettings is not null;

    /// <summary>The draft.</summary>
    public ExportEncoding Value => new(SelectedQuality.Value, SelectedSpeed.Value, SelectedBitrate.Value)
    {
        Codec = SelectedCodec.Value, Container = SelectedContainer.Value, Encoder = SelectedEncoder.Value,
        RateControl = SelectedRateControl.Value, VideoBitrateMbps = SelectedVideoBitrate.Value, Resolution = SelectedResolution.Value
    };

    /// <summary>The draft's export resolution (the export dialog reads it for its line).</summary>
    public ExportResolution Resolution => SelectedResolution.Value;

    /// <summary>The output's pixel size for the draft: the canvas or the chosen resolution (null without an output).</summary>
    public FrameSize? OutputSize => _output is null ? null : ExportResolutions.SizeFor(SelectedResolution.Value, _canvas);

    /// <summary>Raised after any change of the draft.</summary>
    public event EventHandler? Changed;

    /// <summary>The NVENC check finished after the dialog opened: the encoder list follows it, the choice is kept.</summary>
    public void SetAvailability(ExportEncoderAvailability available)
    {
        ArgumentNullException.ThrowIfNull(available);
        if (available == _available) return;
        _available = available;
        Sync(() =>
        {
            var current = SelectedEncoder.Value;
            Encoders = BuildEncoders(available, SelectedCodec.Value, current);
            SelectedEncoder = Encoders.Single(o => o.Value == current);
        });
        Refresh();
    }

    // --- The preset list ---------------------------------------------------------------------------------------------------

    private IReadOnlyList<SavedExportPreset> Saved => _userSettings?.Current.ExportPresets ?? Array.Empty<SavedExportPreset>();

    private void RebuildPresets()
    {
        Sync(() =>
        {
            Presets.Clear();
            foreach (var kind in ExportPresets.All)
                Presets.Add(new ExportPresetChoice(ExportPresetChoiceKind.BuiltIn, PresetName(kind), PresetDetail(kind), ExportPresets.Of(kind), kind));
            foreach (var saved in Saved)
                Presets.Add(new ExportPresetChoice(ExportPresetChoiceKind.Saved, saved.Name, Describe(saved.Settings, _canvas), saved.Settings));
            Presets.Add(new ExportPresetChoice(ExportPresetChoiceKind.Custom, Strings.ExportPreset_Custom, Strings.ExportPreset_CustomDetail));
            if (CanSavePresets) Presets.Add(new ExportPresetChoice(ExportPresetChoiceKind.SaveNew, Strings.ExportPreset_SaveNew, ""));
            SelectedPreset = Matching();
        });
        _lastPreset = SelectedPreset;
        IsSavedPreset = SelectedPreset?.Kind == ExportPresetChoiceKind.Saved;
    }

    /// <summary>The item the draft is: one of the user's saved presets (its file type included — the user's own name first, so it
    /// can be chosen and deleted even when it equals a built-in one), else a built-in preset, else «Пользовательский».</summary>
    private ExportPresetChoice Matching()
    {
        var value = Value;
        return Presets.FirstOrDefault(p => p.Kind == ExportPresetChoiceKind.Saved && ExportPresets.Matches(value, p.Settings!, _canvas, compareContainer: true))
            ?? Presets.FirstOrDefault(p => p.Kind == ExportPresetChoiceKind.BuiltIn && ExportPresets.Matches(value, p.Settings!, _canvas))
            ?? Presets.First(p => p.Kind == ExportPresetChoiceKind.Custom);
    }

    partial void OnSelectedPresetChanged(ExportPresetChoice? value)
    {
        if (_syncing || value is null) return;
        switch (value.Kind)
        {
            case ExportPresetChoiceKind.SaveNew:
                NewPresetName = SuggestedName(Value, _canvas, Saved);
                IsSavingPreset = true;
                Sync(() => SelectedPreset = _lastPreset);
                return;
            case ExportPresetChoiceKind.Custom:
                _lastPreset = value;
                IsSavedPreset = false;
                return;
            case ExportPresetChoiceKind.BuiltIn:
                Load(ExportPresets.Apply(value.Settings!, _canvas, SelectedContainer.Value));
                break;
            case ExportPresetChoiceKind.Saved:
                Load(ExportPresets.Apply(value.Settings!, _canvas));
                break;
        }
        Refresh();
    }

    /// <summary>Shows <paramref name="e"/> in every field.</summary>
    private void Load(ExportEncoding e) => Sync(() =>
    {
        SelectedCodec = CodecOptions.Single(o => o.Value == e.Codec);
        Containers = BuildContainers(e.Codec);
        SelectedContainer = Containers.FirstOrDefault(o => o.Value == e.Container) ?? Containers[0];
        Encoders = BuildEncoders(_available, e.Codec, e.Encoder);
        SelectedEncoder = Encoders.Single(o => o.Value == e.Encoder);
        SelectedResolution = Resolutions.FirstOrDefault(r => r.Value == e.Resolution) ?? Resolutions[0];
        SelectedQuality = Qualities.Single(o => o.Value == e.Quality);
        SelectedSpeed = Speeds.Single(o => o.Value == e.Preset);
        SelectedBitrate = Bitrates.Single(o => o.Value == e.AudioBitrateKbps);
        SelectedRateControl = RateControls.Single(o => o.Value == e.RateControl);
        SelectedVideoBitrate = VideoBitrates.Single(o => o.Value == e.VideoBitrateMbps);
    });

    partial void OnSelectedCodecChanged(ExportChoice<ExportVideoCodec> value)
    {
        if (_syncing) return;
        Sync(() =>
        {
            var container = SelectedContainer.Value;
            Containers = BuildContainers(value.Value);
            SelectedContainer = Containers.FirstOrDefault(o => o.Value == container) ?? Containers[0];   // AV1 out of MOV: MP4
            var encoder = SelectedEncoder.Value;
            Encoders = BuildEncoders(_available, value.Value, encoder);
            SelectedEncoder = Encoders.Single(o => o.Value == encoder);
        });
        FieldChanged();
    }

    partial void OnSelectedContainerChanged(ExportChoice<ExportContainer> value) => FieldChanged();
    partial void OnSelectedEncoderChanged(ExportChoice<ExportVideoEncoder> value) => FieldChanged();
    partial void OnSelectedResolutionChanged(ExportChoice<ExportResolution> value) => FieldChanged();
    partial void OnSelectedBitrateChanged(ExportChoice<int> value) => FieldChanged();
    partial void OnSelectedQualityChanged(ExportChoice<ExportQuality> value) => FieldChanged();
    partial void OnSelectedSpeedChanged(ExportChoice<ExportSpeedPreset> value) => FieldChanged();
    partial void OnSelectedRateControlChanged(ExportChoice<ExportRateControl> value) => FieldChanged();
    partial void OnSelectedVideoBitrateChanged(ExportChoice<int> value) => FieldChanged();

    /// <summary>A field changed by hand: the preset becomes the one the settings now are, or «Пользовательский».</summary>
    private void FieldChanged()
    {
        if (_syncing) return;
        Refresh();
    }

    private void Sync(Action change)
    {
        var was = _syncing;
        _syncing = true;
        try { change(); }
        finally { _syncing = was; }
    }

    // --- Saving and deleting the user's presets -----------------------------------------------------------------------------

    private bool CanSavePreset() => CanSavePresets && !string.IsNullOrWhiteSpace(NewPresetName);

    /// <summary>Keeps the shown settings under <see cref="NewPresetName"/> (a name taken is replaced), offered in every project.</summary>
    [RelayCommand(CanExecute = nameof(CanSavePreset))]
    private void SavePreset()
    {
        if (_userSettings is null || string.IsNullOrWhiteSpace(NewPresetName)) return;
        var name = NewPresetName.Trim();
        var settings = Value;
        _userSettings.Update(s =>
        {
            var list = s.ExportPresets.ToList();
            var index = list.FindIndex(p => string.Equals(p.Name, name, StringComparison.CurrentCultureIgnoreCase));
            if (index >= 0) list[index] = new SavedExportPreset(name, settings);
            else list.Add(new SavedExportPreset(name, settings));
            return s with { ExportPresets = list };
        });
        IsSavingPreset = false;
        RebuildPresets();
        // The rebuilt list shows the settings under the user's new name (a saved preset comes before the built-in ones).
    }

    [RelayCommand]
    private void CancelSavePreset() => IsSavingPreset = false;

    /// <summary>✕: deletes the chosen saved preset; the settings stay (now «Пользовательский» unless they are another preset).</summary>
    [RelayCommand]
    private void DeletePreset()
    {
        if (_userSettings is null || SelectedPreset is not { Kind: ExportPresetChoiceKind.Saved } chosen) return;
        _userSettings.Update(s => s with { ExportPresets = s.ExportPresets.Where(p => p.Name != chosen.Label).ToList() });
        RebuildPresets();
    }

    private void OnUserSettingsChanged(object? sender, EventArgs e)
    {
        var shown = Presets.Where(p => p.Kind == ExportPresetChoiceKind.Saved).Select(p => (p.Label, p.Settings)).ToList();
        if (!shown.SequenceEqual(Saved.Select(p => (p.Name, (ExportEncoding?)p.Settings)))) RebuildPresets();
    }

    /// <summary>The suggested name of a new preset (Q5 A): the settings in short, unique among the saved ones —
    /// «HEVC · 4K · AAC 320», «HEVC · 4K · AAC 320 (2)».</summary>
    public static string SuggestedName(ExportEncoding e, FrameSize canvas, IReadOnlyList<SavedExportPreset> saved)
    {
        var name = Describe(e, canvas);
        var candidate = name;
        for (var n = 2; saved.Any(p => string.Equals(p.Name, candidate, StringComparison.CurrentCultureIgnoreCase)); n++) candidate = $"{name} ({n})";
        return candidate;
    }

    /// <summary>«HEVC · 4K · MP4 · AAC 320» — a preset's settings in short.</summary>
    public static string Describe(ExportEncoding e, FrameSize canvas)
    {
        var applied = ExportResolutions.Ceiling(e, canvas);
        var size = applied.Resolution == ExportResolution.Project && e.Resolution == ExportResolution.Project
            ? Strings.ExportResolution_Project : ResolutionName(e.Resolution);
        var codec = CodecOptions.Single(o => o.Value == e.Codec).Label.Split(' ')[0];
        return $"{codec} · {size} · {e.Container.ToString().ToUpperInvariant()} · AAC {e.AudioBitrateKbps}";
    }

    // --- The notes ---------------------------------------------------------------------------------------------------------

    private void Refresh()
    {
        IsBitrate = SelectedRateControl.Value == ExportRateControl.Bitrate;
        if (!_syncing && Presets.Count > 0)
        {
            Sync(() => SelectedPreset = Matching());
            _lastPreset = SelectedPreset;
            IsSavedPreset = SelectedPreset?.Kind == ExportPresetChoiceKind.Saved;
        }
        var value = Value;
        var size = ExportResolutions.SizeFor(value.Resolution, _canvas);
        var plan = ExportVideoPlan.For(value, size, _available);
        PlanText = string.Format(Strings.ExportPlan, plan.Description);
        SpeedNote = plan.Encoder == ExportVideoEncoder.Nvenc ? Strings.ExportSpeed_Nvenc
            : plan.Codec != ExportVideoCodec.H264 ? Strings.ExportSpeed_SoftwareHevcAv1
            : value.Preset is ExportSpeedPreset.VeryFast or ExportSpeedPreset.Fast ? Strings.ExportSpeed_X264Fast
            : value.Preset == ExportSpeedPreset.Slow ? Strings.ExportSpeed_X264Slow
            : Strings.ExportSpeed_X264;
        CodecNote = value.Codec switch
        {
            ExportVideoCodec.Hevc => Strings.ExportCodec_HevcNote,
            ExportVideoCodec.Av1 => Strings.ExportCodec_Av1Note,
            _ => Strings.ExportCodec_H264Note
        };
        CanEncode = !plan.Unavailable;
        UnavailableNote = plan.Unavailable
            ? string.Format(Strings.ExportCodec_Unavailable, SelectedCodec.Label, SoftwareName(value.Codec))
            : !plan.FellBack ? null
            : string.Format(Strings.ExportEncoder_Unavailable, _available.NvencFor(value.Codec)
                ? string.Format(Strings.ExportEncoder_TooLarge, ExportEncoderAvailability.NvencMaxSideOf(value.Codec))
                : _available.NvencUnavailableReason ?? Strings.ExportEncoder_NotFound, SoftwareName(value.Codec));
        if (_output is { FrameCount: > 0 } output)
        {
            var estimate = ExportSizeEstimate.For(plan, output with { Size = size }, value.AudioBitrateKbps);
            EstimateText = estimate.FromBitrate
                ? string.Format(Strings.ExportEstimate_Bitrate, Size((estimate.LowBytes + estimate.HighBytes) / 2))
                : string.Format(Strings.ExportEstimate, Size(estimate.LowBytes), Size(estimate.HighBytes));
        }
        OnPropertyChanged(nameof(OutputSize));
        OnPropertyChanged(nameof(Resolution));
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

    /// <summary>Stops following the global settings (the dialog closed).</summary>
    public void Detach()
    {
        if (_userSettings is not null) _userSettings.Changed -= OnUserSettingsChanged;
    }
}
