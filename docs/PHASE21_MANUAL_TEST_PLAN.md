# Phase 21 — manual test plan

Manual checks of the animation of the hold outlines (DECISIONS.md D036). Run in the real app on Windows with the hold model
installed (`docs/AI_MODEL.md`) and an isolated profile as in Phases 11–20. Fixture: `tools/manual/New-Phase19MarkupFixture.ps1`
with a real bouldering clip (e.g. the E1 clip c6, 3840 × 2160, 60 fps) on a 1920 × 1080 timeline; mark at least five holds
from the bottom to the top of the route before the animation rows.

Status column — kinds of evidence, never mixed:
- **auto** — covered by automated tests only, not checked in the real app;
- **cloud** — checked in the cloud session (Linux, Xvfb, ffmpeg 7.0.2) by automated tests, the GUI not run;
- **local (Claude)** / **PO** — the real-app run on Windows / the product owner's check.

The cloud session of 2026-10-15 could not run the Avalonia GUI interactively or open the product owner's clips. The local
verification on Windows the same day (Claude, `8e28d32`) ran the Release build with `-warnaserror` and the full suite — no
GUI rows. The product owner then checked Phase 21 in the real app on Windows (2026-10-15) and **accepted the main
functionality**. They reported as checked successfully: the outline animation and its fades in the Preview, the dashed
contours in the markup mode, the manual contour correction, Trim / Slip / speed change / Split, the display on the timeline,
the export with the exported video reopened, and save + reopen of the project. Rows marked **PO: PASS** are those; every
other row stays **NOT RUN** in the real app — not reported as checked, covered by automated tests only where stated.

