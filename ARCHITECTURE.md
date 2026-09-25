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

The solution contains 11 application projects under `src/` plus eight test
projects (`tests/Core.Tests`, `tests/Project.Tests`, `tests/Timeline.Tests`, `tests/UI.Tests`,
`tests/Export.Tests`, `tests/Rendering.Tests`, `tests/Video.Tests`, `tests/ExportEndToEnd.Tests` — Video.Tests runs
ffmpeg integration tests and skips without ffmpeg; Export.Tests uses the Preview's pipeline and fakes as the oracle of
the export contract; Rendering.Tests initialises Avalonia like the app and checks pixels of the Preview control and the
export rasterizer; ExportEndToEnd.Tests runs whole exports — preflight → `ExportService` → real decoders, Avalonia
rasterizer, ffmpeg encoder → MP4 checked with ffprobe — and skips without ffmpeg).

| Project | Responsibility | State |
|---|---|---|
| App | Composition root: `Program.Main`, Generic Host, Serilog, DI (`Composition/ServiceCollectionExtensions.cs`), `appsettings.json` | Implemented |
| UI | Avalonia Views + ViewModels, UI services (file/folder picker, dialogs, status, import workflow, project file workflow, analysis coordinator). References Core only | Implemented |
| Core | Domain entities, service interfaces, `MediaTime`, `IUndoableCommand` / `UndoRedoService`. No infra dependencies | Implemented |
| Infrastructure | Serilog setup, `AppPaths` (incl. the recovery folder), `FfprobeLocator` / `FfmpegLocator` + `FfmpegOptions`, `ErrorTranslator` | Implemented |
| Video | `FfprobeMediaAnalysisService` (ffprobe process + JSON parsing); `FfmpegVideoDecoder` (ffmpeg CLI → BGRA frames + PTS); `FfmpegAudioDecoder` (ffmpeg CLI → 48 kHz stereo float); shared `FfmpegProcess` | Probe + video/audio decode |
| Media | `MediaImportService` (extension validation, file size) | Implemented |
| Project | `ProjectService` (current project, duplicate detection, New/Open/Save/Save As, dirty tracking, missing media, recovery restore); `Persistence/` (`ProjectFileDto`, `ProjectSerializer`, `ProjectFileStore`, `RecoveryStore`); `AutosaveService` | Implemented (Phase 6; format v2 since Phase 7, D022) |
| Timeline | `TimelineEditService` (add/move/trim/split/delete/add track, snapping; clip properties, text clips, speed), `EditPlan`, `TimelineValidator`, `FrameRateRegrid`, undoable commands; the playback engine (`Playback/`) | Implemented (Phases 4, 5, 7) |
| Audio | `WasapiAudioOutput` (NAudio.Wasapi 2.2.1, WASAPI shared mode) | Playback output |
| Export | Offline export orchestration (Phase 8, D023): renders an `ExportJob` with the Core rules and hands frames/audio to an encoder. References Core only; no FFmpeg or UI types | `ExportService` (Step 6) over `ExportFrameSource` / `ExportPictureReader` (Step 2), `ExportAudioSource` / `ExportAudioReader` (Step 4); UI in Step 7 |
| Effects | Later phases | Empty scaffold |

Dependencies flow one way: App → UI / Infrastructure / subsystems → Core.

## Core domain

Domain types (`src/Core/Entities`):
- `Project` — root aggregate: `MediaAssets`, `Timeline`, `Settings`, `LastExportSettings`, `IsDirty`
- `Sequence` — the editable timeline. There is no class named `Timeline`:
  `Project.Timeline` is a property of type `Sequence` (defined in `Timeline.cs`
  together with `Track`). Holds `VideoTracks`, `AudioTracks`, `Markers`,
  `PlayheadPosition`, `ZoomPixelsPerSecond`, `SnappingEnabled`
- `Track` — lane of clips (`Type`, `Order`, mute/hide/lock)
- `Clip` → `MediaBackedClip` (`SourceIn`/`SourceOut`/`Speed` — exact `ClipSpeed`, timing rule `SpeedTiming`, D022) → `VideoClip`, `AudioClip`, `ImageClip`; plus `TextClip`
- `MediaAsset` + `MediaMetadata` + `MediaAnalysisStatus`
- `ExportSettings` (last output path + fixed format enums; session state, D023), `ProjectSettings`, `Effect`,
  `Transition`, `Marker` (effects and transitions are stored but neither played nor exported)
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
  opacity %), crop (per edge %) and text (content, font from the installed fonts via
  `IFontCatalog`, size, `#RRGGBB` color with a swatch, alignment); edits go to `SetClipProperties`
  one field at a time, the fields are refreshed from the model under a sync guard (no echo edits).
  Numeric text rules: `NumericInput`; text clips: D021.
