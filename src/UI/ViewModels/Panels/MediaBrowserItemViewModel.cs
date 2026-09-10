using AiVideoEditor.Core.Entities;
using AiVideoEditor.UI.Common;

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

    public string KindLabel => Asset.Kind switch
    {
        MediaKind.Video => "VIDEO",
        MediaKind.Audio => "AUDIO",
        MediaKind.Image => "IMAGE",
        _ => "?"
    };

    public string FileSizeDisplay => FileSizeFormat.ToShortString(Asset.FileSizeBytes);

    /// <summary>e.g. "MP4 · 842 MB" — matches the Media Browser row format from the spec.</summary>
    public string FormatAndSizeDisplay => $"{FormatLabel} · {FileSizeDisplay}";

    /// <summary>Placeholder tile color until real thumbnail generation exists (Phase 2 stays FFmpeg-free).</summary>
    public string ThumbnailColorHex => Asset.Kind switch
    {
        MediaKind.Video => "#3A5A78",
        MediaKind.Audio => "#3A784F",
        MediaKind.Image => "#78703A",
        _ => "#444444"
    };
}
