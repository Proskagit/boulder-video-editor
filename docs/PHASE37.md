# Phase 37 — preliminary route starts from a COROS FIT and the global start calibration

*Decision: DECISIONS.md **D051** (approved by the product owner on 2026-11-03, with the refinements B1–B7, О1–О5, Q1 and
Q2 of the same day). Manual test plan: `docs/PHASE37_MANUAL_TEST_PLAN.md`. This document is the phase's record.*

**State (2026-11-04): implemented, merged and accepted.** Steps 37.1–37.6 implemented in `90950cc` on the branch
`feat/phase-37-fit-starts` and merged into `main` with PR #33 (`1640996`, 2026-11-03; CI green on the pull request and on
`main`); `project.json` v12. Step 37.7: the automated gates passed (§2); D051 A1–A15 automated on 2026-11-04 (PR #38,
`4717fca`) — all PASS, A10 measured; **accepted by the product owner on 2026-11-04** on that evidence; no manual scenario of Phase 37 remained, and none of the plan rows A1–G7 was run one by one by hand (`docs/PHASE37_MANUAL_TEST_PLAN.md`).

*Later (D056, 2026-11-09, PR #54; accepted by the owner): the marks are no longer drawn on the clips — the video clip shows
the route areas' start `T` instead. The marks, their calibration and their actions (lay out from the mark, move to the
playhead, decline — Delete —, link / unlink) stay in the Inspector's «FIT-метки» and the COROS menu; deleting a route area
never changes a mark. Where this record speaks of «the layer on the clips», that is the state of 2026-11-04.*

How it reached `main` (§5): the product owner decided on 2026-11-03 to start the implementation at once from
`feat/phase-coros-c1` `4070ffc`, not after Phase 35's second pull request as D051 and this record first said; PR #33
therefore carried Phase 35's commits (Steps 35.0–35.4) and Phase 37 together. The documents of Step 37.0 (`20326dc`,
branch `docs/phase-37`) reached `main` with the documentation synchronisation after PR #33. **Phase 36** (the session
summary and the YouTube chapters) is separate work and no part of Phase 37.

## 0. What Phase 37 builds on (checked at Step 37.0 in the code of `feat/phase-coros-c1` `4070ffc`; all of it in `main` since PR #33)

| D051 needs | In the code | For Phase 37 |
|---|---|---|
| The training in the project, `ClockCorrection`, `Z` | `Project.Training : TrainingReference` (FIT, SHA-256, `TrainingActivity`, `ClockCorrection`); `MediaAsset.RecordingZeroOverrideUtc`; `SetRecordingZeroOverrideCommand` (one Undo step); format v11 (`ProjectSerializer.TrainingVersion`) | used as is (v11, Phase 35) |
| `w = Z + s + ClockCorrection` and its inverse | `Core/Telemetry/TrainingAlignment` (`WatchTime`, `SourceTimeOf`, `ClimbsInSource`, `ClimbEndLag`) | the only mapping; `ClimbEndLag` untouched (D051 §2.1) |
| A climb's start by the one rule (D050 B8) | `WorkoutSegment.StartUtc` / `Interval` | `W_FITStart = segment.StartUtc` |
| The climb's identity | `ClimbIdentity(ClimbKey, ClimbNumber, ClimbStartUtc)` — in `Core/Telemetry/Research` | moved to `Core/Telemetry` (the research code refers to it) |
| The training's identity | `TrainingActivity(StartUtc, EndUtc, Manufacturer, Product)` (D049's activity identity) | the record key = `TrainingActivity` + `ClimbIdentity` |
| The D048 animation and `T` | `VideoClip.HoldMarkup.Animation : HoldAnimation` (`Mode = Duration`, `StartSourceTime`, `Duration` in timeline time); `HoldAnimationRule`; `LayOutIntroAndFinal(clipId, t)`; the orange segment `OrangeOf` (`TimelineEditService.RouteTiming.cs`) | `T` by the same rule that draws the strip; D048 unchanged |
| The layer on the clips | the D048 strip: `TimelineViewModel.Strip.cs`, `OverlayStripState` | the marks are drawn the same way (per clip, in source ticks) |
| Undo / Redo | `IUndoableCommand`, `CompositeCommand` (atomic), `IUndoRedoService`, `EditPlan` | every mark action a command; composites for multi-part actions |
| A per-user file with a lock | `RecentProjectsStore` (lock file, re-read, atomic write, `*.damaged`, newer format never overwritten); `AppPaths.ConfigFolderPath` | `FitCalibrationStore` on the same pattern; `AppPaths.FitCalibrationFile` |
| The save | `IProjectService.SaveAsync` / `SaveAsAsync` → `ProjectService.SaveToAsync`; autosave `AutosaveService` (the recovery file) | the observations are synchronised after a successful `SaveToAsync`; autosave never does it |
| Settings | no application settings screen (only `appsettings.json`); the COROS training drop-down on the toolbar (`ToolbarViewModel`) | «Настройки FIT…» there (B2) |
| «Now» | `TimeProvider` is not used yet | introduced (part of .NET 8, no package) for `positionChangedAt`, `firstSeenAt`, `valueChangedAt` |

