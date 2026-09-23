# Architectural Decisions

This file records decisions that should remain stable unless there is a deliberate reason to change them.

## D001 — MediaTime precision

MediaTime uses 100-nanosecond ticks.

Reason:
- precise timeline representation
- compatibility with .NET time representations
- avoids unnecessary floating-point precision loss

Status: Accepted.

---

## D002 — FFmpeg / FFprobe

FFmpeg and FFprobe are part of the media pipeline.

Status: Accepted.

---

## D003 — .NET / Avalonia

The application is a desktop application built with .NET 8 and Avalonia.

Status: Accepted.

---

## D004 — MVVM

CommunityToolkit.Mvvm is used for MVVM functionality.

Status: Accepted.

---

## D005 — Logging

Serilog is used for application logging.

Status: Accepted.

---

## D006 — Rational frame rate and exact frame grid

Date: 2026-09-23

Decision: `MediaTime` stays in 100 ns ticks (D001 unchanged). Frame rates are an
exact rational `FrameRate` (numerator/denominator, e.g. 30000/1001). Frame n starts
at `round_half_up(n · 10⁷ · den / num)` ticks, computed in integer arithmetic
(`Int128`); `MediaTime.FromFrame` / `ToFrameFloor` / `ToNearestFrame` /
`SnapToFrame` round-trip exactly. The former `double`-based frame conversions were
removed. Every clip edge on the timeline is exactly on the project frame grid;
edits compute both edges from frame indices.

Context: most rates (24, 30, 29.97, …) don't divide 10⁷ ticks, so a frame boundary
is not an integer tick; double-based conversion wasn't guaranteed to round-trip.
Flicks (a different time unit) were considered and rejected to keep D001.

Consequences: the tick length of the same frame count can differ by one tick
between positions. Source limits use `FrameMath.MaxWholeFrames` so a clip never
extends past its source at any position.

Status: Accepted.

---

## D007 — Project frame rate from the first video

Date: 2026-09-23

Decision: a new project uses a provisional 30 FPS. Adding the first video clip to
the timeline fixes `ProjectSettings.FrameRate` from the file's `r_frame_rate` and sets
`IsFrameRateLocked`; existing clips are moved to the new grid in the same undoable
step (each edge to its nearest frame; a clip that would collapse grows by one frame
into free space; otherwise the add is rejected atomically). Unknown or out-of-range
source rates (outside 1–240 FPS) lock a 30 FPS fallback and say so explicitly.
After locking, the rate never changes automatically; deleting videos doesn't unlock
— only undoing the locking add does.

Status: Accepted.

---

## D008 — Timeline editing rules (Phase 4)

Date: 2026-09-23

Decision:
- A video file is one `VideoClip` that carries its own audio; no linked V/A pairs yet.
- Overlapping clips on a track are rejected (no overwrite/ripple). Trims clamp to
  neighbours, the source media and a one-frame minimum.
- Video/audio can be added only after successful analysis (duration known); images
  immediately, 5 s by default.
- Only `Speed = 1.0` is editable; edits of clips with any other speed are rejected.
- All edits go through `ITimelineEditService`, which validates the complete result
  before executing one `IUndoableCommand` (absolute before/after snapshots).
  Selection, playhead, zoom and the snapping toggle are view state, not undoable.
- Any timeline change marks the project dirty; a save point (clean after undo) is Phase 6.

Status: Accepted.

---

## D009 — Source frame selection for playback (Phase 5)

Date: 2026-09-23

Decision: which decoded source frame a timeline frame shows is decided by pure
Core logic (`Core/Playback/SourceFrameSelector`), never by a decoder filtergraph.
- Source time of a decoded frame = `Pts · TimeBase − StartTime`, where `StartTime`
  is the container's `format.start_time` (common origin for all streams). Real PTS
  always define frame timing.
