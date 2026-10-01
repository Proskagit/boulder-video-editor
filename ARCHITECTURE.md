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
| UI | Avalonia Views + ViewModels, UI services (file/folder/save pickers, dialogs, status, import / project file / export workflows, analysis / thumbnail / waveform coordinators, `EditingLock`), `ShortcutRouter`, the Avalonia rasterizer (`UI/Rendering`). References Core only | Implemented |
| Core | Domain entities, service interfaces, `MediaTime`, `IUndoableCommand` / `UndoRedoService`. No infra dependencies | Implemented |
| Infrastructure | Serilog setup, `AppPaths` (incl. the recovery folder and the unsaved projects' cache root), `FfprobeLocator` / `FfmpegLocator` + `FfmpegOptions` | Implemented |
| Video | `FfprobeMediaAnalysisService` (ffprobe process + JSON parsing); `FfmpegVideoDecoder` (ffmpeg CLI → BGRA frames + PTS); `FfmpegAudioDecoder` (ffmpeg CLI → 48 kHz stereo float); `FfmpegExportEncoder`; shared `FfmpegProcess` | Probe, video/audio decode, export encoder |
| Media | `MediaImportService` (extension validation, file size); `ThumbnailService` / `WaveformService` and their cache files (Phase 9) | Implemented |
| Project | `ProjectService` (current project, duplicate detection, New/Open/Save/Save As, dirty tracking, missing media, recovery restore); `Persistence/` (`ProjectFileDto`, `ProjectSerializer`, `ProjectFileStore`, `RecoveryStore`); `AutosaveService`; `MediaCacheLocation` / `ThumbnailCacheLocation` / `WaveformCacheLocation` (Phase 9) | Implemented (Phase 6; format v2 since Phase 7, D022; v3 since Phase 10, D025) |
| Timeline | `TimelineEditService` (add/move/trim/split/delete/add track, snapping; clip properties, text clips, speed), `EditPlan`, `TimelineValidator`, `FrameRateRegrid`, undoable commands; the playback engine (`Playback/`) | Implemented (Phases 4, 5, 7) |
| Audio | `WasapiAudioOutput` (NAudio.Wasapi 2.2.1, WASAPI shared mode) | Playback output |
| Export | Offline export orchestration (Phase 8, D023): renders an `ExportJob` with the Core rules and hands frames/audio to an encoder. References Core only; no FFmpeg or UI types | `ExportService` over `ExportFrameSource` / `ExportPictureReader`, `ExportAudioSource` / `ExportAudioReader` |
| Effects | Reserved for a generic effect stack (out of Phase 10's scope, D025) | Empty — the Phase 10 fades and cross dissolve live in Core (`FadeRule`, `TransitionRules`, the snapshot), Timeline (edits) and UI; `Clip.Effects` is only persisted |

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
  `Transition`, `Marker` (`Clip.Effects` is stored but neither played nor exported; transitions are cross dissolves, below)
- Phase 10 (D025): `Clip.FadeIn` / `FadeOut` (durations; frames derived with `ToNearestFrame`);
  `Transition` anchored on a cut (`LeftClipId` / `RightClipId`, type `crossDissolve`), structural rules in
  `TransitionRules` (zone `⌊F/2⌋` before / `⌈F/2⌉` after the cut). Stored and validated since Step 10.3.
  Fades (Step 10.4): `Core/Playback/FadeRule` — ramp `(k+1)/(F+1)`, effective frames clamped to the clip, none on an
  edge with a dissolve (PO-8); `PlaybackSnapshotBuilder` puts the effective frames on picture, text and audio spans
  (presentation only); `LayersAt` multiplies the layer opacity by the fade (so a fading layer never occludes) and
  `NextPictureChange` counts ramp edges (prefetch); `AudioFadeEnvelope` + `AudioMix.Add(…, envelope, firstSample)`
  apply the per-sample gain in the Preview's mixer and the export alike; edits through `SetClipProperties`
  (`ClipPropertyChange.Fade`, merged undo), split moves the fades to the outer edges (`EditPlan.SetProperties`).
  UI (Step 10.5): the Inspector's FADES section (`FadeInFrames` / `FadeOutFrames` in whole frames, the time as
  timecode, the maximum never below the stored value, PO-8 notes from `TimelineClipSelection.DissolveAtStart` /
  `DissolveAtEnd`, disabled by the `EditingLock`); the timeline clip draws each effective ramp as a gradient band
  (`TimelineClipViewModel.FadeInWidth` / `FadeOutWidth`, computed by `TimelineViewModel.RefreshFades`).
  Dissolve edits (Step 10.6): `ITimelineEditService.AddTransition` / `RemoveTransition` / `SetTransitionDuration` /
  `MaxTransitionFrames`; `Timeline/DissolveHandles` (source handles = the trim limits, images / text unlimited);
  `EditPlan` carries transition adds / removes / updates and `ReconcileTransitions` (every edit: removed where the cut
  is gone, moved with both clips) before `TimelineEditService.Validate` checks zones (`TransitionRules.Validate` on the
  planned state) and the handles of touched dissolves; commands `AddTransitionCommand` / `RemoveTransitionCommand` /
  `UpdateTransitionCommand`. Split re-anchors and rejects inside a zone; a far-edge trim is clamped to the zone.
  Dissolve composition (Step 10.7): `PlaybackSnapshotBuilder` puts the zones on `VideoLayer.Dissolves`
  (`DissolveZone`: `[c − ⌊F/2⌋, c + ⌈F/2⌉)`) and the shown range on the pictures (`ExtendedStart` / `ExtendedEnd`);
  `LayersAt` returns A below B in a zone (B × `(j+1)/(F+1)`), `NextPictureChange` counts the zone edges, the export's
  picture reader serves the shown range; the sound is untouched.
  Dissolve UI (Step 10.8): `TimelineViewModel.AddDissolveCommand` (two selected clips; 1 s or the service's
  `MaxTransitionFrames` when shorter), `TimelineTrackViewModel.Transitions` / `TimelineTransitionViewModel` (the zone
  in pixels, drawn over the clips and not hit-testable: the view hit-tests a clip and its trim handles first, and only
  a press on a clip body inside a zone selects the dissolve — `TransitionAt` / `OnTransitionPressed` —, exclusive with
  the clip selection; a drag or trim preview lays the zones out as the release will leave them),
  `TransitionSelectionChanged` → `InspectorViewModel.ShowTransition` (DISSOLVE: `DissolveFrames`, the time, the longest
  that fits, `RemoveDissolveCommand`); Delete removes a selected dissolve; the `EditingLock` disables all of it.
  `SetClipSpeedCommand` carries the dissolves a speed change removes (one step with the speed, merged with the clip's
  next speed changes); `IUndoRedoService.NextUndo` lets a speed typed back to the step's start undo that step.
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
  lives in `MainWindowViewModel`. Keyboard shortcuts: one table in `UI/Common/ShortcutRouter` (key with exact
  modifiers → an existing command; D024 Step 9.6), called from `MainWindow.OnKeyDown` (bubbling, only keys no
  focused control consumed); nothing fires while a `TextBox` (also inside a `NumericUpDown`) has focus; a known
  shortcut whose command can't run (e.g. during an export) is consumed and does nothing. Loop (session state) is the
  Preview's: a playback update that reached the end is followed by Play (D011: from 0).
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
  elapsed of an `IReferenceClock`: the audio device's played-samples clock (`WasapiAudioOutput.Clock`) is the
  master while audio plays, `StopwatchReferenceClock` the fallback (D013). `VideoPipeline` keeps a
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
  the Preview renders the layers (D020). `IsBuffering` holds from a seek until the topmost picture
  layer is settled (its current frame or a placeholder; black when there is none). The former
  single-picture view `PlaybackFrame.Picture` / `IsPictureCurrent` was removed in Step 9.8 (D024);
  the playback tests read the layers.
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

Metadata comes from ffprobe via `IMediaAnalysisService`;
`IFfprobeLocator` resolves it from `Ffmpeg:FfprobePath` or PATH, `IFfmpegLocator` does the
same for ffmpeg (playback decoding). After Open only media without saved metadata that is
present on disk is analysed (`MediaAnalysisCoordinator.QueueWhereNeeded`); missing files are
never probed. Thumbnails use `IThumbnailService`, waveforms `IWaveformService` (below); the export is
`IExportService` (D023).

## Thumbnails (Phase 9 Step 9.4)

Decision: D024 "Refined in Step 9.4". Media Browser only; playback and export never read the cache.

- Core: `IThumbnailService` — `TryGetCached(asset, cacheFolder)` (never decodes) and
  `GetOrCreateAsync(asset, cacheFolder, ct)`; `Thumbnail` = packed BGRA, straight alpha. `IThumbnailCacheLocation` —
  `CurrentFolder` of the current project, `Changed`, `CleanUpUnsavedAsync` (the members of `IMediaCacheLocation`,
  Step 9.5).
- Media: `ThumbnailService` — source time `T = min(⌊Duration / 10⌋, 5 s)` (ticks from the start time), decoded by the
  app's `IVideoDecoder` (software, fit into 160 × 90, upright) and selected by `SourceFrameSelector` (D009: last frame
  at or before T). Cache file `{assetId:N}-{size:x}-{lastWriteUtcTicks:x}-v{rule}.thumb` (`AIVT` header + BGRA),
  written to a temporary name and moved; any other name, a damaged or unreadable file is a miss. Offline media
  (missing, or the file gone) is never decoded: the last cached file or none.
