using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Core.Localization;
using AiVideoEditor.UI.Common;
using AiVideoEditor.UI.Localization;

namespace AiVideoEditor.UI.ViewModels.Panels;

/// <summary>
/// A single Media Browser entry. Wraps a real <see cref="MediaAsset"/> — display
/// properties are all derived from it, nothing is duplicated, so there is exactly
/// one source of truth for a media item's data (per the Phase 2 rule against
/// duplicate models).
/// </summary>
public sealed class MediaBrowserItemViewModel : ViewModelBase
{
    public MediaAsset Asset { get; }

    public MediaBrowserItemViewModel(MediaAsset asset)
    {
        Asset = asset;
    }

    public string FileName => Asset.FileName;

    public string FormatLabel => Asset.FileExtension.TrimStart('.').ToUpperInvariant();

    public string KindLabel => MediaKindText.Badge(Asset.Kind);

    public string FileSizeDisplay => FileSizeFormat.ToShortString(Asset.FileSizeBytes);

    /// <summary>e.g. "MP4 · 842 MB" — matches the Media Browser row format from the spec.</summary>
    public string FormatAndSizeDisplay => $"{FormatLabel} · {FileSizeDisplay}";

    /// <summary>
    /// Compact technical line for the list row — e.g. "1:24 · 1920×1080 · 60 FPS"
    /// for video, "3:12 · 48 kHz · Stereo" for audio, "1920×1080" for images.
    /// Null (row hidden) while analysis hasn't produced anything to show yet, so
    /// the list never displays a made-up value. "Media offline" when the file was missing
    /// when the project was opened.
    /// </summary>
    public string? TechnicalSummary => Asset.IsMissing ? Strings.Analysis_Offline : Asset.AnalysisStatus switch
    {
        MediaAnalysisStatus.Analyzing => Strings.Analysis_Analyzing,
        MediaAnalysisStatus.Failed => Strings.Analysis_Unavailable,
        MediaAnalysisStatus.Completed => BuildTechnicalSummary(),
        // Pending: not analysed yet — a relink without ffprobe stays so until a later Open (D026 §3); an import is
        // Analyzing at once, and offline media says so above.
        _ => Strings.Analysis_Pending
    };

    public bool HasTechnicalSummary => TechnicalSummary is not null;

    private string? BuildTechnicalSummary()
    {
        var m = Asset.Metadata;
        if (m is null) return null;

        IEnumerable<string?> parts = Asset.Kind switch
        {
            MediaKind.Video => new[]
            {
                m.Duration.Ticks > 0 ? TimeFormat.ToShortString(m.Duration) : null,
                ResolutionFormat.Display(m),
                m.FrameRate.HasValue ? string.Format(Strings.Rate_Fps, Math.Round(m.FrameRate.Value.ToDouble())) : null
            },
            MediaKind.Audio => new[]
            {
                m.Duration.Ticks > 0 ? TimeFormat.ToShortString(m.Duration) : null,
                m.AudioSampleRate.HasValue ? string.Format(Strings.Unit_KHz, m.AudioSampleRate / 1000) : null,
                DescribeChannels(m.AudioChannels)
            },
            MediaKind.Image => new[]
            {
                ResolutionFormat.Display(m)
            },
            _ => Array.Empty<string?>()
        };

        var joined = string.Join(" · ", parts.Where(p => !string.IsNullOrEmpty(p)));
        return joined.Length > 0 ? joined : null;
    }

    private static string? DescribeChannels(int? channels) => channels switch
    {
        1 => Strings.Channels_Mono,
        2 => Strings.Channels_Stereo,
        > 2 => string.Format(Strings.Channels_Short, channels),
        _ => null
    };

    private Thumbnail? _thumbnail;

    /// <summary>The asset's thumbnail once it is ready (D024 Step 9.4, from <c>ThumbnailCoordinator</c>); null while there
    /// is none — audio, not analysed, offline without a cached one, still being made — and the tile shows its colour.</summary>
    public Thumbnail? Thumbnail
    {
        get => _thumbnail;
        set
        {
            if (SetProperty(ref _thumbnail, value))
                OnPropertyChanged(nameof(HasThumbnail));
        }
    }

    public bool HasThumbnail => Thumbnail is not null;

    /// <summary>Tile colour behind the thumbnail, and the placeholder when there is none.</summary>
    public string ThumbnailColorHex => Asset.Kind switch
    {
        MediaKind.Video => "#3A5A78",
        MediaKind.Audio => "#3A784F",
        MediaKind.Image => "#78703A",
        _ => "#444444"
    };
}
