# Phase 35 — import of COROS climbing trainings and their synchronisation with the video

*Formerly «Phase Coros». Numbering confirmed by the product owner on 2026-10-28 (`docs/DEVELOPMENT_PLAN.md` on
`docs/phase-numbering`, "Phase and step numbering"): this is **Phase 35**; its steps are **35.0–35.6** — the old labels C0–C5
are kept below in brackets and in the commit messages. The phase's decision **D050** and its format / time model **D049**
were approved by the product owner on 2026-10-29 (`DECISIONS.md`, Step 35.0). The phase is complete only when merged into
`main` and accepted by the product owner — the local commits of 35.1 and 35.2 complete nothing. Where this record says
«session-only», D049 prevails from Step 35.4 on. Manual test plan: `docs/PHASE35_MANUAL_TEST_PLAN.md`.*

Branches `feat/phase-coros-c0` (C0) and `feat/phase-coros-c1` (C1, on top of C0), from `origin/main` `1e58885` (Phases 30 / 31, hold tracking and D048 with its stages 2–3 merged;
`project.json` v10 in the code). C0 was written on `feat/phase-coros` from `e28a9d3` (`32662ee`) and
carried over unchanged (2026-10-28); the old branch is kept. This document is the phase's record until its merge.
`DECISIONS.md` carries the phase's decisions D049 / D050 on this branch (Step 35.0, 2026-10-29); `ROADMAP.md` and
`progress.md` are not edited on this branch (their state lines are on `docs/phase-numbering`). Continues D042 §6 (R3 → F)
with real files.

## 1. What the real export contains (R3 on real data)

Source: `D:\Времянка для монтажа\Боулдер 27.09.2026\Болдеринг20260927100239.fit` (54 003 bytes), the COROS app's summary
screenshot `5386469952690265888.jpg` and 16 phone videos of the same session (`video_20260927_HHMMSS.mp4`). Read with
an independent stdlib Python FIT decoder (own CRC check) and with the app's parser (`tests/Telemetry.LocalTests`).
Earlier notes: `VideosForTest/R3_COROS_preliminary/notes.md` (local, not committed) — confirmed.

| Fact | Value |
|---|---|
| Format | FIT, protocol 2.0, profile 21.158, 14-byte header; header and file CRC valid |
| Device | `file_id` / `device_info`: **COROS APEX 4 42mm** (manufacturer 294, product 814) — not a Pace 4 (D042 §6 named the Pace 4) |
| `file_id.time_created` | 2026-10-20 12:28:18Z — the **export** time, not the training's |
| Sport | `session.sport` 31 / `sub_sport` 69 — rock climbing / bouldering |
| Session | 07:02:39Z – 08:39:14Z; elapsed 5 794.34 s, timer 5 768.2 s (the app's 1:36:08); HR average 130, maximum 169 |
| Time zone | `activity.local_timestamp − timestamp` = +10 800 s (UTC+3): the file states the local offset |
| Timer events | start 07:02:39, stop 08:18:17, start 08:18:45, stop 08:39:14 — one 28 s pause |
| Records | 5 769 at 1 s (one 28 s gap = the pause), monotonic; heart rate in 5 766 (85–169 bpm), altitude in 5 755, field 42 (activity type); **no GPS** |
| Splits (message 312) | 33: type 9 climb (14), 10 rest (19); start / end in whole seconds, elapsed in ms; fields 15 / 16 = the split's average / maximum HR (equal to the records' in 30 of 33 splits) |
| Grade (6C / 7A), flash | not found in any standard field; no developer field descriptions in the file |
| Result (onsight / attempt) | split field 71 (3 or 2 or empty) — **not confirmed**: six 3s and three 2s against five onsights and four attempts in the app |

Video side: 3840 × 2160 HEVC ≈ 60 fps SDR, one vertical (preliminary notes). **Recording time, measured 2026-10-29**
(ffprobe on all 16 videos): `creation_time` is present in the container format, the video and the audio stream (equal),
written as ISO-8601 with `Z`, whole seconds. Compared with the file-name time (read as UTC+3 local time) plus the
duration, it is +0.1…+2.2 s for 14 files, +181.7 s for `video_20260927_103026.mp4` and +65.0 s for
`video_20260927_104651.mp4` (unexplained). This agrees with «a UTC instant near the end of the recording» on this
phone, but **neither its UTC semantics nor the moment it marks is established**; the tag's presence does not confirm any
alignment (Step 35.3). Not established either: the clock difference between the phone and the watch.