- Project: `ThumbnailCacheLocation` — saved `<project>/cache/thumbnails` (`AppPaths.ProjectCacheFolder`), unsaved
  `AppPaths.UnsavedCacheRoot/<projectId:N>/thumbnails`; on `ProjectSaved` the files are carried over (first
  Save moves, Save As copies; per asset its latest variant); at startup (`ProjectFileWorkflow.StartSessionAsync`)
  unsaved caches without a recovery file are removed. Nothing in `project.json` (`MediaAsset.ThumbnailPath` unused).
  The location logic is `MediaCacheLocation`'s, shared with the waveforms (Step 9.5).
- UI: `ThumbnailCoordinator` (UI/Services, singleton; the orchestration is `MediaCacheCoordinator<T>`'s, shared with the
  waveforms) — requests on `MediaAssetsChanged` for video / images with
  completed analysis and, cache only, offline media; one generation per project (`ProjectChanged` cancels the old
  one, its results are dropped); once per asset id and generation; cache reads without a slot, at most 2 makes at
  once; results and `ThumbnailReady` on the UI thread; `ShutdownAsync` from `MainWindowViewModel.PrepareToCloseAsync`.
  `MediaBrowserViewModel` sets `MediaBrowserItemViewModel.Thumbnail` from `Get(assetId)` when rows are built and on
  `ThumbnailReady`; the view draws it over the kind's colour tile through `ThumbnailBitmapConverter` (one
  `WriteableBitmap` per thumbnail instance, via `FrameBitmap`).

