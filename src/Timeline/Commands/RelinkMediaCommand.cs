using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;

namespace AiVideoEditor.Timeline.Commands;

/// <summary>Everything about a media asset's file that a relink replaces (D026 §3) — absolute, so Undo restores the
/// asset exactly. <see cref="IsMissing"/> is runtime state; a re-check corrects it whenever the file system says
/// otherwise.</summary>
public readonly record struct MediaFileState(
    string FilePath, long FileSizeBytes, MediaMetadata? Metadata, MediaAnalysisStatus AnalysisStatus, string? AnalysisError,
    bool IsMissing)
{
    public static MediaFileState Capture(MediaAsset asset) =>
        new(asset.FilePath, asset.FileSizeBytes, asset.Metadata, asset.AnalysisStatus, asset.AnalysisError, asset.IsMissing);

    public void ApplyTo(MediaAsset asset)
    {
        asset.FilePath = FilePath;
        asset.FileSizeBytes = FileSizeBytes;
        asset.Metadata = Metadata;
        asset.AnalysisStatus = AnalysisStatus;
        asset.AnalysisError = AnalysisError;
        asset.IsMissing = IsMissing;
    }
}

/// <summary>One asset's relink: its file state before and after.</summary>
public sealed record MediaRelink(MediaAsset Asset, MediaFileState Before, MediaFileState After);

/// <summary>
/// Relinks one or more assets to other files (D026 §3, PO-1): the assets keep their ids, so every clip keeps referring
/// to them unchanged. Execute and Undo each tell the project (<paramref name="notify"/> — <c>NotifyMediaRelinked</c>),
/// so thumbnails, waveforms and playback follow the file the asset has now.
/// </summary>
public sealed class RelinkMediaCommand(IReadOnlyList<MediaRelink> relinks, Action<IReadOnlyList<MediaAsset>> notify,
    string description = "Relink Media") : IUndoableCommand
{
    public string Description { get; } = description;

    public IReadOnlyList<MediaRelink> Relinks { get; } = relinks;

    public void Execute()
    {
        foreach (var r in Relinks) r.After.ApplyTo(r.Asset);
        notify(Relinks.Select(r => r.Asset).ToList());
    }

    public void Undo()
    {
        foreach (var r in Relinks) r.Before.ApplyTo(r.Asset);
        notify(Relinks.Select(r => r.Asset).ToList());
    }
}