## 2. What the data is good for in the editor

- **Heart rate at 1 s** for an overlay (D042 "Recorded later": an animated heart icon with the BPM, not a graph).
- **The training's structure**: 14 climbs with exact durations and times — the basis for finding the attempts in the
  videos (the later automatic rough cut) and for numbering routes.
- Not available from the file: the grade, flash, a confirmed result — they must come from the user (D042: never guessed).

## 3. Specification (Phase 35)

1. **Import**: a COROS FIT activity export; a foreign manufacturer, a non-activity FIT file, a damaged, cut or empty file
   and a file without heart rate are refused with a reason (`TrainingFileError`). Implausible or repeated samples are
   left out with a warning. Device model is not restricted (APEX 4 and Pace 4 write the same FIT messages) —
   confirmed by the product owner (2026-10-26), see §6.
2. **Time model** (D049 §1–§2): four bases — **W** watch time (UTC by the watch), **C** camera time (UTC by the phone),
   **L** the phone's local wall time (file name, never stored), **S** source time (D009); 100 ns ticks, no floating
   point; the watch's local offset only for display (and as a hypothesis in Step 35.3's file-name check); the system
   time zone is never used.
3. **Synchronisation** (A1, D049 §5–§6): per **media file**, by its source time. Watch time of timeline frame `n`:
   `w(n) = Z(M) + s(n) + ClockCorrection`, where `s(n)` is the clip's source time, `Z(M)` the recording zero of the file
   in C (a manual override, or an automatic zero only once Step 35.3's rule is approved, otherwise **unknown** — no heart
   rate, nothing guessed) and `ClockCorrection = W − C`, one per training. Move, ripple, trim, split, slip, speed and
   duplicate never change what a picture maps to. The correction and the override are one Undo step each.
4. **Heart rate at an instant**: the latest sample at or before it, at most 2 s old, inside the recording and outside the
   timer's pauses; otherwise nothing is shown (no interpolation, no stale value).
5. **Overlay** (Step 35.5): heart icon + BPM through the one composition path, Preview = export. An HR overlay without
   data gives an export warning that must be confirmed; an export without an HR overlay never depends on the training.
6. **Persistence** (P2 / S1, D049 §7–§9): the project stores a reference to the FIT (paths, size, SHA-256, activity
   identity) and its clock correction; a video may store its recording-zero override (C); the metadata stores the raw
   `creation_time` tag with its origin and a probe marker. The parsed telemetry is never stored. A missing, changed or
   unreadable FIT never blocks opening, editing or an export without an HR overlay. The format change (expected v11)
   reaches `main` only with Phase 35's second pull request.
7. **Probing the recording time** (RP1 / M2, D049 §4): online videos without the probe marker are probed in the
   background at most once per session; a failure is not retried in the session; the probe never makes the project
   dirty, never asks to save, never creates an Undo step; the marker is saved with the next ordinary save.

## 4. Steps and acceptance

