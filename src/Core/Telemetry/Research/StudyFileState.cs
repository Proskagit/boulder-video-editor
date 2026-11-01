namespace AiVideoEditor.Core.Telemetry.Research;

/// <summary>
/// The state of a study's file in this session (never saved). <see cref="Verified"/> only after a full SHA-256 equal to the
/// snapshot with the file's size and time unchanged across the read; a quick check (size and time) gives at most
/// <see cref="QuickOk"/>. A freeze or an evaluation needs <see cref="Verified"/> for every subject in the set, re-checked
/// by size and time right before; a change after that last check is not detected.
/// </summary>
public enum StudyFileState
{
    NotChecked,
    QuickOk,
    Verified,
    Missing,
    Unreadable,
    Changed,
    ChangedDuringRead,
}
