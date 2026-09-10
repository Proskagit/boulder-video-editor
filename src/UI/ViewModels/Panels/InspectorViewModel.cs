using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.UI.Common;
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
/// Right-hand Inspector. Phase 2 wires up real Media Browser selection (file
/// name/type/format/size/path). Timeline clip selection — and the Transform
/// properties below — don't exist yet (no timeline editing until Phase 3), so
/// <see cref="SelectionKind"/> can only ever be <see cref="InspectorSelectionKind.None"/>
/// or <see cref="InspectorSelectionKind.Media"/> for now; the TimelineClip case and
/// the Transform/Clip fields are scaffolding the view already renders correctly
/// against, ready for a future ShowTimelineClip(...) method in Phase 3+.
/// </summary>
public sealed partial class InspectorViewModel : ViewModelBase
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsMediaSelected))]
    [NotifyPropertyChangedFor(nameof(IsTimelineClipSelected))]
    [NotifyPropertyChangedFor(nameof(IsNothingSelected))]
    private InspectorSelectionKind _selectionKind = InspectorSelectionKind.None;

    public bool IsMediaSelected => SelectionKind == InspectorSelectionKind.Media;
    public bool IsTimelineClipSelected => SelectionKind == InspectorSelectionKind.TimelineClip;
    public bool IsNothingSelected => SelectionKind == InspectorSelectionKind.None;

    // --- Media selection (Phase 2) ------------------------------------------
    [ObservableProperty] private string _mediaFileName = "";
    [ObservableProperty] private string _mediaTypeLabel = "";
    [ObservableProperty] private string _mediaFormatLabel = "";
    [ObservableProperty] private string _mediaFileSizeDisplay = "";
    [ObservableProperty] private string _mediaFilePath = "";

    // --- Timeline clip selection (Phase 3+ scaffold, not populated yet) ----
    [ObservableProperty] private decimal _positionX;
    [ObservableProperty] private decimal _positionY;
    [ObservableProperty] private decimal _scale = 1.0m;
    [ObservableProperty] private decimal _rotation;
    [ObservableProperty] private decimal _opacity = 1.0m;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StartTimeDisplay))]
    private MediaTime _startTime;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EndTimeDisplay))]
    private MediaTime _endTime;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DurationDisplay))]
    private MediaTime _duration;

    public string StartTimeDisplay => TimeFormat.ToShortString(StartTime);
    public string EndTimeDisplay => TimeFormat.ToShortString(EndTime);
    public string DurationDisplay => TimeFormat.ToShortString(Duration);

    /// <summary>Shows a Media Browser item's real properties. Called by
    /// MainWindowViewModel in response to MediaBrowserViewModel.SelectionChanged.</summary>
    public void ShowMedia(MediaAsset asset)
    {
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
        SelectionKind = InspectorSelectionKind.Media;
    }

    public void ClearSelection()
    {
        SelectionKind = InspectorSelectionKind.None;
    }
}
