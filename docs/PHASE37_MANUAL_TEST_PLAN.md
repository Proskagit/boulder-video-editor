# Phase 37 — Preliminary route starts from a COROS FIT and the global start calibration: manual test plan

Decision: DECISIONS.md D051 (approved 2026-11-03). Record of the phase: `docs/PHASE37.md`. **State (2026-11-03): not run.**
The phase is implemented and merged into `main` with PR #33 (`1640996`); no row of this plan has been run yet, so the
product owner's acceptance of Phase 37 is open (Step 37.7). The acceptance items of D051 §10 are cited as «D051 A1»…«D051 A15»; the rows of this plan have their own
numbers.

Environment: a build of `main` at `1640996` (the merge of PR #33) or later; the projects of 08.08.2026 and 27.09.2026 with their FIT and videos (personal
data, never committed); Russian interface unless a row says otherwise, an English pass for the new texts. Before part F:
a copy of `%LOCALAPPDATA%\AiVideoEditor\config\fit-calibration.json`, if it exists, to restore it afterwards. **Beta (the
collection of observations) is on** for every row — the default (D051 §6.2); check it in «Настройки FIT…» before the run.
A row that turns it off (F5) turns it on again before the next row.

Legend: **PASS** — the criterion is met; **FAIL** — a defect; **PARTIAL** — part met, the rest recorded; **NOT RUN** —
not run, with a reason.

Notation: `W_s` the climb's FIT start, `W_auto = W_s − StartCorrection`, `Z` a video's recording zero, `CC` the training's
clock correction; mark kinds `proposed` / `clamped` / `moved` (D051 §5.3).

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
