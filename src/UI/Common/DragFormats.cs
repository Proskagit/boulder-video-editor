namespace AiVideoEditor.UI.Common;

/// <summary>Custom drag-and-drop data formats used between panels.</summary>
public static class DragFormats
{
    /// <summary>A <c>MediaAsset.Id</c> as a string, dragged from the Media Browser to the timeline.</summary>
    public const string MediaAssetId = "application/x-aivideoeditor-media-asset-id";

    /// <summary>The owner's acceptance of 2026-11-10: every selected <c>MediaAsset.Id</c> of a group dragged from the Media
    /// Browser, comma-separated, in the list's order (the timeline orders them by their recording time).</summary>
    public const string MediaAssetIds = "application/x-aivideoeditor-media-asset-ids";

    public static IReadOnlyList<Guid> ParseIds(string? text) =>
        (text ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).Select(s => Guid.TryParse(s, out var id) ? id : Guid.Empty)
            .Where(id => id != Guid.Empty).ToList();
}
