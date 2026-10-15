# Phase 22 — manual test plan

Manual checks of the timeline selection, the magnetic snapping and the making of room (DECISIONS.md D037). Run in the real
app on Windows with an isolated profile as in Phases 11–21. Fixture: a project with V1, V2, A1 (and A2 for the linked rows),
several video clips with their own sound, one unlinked pair (Unlink Audio) with gaps around it, a dissolve between two clips
of V1, a text clip on V2, and one locked track for the lock rows. Window widths 1024 and 1440 px.

Status column — kinds of evidence, never mixed:
- **auto** — covered by automated tests only, not checked in the real app;
- **local (Claude)** / **PO** — the real-app run on Windows / the product owner's check.

| # | Scenario | Expected | Automated coverage | Status |
|---|---|---|---|---|
| 1 | Ctrl+A with a locked track | Every clip of the unlocked tracks selected (hidden / muted included), none of the locked one (except a partner of a selected clip); "N clips selected"; the Inspector shows the earliest clip "(1 of N selected)" | `TimelineSelectionUiTests` | auto — NOT RUN |
| 2 | Ctrl+A in a text box of the Inspector; in the markup mode | The text box selects its text; in the markup mode nothing on the timeline changes | `ShortcutRoutingTests` (text box) | auto — NOT RUN |
| 3 | Esc with a selection; Esc during a drag | The selection cleared; during a drag the drag is cancelled, the clips back, the selection kept | `TimelineSelectionUiTests` | auto — NOT RUN |
| 4 | Marquee from an empty place across V1 and A1 | The rectangle drawn; every clip it touches selected while dragging; locked-track clips not; partners come along | `TimelineSelectionUiTests` | auto — NOT RUN |
| 5 | Ctrl + marquee with a selection | The touched clips added to the selection; Esc during the marquee restores the selection from before | `TimelineSelectionUiTests` | auto — NOT RUN |
| 6 | Click an empty place without dragging; Ctrl+click it | The plain click clears the selection; the Ctrl click leaves it | `TimelineSelectionUiTests` | auto — NOT RUN |
| 7 | Delete / Ripple Delete / Copy + Paste / Duplicate / Split / Q / W on a Ctrl+A or marquee selection | Each as for a Ctrl+click selection, one undo step | the existing command tests (Phases 12, 15) | auto — NOT RUN |
| 8 | Drag a clip near another clip's edge, the playhead, a marker, 0 (Snap on) | The clip snaps, the blue indicator at the target; with N (Snap off) it lands under the pointer, gaps stay | `TimelineSelectionUiTests` | auto — NOT RUN |
| 9 | Drag a group of clips | The group's outer edges snap | `TimelineSelectionUiTests` | auto — NOT RUN |
| 10 | Drag a clip so that its end snaps into the next clip | The target that would make an overlap is skipped; no push made by the magnet alone | `TimelineSelectionUiTests` | auto — NOT RUN |
| 11 | Drop a file from the Media Browser near a clip's end / onto an empty track | Snaps to the edge with the indicator during the drag; on an empty track at 0; with Snap off exactly under the pointer | `TimelineSelectionUiTests` | auto — NOT RUN |
| 12 | Drag a clip right onto the start of the next clip | The next clip (and the following touching ones) pushed right by exactly the overlap during the drag and after the release; a gap absorbs the push; one undo step restores all | `MakeRoomEditTests`, `TimelineSelectionUiTests` | auto — NOT RUN |
| 13 | Drag a clip so it starts inside another clip | Red, the release changes nothing, no undo step | `MakeRoomEditTests`, `TimelineSelectionUiTests` | auto — NOT RUN |
| 14 | Push a clip whose partner on A1 has a clip right after it | Refused (red) with a message; nothing changes | `MakeRoomEditTests` | auto — NOT RUN |
| 15 | Push a clip that has a dissolve with its unmoved left neighbour | Refused with a message; the dissolve stays | `MakeRoomEditTests` | auto — NOT RUN |
| 16 | Push a chain containing two clips with a dissolve between them | Both move by the same frames; the dissolve kept | `MakeRoomEditTests` | auto — NOT RUN |
| 17 | Paste / Duplicate / + Text where clips already are | The clips on the right pushed; the pasted / duplicated / new clip at its exact place (no snap); one undo step | `MakeRoomEditTests`, `TimelineClipboardUiTests` | auto — NOT RUN |
| 18 | Insert `,` / Overwrite `.` from the Source viewer | Unchanged (Phase 16 rules) | `MakeRoomEditTests` | auto — NOT RUN |
| 19 | Alt+→ ×3, Alt+Shift+←, Undo ×4 | Three one-frame moves, one 1 s move back, each its own undo step; the Preview follows each | `TimelineSelectionUiTests` | auto — NOT RUN |
| 20 | Alt+← into the previous clip; Alt+← at 0 | Refused, no undo step, the status says why | `TimelineSelectionUiTests` | auto — NOT RUN |
| 21 | Drag a clip / an edge / a marquee to the right edge of the view on a long timeline | The view scrolls, faster farther in; the gesture continues with the content under the pointer | `TimelineSelectionUiTests` (the speed rule only) | auto — NOT RUN |
| 22 | Any edit of rows 12–20 on a locked track; during an export | Refused, nothing changes | `MakeRoomEditTests`, `TimelineSelectionUiTests` | auto — NOT RUN |
| 23 | Ripple Delete, Close Gap, trims and dissolves after the phase | As in Phase 21 | the existing Phase 12–21 suites | auto — NOT RUN |
| 24 | Open a Phase 21 project, save it | `formatVersion` 6, byte for byte as Phase 21 wrote it | — (Project.Tests unchanged) | NOT RUN |
| 25 | The timeline header, the Inspector line at 1024 and 1440 px | Readable, nothing clipped | — | NOT RUN |
