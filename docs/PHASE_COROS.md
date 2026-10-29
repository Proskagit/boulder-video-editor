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
| 35.3 (C2) | Recording time: the probe and its marker (D049 §4), the raw tag with its origin (§2–§3); research on the real session — what the tag marks, whether it is a true C instant, the C ↔ W difference per climb (visible start vs split), the outliers; a proposed automatic-zero rule with its tolerance `τ` and device scope | the rule explains ≥ 12 of the 14 typical files within `τ` and rejects or explains the two outliers; approved by the owner; until then every zero «unknown» (manual plan part B) | planned; new-version members in the branch only |
| 35.4 (C3) | Persistence and alignment: the format change (expected v11), `Project.Training`, the FIT life cycle (D049 §9), the clock correction and the per-file override in the Inspector (one Undo step each), video relink with the override (ID1, §8), the zero states, the mapping (§6); 35.2's «not saved» texts revised | D049 §13 tests; manual plan part C | planned; format change in the branch only, in `main` with the second pull request |
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
