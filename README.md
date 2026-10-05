# AI Video Editor

A simplified, desktop-first video editor (Windows 10/11 x64, Avalonia UI, .NET 8),
architected so professional-grade features can be layered in over time without a
rewrite. See `docs/DEVELOPMENT_PLAN.md` for the phased roadmap — **Phases 0–10
are complete** (architecture, UI skeleton, media import, ffprobe metadata
analysis, timeline editing, preview playback with audio, project persistence with
autosave/recovery, basic editing: speed, volume, opacity, transform, crop,
text; MP4 export; quality: stability, thumbnails, waveforms, hotkeys,
performance, polish, CI; fades and a cross dissolve). Phase 11 (media relink & recent projects) is accepted on its
local verification (2026-10-05); CI pending until the branch is published.

## Requirements

- **.NET 8 SDK** (or newer LTS) — https://dotnet.microsoft.com/download
- Windows 10/11 x64 for the shipping target. The code also builds and runs on
  Linux/macOS during development since Avalonia is cross-platform; only the
  packaging/manifest is Windows-specific.
- **FFmpeg** — not required to build or run. **ffprobe** (media metadata) and
  **ffmpeg** (preview decoding) are looked up at `Ffmpeg:FfprobePath` /
  `Ffmpeg:FfmpegPath` in `appsettings.json`, then on `PATH`. Without ffprobe, import
  still works and metadata is reported as unavailable; without ffmpeg, the preview
  shows no picture or sound, and thumbnails, waveforms and the export are unavailable.

## Getting started

```bash
git clone <repo-url>
cd AiVideoEditor
dotnet restore
dotnet build
dotnet run --project src/App/App.csproj
```

Running the tests:

```bash
dotnet test
```

The tests that need ffmpeg / ffprobe look for them on `PATH` and are skipped without them; the heavy 4K export
scenes run only with `AIVE_HEAVY_TESTS=1`. CI (`.github/workflows/ci.yml`, GitHub Actions on Windows) runs the full
suite on every pull request to `main` and push to `main` with FFmpeg 9.0.1 essentials (gyan.dev) and fails on any
skipped test other than those two 4K scenes.

## Solution layout

```
AiVideoEditor.sln
src/
  App/             Composition root: Program.cs, DI wiring, Serilog bootstrap,
                   Application/Window XAML shell. No business logic.
  UI/              Avalonia Views + ViewModels (MVVM). Talks to Core interfaces
                   only — never to FFmpeg or the filesystem directly.
  Core/            Domain model (Project, Sequence (the timeline), Track, Clip,
                   MediaAsset, ...), service interfaces, MediaTime, the undo/redo
                   Command pattern. No dependency on Avalonia, FFmpeg, or any
                   concrete infra.
  Infrastructure/  Serilog logging setup, app folder layout, ffprobe / ffmpeg location.
  Video/           ffprobe metadata analysis (Phase 3); ffmpeg video/audio decoding
                   for playback (Phase 5), export (Phase 8), thumbnails and waveforms
                   (Phase 9); the ffmpeg export encoder (Phase 8).
  Audio/           WASAPI audio output for playback (Phase 5).
  Timeline/        Timeline editing commands (Phases 4, 7) and the playback engine
                   (Phase 5).
  Media/           Media import: extension validation, file info (Phase 2);
                   thumbnails, waveforms and their cache (Phase 9).
  Effects/         Reserved for a generic effect stack. Empty: the Phase 10 fades
                   and cross dissolve live in Core, Timeline and UI.
  Export/          Offline export orchestration (Phase 8): renders the timeline frame
                   by frame like the Preview and hands frames and audio to the encoder.
  Project/         Current project state (Phase 2); project.json persistence,
                   save point, autosave/recovery, missing media (Phase 6); where the
                   media caches live (Phase 9).
tests/
  Core.Tests/      Unit tests for the dependency-free domain layer.
  Project.Tests/   Persistence, save point, autosave/recovery, missing media.
  Timeline.Tests/  Timeline editing and playback engine (fake decoder/clock).
  UI.Tests/        View models and UI workflows against real services.
  Video.Tests/     ffmpeg/ffprobe and audio-device integration tests.
  Export.Tests/    Export frame selection and orchestration (fakes).
  Rendering.Tests/ The shared Avalonia rasterizer (Preview == export bytes).
  ExportEndToEnd.Tests/  Whole exports with real ffmpeg; Preview ↔ export parity
                   (4K scenes only with AIVE_HEAVY_TESTS=1).
docs/
  DEVELOPMENT_PLAN.md          Phase-by-phase roadmap and standing architectural rules.
  EXPORT_MANUAL_TEST_PLAN.md   Manual export checks (Phase 8).
  PHASE9_MANUAL_TEST_PLAN.md   Manual checks of Phase 9, run at its closeout.
  PHASE10_MANUAL_TEST_PLAN.md  Manual checks of Phase 10 (fades, dissolves), run at its closeout.
  PHASE11_MANUAL_TEST_PLAN.md  Manual checks of Phase 11 (media re-check, relink, recent projects), run at its closeout.
```

