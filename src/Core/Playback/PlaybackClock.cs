using AiVideoEditor.Core.Common;

namespace AiVideoEditor.Core.Playback;

/// <summary>A reading of a monotonic reference: <see cref="Value"/> units, where one second
/// is <see cref="UnitsPerSecond"/> units (Stopwatch ticks, or audio samples played).</summary>
public readonly record struct ReferenceTime(long Value, long UnitsPerSecond);

/// <summary>
/// A monotonic time source driving <see cref="PlaybackClock"/>: a Stopwatch now, the audio
/// device position (samples played / sample rate) once audio output exists.
/// </summary>
public interface IReferenceClock
{
    ReferenceTime Now { get; }
}

/// <summary>
/// Timeline position of playback, computed as <c>anchor position + elapsed reference time</c>
/// (never accumulated frame by frame). Starting, seeking and switching the reference create
/// a new anchor at the current position, so none of them makes the position jump.
/// Elapsed time is converted with exact integer arithmetic:
/// <c>floor((now − anchor) · 10⁷ / unitsPerSecond)</c> ticks.
/// <para>Not thread-safe: owned by the playback service and used from one thread.</para>
/// </summary>
public sealed class PlaybackClock
{
    private readonly IReferenceClock _fallback;
    private IReferenceClock? _master;
    private MediaTime _anchorPosition;
    private ReferenceTime _anchorReference;

    /// <param name="fallback">Reference used whenever no master (audio) reference is set.</param>
    public PlaybackClock(IReferenceClock fallback) => _fallback = fallback;

    public bool IsRunning { get; private set; }

    /// <summary>True while a master reference (the audio device) drives the clock.</summary>
    public bool HasMaster => _master is not null;

    private IReferenceClock Reference => _master ?? _fallback;

    public MediaTime Position => IsRunning ? _anchorPosition + Elapsed(Reference.Now) : _anchorPosition;

    /// <summary>Starts advancing from the current position (new anchor).</summary>
    public void Start()
    {
        if (IsRunning) return;
        _anchorReference = Reference.Now;
        IsRunning = true;
    }

    /// <summary>Stops advancing; the position stays where it is.</summary>
    public void Pause()
    {
        if (!IsRunning) return;
        _anchorPosition = Position;
        IsRunning = false;
    }

    /// <summary>Moves to <paramref name="position"/> (new anchor); keeps running if it was.</summary>
    public void Seek(MediaTime position)
    {
        _anchorPosition = position;
        if (IsRunning) _anchorReference = Reference.Now;
    }

    /// <summary>Switches between the audio device (<paramref name="master"/>) and the fallback
    /// (null) without changing the current position.</summary>
    public void SetMaster(IReferenceClock? master)
    {
        if (ReferenceEquals(master, _master)) return;
        var position = Position;
        _master = master;
        _anchorPosition = position;
        if (IsRunning) _anchorReference = Reference.Now;
    }

    private MediaTime Elapsed(ReferenceTime now)
    {
        if (now.UnitsPerSecond != _anchorReference.UnitsPerSecond || now.UnitsPerSecond <= 0)
            throw new InvalidOperationException("Reference clock changed its unit without a new anchor.");

        var units = (Int128)now.Value - _anchorReference.Value;
        if (units <= 0) return MediaTime.Zero; // a reference never runs backwards; guard anyway
        return new MediaTime((long)(units * TimeSpan.TicksPerSecond / now.UnitsPerSecond));
    }
}
