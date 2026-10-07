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

The solution contains 10 application projects under `src/` plus eight test
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

The Phase 10 fades and cross dissolve live in Core (`FadeRule`, `TransitionRules`, the snapshot), Timeline (edits) and UI;
`Effect` / `Clip.Effects` (Core) are only persisted (Project) and copied with a clip (Timeline). An empty `Effects`
project reserved by the Phase 0 skeleton for a generic effect stack was removed in Phase 14 Step 14.7 (D029 §6).

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
- `ExportSettings` (last output path + fixed format enums; session state, D023), `ProjectSettings` (canvas, frame
  rate, and since Phase 13 `Export` — an `ExportEncoding`: quality level → CRF, libx264 preset, AAC bitrate, saved with
  the project, D028; the user limits in `ProjectSettingsRules`; not used by the encoder yet), `Effect`,
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
- Canvas size (Phase 13 Step 13.4, D028): `SetCanvasSize(width, height)` — `ProjectSettingsRules.CanvasError` first, the
  same size a no-op; one "Set Frame Size" step (`SetCanvasSizeCommand` + a `SetClipPropertiesCommand` per changed clip in
  a `CompositeCommand`, one `TimelineChanged`) that multiplies `PositionX / PositionY` of every picture / text clip and
  `FontSize` of every text clip by `s = min(W'/W, H'/H)` (locked and hidden tracks too); refused whole when a scaled value
  leaves `ClipPropertyLimits`. A clipboard copied at another canvas is refused on paste (`TimelineClipboard.Canvas`).
- Project settings together (Phase 13 Steps 13.6 / 13.9, D028): `SetProjectSettings(width, height, rate?, export?)` — one
  part changing goes to `SetCanvasSize` / `SetFrameRate` / `SetExportSettings`; several: `PlanFrameRate` + `PlanCanvas` on
  the unchanged model, then one "Change Project Settings" step (the rate command, the canvas commands that carry only the
  picture / text groups, `SetExportEncodingCommand`). The dialog's EXPORT section (quality, encoding speed, audio
  bitrate) is part of the same draft. UI:
  toolbar "Project Settings…" → `ProjectSettingsWorkflow` (UI/Services; `EditingLock`) → `IProjectSettingsDialog` /
  `AvaloniaProjectSettingsDialog` hosting `Views/ProjectSettingsView` over `ProjectSettingsViewModel` (a draft; Apply
  calls the service, a refusal stays in the dialog, Cancel changes nothing).