- For timeline frame `n` of a clip at speed 1.0: `t(n) = SourceIn + (FromFrame(n) − S)`.
- Sample point `t(n) + δ`, `δ = ½ · min(P, Ssrc)`: `P` = length of timeline frame `n`
  on the project grid, `Ssrc` = nominal source frame length from `avg_frame_rate`,
  falling back to `r_frame_rate`, falling back to `P` alone.
- The frame shown is the last one (in presentation order) whose source time is
  `≤` the sample point. Before the first frame the first frame is held; after the last
  frame the last one is held.
- Everything is exact integer (`Int128`) arithmetic; no floating point.
- Project FPS is still fixed from `r_frame_rate` (D007 unchanged). `MediaMetadata`
  gained `StartTime` and `AvgFrameRate`.

Context: "last frame with PTS ≤ frame start" flips frames for identical rates because
frame starts are tick-rounded (29.97 frame 2 = 667333 ticks vs 667333⅓) and because of
ms-rounded (MKV) or jittery (phone VFR) timestamps. "Frame midpoint" alone is unstable
for 2:1 ratios (59.94 → 29.97). `δ = ½·min(P, Ssrc)` gives: identity with ±½-frame
tolerance for equal rates, midpoint sampling (= ffmpeg `fps` default) when the source
is slower, nearest-frame sampling when it is faster. Verified with generated test media
and ffmpeg 9.0.1: `-ss` accurate seek drops the frame containing the seek point, and the
`fps` filter's grid starts at the first frame after seek, so neither is used for selection.

Consequences: decoders only deliver frames with exact PTS + time base (backend-neutral);
the FFmpeg CLI adapter uses `-copyts`, a bounded time-based preroll and
`-fps_mode passthrough` (no `-vf fps`), and may read PTS from `showinfo` internally.
Phase 8 export must reproduce the same rule.

Status: Accepted.

---

## D010 — Phase 5 playback architecture

Date: 2026-09-23

Decision:
- Backend: FFmpeg CLI processes with raw frames/PCM over pipes, one per video/audio
  source, hidden behind Infrastructure-level interfaces. Core/Timeline never see
  FFmpeg, pipes, CLI arguments or stderr formats; a future FFmpeg.AutoGen backend must
  be possible without changing Core/Timeline. Decoders deliver backend-neutral
  `DecodedFrame`s (pixel data + PTS/time base). Preview decode ≤ 1280×720,
  `-hwaccel auto` with software fallback (hardware decoding is never assumed).
- Audio output: NAudio (MIT, WASAPI, Windows-only), not referenced by Core.
- Clock: separate clock abstraction (fakeable in tests). Master is the audio device
  position (samples played, anchor-based, no accumulated error), falling back to a
  `Stopwatch` clock when no audio is playing.
- Picture: the topmost visible (`!IsHidden`) video track with a clip wins; no
  compositing, opacity, transform or crop yet. (Superseded in Phase 7 by D018/D019: every visible
  layer is decoded and reported; the topmost picture stays as a compatibility view.) Audio: every VideoClip with an audio
  stream and every AudioClip is mixed; `IsHidden` hides picture only; `Track.IsMuted`,
  `AudioClip.IsMuted` and existing `Volume` values apply.
- States: `Paused` / `Playing` plus `IsBuffering` / `IsAvailable` flags. Reaching the
  sequence end pauses; Play at the end restarts from 0; Stop = Pause + Seek(0).
- Timeline edits (incl. undo/redo) during playback don't stop it: a new immutable
  `PlaybackSnapshot` is built on the UI thread and playback resyncs at the current time.
- Missing/invalid/unsupported media: "Media offline"/"Unsupported" placeholder and
  silence for that clip only; playback continues. A decoder that cannot produce the
  needed frame within its bounded preroll reports an error for the segment, never hangs.
- `IPlaybackService` may change for Phase 5 (no Avalonia/FFmpeg/NAudio types;
  `StepFrame` and `Volume` removed). `IVideoEngine` unchanged.
- Transitions and `Speed ≠ 1.0` are not supported.

Status: Accepted (implemented in Phase 5).

---

