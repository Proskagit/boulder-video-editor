namespace AiVideoEditor.Core.Interfaces;

/// <summary>The whole computer's load: the processor and the graphics card, in percent (0–100); null where not measurable.</summary>
public readonly record struct SystemLoad(double? Cpu, double? Gpu);

/// <summary>
/// D078 amendment 1: the processor's and the graphics card's load for the toolbar's monitor. <see cref="Sample"/> returns the
/// load since the previous call (the first call primes it and may return nulls). Not for the UI thread — it may take a few
/// milliseconds; never throws.
/// </summary>
public interface ISystemLoadMonitor
{
    SystemLoad Sample();
}