Agent-oriented docs (`CLAUDE.md`, `ARCHITECTURE.md`, `ROADMAP.md`,
`DECISIONS.md`, `progress.md`) live in the repository root.

Dependencies flow one way: `App` → `UI`/`Infrastructure`/subsystems → `Core`.
`Core` depends on nothing but the BCL and logging abstractions, so the domain
model and undo/redo engine can be unit-tested without Avalonia or FFmpeg.

## Why these choices

- **Avalonia + MVVM** — cross-platform now, matches the "future macOS" goal in
  the spec, and keeps UI declarative (XAML) with no logic in code-behind.
- **Command pattern for undo/redo** (`IUndoableCommand` / `IUndoRedoService` in
  `Core.Common`) — every editing operation from Phase 4 onward is a discrete,
  reversible command, not a full-project snapshot.
- **`MediaTime`** — a 100ns-tick time value distinct from any UI frame rate, so
  the timeline's internal representation stays frame-rate-independent as the
  spec requires.
- **FFmpeg behind Core interfaces** — nothing in `UI` or `Timeline` calls
  FFmpeg directly. Probing is behind `IMediaAnalysisService` (Phase 3), decoding
  behind `IVideoDecoder` / `IAudioDecoder` (Phase 5), thumbnails and waveforms behind
  `IThumbnailService` / `IWaveformService` (Phase 9), export behind `IExportService` (Phase 8).
- **Serilog with per-area log files** (`app-*.log`, `ffmpeg-*.log`,
  `errors-*.log` under `%LOCALAPPDATA%\AiVideoEditor\logs`; an `export-*.log` sink
  is configured but nothing writes to it yet) — keeps FFmpeg noise separate from
  general app logs while still collecting every error in one place for quick triage.

## Status

Phases 0–10 complete (Phase 10 accepted 2026-10-01). Working: media import with validation and duplicate
detection, background ffprobe metadata analysis, Media Browser and Inspector,
timeline editing with undo/redo (tracks, clips, move, trim, split, delete, snapping),
preview playback with video and audio, and projects on disk: New / Open / Save /
Save As (a project folder with `project.json`), unsaved-changes prompt, window title
with `*`, autosave to a recovery file every 2 minutes with a recovery offer after a
crash, missing media shown as offline. Phase 7: per-clip speed (0.25×–4×, pitch kept),
volume (0–200 %) and mute, opacity, position/scale/rotation, crop and text clips, edited
in the Inspector with undo/redo, composited in a multi-layer Preview and saved in
`project.json` format v2 (v1 files still open). Phase 8: export of the timeline to MP4
(H.264 CRF 18 / AAC 48 kHz stereo, canvas size and exact project frame rate) with a
preflight, a progress dialog and Cancel, rendered like the Preview.

Phase 9: stability fixes (clean close, analysis cancelled on New / Open, at most
4 analyses at once, audio device changes, ffmpeg diagnostics in `ffmpeg-*.log`); Media
Browser thumbnails and timeline waveforms with a per-project cache; hotkeys J / K / L
(back one second / pause / play), loop (Ctrl+L), Ctrl+I import, Ctrl+E export, \ zoom
to fit — none of them fire while typing in a text field; a faster export (layers decoded
ahead and in parallel).

Phase 10: Fade In / Fade Out per clip (picture and its own sound, in whole frames, cut to
the clip when an edit shortens it) and a cross dissolve on the cut between two clips of a
video track (Dissolve in the timeline header, length in the Inspector, source handles,
the sound a hard cut), identical in the Preview and the export; `project.json` format v3
(v1 / v2 files still open).

Phase 11 (DECISIONS.md D026; accepted 2026-10-05 on its local verification, CI pending until the branch is published):
re-checking media availability during the session, relink of missing media (one file or a batch found next
to it, undoable) and a list of recent projects. Step 11.3: media
files are checked again when the window becomes active (at most every 3 s) and before an export — a file moved away
shows as offline, a file put back is online again without reopening the project. Step 11.4: the relink itself (checks,
warnings, undo) is implemented underneath, Step 11.5 the batch search in a chosen folder, Step 11.6 their UI: while
media is offline the Media Browser shows Relink… (the selected item) and Find Missing… (a folder), undoable like any edit.
Steps 11.7–11.8: `Recent ▾` next to Open lists the last 10 projects opened, saved as or recovered (kept in
`%LOCALAPPDATA%\AiVideoEditor\config\recent-projects.json`); an entry whose project can't be found is shown as
unavailable and can be removed with ✕.