## D011 — Playback runtime model (Phase 5)

Date: 2026-09-23

Decision (refines D010):
- Polling: the UI tick calls `IPlaybackService.Update()` and receives position, timeline
  frame, state, buffering flag and picture. Background threads never raise UI events and
  know nothing about Avalonia/Dispatcher. All service members are called from one thread.
- Clock: `PlaybackClock` = anchor + elapsed of an `IReferenceClock`
  (`floor(Δunits · 10⁷ / unitsPerSecond)`, Int128). Play, Seek and a reference switch
  (Stopwatch ↔ audio device) create a new anchor at the current position — no jump,
  no accumulation. Stopwatch is the master until audio output exists.
- Seek / snapshot update: new clock anchor at the target, seek generation +1, old
  pipeline cancelled, `IsBuffering` until the target frame is decoded. The clock is
  **not** held while buffering — decoder latency is never added to the position.
  Results are used only for the current (SnapshotVersion, SeekGeneration); older
  snapshot versions are ignored.
- `PlaybackSnapshot` is immutable, built on the UI thread; visible video tracks keep
  their clips as logical spans (TimelineStart, TimelineEnd, ClipId, AssetId, SourceIn) and
  the topmost visible track is resolved at lookup time — clips are not split. The source
  frame is chosen at run time (D009), never in the snapshot.
- Picture kinds are kept distinct: Offline (asset absent/unavailable, incl. file not
  found at decode time), Unsupported (Speed ≠ 1, wrong media kind), DecodeError
  (ffmpeg/pipe/decode failure). A failing clip never stops the sequence.
- ImageClip is a static frame: decoded once through the same decoder, cached and held
  for the whole span.
- End: Position reaches Duration → Paused at Duration (last frame shown); Play at
  Duration restarts from 0; Stop = Pause + Seek(0).

Status: Accepted.

---

## D012 — Playback underrun and hardware fallback (Phase 5)

Date: 2026-09-23

Decision (refines D011):
- Underrun is realtime playback behaviour, not an error. When the position has reached
  timeline frame N but the decoder has not yet made the frame for N certain, `Update()` may
  keep showing the last picture shown (for a frame < N), flagged `IsPictureCurrent = false`
  (stale/late). A frame from the future is never shown (a frame is only returned once no
  later frame can still qualify for the sample point; hold-first applies only at a stream's
  first frame per D009). When the decoder catches up, playback continues with the current
  frame — no extra repeats or skips beyond the frames that were late. No `DecodeError`.
- Hardware → software fallback after decoding started: the frames already decoded stay in the
  buffer; the same span is reopened with `HardwareDecoding.Disabled` at the timeline frame
  playback last requested (never the clip start); frames of the new stream at or before the
  last buffered PTS are dropped by exact PTS comparison; the seek generation does not change
  and the state stays Playing.

Status: Accepted.

---

## D013 — Audio playback and the audio master clock (Phase 5)

Date: 2026-09-23

Decision (refines D010/D011):
- Format: 48 kHz, stereo, float32 interleaved for decoding, mixing and output.
- Clock: whenever an audio device is available, playback runs on its played-frames clock —
  including gaps, where the mixer outputs silence. `IAudioOutput.Clock` is the last observed
  playback position of the device, accumulated across Start/Stop sessions (never backwards).
  No device, an unexpected output format, or a device failure → Stopwatch, switched without
  a jump (`PlaybackClock.SetMaster`). Video follows the same clock (D009/D012 unchanged).
- WASAPI (NAudio.Wasapi 2.2.1, shared mode; latency configurable, 100 ms initially):
  `GetPosition()` is used as the position; pause and seek use `Stop()` (flushes queued audio,
  a new session starts on Play/after seek); `Pause()` is never used (it keeps playing the
  queued buffer). Device objects are created and controlled on the UI thread (COM apartment);
  the mixer runs on the device thread.
