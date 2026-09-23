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
/// properties editable (audio first; Transform is still hidden scaffolding).
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

    /// <summary>The timeline clip shown, if any (the primary selection).</summary>
    private Clip? _clip;

    /// <summary>True while fields are being filled from the model.</summary>
    private bool _syncing;

    public InspectorViewModel(ITimelineEditService edit, StatusService status)
    {
        _edit = edit;
        _status = status;
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
    [ObservableProperty] private decimal _volumePercent = 100;

    [ObservableProperty] private bool _isMuted;

    partial void OnVolumePercentChanged(decimal value)
    {
        if (!_syncing) EditAudio(a => a with { Volume = (double)(value / 100m) });
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

    // Transform scaffold (Phase 7 — hidden in the view until editable).
    [ObservableProperty] private decimal _positionX;
    [ObservableProperty] private decimal _positionY;
    [ObservableProperty] private decimal _scale = 1.0m;
    [ObservableProperty] private decimal _rotation;
    [ObservableProperty] private decimal _opacity = 1.0m;

    /// <summary>Shows the primary selected timeline clip. Called by MainWindowViewModel
    /// on TimelineViewModel.SelectionChanged — which also fires after every timeline
    /// change, so the timing shown here follows moves/trims/undo.</summary>
    public void ShowClip(TimelineClipSelection selection)
    {
        var clip = selection.Clip;
        _clip = clip;
        // A video file without an audio stream has nothing to mix; unknown metadata still shows it.
        HasAudioProperties = clip is AudioClip || (clip is VideoClip && selection.Asset?.Metadata is not { AudioCodec: null });
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
        }
        finally
        {
            _syncing = false;
        }
    }

    private void ForgetClip()
    {
        _clip = null;
        HasAudioProperties = false;
    }

    private void BuildTechnicalRows(MediaKind kind, MediaMetadata m)
    {
        switch (kind)
        {
            case MediaKind.Video:
                AddRow("Duration", m.Duration.Ticks > 0 ? TimeFormat.ToShortString(m.Duration) : null);
                AddRow("Resolution", m.Width.HasValue && m.Height.HasValue ? $"{m.Width}×{m.Height}" : null);
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
                AddRow("Width", m.Width.HasValue ? $"{m.Width}px" : null);
                AddRow("Height", m.Height.HasValue ? $"{m.Height}px" : null);
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
