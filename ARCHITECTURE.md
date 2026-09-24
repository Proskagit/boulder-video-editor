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

The solution contains 11 application projects under `src/` plus five test
projects (`tests/Core.Tests`, `tests/Project.Tests`, `tests/Timeline.Tests`, `tests/UI.Tests`,
`tests/Video.Tests` — the last one runs ffmpeg integration tests and skips without ffmpeg).

| Project | Responsibility | State |
|---|---|---|
| App | Composition root: `Program.Main`, Generic Host, Serilog, DI (`Composition/ServiceCollectionExtensions.cs`), `appsettings.json` | Implemented |
| UI | Avalonia Views + ViewModels, UI services (file/folder picker, dialogs, status, import workflow, project file workflow, analysis coordinator). References Core only | Implemented |
| Core | Domain entities, service interfaces, `MediaTime`, `IUndoableCommand` / `UndoRedoService`. No infra dependencies | Implemented |
| Infrastructure | Serilog setup, `AppPaths` (incl. the recovery folder), `FfprobeLocator` / `FfmpegLocator` + `FfmpegOptions`, `ErrorTranslator` | Implemented |
| Video | `FfprobeMediaAnalysisService` (ffprobe process + JSON parsing); `FfmpegVideoDecoder` (ffmpeg CLI → BGRA frames + PTS); `FfmpegAudioDecoder` (ffmpeg CLI → 48 kHz stereo float); shared `FfmpegProcess` | Probe + video/audio decode |
| Media | `MediaImportService` (extension validation, file size) | Implemented |
| Project | `ProjectService` (current project, duplicate detection, New/Open/Save/Save As, dirty tracking, missing media, recovery restore); `Persistence/` (`ProjectFileDto`, `ProjectSerializer`, `ProjectFileStore`, `RecoveryStore`); `AutosaveService` | Implemented (Phase 6) |
| Timeline | `TimelineEditService` (add/move/trim/split/delete/add track, snapping), `EditPlan`, `TimelineValidator`, `FrameRateRegrid`, undoable commands | Implemented (Phase 4) |
| Audio | `WasapiAudioOutput` (NAudio.Wasapi 2.2.1, WASAPI shared mode) | Playback output |
| Effects, Export | Later phases | Empty scaffolds |

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
  `IProjectService.NotifyTimelineChanged()` on Execute and Undo (raises `TimelineChanged`;
  dirty state follows the undo save point, D015). Rules: D008.
- Inspector (Phase 7): audio (volume 0–200 %, mute), transform (position, scale %, rotation,
  opacity %) and crop (per edge %); edits go to `SetClipProperties` one field at a time, the fields
  are refreshed from the model under a sync guard (no echo edits).
- Clip properties (Phase 7, D017): `SetClipProperties` with typed `VisualProperties` /
  `AudioProperties` / `TextProperties` (Core/Entities/ClipProperties.cs, limits in
  `ClipPropertyLimits`), validated by `ClipPropertyValidator` (Core; also used on load), applied by
  `SetClipPropertiesCommand` (absolute `ClipPropertyValues` before/after). Consecutive changes of
  the same properties of one clip merge into one undo step (`IMergeableCommand`), never into the
  save point and never right after an Undo.
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

## Playback (Phase 5)

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
- UI (D011, D020): `PreviewView`'s `DispatcherTimer` → `PreviewViewModel.Tick()` → `Update()`;
  the layers go to `CompositionView` (UI/Rendering), which draws a `CompositionDrawPlan`
  (canvas → control viewport, per-layer transform/opacity/crop, text, placeholders) with
  `DrawingContext`, one pair of `WriteableBitmap`s per layer. Playhead ↔ playback wiring lives in
  `MainWindowViewModel`: `TimelineViewModel.SeekRequested` (user moves only) → `SeekAsync`;
  `PreviewViewModel.PlaybackPositionChanged` → `TimelineViewModel.ShowPlaybackPosition` (no
  seek). Snapshots are rebuilt by `PreviewViewModel` on project/timeline/media events.
