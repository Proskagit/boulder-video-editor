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

The solution contains 11 application projects under `src/` plus four test
projects (`tests/Core.Tests`, `tests/Timeline.Tests`, `tests/UI.Tests`,
`tests/Video.Tests` — the last one runs ffmpeg integration tests and skips without ffmpeg).

| Project | Responsibility | State |
|---|---|---|
| App | Composition root: `Program.Main`, Generic Host, Serilog, DI (`Composition/ServiceCollectionExtensions.cs`), `appsettings.json` | Implemented |
| UI | Avalonia Views + ViewModels, UI services (file picker, status, import workflow, analysis coordinator). References Core only | Implemented |
| Core | Domain entities, service interfaces, `MediaTime`, `IUndoableCommand` / `UndoRedoService`. No infra dependencies | Implemented |
| Infrastructure | Serilog setup, `AppPaths`, `FfprobeLocator` + `FfmpegOptions`, `ErrorTranslator` | Implemented |
| Video | `FfprobeMediaAnalysisService` (ffprobe process + JSON parsing); `FfmpegVideoDecoder` (ffmpeg CLI → BGRA frames + PTS) | Probe + video decode |
| Media | `MediaImportService` (extension validation, file size) | Implemented |
| Project | `ProjectService` (in-memory project, duplicate detection); Open/Save throw `NotSupportedException` | Partial |
| Timeline | `TimelineEditService` (add/move/trim/split/delete/add track, snapping), `EditPlan`, `TimelineValidator`, `FrameRateRegrid`, undoable commands | Implemented (Phase 4) |
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

New projects get tracks V1 and A1. Clips are created only by `ITimelineEditService`.

## Timeline (Phase 4)

- Time: `MediaTime` ticks + rational `FrameRate`; exact integer frame grid (DECISIONS D006).
  Every clip edge lies exactly on the project grid.
- Project frame rate: provisional 30 FPS until the first video fixes it (D007).
- Editing: `ITimelineEditService` (Core) / `TimelineEditService` (Timeline). Each
  operation builds an `EditPlan` in frame indices, validates all affected tracks with
  `TimelineValidator` (type/track match, grid, ≥ 1 frame, no overlap, source limits),
  then executes one `IUndoableCommand` wrapped in `NotifyingCommand`, which calls
  `IProjectService.NotifyTimelineChanged()` on Execute and Undo (marks dirty, raises
  `TimelineChanged`). Rules: D008.
- UI: `TimelineViewModel` projects the `Sequence` (clip view models reused by Id),
  owns view state (zoom, playhead, selection, drag previews using the service's
  dry-run `CanMoveClips` / `PreviewTrim`) and never mutates the model directly.
  `TimelineView` code-behind only converts pointer/wheel/drag-drop events to
  content coordinates. Pixel ↔ time math: `TimelineCoordinateMapper`; all times sent
  to the domain are snapped to the frame grid first.
- Cross-panel wiring (selection → Inspector, Add to Timeline, playhead ↔ Preview/playback)
  lives in `MainWindowViewModel`. Keyboard shortcuts are routed in
  `MainWindow.OnKeyDown` and ignored while a TextBox has focus.
- Threading: the project model is mutated on the UI thread only.

## Playback (Phase 5, in progress)

- Which source frame a timeline frame shows: `Core/Playback/SourceFrameSelector`
  (pure, exact Int128; DECISIONS D009). Decoded frames carry `SourceTimestamp`
  (`Pts` + `TimeBase`); source time is measured from `MediaMetadata.StartTime`.
- Decoding: `IVideoDecoder` / `IVideoFrameStream` / `DecodedFrame` (Core/Playback,
  backend-neutral) implemented by `FfmpegVideoDecoder` (Video). ffmpeg is found by
  `IFfmpegLocator` / `FfmpegLocator` (`Ffmpeg:FfmpegPath`, then PATH). The decoder seeks a
  bounded preroll before the first sample point and retries with a larger preroll until the
  first frame is at or before it; `showinfo` PTS parsing stays internal to Video.
- Playback core (D011): `PlaybackSnapshotBuilder` (UI thread) → immutable `PlaybackSnapshot`
  → `IPlaybackService` / `PlaybackService` (Timeline/Playback). `PlaybackClock` = anchor +
  elapsed of an `IReferenceClock` (Stopwatch now, audio device later). `VideoPipeline` keeps a
  `SpanReader` for the visible clip plus the next one within the prefetch window; each reader
  decodes in the background into a bounded buffer and returns a frame only when certain.
  The UI polls `Update()` each tick; nothing is pushed to the UI thread.
- UI (D011): `PreviewView`'s `DispatcherTimer` → `PreviewViewModel.Tick()` → `Update()`;
  the picture goes to a `WriteableBitmap` in the view. Playhead ↔ playback wiring lives in
  `MainWindowViewModel`: `TimelineViewModel.SeekRequested` (user moves only) → `SeekAsync`;
  `PreviewViewModel.PlaybackPositionChanged` → `TimelineViewModel.ShowPlaybackPosition` (no
  seek). Snapshots are rebuilt by `PreviewViewModel` on project/timeline/media events.
- Planned: audio decoding + NAudio output (audio master clock).

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
