# Phase 16 — manual test plan

Run in the real app (`dotnet run --project src/App/App.csproj`, ffmpeg / ffprobe on PATH unless the scenario says
otherwise). Written at Step 16.2 (D031, product owner decisions of 2026-10-08) as a skeleton; each step that implements a
feature completes its scenarios and records its development-time run; Step 16.7 runs the scenarios not yet run in the
real app and the regression. Manual runs use an isolated profile (`USERPROFILE` / `LOCALAPPDATA` / `APPDATA` pointed to a
scratch folder), as in Phases 11–15. Every new control is checked at 1024 and 1440 px window width.

Fixtures: generated outside the repository (ffmpeg `testsrc2` video with sound, a sine-tone WAV), as in Phases 10–15:
a video with sound longer than 10 s, a second video, an audio file, an image; a timeline with two video tracks and one
audio track, a dissolve and fades.

Status column — kinds of evidence, never mixed:
- **planned** — written at 16.2, not implemented yet;
- **auto (16.x)** — covered by automated tests only, not checked in the real app;
- **app 16.x (Claude), passed** — checked in the real app by Claude while implementing step 16.x;
- **16.7 (Claude): PASS / FAIL / BLOCKED / NOT RUN** — the formal Step 16.7 run;
- **PO** — run by the product owner.

## Source viewer (Steps 16.4 / 16.5)

| # | Scenario | Expected | Automated coverage | Status |
|---|---|---|---|---|
| 1 | Double-click a video in the Media Browser | The Preview switches to Source, shows the asset's first frame and its duration; the timeline playhead stays | | planned |
| 2 | Play / J / K / L / ← / → / Home / End in Source | The source plays with sound and steps; the timeline playhead never moves | | planned |
| 3 | I / O in Source, ✕ | The source range is marked on the Source bar and cleared by ✕; the project stays clean | | planned |
| 4 | Loop in Source with a range | Playback loops over [In, Out) | | planned |
| 5 | Back to Timeline | The timeline frame at the playhead is shown; I / O set the timeline range again | | planned |
| 6 | An image, a missing asset, an asset still analyzing | Not opened; a message | | planned |
| 7 | New / Open while in Source | Source is closed; the ranges are gone | | planned |

## Insert / Overwrite (Steps 16.3 / 16.6)

| # | Scenario | Expected | Automated coverage | Status |
|---|---|---|---|---|
| 8 | `,` with the playhead inside a clip on V1 | The clip is split, its right part and the later clips of V1 move right by the range; other tracks and markers stay; the playhead at the end of the new clip, which is selected | | planned |
| 9 | `.` over a cut with a dissolve | The covered clips are trimmed / removed; the dissolve is removed with the note; Undo restores exactly | | planned |
| 10 | Target track by the selection | With a clip on V2 selected the edit goes to V2; with nothing selected to V1 (audio: A1) | | planned |
| 11 | Locked target, during an export | Refused with a message; nothing changes | | planned |
| 12 | An audio asset | Goes onto the audio target as an audio clip | | planned |

## Regression (Step 16.7)

| # | Scenario | Expected | Status |
|---|---|---|---|
| R1 | A Phase 15 project | Opens and saves byte for byte; `formatVersion` 3 | planned |
| R2 | Export | The default export identical to Phase 15's for the same project | planned |
| R3 | Phase 15 tools | Q / W, ripple trim, slip, In / Out loop and range export as before | planned |
| R4 | No ffmpeg | The Source viewer reports playback unavailable like the Preview; no crash | planned |