| Step | Content | Acceptance | State |
|---|---|---|---|
| **35.0** | D049 (format and time model), D050 (phase), this plan, the manual test plan, the ARCHITECTURE section | approved by the product owner | **D049 / D050 approved 2026-10-29**; documents written on this branch; not merged |
| **35.1** (C0) | Core: `Telemetry/FitFile` (FIT reader: header / file CRC, both byte orders, compressed timestamps, developer fields skipped), `CorosWorkoutParser`, `ClimbingWorkout` model, `WorkoutTimeline` (HR at an instant, splits in a range), `WorkoutVideoAlignment`. Tests in `Core.Tests/Telemetry` on synthetic FIT files; `tests/Telemetry.LocalTests` (outside the solution) on the real export | the real export parses to the facts of §1 (14 climbs, 5 766 HR samples, the pause, +3 h); every refusal reason covered; no change of `project.json`, UI or existing code — except the rule of `LocalizationGuardTests` and the related localization fix of `FitFile` (§7, `f12c5e6`) | **implemented** (`4636dd5`, `f12c5e6`, local branch `feat/phase-coros-c0`); build and tests run on Windows (see §7); not merged |
| **35.2** (C1) | Import entry: `ITrainingImportService` (Core) + `TrainingImportService` (Media; 16 MiB limit, missing / unreadable file); «Тренировка COROS…» on the toolbar with a `.fit` picker (`TrainingImportWorkflow`); localized refusal texts and warnings (`TrainingText`); the workout held in `TrainingSessionService` (not saved) | importing the real file shows device, date, local time, 14 climbs; each refusal reason has a Russian / English text; nothing persisted | **implemented** (`934bbf6`, local branch `feat/phase-coros-c1`); **manual acceptance A1–A11 PASS (2026-10-29, §9)**, no defect found; not merged |
| 35.3 (C2) | Recording time: the probe and its marker (D049 §4), the raw tag with its origin (§2–§3); research on the real session — what the tag marks, whether it is a true C instant, the C ↔ W difference per climb (visible start vs split), the outliers; a proposed automatic-zero rule with its tolerance `τ` and device scope. Sub-steps 35.3a–e, the Debug research window and the study file: D050 amendment B5–B9, §10 below | (D050 B6) `|r_i| ≤ τ` (leave-one-out, `τ = 2 s` working value) for ≥ 11 of the 13 filmed climbs C2–C14, every one of the 13 explained, no drift beyond `τ / 2`, the 7 videos without a climb as the independent check of the tag; approved by the owner; until then every zero «override» or «unknown» (manual plan part B) | the probe **implemented** (`02ded40`); 35.3a, 35.3b1, 35.3b2a and the tool 35.3R1 **implemented** (R1–R12 PASS); the reference marking 35.3R2 **done and frozen** by the owner; 35.3b2b–d, 35.3c (beyond 35.3R1), 35.3d and 35.3e not started (§10); the owner's descriptive research of two trainings in §11; new-version members in the branch only |
| 35.4 (C3) | Persistence and alignment: the format change (expected v11), `Project.Training`, the FIT life cycle (D049 §9), the training's clock correction (a setting of the COROS training drop-down) and the per-video zero override Z (in the clip's Inspector: the playhead's frame matched to the end of climb N — the default, less the end lag of 4.6 s — or to its start; D050 amendment of 2026-11-02) — one Undo step each, video relink with the override (ID1, §8), the zero states «override» / «unknown» (the «automatic» state after 35.3's rule), the mapping (§6) and its read-only readout at the playhead in the Inspector; 35.2's «not saved» texts revised (D050 amendment of 2026-11-01) | D049 §13 tests; manual plan part C | **implemented** (`e15d7af`; the match point by the D050 amendment of 2026-11-02): automated tests pass; manual rows C1–C14 not run; format change in the branch only, in `main` with the second pull request |
| 35.5 (C4) | Heart-rate overlay: heart icon + BPM, a few looks / positions, through the one composition path; the export warning `HeartRateUnavailable` | Preview = export at sampled frames; pauses, gaps and an unknown zero show nothing; the warning exactly when data is missing, confirmation required (manual plan part D) | planned |
| 35.6 (C5) | Verification and closeout: full and heavy suites, `--blame-hang`, the manual plan on the real session (video + export), documents | product owner's acceptance (manual plan part E) | planned |

## 5. Dependencies and possible conflicts

- C0 adds new files only (`src/Core/Telemetry/*`, `tests/Core.Tests/Telemetry/*`, `tests/Telemetry.LocalTests/*`,
  this document): carried onto `1e58885` without a conflict. Core.Tests picks the new tests up by its glob.
- `tests/Telemetry.LocalTests` is outside `AiVideoEditor.sln` like `tests/Vision.ModelTests`: the CI skip gate
  (`Assert-TestResults.ps1`) and the expected TRX count are unaffected; the personal FIT file is never committed.
- C1 touches the localization resources, the import entry of the toolbar and the DI registrations — Phase 31 is merged
  into `1e58885`, so C1 starts from its state; any D048 implementation branch editing the same files is a later merge.
- Format (D049 §7, V1): v10 is D048's (merged). Phase 35's change takes the next free version — expected v11 — and
  reaches `main` only with the second pull request (Steps 35.3–35.6); the first pull request (35.0–35.2) leaves v10.
  The number is fixed at merge: Phase 36 or hold tracking take later versions, or this one is renumbered at the rebase
  if one of them merges a format change first.
- Pull requests (D050): PR 1 = Steps 35.0–35.2 (after or with `docs/phase-numbering`); PR 2 = Steps 35.3–35.6.
- C4 uses the overlay / animation path of Phase 27 (D043) and the composition painter.

## 6. Questions for the product owner

