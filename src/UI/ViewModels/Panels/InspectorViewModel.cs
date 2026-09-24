using System.Collections.ObjectModel;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.UI.Common;
using AiVideoEditor.UI.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AiVideoEditor.UI.ViewModels.Panels;

/// <summary>What the Inspector is currently showing. Kept as an explicit enum
/// (rather than e.g. two nullable "selected thing" properties) so the view can
/// switch sections with simple IsVisible bindings and so a future third kind of
/// selection doesn't require restructuring.</summary>
public enum InspectorSelectionKind
{
    None,
    Media,
    TimelineClip
}

/// <summary>
/// Right-hand Inspector. Phase 2 wired up real Media Browser selection; Phase 3
/// adds real technical metadata (Duration/Resolution/Codec/etc.), populated only
/// once analysis actually completes — never fake values. Phase 4 adds timeline clip
/// selection: read-only clip timing as non-drop-frame timecode. Phase 7 makes clip
/// properties editable: audio (volume, mute), transform, opacity and crop.
/// <para>
/// Editing: the fields show the model (<see cref="ShowClip"/>, called again after every timeline
/// change, undo and redo included). A value the user changes is sent to
/// <see cref="ITimelineEditService.SetClipProperties"/> — the model is never written here — and
/// the resulting refresh writes the fields back from the model while <c>_syncing</c> is set, so
/// showing a value never produces another edit (no Inspector ↔ TimelineChanged loop).
/// </para>
/// </summary>
public sealed partial class InspectorViewModel : ViewModelBase
{
    private readonly ITimelineEditService _edit;
    private readonly StatusService _status;
    private readonly IReadOnlyList<string> _systemFonts;

    /// <summary>The timeline clip shown, if any (the primary selection).</summary>
    private Clip? _clip;

    /// <summary>True while fields are being filled from the model.</summary>
    private bool _syncing;