- Speed (Phase 7, D022): `ITimelineEditService.SetClipSpeed` (start and source range kept, frames
  from `SpeedTiming.FramesFor`, merged undo via `SetClipSpeedCommand`); speed is part of `ClipState`
  so every timing command restores it; playback maps timeline → source with the speed (video sample
  points, audio `AudioTiming`), the FFmpeg audio decoder adds `apad` + `atempo` and compensates its
  latency; `project.json` v2.
- Text clips (Phase 7, D021): `ITimelineEditService.AddTextClip(start)` — topmost video track,
  frame-grid start, 5 s, one "Add Text" step; "+ Text" in the timeline header adds at the playhead
  and selects the clip. The timeline label of a text clip is its first line (`(empty text)` for
  blank text), recomputed on every timeline refresh.
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
  `SpanReader` for the visible clip plus the next one within the prefetch window (since Phase 7:
  one per visible layer, D019); each reader
  decodes in the background into a bounded buffer and returns a frame only when certain.
  The UI polls `Update()` each tick; nothing is pushed to the UI thread.
- UI (D011, D020): `PreviewView`'s `DispatcherTimer` → `PreviewViewModel.Tick()` → `Update()`;
  the layers go to `CompositionView` (UI/Rendering), which draws a `PreviewDrawPlan` — the shared Core
  `CompositionDrawPlan` (canvas → control viewport, per-layer transform/opacity/crop, text) plus placeholders —
  with `CompositionPainter` (`DrawingContext`, also used by the export), one pair of straight-alpha
  `WriteableBitmap`s per layer. Playhead ↔ playback wiring lives in
  `MainWindowViewModel`: `TimelineViewModel.SeekRequested` (user moves only) → `SeekAsync`;
  `PreviewViewModel.PlaybackPositionChanged` → `TimelineViewModel.ShowPlaybackPosition` (no
  seek). Snapshots are rebuilt by `PreviewViewModel` on project/timeline/media events.