1. The device: the real export is from a **COROS APEX 4**, the plan says Pace 4. Accept any COROS activity FIT (current
   C0 behaviour), or restrict to named models? — **Answered (2026-10-26)**: the watch is a COROS APEX 4 42mm; no model
   restriction — any valid COROS activity FIT whose content meets the parser's requirements is accepted.
2. Persistence (C2 / C3): saving the video's `creation_time` and the per-clip alignment needs `project.json` v10. Approve,
   or keep the training session-only for the first version? — **Answered (2026-10-28)**: the FIT import of the first
   version lives in the current session only; nothing is written to `project.json`. — **Superseded (2026-10-29, D049)**:
   the training is saved in the project from Step 35.4 (P2); the import stays session-only until then.
3. The route result: confirm whether field 71 is wanted at all (it is not reliable on the one export); grade / result
   then come from the user. — **Answered (2026-10-28)**: the route's grade and the attempt's result are set by hand in
   the overlay; COROS support did not confirm a way to get them from the FIT or through MCP. Field 71 stays unread.
4. The persistence, time model and life cycle (2026-10-29) — **Answered, D049 approved**: P2, S1, A1, V1, one training per
   project; override in camera time C (Q-OV); the watch's offset only as a hypothesis for the file-name check (Q-TZ);
   the override kept on a relink of the same file (Q-RL / ID1: size, duration and tag equal; otherwise asked, default
   «no»); the raw tag with its origin (Q-RT, RT1); a probe marker, at most one probe per asset per session, never dirty
   (Q-RP, RP1, M2); a confirmed export warning; replace asks and resets the correction, remove keeps the overrides.
   Open until Step 35.3's measurement: what the tag marks, its UTC semantics, the automatic-zero rule, `τ`, the outliers.