## Waveforms (Phase 9 Step 9.5)

Decision: D024 "Refined in Step 9.5". Timeline only, display only: playback and export never read the cache and the
waveform never changes the audio.

- Core: `IWaveformService` — `TryGetCached(asset, cacheFolder)` (never decodes), `GetOrCreateAsync(asset, cacheFolder,
  ct)`; `Waveform` — one byte peak (`⌈max(|L|, |R|) · 255⌉`, capped) per 256 source samples (48 kHz from the file's
  start time), `SampleCount` where the audio ends, `MaxPeak(from, to)`. `IWaveformCacheLocation` (an
  `IMediaCacheLocation`).
- Media: `WaveformService` — the file's audio decoded once by the app's `IAudioDecoder` (source position 0, 1×, strict
  end), samples placed by the stream's `FirstSampleIndex`; audio files and video with an `AudioCodec`, analysis
  completed; offline media never decoded (last cached file or none). Cache file
  `{assetId:N}-{size:x}-{lastWriteUtcTicks:x}-v{rule}.peaks` (`AIVW` header + peaks); key, atomic write and offline
  lookup shared with the thumbnails (`Media/Caching/SourceFileCache`).
- Project: `WaveformCacheLocation` — saved `<project>/cache/waveforms`, unsaved
  `AppPaths.UnsavedCacheRoot/<projectId:N>/waveforms`; carry-over on Save / Save As and the startup cleanup
  as the thumbnails' (`MediaCacheLocation`); each kind touches only its own folder and files.
