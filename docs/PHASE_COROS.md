# Phase 35 — import of COROS climbing trainings and their synchronisation with the video

*Formerly «Phase Coros». Numbering confirmed by the product owner on 2026-10-28 (`docs/DEVELOPMENT_PLAN.md` on
`docs/phase-numbering`, "Phase and step numbering"): this is **Phase 35**; its steps are **35.0–35.6** — the old labels C0–C5
are kept below in brackets and in the commit messages. The phase is not approved yet (Step 35.0: its decision, plan and
acceptance criteria) and complete only when merged into `main` and accepted by the product owner — the local commits of
35.1 and 35.2 complete nothing.*

Branches `feat/phase-coros-c0` (C0) and `feat/phase-coros-c1` (C1, on top of C0), from `origin/main` `1e58885` (Phases 30 / 31, hold tracking and D048 with its stages 2–3 merged;
`project.json` v10 in the code). C0 was written on `feat/phase-coros` from `e28a9d3` (`32662ee`) and
carried over unchanged (2026-10-28); the old branch is kept. This document is the phase's record until its merge.
`DECISIONS.md`, `ROADMAP.md` and `progress.md` are not edited on this branch; their entries are written when the phase is
merged. Continues D042 §6 (R3 → F) with real files.

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

Video side (from the preliminary notes, not re-measured): 3840 × 2160 HEVC ≈ 60 fps SDR, one vertical; the file name
is the local start time of the recording; the container `creation_time` (UTC) is ≈ the **end** of the recording for
14 files (name + duration − 0.1…2.2 s) and not for 2 (−181.7 s, −65.0 s, unexplained). Not established: the clock
difference between the phone and the watch.

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
2. **Time model**: the training is absolute UTC time (`DateTimeOffset`, 100 ns ticks — the same unit as `MediaTime`);
   millisecond durations converted exactly; the local offset only for display.
3. **Synchronisation**: per video clip, bound to its **source time** (as the holds, D034 / D036): `WorkoutVideoAlignment` =
   UTC of the clip's source time zero + a user correction. The automatic zero comes from the video's timestamps (§C2); the
   correction is a signed offset in whole frames; one Undo step per change.
4. **Heart rate at an instant**: the latest sample at or before it, at most 2 s old, inside the recording and outside the
   timer's pauses; otherwise nothing is shown (no interpolation, no stale value).
5. **Overlay** (later step): heart icon + BPM through the one composition path, Preview = export.

## 4. Steps and acceptance