- Audio (D013): `PlaybackSnapshot.AudioSpans` → `AudioPipeline` (UI thread; look-ahead window,
  reader reuse on snapshot updates) → `AudioSpanReader` per clip (background ffmpeg decode into
  a bounded ring buffer, aligned by the stream's real first sample) → `AudioMixer`
  (`IAudioSampleSource`, device thread, never blocks) → `IAudioOutput` = `WasapiAudioOutput`
  (Audio project, NAudio). The output's played-frames clock is the playback master while it
  runs; Stopwatch otherwise. Core holds only backend-neutral contracts (`AudioContracts.cs`,
  `AudioTiming`).
- Volume/mute (Phase 7, D013 refinement): muted clips stay as silent spans (`AudioSpan.IsMuted`,
  mixed at `EffectiveGain` 0). A snapshot that `DiffersOnlyInPresentation` (D018) from the current one keeps the
  video pipeline, seek generation, picture and every reader; only `AudioPipeline.UpdateMix`
  republishes the gains.

### MediaTime

MediaTime uses 100-nanosecond ticks.

This is a deliberate precision decision and should be preserved unless an explicit architectural decision changes it.

## Composition (Phase 7, D018)

- `Core/Composition`: `CompositionMath.Layout(canvas, sourceSize, VisualProperties)` → `LayerGeometry`
  (source rect in pixels and normalized, fit, `Affine2D` crop-local → canvas, centre, opacity, bounds,
  `CoversCanvas`), `CompositionMath.TextTransform`, `FrameSize` / `RectD` / `PointD` / `Affine2D`;
  exact coverage via an internal BigInteger rational. Order: crop → fit (contain) → scale → rotation
  (clockwise, around the centre) → position (centre offset from the canvas centre, canvas px, Y down)
  → opacity. Canvas = project `FrameWidth × FrameHeight`.
- `PlaybackSnapshot.LayersAt(time)` → `CompositionLayer`s bottom to top (`PictureLayer` with
  `PictureSpan` + geometry, `TextLayer` with renderer-neutral `TextProperties` + transform); culls below
  an opaque video that provably covers the canvas.
- Playback of layers (D019): `VideoPipeline` decodes every layer `LayersAt` returns (readers keyed by
  clip, prefetch at the next edge, `UpdatePresentation` for presentation-only snapshots — no new seek
  generation, newly uncovered layers Pending); `PlaybackFrame.Layers` = `LayerPicture`s bottom to top
  (Frame/Text/Pending/Offline/Unsupported/DecodeError, per-layer late flag, placeholder area);
  `Picture` is the compatibility view the current Preview still shows (layer rendering: Step 7).
- Orientation (D019): metadata keeps the coded `Width/Height` and adds `DisplayRotation` /
  `DisplayWidth/Height` (what the decoder delivers with `-autorotate`); composition uses the display size.

## Media pipeline

Import → analysis flow:
`MediaImportWorkflow` (UI) → `IMediaImportService` (Media) →
`IProjectService.AddMediaAssets` (Project) → `MediaAnalysisCoordinator` (UI,
background) → `IMediaAnalysisService` (Video, ffprobe) → metadata written onto
the same `MediaAsset` → `IProjectService.MediaAssetsChanged`.

Metadata comes from ffprobe via `IMediaAnalysisService` (not `IVideoEngine`);
`IFfprobeLocator` resolves it from `Ffmpeg:FfprobePath` or PATH, `IFfmpegLocator` does the
same for ffmpeg (playback decoding). After Open only media without saved metadata that is
present on disk is analysed (`MediaAnalysisCoordinator.QueueWhereNeeded`); missing files are
never probed. `IVideoEngine`, `IThumbnailService` and `IExportService` are interfaces without
implementations.

## Project persistence (Phase 6)

Decisions: D014 (format, Open/Save, missing media), D015 (save point), D016 (autosave,
recovery, unsaved changes).

- On disk: a project folder with `project.json` (format v1). `ProjectSerializer` maps entities
  ⇄ DTOs (`ProjectFileDto.cs`; ticks as `long`, exact frame rates, no runtime state) and
  validates on load (incl. clip property ranges, D017); `ProjectFileStore` reads and writes atomically (temp + `File.Replace`).
- `ProjectService` (UI thread): Open reads + validates + marks missing media off the UI thread
  into a separate object, then replaces the project (history cleared, clean). Save / Save As
  snapshot text, history position and non-undoable change count together, write, and only
  then mark the save point. Events: `ProjectChanged`, `MediaAssetsChanged`, `TimelineChanged`,
  `SaveStateChanged` (dirty/name/folder), `ProjectSaved`.
- Dirty = undo history not at its save point (`IUndoRedoService.CurrentPosition` /
  `MarkSavePoint` / `IsAtSavePoint`) or a media import since the save.
- Autosave: `AutosaveService` (Project, implements Core `IAutosaveService`) every 2 min,
  snapshot on the UI thread, written by `RecoveryStore` to
  `%LOCALAPPDATA%\AiVideoEditor\recovery\<projectId>.json` (never `project.json`); per-project
  generation guards against an autosave resurrecting a file a Save made obsolete.
  `IProjectService.RestoreRecoveryAsync` opens a recovery file with the Open validation.
- UI: `ProjectFileWorkflow` — New / Open / Save / Save As / Close, folder picker
  (`IFilePickerService.PickFolderAsync`), Save / Don't Save / Cancel prompt (`IDialogService`,
  `AvaloniaDialogService`), startup recovery offer (`StartSessionAsync` on window Opened), autosave
  shutdown (`PrepareToCloseAsync` from `MainWindow.OnClosing`, which cancels the first close and
  closes again once approved). Toolbar commands and Ctrl+N / O / S / Shift+S call it; the window
  title comes from `MainWindowViewModel.Title`.
- Playhead, zoom and snapping are session state (D015): stored in `project.json`, never dirty,
  never undoable; `TimelineViewModel` reads zoom/snapping from the sequence on every `ProjectChanged`.

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

Verified against the source at the end of Phase 6 (branch `feat/phase-6-project-persistence`).
Re-check the code before relying on details that later phases may have changed.
