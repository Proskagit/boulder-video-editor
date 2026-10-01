using AiVideoEditor.Core.Entities;

namespace AiVideoEditor.Core.Interfaces;

/// <summary>
/// Relink of missing media (D026 §3, PO-1…PO-3, PO-6, PO-9): an offline <see cref="MediaAsset"/> gets the file the user
/// picked. Two phases, so the user can confirm warnings in between: <see cref="CheckAsync"/> re-checks the media,
/// validates the file and probes it; <see cref="ApplyAsync"/> validates again what may have changed meanwhile and
/// applies the relink as one undoable step. The asset keeps its id; clips are never adapted.
/// </summary>
public interface IMediaRelinkService
{
    /// <summary>Re-checks the project's media (PO-5), then checks <paramref name="filePath"/> for the asset: the hard
    /// rejects of PO-2 / PO-6 / PO-9 and, with a probe, the warnings. Never changes the project. Called on the UI
    /// thread; the file system and the probe run off it.</summary>
    Task<RelinkCheck> CheckAsync(Guid assetId, string filePath, CancellationToken ct = default);

    /// <summary>Applies a check that had no rejection (its warnings confirmed by the user): validates again whether the
    /// asset is still offline, the file still there, the path still free and the clips still within the file, then
    /// executes the relink through the undo history (the project becomes dirty). Returns why not, otherwise.</summary>
    Task<RelinkResult> ApplyAsync(RelinkCheck check, CancellationToken ct = default);

    /// <summary>Raised (on the UI thread) when an analysis that completed later finds that a media file doesn't fit
    /// the clips that use it — e.g. relinked without ffprobe (PO-3) and probed at a later Open. Nothing is undone or
    /// adapted: the user may Undo the relink (product owner, 2026-10-01).</summary>
    event EventHandler<RelinkedMediaIncompatibleEventArgs>? RelinkedMediaFoundIncompatible;
}

/// <summary>Why a relink can't be applied (D026 §3).</summary>
public enum RelinkRejection
{
    /// <summary>The asset is not in the current project.</summary>
    AssetNotFound,
    /// <summary>The asset's file is there (PO-6: only offline media is relinked).</summary>
    NotOffline,
    /// <summary>The chosen file doesn't exist.</summary>
    FileNotFound,
    /// <summary>Another kind of media, or an unsupported file type.</summary>
    WrongMediaType,
    /// <summary>ffprobe is available but could not read the file as media.</summary>
    UnreadableMedia,
    /// <summary>The file is shorter than the source range a clip of the asset uses.</summary>
    TooShort,
    /// <summary>The path belongs to another media asset of the project (PO-9).</summary>
    PathInUse,
    /// <summary>The check was made for another project or is otherwise no longer applicable.</summary>
    Stale
}

/// <summary>A difference the user confirms before the relink (PO-2), or that it could not be checked (PO-3).</summary>
public enum RelinkWarningKind
{
    /// <summary>ffprobe is unavailable: nothing beyond existence and file type was checked.</summary>
    NotChecked,
    DisplaySize,
    FrameRate,
    NoAudio,
    Rotation,
    StartTime,
    VideoCodec,
    AudioCodec,
    SampleRate,
    Channels,
    /// <summary>The new file has too little media beyond a clip for one of its dissolves (D025 §4: held frames).</summary>
    DissolveHandles
}

public sealed record RelinkWarning(RelinkWarningKind Kind, string Message);

/// <summary>The result of <see cref="IMediaRelinkService.CheckAsync"/>.</summary>
public sealed class RelinkCheck
{
    public required Guid AssetId { get; init; }

    /// <summary>The chosen file's full path.</summary>
    public required string FilePath { get; init; }

    /// <summary>Null when the relink may be applied.</summary>
    public RelinkRejection? Rejection { get; init; }

    /// <summary>The rejection's reason for the user; empty otherwise.</summary>
    public string Message { get; init; } = "";

    /// <summary>To be confirmed by the user before <see cref="IMediaRelinkService.ApplyAsync"/>.</summary>
    public IReadOnlyList<RelinkWarning> Warnings { get; init; } = Array.Empty<RelinkWarning>();

    /// <summary>The probed metadata of the file; null when ffprobe is unavailable (or the check was rejected).</summary>
    public MediaMetadata? Metadata { get; init; }

    public long FileSizeBytes { get; init; }

    /// <summary>The project the check was made for (a check is applied only to it).</summary>
    public Guid ProjectId { get; init; }

    public bool CanApply => Rejection is null;
}

/// <summary>The result of <see cref="IMediaRelinkService.ApplyAsync"/>.</summary>
public sealed record RelinkResult(bool Applied, RelinkRejection? Rejection, string Message);

public sealed class RelinkedMediaIncompatibleEventArgs : EventArgs
{
    public required MediaAsset Asset { get; init; }
    public required string Message { get; init; }
}
