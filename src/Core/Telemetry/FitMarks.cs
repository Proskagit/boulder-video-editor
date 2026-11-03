using AiVideoEditor.Core.Common;

namespace AiVideoEditor.Core.Telemetry;

/// <summary>The kind of a FIT mark's own position (D051 §5.3).</summary>
public enum FitMarkPositionKind
{
    /// <summary>As proposed: follows <see cref="FitMark.AutoStartUtc"/> (<c>W_auto</c>); no source time is stored.</summary>
    Proposed,

    /// <summary>Put on the edge of its video's recording (D051 §5.5.1 step 3); not usable for the calibration until it is
    /// moved or linked (§5.8).</summary>
    Clamped,

    /// <summary>Moved by the user.</summary>
    Moved,
}

/// <summary>A mark's own position: its kind and, for <see cref="FitMarkPositionKind.Clamped"/> and
/// <see cref="FitMarkPositionKind.Moved"/>, the source time of its video (ticks from the file's start).</summary>
public readonly record struct FitMarkPosition(FitMarkPositionKind Kind, MediaTime? SourceTime)
{
    public static FitMarkPosition Proposed { get; } = new(FitMarkPositionKind.Proposed, null);

    public static FitMarkPosition Clamped(MediaTime sourceTime) => new(FitMarkPositionKind.Clamped, sourceTime);

    public static FitMarkPosition Moved(MediaTime sourceTime) => new(FitMarkPositionKind.Moved, sourceTime);

    /// <summary>A stored source time exactly for the clamped and moved kinds.</summary>
    public bool IsValid => (Kind == FitMarkPositionKind.Proposed) == (SourceTime is null);
}

/// <summary>
/// The active mark of one FIT climb (D051 §5.3): the climb's identity; <c>W_auto</c> and the <c>StartCorrection</c> and
/// calibration version it was created with (frozen, §5.5); the video it was proposed on (chosen by §5.5.1, never
/// reassigned); its own position; <see cref="PositionChangedAt"/> (§5.6); and the optional explicit link to the video clip
/// whose <c>Duration</c> hold animation gives its effective start (§5.4). Immutable: every edit replaces the mark.
/// </summary>
public sealed record FitMark(
    ClimbIdentity Climb,
    DateTimeOffset AutoStartUtc,
    long StartCorrectionTicks,
    int CalibrationVersion,
    Guid MediaAssetId,
    FitMarkPosition Position,
    DateTimeOffset PositionChangedAt,
    Guid? LinkedClipId = null);

/// <summary>A climb the user declined (D051 §5.3, §5.7): its identity and, if a mark existed, the mark as it was — never
/// with a link (declining removes it). Never recreated by a new import or a new <c>Z</c>; not active, never observed.</summary>
public sealed record DeclinedClimb(ClimbIdentity Climb, FitMark? FormerMark = null);