- Timeline → samples: timeline sample k is k/48000 s; a clip [S, E) owns samples
  [ceil(S·48000/10⁷), ceil(E·48000/10⁷)); it plays source sample k + d with
  d = round((SourceIn − S)·48000/10⁷), rounded once per clip (`AudioTiming`).
- Decoding: ffmpeg CLI per audio source, `-copyts`, `aresample=48000:async=1`, stereo float;
  the first sample's index comes from `ashowinfo` PTS relative to `format.start_time`, never
  from `-ss` (AAC in MP4 starts at a later codec frame); a stream starting after the needed
  sample is retried with a larger preroll (up to 5 s, then from the file start).
- Mixing: audio of every VideoClip with an audio stream (hidden tracks included) and every
  AudioClip; muted tracks/clips excluded; gain = Volume; sum clamped to [-1, 1] (no limiter,
  no crossfade/declick). Gaps, Offline/Unsupported spans and decode errors are silence and
  never stop playback. Underrun is silence — the mixer never waits and time never shifts.
- Lifecycle: seek and pause stop the device and rebuild the audio pipeline at the position;
  a snapshot update keeps the device running and continues at the mixer's write position,
  reusing readers of unchanged clips (gain changes applied without reopening).

Refined in Phase 7 Step 4 (2026-09-23), volume and mute:
- A muted clip (VideoClip or AudioClip, `IsMuted`) stays in the snapshot as an `AudioSpan` with
  `IsMuted = true`; the mixer applies `EffectiveGain` (0 while muted, the volume otherwise). Its
  reader keeps running, so unmuting is instant and never reopens ffmpeg. `Gain` is always the
  clip's volume — mute never replaces it. Muted *tracks* still drop their spans (unchanged).
- A snapshot that differs from the current one only in the mix
  (`PlaybackSnapshot.DiffersOnlyInMix`, since Step 5 `DiffersOnlyInPresentation` — D018: same frame
  rate, duration, layers, spans apart from gain/mute and picture/text properties, assets) is not a resync: the video pipeline, its readers, the seek generation and the
  picture stay; the same `AudioPipeline` takes the new snapshot (`UpdateMix`) and republishes the
  gains at the mixer's write position. No ffmpeg process starts, nothing buffers.
- The "current" check for pictures uses the snapshot version the video pipeline was built from
  (set where pipelines are created), not the latest snapshot the service holds — after a
  mix-only update the two differ.

Status: Accepted.

---

## D014 — Project persistence: format, Open/Save, missing media (Phase 6)

Date: 2026-09-23

Decision:
- A project is a folder containing `project.json` (plus `cache/` for later phases); no
  single-file `.aveproj`. Media is referenced, never copied into the project.
- `project.json` is format version 1: `"format": "AiVideoEditor.Project"`,
  `"formatVersion": 1`. It is written from DTOs (`Project/Persistence/ProjectFileDto.cs`) that
  are separate from the runtime entities, with System.Text.Json (no new package).
  Every `MediaTime` is a `long` of 100 ns ticks (`…Ticks` properties, never seconds or double),
  every `FrameRate` an exact `{numerator, denominator}`. Enums are names; clips carry a `type`
  discriminator; a track's type is implied by the list it is in. Runtime/UI state is not
  stored: `IsSelected`, `IsDirty`, `IsMissing`, analysis status/error, `ProjectFolderPath`.
  Unknown properties are ignored; a higher `formatVersion` is refused with its own message.
- ffprobe metadata is stored only for a completed analysis and reused on Open (status
  Completed, no new probe). Metadata that is internally inconsistent is dropped, and that
  media is analysed again; it does not make the project invalid.
- Each media file is stored with its absolute path and its path relative to the project
  folder (none on another volume). On Open: the absolute path if the file exists, else the
  relative one if that file exists (project moved together with its media), else the
  absolute path and the asset is missing.
- Open reads and fully validates into a separate object — ids unique and present,
  references, clip kind vs media kind and track type, clip edges on the project frame grid,
  ≥ 1 frame, no overlaps, source range = duration at speed 1 — and only then replaces the
  current project (undo history reset, clean). Any failure (`ProjectFileException`, a short
  user-facing message) or cancellation leaves the current project, its history and save
  point untouched. Clips are not checked against the media on disk (that is offline media,
  not a damaged project).
