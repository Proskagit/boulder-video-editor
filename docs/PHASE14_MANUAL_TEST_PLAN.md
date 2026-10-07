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
| R1 | A project saved by the Phase 13 build (`ed40b74`, built apart), with non-default canvas, rate and export settings | Opens with every setting; a plain Save writes it byte for byte as Phase 13 did (`formatVersion` 3) | 14.8 (Claude): PASS — the Phase 13 app (`ed40b74`, `git archive`, built apart) opened a 640 × 360 / 25 fps fixture with Compact / Slow / 320 kbps, an `effects` entry and markers, analysed its 4 media and saved it; Phase 14 opened it: Project Settings showed "Landscape — 640 × 360", 25 FPS, Compact (CRF 28) / Slow / 320 kbps ("Current: 640 × 360 · 25 FPS"), Cancel; a plain Save wrote the file byte for byte as Phase 13 (SHA-256 identical) |
| R2 | `docs/EXPORT_MANUAL_TEST_PLAN.md` — the default export through the UI | Passes as before; the MP4 as in Phase 13 at the default settings | 14.8 (Claude): PASS — the same project without `settings.export` exported through the UI (Export → save dialog → "Export finished") by Phase 13 and by Phase 14: the two MP4 files byte-identical (SHA-256), decoded video 675 / audio 1266 frames identical; H.264 High 640 × 360 yuv420p BT.709 limited 25 fps, AAC-LC 48 kHz stereo, 27 s. The preflight refused a first attempt that pointed at `media\red.mp4` ("would overwrite the project media") — the file untouched |
| R3 | Autosave and recovery (the area of 14.3): edit, wait for the autosave, end the process, start again | The recovery offer (Recover / Discard / Not now) as before; Recover restores the edit | 14.8 (Claude): PASS — a marker added (title `*`), the timer autosave written 2 min after the start (3 markers in the recovery file, `project.json` still 2), the process killed; the next start offered "Recover unsaved work" (Recover / Discard / Not now); Recover restored the 3 markers (title `*`, status "Recovered unsaved changes … save the project to keep them."), Save wrote them, the recovery file removed |
| R4 | FFmpeg found / missing (the area of 14.4): start with ffmpeg / ffprobe on PATH, then with them hidden from PATH | Found: analysis, thumbnails, playback, export as before; missing: the same messages as in Phase 13, no crash | 14.8 (Claude): PASS — found: 4 media analysed, 3 thumbnails and 2 waveforms in the project cache, Play advanced the timecode with an audio device opened, exports in R2 / R5; missing (ffmpeg / ffprobe removed from PATH), Phase 13 and Phase 14 alike: four "FFprobe could not be found" analysis warnings, Export → "Export not possible" (ffmpeg not found + 4 media not analysed), status "Export not possible: 5 problems.", no crash, closed normally |
| R5 | `src/Effects` removed at 14.7: open, edit, save and export a project with every Phase 7–13 clip property | As before — the solution builds, `project.json` unchanged | 14.8 (Claude): PASS — a fixture with every Phase 7–13 property (position, scale, rotation, opacity, volume, mute, crop, speed 2× / 0.5×, a text clip, fades, a dissolve, markers, a muted track, Compact / Slow / 320) and an `effects` entry with parameters; Phase 14 and Phase 13 each: open → Save → ◆+ → Save → Export. The saved files of both builds identical (paths aside); every value of the fixture kept, `effects` with its parameters kept; the edited save differs only by the new marker and `modifiedAt`; the two MP4 files byte-identical (`crf=28.0`, `subme=8`, `-b:a 320000`, 675 frames, 27 s) |
## Results log

### 2026-10-07 — Step 14.8 closeout (Claude), `90caae9`, Debug

- Builds: the Phase 14 Debug build copied to a scratch folder; the Phase 13 app from `git archive ed40b74` built apart (no
  worktree, branch or commit). Both started with `--open-project` (Debug-only) and driven by UI Automation (Avalonia
  controls by name / `x:Name`; the native save dialog through `WM_SETTEXT` / `BM_CLICK`).
- Isolation: the runs set `LOCALAPPDATA` / `APPDATA` / `USERPROFILE` to a scratch profile; the app then resolved its data
  folder to `<exe folder>\AiVideoEditor` (logs, recovery, recent list) — the user's real profile untouched. The save
  dialog reported the scratch profile's missing `Desktop` once ("Расположение недоступно") — an artefact of that
  isolation, dismissed.
- Automation notes (not app defects): `Ctrl+S` by `keybd_event` didn't reach a background window — the Save button was
  invoked instead; a first Export confirm hit the `media` list item (also id `1`) and the dialog then offered
  `red.mp4` — the preflight refused it as expected; redone with the file-name edit set explicitly.
- R1–R5: see the table. Media: fixtures generated with ffmpeg in the scratch folder (outside the repository).
