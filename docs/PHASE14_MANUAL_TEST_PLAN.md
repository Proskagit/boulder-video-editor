# Phase 14 — manual test plan

Phase 14 (D029) is a stabilization / technical-debt phase with no new user functionality, so this plan holds only the
regression run of the closeout (Step 14.8): it checks that nothing the user sees has changed. Written at Step 14.2 as a
skeleton; a step that touches code next to one of these areas adds its scenario here. Run in the real app
(`dotnet run --project src/App/App.csproj`, ffmpeg / ffprobe on PATH unless the scenario says otherwise) with an
isolated profile (`USERPROFILE` / `LOCALAPPDATA` pointed to a scratch folder), as in Phases 11–13. Logs:
`%LOCALAPPDATA%\AiVideoEditor\logs`.

Status column — kinds of evidence, never mixed:
- **planned** — written at 14.2, not run yet;
- **14.8 (Claude): PASS / FAIL / BLOCKED / NOT RUN** — the formal Step 14.8 run;
- **PO** — run by the product owner.

## Regression (Step 14.8)

| # | Scenario | Expected | Status |
|---|---|---|---|
| R1 | A project saved by the Phase 13 build (`ed40b74`, built apart), with non-default canvas, rate and export settings | Opens with every setting; a plain Save writes it byte for byte as Phase 13 did (`formatVersion` 3) | planned |
| R2 | `docs/EXPORT_MANUAL_TEST_PLAN.md` — the default export through the UI | Passes as before; the MP4 as in Phase 13 at the default settings | planned |
| R3 | Autosave and recovery (the area of 14.3): edit, wait for the autosave, end the process, start again | The recovery offer (Recover / Discard / Not now) as before; Recover restores the edit | planned |
| R4 | FFmpeg found / missing (the area of 14.4): start with ffmpeg / ffprobe on PATH, then with them hidden from PATH | Found: analysis, thumbnails, playback, export as before; missing: the same messages as in Phase 13, no crash | planned |
| R5 | `src/Effects` (only if removed at 14.7): open, edit, save and export a project with every Phase 7–13 clip property | As before — the solution builds, `project.json` unchanged | planned |

## Results log

(empty — filled at Step 14.8)
