# Phase 30 Step 30.6 — inventory of the manual checks (skeleton 2026-10-22; Phase 30 rows updated after the runs of 2026-10-23 and 2026-10-24)

The record of Step 30.6 (DECISIONS.md D046 §1.7, §7; `docs/DEVELOPMENT_PLAN.md`, Phase 30). The plans themselves are
unchanged — this file reads them, it does not rewrite their results. Sources: `docs/EXPORT_MANUAL_TEST_PLAN.md`,
`docs/PHASE9_MANUAL_TEST_PLAN.md` … `docs/PHASE30_MANUAL_TEST_PLAN.md` (every table row read by a script outside the
repository, then the rows with mixed or prose-recorded results read by hand), `progress.md`, D046. The rows of the earlier
plans are the state of `c421018` (2026-10-22) and are not changed here; a later run of a row is recorded in its own plan.
Phase 30's own rows (A, B, C, D, E) are updated here after the run of Step 30.3 (2026-10-23) and after the owner's manual
checks of 2026-10-24.

Rules: a row is PASS only when its plan records it as run and passed (the row, the plan's formal run or its result log).
**AUTO ONLY** — covered by automated tests, never run by hand (not counted as done). **PARTIAL** — part of the row run
and passed, the named rest not run. **NOT RUN** — not run (the reason as recorded). Phase 9's development-check rows are
superseded by its formal run (9.10) and not counted twice. Duplicates (the same check carried by several plans) are
merged below; the plans keep their rows.

"Main path" — D042 §1 without step 4, as D046 / the Phase 30 manual plan part B lists it: import; cut, split, arrange;
holds chosen and outlined (with their animation); animated text; transitions; music, its volume, a template; framing;
the Preview and the export watched in an external player.

## 1. Summary per plan (rows as recorded)

| Plan | Rows | PASS | PARTIAL | AUTO ONLY | NOT RUN | Other |
|---|---|---|---|---|---|---|
| Export | 14 | 13 | — | — | 1 (9, optional) | — |
| Phase 9 | 61 (formal run) | 59 | — | 1 (4K, automated) | — | 1 n/a (9: no manual form) |
| Phase 10 | 26 | 25 | — | — | 1 (2, optional) | — |
| Phase 11 | 85 | 84 | — | — | — | 1 n/a |
| Phase 12 | 37 | 33 | 2 (5, 26) | 1 (33) | 1 (12: the condition not reproducible) | — |
| Phase 13 | 29 | 22 | — | 6 (1, 2, 3, 18, 19, 20) | 1 (12a) | — |
| Phase 14 | 5 | 5 | — | — | — | — |
| Phase 15 | 42 | 37 | 1 (R8) | 2 (7, 24) | 2 (32, R10) | — |
| Phase 16 | 17 | 14 | 2 (2, R4) | 1 (12) | — | — |
| Phase 17 | 31 | 18 | — | 12 | 1 (R10) | — |
| Phase 18 | 30 | 21 | 1 (R3) | — | 6 (3, 11, 12, R5, S1 export, PQ) | 2 measured (9, LUT) |
| Phase 19 | 29 | 25 | 2 (13, R2) | — | — | 1 BLOCKED (4), 1 decided (T) |
| Phase 20 | 16 | 13 | — | 3 (13, 14, 15) | — | — |
| Phase 21 | 23 | 8 | 1 (16) | — | 14 | — |
| Phase 22 | 25 | 25 | — | — | — | — |
| Phase 23 | 24 | 23 | — | — | 1 (23) | — |
| Phase 24 | 21 | 21 | — | — | — | — |
| Phase 25 | 32 | 27 | 4 (8, 13, 14, F9) | — | 1 (12) | — |
| Phase 26 | 16 | 13 | 2 (14, 15) | 1 (12) | — | — |
| Phase 27 | 13 | 6 | 4 (1, 4, 9, 13) | 3 (5, 10, 12) | — | — |
| Phase 28 | 22 | 15 | 7 (1, 5, 6, 7, 8, 11, 14) | — | — | — |
| Phase 29 | 25 | 16 | 1 (12) | 7 (B1–B4, B6–B8 in the app) | — | 1 measured (11) |
| **Earlier phases** | **623** | **523** | **27** | **37** | **29** | **7** |
| Phase 30 (own rows) | 27 | 15 (B1–B9; A3–A6, A8 and A10 — the partial clean-environment check of 30.2 on 2026-10-24) | 1 (A1: built, without FFmpeg) | — | 1 (A2 review) | 10 (A7 and A9 BLOCKED — the environment; C1–C6 measured — the targets agreed by the product owner on 2026-10-24; D filled; E: U1 — not fixed by the owner's decision of 2026-10-24; one open observation from C4 — Preview does not update live during a continuous scrub, no fix or optimisation authorised) |

No row of an earlier plan is recorded as FAIL (the defects found in the runs were fixed and re-run, as the rows say).

Counted as PASS although the row's own status cell says otherwise — each by a later result the same plan records:
- Phase 10 rows 1 and 3 (cell "auto"), 8 and 9 (cell "changed 2026-09-30 — to rerun"): the formal run of Step 10.9 —
  R1 (row 1) and R2 (row 3) passed on 2026-09-30; row 9 in the second run by Claude and row 8 by the product owner on
  2026-10-01 (the prose under "Formal run (Step 10.9)").
- Phase 11 rows 24, 27, 28 (cell "11.8: not run; 11.9 (Claude): PASS"); the 11.8 run's summary row "23, 24, 27 — not
  run", whose rows 23, 24 and 27 each record a PASS at 11.9; R2 (the export regression, "PASS (9 NOT RUN — optional)" —
  row 9 is counted once, under the export plan).
- Phase 25 row 5 (cell "PASS (not reproduced)"): the check of defect FU-22.1, which did not reproduce — the row passed.
- The export plan has no status column: its rows take the result log (13 PASS in the runs of 2026-09-29, 2026-10-01 and
  2026-10-05; row 9 never run).
- Phase 9: the formal run (9.10) governs; its development-check rows are not counted.
Nothing else was raised: a cell with a PASS for one part and NOT RUN for another is PARTIAL, a cell with only an
automated result is AUTO ONLY.

## 2. Open rows of earlier phases, merged

The rows closed by Phase 30 carry a pointer to the Phase 30 manual plan (part B and part D); the earlier plans keep their own
results and are not rewritten here.

| # | Check (source rows) | Status | Evidence recorded | Needs | Main path / Phase 30 |
|---|---|---|---|---|---|
| O1 | Hold outline animation in the app: the Holds section, Animate, the fades frame by frame, Order, Interval + one Undo, fades 0, until clip end, Set to playhead, Duplicate / Paste, opacity + dissolve, a v5 project, an export running, the Inspector at the minimum width (P21 1–9, 12, 13, 18, 20, 21) | **PASS → closed by Phase 30** | the automated suites; PO accepted the main functionality 2026-10-15; **30.3 B3: the markup mode, the holds confirmed, a vertex corrected and undone / redone, the animation appear / stay / disappear checked frame by frame** | the app, a marked gym clip | **yes** (holds with their animation) → B3 |
| O2 | The animated export compared frame by frame with the Preview (P21 16) | PARTIAL → closed by Phase 30 | PO: real export viewed, PASS; the ffmpeg frame comparison not run — **30.3: the frames at the checkpoints equal to the Preview; PO 2026-10-24: the export watched in an external player (B9), PASS** | — | **yes** → B9 |
| O3 | A v6 project in the Phase 20 build (P21 19) | NOT RUN | — | a second, older build | no |
| O4 | Holds after a crop / move in the app (P23 23) | **PASS → closed by Phase 30** | the fixture had no holds; **30.3 B3 / B7: the holds confirmed and animated, the framing with a crop and a move — the picture kept in place** | a gym clip with holds | **yes** (framing + holds) → B3 / B7 |
| O5 | The outline stroke at 720p (P19 13) | NOT RUN (recorded) | 1080p, 1080 × 1920 and 4K were measured in the app, but the 720p canvas was not part of the run (1440 × 900 and 1024 × 640); the Phase 30 manual plan records this row as NOT RUN | a 1280 × 720 canvas | **yes** (export) |
| O6 | The Phase 15–18 tools on a marked clip: slip, ripple trim, J / L cuts, unlink audio, sound-only insert (P19 R2) | PARTIAL | the other tools PASS | the app | **yes** (cut / arrange with holds) → B2 |
| O7 | Hold-model failure paths: DirectML forced off (P19 4), a model error (P25 12) | BLOCKED / NOT RUN | the CPU fallback tested automatically | changing system files / a broken model | no (error paths) → A7 / A8 partly |
| O8 | Hold correction during an export / on a locked track; the colour after corrections (P20 13–15) | AUTO ONLY | the automated suites | the app | partly (holds) |
| O9 | Text: "+ Text" on a portrait gym clip (P27 1); all nine grid cells and a free move (P27 4); bold / colour / size changed in the app (P27 5) | PARTIAL / AUTO ONLY | the empty canvas and one cell PASS | the app, a gym clip | **yes** (animated text) → B4 |
| O10 | The text export watched in a video player (P27 9); the vertical-framing export watched in a player (P26 14) | PARTIAL → closed by Phase 30 | the files checked by frames and `cropdetect`; no player — **PO 2026-10-24: both exports of 30.3 watched in an external player (B9), PASS** | — | **yes** → B9 |
| O11 | A cancel during the UI-thread wait (P27 13), a v7 project in the Phase 26 build (P27 10), locked track / running export (P27 12) | PARTIAL / AUTO ONLY | the rest PASS | the app; an older build | no (edge cases) |
| O12 | The framing dialog and the context menu at 1024 × 640 (P26 15); Fill with crop / rotation / offset (P26 12) | **PASS → closed by Phase 30** | 1440 × 900 PASS; **30.3 B7 at both sizes: Fill 316.05 % / Fit 100 %, the move by a drag in the Preview, the scale, the crop by a handle drag, Undo / Redo** | the app at 1024 × 640 | **yes** (framing, both window sizes) → B7 |
| O13 | Transitions in the app: the duration at 24 / 25 / 30 fps (P28 5); type change, move, ripple, speed near a zone (P28 6, 7); playback through every type, a text and hold outlines over a zone (P28 8); 25 / 30 fps sources (P28 1); an edit during an export (P28 14) | PARTIAL | the automated suites PASS; 60 fps and the named parts PASS in the app | 25 / 30 fps sources, the app | **yes** (transitions) → B5 |
| O14 | A v7 project with dissolves exported from the app (P28 11) | PARTIAL | opened in the app PASS; export by the harness byte-identical | real media for that project | partly |
| O15 | Music in the app: import by the Media Browser and drag and drop, placing under the video, volume by field and slider, trim / split / delete, fitting the end, the export by ear, the Preview across a trim (P29 B1–B4, B6–B8) | **PARTIAL** | `MusicScenarioAuditTests` PASS; **30.3 + PO 2026-10-24 B6: the volume 50 % / 70 %, the fades 1 / 2 s and 0.5 / 1 s, one undo step each, the export's level per second follows the fades, the mix by ear PASS; the music file is the owner's MP3 (MP3, 44 100 Hz, stereo, 199.6 s — the parameters M1 requires of `docs/PHASE29_MANUAL_TEST_PLAN.md` part B; the optional M4A / AAC counterpart not provided)**; the import by the Media Browser and drag-and-drop, and the Preview across a trim, were not run by hand | the app (the import by the Media Browser and drag and drop) | **yes** (music + volume) → B6 |
| O16 | Templates in the app: the menu, the Inspector, Split, playback and export (P29 12) | PARTIAL | window shots only, no input sent | the app | **yes** (template) → B6 |
| O17 | Volume by ear (P25 8, F9) | PARTIAL → closed by Phase 30 | the rendered levels measured ±6.02 dB — **PO 2026-10-24: both exports listened to, the mix by ear PASS (B6)** | — | **yes** (music volume) → B6 |
| O18 | Audio device interruption — R10 (P15 32, R10; P17 R10) | NOT RUN | `PreviewSoundStatusTests` | a physical audio device to unplug | no — **not blocking S1** (D046 §7) |
| O19 | HDR: the S1 export on a phone / SDR monitor (P18 12), real PQ footage (P18 PQ), an SDR import in the app (P18 3), an export while the colour is unknown (P18 11), the Phase 15–17 tools on HDR clips (P18 R5), the HDR picture in the Phase 17 build (P18 R3) | NOT RUN / PARTIAL | measured: hardware decode 20 / 20 identical, the LUT within rounding | the PO's eyes, PQ footage | no — **not blocking S1** (D046 §7); the PO's gym clips may be HDR → watch in B9 |
| O20 | Linked sound and crossfades by hand (P17 2, 3, 5, 6, 11, 12, 14, 17, 19, 21, 22, 23) | AUTO ONLY | the automated suites | the app | partly (cut / arrange with sound) → B2 |
| O21 | Source viewer K / L, an analysed asset in Source without ffmpeg, an audio asset (P16 2, R4, 12) | PARTIAL / AUTO ONLY | the rest PASS | the app | no |
| O22 | Project / export settings by hand: save / recovery / older file, defaults, each quality level, speed preset and bitrate, paste after a rate change (P13 1, 2, 3, 12a, 18, 19, 20) | AUTO ONLY / NOT RUN | the automated suites; L1-c per level measured at 13.8 | the app | partly (the export settings) → B9 |
| O23 | Slip export, the header during an export (P15 24, 7); relink / Find Missing / track delete / media removal in the regression (P15 R8) | AUTO ONLY / PARTIAL | the rest PASS | the app | no |
| O24 | Track reorder heard, a rejected paste's last case, removal during analysis, New during an import (P12 5, 26, 12, 33) | PARTIAL / NOT RUN / AUTO ONLY | the races not reproducible by hand | the app, slow media | no |
| O25 | A damaged project through «Открыть», the export warnings dialog (P25 13, 14) | PARTIAL | the start-up path and a failed export PASS | the app, a warning fixture | no (error paths) |
| O26 | Export cancel during finalizing (Export 9) | NOT RUN (optional) | never hit in four runs | a long export | no |
| O27 | A v3 project in an older build (P10 2) | NOT RUN (optional) | — | an older build | no |
| O28 | The 4K export scenes (P9 4K) | AUTO ONLY | `AIVE_HEAVY_TESTS=1` 2 / 2 | — | covered by 30.7's heavy run |

## 3. Phase 30's own rows (after the run of Step 30.3, 2026-10-23, the owner's manual checks of 2026-10-24 and the partial clean-environment check of Step 30.2, 2026-10-24)

A1 built without FFmpeg (D046 §2 open); A2 the notices generated, review pending; C1–C6 **measured on 2026-10-24** (the
typical project of D046 §6 in the portable build: the opening 0.21 s, the timeline's answer 13–22 ms, the playback 98.6 % of
the real time, the scrub 109–121 ms, the export 4.72 × the real time, the memory up to 1077 MiB of the app and ≈ 3.7 GiB
with the ffmpeg processes) — **the targets were agreed by the product owner on 2026-10-24** (D046 §6); D filled (the
table of the manual plan's part D, the 28 items of §2 above); E holds one minor / UX defect, **U1** — **not fixed: the
product owner's decision of 2026-10-24**; the remark stays documented, no code change (D046 §5). One open observation from
C4: during a continuous scrub by dragging the ruler the Preview does **not** update live; no fix or optimisation was
authorised. **Phase 30 is technically closed out on 2026-10-24 by the product owner's decision; the full acceptance of S1
is not declared** — see `docs/DEVELOPMENT_PLAN.md`, `progress.md`, `ROADMAP.md` and DECISIONS.md D046 §13.

**A3–A10 (Step 30.2, 2026-10-24)** — a **partial** check on the development machine (the product owner's decision of 2026-10-24: a full clean environment is not required now; the absence of a VM / Windows Sandbox is recorded as a limit of the check and does not block the development). **PASS:** A3 (the start by a double click — the main window, the Russian interface, no error dialog, the log in `%LOCALAPPDATA%\AiVideoEditor\logs`), A4 (the start from another current directory — `appsettings.json` and the tools' relative paths read from the application folder), A5 (a folder with spaces and Cyrillic — the start, the import, the save and the export), A6 (no FFmpeg — the existing messages at the import and at the export, no crash), A8 (the model installed — the check and the loading progress, DirectML, a candidate contour confirmed), A10 (the templates' fonts — installed, the templates drawn as in the development build, no font warning). **BLOCKED by the environment:** A7 (the model cannot be uninstalled here — the app's data root cannot be redirected and the user's model files are not touched) and A9 (the Visual C++ runtime cannot be removed; the PE import tables show that `onnxruntime.dll` needs MSVCP140 / MSVCP140_1 / VCRUNTIME140 / VCRUNTIME140_1 → the runtime **is** required on a machine without it). **No defect was found.** The evidence in each row: `docs/PHASE30_MANUAL_TEST_PLAN.md` part A.

**The main path (B1–B9) — the run of 2026-10-23** (local, Claude) in the portable build `0.30.0+cf718bc7dd.dirty` with
gyan.dev FFmpeg 9.0.1 from its `ffmpeg\` folder (not on `PATH`), the product owner's clips of 2026-09-27 and an MP3, at
1440 × 900 and 1024 × 640 (two projects): **B1, B2, B3, B4, B5, B7 and B8 PASS** at both sizes; **B6 BLOCKED** for the mix
by ear and **B9 BLOCKED** for the export in an external player (both need a person); the frames at the checkpoints equal
to the Preview; no blocker and no major defect. What was checked in each row: `docs/PHASE30_MANUAL_TEST_PLAN.md` part B.
The withdrawn former D1 (the test driver's DPI artefact) and the NOT RUN check of dragging the window between the
monitors: the manual plan's part E.

**The owner's manual checks of 2026-10-24** — the rows that needed a person, run by the product owner on the artefacts of
the run of 2026-10-23 (the same portable build and the same two exported files, no new export): **B6 PASS** (the mix by
ear — both exports listened to: the music and the gym's own sound are correct, the fade-out is normal, no critical clicks,
level jumps or desynchronisation noticed); **B9 PASS** (both exports in an external player — the captions, the hold
outlines, the transitions, the templates and the framing look correct, no critical playback problems noticed); the
dragging of the window between the monitors with the real mouse at 125 % ↔ 100 %, the interface's interaction and the
clicks: **PASS** (no problems found). The observations are the owner's (by ear and by eye); no new measurements were made.
**B1–B9 are PASS; no row of the main path waits for a person.**

**Two questions left open (neither blocks the main path).**
- **M1.** The Phase 29 plan (part B) defines M1 as the product owner's real music file (an MP3, ideally also the same piece
  as M4A / AAC), ≥ 3 min, 44.1 kHz. The music of the projects of 30.3 is the owner's MP3
  `SawanoHiroyukinZkRei_-_TO_BE_HERO_X_-_Opening_FULL_INERTIA_79350751.mp3`, transferred on Claude's request and measured
  as MP3, 44 100 Hz, stereo, 199.6 s — the parameters M1 requires; the optional M4A / AAC counterpart was not provided. The
  rows of Phase 29 that waited for M1 keep their recorded results (that plan is not rewritten); the by-ear result of
  2026-10-24 is recorded here and in the Phase 30 plan.
- **B1's «3 / 4 files imported and analysed».** The final project holds all four assets (three videos and the MP3) — the
  product owner confirms it, and `project.json` lists them. Which file was not imported at that step is not established by
  the existing evidence (the driver's log holds only timestamps); **the ambiguity stays open**. It is not a defect: nothing
  is missing from the project.

## 4. What remains for the product owner (by hand)

1. **What the run of Step 30.3 (2026-10-23) and the owner's manual checks (2026-10-24) closed** — the main path B1–B9 at
   1024 × 640 and 1440 × 900 on the gym clips: **all PASS**. The run closed the main-path rows O1, O4 and O12 and the
   save / reopen part of O22; the owner's checks closed O2 and O10 (the external player) and the by-ear part of O15 and
   O17. O6, O9, O13, O15, O16 and O22 stay partly closed; the rest of them is recorded with its reason.
2. **The decisions the product owner alone takes:** **U1 — decided on 2026-10-24: not fixed**, left as a documented minor /
   UX defect (the reason is the product owner's decision, not a confirmation that the defect is absent; D046 §5).
   **The clean-environment check of Step 30.2 — decided on 2026-10-24: a partial check on the current machine**, the absence
   of a VM / Windows Sandbox recorded as a limit of the check, not blocking the development. Still open: the **FFmpeg
   distribution** and its inclusion in the delivery (D046 §2, §13) — **deferred** by the product owner on 2026-10-24 to the
   preparation of the release, to be considered again before the publication; it is **not** regarded as settled and no work
   on it is started; the **performance targets of Step 30.5 — agreed by the product owner on 2026-10-24** (D046 §6): the
   targets are in the manual plan's part C; **one open observation from C4 remains** — during a continuous scrub by dragging
   the ruler the Preview does **not** update live, and no fix or optimisation was authorised. **Phase 30 is technically
   closed out on 2026-10-24 by the product owner's decision; the full acceptance of S1 is not declared** (FFmpeg, notices,
   A7/A9 environment, C4 observation, CI green and final sign-off remain open).
3. The clean environment for A3–A10 — **the partial check of 2026-10-24 is done** (A3–A6, A8 and A10 PASS; A7 and A9 BLOCKED by the environment, with the reasons recorded); a full clean environment (Windows Sandbox / a VM) stays open and is **not blocking**, per the product owner's decision of 2026-10-24.
4. Optional, not blocking S1: R10 (O18), the HDR / PQ checks (O19), the older-build checks (O3, O11, O27), the error
   paths needing system changes (O7).
5. The two open questions of §3 — M1 (the Phase 29 rows that waited for it) and B1's «3 / 4 files imported» — neither
   blocks the main path.
