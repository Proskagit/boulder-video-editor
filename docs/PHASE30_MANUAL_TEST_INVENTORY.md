# Phase 30 Step 30.6 — inventory of the manual checks (2026-10-22)

The record of Step 30.6 (DECISIONS.md D046 §1.7, §7; `docs/DEVELOPMENT_PLAN.md`, Phase 30). The plans themselves are
unchanged — this file reads them, it does not rewrite their results. Sources: `docs/EXPORT_MANUAL_TEST_PLAN.md`,
`docs/PHASE9_MANUAL_TEST_PLAN.md` … `docs/PHASE30_MANUAL_TEST_PLAN.md` (every table row read by a script outside the
repository, then the rows with mixed or prose-recorded results read by hand), `progress.md`, D046. The state of
`c421018` (2026-10-22); a later run of a row is recorded in its own plan, not here.

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
| Phase 30 (own rows) | 27 | — | 1 (A1: built, without FFmpeg) | — | 23 (A2 review, A3–A10, B1–B9, C1–C6) | D, E empty |

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

| # | Check (source rows) | Status | Evidence recorded | Needs | Main path / Phase 30 |
|---|---|---|---|---|---|
| O1 | Hold outline animation in the app: the Holds section, Animate, the fades frame by frame, Order, Interval + one Undo, fades 0, until clip end, Set to playhead, Duplicate / Paste, opacity + dissolve, a v5 project, an export running, the Inspector at the minimum width (P21 1–9, 12, 13, 18, 20, 21) | NOT RUN (auto only) | the automated suites; PO accepted the main functionality 2026-10-15 | the app, a marked gym clip | **yes** (holds with their animation) → B3 |
| O2 | The animated export compared frame by frame with the Preview (P21 16) | PARTIAL | PO: real export viewed, PASS; the ffmpeg frame comparison not run | ffmpeg frames of an export | **yes** → B9 |
| O3 | A v6 project in the Phase 20 build (P21 19) | NOT RUN | — | a second, older build | no |
| O4 | Holds after a crop / move in the app (P23 23) | NOT RUN | the fixture had no holds | a gym clip with holds | **yes** (framing + holds) → B3 / B7 |
| O5 | The outline stroke at 720p (P19 13) | PARTIAL | 1080p, 1080 × 1920, 4K measured; 720p not run | a 1280 × 720 canvas | **yes** (export) |
| O6 | The Phase 15–18 tools on a marked clip: slip, ripple trim, J / L cuts, unlink audio, sound-only insert (P19 R2) | PARTIAL | the other tools PASS | the app | **yes** (cut / arrange with holds) → B2 |
| O7 | Hold-model failure paths: DirectML forced off (P19 4), a model error (P25 12) | BLOCKED / NOT RUN | the CPU fallback tested automatically | changing system files / a broken model | no (error paths) → A7 / A8 partly |
| O8 | Hold correction during an export / on a locked track; the colour after corrections (P20 13–15) | AUTO ONLY | the automated suites | the app | partly (holds) |
| O9 | Text: "+ Text" on a portrait gym clip (P27 1); all nine grid cells and a free move (P27 4); bold / colour / size changed in the app (P27 5) | PARTIAL / AUTO ONLY | the empty canvas and one cell PASS | the app, a gym clip | **yes** (animated text) → B4 |
| O10 | The text export watched in a video player (P27 9); the vertical-framing export watched in a player (P26 14) | PARTIAL | the files checked by frames and `cropdetect`; no player | an external player, a person | **yes** → B9 |
| O11 | A cancel during the UI-thread wait (P27 13), a v7 project in the Phase 26 build (P27 10), locked track / running export (P27 12) | PARTIAL / AUTO ONLY | the rest PASS | the app; an older build | no (edge cases) |
| O12 | The framing dialog and the context menu at 1024 × 640 (P26 15); Fill with crop / rotation / offset (P26 12) | PARTIAL / AUTO ONLY | 1440 × 900 PASS | the app at 1024 × 640 | **yes** (framing, both window sizes) → B7 |
| O13 | Transitions in the app: the duration at 24 / 25 / 30 fps (P28 5); type change, move, ripple, speed near a zone (P28 6, 7); playback through every type, a text and hold outlines over a zone (P28 8); 25 / 30 fps sources (P28 1); an edit during an export (P28 14) | PARTIAL | the automated suites PASS; 60 fps and the named parts PASS in the app | 25 / 30 fps sources, the app | **yes** (transitions) → B5 |
| O14 | A v7 project with dissolves exported from the app (P28 11) | PARTIAL | opened in the app PASS; export by the harness byte-identical | real media for that project | partly |
| O15 | Music in the app: import by the Media Browser and drag and drop, placing under the video, volume by field and slider, trim / split / delete, fitting the end, the export by ear, the Preview across a trim (P29 B1–B4, B6–B8) | AUTO ONLY (in the app NOT RUN) | `MusicScenarioAuditTests` PASS; M1 (the PO's music) not provided | the PO's music, the app, a listener | **yes** (music + volume) → B6 |
| O16 | Templates in the app: the menu, the Inspector, Split, playback and export (P29 12) | PARTIAL | window shots only, no input sent | the app | **yes** (template) → B6 |
| O17 | Volume by ear (P25 8, F9) | PARTIAL | the rendered levels measured ±6.02 dB | a listener | **yes** (music volume) → B6 |
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

## 3. Phase 30's own rows (all open)

A1 built without FFmpeg (D046 §2 open); A2 the notices generated, review pending; A3–A10 (the clean environment) NOT
RUN; B1–B9 (the main path) NOT RUN; C1–C6 (performance) not measured; D (this inventory) and E (defects) empty in the
plan.

## 4. What remains for the product owner (by hand)

1. The main path B1–B9 in the portable build at 1024 × 640 and 1440 × 900 on the gym clips — it also closes the
   main-path rows O1, O2, O4, O5 (a 720p canvas), O6, O9, O10, O12, O13 (25 / 30 fps sources if available), O15, O16,
   O17 (listening), O20 and O22 (the export settings) when they are run as part of it.
2. The music file M1 for B6 / O15 (not provided so far).
3. Watching the exports in an external player (O2, O10, B9) and listening to the mix (O15, O17).
4. The clean environment for A3–A10 (Windows Sandbox / a VM, or a partial check agreed).
5. Optional, not blocking S1: R10 (O18), the HDR / PQ checks (O19), the older-build checks (O3, O11, O27), the error
   paths needing system changes (O7).