- Save is atomic: temp file next to `project.json`, flushed, then `File.Replace`; a failed or
  cancelled write leaves the existing file as it was and changes neither the save point nor
  the project's folder/name. Save As makes the chosen folder the project folder and names the
  project after it, only after the write succeeded. Saves are serialized.
- Missing media: detected on the loaded project before it replaces the current one, so the
  first playback snapshot already shows "Media offline" (existing playback behaviour,
  unchanged). Missing is runtime state only: not saved, never makes the project dirty, never
  blocks Open. Missing files are not probed (no failed analyses, no retries). Detection
  happens once per Open; a file that reappears later stays offline until the project is
  reopened. Relink is out of scope.

Consequences: `AppPaths.ProjectFile` and `ProjectFileStore.ProjectFileName` both name
`project.json` (Project does not reference Infrastructure; kept as is for now).

Status: Accepted.

---

## D015 — Dirty tracking with an undo save point (Phase 6)

Date: 2026-09-23

Decision (completes the save point deferred in D008):
- `IUndoRedoService` identifies a history position by the command on top of the undo stack
  (`CurrentPosition`); `MarkSavePoint(position)` records the saved one; `IsAtSavePoint`
  compares them. A save point dropped from the redo stack by a new command can never be
  reached again; `Clear()` makes the empty history the save point.
- The project is dirty when the history is not at the save point or a non-undoable change
  (media import) happened since the last save. Undo/Redo back to the saved state make it
  clean; New/Open start clean; a recovered project (D016) is dirty until saved.
- Save captures the text, the history position and the non-undoable change count at the same
  moment on the UI thread; the save point is marked only after the write succeeded, with that
  earlier position — edits made while the file was being written stay unsaved.
  `IProjectService.SaveStateChanged` reports dirty/name/folder changes (window title
  "Name[*] — AI Video Editor"); `ProjectSaved` follows a successful save.

Playhead, zoom and snapping (open in Phase 6, decided by the product owner at the start of
Phase 7, 2026-09-23): they are **session state** of the sequence. They stay in `project.json`
(so a reopened project comes back where it was left), but changing them never makes the
project dirty, never triggers an autosave on its own and never enters undo/redo. Whenever
another project becomes current (New / Open / Recover), the timeline view takes zoom and
snapping from the new sequence instead of keeping the previous project's values.

Status: Accepted.

---

## D016 — Autosave, recovery and unsaved-changes handling (Phase 6)

Date: 2026-09-23

Decision:
- Autosave never writes `project.json`. Every 2 minutes, if the project is dirty, it is
  snapshotted on the UI thread and written atomically to
  `%LOCALAPPDATA%\AiVideoEditor\recovery\<projectId>.json` (`AppPaths.RecoveryFolder`): the same
  project DTO wrapped with `"format": "AiVideoEditor.Recovery"`, the project's folder (null if
  never saved), the autosave time and the writing process (id + start time). One app-wide
  folder, not the project folder, so recovery can be found at startup without knowing which
  project was open and works for never-saved projects.
- A recovery file is obsolete, and deleted, after a successful Save of a clean project, when
  this session finds the project clean at an autosave (only files it wrote itself), on
  Discard / "Don't Save", and at shutdown of a clean project. Writes and deletes are
  serialized per store; a delete bumps the project's generation and a write snapshotted at an
  older generation is dropped, so an autosave taken before a Save can't recreate the file.
- Startup (after the window is shown): damaged or unusable recovery files are renamed
  `*.damaged` (kept, not offered again), files older than their project's `project.json` are
  removed, files of a still-running process are ignored; the newest remaining one is offered:
  Recover / Discard / Not now. Recover goes through the same validation as Open, restores the
  original folder and leaves the project dirty. Nothing here can prevent startup or change
  the current project on failure. Autosave starts afterwards.
