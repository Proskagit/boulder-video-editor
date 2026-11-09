using AiVideoEditor.Core.Common;

namespace AiVideoEditor.Core.Interfaces;

/// <summary>
/// The application's global settings of one Windows user (D057 §3.8) — never a project's. Kept in a per-user file; a missing
/// or unusable file gives these defaults.
/// </summary>
public sealed record UserSettings
{
    /// <summary>The heart rate's display offset by default: −60 s (D057 §3.8).</summary>
    public static readonly MediaTime DefaultHeartRateDisplayOffset = MediaTime.FromSeconds(-60);

    /// <summary>The largest offset either way the settings accept.</summary>
    public static readonly MediaTime MaxHeartRateDisplayOffset = MediaTime.FromSeconds(600);

    /// <summary>
    /// D057 §3.8: the heart rate is shown shifted by this on the timeline — negative is earlier: at the picture of watch time
    /// <c>w</c> the value shown is the FIT's sample at <c>w − offset</c>. A display correction only: the FIT's samples,
    /// <c>T_file</c>, <c>CC</c>, <c>Z</c>, the binding, the marks, the calibration and the route bounds never use it.
    /// </summary>
    public long HeartRateDisplayOffsetTicks { get; init; } = DefaultHeartRateDisplayOffset.Ticks;

    /// <summary>D057 §3.15: automatic hold proposals on entering the markup mode — off by default.</summary>
    public bool HoldProposalsEnabled { get; init; }

    /// <summary>D057 §3.8: the interface language («ru» / «en»), or null for the application's configuration
    /// (<c>App:Language</c>). Applied at the next start.</summary>
    public string? Language { get; init; }

    /// <summary>D057 §3.8: the FFmpeg / FFprobe executables chosen by the user, or null for the application's configuration
    /// (<c>Ffmpeg:*</c>). Applied at the next start.</summary>
    public string? FfmpegPath { get; init; }

    public string? FfprobePath { get; init; }

    public MediaTime HeartRateDisplayOffset => new(HeartRateDisplayOffsetTicks);

    /// <summary>These settings with every value inside its bounds (a value from a file edited by hand).</summary>
    public UserSettings Normalized() => this with
    {
        HeartRateDisplayOffsetTicks = Math.Clamp(HeartRateDisplayOffsetTicks, -MaxHeartRateDisplayOffset.Ticks, MaxHeartRateDisplayOffset.Ticks),
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