5. Step 35.3's research (2026-11-01) — **Answered, D050 amendment B5–B9**: the fixed set is the whole folder (16 videos,
   the FIT with 14 climbs); a Debug-only research window that never changes `project.json`, the study in a separate file;
   `τ = 2 s` as a working value; a climb starts when the feet leave the floor; ≥ 11 of the 13 filmed climbs within `τ`,
   all 13 explained; the 7 videos without a climb only as the check of the tag; one estimator and one mapping
   (`TrainingAlignment` with its inverse); markers with number and interval, no result; **the FIT track on the editing
   timeline is not part of Phase 35** (a later feature: a projection onto the clips' source time).
6. After the research of two trainings (2026-11-02) — **Answered**: B2's match point is the end of the climb by default,
   with a constant end lag of 4.6 s, the start as the alternative (D050 amendment of 2026-11-02); 35.3d is prepared and
   made with B6 and `τ = 2 s` unchanged — a negative result is an admissible result; the FIT events as editable items, the
   manual adding of attempts missing from the FIT and their transfer to the overlay stay out of Phase 35 (B9) and come
   after its second pull request with a decision of their own; 35.3R2 is frozen.

## 7. Verification state

- The FIT facts of §1: independent Python decoder on the real file (CRC verified).
- The synthetic fixture of `CorosWorkoutParserTests`: re-created in Python and decoded by the reference decoder — CRC
  valid, 52 records, compressed timestamps reconstructed, the expected values of the tests confirmed.
- C0 on Windows (2026-10-26, .NET SDK 8.0.424, worktree from `e28a9d3`), no change to the code of the cloud session:
  `dotnet build AiVideoEditor.sln` 0 warnings / 0 errors;
  `dotnet test tests/Core.Tests/Core.Tests.csproj --filter "FullyQualifiedName~Telemetry"` 40 / 0 / 0 (passed / failed /
  skipped); `dotnet test tests/Telemetry.LocalTests/Telemetry.LocalTests.csproj` on the real export 6 / 0 / 0. The full
  suite was not run (C0 adds new files only).
- C0 carried onto `1e58885` (2026-10-28, `git cherry-pick --no-commit 32662ee`, no conflict, code unchanged): build 0 / 0;
  Core Telemetry 40 / 0 / 0; Telemetry.LocalTests on the real export 6 / 0 / 0.
- C0 follow-up (`f12c5e6`): the full UI.Tests run showed `LocalizationGuardTests` failing on the English details of
  `TrainingFileException` (C0 had run the Telemetry tests only). The guard's internal-exception rule now names
  `TrainingFile`; `FitFile` throws the exception directly. No behaviour change.
- C1 (2026-10-28, branch `feat/phase-coros-c1`): `dotnet build AiVideoEditor.sln` 0 / 0; `dotnet test AiVideoEditor.sln` —
  Core 1 444, Project 543, Timeline 1 015, UI 964 (18 new in `TrainingImportTests`), Rendering 178, Export 122, Vision 24,
  ExportEndToEnd 165 + 2 skipped (as on `main`), Video 362 + 1 failed in the parallel run:
  `WasapiAudioOutputDeviceTests.Clock_CountsPlayedFrames_…` (the real audio device under the load of the full run; passes
  alone, 2 / 0; untouched by C1). One existing test updated for the new button: `ProjectSettingsViewBindingTests` (the
  import group is now «Импорт медиа», «Тренировка COROS…»). `Telemetry.LocalTests` on the real export 7 / 0 / 0 (new:
  the import from disk and the Russian summary «COROS APEX 4 42mm, 27.09.2026 10:02–11:39 (UTC+03:00), 14 подходов»).
  The manual acceptance (§9) was not part of this run.
- Step 35.2 manual acceptance (2026-10-29, build of `feat/phase-coros-c1` at `d855c3a`, the real export of 2026-09-27):
  **A1–A11 PASS, no defect found** — the record in §9.

## 8. Step 35.2 (C1) — the import into the session (as implemented)

- **Entry**: the toolbar button «Тренировка COROS…» right after «Импорт медиа»; one `.fit` file per import. Disabled during
  an export (the editing lock), as the other commands.
- **Reading** (`Media/TrainingImportService`): the file is checked (exists, ≤ 16 MiB), read whole and parsed off the UI
  thread by `CorosWorkoutParser`. A missing file, an I/O error (locked, no access) and an oversized file are refusals of
  their own (`TrainingFileError.FileNotFound`, `Unreadable`, `TooLarge`), next to the parser's reasons.
- **Result in the status bar**: «Тренировка импортирована: COROS APEX 4 42mm, 27.09.2026 10:02–11:39 (UTC+03:00), 14
  подходов», then what was left out («; пропущено 2 недостоверных значения пульса»). Times are the watch's local time from
  the file's offset; without a usable offset they are UTC and say so. A refusal: «Тренировка не импортирована: <reason>»;
  the training already held stays.
- **Session only** (§6.2; superseded from Step 35.4 by D049): `UI/Services/TrainingSessionService` holds the workout and its file path in memory. Nothing is
  added to `Project` or `project.json` (tested: a project saved after an import has no trace of it). The training belongs to
  the project it was imported into, like a media import (D027 §7): opening / creating / recovering another project drops it,
  and a training read while another project became current is not kept. A new import replaces it.
- **Tooltip** of the button: what it does, or — once a training is held — its summary, the file name and «Не сохраняется в
  проект».
- Not in C1: any use of the workout (alignment, overlay), a «remove training» command, a shortcut.

## 9. Step 35.2 (C1) — manual acceptance (product owner)

*The record of 2026-10-29; part A of `docs/PHASE35_MANUAL_TEST_PLAN.md` refers to it. A8's expectation («no trace in
`project.json`») holds for Step 35.2's version only: from Step 35.4 the training is saved (D049).*

Build of `feat/phase-coros-c1`; the real export of 2026-09-27 and any other file at hand. Russian interface.

