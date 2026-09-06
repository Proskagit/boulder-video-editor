# AI Video Editor

A simplified, desktop-first video editor (Windows 10/11 x64, Avalonia UI, .NET 8),
architected so professional-grade features can be layered in over time without a
rewrite. See `docs/DEVELOPMENT_PLAN.md` for the phased roadmap — **this checkout
is Phase 0: architecture skeleton only.** There is no media import, timeline
editing, or export yet; that lands in Phases 1–7.

## Requirements

- **.NET 8 SDK** (or newer LTS) — https://dotnet.microsoft.com/download
- Windows 10/11 x64 for the shipping target. The code also builds and runs on
  Linux/macOS during development since Avalonia is cross-platform; only the
  packaging/manifest is Windows-specific.
- **FFmpeg** — not required to build or run Phase 0 (nothing calls it yet), but
  will be required starting Phase 2. Once needed, `AiVideoEditor.Infrastructure`
  will look for it on `PATH` or a configured path; the app never silently
  assumes it's present.

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
  Core/            Domain model (Project, Track, Clip, Timeline, MediaAsset, ...),
                   service interfaces, MediaTime, the undo/redo Command pattern.
                   No dependency on Avalonia, FFmpeg, or any concrete infra.
  Infrastructure/  Serilog logging setup, app folder layout, user-facing error
                   translation. Implements cross-cutting Core interfaces.
  Video/           (Phase 2+) FFmpeg-backed decode/thumbnail/probe operations.
  Audio/           (Phase 2/8) Audio decode, mixing, waveform generation.
  Timeline/        (Phase 3) Timeline editing commands (move/trim/split/snap).
  Media/           (Phase 2) Media Browser import + metadata + thumbnail cache.
  Effects/         (Phase 6+) Effect/transition definitions and parameter schemas.
  Export/          (Phase 7) FFmpeg render/export pipeline.
  Project/         (Phase 5) project.json persistence, autosave, missing media.
tests/
  Core.Tests/      Unit tests for the dependency-free domain layer.
docs/
  DEVELOPMENT_PLAN.md   Phase-by-phase roadmap and standing architectural rules.
```

Dependencies flow one way: `App` → `UI`/`Infrastructure`/subsystems → `Core`.
`Core` depends on nothing but the BCL and logging abstractions, so the domain
model and undo/redo engine can be unit-tested without Avalonia or FFmpeg.

## Why these choices

- **Avalonia + MVVM** — cross-platform now, matches the "future macOS" goal in
  the spec, and keeps UI declarative (XAML) with no logic in code-behind.
- **Command pattern for undo/redo** (`IUndoableCommand` / `IUndoRedoService` in
  `Core.Common`) — every editing operation from Phase 3 onward is a discrete,
  reversible command, not a full-project snapshot.
- **`MediaTime`** — a 100ns-tick time value distinct from any UI frame rate, so
  the timeline's internal representation stays frame-rate-independent as the
  spec requires.
- **FFmpeg behind `IVideoEngine`/`IExportService`** — nothing in `UI` or
  `Timeline` calls FFmpeg directly; concrete implementations arrive in Phase 2
  (probing/thumbnails) and Phase 7 (export), and can be swapped or mocked.
- **Serilog with per-area log files** (`app-*.log`, `ffmpeg-*.log`,
  `export-*.log`, `errors-*.log` under `%LOCALAPPDATA%\AiVideoEditor\logs`) —
  keeps FFmpeg/export noise separate from general app logs while still
  collecting every error in one place for quick triage.

## Status

Phase 0 only: the app launches into a single window that proves the DI
container, Core services (`IUndoRedoService`), and logging pipeline are wired
correctly end to end (a demo counter button executes an `IUndoableCommand`).
No media, timeline, or export functionality exists yet.