- New / Open / Close with unsaved changes ask Save / Don't Save / Cancel. Save that doesn't
  happen (picker cancelled, write failed) counts as Cancel; edits made during that save ask
  again. "Don't Save" removes the discarded project's recovery file only after New/Open
  succeeded (a failed Open keeps the project, its changes and its recovery); on Close, autosave
  shuts down without keeping a recovery file. Cancel on Close keeps the window and autosave.
- Save of a never-saved project is Save As. Save As and Open use a folder picker; Save As
  over another project's folder asks before replacing it.

Status: Accepted.

---

## D017 — Clip property edits and undo merging (Phase 7)

Date: 2026-09-23

Decision:
- Non-timing clip properties are edited only through
  `ITimelineEditService.SetClipProperties(clipId, ClipPropertyChange)`. The change carries typed,
  absolute groups — `VisualProperties` (position, scale, rotation, opacity, crop; video, image,
  text — text has no crop), `AudioProperties` (volume, mute; video and audio clips) and
  `TextProperties` (text, font, size, `#RRGGBB` color, alignment; text clips). A null group is
  left unchanged; a given group replaces the clip's values of that group as a whole.
- Values are stored exactly as given — no rounding, clamping or derivation. Out-of-range or
  non-finite values, a group that doesn't apply to the clip, or a locked track reject the whole
  change without touching the model. Limits: `ClipPropertyLimits` (opacity 0–1, scale 0.01–10,
  rotation ±360°, position ±100 000 px, each crop inset in [0, 1) with opposite insets < 1,
  volume 0–2 linear, font size 1–1000, text ≤ 10 000 characters; empty and multiline text are
  valid). Timing is never changed, so these edits are also allowed on clips with speed ≠ 1.
- Mute is its own state on every clip that can carry audio (`VideoClip.IsMuted` added);
  muting never changes the volume.
- `SetClipPropertiesCommand` stores complete before/after snapshots (`ClipPropertyValues`) and
  writes them back verbatim on Execute/Undo/Redo.
- Undo merging: `IMergeableCommand.TryMerge`. `UndoRedoService.Execute` merges a command into
  the top of the undo stack only when (a) the redo stack is empty (not right after an Undo),
  (b) the top is not the save point, and (c) the top accepts it. A property command accepts the
  next one when it is for the same clip, changes exactly the same set of properties and starts
  from its "after" state. Merging never mutates a command: the top is replaced by a new instance
  (a history position captured before, e.g. by a Save in progress, can't later mark the merged
  state as saved), or removed when the edits cancel out exactly (e.g. back to the saved value →
  the project is clean again). `NotifyingCommand` forwards merging to its inner command.

- The rules live in Core (`ClipPropertyValidator`, `ClipPropertyLimits`) and are applied both to
  edits and to every clip read from `project.json` or a recovery file
  (`ClipPropertyValidator.ValidateCurrent`, called by `ProjectSerializer`): a value the editor
  couldn't have produced makes the file damaged (`ProjectFileException`), and — as for every
  Open failure (D014) — the current project, its history and save point stay untouched.
- `VideoClip.isMuted` is an optional field of format v1 (no version change): files written before
  it existed load unmuted. Each clip type has its own DTO, so properties of another clip kind
  can't reach a clip; stray JSON properties are ignored like any unknown property (D014).

- Inspector (Step 4): the fields are filled from the model under a sync guard, so showing a
  clip never produces an edit — needed because a displayed value may not round-trip exactly
  (volume 1/3 → 33.3333333333333 %); only a value the user changes is sent to
  `SetClipProperties`. A rejected edit is reported in the status bar and the fields show the
  model again.

Consequences: playback honours `VideoClip.IsMuted` from Step 4 on (D013 refinement).

Status: Accepted.

---

## D018 — Composition model (Phase 7)

Date: 2026-09-23

