using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Telemetry;

namespace AiVideoEditor.Core.Interfaces;

/// <summary>
/// The application's global settings of one Windows user (D057 §3.8) — never a project's. Kept in a per-user file; a missing
/// or unusable file gives these defaults.
/// </summary>
public sealed record UserSettings
{
    /// <summary>The heart rate's display offset of a COROS training by default: −70 s (D057 §3.8; −60 s until the owner's
    /// acceptance of 2026-11-10).</summary>
    public static readonly MediaTime DefaultHeartRateDisplayOffset = MediaTime.FromSeconds(-70);

    /// <summary>D062 amendment 1 (owner, 2026-11-17): the offset of a Garmin training and of any other watch's by default:
    /// −20 s.</summary>
    public static readonly MediaTime DefaultOtherHeartRateDisplayOffset = MediaTime.FromSeconds(-20);

    /// <summary>The largest offset either way the settings accept.</summary>
    public static readonly MediaTime MaxHeartRateDisplayOffset = MediaTime.FromSeconds(600);

    /// <summary>
    /// D057 §3.8: the heart rate is shown shifted by this on the timeline — negative is earlier: at the picture of watch time
    /// <c>w</c> the value shown is the FIT's sample at <c>w − offset</c>. A display correction only: the FIT's samples,
    /// <c>T_file</c>, <c>CC</c>, <c>Z</c>, the binding, the marks, the calibration and the route bounds never use it.
    /// </summary>
    /// <remarks>The value for a COROS training (D062 amendment 1); the name is kept so a saved value stays the COROS one.</remarks>
    public long HeartRateDisplayOffsetTicks { get; init; } = DefaultHeartRateDisplayOffset.Ticks;

    /// <summary>D062 amendment 1: the display offset of a Garmin training.</summary>
    public long GarminHeartRateDisplayOffsetTicks { get; init; } = DefaultOtherHeartRateDisplayOffset.Ticks;

    /// <summary>D062 amendment 1: the display offset of a training from any other watch.</summary>
    public long OtherHeartRateDisplayOffsetTicks { get; init; } = DefaultOtherHeartRateDisplayOffset.Ticks;

    /// <summary>D057 §3.15: automatic hold proposals on entering the markup mode — off by default.</summary>
    public bool HoldProposalsEnabled { get; init; }

    /// <summary>D061 amendment 1: the people's masks drawn over the Preview — a diagnostic of the segmentation, off by default;
    /// never exported.</summary>
    public bool ShowPeopleMasks { get; init; }

    /// <summary>D058 §1: the timeline turns its page with the playhead while playing; on by default.</summary>
    public bool TimelineFollowsPlayhead { get; init; } = true;

    /// <summary>D063 §B: the Preview's magnetic snap of a picture's edges to the canvas edges — its threshold in screen
    /// pixels (0: off).</summary>
    public double CanvasSnapPixels { get; init; } = Composition.CanvasSnap.DefaultThresholdPixels;

    /// <summary>D065 §3: the smoothing of a new hold's contour (0 — the mask's own boundary … 100), the slider «Сглаживание»
    /// of the hold markup.</summary>
    public int HoldContourSmoothing { get; init; } = Holds.ContourSmoothing.DefaultStrength;

    /// <summary>D071 §2: the Preview's picture quality, chosen on the Preview's bar («Среднее», 1080p, by default) — the largest
    /// picture the Preview decodes (amendment 3: no proxies).</summary>
    public Playback.PreviewQuality PreviewQuality { get; init; } = Playback.PreviewDecoding.DefaultQuality;

    /// <summary>D057 §3.8: the interface language («ru» / «en»), or null for the application's configuration
    /// (<c>App:Language</c>). Applied at the next start.</summary>
    public string? Language { get; init; }

    /// <summary>D057 §3.8: the FFmpeg / FFprobe executables chosen by the user, or null for the application's configuration
    /// (<c>Ffmpeg:*</c>). Applied at the next start.</summary>
    public string? FfmpegPath { get; init; }