- UI: `WaveformCoordinator` (UI/Services, singleton, a `MediaCacheCoordinator<Waveform>`) — candidates are the media of
  the timeline's clips (any track), requested on `TimelineChanged`, `MediaAssetsChanged` and at start; at most 2 makes
  at once with slots of its own; `WaveformReady` / `Get`; shut down with the window after the thumbnails.
  `TimelineViewModel` gives each `TimelineClipViewModel` a `ClipWaveform` (data, timeline start / end, source in,
  speed, volume, muted — clip or track —, lower half for video, zoom) on every refresh / relayout and on
  `WaveformReady`; `UI/Common/WaveformLayout` maps a pixel column to its source range by the clip's `AudioPlacement`
  (the playback / export rule, D013 / D022) and to a height `peak / 255 · volume / 2`; `UI/Rendering/WaveformView`
  draws, inside each clip under its label, only the columns in the timeline `ScrollViewer`'s viewport (dimmed when
  muted, the lower half for video).

## Project persistence (Phase 6)

Decisions: D014 (format, Open/Save, missing media), D015 (save point), D016 (autosave,
recovery, unsaved changes).

- On disk: a project folder with `project.json` (format v3 since Phase 10: clip fades and anchored transitions,
  D025; v2 since Phase 7: the clip speed as an exact fraction `speedRatio`, D022; v1 files are read when their speed
  is 1; v1 / v2 are read without fades and transitions and saved as v3; files of a newer version are refused). `ProjectSerializer` maps entities
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
- Media paths and missing media (D014): each asset is saved with its absolute `FilePath` and a `RelativePath` to the
  project folder (none on another volume); `ProjectSerializer.ResolveMediaPath` takes the absolute path if the file
  exists, else the relative one, else keeps the absolute path. `ProjectService.MarkMissingMedia` sets the runtime flag
  `MediaAsset.IsMissing` on Open and Recover, before the project becomes current. Missing media is never probed, never
  decoded for thumbnails / waveforms (cached results only), shown as "Media offline" / the Preview's placeholder, blocks
  the export preflight and can't be added to the timeline.
- Media availability during the session (Phase 11 Step 11.3, D026 §2): `IProjectService.RecheckMediaAsync` — the files
  are checked in one `Task.Run` (never on the UI thread), the result applied on the UI thread to the same project and
  only to assets whose path is unchanged; checks never overlap (a request while one runs is folded into one more check
  after it); changes raise `MediaAvailabilityChanged` (`Returned`, `Gone`) and then `MediaAssetsChanged`; never dirty,
  never undoable. Triggers: `MediaAvailabilityMonitor` (UI) on `MainWindow.Activated` — at most one check per 3 s, an
  activation inside that interval answered by one trailing check, stopped at close, changes reported in the status bar —
  and `ExportWorkflow` before its preflight (unthrottled). Reactions: `MediaAnalysisCoordinator` analyses a returned
  `Pending` / `Failed` asset (and refreshes a missing display size); an analysis whose asset went missing or got another
  path meanwhile writes nothing (a missing one gets its earlier status back). `MediaCacheCoordinator.Restart` requests
  a returned asset's thumbnail / waveform again in the same generation (the old result shown until replaced; earlier
  work publishes nothing). The Preview rebuilds its snapshot through `PlaybackSnapshotBuilder.AssetState.IsMissing`.