## 1. Order of the steps

```
37.0 documents ─► 37.1 Core K3 ─► 37.2 store ─┐
                  37.3 model / format ─► 37.4a edits + Undo ─► 37.4b UI ─► 37.5 observations ─► 37.6 settings ─► 37.7 verification
```

37.1 and 37.3 are independent; 37.5 needs 37.2 and 37.4a; 37.6 needs 37.2. As implemented, each step's diff was shown to
the product owner, and Steps 37.1–37.6 were committed together as `90950cc` (the product owner allowed the work through
37.7 without a stop per step).

## 2. Steps

### 37.0 — Documents *(done: `20326dc` on `docs/phase-37`; in `main` with the documentation synchronisation after PR #33)*
- D051 in `DECISIONS.md`; this record; `docs/PHASE37_MANUAL_TEST_PLAN.md`; the ARCHITECTURE section; the state lines in
  `ROADMAP.md`, `docs/DEVELOPMENT_PLAN.md`, `progress.md`.
- **Acceptance:** the product owner approves the documents; no code changes.

### 37.1 — Core: the K3 calibration (pure functions) *(done, `90950cc`; 34 tests)*
- `Core/Telemetry/Calibration/`: `StartCalibration` with the constants of D051 §6.5 (`I` = 120 000 000, `N` = 100,
  `MIN_N` = 20, bounds [−300 000 000, +900 000 000], `C` = 50 000 000 ticks); `CalibrationKey(TrainingActivity,
  ClimbIdentity)` with an ordinal order; `CalibrationRecord` and `RecordStatus { Used, Excluded, Removed }`;
  `Huber.Target` (`S(μ)` by prefix sums, the `L` / `U` bisection, `⌊(L + U) / 2⌋`); `StartCalibration.Estimate`; a
  floor-division helper (C# `/` truncates).
- `ClimbIdentity` moves from `Research` to `Core/Telemetry` (namespace only).
- **Tests (T1, part of T1a):** the reference vectors of the K3 reference implementation embedded as constants (no Python
  in CI); the cases of D051 T1, including a record becoming `used` with an older `positionChangedAt` (99 / 100 newer
  records).
- **Acceptance:** `dotnet build` 0 / 0; Core.Tests green; equal to the reference to the tick.

### 37.2 — The store `fit-calibration.json` *(done, `90950cc`)*
- `Project/Persistence/FitCalibrationStore` beside `RecentProjectsStore`: format `AiVideoEditor.FitCalibration` v1; Beta,
  the initial value, the records, the derived estimate with its version; lock file `fit-calibration.lock`; re-read before
  every change; atomic write; damaged → `*.<time>.damaged`; a newer format neither read nor overwritten (the estimate `I`,
  logged); the file created on its first write.
- **Beta on by default (B6):** no file or no flag → on; off is the user's kept choice and removes nothing; reset separate.
- A Core interface for `GetEstimate`, `Synchronize`, `SetBeta`, `Reset`, `UsedCount`, and an in-memory cache
  `IStartCorrectionSource.Current` read synchronously by the commands (B3). A failed write is logged, never fails the
  project's save.
- **Tests (T2)**; every test that saves a project with a training gets a store in a temporary folder or a fake; a guard
  test checks that the real user file is neither created nor changed by a test run.

### 37.3 — The project model and format *(done, `90950cc`: `project.json` v12)*
- `FitMark`: `ClimbIdentity`, `W_auto`, the `StartCorrection` and the calibration version at creation, `MediaAssetId` (the
  video chosen by D051 §5.5.1, never reassigned), `FitMarkPosition { Kind: Proposed | Clamped | Moved, SourceTicks? }`,
  `PositionChangedAt`, `LinkedClipId?`; `DeclinedClimb` with the former mark's data (no link). In the file
  `"position": "proposed" | "clamped" | "moved"` and `"sourceTicks"`.
- The link is stored on the mark (`LinkedClipId`), not inside `HoldAnimation`: the D048 markup value and its commands are
  unchanged; «a climb → at most one animation» holds by structure, «an animation → at most one climb» by validation. The
  linked clip may show any video.
- Load validation (damaged): D051 T3's list.
- **Tests (T3)**; v10 / v11 tests unchanged. As implemented: `fitMarks` / `declinedClimbs` are written only when present; the
  persistence and edit tests that assert the current version expect 12 instead of 11.

### 37.4a — Edits, proposals, Undo *(done, `90950cc`)*
- The mark service (Timeline, beside `TimelineEditService`; the interface in Core):
  - `FitMarkProposals.For(...)` (pure, Core) — the choice of D051 §5.5.1; on the FIT import (no Undo step, D049 §11) and
    in `SetRecordingZeroOverride`: `CompositeCommand("Set Recording Start", [SetRecordingZeroOverrideCommand,
    AddFitMarksCommand])` in one `NotifyingCommand` (B3); `AddFitMarksCommand` holds the finished marks, so Redo restores
    the same values;
  - move, hide / decline (Delete), «Вернуть», «Связать с FIT-подъёмом», «Снять FIT-связь», «Разметить трассу от метки»
    (`LayOutIntroAndFinal` + the link in one `CompositeCommand`);
  - **the paste refusal** in `PasteInto` before `MakeRoom` (§3 B1): Paste and Duplicate;
  - **the link following its markup** in `EditPlan.Commit` (Split, Insert / Overwrite);
  - refusals with a message: a second mark, a second link either way, a link to a declined climb, a duplicate FIT markup.
- `EffectiveStart` (Core) and the watch time of D051 §4.5; «changes» by (video, source frame, `GetSourceGrid`), by ticks
  without a grid (О5).
- A change of a linked `T` (D048 commands) and the removal of a clip or an animation update the mark in the same
  composite step.
- **Tests (T4, T7).**
- As implemented: the rules live in `Core/Telemetry/FitMarkRules` (proposals, `ChooseVideo`, `EffectiveStart`, the
  observations, `Follow`), the commands in `Timeline/Commands/FitMarkCommands.cs` (`SetFitMarksCommand`; the following of
  every edit by `FitMarksFollowingCommand`, which wraps an edit while marks exist, instead of a change of `EditPlan.Commit`).
  Found and settled: a new `Z` no longer sets `positionChangedAt` of an unmoved mark; at a clip speed ≠ 1 «Разметить
  трассу от метки» keeps the position time explicitly (D051 §5.6); a mark `clamped` to the first frame stays unlinked by
  «Разметить трассу от метки», because D048 makes no animation with no time before `T` (the layout's own note is shown;
  D048 unchanged; accepted by the product owner).

### 37.4b — The interface of the marks *(done, `90950cc`; one accepted difference)*
- The layer on the clips (like the D048 strip), editor only; a `clamped` mark shown as such; a click selects a mark; Delete
  hides it.
- **Accepted difference (product owner, 2026-11-03):** a mark is moved by «Переместить метку к плейхеду» in the Inspector,
  not by a drag on the timeline as planned here. D051 §8.2 does not fix the way; the drag is a separate later improvement,
  not blocking Phase 37.
- The Inspector of a `Duration` animation: «Связать с FIT-подъёмом» (active climbs without a link), «Снять FIT-связь», the
  linked climb.
- «Отклонённые подъёмы» with «Вернуть» in the COROS training drop-down.
- Localization RU / EN, `LocalizationGuardTests`, the editing lock during an export.
- **Tests (UI.Tests)**; marks absent from Preview and export (Rendering / ExportEndToEnd).

### 37.5 — Observations on save *(done, `90950cc`: `FitCalibrationSaveSync`)*
- After a successful `SaveToAsync` (Save and Save As; not autosave or recovery): the usable marks of D051 §5.8 with `Z`
  on the video of their effective start → `c` by §4.5 → `Synchronize` off the UI thread; a failure logged.
- A `proposed` mark whose `W_auto` lies outside its video's recording after a new `Z` is still usable: it gives
  `c` = its frozen `StartCorrection` (D051 §5.8) — the synchronisation never asks whether the start falls inside the
  recording, only whether the effective-start video has `Z`.
- **Tests (T5).**

### 37.6 — «Настройки FIT…» *(done, `90950cc`)*
- An item of the COROS training drop-down, available with or without a training (О4); a small window after
  `AvaloniaProjectSettingsDialog`: Beta (on by default), the current `StartCorrection`, the number of `used` records, reset
  (asks first). If the drop-down does not open without a training today, the item is added to its «no training» form.
- **Tests (T6).**

### 37.7 — Verification and closeout *(done: D051 A1–A15 automated 2026-11-04; accepted by the product owner 2026-11-04)*
- Full and heavy suites, `--blame-hang`; the manual plan on the projects of 08.08 and 27.09 (A1–A15); the measurement
  A10 (no threshold); documents; the Phase 37 pull request.
- **Done (2026-11-03, on `feat/phase-37-fit-starts`):** Release / Debug `-warnaserror` 0 / 0; heavy (`AIVE_HEAVY_TESTS=1`)
  5229 passed, 0 failed, 0 skipped; three `--blame-hang` runs: the first with one UI.Tests hang
  (`PreviewTransformUiTests.Esc_during_a_crop_restores_crop_scale_and_position_exactly`, no Phase 37 code on its path; not
  reproduced in 10 further runs on this branch and 10 on `feat/phase-coros-c1`) and the known flaky tests
  `WasapiAudioOutputDeviceTests` and `TrainingLifecycleTests.Other_bytes…`, the second with the latter only, the third
  green. CI green on PR #33 and on `main` after the merge.
- **Final audit, two defects fixed with regression tests:** «Удалить медиа» of a video with marks left them on the removed
  video and the saved project was refused as damaged — the marks now follow the removal in the same Undo step; a new import
  of the same activity replaced the training, losing the marks and recreating declined climbs — it now keeps them
  (D051 §5.2, §5.3; another activity still replaces them by §5.11).
- `e8d7960` (in the same pull request) fixed two Phase 35 test issues seen on a CI checkout: the FIT resolve joins a
  running read (the race behind `TrainingLifecycleTests.Other_bytes…`), and the toolbar tooltip test ignores line ends.
- **Automated acceptance (2026-11-04, branch `test/phase-37-31-acceptance`):** D051 A1–A15 each with its evidence in
  `docs/PHASE37_MANUAL_TEST_PLAN.md`, «Automated acceptance of D051 A1–A15» — all PASS; A1 and A13 on the owner's real
  27.09 session (the local `Phase37RealSessionTests`: 8 of 16 videos with the owner's `Z`, 12 marks exactly on the
  climbs with a filmed start, none without `Z`, C1 none); the calibration items A4–A8, A14 through the real store on a
  temporary file (`FitCalibrationAcceptanceTests`); A10 measured on 27.09 and 08.08 (28 climbs: the estimate 12.0 → 11.98 s,
  routes within 2 s 9 of 28 before and after — no bias for a global correction to remove). New tests for A1 (no marks in
  the snapshot of Preview and export; a `clamped` mark named so) and A2 (deleting the linked animation). No defect found.
- **Accepted by the product owner on 2026-11-04** on the evidence of the automated run (merged with PR #38, `4717fca`);
  no manual scenario remained. The rows A1–G7 of the manual plan were not run one by one by hand.

## 3. The decisions of 2026-11-03 and their technical basis

### B1 + О1 — no pasted duplicate of a FIT markup
Rule: D051 §5.4. **Reliability of `Hold.Id` (checked in the code):**

| Question | Fact | Where |
|---|---|---|
| How an id is made | always `Guid.NewGuid()` when a hold is confirmed — a manual hold, a refined proposal, a route hold (manual or found). The deterministic proposal ids (`HoldProposalService.ProposalIdFromPrompt`) never reach a hold: the refinement makes a new id | `HoldMarkupViewModel.cs:1055`, `HoldProposalRefiner.cs:177`, `RouteHoldFinder.cs:126`, `HoldMarkupViewModel.Route.cs:212` |
| A contour edit | keeps the id | `TimelineEditService.HoldMarkup.cs:70` |
| Unique in one markup | `AddHolds` skips a repeat and `Guid.Empty`; on load `Guid.Empty` is damage; a repeat inside a markup is not checked on load (possible only by editing the file) | `TimelineEditService.HoldMarkup.cs:42`, `ProjectSerializer` |
| Unique in the project | **not guaranteed**: one id legitimately sits in several clips | — |
| Copy / Paste / Duplicate | `CloneClip` carries the `HoldMarkup` with the same ids | `TimelineEditService.cs` (`CloneClip`) |
| Split, `Interval` mode | the markup in both parts (D034 §6) — the same ids | `PlanSplitOverlays` |
| Split, `Duration` mode | the markup goes **only** to the part holding the animation's start; a cut inside deletes it (D048 §3.6) — no twins | `TimelineEditService.RouteTiming.cs` (`PlanSplitOverlays`) |
| Insert / Overwrite, `Duration` | the right part may receive the markup, the left loses it; the head trim by §3.8 | `TimelineEditService.RouteTiming.cs` (`TrimPlacedMarkup`) |
| The same route marked again | new ids | — |
| A paste from another project (the clipboard lives in `TimelineViewModel`) | the source project's ids | `TimelineViewModel.cs` (`Clipboard`) |

Conclusion: a shared id arises only from copying the same markup; an independent markup always gets new random ids — no
false refusal. A FIT markup is always in the `Duration` mode, where Split makes no twins. One shared id is enough and also
catches a copy edited afterwards. The application's hold identity needs no change.

**The link follows its markup** (found in the check): D048 moves a `Duration` markup to the right part (a new clip) on
Split / Insert / Overwrite or deletes it; so `EditPlan.Commit` moves the link, in the same Undo step, to the one clip of
the edit that received a markup with the same `Hold.Id`s, or removes it with the deleted markup (D051 §5.4).

### B2 + О4 — «Настройки FIT…»: §2 Step 37.6.

### B3 + О3 — marks of a `Z` in the same Undo step
`SetRecordingZeroOverride` (`TimelineEditService.cs`) today executes one `NotifyingCommand(SetRecordingZeroOverrideCommand)`;
with new marks it becomes the composite of Step 37.4a. Existing marks keep their kind on a new `Z` or clock correction;
`positionChangedAt` is not set (D051 §5.5, §5.6). A `proposed` mark whose `W_auto` falls outside its video after a new `Z`
is not drawn and stays active and usable; its observation equals its frozen `StartCorrection` and does not depend on `Z`
(D051 §5.8; T5 and the manual row A8).

### B4 + О2 — the choice of the video; the `clamped` mark
D051 §5.5.1 (`R(v)` by the frame grid `GetSourceGrid`, else `MediaAsset.Duration`). The checked scenarios:

| Scenario | Result |
|---|---|
| One video, `W_auto` and `W_s` inside | `proposed` |
| Two overlapping recordings, both hold the whole climb | the one starting earlier |
| Overlapping, one stops before `W_e` | the one holding the whole climb |
| Equal recording starts | the one holding the whole climb; else the longer; equal length → the smaller media id |
| Several candidates of different lengths | the longer wins only when the earlier criteria are equal |
| `W_auto` in v1, `W_s` in v2 (a joint) | v1, `proposed` (the left reference first, the owner's rule); fixed by a link to an animation on v2 |
| `W_auto` in a gap or before the recording, `W_s` inside | `clamped`, first frame |
| A negative estimate, `W_auto` after the recording, `W_s` inside | `clamped`, start of the last frame |
| `W_auto` and `W_s` outside every recording (C1 of 27.09; a climb begun before the recording) | no mark |
| A video without `Z` or without frames | not a candidate |
| A clip at speed ≠ 1, trimmed, slipped | the position unchanged (source); the timeline frame by the clip's rule |
| Slow / fast motion, time-lapse | not supported (Q1, D051 §1.5) |

**Q2 — a `clamped` mark linked without a change.** «Разметить трассу от метки» on a `clamped` mark puts `T` on the edge
frame and links it: the effective start (video, frame) is unchanged, so `positionChangedAt` stays `t0` (the mark's
creation), and the mark becomes a full observation (`c` by §4.5). At the next save a `used` (or `excluded`) record is
written with `positionChangedAt = t0` (or a `removed` record returns with its `firstSeenAt`). In the window it takes the
place of `t0`: with 100 newer `used` records it lies outside at once (the estimate unchanged), otherwise inside — as any
unmoved mark of that time; the estimate stays a function of the records only. Undo → `clamped` without a link → `removed`
at the save; Redo → `used` with the same `t0`. No contradiction.

**A link to an animation on another video.** The effective start is the frame `T` of the linked clip of video `u`:
`s_T` by the clip's rule (one conversion timeline → source), `W = Z_u + s_T + ClockCorrection` (one conversion source →
watch), `c = W_FITStart − W`. `Z_u` from `RecordingZeroEstimator.Resolve`; the mark's video `v`, `Z_v`, `W_auto`,
`StartCorrection` and `ClimbEndLag` do not enter; no `Z_u` → no observation, the record unchanged. The calibration receives
`c` in ticks under the key training + climb (independent of the video). Linking to another video always changes the
effective start (`positionChangedAt` set); a new `Z_u` changes the value only.

### Q1 — the limitation: slow and fast motion (D051 §1.5)
`MediaMetadata` has `FrameRate`, `AvgFrameRate`, `Duration` and no capture rate, so nothing reliable tells such a file
apart. Phase 37 does not detect or convert it; the user gives it no `Z`. A test pins the 1 : 1 model (it fails on purpose
if a conversion is ever added); the manual plan has a row.

### B5 — deleting a mark is hiding it (Delete = «Скрыть / Отклонить»). B6 — Beta on by default: §2 Step 37.2 and §4.
### B7 — D051 in `DECISIONS.md` at Step 37.0 on the Phase 37 branch, not in Phase 35's second pull request.

## 4. Automated tests (summary)
| Suite | What | Step |
|---|---|---|
| Core.Tests | K3 (T1), records (T1a), `EffectiveStart`, §4.5, §5.5.1, keys, the 1 : 1 model | 37.1, 37.4a |
| Project.Tests | the store (T2), format and validation (T3), the synchronisation on save (T5) | 37.2, 37.3, 37.5 |
| Timeline.Tests | the mark commands, Undo / Redo, refusals, the paste refusal, the link following its markup (T4, T7) | 37.4a |
| UI.Tests | the layer, the menus, the Inspector, the declined list, «Настройки FIT…», localization (T6) | 37.4b, 37.6 |
| Rendering / ExportEndToEnd | no mark in Preview or export | 37.4b |
| Telemetry.LocalTests (outside the solution) | the real FIT of 27.09 / 08.08: proposals for the climbs with a filmed start on videos with `Z` | 37.7 |

Beta on by default (B6): the file appears without an explicit action on the first save of a project with usable marks;
no test writes the real `%LOCALAPPDATA%` file (a temporary or fake store everywhere, a guard test); a damaged file set
aside → a new file with Beta on; a newer file → no collection, the window says so; the proposal tests set the estimate
explicitly (a fake `IStartCorrectionSource`).

## 5. Dependencies and how they were resolved
At Step 37.0 (2026-11-03) the plan was: Phase 35's pull requests 1 (Steps 35.0–35.2) and 2 (Steps 35.3–35.6) first, then
the Phase 37 branch from `main`. What happened the same day:
1. The product owner chose to start the implementation from `feat/phase-coros-c1` `4070ffc` at once (a deliberate
   deviation; D051's normative content unchanged).
2. PR #33 (`feat/phase-37-fit-starts`) carried Phase 35's commits — Steps 35.0–35.4 with the 35.3 research — and Phase 37;
   it was merged as `1640996`. Phase 35's two planned pull requests were never opened; its Steps 35.5 and 35.6 are not
   done (`docs/PHASE_COROS.md`).
3. The format: v11 is Phase 35's (Step 35.4), v12 Phase 37's — both reached `main` with PR #33 (D049 V1).
4. D051, this record and the manual plan reached `main` with the documentation synchronisation after PR #33, together with
   the numbering of `docs/phase-numbering`.
5. Phase 36 stays separate.

## 6. Discrepancies found while preparing the phase, and their state
1. `docs/PHASE_COROS.md` (the 35.3 state lines; the result of 35.3d missing) — resolved by the documentation
   synchronisation after PR #33 (`docs/PHASE_COROS.md` §4, §10, §12).
2. D050, the table of steps: the acceptance of 35.3 «≥ 12 of the 14 typical files» is the earlier wording, superseded by
   the amendment B6 (≥ 11 of 13) — marked as superseded in `docs/PHASE_COROS.md` §4; D050's own text is not rewritten.
3. `ProjectSerializer.CurrentFormatVersion`: the doc comment listed the versions up to 8 — completed with Step 35.5
   (versions 9–13).
4. `ROADMAP.md`, `docs/DEVELOPMENT_PLAN.md`, `progress.md` without Phase 35's state — resolved by the synchronisation.
5. `ClimbIdentity` moved from `Core/Telemetry/Research` to `Core/Telemetry` in Step 37.1 — done.
