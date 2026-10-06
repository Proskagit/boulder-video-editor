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
| 1 | Settings survive save / reopen | Change the canvas, the rate and the export settings; save; close; reopen | Every setting as saved; the project not dirty after the reopen | `ExportEncodingPersistenceTests` (round trip of every value, not dirty), `ProjectSerializerRoundTripTests` | auto (13.3); manual after 13.6 / 13.9 (no UI before) |
| 2 | Settings survive a recovery | Change settings, wait for the autosave, kill the app, start it, Recover | The recovered project has the changed settings | `ExportEncodingPersistenceTests` (recovery round trip, default, invalid) | auto (13.3); manual after 13.6 / 13.9 |
| 3 | An older project file | Open a project written by Phase 12 | v3 kept (D028 Step 13.3): opens with the default export settings and saves unchanged | `ExportEncodingPersistenceTests` (no `settings.export` → default, saved unchanged; old `lastExportSettings` ignored) | auto (13.3); manual at 13.10 (R1) |

## Step 13.4 — canvas size (D028 §4)

| # | Scenario | Steps | Expected | Automated coverage | Status |
|---|---|---|---|---|---|
| 4 | Portrait canvas | A landscape project with a portrait phone video, a logo near a corner and a title; change the canvas to 1080 × 1920 | The Preview shows a portrait frame; the portrait video fills it; landscape clips fit by width; positions and font sizes × 0.5625 (D028 13.4, CS-1 B): the logo and the title stay inside the old frame, now a band in the middle | `CanvasEditTests` (five transitions, centre / corners), `ExportCanvasChangeEndToEndTests`, `PreviewLayersTests` (new canvas, decoders kept) | auto (13.4); app 13.6 (Claude), passed — 640 × 360 → 1080 × 1920: the Preview portrait, landscape clips fit by width; the FADE title × 1.6875 (font 48 → 81, position −200 / 120 → −337.5 / 202.5 in the Inspector) |
| 5 | Undo / redo of a canvas change | After 4, Undo, Redo | The Preview back to 16:9, then portrait again; every value exact; dirty / clean at the save point; changing back to 16:9 by the dialog instead does **not** restore the values (accepted) | `CanvasEditTests` (undo / redo exact, round trip ×0.3164) | auto (13.4); app 13.6 (Claude), passed — one Undo: 640 × 360 / 25 FPS back, title clean, Undo off; Redo: portrait again |
| 6 | Export at a non-default canvas | Export after 4 | The MP4 is 1080 × 1920 at the project rate; frames match the Preview | `ExportCanvasChangeEndToEndTests` (180 × 320, 240 × 240, byte for byte) | auto (13.4); app 13.6 (Claude), passed — export after 1080 × 1080 / 29.97: MP4 1080 × 1080, 30000/1001, 1019 frames |
| 7 | Refused sizes | Try an odd or out-of-limits size; a size that would push a font size past 1000 | Not accepted, with a message (the clip and track named for the font size); nothing changes, no Undo step | `CanvasEditTests` (rules, limits, atomic), `ExportPreflightTests` (preflight) | auto (13.4); app 13.6 (Claude), passed — 641 → "must be even", Apply off; 4K with a 400 px title → the clip / track / font message under Apply, dialog open, project unchanged; 1280 × 720 then applied |
| 8 | During an export | Start an export | The canvas can't be changed until it ends | the dialog's command (13.6) | app 13.6 (Claude), passed — during the export Project Settings…, Undo and Export off; on again afterwards |
| 8a | Paste after a canvas change | Copy a clip, change the canvas, paste | "The project frame size changed since the clips were copied. Copy them again."; nothing pasted | `CanvasEditTests` (clipboard) | auto (13.4); app 13.6 (Claude), passed — Ctrl+C on FADE, canvas → 1080 × 1080, Ctrl+V: "The project frame size changed … Copy them again." |

## Step 13.5 — frame rate (D028 §4)