- Audio (D013): `PlaybackSnapshot.AudioSpans` → `AudioPipeline` (UI thread; look-ahead window,
  reader reuse on snapshot updates) → `AudioSpanReader` per clip (placement: Core `AudioPlacement`, shared with the export) (background ffmpeg decode into
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
  the Preview renders the layers (D020). `PlaybackFrame.Picture` (topmost picture layer) is no longer
  used by the UI; it stays in Core as the oracle of the playback tests (deferred cleanup).
- Orientation (D019): metadata keeps the coded `Width/Height` and adds `DisplayRotation` /
  `DisplayWidth/Height` (what the decoder delivers with `-autorotate`); composition uses the display size.
- Export (D023): renders the same snapshot, layers and geometry offline — no second implementation of
  these rules and no ffmpeg filtergraph; text is rasterized like the Preview (a missing font falls back
  the same way and is a preflight warning); clip speed follows D022 (exact mapping, pitch kept).

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
never probed. `IVideoEngine` is an interface without implementation, reserved for thumbnails/waveforms
(Phase 9; `IThumbnailService` exists only as a commented-out registration). The export is `IExportService`
(D023).

## Project persistence (Phase 6)

Decisions: D014 (format, Open/Save, missing media), D015 (save point), D016 (autosave,
recovery, unsaved changes).

- On disk: a project folder with `project.json` (format v2 since Phase 7: the clip speed as an exact
  fraction `speedRatio`, D022; v1 files are read when their speed is 1 and saved as v2; files of a
  newer version are refused). `ProjectSerializer` maps entities
  ⇄ DTOs (`ProjectFileDto.cs`; ticks as `long`, exact frame rates, no runtime state) and
  validates on load (incl. clip property ranges, D017, and the speed timing invariant, D022);
  `ProjectFileStore` reads and writes atomically (temp + `File.Replace`).
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

## Export (Phase 8, D023)

- Principle: the export is an offline rendering of the Preview. Video: `PlaybackSnapshot` → frame n at
  `MediaTime.FromFrame(n)` → `LayersAt` / composition plan / `SourceFrameSelector` → BGRA canvas → encoder.
  Audio: `AudioSpans` → `AudioTiming` placement + the playback audio decoder → 48 kHz stereo float → encoder.
  Reads are blocking: no late frames or underrun silence; a decode failure aborts the export — also a decoder
  that fails after delivering data (`StrictEnd` on the export's decode requests; playback keeps its old end handling).
- Source frames (Step 2): `ExportFrameSource` → per output frame `LayersAt` + one `ExportPictureReader` per
  visible picture layer (sequential decode at full resolution, software; the last frame at or before the
  D009/D022 sample point, hold-first/hold-last), readers opened/closed with the layer set like the Preview's
  `VideoPipeline`.
- Composition (Step 3): `ExportFrame.DrawPlan()` = Core `CompositionDrawPlan` at the canvas size (the Preview's plan
  through identity) → `ICompositionRasterizer` (Core) = `AvaloniaCompositionRasterizer` (UI/Rendering): Avalonia
  offscreen `RenderTargetBitmap` + `CompositionPainter`, the Preview's drawing routine; runs off the UI thread,
  one instance per export; BGRA canvas, opaque black background.
- Audio (Step 4): `ExportAudioSource` → per audible span an `ExportAudioReader` (blocking, placement by Core
  `AudioPlacement`) → Core `AudioMix` (Σ × gain, clamp) → exactly `AudioSampleCount` frames of 48 kHz stereo float;
  the same samples as the Preview's `AudioPipeline` + `AudioMixer`.
- Encoder (Step 5): Core `IExportEncoder` / `IExportEncoding` (audio first, then frames, then complete; temporary
  files moved into place on success) → Video `FfmpegExportEncoder`: pass 1 PCM → AAC-LC 192k in a temporary m4a,
  pass 2 BGRA → BT.709 limited 4:2:0 libx264 CRF 18 medium + the audio copied → MP4 `+faststart`.
- Orchestration (Step 6): `ExportService` (Export) = `IExportService`. Preparing: one rasterizer from the
  `Func<ICompositionRasterizer>` the app registers (`AvaloniaCompositionRasterizer`; Export has no rendering backend)
  and `IExportEncoder.StartAsync`; Audio: `ExportAudioSource` → `WriteAudioAsync` in 0.5 s chunks until the source
  ends; Video: per frame `ExportFrameSource.GetFrameAsync` → `ExportFrame.DrawPlan()` → `Render` into one reused
  canvas (stride = width · 4) → `WriteFrameAsync`; Finalizing: `CompleteAsync`. Runs on the thread pool
  (`Task.Run`). Progress: `ExportProgress` Preparing 0/1, Audio samples/`AudioSampleCount`, Video frames/`FrameCount`,
  Finalizing 0/1 → 1/1 only after `CompleteAsync`. The job is taken as the preflight made it (no repeated checks; the
  encoder rejects outputs it can't encode). Failures and cancellation propagate unchanged; every part is disposed
  (`await using`), so an unfinished encoding ends ffmpeg and deletes its temporary files — the service never
  touches the destination. DI (App): `IExportEncoder` → `FfmpegExportEncoder`, `Func<ICompositionRasterizer>` →
  `new AvaloniaCompositionRasterizer()`, `IExportService` → `ExportService`.
- Layers: Core (composition model, geometry, draw plan, the contract below) → a backend-specific
  rasterizer (chosen in Step 3; Core never references Avalonia) → Export (orchestration) → Video (FFmpeg
  encoder: arguments, pipes, stderr).
- Contract (`Core/Export`): `ExportPreflight.Check(project, outputPath, environment)` on the UI thread builds
  the snapshot, collects every issue (errors block: empty timeline, odd canvas, output path/folder, output =
  project media, ffmpeg missing, media offline / not analysed / unsupported — only clips that reach the
  output; warning: missing font) and returns an `ExportJob` (snapshot + full output path) when nothing
  blocks. `ExportOutput` = canvas size, exact project frame rate, whole frames covering the duration, the
  matching 48 kHz sample count; `ExportFormat` = MP4 / H.264 CRF 18 medium / AAC 48 kHz stereo 192 kbps.
  `IExportService`: `IsAvailableAsync`, `ExportAsync(job, progress, ct)` → temp file moved into place on
  success, `ExportException` / cancellation leave nothing behind.
- UI (Step 7): Toolbar Export → `ExportWorkflow` (UI/Services): preflight (without the output file) → errors stop,
  warnings may be accepted → save-file picker → preflight with the file (the job) → replace confirmation → `EditingLock`
  + modal `AvaloniaExportProgressDialog` (`ExportProgressViewModel`, pulled by a timer; Cancel = the token) →
  `IExportService.ExportAsync` → outcome message per `ExportFailure` / cancelled / unexpected. `EditingLock` is shared
  by Toolbar, Timeline, Inspector and Media Browser (`CanExecute` / edit guards). `LastExportSettings` is session-only
  state (not dirty, not undoable, not serialized), updated after a successful export. Manual plan: `docs/EXPORT_MANUAL_TEST_PLAN.md`.
- Parity verification (Step 8, tests only): `tests/ExportEndToEnd.Tests` compares the export with the Preview's own
  pipeline and control — byte-equal at the canvas size for sources ≤ 1280 × 720, the D023 tolerances (`ParityMetrics`:
  geometry ±1 px on luma, flat colour R ≤ 4 / G ≤ 3 / B ≤ 4, same source frame) for larger sources and in a viewport,
  and MP4 → Preview through the codec (geometry, bar edges, same frame, sound timing ≤ 10 ms; colour not a criterion).
  The codec leg MP4 → export canvas has no tolerance (measured only, D023 Step 8). 4K scenes run only with
  `AIVE_HEAVY_TESTS=1`.

## Verification note

Verified against the source at the end of Phase 6 (branch `feat/phase-6-project-persistence`); the Phase 7
sections at the Phase 7 closeout, the Export section at the Phase 8 closeout (Step 8.7).
Re-check the code before relying on details that later phases may have changed.
