# Phase Coros — import of COROS climbing trainings and their synchronisation with the video

Branch `feat/phase-coros-c0`, from `origin/main` `1e58885` (Phases 30 / 31, hold tracking and D048 with its stages 2–3 merged;
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

## 3. Specification (Phase Coros)

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
| **C0** | Core: `Telemetry/FitFile` (FIT reader: header / file CRC, both byte orders, compressed timestamps, developer fields skipped), `CorosWorkoutParser`, `ClimbingWorkout` model, `WorkoutTimeline` (HR at an instant, splits in a range), `WorkoutVideoAlignment`. Tests in `Core.Tests/Telemetry` on synthetic FIT files; `tests/Telemetry.LocalTests` (outside the solution) on the real export | the real export parses to the facts of §1 (14 climbs, 5 766 HR samples, the pause, +3 h); every refusal reason covered; no change of `project.json`, UI or existing code | **done**: build and tests run on Windows (see §7) |
| C1 | Import entry: `ITrainingImportService` (Core) + file implementation (Media; size limit, I/O errors); «Импорт тренировки COROS…» with a `.fit` picker; localized refusal texts and warnings; the workout held in the session (not saved) | importing the real file shows device, date, local time, 14 climbs; each refusal reason has a Russian / English text; nothing persisted | planned |
| C2 | Video timestamps: ffprobe `creation_time` (format and stream tags) read at analysis; its meaning per device (end of recording on this phone) → the automatic source zero; the file-name time as a cross-check; measuring the phone ↔ watch clock difference on the real session (a climb's visible start against its split) | for ≥ 12 of the 14 climbs on video the automatic match is within a stated tolerance, or the measured difference is documented and the correction made mandatory | planned; session-only in the first version (§6.2) |
| C3 | Alignment per clip + the correction in the Inspector (whole frames, one Undo step); Trim / Split / Move keep the match; persistence | tests: Trim / Split / Move, Undo / Redo, save / load | planned; first version session-only (§6.2) — persistence later, in one version sequence with D048 (v10 is D048's) |
| C4 | Overlay: heart icon + BPM, a few looks / positions; Preview and export through the one composition path | parity test Preview = export; seeking; data gaps and pauses show nothing | planned |
| C5 | Manual check on the real session (video + export) | product owner's acceptance | planned |

## 5. Dependencies and possible conflicts

- C0 adds new files only (`src/Core/Telemetry/*`, `tests/Core.Tests/Telemetry/*`, `tests/Telemetry.LocalTests/*`,
  this document): carried onto `1e58885` without a conflict. Core.Tests picks the new tests up by its glob.
- `tests/Telemetry.LocalTests` is outside `AiVideoEditor.sln` like `tests/Vision.ModelTests`: the CI skip gate
  (`Assert-TestResults.ps1`) and the expected TRX count are unaffected; the personal FIT file is never committed.
- C1 touches the localization resources, the import entry of the toolbar and the DI registrations — Phase 31 is merged
  into `1e58885`, so C1 starts from its state; any D048 implementation branch editing the same files is a later merge.
- Persistence of the alignment (later than the first version) changes `project.json`: v10 is D048's (merged); a
  later v11 needs its own approval and a schema check against v10.
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
