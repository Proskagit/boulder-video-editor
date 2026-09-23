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

    /// <summary>
    /// Compact technical line for the list row — e.g. "1:24 · 1920×1080 · 60 FPS"
    /// for video, "3:12 · 48 kHz · Stereo" for audio, "1920×1080" for images.
    /// Null (row hidden) while analysis hasn't produced anything to show yet, so
    /// the list never displays a made-up value. "Media offline" when the file was missing
    /// when the project was opened.
    /// </summary>
    public string? TechnicalSummary => Asset.IsMissing ? "Media offline" : Asset.AnalysisStatus switch
    {
        MediaAnalysisStatus.Analyzing => "Analyzing…",
        MediaAnalysisStatus.Failed => "Metadata unavailable",
        MediaAnalysisStatus.Completed => BuildTechnicalSummary(),
        _ => null // Pending: nothing to show yet, and nothing has failed either.
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
                m.Width.HasValue && m.Height.HasValue ? $"{m.Width}×{m.Height}" : null,
                m.FrameRate.HasValue ? $"{Math.Round(m.FrameRate.Value.ToDouble())} FPS" : null
            },
            MediaKind.Audio => new[]
            {
                m.Duration.Ticks > 0 ? TimeFormat.ToShortString(m.Duration) : null,
                m.AudioSampleRate.HasValue ? $"{m.AudioSampleRate / 1000} kHz" : null,
                DescribeChannels(m.AudioChannels)
            },
            MediaKind.Image => new[]
            {
                m.Width.HasValue && m.Height.HasValue ? $"{m.Width}×{m.Height}" : null
            },
            _ => Array.Empty<string?>()
        };

        var joined = string.Join(" · ", parts.Where(p => !string.IsNullOrEmpty(p)));
        return joined.Length > 0 ? joined : null;
    }

    private static string? DescribeChannels(int? channels) => channels switch
    {
        1 => "Mono",
        2 => "Stereo",
        > 2 => $"{channels} ch",
        _ => null
    };

    /// <summary>Placeholder tile color until real thumbnail generation exists (Phase 9).</summary>
    public string ThumbnailColorHex => Asset.Kind switch
    {
        MediaKind.Video => "#3A5A78",
        MediaKind.Audio => "#3A784F",
        MediaKind.Image => "#78703A",
        _ => "#444444"
    };
}
