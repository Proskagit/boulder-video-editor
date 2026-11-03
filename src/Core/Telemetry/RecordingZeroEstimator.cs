using System.Globalization;
using System.Text.RegularExpressions;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;

namespace AiVideoEditor.Core.Telemetry;

/// <summary>Where a media file's recording zero comes from (D049 §5).</summary>
public enum RecordingZeroState
{
    /// <summary>Set by the user (<see cref="MediaAsset.RecordingZeroOverrideUtc"/>).</summary>
    Override,

    /// <summary>Derived by Step 35.3's approved rule. Never produced until the product owner approves it (D050 B1, B6).</summary>
    Automatic,

    /// <summary>Not known: no heart rate on the file's clips; <see cref="RecordingZero.Reason"/> says why.</summary>
    Unknown,
}

/// <summary>Why a recording zero is unknown.</summary>
public enum RecordingZeroUnknownReason
{
    /// <summary>Only a video has a recording zero.</summary>
    NotAVideo,

    /// <summary>No override and no approved automatic rule (D050 amendment B1).</summary>
    NoApprovedRule,
}

/// <summary>A media file's recording zero in camera time C (offset 0), its state and, when unknown, the reason.</summary>
public readonly record struct RecordingZero(DateTimeOffset? Utc, RecordingZeroState State, RecordingZeroUnknownReason? Reason)
{
    public static RecordingZero Unknown(RecordingZeroUnknownReason reason) => new(null, RecordingZeroState.Unknown, reason);
}

/// <summary>What the probe found of a file's recording-time tag.</summary>
public enum RecordingTimeTagStatus
{
    /// <summary>The file has not been probed (no <see cref="RecordingTimeProbe"/>).</summary>
    NotProbed,

    /// <summary>Probed: the file has no tag.</summary>
    Absent,

    /// <summary>A tag the parser does not read (D049 §3).</summary>
    Unrecognised,

    /// <summary>A tag read as an instant.</summary>
    Recognised,
}

/// <summary>
/// The hypotheses for a video's recording zero — research values of Step 35.3, never a zero by themselves (D049 §5): the
/// tag read as the end of the recording (<c>tag − duration</c>) or as its start (<c>tag</c>), and the file-name time under
/// an offset hypothesis <c>h</c> (<c>L − h</c>, a check only, D049 §1). Each is null when its inputs are missing.
/// </summary>
public sealed record RecordingZeroCandidates(
    RecordingTimeTagStatus TagStatus,
    DateTimeOffset? TagUtc,
    DateTimeOffset? TagMarksEnd,
    DateTimeOffset? TagMarksStart,
    DateTime? FileNameLocalTime,
    DateTimeOffset? FromFileName);

/// <summary>
/// The one estimator of a media file's recording zero (D050 amendment B8): the product's zero state and Step 35.3's research
/// both use it, so the rule that is approved is the rule that was measured. Until the rule is approved (D050 B1, B6) it
/// returns the override or «unknown», never «automatic»; <see cref="Candidates"/> only describes the hypotheses and is
/// never used as a zero. Never reads the system clock or time zone.
/// </summary>
public static partial class RecordingZeroEstimator
{
    /// <summary>The recording zero the mapping uses (D049 §5).</summary>
    public static RecordingZero Resolve(MediaAsset asset)
    {
        if (asset.Kind != MediaKind.Video) return RecordingZero.Unknown(RecordingZeroUnknownReason.NotAVideo);
        if (asset.RecordingZeroOverrideUtc is { } overridden)
            return new(overridden.ToUniversalTime(), RecordingZeroState.Override, null);
        return RecordingZero.Unknown(RecordingZeroUnknownReason.NoApprovedRule);
    }

    /// <summary>The hypotheses for the file's recording zero; <paramref name="fileNameOffset"/> is the offset hypothesis
    /// <c>h</c> of its file-name time (e.g. the watch's UTC offset, D049 Q-TZ), applied once; null skips that check.</summary>
    public static RecordingZeroCandidates Candidates(MediaAsset asset, TimeSpan? fileNameOffset = null) =>
        Candidates(asset.FilePath, asset.Metadata?.Duration, asset.Metadata?.RecordingTimeProbe, fileNameOffset);

    /// <summary>The same hypotheses from a snapshot (Step 35.3b2): the file name (the study passes the video's original
    /// name), the duration and the probe as recorded; <paramref name="fileNameOffset"/> is the explicit hypothesis <c>h</c>.</summary>
    public static RecordingZeroCandidates Candidates(string fileName, MediaTime? duration, RecordingTimeProbe? probe, TimeSpan? fileNameOffset)
    {
        var tag = RecordingTimeTag.Parse(probe?.Tag);
        var status = probe is null ? RecordingTimeTagStatus.NotProbed
            : !probe.IsFound ? RecordingTimeTagStatus.Absent
            : tag is null ? RecordingTimeTagStatus.Unrecognised
            : RecordingTimeTagStatus.Recognised;
        var length = duration is { } d && d > MediaTime.Zero ? d : (MediaTime?)null;
        var local = FileNameLocalTime(fileName);
        return new RecordingZeroCandidates(
            status,
            tag,
            tag is { } end && length is { } l2 ? end.AddTicks(-l2.Ticks) : null,
            tag,
            local,
            local is { } l && fileNameOffset is { } h ? new DateTimeOffset(l, h).ToUniversalTime() : null);
    }

    [GeneratedRegex(@"(?<!\d)(\d{8})_(\d{6})(?!\d)", RegexOptions.CultureInvariant)]
    private static partial Regex FileNameStamp();

    /// <summary>The phone's local wall time L in a file name such as <c>video_20260927_100405.mp4</c> (no zone; never stored,
    /// D049 §1), or null when the name has no such stamp.</summary>
    public static DateTime? FileNameLocalTime(string path)
    {
        var m = FileNameStamp().Match(Path.GetFileNameWithoutExtension(path));
        return m.Success && DateTime.TryParseExact(m.Groups[1].Value + m.Groups[2].Value, "yyyyMMddHHmmss",
            CultureInfo.InvariantCulture, DateTimeStyles.None, out var local)
            ? DateTime.SpecifyKind(local, DateTimeKind.Unspecified)
            : null;
    }
}