| # | Scenario | Expected | Automated coverage | Status |
|---|---|---|---|---|
| 1 | Select a marked clip; Holds section | An "Animate" check box under Keyline; the block hidden while it is off; nothing changes in the Preview | `HoldMarkupUiTests` | auto — NOT RUN |
| 2 | Pause on the clip at the start of the climb; check "Animate" | One undo step "Animate Holds"; Start = the source time under the playhead; Order Bottom to top, 0.5 / 0.25 / 3 / 0.5 s; the summary line "All N shown after …, gone after …" | `HoldMarkupUiTests` | auto — NOT RUN (the defaults, the undo step and the summary not reported separately) |
| 3 | Play from before the start | Before the start no contour; from the start the lowest hold fades in, then the next every 0.5 s upwards; each stays; 3 s after the last all fade out together; nothing to the clip's end | `HoldAnimationSnapshotTests`, `HoldAnimationRuleTests` | **PO: PASS** (2026-10-15) — the animation and the fades in the Preview |
| 4 | Pause / step frame by frame through an appear fade and the disappear fade | Each frame a steady state (no flicker); stepping back shows the earlier state; a seek shows the state of the frame under the playhead | `HoldAnimationSnapshotTests` | auto — NOT RUN |
| 5 | Order → Marking order | The holds appear in the order they were marked; one undo step | `HoldAnimationRuleTests`, `HoldMarkupUiTests` | auto — NOT RUN |
| 6 | Change Interval several times with the spinner, then Undo once | All the interval changes undone in one step; the Inspector follows | `HoldAnimationEditTests`, `HoldMarkupUiTests` | auto — NOT RUN |
| 7 | Appear fade 0; Disappear fade 0 | Holds pop in / all vanish on one frame | `HoldAnimationRuleTests` | auto — NOT RUN |
| 8 | "until clip end" | Every contour stays to the clip's end; Disappear fade hidden, Stay disabled | `HoldAnimationRuleTests`, `HoldAnimationSnapshotTests` | auto — NOT RUN |
| 9 | Move the playhead near the clip's end, "Set to playhead" | The start moves there; the orange warning that the animation runs past the clip's end; the Preview cuts it at the end | `HoldMarkupUiTests` | auto — NOT RUN |
| 10 | Trim the clip's head, slip it, set 2× speed | The animation stays on the same moment of the climb (the same picture when the first hold appears); at 2× the holds still appear every 0.5 s of output | `HoldAnimationEditTests`, `HoldAnimationSnapshotTests` | **PO: PASS** (2026-10-15) — Trim, Slip and the speed change |
| 11 | Split the clip in the middle of the animation | The right half continues the animation without a restart; both halves keep their own settings afterwards | `HoldAnimationEditTests` | **PO: PASS** (2026-10-15) |
| 12 | Duplicate / Copy + Paste the clip | The copies carry the animation | `HoldAnimationEditTests` | auto — NOT RUN |
| 13 | Opacity 50 % and a fade-in on the clip; a dissolve into the next clip | The contours fade with the clip (hold × clip × fade / dissolve); a keyline never shows through the core, the corners of a fading outline are not darker | `HoldAnimationSnapshotTests`, `HoldAnimationRenderTests` | auto — NOT RUN |
| 14 | Markup mode on a frame before the start (no contour in the composition) | Every hold outlined thin, white, dashed; click one: selected (yellow); drag a handle / Alt+click / Delete over a handle as in Phase 20; Done → the dashed outlines go | `HoldMarkupUiTests` | **PO: PASS** (2026-10-15) — the dashed contours in the markup mode |
| 15 | Phase 20 regression: correct a contour with the animation off and on | As `docs/PHASE20_MANUAL_TEST_PLAN.md` rows 1–12; with Bottom to top a contour dragged lower may now appear earlier | `HoldMarkupUiTests`, `HoldAnimationEditTests` | **PO: PASS** (2026-10-15) — the manual contour correction |
| 16 | Export the clip; check with ffmpeg frames before the start, mid appear fade, all shown, mid disappear fade, after | The exported frames show exactly the Preview's state of those frames; no dashed outline, no handle in the video | `ExportHoldAnimationEndToEndTests` (synthetic clip: Preview = canvas bytes, MP4 within codec error) | cloud (synthetic clip); **PO: PASS** (2026-10-15) — a real export, the exported video reopened and viewed; the frame-by-frame ffmpeg comparison with the Preview not run |
| 17 | Save, close, reopen | The animation as set (every value); the file says `"formatVersion": 6` | `HoldAnimationPersistenceTests` | **PO: PASS** (2026-10-15) — save and reopen of the project |
| 18 | Open a Phase 20 (v5) project | Opens without animation; saved as v6 | `HoldAnimationPersistenceTests` | auto — NOT RUN |
| 19 | Open the saved v6 project in the `main` build of Phase 20 | Refused: "written by a newer version" | existing newer-version check (`HoldMarkupPersistenceTests`) | NOT RUN |
| 20 | Start an export, try to change a setting | Refused with "The animation can't be changed while an export is running." | `HoldMarkupUiTests` | auto — NOT RUN |
| 21 | Inspector at the minimum window width | The Animation block readable; labels not clipped; the warning wraps | — | NOT RUN |
| 22 | The product owner on their own clips | The scenario of D036 §1 with every parameter; Preview = export | — | **PO: main functionality accepted** (2026-10-15); not every parameter reported as checked — see the rows above |
| 23 | The clip with an animated route on the timeline | Shown as before; nothing of the animation breaks the timeline display | — | **PO: PASS** (2026-10-15) — the display on the timeline (row added at acceptance) |

## Follow-ups found during the acceptance (not in the scope of Phase 21)

Both are about the timeline markers of Phase 12 (DECISIONS.md D027 §6), not about the outline animation; Phase 21 changed no
marker code. Recorded as separate follow-up issues in `ROADMAP.md` ("Follow-ups and backlog"); not fixed in Phase 21.

- **FU-21.1 — "Previous marker" status message.** Going to a marker outside the clip sometimes first shows "There is no
  marker before the playhead", although a second press moves the playhead; after returning, the warning stays in the
  status bar. Cause not investigated yet. Known from the code: Previous / Next compare the markers with the playhead's
  nearest frame (`TimelineEditService.PreviousMarker` / `NextMarker`), and a successful jump reports nothing, so an
  earlier status message is not replaced (`TimelineViewModel.PreviousMarker`).
- **FU-21.2 — Markers stay in place when a clip is moved.** Observed: after moving a clip the markers keep their
  position. Semantics found: markers are sequence (timeline) markers — `Sequence.Markers`, each a timeline position;
  D027 ("Refined in Step 12.7") says that a ripple, a move or any other edit never moves a marker. So the observed
  behaviour matches the current decision; whether clip-bound markers (moving with their clip) are wanted is a product
  question for the product owner, not decided here.
