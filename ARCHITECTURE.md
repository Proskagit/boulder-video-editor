# Architecture

This document describes the verified architecture of the AI Video Editor.

## Current known stack

- .NET 8
- C#
- Avalonia
- CommunityToolkit.Mvvm
- Serilog
- FFmpeg / FFprobe

## Solution

The solution contains 11 application projects under `src/` plus one test
project (`tests/Core.Tests`).

| Project | Responsibility | State |
|---|---|---|
| App | Composition root: `Program.Main`, Generic Host, Serilog, DI (`Composition/ServiceCollectionExtensions.cs`), `appsettings.json` | Implemented |
| UI | Avalonia Views + ViewModels, UI services (file picker, status, import workflow, analysis coordinator). References Core only | Implemented |
| Core | Domain entities, service interfaces, `MediaTime`, `IUndoableCommand` / `UndoRedoService`. No infra dependencies | Implemented |
| Infrastructure | Serilog setup, `AppPaths`, `FfprobeLocator` + `FfmpegOptions`, `ErrorTranslator` | Implemented |
| Video | `FfprobeMediaAnalysisService` (ffprobe process + JSON parsing) | Implemented (probe only) |
| Media | `MediaImportService` (extension validation, file size) | Implemented |
| Project | `ProjectService` (in-memory project, duplicate detection); Open/Save throw `NotSupportedException` | Partial |
| Timeline | Timeline editing logic (Phase 4) | Empty scaffold |
| Audio, Effects, Export | Later phases | Empty scaffolds |

Dependencies flow one way: App → UI / Infrastructure / subsystems → Core.

## Core domain

Domain types (`src/Core/Entities`):
- `Project` — root aggregate: `MediaAssets`, `Timeline`, `Settings`, `LastExportSettings`, `IsDirty`
- `Sequence` — the editable timeline. There is no class named `Timeline`:
  `Project.Timeline` is a property of type `Sequence` (defined in `Timeline.cs`
  together with `Track`). Holds `VideoTracks`, `AudioTracks`, `Markers`,
  `PlayheadPosition`, `ZoomPixelsPerSecond`, `SnappingEnabled`
- `Track` — lane of clips (`Type`, `Order`, mute/hide/lock)
- `Clip` → `MediaBackedClip` (`SourceIn`/`SourceOut`/`Speed`) → `VideoClip`, `AudioClip`, `ImageClip`; plus `TextClip`
- `MediaAsset` + `MediaMetadata` + `MediaAnalysisStatus`
- `ExportSettings`, `ProjectSettings`, `Effect`, `Transition`, `Marker`
- `MediaTime`

No code creates `Track` or `Clip` instances yet; the Timeline panel shows mock data.

### MediaTime

MediaTime uses 100-nanosecond ticks.

This is a deliberate precision decision and should be preserved unless an explicit architectural decision changes it.

## Media pipeline

Import → analysis flow:
`MediaImportWorkflow` (UI) → `IMediaImportService` (Media) →
`IProjectService.AddMediaAssets` (Project) → `MediaAnalysisCoordinator` (UI,
background) → `IMediaAnalysisService` (Video, ffprobe) → metadata written onto
the same `MediaAsset` → `IProjectService.MediaAssetsChanged`.

Only ffprobe is integrated, via `IMediaAnalysisService` (not `IVideoEngine`).
`IFfprobeLocator` resolves the path from `Ffmpeg:FfprobePath` or PATH. There is
no ffmpeg locator yet. `IVideoEngine`, `IThumbnailService`, `IPlaybackService`,
`IExportService`, `IAutosaveService` are interfaces without implementations.

## MVVM

CommunityToolkit.Mvvm is part of the existing stack.

Prefer the established project conventions for:
- ViewModels
- Observable properties
- Commands
- Binding
- Services

## Logging

Serilog is part of the existing stack.

Prefer the existing logging abstraction and configuration.

## Architecture change policy

Major architectural changes require explicit user approval.

Routine refactoring needed to implement a feature does not.

## Verification note

Verified against the source at the Phase 3 commit (`a8e5bac`). Re-check the
code before relying on details that later phases may have changed.
