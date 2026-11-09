# Phase 37 — Preliminary route starts from a COROS FIT and the global start calibration: manual test plan

Decision: DECISIONS.md D051 (approved 2026-11-03). Record of the phase: `docs/PHASE37.md`. **State (2026-11-04): the
acceptance items D051 A1–A15 are checked automatically — see «Automated acceptance of D051 A1–A15» below; the rows A1–G7
of this plan were not run one by one by hand. Phase 37 accepted by the product owner on 2026-11-04 on that evidence
(«Product owner's acceptance» below).** The phase is implemented and merged into `main` with PR #33 (`1640996`). The acceptance items of D051 §10 are cited as «D051 A1»…«D051 A15»; the rows of this plan have their own
numbers.

*Since D056 (2026-11-09, PR #54) the marks are not drawn on the clips; the rows that look at a mark on a clip describe the
state of 2026-11-04 — the marks and their actions are in the Inspector («FIT-метки»).*

Environment: a build of `main` at `1640996` (the merge of PR #33) or later; the projects of 08.08.2026 and 27.09.2026 with their FIT and videos (personal
data, never committed); Russian interface unless a row says otherwise, an English pass for the new texts. Before part F:
a copy of `%LOCALAPPDATA%\AiVideoEditor\config\fit-calibration.json`, if it exists, to restore it afterwards. **Beta (the
collection of observations) is on** for every row — the default (D051 §6.2); check it in «Настройки FIT…» before the run.
A row that turns it off (F5) turns it on again before the next row.

Legend: **PASS** — the criterion is met; **FAIL** — a defect; **PARTIAL** — part met, the rest recorded; **NOT RUN** —
not run, with a reason.

Notation: `W_s` the climb's FIT start, `W_auto = W_s − StartCorrection`, `Z` a video's recording zero, `CC` the training's
clock correction; mark kinds `proposed` / `clamped` / `moved` (D051 §5.3).

## Automated acceptance of D051 A1–A15 (2026-11-04)

Run on `main` `3bb5fa1` plus the branch `test/phase-37-31-acceptance`. «Real data» means the product owner's 27.09 session
(the FIT and its 16 videos, probed in place, read only) with each video's `Z` set as the owner sets it — «Совместить кадр
с концом подъёма» at the owner's frozen end mark of a confirmed climb (35.3R2) — and, for A10, also the owner's accepted
research table of 08.08. Nothing of the real material is written, copied into the repository or quoted here. The
calibration tests use a real `FitCalibrationStore` on a file in a temporary folder, never the user's file.

| D051 | What it checks | How | Evidence | Result |
|---|---|---|---|---|
| A1 | one mark per filmed climb, `W_auto = W_s − 12 s`; on the edge `clamped` and shown as such; none without `Z`; none in Preview / export | real data + unit / UI | `Phase37RealSessionTests.A1_A13_…` (local): 8 of 16 videos with `Z`, 12 marks (C2–C6, C8–C14), exactly the climbs whose start or `W_auto` lies in a video with `Z`, all `proposed`, `StartCorrection` 12 s, none on the 8 videos without `Z`; `FitMarkUiTests.A_mark_clamped_to_the_edge_…` (new): `clamped` on frame 0 and named so in the Inspector; `FitMarkEditTests.Marks_are_an_editor_layer_only_…` (new): the `PlaybackSnapshot` that Preview and export render is the same with and without marks | **PASS** |
| A2 | «Разметить трассу от метки» opens D048 at the mark, the route starts at `T`; moving `T` leaves the mark; deleting the animation makes the mark the start again | unit / UI | `FitMarkEditTests.Laying_the_route_out_from_a_mark_…`, `Moving_T_of_the_linked_animation_…`, `Deleting_the_linked_animation_makes_the_mark_the_start_again_…` (new: the markup deleted, or only its animation), `Deleting_the_linked_clip_…`; `FitMarkUiTests.The_Inspector_lays_out_from_the_mark_…` | **PASS** |
| A3 | a declined climb never returns on import / `Z`; «Вернуть» and Undo; a linked animation keeps working without the link; Undo / Redo of `Z` | unit / UI | `FitMarkEditTests.Declining_a_linked_climb_…`, `A_new_Z_never_brings_a_declined_climb_back_…`, `Setting_Z_proposes_the_marks_in_the_same_undo_step_and_Redo_…`; `FitMarkUiTests.A_new_import_of_the_same_activity_…` | **PASS** |
| A4 | Beta on for a fresh user; a save fills the calibration; an unchanged save adds nothing; a moved mark changes one record | integration (real store, temp file) | `FitCalibrationAcceptanceTests.A4_…` (new): no file → Beta on, 0; the first save creates the file with 5 records; an unchanged save leaves the bytes; a move changes one record | **PASS** |
| A5 | below 20 the value stays 12 s; above it follows §6.5; reset → 12 s | integration | `FitCalibrationAcceptanceTests.A4_…` (5 records → 12 s), `A5_A6_A7_…` (new): 24 records, 19 moved by −6 s → the estimate of `StartCalibration` and the window's text; reset → 0 records, 12 s; `StartCalibrationTests` (T1) | **PASS** |
| A6 | Beta off: count unchanged, nothing removed, imports use the last value, off kept after a restart | integration | `FitCalibrationAcceptanceTests.A5_A6_A7_…`: the file's bytes unchanged by a save, Beta still off in a new store | **PASS** |
| A7 | a new import after a change proposes with the new value; earlier marks do not move | integration | `FitCalibrationAcceptanceTests.A5_A6_A7_…`: a second project's marks carry the new estimate, the first project's marks keep 12 s | **PASS** |
| A8 | hide → save → Undo → save: the count back, no duplicate | integration | `FitCalibrationAcceptanceTests.A8_…` (new): 5 → 4 → 5 used, 5 records in the file; `FitCalibrationSaveSyncTests.A_hidden_mark_leaves_a_tombstone_…` | **PASS** |
| A9 | the same FIT again: no second mark; a second mark refused | unit / UI | `FitMarkUiTests.A_new_import_of_the_same_activity_…`, `FitMarkRulesTests.Proposals_skip_active_and_declined_climbs_…`, `FitMarkPersistenceTests` (two active marks → damaged) | **PASS** |
| A10 | measured, before / after the calibration, no threshold | real data | `Phase37RealSessionTests.A10_…` (local), see «A10 measurement» below | **PASS** (measured) |
| A11 | a project without a FIT and animations made before the import work and export by D048; a second link refused both ways | unit | `FitMarkEditTests.Without_a_training_the_D048_edits_work_as_before`, `A_link_is_unique_both_ways_…`; `FitMarkPersistenceTests.An_animation_without_a_link_…` | **PASS** |
| A12 | Copy / Duplicate of a linked clip refused; an ordinary markup pasted next to it; Split keeps the link with the markup | unit | `FitMarkEditTests.Paste_and_Duplicate_of_a_linked_clip_are_refused_…` (an ordinary markup pasted in the same test), `A_split_before_the_animation_…` | **PASS** |
| A13 | the joint, overlapping recordings by §5.5.1; a climb begun before the recording gets no mark | unit + real data | `FitMarkRulesTests.At_a_joint_…`, `Of_two_overlapping_recordings_…`, `With_equal_starts_…`, `A_climb_whose_start_is_not_filmed_…`; real 27.09: C1 (begun before every recording) has no mark | **PASS** |
| A14 | a `clamped` mark gives no observation; a move or a layout raises the count by one; Undo lowers it | integration | `FitCalibrationAcceptanceTests.A14_…` (new); `FitMarkEditTests.A_mark_clamped_to_the_last_frame_laid_out_from_…`, `Moving_a_clamped_mark_makes_it_usable` | **PASS** |
| A15 | a link to an animation on another video: one record by that video's `Z` | unit | `FitMarkRulesTests.A_link_to_an_animation_on_another_video_uses_that_videos_zero_once` (the mark video's `Z` has no influence, the animation video's `Z` moves the value, no `Z` → none); `FitCalibrationSaveSyncTests.A_save_writes_one_record_per_usable_mark` | **PASS** |

**A10 measurement** (`Phase37RealSessionTests.A10_…`, 28 observations: 10 confirmed climbs of 27.09, 18 of 08.08). An
observation is what a mark moved to the visible start would record, `c = W_s − W_visible start`, with each video's `Z`
matched by the end of its first climb. The estimate is computed by the application's own `StartCalibration.Estimate`
from all 28. A route is counted «usable without a correction» when the proposal is within 2 s of the visible start.

| Training | `StartCorrection` | median error | within 1 s | within 2 s | within 3 s | corrections (> 2 s) |
|---|---|---|---|---|---|---|
| 27.09 | before 12.0 s | 8.3 s | 1/10 | 1/10 | 2/10 | 9 |
| 27.09 | after 11.98 s | 8.2 s | 1/10 | 1/10 | 2/10 | 9 |
| 08.08 | before 12.0 s | 3.1 s | 6/18 | 8/18 | 9/18 | 10 |
| 08.08 | after 11.98 s | 3.2 s | 6/18 | 8/18 | 8/18 | 10 |
| both | before 12.0 s | 3.8 s | 7/28 | 9/28 | 11/28 | 19 |
| both | after 11.98 s | 3.8 s | 7/28 | 9/28 | 10/28 | 19 |

The median lag of both trainings is 11.8 s, so the calibration hardly moves the value (12.0 → 11.98 s) and the share of
usable routes does not change: on these two trainings the error is the spread of the lag from climb to climb (wide on
27.09), not a bias that one global correction removes. D051 sets no threshold; this is the report A10 asks for.

The rows A1–G7 below keep their empty evidence column: they were not run one by one by hand; the table above is the
acceptance evidence of D051 A1–A15.

## Product owner's acceptance (2026-11-04)

After the automated run above no manual scenario of Phase 37 was left (A1–A15 each with its evidence; A10 is a
measurement without a threshold). The product owner closed the acceptance of Phase 37 on 2026-11-04. Recorded: D051
A1–A15 **PASS** (A10 measured); the rows A1–G7 of this plan **not run by hand** — their empty evidence column is not a
PASS of its own.

## A. Proposals
| # | Check | Expected result | Evidence |
|---|---|---|---|
| A1 | Project of 27.09 with no `Z`, import the FIT | no marks (D051 §3.2) | |
| A2 | Note the current `StartCorrection` in «Настройки FIT…»; set `Z` on one video | a mark for each climb whose start that video films, the current `StartCorrection` before the climb's start (D051 A1; 12 s only on a fresh calibration — checked in F0–F1); none on other videos | |
| A3 | Undo the `Z`; Redo; change the `Z` a second time | Undo removes the `Z` and its marks in one step; Redo returns the same marks (same time and value); moved and declined climbs unchanged (D051 A3) | |
| A4 | Set `Z` on the other videos | marks on every video with `Z`; none in Preview or in an export of a short range | |
| A5 | Import the same FIT again | no second mark for any climb (D051 A9) | |
| A6 | A new `Z` (1 s shift) on a video with marks | an unmoved `proposed` mark follows the proposal; a moved one stays on its frame | |
| A7 | Change the clock correction | no mark created or moved; `proposed` marks keep following `W_auto` | |
| A8 | Preconditions: (1) set the video's original `Z`; (2) save the project; (3) make sure the unmoved `proposed` mark has a record with the status `used` and the value = the current `StartCorrection` (in `fit-calibration.json`; the count in «Настройки FIT…» includes it). Then change the `Z` of the video so that the mark's `W_auto` falls outside the video → save; set the `Z` back | the mark is not drawn but stays active (not in «Отклонённые подъёмы»); the `used` count unchanged and its record keeps the value = its `StartCorrection` (D051 §5.8); with the `Z` back it is drawn again | |

## B. Moving, laying out, the choice of the video
| # | Check | Expected result | Evidence |
|---|---|---|---|
| B1 | Put the playhead 2 s after a mark, «Переместить метку к плейхеду» in the Inspector (the implemented way; no drag on the timeline — accepted by the product owner on 2026-11-03, `docs/PHASE37.md` §2) | the mark on the playhead's frame, kind `moved`; Undo returns it | |
| B2 | «Разметить трассу от метки» | the D048 route selection with `T` on the mark's frame; after the layout the route starts at `T` (D051 A2) | |
| B3 | Move `T` of the animation | the mark does not move; the effective start is the new `T` | |
| B4 | Delete the animation | the link is removed; the effective start is the mark's own position | |
| B5 | A climb with `W_auto` before the recording and `W_s` inside | the mark is `clamped` to the first frame and shown as such; a save does not raise the `used` count (D051 A14) | |
| B6 | Move the `clamped` mark → save; Undo → save | the count +1; after Undo no observation again (record `removed`) | |
| B7 | «Разметить трассу от метки» on a `clamped` mark → save | an observation by `T` appears; the mark's `positionChangedAt` unchanged | |
| B8 | A climb at the joint of two videos; two overlapping recordings; equal recording starts; a climb begun before the recording | the video by D051 §5.5.1; in the last case no mark (D051 A13) | |
| B9 | Clips at speed 0.5 and 2: «Разметить трассу от метки» | the mark on the same source frame; `positionChangedAt` unchanged | |
| B10 | A `clamped` mark → «Разметить трассу от метки» → save; Undo → save; Redo → save | the count +1, then −1 (record `removed`), then +1; the mark's `positionChangedAt` the same throughout | |

## C. Links
| # | Check | Expected result | Evidence |
|---|---|---|---|
| C1 | «Связать с FIT-подъёмом» on an unlinked animation | the list offers only active climbs without a link | |
| C2 | Link a second animation to a linked climb | refused with a message; the first link intact | |
| C3 | Link a linked animation to another climb | refused; the link intact | |
| C4 | «Снять FIT-связь» | the link removed, the animation unchanged; Undo returns it | |
| C5a | Copy a linked clip | allowed; nothing changes on the timeline | |
| C5b | Paste the copied linked clip while the original is on the timeline | refused with a message, nothing pasted (D051 §5.4, A12) | |
| C5c | Duplicate a linked clip | refused with a message, nothing added (D051 §5.4, A12) | |
| C5d | Cut a linked clip, then Paste it | pasted; it has no FIT link (the cut removed it); the climb can be linked again by «Связать с FIT-подъёмом» | |
| C5e | Split a linked clip | allowed; no refusal (see C9 for the link) | |
| C6 | A project without a FIT and a project with animations made before the import | everything works and exports by D048; no message of damage (D051 A11) | |
| C7 | Paste a clip with an ordinary D048 markup while a FIT-linked clip is on the timeline; and the other way round | both pasted (D051 A12) | |
| C8 | Delete on a selected mark | «Скрыть / Отклонить», one Undo step | |
| C9 | Split a linked clip before the animation, after its end, inside it | the link follows the markup (to the right part or staying); inside — the markup deleted by D048, the link removed | |
| C10 | Link a climb to an animation on a clip of another video (different `Z`) → save | one record; value = `W_s − (Z of the animation's video + source time of T + CC)`; changing the mark video's `Z` does not change it, changing the animation video's `Z` does (D051 A15) | |
| C11 | The animation's video has no `Z` | no observation; after setting its `Z` and saving — a record | |

## D. Decline and «Вернуть»
| # | Check | Expected result | Evidence |
|---|---|---|---|
| D1 | Decline an unlinked climb | the mark moves to «Отклонённые подъёмы»; one Undo step | |
| D2 | Decline a climb with a linked animation | the link removed; the animation works and exports as before (D051 A3) | |
| D3 | Undo D2 | the climb, the mark and the link back | |
| D4 | «Вернуть» after D2 | the mark back without a link, on its own position | |
| D5 | A new import / a new `Z` | the declined climb does not come back (D051 A3) | |
| D6 | Decline a climb that had no mark, then «Вернуть» | a new proposal if a video is chosen by D051 §5.5.1 | |

## E. Save, open, compatibility
| # | Check | Expected result | Evidence |
|---|---|---|---|
| E1 | Save, close, open | marks (video, kind), declined climbs and links as they were | |
| E2 | Open a v11 project (Phase 35) | opens without marks; saved as v12 | |
| E3 | Open a new project in an older build | refused as a newer version | |
| E4 | Damage `project.json` (two links on one climb) | the open is refused with a reason, no crash | |
| E5 | A crash → recovery | marks in the recovered project; the calibration file not changed by the recovery | |
| E6 | The FIT renamed (not available) | the project opens; marks kept; visible again after the FIT is relinked | |
| E7 | «Удалить медиа» of a video with marks (one of them declined) → save → close → open; then Undo | the project opens (no damage); the video's marks and the declined climbs' former marks on it are gone; Undo returns them (found and fixed in the final audit, `docs/PHASE37.md` §2) | |

## F. Calibration
| # | Check | Expected result | Evidence |
|---|---|---|---|
| F0 | A Windows user without a calibration file: open «Настройки FIT…» (with and without a training in the project) | the item available in both cases; Beta on by default, the estimate 12 s, 0 records | |
| F1 | Without turning anything on, save a project with marks | the file created, the `used` count raised (D051 A4) | |
| F2 | Save without a change | the same count; the file not rewritten (its modification time) | |
| F3 | Move a mark → save | the same count; the estimate may change (with `n ≥ 20`) | |
| F4 | Hide a mark (Delete) → save → Undo → save | the count back; no duplicate (D051 A8) | |
| F5 | Beta off → save; restart the application | the file unchanged, no record removed; new proposals with the last estimate; Beta still off after the restart (D051 A5, A6) | |
| F6 | Reset | asks first; then 0 records, `StartCorrection` 12 s | |
| F7 | Two running instances save different projects at the same time | both records in the file; the file not damaged | |
| F8 | Damage the calibration file by hand | set aside as `*.damaged`; the estimate 12 s; the project saves; Beta on | |
| F9 | Make the calibration file read-only | the project's save succeeds; a log entry; the next save retries | |
| F10 | After the estimate has changed, import again | new marks with the new value; earlier marks unmoved (D051 A7) | |
| F11 | The measurement of D051 A10 | the share of routes usable without a correction and the corrections per training, before and after the calibration — reported, no threshold | |

## G. Edge cases
| # | Check | Expected result | Evidence |
|---|---|---|---|
| G1 | A negative estimate (a test calibration) with `W_auto` after the recording | the mark `clamped` to the last frame | |
| G2 | C8 of 27.09 (3.6 s); C10 / C11 (11 s apart) | one mark per FIT climb; nothing merged or split | |
| G3 | A clip with another speed, trimmed, slipped | the marks' watch times unchanged | |
| G4 | An observation outside [−30 s, +90 s] | `excluded`, outside the window; back inside the bounds → `used` | |
| G5 | Undo / Redo of a long chain (10+ steps) mixing mark actions and D048 commands | every state restored exactly | |
| G6 | A phone slow-motion video | not supported (D051 §1.5): no `Z` set → no marks; with a `Z` set anyway the marks do not match the picture — expected, recorded as the limitation | |
| G7 | English interface | every new text translated | |
