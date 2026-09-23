# AI Video Editor

A simplified, desktop-first video editor (Windows 10/11 x64, Avalonia UI, .NET 8),
architected so professional-grade features can be layered in over time without a
rewrite. See `docs/DEVELOPMENT_PLAN.md` for the phased roadmap — **Phases 0–3
are complete** (architecture, UI skeleton, media import, ffprobe metadata
analysis). Timeline editing, preview playback, project persistence and export
are not implemented yet (Phases 4–8).

## Requirements

- **.NET 8 SDK** (or newer LTS) — https://dotnet.microsoft.com/download
- Windows 10/11 x64 for the shipping target. The code also builds and runs on
  Linux/macOS during development since Avalonia is cross-platform; only the
  packaging/manifest is Windows-specific.
- **FFmpeg** — not required to build or run. Currently only **ffprobe** is used
  (media metadata analysis, Phase 3). `AiVideoEditor.Infrastructure` looks for it
  at `Ffmpeg:FfprobePath` in `appsettings.json`, then on `PATH`; if it is missing,
  import still works and metadata is reported as unavailable. ffmpeg itself
  (thumbnails, decode, export) will be required from later phases.

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
  Video/           ffprobe metadata analysis (Phase 3); decode (Phase 5) and
                   thumbnails (Phase 9) later.
  Audio/           (Phase 8/9) Audio mixing, waveform generation. Empty.
  Timeline/        (Phase 4) Timeline editing commands (move/trim/split/snap). Empty.
  Media/           Media import: extension validation, file info (Phase 2).
  Effects/         (Phase 7+) Effect/transition definitions and parameter schemas. Empty.
  Export/          (Phase 8) FFmpeg render/export pipeline. Empty.
  Project/         In-memory project state (Phase 2); project.json persistence,
                   autosave, missing media in Phase 6.
tests/
  Core.Tests/      Unit tests for the dependency-free domain layer.
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
  FFmpeg directly. Probing is implemented behind `IMediaAnalysisService`
  (Phase 3); `IVideoEngine` (thumbnails/decode) and `IExportService` (Phase 8)
  have no implementations yet.
- **Serilog with per-area log files** (`app-*.log`, `ffmpeg-*.log`,
  `export-*.log`, `errors-*.log` under `%LOCALAPPDATA%\AiVideoEditor\logs`) —
  keeps FFmpeg/export noise separate from general app logs while still
  collecting every error in one place for quick triage.

## Status

Phases 0–3 complete. Working: New Project, multi-file media import with
validation and duplicate detection, background ffprobe metadata analysis, Media
Browser and Inspector showing real file and technical properties.

Not yet working: the Timeline panel shows mock tracks/clips (Phase 4); the
Preview panel has no decoder and shows mock time values (Phase 5);
Open/Save (Phase 6) and Export (Phase 8) report "not implemented yet".