| # | Action | Expected |
|---|---|---|
| A1 | Toolbar: hover «Тренировка COROS…» | tooltip: import a COROS training (.fit), kept in this session only, not saved in the project |
| A2 | Click it, cancel the dialog | nothing changes, no status message |
| A3 | Click it, pick `Болдеринг20260927100239.fit` | dialog filter «Тренировки FIT» (`*.fit`), one file only; status «Тренировка импортирована: COROS APEX 4 42mm, 27.09.2026 10:02–11:39 (UTC+03:00), 14 подходов» |
| A4 | Hover the button again | the same summary, the file name, «Не сохраняется в проект…» |
| A5 | Pick a non-FIT file renamed to `.fit` (e.g. a `.txt`) | «Тренировка не импортирована: это не FIT-файл.»; the tooltip still shows the 27.09 training |
| A6 | Pick a copy of the real FIT with a few bytes changed in the middle (any hex editor) | «… файл повреждён: не сходится контрольная сумма.» |
| A7 | Open the real FIT in another program that locks it (or pick it, then delete / move it before confirming, if possible) | a refusal with its reason (не удалось прочитать / не найден), no crash |
| A8 | Save the project, then open `project.json` | no trace of the training (no device, heart rate, file path) |
| A9 | New project (or Open another) | the tooltip is back to «Импортировать тренировку…»: the training was dropped with the project |
| A10 | Start an export and look at the toolbar while it runs | «Тренировка COROS…» disabled like the other commands |
| A11 | Switch the interface to English (`appsettings.json`: `App.Language` = `en`) and repeat A3 | «Training imported: COROS APEX 4 42mm, … 14 climbs» (dates in the system's format) |

**Result (2026-10-29): A1–A11 PASS; no defect found.** Build of `feat/phase-coros-c1` at `d855c3a` (Debug, 0 / 0),
the real export `Болдеринг20260927100239.fit`.

| # | Result | Checked by / evidence |
|---|---|---|
| A1 | PASS | Claude, through the app's interface: the tooltip as expected |
| A2 | PASS | Claude, through the interface: after Cancel the status stays «Готово.»; the log has only «file picker was cancelled» |
| A3 | PASS | Claude, through the interface: dialog «Импорт тренировки COROS», filter «Тренировки FIT (*.fit)» (only the `.fit` shown among 16 videos), a Ctrl+click replaces the selection (one file only); status exactly «Тренировка импортирована: COROS APEX 4 42mm, 27.09.2026 10:02–11:39 (UTC+03:00), 14 подходов» |
| A4 | PASS | Claude, through the interface: the summary, the file name and «Не сохраняется в проект. Нажмите, чтобы импортировать другую.» |
| A5 | PASS | Claude, through the interface: «Тренировка не импортирована: это не FIT-файл.»; the tooltip still shows the 27.09 training |
| A6 | PASS | Claude, through the interface (4 bytes changed in the middle of a copy): «…файл повреждён: не сходится контрольная сумма.» |
| A7 | PASS | Claude, through the interface (the file held open exclusively by another process): «…файл не удалось прочитать (он занят другой программой или нет доступа).», no crash. A file deleted after it was picked is refused by the Windows dialog itself («Файл не найден») before the app gets it; the app's own «файл не найден» is covered by the automated tests |
| A8 | PASS | Product owner: the saved project's `project.json` checked — no data of the COROS training found by the checked signs |
| A9 | PASS | Claude, through the interface («Создать»): status «Создан новый проект.», the tooltip back to «Импортировать тренировку COROS (.fit)…» |
| A10 | PASS | Claude, through the interface: during an export «Тренировка COROS…» dimmed with the other commands, a click opens nothing; enabled again after the export is cancelled |
| A11 | PASS | Product owner (screenshot): English interface, status «Training imported: COROS APEX 4 42mm, 27.09.2026 10:02–11:39 (UTC+03:00), 14 climbs» |

## 10. Step 35.3 — the research plan (D050 amendment B5–B9, 2026-11-01)

*Agreed with the product owner on 2026-11-01; partly implemented (the state below). Each sub-step's implementation is
agreed separately. D049 is unchanged: nothing of the research enters `project.json`; the automatic zero is computed,
never saved.*

**Fixed verification set** — the folder `D:\Времянка для монтажа\Боулдер 27.09.2026`: the FIT (14 climbs) and all 16
videos; frozen with a hash before the evaluation; never committed. The climbs and the videos as placed by the file names
at a zero clock correction (`docs/FEATURE_SESSION_SUMMARY_AND_YOUTUBE_CHAPTERS.md` §11.3, branch
`docs/session-summary-youtube-chapters`; a starting point, not a measurement):

| Group | Members | Role in the research |
|---|---|---|
| Filmed climbs | C2 `100628`, C3 `101034`, C4 `101712`, C5 `103026`, C6 `103744`, C7 `105438`, C8–C11 `110133`, C12–C13 `111424`, C14 `112606` | the 13 cases of the matching statistics and the acceptance (≥ 11 of 13 within `τ`, all 13 explained) |
| Not filmed | C1 (10:02:39, before the first video at 10:04:05) | reported, not counted |
| Videos without a climb | `100405`, `101229`, `102533`, `104110`, `104651`, `105713`, `112854` | not in the matching statistics; the independent check of the `creation_time` semantics and of the file time ↔ name consistency |
| Atypical tag | `103026` (+181.7 s, holds C5), `104651` (+65.0 s, no climb) | analysed separately; never dropped without an explanation |

Known watch-side doubts to be explained, not hidden: C8 lasts 3.6 s; C10 and C11 are 11 s apart (one attempt or two);
two «rest» splits with 1 m of ascent; C14 ends 0.2 s before its video's end.

**Two separate tasks** (B8): *recording synchronisation* — the zero `Z` of each file (C) and the clock correction
`W − C`; *attempt matching* — which FIT climb a climb marked on a video is. Match states: not checked / confirmed /
rejected / not matched; residual classes: within `τ` / outlier / not computed.

**Measures** (B6), each reported on its own: the clock-correction estimate `K̂ = median(k_i)`,
`k_i = W_split_i − (Z(M_i) + s_i)`, with MAD and count; the leave-one-out residual `r_i = k_i − median(k_j, j ≠ i)` in ms
and frames; the drift (median pairwise slope of `k_i` over `W_split_i` × the span; beyond `τ / 2` → stop and ask);
the outliers (`|r_i| > τ`), each with its explanation. `τ = 2 s` (working value, B7). The start of a climb on a video:
the frame where the feet leave the floor and the physical climb begins.

**Sub-steps** (state on 2026-11-02: 35.3a done — `f58a387`; 35.3b partly — see below; 35.3c only as the marking mode of
35.3R1; 35.3d and 35.3e not started):

| Sub-step | Content | Depends on |
|---|---|---|
| 35.3a | Shared Core: `TrainingAlignment` as the one mapping with its inverse (`WorkoutVideoAlignment` removed or internal, tests moved); one rule for a climb's boundaries; the tag parser (D049 §3); the zero estimator (candidates from the tag and the duration, the file-name hypothesis as a check only) — no user-visible change | — |
| 35.3b | The study model and file (`*.syncstudy.json`), the matching proposals without the tag-derived zero (order, durations, a coarse file-name window), the statistics, the JSON / CSV export | 35.3a |
| 35.3c | The Debug window «Проверка синхронизации COROS»: the W axis, the training lane with the numbered climb intervals, one lane per video, the panels «Несопоставленные попытки», outliers, statistics, the check flow (pick a climb → the video at the predicted frame → mark the start → Δ in ms / frames → confirm / reject) | 35.3a–b |
| 35.3d | Marking of the fixed set, the measurement report, the proposed rule with `τ` and its device scope → the owner's decision | 35.3c |
| 35.3e | The «automatic» zero state in the product (D049 §5), computed by the same estimator | the approved rule |

Step 35.5 depends on Step 35.4 only and may run alongside 35.3d.

**Steps of 35.3b as agreed** (they keep their numbers): 35.3b1 the study model, file, revisions and set identity
(`adb9600`); 35.3b2a the model extensions and the evaluation admission (`be9e0f6`); 35.3b2b the matching proposals and
their ranking; 35.3b2c the statistics, B6's statuses, the tag check and the per-video sensitivity; 35.3b2d the report, the
stored evaluation records and the JSON / CSV export. Done: 35.3b1, 35.3b2a, 35.3b2c (the evaluation in Core,
`StudyEvaluator` with `EvaluationProtocol.B6`, synthetic tests); not started: 35.3b2d. **35.3b2b is deferred** — not
cancelled — to the later work on the FIT events (D050 amendment of 2026-11-02 «the protocol of Step 35.3d»): the
reference marking is manual and frozen, only human-confirmed matches are evaluated.

**The protocol of 35.3d** (fixed before the evaluation, D050 amendment of 2026-11-02): the hypothesis `Z = tag −
duration` (the tag as the start for reference only); the 13 climbs C2–C14 always the denominator, each excluded / within
`τ` / outlier / not computed with its reason; (1) ≥ 11 of 13 within `τ`, (2) every one of the 13 explained by its family,
(3) the Theil–Sen drift ≤ `τ / 2` («not established» is not met); the tag check on B6's 7 videos at 2 s — fixed after the
descriptive analysis, before the evaluation, not registered in advance — reported per file, outside the verdict; no new
exclusion before 35.3d; a negative result is recorded as it is.

**Reference marking — 35.3R** (D050 amendment of 2026-11-02): the marking of 35.3d as a preparatory task of its own.

| Sub-step | Content | Depends on |
|---|---|---|
| 35.3R1 | The tool: a Debug-only mode of the window «Проверка синхронизации COROS» (opened with `--open-project <set project folder> --coros-reference <study file>`; the study file is required and never created by the window) over the existing Source viewer — the 16 videos with their coverage and marks, all climbs C1–C14 without any hint from file names or times (C1 shown as «нет на видео, вне B6»), start / end at the Source position, moves, «go to», manual confirmation or «not matched», coverage, save and freeze. A video is marked only when it maps to the project's video by full path and size and is verified by SHA-256 (checked when it is first opened; size and time re-checked before every marking action) | — |
| 35.3R2 | The manual marking of the 16 videos and the 13 climbs C2–C14 by the rule below, the freeze of the study (the full check of the set first) — the frozen reference data set | 35.3R1 |

State: 35.3R1 done (`8684717`, the creation tool `83fdf91`; manual R1–R12 PASS, 2026-11-02); 35.3R2 done — the 16
videos marked with their coverage, 10 confirmed matches, C1, C7 and C13 «ambiguous», 9 marks «no climb in the FIT»; frozen
by the product owner (2026-11-02). No evaluation record yet (35.3d).

**The marking rule** (stored in the study as its marking rule, shown in the window; one rule for every video):
- **start** (required) — the moment the last foot leaves the floor and the climber moves from the start onto the wall;
  not the first movement of a hand, not the moment the watch registered the start;
- **end** (optional, only on a mark with a start) — the last contact with the wall when jumping off; not the landing,
  not the moment the watch registered the end.
Both are chosen by eye on the video; a small human error is accepted — no objectively exact frame is sought. A mark's
time is the Source viewer's position when the button is pressed; the shown frame's PTS may be kept for audit only. The
report (35.3b2d) states that the reference marks carry the error of a manual visual choice and names the rule version.

**One camera** — a FIT climb has at most one confirmed mark in force, a mark names at most one climb (D050 amendment of
2026-11-02).

**Order**: 35.3b2b–d are developed and tested on synthetic data alongside 35.3R1 / 35.3R2; the real set is evaluated
only after 35.3R2 is done and frozen (the freeze is recorded before the first evaluation record).

**Out of Phase 35** (B9): a FIT / COROS track on the editing timeline, climb markers on it and their visibility toggle.
The editing timeline has no watch-time axis; such a track is a later feature with its own decision, built as a
projection of the FIT's events onto the clips' source time (as the D048 strip is drawn per clip), reusing the Core
mapping and the climb rule — not the research window.

## 11. The owner's research of two trainings (2026-11-02) — descriptive results

*Descriptive results of the product owner's research, not the evaluation of B6: 35.3d has not been made, and nothing
here says that B6 passes or fails. B6, `τ = 2 s` and the matching rules are unchanged; neither training's results enter
B6's sample by this record. The reports are kept with the personal data and never committed:*
- `D:\Времянка для монтажа\Боулдер 08.08.2026\Итоги исследования 08.08.2026.md` — an independent training (10 videos, 20
  climbs, C1 and C2 not filmed; 27 attempts marked on the timeline, 18 matched to C3–C20);
- `D:\Времянка для монтажа\Боулдер 27.09.2026\Итоги исследования 27.09.2026.md` — the fixed set of B5, by the study of
  35.3R2 (10 confirmed matches; C1, C7, C13 ambiguous).

`k = W_FIT − (file-name time + source position)`, about ±1 s (whole seconds in the FIT and the names):

| | 2026-08-08 (18) | 2026-09-27 (10) |
|---|---|---|
| `k_end` | +3.7…+7.7 s, median 4.4 | +2.7…+8.1 s, median 4.8 |
| `k_start` | +5.7…+22.0 s, median 11.0 | +3.6…+39.1 s, median 15.8 |

What both trainings show:
- the watch records a climb's start later than the start seen on the video, by widely varying amounts; its end later by a
  far steadier amount — the reason for the D050 amendment of 2026-11-02 to B2 (the end as the default match point, the
  constant lag of 4.6 s);
- short attempts (3.4–14.5 s) can be missing from the FIT; a FIT climb can have no attempt (C10 of 27.09, by the owner's
  observation); a boundary between two rests need not be an attempt;
- the first climb starts at the activity's start, followed by a gap without splits (122 s on 27.09, 83 s on 08.08);
- grade and result are not in the standard FIT (set by hand in the overlay, §6.3).

Not established: the source of short tones heard at many FIT boundaries on 08.08 (20 of 36) and at few on 27.09 (6 of
34) — an observation, not evidence of the clocks' synchronisation. Body position and height were not studied.
