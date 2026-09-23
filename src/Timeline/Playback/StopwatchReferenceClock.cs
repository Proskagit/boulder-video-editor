using System.Diagnostics;
using AiVideoEditor.Core.Playback;

namespace AiVideoEditor.Timeline.Playback;

/// <summary>Monotonic fallback reference (and the master until audio output exists).</summary>
public sealed class StopwatchReferenceClock : IReferenceClock
{
    public ReferenceTime Now => new(Stopwatch.GetTimestamp(), Stopwatch.Frequency);
}