- Frame rate (Phase 13 Step 13.5, D028): `SetFrameRate(rate)` — one of `ProjectSettingsRules.SelectableFrameRates`; the
  current rate only locks an unlocked project (FR-4); otherwise one "Set Frame Rate" step: `EditPlan.SetFrameRate(rate,
  locked: true)` + `FrameRateRegrid.Plan` (D007's rule, every track) + `Validate`; fades, dissolves and markers keep their
  time; the playhead snaps to the grid in the command's notification. `EditPlan.IsTouched` treats every dissolve as
  touched on a rate change, so all source handles are checked (FR-1, also for the first video's lock).
- Editing essentials (Phase 12, D027) — all in `ITimelineEditService` / `TimelineEditService`, each one undoable step
  through the existing commands, `project.json` v3 unchanged:
  - Tracks: `DeleteTrack` (`RemoveTrackCommand`: the same track object with its clips and dissolves back at its list
    index on Undo; refused for a locked track and for the last track of the timeline; `GetDeleteTrackBlockReason` lets
    the UI skip its confirmation) and `MoveTrack(±1)` (`SetTrackOrderCommand`: only `Track.Order` changes — swapped with
    the neighbour of the same kind, equal orders numbered anew; refused for a locked track or past a locked
    neighbour). `Track.Order` stays the only layer order: the playback snapshot (Preview and export) and the timeline
    rows (video by descending order, equal orders by list place as the snapshot draws them) follow it.
  - Media removal: `RemoveMedia` (with `CountClipsUsing`, `GetRemoveMediaBlockReason`) — the clips of every track that
    use the asset through an `EditPlan` (their dissolves by `ReconcileTransitions`), then `RemoveMediaAssetCommand` (the
    same asset object back at its index on Undo), one `CompositeCommand`; refused when a clip of it is on a locked
    track; the file on disk is never touched. The thumbnail / waveform coordinators keep results per asset id, so
    Undo shows them again without making them anew; a running analysis completes into the removed asset.
  - Ripple delete and close gap: `RippleDeleteClips` (per track, every clip after a removed one moves left by the
    removed frames before it; other tracks, the playhead and markers stay) and `CloseGap(track, at)` /
    `CloseGapBefore(clip)` (one existing empty span). The move's per-clip timing rule is shared: `PlanShift` /
    `ShiftedState` (frame count, speed and source range go along). Dissolves: a removed clip's goes, the others keep
    their clips and length, none is created (D027 §2).
  - Copy / paste / duplicate: `CopyClips` → `TimelineClipboard` (detached `CloneClip` copies, their track ids, the frame
    rate), `PasteClips(clipboard, at)` and `DuplicateClips` through `PasteInto` (track, lock, media and rate checks, new
    ids, `ShiftedState`, `Insert`, `Validate`) — rejected whole on any problem; no dissolve copied. The clipboard is
    `TimelineViewModel.Clipboard`, emptied when another project becomes current.
  - Markers: `AddMarker` (frame grid, one per frame), `RemoveMarkerAt`, `NextMarker` / `PreviousMarker` over the
    persisted `Sequence.Markers` (`AddMarkerCommand` keeps them sorted, `RemoveMarkerCommand`); markers are snap
    targets; no edit moves them.
  - UI: ▲ / ▼ / ✕ in each track header (84 px column), ✕ on the selected Media Browser row, Ripple Delete / Close Gap /
    Paste / Duplicate in the timeline header (Copy by Ctrl+C only; Ctrl+C / Ctrl+V / Ctrl+D in `ShortcutRouter`), the
    marker block ◀ ◆+ ◆− ▶ left of the ruler and the markers drawn on it; every command that changes the project is off
    during an export (`EditingLock`).
- Track state (Phase 15 Step 15.3, D030 §4): `SetTrackMuted` / `SetTrackHidden` / `SetTrackLocked` — one
  `SetTrackStateCommand` each (one flag, its old value back on Undo), refused only for a missing track and for hiding an
  audio track; mute / hide are allowed on a locked track. The flags are `Track.IsMuted` / `IsHidden` / `IsLocked` of
  `project.json` v3; `PlaybackSnapshotBuilder` already reads them (hidden: no layer, no dissolve zones, the sound kept;
  muted: no audio spans), so the Preview and the export follow, and `Sequence.Duration` ignores them. The lock is checked
  where an edit is planned (`CheckEditable` and the track / media checks), not by Undo / Redo. UI: M / 👁 / 🔒 next to
  the track name in the header (audio: M / 🔒), coloured when on; a locked lane tinted, a hidden lane's clips dimmed;
  off during an export.
- Trim to the playhead (Phase 15 Step 15.4, D030 §5): `TrimToPlayhead(clipIds, edge, playhead, ripple)` — the selected
  clips with the playhead's frame strictly inside, one `EditPlan` / undo step. Each clip's timing is the edge trim's
  rule (`TrimmedState`, shared with `PlanTrim`); the per-clip limit keeps every dissolve (the zone fit of
  `DissolveParts`; a plain trim leaves a cut edge alone). Ripple: the clip back at its start (`ShiftedState` over the
  planned state), the later clips of its track moved by `PlanShift`; other tracks and markers stay.
  `TimelineEditResult.Playhead` tells the view where the playhead goes after a ripple of the start. Keys Q / W /
  Shift+Q / Shift+W in `ShortcutRouter`; off during an export.
- Ripple trim by dragging (Phase 15 Step 15.5, D030 §6): one planner, `PlanRippleTrim`, for Shift+Q / Shift+W and the
  Shift + edge drag — `PreviewRippleTrim` (planned clip timings, nothing applied) on every pointer move,
  `RippleTrimClip` (one undo step) on release; inward and outward, the source and the dissolves limiting it.
  `TrimmedState` / `PlanTrimAtSpeed` take a clip state and a neighbour list (none for a ripple). The press's modifiers
  are read by `TimelineGestureModifiers` (Ctrl toggle, Shift ripple); Esc / a lost capture rebuild the layout from the
  model. The ordinary drag is unchanged.
- Slip (Phase 15 Step 15.6, D030 §7): `SlipClip` / `PreviewSlip` over `PlanSlipOf` — video and audio clips; `SourceIn`
  and `SourceOut` moved together by D022's start-trim amount (`SlippedState`), the timing untouched; the allowed range
  from the source and the dissolve handles (`DissolveHandles`), clamped with a note; one undo step. The view starts it on
  an Alt press on a clip's body (`TimelineGestureModifiers.IsSlip`, `BeginSlip`), shows the planned Source In / Out on
  the clip (`TimelineClipViewModel.SlipText`) and commits on release.
- Import (Phase 12 Step 12.8, D027 §7): `MediaImportWorkflow` keeps the project the import started in and adds nothing
  (and queues no analysis) when another project is current after the picker, the status yield or the file check.
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
  → opacity. Canvas = project `FrameWidth × FrameHeight` — a user setting since Phase 13 (D028; changed through
  `ITimelineEditService.SetCanvasSize`, a presentation-only change for playback).
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
same for ffmpeg (playback decoding). Both resolve once per app run; a PATH tool must answer `-version` within
`ExecutableLocator.DefaultProbeTimeout` (5 s) or counts as not found for the run (tests whose subject is not that limit
use a longer one through an internal seam, D029 Step 14.4). After Open only media without saved metadata that is
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
  is 1; v1 / v2 are read without fades and transitions and saved as v3; files of a newer version are refused; Phase 13
  adds the optional `settings.export` to v3, written only when not the default, D028). `ProjectSerializer` maps entities
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
  generation guards against an autosave resurrecting a file a Save made obsolete. The timer comes from a
  `TimeProvider` (`TimeProvider.System` in the app, a manual clock in the tests); each `Start` gets a run token, so a
  timer callback queued before `Stop` / `ShutdownAsync` starts no tick (Phase 14 Step 14.3, D029).
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
- Relink of missing media (Phase 11 Step 11.4, D026 §3 — the core; its UI, Step 11.6, below): Core `IMediaRelinkService`
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
- Batch relink (Step 11.5, D026 §4): `IMediaRelinkService.SearchFolderAsync(folder)` — one re-check, the folder's own
  files listed off the UI thread, exact names ignoring case, a name of several offline items given to none
  (`Ambiguous`), each match checked by the same `CheckCoreAsync` as `CheckAsync` → `RelinkSearch` (entries Found /
  Rejected / NotFound / Ambiguous, `Applicable`, `Summary()`); nothing changes until `ApplyAllAsync(checks)`, which
  re-checks once, validates every item again and within the batch (one file per item, one item once) and executes one
  `RelinkMediaCommand` for the items still valid (the others returned with their reason). `ApplyAsync(check)` is
  `ApplyAllAsync` with one item.
- Relink UI (Step 11.6): `UI/Services/MediaRelinkWorkflow` (singleton) — pickers, dialogs (`IDialogService`, long text
  scrolls) and status around `IMediaRelinkService`, no relink rule of its own; `EditingLock` checked before, after every
  await and right before applying; `IsRunning` (one at a time). `MediaBrowserViewModel`: `RelinkCommand` (selected
  offline item), `FindMissingCommand` (any offline), `HasOfflineMedia` (the "OFFLINE" header row), selection kept by asset
  id. `FilePickerRequest.StartFolder` (→ `SuggestedStartLocation`). After an applied relink with other media offline the
  workflow offers the search in the chosen file's folder. `MediaRelinked` carries `MediaFileReplacement`s (asset +
  previous path); `MediaCacheCoordinator` keeps what was shown per (asset, file) for the generation and shows it again when
  an Undo / Redo returns the asset to that file (not the cache's last file, which after a relink is the relinked one).
  The Inspector's media state line (`AnalysisStatusText`) uses the Media Browser's order: offline, Analyzing, Not
  analysed yet.
- Recent projects (D026 §6, Step 11.7): `IRecentProjectsStore` (Core) → `RecentProjectsStore` (Project / Persistence),
  the per-user list `%LOCALAPPDATA%\AiVideoEditor\config\recent-projects.json` (`AppPaths.RecentProjectsFile`, the folder
  created only when written; the only per-user data stored): at most 10 project folders, most recent first, one per
  full path (case and a trailing separator ignored). Every change re-reads the file and writes it atomically
  (`ProjectFileStore.WriteAtomicAsync`) under `recent-projects.lock` (`FileShare.None`, shared by running instances,
  about 2 s) plus an in-process semaphore; damaged → set aside as `*.damaged`, newer format or unreadable → never
  overwritten; errors are logged, never thrown. Availability (`IsAvailableAsync`: `project.json` present) is runtime
  only, off the calling thread, without the lock. `ProjectFileWorkflow` adds an entry after a successful Open
  (`OpenAsync(folder)`), Save As and Recover of a project with a folder — nothing else. UI (Step 11.8):
  `RecentProjectsViewModel` (`ToolbarViewModel.Recent`; the `DropDownButton` after Open, `ToolbarView` calls
  `OnOpenedAsync` / `OnClosed` from the flyout's events and hides it on `CloseRequested`): reads the list at every opening,
  one `RecentProjectItemViewModel` per entry per reading with `Availability` Checking / Available / Unavailable from
  `IsAvailableAsync` (one running check per folder shared by readings; results reach only their own item; readings
  overtaken or older than a removal are dropped); `OpenCommand` (Available only) → `ProjectFileWorkflow.OpenFolderAsync`
  (the confirmed open shared with Open), `RemoveCommand` → `RemoveAsync`; one action at a time; `EditingLock` disables
  the button and the commands. `project.json` stays v3.

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
  output; warning: missing font) and returns an `ExportJob` (snapshot + full output path + the project's
  `ExportEncoding` at that moment, Phase 13 Step 13.7) when nothing blocks. `ExportOutput` = canvas size, exact
  project frame rate, whole frames covering the duration, the matching 48 kHz sample count; `ExportFormat` = the fixed
  part: MP4 / H.264 / AAC 48 kHz stereo; the CRF, the libx264 preset and the AAC bitrate come from `job.Encoding`
  (`IExportEncoder.StartAsync(output, encoding, …)`; default CRF 18 medium 192 kbps, the Phase 12 command lines).
  `IExportService`: `IsAvailableAsync`, `ExportAsync(job, progress, ct)` → temp file moved into place on
  success, `ExportException` / cancellation leave nothing behind.
- UI (Step 7): Toolbar Export → `ExportWorkflow` (UI/Services): preflight (without the output file) → errors stop,
  warnings may be accepted → save-file picker → preflight with the file (the job) → replace confirmation → `EditingLock`
  + modal `AvaloniaExportProgressDialog` (`ExportProgressViewModel`, pulled by a timer; Cancel = the token) →
  `IExportService.ExportAsync` → outcome message per `ExportFailure` / cancelled / unexpected. `EditingLock` is shared
  by Toolbar, Timeline, Inspector and Media Browser (`CanExecute` / edit guards). `LastExportSettings` is session-only
  state (not dirty, not undoable, not serialized), updated after a successful export. Manual plan: `docs/EXPORT_MANUAL_TEST_PLAN.md`.
- Codec leg (Phase 13 Step 13.8, L1-c, tests only): `ExportCodecLegTests` / `CodecLegMetrics` measure the MP4 against
  the export canvases and against a lossless reference of them at every quality level and preset; per-level bounds on
  the codec's own error, the level order and flat colour (D028 "Refined in Step 13.8").
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
paragraph at the Step 11.3 closeout, the relink paragraph at the Step 11.4 closeout, the batch relink paragraph at the
Step 11.5 closeout, the relink UI paragraph at the Step 11.6 implementation, the recent projects paragraphs written at
Steps 11.7–11.8 (Phase 11 merged into `main` as `47ed2fa`); the Phase 12 editing-essentials and import paragraphs of the
Timeline section at the Phase 12 closeout (Step 12.9, `d467a84`; Phase 12 merged into `main` as `c0cb600`). Phase 13
(D028, project & export settings): the `ProjectSettings` / persistence lines at Step 13.3, the canvas-size and
composition lines at Step 13.4, the frame-rate line at Step 13.5, the project-settings line at Step 13.6, the export
contract (job settings, `ExportFormat`) at Step 13.7, the codec-leg line at Step 13.8, the export-settings UI line at
Step 13.9; the whole Phase 13 part re-checked at the closeout (Step 13.10, `5c01aed`; Phase 13 merged into `main` as
`ed40b74`). Phase 14 (D029, stabilization): the `Effects` row of the module table checked against the code at Step 14.2
(empty, referenced only by `App`; D029 §6) and removed with the project at Step 14.7; the autosave paragraph of the Project persistence section at Step 14.3; the locator lines of the media section at
Step 14.4. At the Phase 14 closeout (Step 14.8, `90caae9`) the Phase 14 parts above and the project counts were checked against the code.
Re-check the code before relying on details that later phases may have changed.