    public string? FfprobePath { get; init; }

    /// <summary>The COROS training's display offset.</summary>
    public MediaTime HeartRateDisplayOffset => new(HeartRateDisplayOffsetTicks);

    /// <summary>D062 amendment 1: the display offset of a training by its watch's maker.</summary>
    public MediaTime HeartRateDisplayOffsetFor(FitVendor vendor) => new(vendor switch
    {
        FitVendor.Coros => HeartRateDisplayOffsetTicks,
        FitVendor.Garmin => GarminHeartRateDisplayOffsetTicks,
        _ => OtherHeartRateDisplayOffsetTicks,
    });

    /// <summary>The display offset of a training whose file names <paramref name="manufacturer"/>; zero without a training.</summary>
    public MediaTime HeartRateDisplayOffsetFor(int? manufacturer) =>
        manufacturer is { } m ? HeartRateDisplayOffsetFor(FitWorkoutParser.VendorOf(m)) : MediaTime.Zero;

    /// <summary>These settings with every value inside its bounds (a value from a file edited by hand).</summary>
    public UserSettings Normalized() => this with
    {
        HeartRateDisplayOffsetTicks = Math.Clamp(HeartRateDisplayOffsetTicks, -MaxHeartRateDisplayOffset.Ticks, MaxHeartRateDisplayOffset.Ticks),
        GarminHeartRateDisplayOffsetTicks = Math.Clamp(GarminHeartRateDisplayOffsetTicks, -MaxHeartRateDisplayOffset.Ticks, MaxHeartRateDisplayOffset.Ticks),
        OtherHeartRateDisplayOffsetTicks = Math.Clamp(OtherHeartRateDisplayOffsetTicks, -MaxHeartRateDisplayOffset.Ticks, MaxHeartRateDisplayOffset.Ticks),
        CanvasSnapPixels = double.IsFinite(CanvasSnapPixels)
            ? Math.Clamp(CanvasSnapPixels, Composition.CanvasSnap.MinThresholdPixels, Composition.CanvasSnap.MaxThresholdPixels)
            : Composition.CanvasSnap.DefaultThresholdPixels,
        HoldContourSmoothing = Math.Clamp(HoldContourSmoothing, Holds.ContourSmoothing.MinStrength, Holds.ContourSmoothing.MaxStrength),
        PreviewQuality = Enum.IsDefined(PreviewQuality) ? PreviewQuality : Playback.PreviewDecoding.DefaultQuality,
        Language = string.IsNullOrWhiteSpace(Language) ? null : Language.Trim(),
        FfmpegPath = string.IsNullOrWhiteSpace(FfmpegPath) ? null : FfmpegPath.Trim(),
        FfprobePath = string.IsNullOrWhiteSpace(FfprobePath) ? null : FfprobePath.Trim()
    };
}

/// <summary>The global settings (D057 §3.8): read once at start, changed by the settings window and a few controls.</summary>
public interface IUserSettingsService
{
    UserSettings Current { get; }

    /// <summary>Raised on the calling thread after <see cref="Update"/> changed the settings.</summary>
    event EventHandler? Changed;

    /// <summary>Applies <paramref name="change"/> and saves the result; a change that leaves the settings equal does nothing.
    /// A failed save is logged and never thrown: the settings stay changed for this run.</summary>
    void Update(Func<UserSettings, UserSettings> change);
}

/// <summary>Settings in memory only — the default for code and tests without a settings file.</summary>
public sealed class InMemoryUserSettings(UserSettings? initial = null) : IUserSettingsService
{
    public UserSettings Current { get; private set; } = (initial ?? new UserSettings()).Normalized();

    public event EventHandler? Changed;

    public void Update(Func<UserSettings, UserSettings> change)
    {
        var next = change(Current).Normalized();
        if (next == Current) return;
        Current = next;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
