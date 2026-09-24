# AI Video Editor

A simplified, desktop-first video editor (Windows 10/11 x64, Avalonia UI, .NET 8),
architected so professional-grade features can be layered in over time without a
rewrite. See `docs/DEVELOPMENT_PLAN.md` for the phased roadmap — **Phases 0–7
are complete** (architecture, UI skeleton, media import, ffprobe metadata
analysis, timeline editing, preview playback with audio, project persistence with
autosave/recovery, basic editing: speed, volume, opacity, transform, crop,
text). Export and polish are not implemented
yet (Phases 8–9).

## Requirements

- **.NET 8 SDK** (or newer LTS) — https://dotnet.microsoft.com/download
- Windows 10/11 x64 for the shipping target. The code also builds and runs on
  Linux/macOS during development since Avalonia is cross-platform; only the
  packaging/manifest is Windows-specific.
- **FFmpeg** — not required to build or run. **ffprobe** (media metadata) and
  **ffmpeg** (preview decoding) are looked up at `Ffmpeg:FfprobePath` /
  `Ffmpeg:FfmpegPath` in `appsettings.json`, then on `PATH`. Without ffprobe, import
  still works and metadata is reported as unavailable; without ffmpeg, the preview
  shows no picture or sound. Export (Phase 8) will also need ffmpeg.

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
  Infrastructure/  Serilog logging setup, app folder layout, ffprobe location,
                   user-facing error translation.
  Video/           ffprobe metadata analysis (Phase 3); ffmpeg video/audio decoding
                   for playback (Phase 5); thumbnails (Phase 9) later.
  Audio/           WASAPI audio output for playback (Phase 5).
  Timeline/        Timeline editing commands (Phase 4) and the playback engine
                   (Phase 5).
  Media/           Media import: extension validation, file info (Phase 2).
  Effects/         (Phase 7+) Effect/transition definitions and parameter schemas. Empty.
  Export/          (Phase 8) FFmpeg render/export pipeline. Empty.
  Project/         Current project state (Phase 2); project.json persistence,
                   save point, autosave/recovery, missing media (Phase 6).
tests/
  Core.Tests/      Unit tests for the dependency-free domain layer.
  Project.Tests/   Persistence, save point, autosave/recovery, missing media.
  Timeline.Tests/  Timeline editing and playback engine (fake decoder/clock).
  UI.Tests/        View models and UI workflows against real services.
  Video.Tests/     ffmpeg/ffprobe and audio-device integration tests.
docs/
  DEVELOPMENT_PLAN.md   Phase-by-phase roadmap and standing architectural rules.
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
  behind `IVideoDecoder` / `IAudioDecoder` (Phase 5); `IVideoEngine` (thumbnails)
  and `IExportService` (Phase 8) have no implementations yet.
- **Serilog with per-area log files** (`app-*.log`, `ffmpeg-*.log`,
  `export-*.log`, `errors-*.log` under `%LOCALAPPDATA%\AiVideoEditor\logs`) —
  keeps FFmpeg/export noise separate from general app logs while still
  collecting every error in one place for quick triage.

## Status

Phases 0–7 complete. Working: media import with validation and duplicate
detection, background ffprobe metadata analysis, Media Browser and Inspector,
timeline editing with undo/redo (tracks, clips, move, trim, split, delete, snapping),
preview playback with video and audio, and projects on disk: New / Open / Save /
Save As (a project folder with `project.json`), unsaved-changes prompt, window title
with `*`, autosave to a recovery file every 2 minutes with a recovery offer after a
crash, missing media shown as offline. Phase 7: per-clip speed (0.25×–4×, pitch kept),
volume (0–200 %) and mute, opacity, position/scale/rotation, crop and text clips, edited
in the Inspector with undo/redo, composited in a multi-layer Preview and saved in
`project.json` format v2 (v1 files still open).

Not yet working: Export (Phase 8, reports "not implemented yet"); relink of missing media
and recent projects are not planned yet. Known issue: closing the app after a project with
media was open can hang the process (the window closes, the process stays; end it in Task
Manager) — being tracked separately.