| # | Scenario | Steps | Expected | Automated coverage | Status |
|---|---|---|---|---|---|
| 9 | Change the rate of an edited project | A 25 fps project with clips, fades, a dissolve, markers; change to 29.97 fps | The clips on the new grid (each edge to its nearest frame); fades, the dissolve and the markers at their times; the playhead on the nearest frame; the Preview and the timecode follow; locked / hidden tracks re-gridded too | `FrameRateEditTests` (re-grid, locked / hidden, speed, markers, playhead) | auto (13.5); app 13.6 (Claude), passed — 25 → 29.97 with the canvas in one Apply: timeline "29.97 FPS", fades 12 / 24 frames in the Inspector (markers: automated only — the fixture has none) |
| 10 | Refused rate change | A one-frame clip between neighbours at 60 fps → 24 fps; a 2-frame dissolve at 60 → 24; a dissolve whose source runs short at the new rate | Refused whole with a message; nothing changes, no Undo step | `FrameRateEditTests` (re-grid, dissolve < 2 frames, FR-1, rates not offered) | auto (13.5); app 13.6 (Claude), passed — 60 → 24 with a one-frame image between neighbours: "Can't change the frame rate to 24 FPS: A very short clip on track V1 has no room …" in the dialog, nothing changed |
| 11 | User rate vs the first video | New project, choose the provisional 30 fps (or another rate), then add a 25 fps video | The project stays at the chosen rate | `FrameRateEditTests` (FR-4: locked by choosing it) | auto (13.5); app 13.6 (Claude), passed — "30 FPS (provisional)" offered; "30 FPS" applied ("kept at 30 FPS"); pattern.mp4 (25 fps) added: still 30 FPS |
| 12 | Undo / redo and export | Undo / Redo the change of 9; export at the new rate | Exact restore; the MP4 at the new rate matches the Preview | `FrameRateEditTests` (undo / redo exact), `ExportFrameRateChangeEndToEndTests` (25 → 30, byte for byte) | auto (13.5); app 13.6 (Claude), passed — Undo / Redo of the canvas + rate step exact; the export at 29.97 (see 6) |
| 12a | Paste after a rate change | Copy a clip, change the rate, paste | "The project frame rate changed since the clips were copied. Copy them again."; after Undo of the change the paste works | `FrameRateEditTests` (clipboard) | auto (13.5); manual not run separately — the rate refusal message is the D027 rule checked at 12.6; 8a checked the canvas variant |

## Step 13.6 — project settings UI (D028 §1, §4)

