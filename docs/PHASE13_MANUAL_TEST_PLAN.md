# Phase 13 — manual test plan

Run in the real app (`dotnet run --project src/App/App.csproj`, ffmpeg / ffprobe on PATH). Written at Step 13.2
(D028, product owner decisions of 2026-10-06) as a skeleton; each step that implements a feature completes its
scenarios (exact texts, presets, limits, automated coverage, fixtures) and records its development-time run; Step 13.10
runs the scenarios not yet run in the real app, the regression and the export manual plan. Logs:
`%LOCALAPPDATA%\AiVideoEditor\logs`. Manual runs use an isolated profile (`USERPROFILE` / `LOCALAPPDATA` pointed to a
scratch folder), as in Phases 11–12.

Fixtures: written by the steps that need them (scripts under `tools/manual`, projects and media outside the
repository, as in Phases 10–12): landscape, portrait and square sources; a project with two video tracks and one audio
track, clips with transforms (position, scale), a text clip, fades, a dissolve and a marker; a 25 fps and a 29.97 fps
source. If the format moves to v4 (D028 §5, decided at 13.3), the fixture scripts write v4.

Status column — kinds of evidence, never mixed:
- **planned** — written at 13.2, not implemented yet;
- **auto (13.x); manual pending** — implemented and covered by automated tests at step 13.x, not yet run in the real app;
- **auto** — covered by automated tests only, not checked in the real app;
- **app 13.x (Claude), passed** — checked in the real app by Claude while implementing / accepting step 13.x;
- **13.10 (Claude): PASS / FAIL / BLOCKED / NOT RUN** — the formal Step 13.10 run;
- **PO** — run by the product owner.
A scenario is marked passed only for the run that actually checked it; the "Automated coverage" column is separate.

## Step 13.3 — settings model and project format (D028 §5)

| # | Scenario | Steps | Expected | Automated coverage | Status |
|---|---|---|---|---|---|
| 1 | Settings survive save / reopen | Change the canvas, the rate and the export settings; save; close; reopen | Every setting as saved; the project not dirty after the reopen | (13.3) | planned |
| 2 | Settings survive a recovery | Change settings, wait for the autosave, kill the app, start it, Recover | The recovered project has the changed settings | (13.3) | planned |
| 3 | An older project file | Open a project written by Phase 12 | v3 kept: opens with the default export settings and saves unchanged; v4: the handling decided at 13.3 (opens, or a clear refusal) | (13.3) | planned |

## Step 13.4 — canvas size (D028 §4)

| # | Scenario | Steps | Expected | Automated coverage | Status |
|---|---|---|---|---|---|
| 4 | Portrait canvas | A landscape project with a portrait phone video; change the canvas to 1080 × 1920 | The Preview shows a portrait frame; the portrait video fills it; landscape clips fit by width; positions / text by the rule of 13.4 | (13.4) | planned |
| 5 | Undo / redo of a canvas change | After 4, Undo, Redo | The Preview back to 16:9, then portrait again; dirty / clean at the save point | (13.4) | planned |
| 6 | Export at a non-default canvas | Export after 4 | The MP4 is 1080 × 1920 at the project rate; frames match the Preview | (13.4) | planned |
| 7 | Refused sizes | Try an odd or out-of-limits size | Not accepted, with a message; nothing changes, no Undo step | (13.4) | planned |
| 8 | During an export | Start an export | The canvas can't be changed until it ends | (13.4) | planned |

## Step 13.5 — frame rate (D028 §4)

| # | Scenario | Steps | Expected | Automated coverage | Status |
|---|---|---|---|---|---|
| 9 | Change the rate of an edited project | A 25 fps project with clips, fades, a dissolve, markers; change to 30 fps | The clips on the new grid; fades, the dissolve and the markers at their times; the Preview and the timecode follow | (13.5) | planned |
| 10 | Refused rate change | A project where the re-grid can't be exact (as decided at 13.5) | Refused whole with a message; nothing changes | (13.5) | planned |
| 11 | User rate vs the first video | New project, choose 25 fps, then add a 29.97 fps video | The project stays at 25 fps | (13.5) | planned |
| 12 | Undo / redo and export | Undo / Redo the change of 9; export at the new rate | Exact restore; the MP4 at the new rate matches the Preview | (13.5) | planned |

## Step 13.6 — project settings UI (D028 §1, §4)

| # | Scenario | Steps | Expected | Automated coverage | Status |
|---|---|---|---|---|---|
| 13 | Open the dialog | Open project settings | Current canvas and rate shown; presets incl. portrait / square, custom size, rates | (13.6) | planned |
| 14 | Apply and cancel | Change values, Cancel; then change and Apply | Cancel changes nothing; Apply is one Undo step (canvas and rate together) | (13.6) | planned |
| 15 | New project | New | Settings as decided at 13.6 (asked or defaults) | (13.6) | planned |
| 16 | Layout | The dialog and the main window at 1024 px and 1440 px | Nothing cut off or overlapping | — | planned |
| 17 | During an export | Start an export | The settings command is disabled | (13.6) | planned |

## Step 13.7 — export settings core (D028 §7)

| # | Scenario | Steps | Expected | Automated coverage | Status |
|---|---|---|---|---|---|
| 18 | Default settings | Export with the defaults | The same output as Phase 12 (H.264 CRF 18 medium, AAC 192 kbps) | (13.7) | planned |
| 19 | Each quality level | Export the same project at every level | Valid MP4s; size changes with the level; the picture as the Preview within the level's criterion (13.8) | (13.7) | planned |
| 20 | Speed preset and audio bitrate | Export with another preset and bitrate | Valid MP4; the bitrate as chosen (ffprobe) | (13.7) | planned |

## Step 13.8 — L1-c (D028 §6)

Measurement only in the real app: none planned (the measurement runs as a tool / tests). Scenario 19 checks the chosen
criteria visually.

## Step 13.9 — export settings UI (D028 §7)

| # | Scenario | Steps | Expected | Automated coverage | Status |
|---|---|---|---|---|---|
| 21 | Choose settings in the export flow | Export, change the level / preset / bitrate | The export uses them | (13.9) | planned |
| 22 | Remembered settings | Export again (and after a reopen, if saved per project) | The last choice is offered (D028 question 3) | (13.9) | planned |
| 23 | Cancel | Cancel the settings / the picker | Nothing exported; settings as decided at 13.9 | (13.9) | planned |
| 24 | Layout and lock | The settings at 1024 px; during an export | Nothing cut off; not changeable during an export | (13.9) | planned |

## Regression (Step 13.10)

| # | Scenario | Expected | Status |
|---|---|---|---|
| R1 | A project saved by the Phase 12 build | v3 kept: opens unchanged, Save writes `formatVersion` 3; v4: the handling decided at 13.3 | planned |
| R2 | `docs/EXPORT_MANUAL_TEST_PLAN.md` at the default settings and at one non-default setting | Passes as before | planned |
| R3 | Relink, recent projects, tracks, ripple, copy / paste, markers, fades and dissolves at a non-default canvas and rate (short run) | As accepted in Phases 10–12 | planned |

## Results log

(none yet)