Decision: one pure Core model (`Core/Composition`) decides where every visual layer lands; the
Preview (Phase 7) and the Export (Phase 8) both reproduce it. Core holds no Avalonia or FFmpeg types.

- Canvas: `ProjectSettings.FrameWidth × FrameHeight` (default 1920 × 1080; never taken from a video).
  Coordinates are canvas pixels, origin top-left, +X right, +Y down.
- Per picture clip (video, image), operations in this order:
  1. crop — `CropRect` insets are fractions of the source; the source rectangle is
     `(L·W, T·H, (1−L−R)·W, (1−T−B)·H)` in source pixels (also given normalized). The validator's
     limits (each inset in [0, 1), opposite insets < 1) guarantee a non-empty rectangle.
  2. fit — "contain": the cropped rectangle is scaled uniformly by
     `fit = min(canvasW / cropW, canvasH / cropH)` and centred; the axis is chosen by exact
     cross-multiplication. Never stretched independently in X/Y.
  3. scale — uniform, multiplies `fit`, around the picture's centre.
  4. rotation — `RotationDegrees` around the picture's centre; positive = clockwise on screen.
     Multiples of 90° use exact 0/±1 coefficients. The picture is not re-fitted after turning.
  5. position — `PositionX/Y` move the picture's centre from the canvas centre, in canvas pixels
     (0, 0 = centred). This is the existing model's meaning (defaults 0 = centred, limits "position
     offsets in canvas pixels", D017); no new UX semantics.
  6. opacity — a 0..1 multiplier for the whole layer.
  As one matrix (column vectors, `Affine2D`): `M = T(cw/2 + PositionX, ch/2 + PositionY) · R(θ) ·
  S(fit · Scale) · T(−cropW/2, −cropH/2)`, mapping the crop-local rectangle [0, cropW] × [0, cropH]
  onto the canvas. `LayerGeometry` carries source rect (pixels and normalized), fit, matrix, centre,
  opacity, canvas bounds and `CoversCanvas`.
- Source size: the source's pixel size from ffprobe metadata. When it is unknown the layer has no
  geometry and the renderer applies the same `CompositionMath.Layout` to the decoded frame's size. A
  renderer whose decoded frame has another resolution maps it through `NormalizedSourceRect`.
- Text clips: same model without crop and fit — `M = T(cw/2 + PositionX, ch/2 + PositionY) · R(θ) ·
  S(Scale)`. Content and style stay renderer-neutral (`TextProperties`: multiline text, font family,
  size in canvas pixels, `#RRGGBB`, alignment); the renderer lays the text out, centres its box on the
  local origin and draws it through `M` with the layer opacity.
- `PlaybackSnapshot.LayersAt(time)`: per visible track (hidden tracks excluded) the clip covering the
  time (half-open) becomes a layer, bottom to top by track order. Opacity-0 clips and empty/whitespace
  text are not layers. Walking down from the top, everything below an occluder is dropped. An occluder
  is a decodable video (no alpha) at opacity exactly 1 whose picture provably contains the whole
  canvas. Images (may carry alpha), text, placeholders (offline/unsupported) and layers of unknown size
  never occlude.
- Coverage proof: for multiples of 90° it is decided exactly in rational arithmetic from the model
  values (every double is an exact rational) — touching the edge covers, falling 10⁻¹³ px short does
  not. For other angles the canvas corners are mapped back into the picture and must lie inside it
  by a margin of 10⁻⁶ of the picture size, far above double rounding; borderline cases don't cull.
- Snapshot: `PictureSpan` carries `Visual` + `SourceSize`, `VideoLayer.Texts` the text clips,
  `PlaybackSnapshot.Canvas` the canvas. None of it affects decoding: `PictureAt` (Phase 5 picture)
  is unchanged, and the Step 4 "mix-only" check became `DiffersOnlyInPresentation` (gains, mute,
  picture/text properties, source size, canvas) — playback keeps every decoder on such changes.

Context: the preview composites on the GPU (Step 7) and the export will use an ffmpeg filtergraph
(Phase 8); both need one exact, renderer-free definition. Uniform scale commutes with rotation, so
their relative order is not observable; every other order is (tests).