| # | Scenario | Steps | Expected | Automated coverage | Status |
|---|---|---|---|---|---|
| 13 | Open the dialog | Open project settings | Current canvas and rate shown; presets incl. portrait / square, custom size, rates | `ProjectSettingsUiTests`, `ProjectSettingsViewBindingTests` | app 13.6 (Claude), passed — current "640 × 360 · 25 FPS", the 10 presets + Custom, the 8 rates |
| 14 | Apply and cancel | Change values, Cancel; then change and Apply | Cancel changes nothing; Apply is one Undo step (canvas and rate together) | `ProjectSettingsUiTests` (workflow, Cancel, one step), `ProjectSettingsEditTests` | app 13.6 (Claude), passed — preset, rate and Swap changed, Cancel: title clean, Undo off; Apply of 1080 × 1920 + 29.97: one Undo step |
| 15 | New project | New | Settings as decided at 13.6 (asked or defaults) | `ProjectSettingsUiTests` (New) | app 13.6 (Claude), passed — New (Don't Save): "Untitled Project", 30 FPS (provisional), tooltip 1920 × 1080 · 30 FPS (provisional), no dialog |
| 16 | Layout | The dialog and the main window at 1024 px and 1440 px | Nothing cut off or overlapping | — | app 13.6 (Claude), passed after a fix — at 1024 px the four-digit sizes were cut off; Swap moved next to the presets; toolbar and dialog checked at 1024 and 1440 px |
| 17 | During an export | Start an export | The settings command is disabled | `ProjectSettingsUiTests` (lock), `ProjectSettingsViewBindingTests` | app 13.6 (Claude), passed — see 8 |

## Step 13.7 — export settings core (D028 §7)

| # | Scenario | Steps | Expected | Automated coverage | Status |
|---|---|---|---|---|---|
| 18 | Default settings | Export with the defaults | The same output as Phase 12 (H.264 CRF 18 medium, AAC 192 kbps) | `FfmpegExportEncoderTests` (golden default command lines), `ExportSettingsEndToEndTests` (default through the chain) | auto (13.7); manual after 13.9 (no UI before; the default export was run in the app at 13.6 — scenario 6) |
| 19 | Each quality level | Export the same project at every level | Valid MP4s; size changes with the level; the picture as the Preview within the level's criterion (13.8) | `FfmpegExportEncoderTests` (every level: valid MP4, `crf=` in the x264 options), `ExportSettingsEndToEndTests` | auto (13.7) for validity; the picture against the Preview per level: 13.8 (L1-c); manual after 13.9 |
| 20 | Speed preset and audio bitrate | Export with another preset and bitrate | Valid MP4; the bitrate as chosen (ffprobe) | `FfmpegExportEncoderTests` (presets: `subme`; bitrates: AAC-LC near the request up to 256 kbps — 320 kbps reaches ≈ 243 kbps, D028 13.7) | auto (13.7); manual after 13.9 |

## Step 13.8 — L1-c (D028 §6)

No real-app scenario: the measurement and the criteria run in the suite (`ExportCodecLegTests`, D028 "Refined in Step
13.8"). Scenario 19 (each level in the app) follows the UI of 13.9.

## Step 13.9 — export settings UI (D028 §7)

| # | Scenario | Steps | Expected | Automated coverage | Status |
|---|---|---|---|---|---|
| 21 | Choose export settings | Project Settings…, EXPORT: change the quality, the speed and the bitrate; Apply; export | One Undo step; the export uses them (13.7 pipeline) | `ProjectSettingsUiTests` (every value, one step, the next job), `ExportSettingsEditTests` | auto (13.9); app 13.9 (Claude), passed — Compact / Slow / 320 applied, one Undo / Redo (the export itself: automated, 13.7) |
| 22 | Remembered settings | Reopen the dialog; save, close and open the project | The applied values are selected | `ProjectSettingsUiTests` (reopen), `ExportSettingsEditTests` (save → reopen) | auto (13.9); app 13.9 (Claude), passed — reopened after Undo / Redo; Save wrote `settings.export` |
| 23 | Cancel | Change the export choices, Cancel / Esc / close | Nothing changes, no Undo step | `ProjectSettingsUiTests` (draft + Cancel) | auto (13.9); app 13.9 (Claude), passed — draft: title clean, Undo off |
| 24 | Layout and lock | The dialog at 1024 px; during an export | Nothing cut off; not changeable during an export | `ProjectSettingsViewBindingTests`, `ProjectSettingsUiTests` (lock) | app 13.9 (Claude), passed at 1024 px; the lock as at 13.6 (scenario 17) |

## Regression (Step 13.10)

| # | Scenario | Expected | Status |
|---|---|---|---|
| R1 | A project saved by the Phase 12 build | v3 kept: opens unchanged, Save writes `formatVersion` 3; v4: the handling decided at 13.3 | planned |
| R2 | `docs/EXPORT_MANUAL_TEST_PLAN.md` at the default settings and at one non-default setting | Passes as before | planned |
| R3 | Relink, recent projects, tracks, ripple, copy / paste, markers, fades and dissolves at a non-default canvas and rate (short run) | As accepted in Phases 10–12 | planned |

## Results log

### 2026-10-06 — Step 13.6 real-app run (Claude), Debug, isolated profile

The Debug build (`src/App/bin/Debug`), `USERPROFILE` / `LOCALAPPDATA` / `APPDATA` in a scratch folder, 125 % display
scaling, the window at 1024 and 1440 logical px. Fixtures copied from the Phase 10 fade fixture (640 × 360, 25 fps) with
`project.json` edited for the variants: the FADE title at font 48 and position −200 / 120; the title at font 400; a 60 fps
timeline with a one-frame image between two neighbours; an unlocked 30 fps timeline with only the image and the title.
Every action through the UI (UI Automation for the buttons, combo boxes and dialogs; real clicks for a timeline clip and
the Windows save dialog; Ctrl+C / Ctrl+V as keys); results read from the window title, the status bar, the timeline's
rate label, the Inspector's fields, screenshots and ffprobe of the export.
- First attempt invalid: the app ran a UI build left over from a mutation check (the "draft written to the project"
  mutation, source already restored) — selecting a preset dirtied the project. The solution was rebuilt and every
  scenario run again on the clean build; the source never had the behaviour (the mutation tests fail on it).
- Found and fixed: at 1024 px the Width / Height fields cut four-digit values off ("1920") with Swap in their row; Swap
  moved next to the presets; re-checked at 1024 and 1440 px.
- Results: see the table rows 4–17 and 8a.