    /// <param name="fonts">Installed font families for the text font list; without it the list
    /// holds only the shown clip's font.</param>
    public InspectorViewModel(ITimelineEditService edit, StatusService status, IFontCatalog? fonts = null)
    {
        _edit = edit;
        _status = status;
        _systemFonts = fonts?.FamilyNames ?? Array.Empty<string>();
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsMediaSelected))]
    [NotifyPropertyChangedFor(nameof(IsTimelineClipSelected))]
    [NotifyPropertyChangedFor(nameof(IsNothingSelected))]
    private InspectorSelectionKind _selectionKind = InspectorSelectionKind.None;

    public bool IsMediaSelected => SelectionKind == InspectorSelectionKind.Media;
    public bool IsTimelineClipSelected => SelectionKind == InspectorSelectionKind.TimelineClip;
    public bool IsNothingSelected => SelectionKind == InspectorSelectionKind.None;

    // --- Media selection: basic info (Phase 2) ------------------------------
    [ObservableProperty] private string _mediaFileName = "";
    [ObservableProperty] private string _mediaTypeLabel = "";
    [ObservableProperty] private string _mediaFormatLabel = "";
    [ObservableProperty] private string _mediaFileSizeDisplay = "";
    [ObservableProperty] private string _mediaFilePath = "";

    // --- Media selection: technical info (Phase 3) --------------------------
    [ObservableProperty] private bool _isAnalyzing;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAnalysisError))]
    private string? _analysisErrorMessage;

    public bool HasAnalysisError => !string.IsNullOrEmpty(AnalysisErrorMessage);

    public ObservableCollection<TechnicalInfoRow> TechnicalRows { get; } = new();

    public bool HasTechnicalInfo => TechnicalRows.Count > 0;

    // --- Timeline clip selection (Phase 4) -----------------------------------
    [ObservableProperty] private string _clipName = "";
    [ObservableProperty] private string _clipTypeLabel = "";
    [ObservableProperty] private string _startTimeDisplay = "";
    [ObservableProperty] private string _endTimeDisplay = "";
    [ObservableProperty] private string _durationDisplay = "";
    [ObservableProperty] private string _clipFrameRateDisplay = "";

    // --- Audio (Phase 7): video clips with sound and audio clips -----------------
    [ObservableProperty] private bool _hasAudioProperties;

    /// <summary>Volume in percent (0–200, linear). Independent of <see cref="IsMuted"/>.</summary>
    [ObservableProperty] private decimal? _volumePercent = 100;

    [ObservableProperty] private bool _isMuted;

    partial void OnVolumePercentChanged(decimal? value)
    {
        if (!_syncing && value is { } volume) EditAudio(a => a with { Volume = (double)(volume / 100m) });
    }

    partial void OnIsMutedChanged(bool value)
    {
        if (!_syncing) EditAudio(a => a with { IsMuted = value });
    }

    private void EditAudio(Func<AudioProperties, AudioProperties> change)
    {
        if (_clip is null || AudioProperties.Of(_clip) is not { } current) return;

        var result = _edit.SetClipProperties(_clip.Id, new ClipPropertyChange { Audio = change(current) });
        if (!result.Success)
        {
            _status.Report(result.Message ?? "The clip could not be changed.");
            SyncFromModel(); // show what the clip really has
        }
    }

    // --- Transform, opacity, crop (Phase 7): video, image and text clips -----------
    // Values are shown in UI units (canvas pixels, degrees, percent) and sent to
    // SetClipProperties one field at a time, so consecutive changes of one field merge into
    // one undo step (D017). The rest of the group is taken from the model at that moment.
    // A numeric field (volume included) is null while it is empty: never an edit; it shows the
    // model again when it loses focus (ShowModelValues). Text rules: NumericInput.

    [ObservableProperty] private bool _hasVisualProperties;

    /// <summary>Crop applies to pictures only (video, image), not to text.</summary>
    [ObservableProperty] private bool _hasCrop;

    /// <summary>Centre offset from the canvas centre, canvas pixels, +X right (D018).</summary>
    [ObservableProperty] private decimal? _positionX;

    /// <summary>Centre offset from the canvas centre, canvas pixels, +Y down (D018).</summary>
    [ObservableProperty] private decimal? _positionY;

    /// <summary>Uniform scale after fitting, in percent (100 = fitted).</summary>
    [ObservableProperty] private decimal? _scalePercent = 100;

    /// <summary>Degrees, clockwise.</summary>
    [ObservableProperty] private decimal? _rotation;

    [ObservableProperty] private decimal? _opacityPercent = 100;

    [ObservableProperty] private decimal? _cropLeftPercent;
    [ObservableProperty] private decimal? _cropTopPercent;
    [ObservableProperty] private decimal? _cropRightPercent;
    [ObservableProperty] private decimal? _cropBottomPercent;

    // Input limits for the view (ClipPropertyLimits in UI units).
    public decimal MaxPosition => (decimal)ClipPropertyLimits.MaxPositionMagnitude;
    public decimal MinPosition => -(decimal)ClipPropertyLimits.MaxPositionMagnitude;
    public decimal MinScalePercent => (decimal)ClipPropertyLimits.MinScale * 100;
    public decimal MaxScalePercent => (decimal)ClipPropertyLimits.MaxScale * 100;
    public decimal MinRotation => (decimal)ClipPropertyLimits.MinRotationDegrees;
    public decimal MaxRotation => (decimal)ClipPropertyLimits.MaxRotationDegrees;
    public decimal MinOpacityPercent => (decimal)ClipPropertyLimits.MinOpacity * 100;
    public decimal MaxOpacityPercent => (decimal)ClipPropertyLimits.MaxOpacity * 100;

    /// <summary>Each crop edge is below 100 %; opposite edges together must stay below 100 %
    /// too — that is checked by the edit service, which rejects the change and the field shows the
    /// model value again.</summary>
    public decimal MaxCropPercent => 99.9m;
    public decimal MinCropPercent => 0;

    partial void OnPositionXChanged(decimal? value) => EditVisual(value, (v, x) => v with { PositionX = (double)x });
    partial void OnPositionYChanged(decimal? value) => EditVisual(value, (v, x) => v with { PositionY = (double)x });
    partial void OnScalePercentChanged(decimal? value) => EditVisual(value, (v, x) => v with { Scale = (double)(x / 100m) });
    partial void OnRotationChanged(decimal? value) => EditVisual(value, (v, x) => v with { RotationDegrees = (double)x });
    partial void OnOpacityPercentChanged(decimal? value) => EditVisual(value, (v, x) => v with { Opacity = (double)(x / 100m) });
    partial void OnCropLeftPercentChanged(decimal? value) => EditVisual(value, (v, x) => v with { Crop = v.Crop with { Left = (double)(x / 100m) } });
    partial void OnCropTopPercentChanged(decimal? value) => EditVisual(value, (v, x) => v with { Crop = v.Crop with { Top = (double)(x / 100m) } });
    partial void OnCropRightPercentChanged(decimal? value) => EditVisual(value, (v, x) => v with { Crop = v.Crop with { Right = (double)(x / 100m) } });
    partial void OnCropBottomPercentChanged(decimal? value) => EditVisual(value, (v, x) => v with { Crop = v.Crop with { Bottom = (double)(x / 100m) } });

    private void EditVisual(decimal? value, Func<VisualProperties, decimal, VisualProperties> change)
    {
        if (_syncing || value is not { } x || _clip is null || VisualProperties.Of(_clip) is not { } current) return;

        var result = _edit.SetClipProperties(_clip.Id, new ClipPropertyChange { Visual = change(current, x) });
        if (!result.Success)
        {
            _status.Report(result.Message ?? "The clip could not be changed.");
            SyncFromModel(); // show what the clip really has
        }
    }

    // --- Text (Phase 7 Step 8): text clips ---------------------------------------------------
    // Same pattern as the visual fields: each field is sent to SetClipProperties on its own and live,
    // consecutive changes of one field merge into one undo step (D017), the fields are filled from the
    // model under the sync guard. Empty or whitespace text is a valid value (it just draws nothing).

    [ObservableProperty] private bool _hasTextProperties;

    /// <summary>The clip's text; may span several lines.</summary>
    [ObservableProperty] private string _textContent = "";

    /// <summary>Installed font families, plus the clip's font when it isn't installed here.</summary>
    [ObservableProperty] private IReadOnlyList<string> _fontFamilies = Array.Empty<string>();

    /// <summary>Null while the font list has no selection (e.g. while it is being replaced).</summary>
    [ObservableProperty] private string? _fontFamilyName;

    /// <summary>Font size in canvas pixels; null while the field is empty.</summary>
    [ObservableProperty] private decimal? _fontSize;

    /// <summary>What the color field holds. Only a complete <c>#RRGGBB</c> is applied, so typing
    /// never gets reverted halfway; other text shows the model again when the field loses focus.</summary>
    [ObservableProperty] private string _textColorHex = "";

    /// <summary>The clip's color as stored (the swatch next to the field).</summary>
    [ObservableProperty] private string _textColorSwatch = "#FFFFFF";

    [ObservableProperty] private TextAlignment _alignment = TextAlignment.Center;

    public IReadOnlyList<TextAlignment> Alignments { get; } = Enum.GetValues<TextAlignment>();

    public decimal MinFontSize => (decimal)ClipPropertyLimits.MinFontSize;
    public decimal MaxFontSize => (decimal)ClipPropertyLimits.MaxFontSize;

    partial void OnTextContentChanged(string value) => EditText(t => t with { Text = value });

    partial void OnFontFamilyNameChanged(string? value)
    {
        if (!string.IsNullOrEmpty(value)) EditText(t => t with { FontFamily = value });
    }

    partial void OnFontSizeChanged(decimal? value)
    {
        if (value is { } size) EditText(t => t with { FontSize = (double)size });
    }

    partial void OnTextColorHexChanged(string value)
    {
        if (ClipPropertyValidator.IsHexColor(value)) EditText(t => t with { ColorHex = value });
    }

    partial void OnAlignmentChanged(TextAlignment value) => EditText(t => t with { Alignment = value });

    private void EditText(Func<TextProperties, TextProperties> change)
    {
        if (_syncing || _clip is null || TextProperties.Of(_clip) is not { } current) return;

        var result = _edit.SetClipProperties(_clip.Id, new ClipPropertyChange { Text = change(current) });
        if (!result.Success)
        {
            _status.Report(result.Message ?? "The clip could not be changed.");
            SyncFromModel(); // show what the clip really has
        }
    }

    /// <summary>The installed fonts, with <paramref name="current"/> first when it isn't one of them
    /// (a project from another machine), so the list can always show the clip's font.</summary>
    private IReadOnlyList<string> FontListFor(string current) =>
        _systemFonts.Contains(current, StringComparer.OrdinalIgnoreCase)
            ? _systemFonts
            : new[] { current }.Concat(_systemFonts).ToList();

    /// <summary>Shows the primary selected timeline clip. Called by MainWindowViewModel
    /// on TimelineViewModel.SelectionChanged — which also fires after every timeline
    /// change, so the timing shown here follows moves/trims/undo.</summary>
    public void ShowClip(TimelineClipSelection selection)
    {
        var clip = selection.Clip;
        _clip = clip;
        // A video file without an audio stream has nothing to mix; unknown metadata still shows it.
        HasAudioProperties = clip is AudioClip || (clip is VideoClip && selection.Asset?.Metadata is not { AudioCodec: null });
        HasVisualProperties = VisualProperties.Of(clip) is not null;
        HasCrop = clip is VideoClip or ImageClip;
        HasTextProperties = clip is TextClip;
        SyncFromModel();

        ClipName = selection.Name;
        ClipTypeLabel = clip switch
        {
            VideoClip => "Video clip",
            AudioClip => "Audio clip",
            ImageClip => "Image clip",
            TextClip => "Text clip",
            _ => "Clip"
        };
        StartTimeDisplay = TimeFormat.ToTimecode(clip.TimelineStart, selection.Rate);
        EndTimeDisplay = TimeFormat.ToTimecode(clip.TimelineEnd, selection.Rate);
        DurationDisplay = TimeFormat.ToTimecode(clip.Duration, selection.Rate);
        ClipFrameRateDisplay = selection.Rate.Denominator == 1
            ? $"{selection.Rate.Numerator} FPS"
            : $"{Math.Round(selection.Rate.ToDouble(), 3)} FPS ({selection.Rate})";

        TechnicalRows.Clear();
        SelectionKind = InspectorSelectionKind.TimelineClip;
        OnPropertyChanged(nameof(HasTechnicalInfo));
    }

    /// <summary>Shows a Media Browser item's real properties, including whatever
    /// technical metadata analysis has produced so far. Called by
    /// MainWindowViewModel in response to MediaBrowserViewModel.SelectionChanged,
    /// and again whenever that same asset's analysis completes (the Media Browser
    /// reload re-fires selection for the still-selected item).</summary>
    public void ShowMedia(MediaAsset asset)
    {
        ForgetClip();
        MediaFileName = asset.FileName;
        MediaTypeLabel = asset.Kind switch
        {
            MediaKind.Video => "Video",
            MediaKind.Audio => "Audio",
            MediaKind.Image => "Image",
            _ => "Unknown"
        };
        MediaFormatLabel = asset.FileExtension.TrimStart('.').ToUpperInvariant();
        MediaFileSizeDisplay = FileSizeFormat.ToShortString(asset.FileSizeBytes);
        MediaFilePath = asset.FilePath;

        IsAnalyzing = asset.AnalysisStatus is MediaAnalysisStatus.Pending or MediaAnalysisStatus.Analyzing;
        AnalysisErrorMessage = asset.AnalysisStatus == MediaAnalysisStatus.Failed ? asset.AnalysisError : null;

        TechnicalRows.Clear();
        if (asset.AnalysisStatus == MediaAnalysisStatus.Completed && asset.Metadata is { } metadata)
            BuildTechnicalRows(asset.Kind, metadata);

        SelectionKind = InspectorSelectionKind.Media;
        OnPropertyChanged(nameof(HasTechnicalInfo));
    }

    public void ClearSelection()
    {
        ForgetClip();
        SelectionKind = InspectorSelectionKind.None;
        TechnicalRows.Clear();
        IsAnalyzing = false;
        AnalysisErrorMessage = null;
        OnPropertyChanged(nameof(HasTechnicalInfo));
    }

    /// <summary>Fills the property fields from the shown clip without producing edits.</summary>
    private void SyncFromModel()
    {
        if (_clip is null) return;
        _syncing = true;
        try
        {
            if (AudioProperties.Of(_clip) is { } audio)
            {
                VolumePercent = (decimal)audio.Volume * 100m;
                IsMuted = audio.IsMuted;
            }
            if (VisualProperties.Of(_clip) is { } visual)
            {
                PositionX = (decimal)visual.PositionX;
                PositionY = (decimal)visual.PositionY;
                ScalePercent = (decimal)visual.Scale * 100m;
                Rotation = (decimal)visual.RotationDegrees;
                OpacityPercent = (decimal)visual.Opacity * 100m;
                CropLeftPercent = (decimal)visual.Crop.Left * 100m;
                CropTopPercent = (decimal)visual.Crop.Top * 100m;
                CropRightPercent = (decimal)visual.Crop.Right * 100m;
                CropBottomPercent = (decimal)visual.Crop.Bottom * 100m;
            }
            if (TextProperties.Of(_clip) is { } text)
            {
                TextContent = text.Text;
                var fonts = FontListFor(text.FontFamily);
                if (!fonts.SequenceEqual(FontFamilies)) FontFamilies = fonts; // may clear the selection first
                // Exactly the listed spelling, so the list selects it.
                FontFamilyName = fonts.First(f => string.Equals(f, text.FontFamily, StringComparison.OrdinalIgnoreCase));
                FontSize = (decimal)text.FontSize;
                TextColorHex = text.ColorHex;
                TextColorSwatch = text.ColorHex;
                Alignment = text.Alignment;
            }
        }
        finally
        {
            _syncing = false;
        }
    }

    /// <summary>Shows the model's values in the fields again. Called when a field loses focus: an
    /// empty numeric field (null here) or an incomplete color was never an edit; fields that already
    /// show the model don't change.</summary>
    public void ShowModelValues() => SyncFromModel();

    private void ForgetClip()
    {
        _clip = null;
        HasAudioProperties = false;
        HasVisualProperties = false;
        HasCrop = false;
        HasTextProperties = false;
    }

    private void BuildTechnicalRows(MediaKind kind, MediaMetadata m)
    {
        switch (kind)
        {
            case MediaKind.Video:
                AddRow("Duration", m.Duration.Ticks > 0 ? TimeFormat.ToShortString(m.Duration) : null);
                AddRow("Resolution", ResolutionFormat.Display(m));
                AddRow("Orientation", ResolutionFormat.Orientation(m));
                AddRow("Frame Rate", m.FrameRate.HasValue ? $"{Math.Round(m.FrameRate.Value.ToDouble(), 2)} FPS" : null);
                AddRow("Video Codec", m.VideoCodec?.ToUpperInvariant());
                AddRow("Audio Codec", m.AudioCodec?.ToUpperInvariant());
                AddRow("Bitrate", m.BitrateBps.HasValue ? FormatBitrate(m.BitrateBps.Value) : null);
                break;

            case MediaKind.Audio:
                AddRow("Duration", m.Duration.Ticks > 0 ? TimeFormat.ToShortString(m.Duration) : null);
                AddRow("Audio Codec", m.AudioCodec?.ToUpperInvariant());
                AddRow("Sample Rate", m.AudioSampleRate.HasValue ? $"{m.AudioSampleRate / 1000} kHz" : null);
                AddRow("Channels", DescribeChannels(m.AudioChannels));
                AddRow("Bitrate", m.BitrateBps.HasValue ? FormatBitrate(m.BitrateBps.Value) : null);
                break;

            case MediaKind.Image:
                AddRow("Width", (m.DisplayWidth ?? m.Width) is { } w ? $"{w}px" : null);
                AddRow("Height", (m.DisplayHeight ?? m.Height) is { } h ? $"{h}px" : null);
                AddRow("Orientation", ResolutionFormat.Orientation(m));
                break;
        }
    }

    private void AddRow(string label, string? value)
    {
        if (!string.IsNullOrEmpty(value))
            TechnicalRows.Add(new TechnicalInfoRow { Label = label, Value = value });
    }

    private static string FormatBitrate(long bitsPerSecond)
    {
        var kbps = bitsPerSecond / 1000.0;
        return kbps >= 1000 ? $"{kbps / 1000:0.#} Mbps" : $"{kbps:0} kbps";
    }

    private static string? DescribeChannels(int? channels) => channels switch
    {
        1 => "Mono",
        2 => "Stereo",
        > 2 => $"{channels} channels",
        _ => null
    };
}