| Step | Content | Acceptance | State |
|---|---|---|---|
| 35.0 | The phase's decision (its D-number when approved), this plan as the acceptance criteria, the manual test plan | approved by the product owner | to do |
| **35.1** (C0) | Core: `Telemetry/FitFile` (FIT reader: header / file CRC, both byte orders, compressed timestamps, developer fields skipped), `CorosWorkoutParser`, `ClimbingWorkout` model, `WorkoutTimeline` (HR at an instant, splits in a range), `WorkoutVideoAlignment`. Tests in `Core.Tests/Telemetry` on synthetic FIT files; `tests/Telemetry.LocalTests` (outside the solution) on the real export | the real export parses to the facts of §1 (14 climbs, 5 766 HR samples, the pause, +3 h); every refusal reason covered; no change of `project.json`, UI or existing code — except the rule of `LocalizationGuardTests` and the related localization fix of `FitFile` (§7, `f12c5e6`) | **implemented** (`4636dd5`, `f12c5e6`, local branch `feat/phase-coros-c0`); build and tests run on Windows (see §7); not merged |
| **35.2** (C1) | Import entry: `ITrainingImportService` (Core) + `TrainingImportService` (Media; 16 MiB limit, missing / unreadable file); «Тренировка COROS…» on the toolbar with a `.fit` picker (`TrainingImportWorkflow`); localized refusal texts and warnings (`TrainingText`); the workout held in `TrainingSessionService` (not saved) | importing the real file shows device, date, local time, 14 climbs; each refusal reason has a Russian / English text; nothing persisted | **implemented** (`934bbf6`, local branch `feat/phase-coros-c1`); **manual acceptance A1–A11 PASS (2026-10-29, §9)**, no defect found; not merged |
| 35.3 (C2) | Video timestamps: ffprobe `creation_time` (format and stream tags) read at analysis; its meaning per device (end of recording on this phone) → the automatic source zero; the file-name time as a cross-check; measuring the phone ↔ watch clock difference on the real session (a climb's visible start against its split) | for ≥ 12 of the 14 climbs on video the automatic match is within a stated tolerance, or the measured difference is documented and the correction made mandatory | planned; session-only in the first version (§6.2) |
| 35.4 (C3) | Alignment per clip + the correction in the Inspector (whole frames, one Undo step); Trim / Split / Move keep the match; persistence | tests: Trim / Split / Move, Undo / Redo, save / load | planned; first version session-only (§6.2) — persistence later, in `project.json` v11 — a planned format, not implemented — by decision D049 (number reserved; planned, not approved, not yet in `DECISIONS.md`; v10 is D048's) |
| 35.5 (C4) | Overlay: heart icon + BPM, a few looks / positions; Preview and export through the one composition path | parity test Preview = export; seeking; data gaps and pauses show nothing | planned |
| 35.6 (C5) | Verification, the manual check on the real session (video + export), closeout | product owner's acceptance | planned |

## 5. Dependencies and possible conflicts

- C0 adds new files only (`src/Core/Telemetry/*`, `tests/Core.Tests/Telemetry/*`, `tests/Telemetry.LocalTests/*`,
  this document): carried onto `1e58885` without a conflict. Core.Tests picks the new tests up by its glob.
- `tests/Telemetry.LocalTests` is outside `AiVideoEditor.sln` like `tests/Vision.ModelTests`: the CI skip gate
  (`Assert-TestResults.ps1`) and the expected TRX count are unaffected; the personal FIT file is never committed.
- C1 touches the localization resources, the import entry of the toolbar and the DI registrations — Phase 31 is merged
  into `1e58885`, so C1 starts from its state; any D048 implementation branch editing the same files is a later merge.
- Persistence of the alignment (later than the first version) changes `project.json`: v10 is D048's (merged); a
  later v11 needs its own approval — decision D049 (number reserved; planned, not approved) — and a schema check against v10.
- C4 uses the overlay / animation path of Phase 27 (D043) and the composition painter.

## 6. Questions for the product owner

1. The device: the real export is from a **COROS APEX 4**, the plan says Pace 4. Accept any COROS activity FIT (current
   C0 behaviour), or restrict to named models? — **Answered (2026-10-26)**: the watch is a COROS APEX 4 42mm; no model
   restriction — any valid COROS activity FIT whose content meets the parser's requirements is accepted.
2. Persistence (C2 / C3): saving the video's `creation_time` and the per-clip alignment needs `project.json` v10. Approve,
   or keep the training session-only for the first version? — **Answered (2026-10-28)**: the FIT import of the first
   version lives in the current session only; nothing is written to `project.json`.
3. The route result: confirm whether field 71 is wanted at all (it is not reliable on the one export); grade / result
   then come from the user. — **Answered (2026-10-28)**: the route's grade and the attempt's result are set by hand in
   the overlay; COROS support did not confirm a way to get them from the FIT or through MCP. Field 71 stays unread.

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
- **Session only** (§6.2): `UI/Services/TrainingSessionService` holds the workout and its file path in memory. Nothing is
  added to `Project` or `project.json` (tested: a project saved after an import has no trace of it). The training belongs to
  the project it was imported into, like a media import (D027 §7): opening / creating / recovering another project drops it,
  and a training read while another project became current is not kept. A new import replaces it.
- **Tooltip** of the button: what it does, or — once a training is held — its summary, the file name and «Не сохраняется в
  проект».
- Not in C1: any use of the workout (alignment, overlay), a «remove training» command, a shortcut.

## 9. Step 35.2 (C1) — manual acceptance (product owner)

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