- Relink of missing media (Phase 11 Step 11.4, D026 §3, core only — the UI is Step 11.6): Core `IMediaRelinkService`
  (`CheckAsync` → `RelinkCheck`, `ApplyAsync` → `RelinkResult`, `RelinkedMediaFoundIncompatible`), `MediaFileTypes` (the
  extension → kind table of the import); Timeline `MediaRelinkService` + `Commands/RelinkMediaCommand` (absolute
  `MediaFileState` before / after per asset). Check: re-check (PO-5), offline only, extension and probed streams of the
  asset's kind, the probe (unavailable → allowed, `Pending`, no metadata; failed → rejected), length ≥ the largest
  `SourceOut` of the asset's video / audio clips, path not another asset's; warnings for differing characteristics and
  dissolve handles. Apply: re-check, validate again, one command through `IUndoRedoService` (dirty by the save point);
  `NotifyMediaRelinked` → `IProjectService.MediaRelinked` + `MediaAssetsChanged` on Execute / Undo: thumbnails and
  waveforms of the old file dropped and requested again (`MediaCacheCoordinator.Restart(dropResults)`), timeline
  waveforms refreshed, the Preview's snapshot rebuilt by the path. A later analysis whose metadata doesn't fit the clips
  raises `RelinkedMediaFoundIncompatible` (status bar through `MainWindowViewModel`). The asset id, clips and project frame
  rate never change.
- Planned in Phase 11 (D026, not implemented yet): the relink UI; a batch relink by exact file name in the chosen folder;
  recent projects (10, outside every project, `Recent ▾` next to Open). No per-user settings are stored yet
  (`AppPaths.ConfigFolder` is unused). `project.json` stays v3.

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
  `VideoPipeline`. The picture layers of one output frame are fetched at the same time (their decoders run in
  parallel, each reader's requests stay ascending, D024 Step 9.7); every fetch ends before a failure propagates.
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
  canvas (stride = width · 4) → `WriteFrameAsync`, frame n + 1 fetched while frame n is rasterized and written (one
  frame ahead, never more; cancelled and awaited on an early end — D024 Step 9.7; memory: ≤ 3 decoded frames alive per
  picture layer); Finalizing: `CompleteAsync`. Runs on the thread pool
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

## CI (Phase 9 Step 9.9)

Decision: D024 "Refined in Step 9.9". `.github/workflows/ci.yml` (GitHub Actions) on pull requests to `main`, pushes to `main`
and by hand: one job on `windows-2025` (WASAPI, Avalonia Win32 / Skia), .NET SDK 8.0.424, Debug; FFmpeg 9.0.1 essentials
(gyan.dev) downloaded from its release, SHA256-checked, cached and put first on `PATH`, its version checked; restore, build with
`-warnaserror`, the full suite with `--blame-hang` and TRX results (uploaded as an artifact). `.github/scripts/Assert-TestResults.ps1`
gates the TRX: every test passed, 8 result files, and no skip except the two opt-in 4K scenes of `ExportParityScaledTests`
("Heavy scenario") — a skip for a missing ffmpeg fails the job. Not in CI: the 4K scenes, a real audio device (the device tests
pass without one), branch protection (a repository setting).

## Verification note

Verified against the source at the end of Phase 6 (branch `feat/phase-6-project-persistence`); the Phase 7
sections at the Phase 7 closeout, the Export section at the Phase 8 closeout (Step 8.7), the Thumbnails section at
the Step 9.4 closeout (9.4e), the Waveforms section (and the shared parts of the Thumbnails section) at the Step 9.5
closeout (9.5e); the Export section's source frames and orchestration at the Step 9.7 closeout; the module table, playback
and media sections at the Step 9.8 closeout; the CI section at the Step 9.10 closeout; the Phase 10 parts of the Core
domain section step by step in Steps 10.3–10.8 and with the module table at the Step 10.9 closeout; the media paths and
missing media paragraph of the Project persistence section at the Step 11.1 audit (`2e758f1`), the media availability
paragraph at the Step 11.3 closeout, the relink paragraph at the Step 11.4 closeout.
Re-check the code before relying on details that later phases may have changed.
