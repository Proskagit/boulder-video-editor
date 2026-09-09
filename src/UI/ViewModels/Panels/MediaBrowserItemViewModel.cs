namespace AiVideoEditor.UI.ViewModels.Panels;

/// <summary>Media kind shown in the browser. Mirrors <see cref="Core.Entities.MediaKind"/>
/// but kept as its own small UI-facing enum so the view model doesn't need to pull in
/// the full Core entity just to render a placeholder tile.</summary>
public enum MediaBrowserItemKind
{
    Video,
    Audio,
    Image
}

/// <summary>
/// A single Media Browser entry. In Phase 1 these are hand-authored mock items;
/// once Phase 2 lands, <see cref="MediaBrowserViewModel"/> will populate this same
/// shape from <c>IMediaImportService</c> results instead.
/// </summary>
public sealed class MediaBrowserItemViewModel : ViewModelBase
{
    public required string FileName { get; init; }
    public required string DurationDisplay { get; init; }
    public required MediaBrowserItemKind Kind { get; init; }

    /// <summary>Placeholder thumbnail color until real thumbnail generation exists (Phase 2).</summary>
    public string ThumbnailColorHex => Kind switch
    {
        MediaBrowserItemKind.Video => "#3A5A78",
        MediaBrowserItemKind.Audio => "#3A784F",
        MediaBrowserItemKind.Image => "#78703A",
        _ => "#444444"
    };

    public string KindLabel => Kind switch
    {
        MediaBrowserItemKind.Video => "VIDEO",
        MediaBrowserItemKind.Audio => "AUDIO",
        MediaBrowserItemKind.Image => "IMAGE",
        _ => "?"
    };
}
