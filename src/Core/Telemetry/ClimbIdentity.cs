namespace AiVideoEditor.Core.Telemetry;

/// <summary>The full identity of a FIT climb (D050 B8): the split index alone is stable only within one FIT, so a
/// decision or an exclusion stays bound to the climb's index, number and start, all three. Shared by the research study
/// (Step 35.3) and the FIT marks of Phase 37 (D051 §5.1).</summary>
public readonly record struct ClimbIdentity(int ClimbKey, int ClimbNumber, DateTimeOffset ClimbStartUtc);