Consequences: ffprobe reports coded width/height without rotation side data, so a phone video
stored landscape with a 90° rotation flag (autorotated by ffmpeg on decode) would get a landscape
`SourceSize` although its frames are portrait — resolved in Step 6 (D019: `SourceSize` is the
probed display size). SAR (non-square pixels) is ignored as before.

Status: Accepted.

---

## D019 — Display orientation and multi-layer playback (Phase 7 Step 6)

Date: 2026-09-23

Decision:
- Orientation: `MediaMetadata.Width/Height` stay the coded size; `DisplayRotation` (clockwise
  0/90/180/270, null when not a plain right angle) and `DisplayWidth/DisplayHeight` (the size of the
  frames the decoder delivers) are probed from the stream's display matrix (or a legacy `rotate` tag),
  else the first frame's display matrix (EXIF orientation of images), else 0°. The interpretation
  mirrors ffmpeg's automatic rotation, which the decoder uses explicitly (`-autorotate`): within 1°
  of 90/270 width and height swap (also for mirrored matrices), other angles keep the coded size.
  Odd angles and mirrored matrices are not supported orientations: logged, laid out with the decoded
  size. Composition (D018) uses the display size (`PictureSpan.SourceSize`); unknown → no geometry.
  Users see the display size. SAR stays out of scope.
- Older metadata (no display size) is kept and re-probed in the background when the file is present:
  the asset stays Completed, the project doesn't become dirty, a failed probe keeps the saved metadata,
  missing files stay offline. Inconsistent orientation values drop the metadata (D014).
- Multi-layer playback supersedes D010's "topmost visible track wins": `VideoPipeline` keeps a reader
  for every decodable layer of `PlaybackSnapshot.LayersAt` (nothing under an opaque full-canvas video)
  plus the layers appearing at the next clip edge within the prefetch window, and closes the others.
  `PlaybackFrame.Layers` lists every visible layer bottom to top as a `LayerPicture`: Frame (possibly
  late, per layer — D012), Text, Pending, Offline, Unsupported, DecodeError; `Canvas` is the project
  canvas. `Picture` / `IsPictureCurrent` remain as the compatibility view (the topmost picture layer)
  for the single-picture Preview until Step 7.
- A seek is ready when every layer visible at the target frame is. A presentation-only snapshot
  (`DiffersOnlyInPresentation`) is taken over by the running pipeline (`UpdatePresentation`): same seek
  generation, no buffering; a layer it uncovers opens its reader and is Pending until its first frame;
  a layer it covers closes its reader. Readers are keyed by clip — within one pipeline a clip's
  decoding never changes. Structural timeline changes still resync as before (no reader reuse).
- Placeholders (Offline/Unsupported/DecodeError) use the clip's geometry (display size, Position/
  Scale/Rotation) or the whole canvas when the size is unknown, and never hide lower layers: a video
  that fails at run time (decode error, file gone) stops occluding and the layers below are decoded.
- No limit on simultaneous decoders. Measured (ffmpeg 9, RTX 4070 SUPER, 1080p30 H.264 decoded at
  ≤ 1280 × 720, Preview tick 10 ms): 1, 2 and 4 layers, hardware and software — 0 % late frames over
  4 s, one ffmpeg process per visible layer.

Consequences: `PlaybackSnapshot.LayersAt` gained an optional `mayOcclude` veto (Step 5 geometry
unchanged). Found while testing: `IPlaybackService.SeekAsync` only completes while `Update()` is being
called when a source's preroll exceeds `BufferFrames` (frames before the target are released by
`Update`); the Preview always ticks, so the app is not affected — unchanged since Phase 5.

Status: Accepted.

---

## How to add a decision

When a major architectural decision is made, add:

- ID
- Date
- Decision
- Context
- Consequences
- Status

Do not rewrite old decisions merely because a newer approach is preferred. Supersede them explicitly.
