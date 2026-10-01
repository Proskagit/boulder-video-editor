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

Status: Accepted. Refined by D022 (Phase 7 Step 9): speeds other than 1× are editable; move, trim,
split and re-grid follow D022's timing rule.

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

Status: Accepted. Generalized by D022: at speed s, t(n) = SourceIn + (FromFrame(n) − S)·s and
δ = ½·min(P·s, Ssrc); at 1× unchanged.

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

Status: Accepted (implemented in Phase 5). Partly superseded: the picture rules ("topmost track wins", no
compositing/opacity/transform/crop) by D018–D020 (Phase 7), `Speed ≠ 1.0` by D022 (Phase 7); the export
renders the same snapshot offline (D023, Phase 8). Backend, audio output, clock, states, edits during
playback and offline media stay as decided here.

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

Status: Accepted. Since Phase 8 Step 4 (D023) the per-clip placement (owned samples, 1× offset, decode request,
stream placement at every speed) is Core `AudioPlacement` and the mix rule (Σ sample × gain, clamp) Core `AudioMix`,
used by `AudioSpanReader`/`AudioMixer` and by the export — behaviour unchanged. Refined by D022: clips at other speeds are decoded tempo-changed with the pitch
kept (ffmpeg atempo), placed within 10 ms of the exact mapping.

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

Status: Accepted. Format version 2 since D022 (exact clip speed); version 1 files are still read.

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

Status: Accepted. The export does not use an ffmpeg filtergraph after all (context above): D023
renders the same layers with the same Core rules offline and uses ffmpeg only to encode.

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

## D020 — Preview composition rendering and visual Inspector (Phase 7 Step 7)

Date: 2026-09-23

Decision:
- The Preview draws `PlaybackFrame.Layers` with Avalonia's `DrawingContext` (no new rendering
  architecture or dependency): `PreviewViewModel` publishes `Layers`, `Canvas` and
  `AreLayersCurrent`; `CompositionView` (UI/Rendering) executes a `CompositionDrawPlan`.
- Coordinates: `layer-local → canvas` is the layer's D018 transform, `canvas → control` a uniform
  "contain" viewport (letterboxed, centred); the composition is never rescaled for the UI.
  `RenderConversions.ToMatrix` is the only place `Affine2D` becomes an Avalonia `Matrix` (column →
  row vectors). Everything is clipped to the canvas rectangle; the canvas has the project's real
  proportions (no fixed 960 × 540).
- Per layer, bottom to top, with its own opacity: Frame — the decoded frame's `NormalizedSourceRect`
  (in its own pixels) drawn into the crop rectangle, geometry from D018, or from the decoded size
  when the source size is unknown; late frames are drawn; Text — laid out by Avalonia at its font size,
  lines aligned in their box, box centred on the local origin, then the layer transform; Offline /
  Unsupported / DecodeError — a minimal placeholder (box + label) in `PlaceholderArea`; Pending —
  nothing. Culling stays D018/D019.
- Bitmaps: two `WriteableBitmap`s per layer, alternated; a frame is copied only when that layer's
  decoded frame changes; bitmaps of vanished layers are disposed.
- The preview keeps polling while any layer is pending or late (`AreLayersCurrent`), so a layer
  uncovered while paused still appears; while a seek/timeline change buffers, the previous layers stay.
- The UI's single-picture compatibility path (`CurrentFrame`, `PictureKind`, `PlaceholderText`,
  `IsPictureCurrent` on the view model, the per-view bitmap copy) is removed.
  `PlaybackFrame.Picture` / `IsPictureCurrent` remain in Core as the topmost-picture view that the
  Phase 5–7 playback tests use as their oracle; no product code reads them.
- Inspector: Transform (Position X/Y px, Scale %, Rotation °, Opacity %) for video, image and text;
  Crop (Left/Top/Right/Bottom %) for video and image. Each field is sent to `SetClipProperties` on its
  own (consecutive changes of one field merge into one undo step, D017); fields are filled from the
  model under the sync guard; limits from `ClipPropertyLimits`; a rejected value (e.g. opposite crop
  edges ≥ 100 %) is reported and the field shows the model again.

Consequences: measured (offscreen software rendering, an upper bound; 1280 × 720 decoded frames, all
layers changing every frame): copy/update 0.11 / 0.24 / 0.55 / 1.31 ms and render 0.8 / 3.2 / 7.4 /
13.9 ms per frame for 1 / 2 / 4 / 8 layers — within the 33 ms budget at 30 fps, no optimization needed.
Placeholder labels scale with the clip (small for small clips); the placeholder design is minimal.

Status: Accepted. Since Phase 8 Step 3 (D023) the plan is the shared Core `CompositionDrawPlan` plus the
Preview's placeholders (`PreviewDrawPlan`), painted by `CompositionPainter` — the same routine the export uses;
layer bitmaps are straight alpha (`Unpremul`) instead of `Opaque`, so images with transparency blend (D018).

---

## D021 — Text clips (Phase 7 Step 8)

Date: 2026-09-24

Decision:
- Adding: `ITimelineEditService.AddTextClip(start)` puts a text clip on the topmost existing video
  track (highest `Order`) at `start` snapped to the frame grid (negative → 0), 5 s long in whole
  frames. Defaults: text "Text" (empty text would be invisible) and the model defaults Segoe UI, 48 px,
  `#FFFFFF`, centred; visual properties default. One `EditPlan`, one undo step "Add Text"
  (`EditPlan` now passes its description to an insert-only command). Rejected without any change when
  the clip would overlap another clip on that track, the track is locked, or there is no video track.
  No track is ever created; adding text never locks the project frame rate.
- UI: "+ Text" in the timeline header adds at the playhead and selects the new clip (Inspector and
  Preview follow); a rejection is reported in the status bar and the selection stays.
- Inspector TEXT section (text clips only): multiline text, font, size, color, alignment — each field
  sent live to `SetClipProperties` on its own, consecutive changes of one field merge into one undo
  step (D017), fields filled from the model under the sync guard. Font: a list of the installed
  families (Avalonia `FontManager` via `IFontCatalog`; no free input); a font that is not installed
  (a project from another machine) is listed first so the clip's font is always shown. Color: a
  `#RRGGBB` field plus a swatch of the stored color — no ColorPicker package. Only a complete value
  valid under D017 (`ClipPropertyValidator.IsHexColor`) is applied, so typing is never reverted
  halfway; other text shows the model again when the field loses focus. Size uses the shared numeric
  rules (`NumericInput`, `decimal?`).
- Empty or whitespace text is a valid property value; such a clip is still not a layer (D018).
  Text edits are presentation-only snapshot changes: no reader, pipeline or seek generation changes
  (D018).
- Timeline label: the first line of the text (any line break), `(empty text)` when the text is
  blank; recomputed on every `TimelineChanged` (edits, undo, redo), not only on `MediaAssetsChanged`.
  `TimelineClipViewModel.Name` notifies the view.

Consequences / known risk: the preview (Avalonia) silently falls back to another font when the clip's
font is missing on the machine, while the Phase 8 export through ffmpeg `drawtext` needs a font file;
missing fonts must be handled there (not in Step 8).

Status: Accepted. The export risk is resolved by D023: text is rasterized like the Preview (no `drawtext`,
no font file); a missing font falls back exactly as in the Preview and is reported as a warning before export.

---

## D022 — Clip speed (Phase 7 Step 9)

Date: 2026-09-24

Decision:
- Value: `ClipSpeed`, an exact multiple of 0.05 from 0.25× to 4× (`k/20`, 5 ≤ k ≤ 80), stored and
  computed as a fraction — never a floating-point factor. Video and audio clips only (a VideoClip's
  sound follows its picture); images and text have no speed.
- Timing rule (the only rounding rule, used by speed changes, trim, split, re-grid, the validator
  and project loading): `SourceLength(N) = ⌊N · s · 10⁷ · rateDen / rateNum⌋` ticks for N project
  frames, independent of the clip's position; `FramesFor(L)` = the largest N with SourceLength(N) ≤ L.
  Invariant: `SourceLength(N) ≤ SourceOut − SourceIn < SourceLength(N + 1)` — the clip is as many
  whole frames as its source range allows. Every existing 1× clip satisfies it.
- Operations at speed ≠ 1× (1× keeps its exact existing rule, SourceOut = SourceIn + duration, and
  produces the same ticks as before):
  - speed change: start, SourceIn and SourceOut stay (a speed change never changes the selected
    source range); N = FramesFor(SourceOut − SourceIn); rejected (no change) below one frame, on
    overlap or on a locked track; consecutive changes of a clip merge into one undo step
    (`SetClipSpeedCommand`), Undo restores every tick;
  - trim end: SourceOut = SourceIn + SourceLength(N); trim start: SourceIn moves by SourceLength(Δ)
    (content under the playhead stays), SourceOut stays; move: source range and frame count unchanged;
    split at k frames: the cut is SourceIn + SourceLength(k), the right half keeps SourceOut;
  - re-grid to another project rate: start snapped as before, source range kept, N = FramesFor on the
    new grid;
  - where two floored lengths are added or subtracted (trim start, split) the result can miss the
    invariant by one tick; only then it is normalized with the smallest change (SourceIn back, or at
    the start of the source SourceOut on, for a shortfall; SourceOut down for an excess).
- A clip set back to 1× after edits at another speed may keep an unused source tail shorter than one
  frame: 1× validation (timeline and format v2) accepts the invariant; format v1 keeps its exact check.
- Playback (renderer-free, also for the Phase 8 export): video — D009 with
  `t(n) = SourceIn + (FromFrame(n) − S) · s` and `δ = ½ · min(P · s, Ssrc)`, exact `Int128`; audio —
  timeline sample k plays source time `SourceIn + (k/48000 − S) · s`, mapped once per reader start
  (`AudioTiming.SourceTimeAt` / `TimelineSampleOfStreamStart`, ≤ ½ sample). A speed change is a timing
  change of the snapshot (spans carry the speed), never presentation-only.
- Audio decoding at speed ≠ 1× (FFmpeg decoder only): `ashowinfo` before the tempo change (its PTS
  are source time; after atempo they count output samples), `apad=pad_dur=0.25` (the tempo window
  reaches the true end of the file), then `atempo=s` (one filter from 0.5× to 4×) or
  `atempo=0.5,atempo=2s` below 0.5×; the pitch is kept. The chain's constant output latency (measured
  with FFmpeg 9.0.1: 449–483 source samples for one filter, 542 for two) is compensated inside the
  decoder in the stream's first-sample index — an implementation detail, not part of the model.
  Tolerance: the placed audio stays within 10 ms of the exact mapping with no accumulated drift
  (measured ≤ 4.7 ms; guarded by an integration test with the real ffmpeg).
- `project.json` format version 2: media clips store `speedRatio: {numerator, denominator}`; the v1
  number `speed` is not written. Version 1 files are read when every speed is exactly 1 (anything
  else could not have been written by the editor: damaged); saving always writes version 2. Recovery
  files use the same format.
- UI: Inspector field "Speed" (0.25×–4.00×, step 0.05×, the shared numeric rules), live, merged undo,
  a value off the 0.05 grid or out of range is reported and the field shows the model again. No speed
  label on timeline clips.

Consequences: builds before this step can't open version 2 files ("saved by a newer version"). At
3–4× the preview decodes 3–4 times as many frames; heavy sources may run late (D012), no
decoding optimization. Out of scope: speed ramps/keyframes, reverse, freeze frames, ripple, export.

Status: Accepted.

---

## D023 — Export (Phase 8)

Date: 2026-09-24

Decision (product owner, 2026-09-24):
- Architecture: the export is an **offline rendering of the Preview**, not a second pipeline with its own
  semantics. A C# compositor renders every frame from the same immutable `PlaybackSnapshot`, and ffmpeg is
  used only as the encoder. No ffmpeg filtergraph for composition, frame selection, speed or text.
  - Video: `PlaybackSnapshot` → output frame n at `MediaTime.FromFrame(n)` → `LayersAt` → composition plan
    (`CompositionMath`, D018) + source frame chosen by `SourceFrameSelector` (D009/D022) → BGRA canvas →
    encoder.
  - Audio: `PlaybackSnapshot.AudioSpans` → `AudioTiming` placement (D013/D022) with the same audio decoder
    (tempo chain and latency compensation included) → sum × `EffectiveGain`, clamped to [−1, 1] as the
    playback mixer does → 48 kHz stereo float → encoder.
  - Rules that already exist in Core are never re-implemented in Export (crop, transform, opacity, layer
    order, culling, speed mapping, audio placement). Where Core logic is currently embedded in playback
    classes (e.g. the per-clip audio placement in `AudioSpanReader`), it is moved into Core and shared.
  - Layers: Core owns the composition model, geometry, layer order, transforms, crop, opacity, text
    geometry and the draw plan; rasterization into BGRA is backend-specific (chosen by a spike in Step 3;
    Core never depends on Avalonia); Export (`src/Export`) owns the offline orchestration; Video owns the
    FFmpeg encoder (arguments, pipes, stderr) — FFmpeg types stay out of Core, Timeline and Export.
  - Unlike realtime playback, the export never substitutes: frames and samples are read blocking (no late
    frames, no underrun silence). Performance is secondary in Phase 8 (correctness and determinism first).
- Output (fixed, no user settings in Phase 8; `ExportFormat` / `ExportOutput` in `Core/Export`):
  - MP4, H.264 (libx264, CRF 18, preset medium, 8-bit 4:2:0, BT.709), AAC-LC 48 kHz stereo 192 kbps;
  - size = the canvas `ProjectSettings.FrameWidth × FrameHeight` (must be even), frame rate = the exact
    rational `ProjectSettings.FrameRate` (constant frame rate); no scaling of the composition;
  - `FrameCount` = whole frames covering the sequence duration (`Sequence.Duration()`, i.e. including gaps
    and clips on hidden tracks — as the Preview plays it); gaps are black; the audio track has exactly the
    samples of that duration and is always present (silence when nothing is audible);
  - `ProjectSettings.AudioSampleRate` stays stored but unused (output is always 48 kHz, as playback).
- Preview ↔ Export contract: equality of the model — same snapshot, layers, geometry, frame selection,
  text layout rules and audio placement — not of pixels: the Preview decodes at ≤ 1280 × 720 and draws on
  screen, the export decodes at full size and encodes to YUV, so pixels are compared with tolerances
  (geometry ±1 px, colour within YUV quantization, audio within the 10 ms of D022).
- Contract (`Core/Export`, `Core/Interfaces/IExportService`):
  - `ExportPreflight.Check(project, outputPath, environment)` runs on the UI thread, builds the snapshot
    itself and returns every issue at once plus an `ExportJob` (snapshot + full output path) only when no
    error was found. Errors: empty timeline; odd canvas; invalid output path (missing, relative, not
    `.mp4`, invalid characters, an existing folder); missing output folder; output = a media file of the
    project; ffmpeg unavailable; media offline (not in the project, marked missing, or its file gone now),
    not analysed (pending, running or failed — images included) or unsupported by its clip. Warning:
    a text clip's font is not installed (the Preview's fallback font is used). Media and font issues are
    grouped per media file / font and list every affected clip.
  - Only clips that reach the output are checked — the ones the snapshot plays: pictures on visible tracks
    with opacity > 0; audio on unmuted tracks of unmuted clips with volume > 0 (a VideoClip on a hidden
    track still counts for its audio); text on visible tracks with opacity > 0 and non-blank text.
  - `IExportService.ExportAsync(job, progress, ct)`: off the UI thread; `ExportProgress` (stage, done,
    total); decode/encode/output failures → `ExportException` (`ExportFailure`); cancellation →
    `OperationCanceledException`. Output is written to a temporary file and moved into place on success
    only; on failure or cancellation no partial file remains and an existing output file is untouched.
    `IsAvailableAsync` tells the preflight whether ffmpeg can be used.
  - Effects and transitions stored in the model are ignored, as in the Preview.
- UX: a modal progress window with Cancel; editing is blocked while exporting (the job's snapshot is
  immutable anyway).
- `LastExportSettings` is session state (like D015): saved in `project.json`, but updating it never makes
  the project dirty and never enters undo/redo. *(Corrected at the Step 7 closeout, see below: not saved at all.)* It keeps only the output path and the (fixed) format
  enums; the size / double frame rate / bitrates of earlier builds are no longer mapped and are ignored on
  read (unknown properties, D014) — no competing quality semantics, `formatVersion` stays 2.
- Out of scope: HDR / 10-bit tone mapping and colour management (known issue stays), user quality
  presets, bitrate, scaling, in/out range, other containers/codecs, hardware encoding, background export
  while editing, embedding fonts, transitions/effects.

Context: D018 anticipated an ffmpeg filtergraph. Analysis before Phase 8 showed it would re-implement the
semantics approximately: `setpts`/`fps` differ from the D009 sample point, `overlay`/`crop`/`scale` work in
whole pixels while the Preview is sub-pixel, `drawtext` needs a font file and lays out and rotates text
differently, and the `atempo` latency would have to be compensated a second time.

Consequences: `IExportService` takes an `ExportJob` instead of the mutable `Sequence` / media list (D011:
background work never reads the live model) and gained `IsAvailableAsync`. `ExportSettings` lost
`Width`, `Height`, `FrameRate` (a `double`, against D006), `VideoBitrateBps` and `AudioBitrateBps`.
The export is expected to be slower than real time with several layers or 4K sources; throughput is a
later phase.

Refined in Step 2 (2026-09-24), source frames:
- `ExportFrameSource` (Export) yields output frame n as `LayersAt(FromFrame(n))` (no `mayOcclude` veto: a
  failure stops the export) with the decoded frame of every picture layer; frames in ascending order within
  `[0, FrameCount)`. `FrameCount` = `FrameMath.CeilingFrame(Duration)` — playback's last frame + 1.
- `ExportPictureReader` (per clip, internal): opens the decoder at the first frame the layer is visible with
  that frame's `SourceFrameSelector.SamplePoint` (decoder contract as for the Preview: first frame at or before
  the point, or the stream's first frame), then for each request advances while the next decoded frame is
  `IsAtOrBefore` the point. Result: the last frame at or before the point (= `SourceFrameSelector.Select`),
  hold-first before the stream's first frame, hold-last after its end; a still image is decoded once. A
  request waits until the answer is certain — never a late or previous frame. Readers exist for exactly the
  picture layers of the current frame (closed when a layer leaves or is culled, reopened when it returns),
  as in the Preview's pipeline.
- Decoding: full source resolution (maximum frame size 16384, i.e. no downscaling; the Preview keeps ≤ 1280 ×
  720) and software decoding by default (deterministic; no mid-stream hardware fallback needed). Any decoder
  failure, a stream without frames, or an offline/unsupported span → `ExportException` (`DecodeFailed`;
  ffmpeg missing → `EncoderUnavailable`).
- Export references Core only, so realtime playback readers (Timeline) can't be used by it.

Refined in Step 3 (2026-09-24), composition and rasterization:
- Shared plan in Core (`Core/Composition/CompositionDrawPlan.cs`): `ResolvedLayer` (a `LayersAt` layer + its decoded
  frame, none for text) → `CompositionDrawPlan.Build(canvas, canvasToTarget, layers)` → `FrameDraw` (crop via the
  frame's `NormalizedSourceRect` into the crop-local rectangle, D018 transform, opacity) / `TextDraw` (text
  transform, properties, opacity), canvas bounds as clip, opaque black background. The Preview uses it through its
  "contain" viewport and adds only its playback states (`PreviewDrawPlan` in UI: placeholders as `PlaceholderDraw`,
  pending skipped, late frames drawn); the export uses it at the canvas size (`ExportFrame.DrawPlan()`, identity).
- Text box rule, written down on `TextDraw`: laid out at the font size without wrapping, box = widest line (trailing
  spaces included) × laid-out height, lines aligned inside the box, box centred on the local origin; glyphs, line
  height and fallback of a missing family are the text engine's (Avalonia `FormattedText`).
- Rasterizer: `ICompositionRasterizer` (Core) — canvas-size plan → BGRA, opaque; implemented by
  `AvaloniaCompositionRasterizer` (UI/Rendering): Avalonia offscreen `RenderTargetBitmap` + the Preview's own drawing
  routine `CompositionPainter`. Spike with Avalonia 11.1.3 and the app's platform (`UsePlatformDetect`,
  Win32 + Skia): rendering off the UI thread works and gives the same bytes as on the UI thread (also text of every
  alignment, several families and a missing one); 4 threads × 1 200 renders of 1080p deterministic; handles and
  memory flat over 3 × 400 renders; ~3.6 ms per 1080p frame with a rotated full-frame bitmap and text. Only
  `AvaloniaObject`s are thread-bound (`SolidColorBrush` throws "Call from invalid thread"), so the painter uses
  immutable brushes/pens only. SkiaSharp directly was not needed (no new dependency; Core has no Avalonia reference).
- Found and fixed: the Preview created layer bitmaps as `AlphaFormat.Opaque`, so the transparent parts of images
  were drawn opaque (a 50 % alpha pixel at full strength), against D018 ("images may carry alpha"). ffmpeg delivers
  straight alpha (`bgra`: PNG green at 50 % → B0 G255 R0 A127); the shared bitmaps are now `Unpremul` — video frames
  (alpha 255) look exactly as before.
- Guarantees are tested at two levels: the plan (Core/UI tests, exact values) and pixels (Rendering.Tests: every
  pixel ≥ 1 px inside/outside a layer edge has the expected colour, colours ±2–3; the Preview's control and the
  export rasterizer give identical bytes for the same canvas-size composition, pictures and text).

Refined in Step 4 (2026-09-24), audio:
- Shared placement in Core (`Core/Playback/AudioPlacement.cs`): `AudioPlacement.Of(span)` → `FirstSample` / `EndSample`
  (⌈S⌉, ⌈E⌉ at 48 kHz), `SourcePositionAt(k)` (1×: start of source sample k + d, d rounded once per clip; other speeds:
  `AudioTiming.SourceTimeAt`), `TimelineSampleOfStream(F)` (1×: F − d; other speeds: `TimelineSampleOfStreamStart`,
  nearest sample once per stream), `Request(asset, k)`. It is exactly the math `AudioSpanReader` had inline; the reader
  now calls it. Streams are placed by the decoder's real first sample (its atempo latency is compensated inside
  `FfmpegAudioDecoder`, unchanged); samples before the need are dropped, a later start or an early end is silence,
  nothing plays outside the clip. SourceOut is not used — the timeline edge ends the clip.
- Shared mix (`AudioMix`): `Gain(span)` = `(float)EffectiveGain`, `Add` = Σ sample × gain, `Clamp` to [−1, 1] after the
  sum — no normalization, limiter, compressor, auto gain or headroom. `AudioMixer` (Preview) and the export use it.
- Export: `ExportAudioSource` (public) mixes the snapshot's audible spans (gain ≠ 0, snapshot order = the Preview's
  mixer order) into exactly `ExportOutput.AudioSampleCount` frames of 48 kHz stereo float, read sequentially; silence
  where nothing plays, also for a project without audio. `ExportAudioReader` (internal, per span) opens the decoder at
  the first sample needed and waits for data (no underrun). Muted clips / volume 0 are not decoded (same output as the
  Preview mixing them at 0; the preflight doesn't check them either). An audible offline/unsupported span or any
  decoder exception → `ExportException` (`DecodeFailed`; ffmpeg missing → `EncoderUnavailable`). A stream that ends
  normally without samples is silence, as in the Preview (the source has no audio there).
- Verified: for the same snapshot and decoder the export's samples equal the Preview's pipeline + mixer output exactly
  (fake decoder: 1×/0.25×/1.35×/4×, trim, split, gap, volume, clip/track mute, hidden video track, overlaps, clipping,
  decoder prerolls; real ffmpeg: 0.25×/1×/4×), bursts within ±5.1 ms of the exact mapping (±0.05 ms at 1×), no drift.
- Strict end of stream (follow-up, product owner 2026-09-24): `VideoDecodeRequest.StrictEnd` / `AudioDecodeRequest.StrictEnd`
  (default false). At the end of stdout the ffmpeg streams ask `FfmpegProcess.AbnormalExitAsync` (one shared check: wait
  for the exit, non-zero code → failure text): a failed ffmpeg is always an error when nothing was delivered (unchanged)
  and, with a strict end, also after frames/samples (`DecoderFailed`); exit code 0 is a normal end. The export sets
  `StrictEnd` in `ExportPictureReader` / `ExportAudioReader` (→ `ExportException` `DecodeFailed`); playback never does, so
  the Preview keeps holding the last frame / playing silence after a decoder that failed mid-stream. Cancellation while
  the end is judged is `OperationCanceledException`; disposal kills the process tree.

Refined in Step 5 (2026-09-24), the encoder:
- Contract (Core `Export/IExportEncoder.cs`): `IExportEncoder.StartAsync(ExportOutput, destinationPath)` →
  `IExportEncoding`: the whole audio first (`WriteAudioAsync`, exactly `AudioSampleCount` stereo frames), then exactly
  `FrameCount` BGRA canvases (`WriteFrameAsync(bgra, stride)`), then `CompleteAsync`. Nothing reaches the destination
  before a successful completion: temporary files next to it, moved into place (replacing an existing file) at the end;
  failure, cancellation or disposal without completion delete them. Errors: ffmpeg missing → `EncoderUnavailable`,
  encoder failure (non-zero exit, stopped reading) → `EncodeFailed`, move/folder → `OutputFailed`; cancellation →
  `OperationCanceledException`; wrong order/counts → `InvalidOperationException`. No composition or audio semantics.
- Implementation (Video `FfmpegExportEncoder`), two ffmpeg passes, input on stdin (`FfmpegProcess` gained an optional
  stdin): (1) `-f f32le -ar 48000 -ac 2 -i pipe:0 -c:a aac -profile:a aac_low -b:a 192000 -ar 48000 -ac 2 -f mp4 <tmp.m4a>`;
  (2) `-f rawvideo -pix_fmt bgra -s WxH -framerate num/den -i pipe:0 -i <tmp.m4a> -map 0:v:0 -map 1:a:0
  -vf scale=out_color_matrix=bt709:out_range=tv,format=yuv420p,setparams=range=tv:color_primaries=bt709:color_trc=bt709:colorspace=bt709
  -fps_mode passthrough -c:v libx264 -preset medium -crf 18 -pix_fmt yuv420p -colorspace bt709 -color_primaries bt709
  -color_trc bt709 -color_range tv -c:a copy -movflags +faststart -f mp4 <tmp.mp4>` (plus `-hide_banner -nostats
  -loglevel error -y`). Two passes: the audio is short to encode, the producer order is simple and no named pipes are needed.
- Measured with FFmpeg 9.0.1: without colour options ffmpeg converts BGRA with the BT.601 matrix and leaves the stream
  untagged (red → Y 81 instead of 63); `-colorspace`/`-color_range` alone switch to BT.709 but leave primaries/transfer
  "unknown" (frame properties win); the explicit scale + setparams give the BT.709 limited-range values ±1 of the formula,
  all four tags, and ≤ 3 per channel after decoding back to BGRA. Rational rates give exact timestamps (`pts·tb =
  n·den/num`, time bases 24000/30000/12800/60000/12288), every frame once. AAC: 1024 priming samples (first packet pts
  −1024) are removed by the MP4 edit list — the decoded audio has exactly the samples written, a burst lands 0.5 sample
  from where it was written; the stream copy of pass 2 keeps this unchanged; no compensation of our own. A/V: a burst
  written at the start of frame k is heard within 0.04 ms of frame k (23.976/29.97/25). Blocked stdin writes end on
  cancellation (.NET cancels the pending pipe write).

Refined in Step 6 (2026-09-24), orchestration:
- `ExportService` (Export) implements `IExportService` by connecting the parts and nothing else: one
  `ICompositionRasterizer` per job from a `Func<ICompositionRasterizer>` (the app registers the Avalonia one — Export and
  Core never reference a rendering backend), `IExportEncoder.StartAsync`, the whole audio from `ExportAudioSource`, every
  frame `ExportFrameSource` → `ExportFrame.DrawPlan()` → rasterizer → `WriteFrameAsync`, then `CompleteAsync`. No
  composition, placement, timing or output-file logic of its own; the audio length is the source's (the encoder checks
  the count), the destination is the encoder's (temporary files, move on success).
- Preflight boundary: the service takes the `ExportJob` as `ExportPreflight` produced it and does not repeat its checks
  (Step 7's UI runs the preflight); a job that bypassed it is rejected by the encoder before anything is written
  (no frames, odd size, relative path, missing folder, ffmpeg missing).
- Runs on the thread pool; progress = the existing `ExportProgress` (Preparing 0/1; Audio samples; Video frames;
  Finalizing 0/1, then 1/1 only after `CompleteAsync` succeeded), monotonic within each stage.
- Failures keep their type and category (`ExportException` with `ExportFailure`, a rasterizer error as thrown);
  cancellation stays `OperationCanceledException` and reaches the sources, the encoder writes and `CompleteAsync`. Any
  exit other than success disposes the sources, the rasterizer and the unfinished encoding (ffmpeg ended, temporary
  files deleted, destination untouched).

Refined in Step 7 (2026-09-25), export UI:
- `ExportWorkflow` runs `ExportPreflight` twice — before the file picker (the output-file issues are left for later,
  everything else is shown: errors block, warnings need Continue) and with the chosen file (it creates the job and its
  snapshot). The UI has no validation rules of its own. The picker adds no overwrite prompt; the workflow asks
  "Replace file?" itself; the replacement stays the encoder's (on success only). A `.mov` name is reported, never renamed.
- Editing lock: a shared UI `EditingLock` from the job's creation until `ExportAsync` returned (a cancelled export
  included) disables the commands and edit entry points that change the project (Toolbar, Timeline, Inspector, Media
  Browser); viewing and playback stay; the model services and undo/redo are unchanged. The modal progress window blocks
  the main window's input as well.
- Progress: the window shows the service's `ExportProgress` as is (stage, done/total, percent); it pulls the latest
  report with a timer, so nothing is marshalled and nothing arrives after it closed. Cancel (button or title-bar close)
  only cancels the token; the window closes when `ExportAsync` has finished.
- Results: success (path), cancelled (a status message, not an error), `ExportFailure` categories with their own
  messages, anything else as an unexpected error, logged with the exception. `LastExportSettings.OutputPath` changes only
  after a success (session state).

Corrected at the Step 7 closeout (product owner, 2026-09-25): `LastExportSettings` is **session-only** — not part of the
serialized project. `ProjectSerializer` no longer writes it (neither `project.json` nor recovery files) and no longer
reads it: a `lastExportSettings` in an older file is ignored like any unknown property (D014) whatever its content, and
an opened project starts with empty export settings. It stays in memory for the session, never dirty, never undoable.
`formatVersion` stays 2. This supersedes "saved in `project.json`" above.

Refined in Step 8 (2026-09-25), Preview ↔ Export parity (product owner decisions A–F, 1a, 2a, 5a/5c, L1-c). Three
kinds of statements follow and must not be mixed: (1) the normative parity criteria — pass/fail, enforced by the test
suite; (2) the results of the measurement-only Step 8.6 — data, no criterion; (3) the measured characteristics of the
H.264 encoding — known properties of the fixed output format, not requirements.

(1) Normative parity criteria (the numeric form of "geometry ±1 px, colour within YUV quantization, audio within 10 ms"
above). Checked with software decoding (`Hardware = Auto` is not a criterion, decision B), on generated sources: PNG
with straight alpha, JPEG, display-matrix 90° video, video at 0.25× / 2× / 4×, source rate ≠ project rate, 1080p and
4K sources (4K scenes only with `AIVE_HEAVY_TESTS=1`, decision F); every generated source is itself verified with
ffprobe/ffmpeg and the app's analysis (Step 8.1).
- Canvas size, sources ≤ 1280 × 720 (Step 8.2, A1): the Preview (its own `VideoPipeline` and control) draws exactly the
  export's canvas — byte-equal. Where a scene shows a source 1:1, the canvas also matches that source frame decoded by
  ffmpeg and chosen by an independent exact D009/D022 computation (max ≤ 3, mean < 0.5; neighbouring frames must differ),
  so a rule wrong on both sides fails too.
- Sources above the Preview's decode limit (Step 8.3, A2), measured at the canvas size:
  - geometry ±1 px on luma (BT.601 weights; decision 2a): every lit (luma > 10) / black pixel of one side has one of the
    same kind within 1 px on the other; colour-bar edges (luma step > 40 between flat plateaus) at the same x ±1. Luma,
    not RGB, because both sides are 4:2:0 by design and the Preview's chroma of a 4K source is coarser than 1 px;
  - colour within one YUV code step where both pictures are flat (5 × 5 range ≤ 4 per channel, over > 30 % of the
    frame): R ≤ 4, G ≤ 3, B ≤ 4 (decision 1a — one step of each of Y, Cb, Cr through the BT.601 limited-range conversion
    plus rounding; derived from the formula, not fitted to data);
  - the same source frame: after 16 × 16 block averaging the Preview's frame n is closest to the export's frame n.
- Viewport (Step 8.4, A3, two cases): the Preview's control laid out smaller than the canvas ("contain": scale =
  min(vw / cw, vh / ch), centred, sub-pixel) against the export canvas reduced to the same viewport by an independent
  exact area filter, with the Step 8.3 criteria in viewport pixels; nothing is drawn outside the canvas.
- Through the codec, MP4 → Preview (Step 8.5, A4, decision 5c): geometry — at most 0.01 % of the pixels outside the
  ±1 px luma mask (decision 5a, kept by 5c); bar edges ±1 px and the same source frame strictly (not for a scene whose
  frames are identical by construction). Colour is deliberately **not** a criterion on this leg: strict colour parity
  is the canvas-level checks above, and a colour error of the export that the codec would carry through is caught there
  (and by the Rendering tests), not here.
- Sound through the codec (Step 8.5): the MP4's AAC track against the Preview's own `AudioPipeline` + `AudioMixer` with
  the real decoder — the same length (`AudioSampleCount`), whole-signal lag and every burst onset within 10 ms (D022).
  The suite also requires SNR ≥ 20 dB as a sanity bound for the AAC round trip (the value the Step 6 tests used); it is
  not a fidelity requirement of this decision.
- The codec leg MP4 → export canvas (decision L1-c) has **no numeric tolerance**: none is defined here, and the
  Step 8.6 data below must not be read as one. Whether and how to set one is an open product decision. Unchanged
  meanwhile: the Step 5 encoder tests (BT.709 values on flat colours) and the Step 6 end-to-end bound "mean |Δ| ≤ 3 per
  frame, MP4 vs canvas" on its nine simple scenes — a test sanity bound of those scenes, not a codec tolerance.

(2) Step 8.6 results, measurement only (scratch tool outside the repository, not part of the suite, no thresholds; two
identical runs). MP4 decoded by ffmpeg (BT.709 tags honoured) against the export canvas, every frame of the 30 existing
parity/end-to-end scenes (820 frames), 8-bit RGB. The error was also split with a reference encoded through the same
BGRA → BT.709 limited 4:2:0 conversion but libx264 lossless (`-qp 0`): "floor" = reference vs canvas (conversion and
chroma subsampling, no quantization), "quant" = MP4 vs reference (the lossy encoding itself). Pooled per scene group:

| Group (scenes, frames) | Total mean / p99 / p99.9 / max / PSNR | Floor mean / p99 / max | Quant mean / p99 / p99.9 / max / PSNR |
|---|---|---|---|
| Flat, static (3, 75) | 1.11 / 2 / 2 / 2 / 46.9 dB | 1.11 / 2 / 2 | 0 / 0 / 0 / 0 / lossless |
| Static graphics: text, PNG, JPEG (3, 75) | 1.41 / 16 / 123 / 197 / 31.1 dB | 1.28 / 16 / 193 | 0.27 / 4 / 7 / 62 / 48.8 dB |
| Moving pattern 320 × 180 (15, 405) | 2.04 / 29 / 113 / 255 / 29.8 dB | 1.76 / 26 / 255 | 0.69 / 11 / 22 / 94 / 41.1 dB |
| Speed 0.25× / 2× / 4× (3, 75) | 1.65 / 19 / 34 / 75 / 36.5 dB | 1.37 / 15 / 43 | 0.72 / 10 / 21 / 76 / 41.2 dB |
| 1080p sources, HD canvases (6, 50) | 1.19 / 14 / 51 / 255 / 35.7 dB | 1.10 / 12 / 255 | 0.28 / 6 / 14 / 84 / 46.2 dB |

Per frame, total mean |Δ| ranged 0.14–4.57 and PSNR 23.7–51.8 dB (worst scene: mixed layers, 4.02 / 24.4 dB, of which
quant 1.10 / 38.9 dB). Quant grew with speed (0.25× → 2× → 4×: mean 0.55 → 0.76 → 0.83, PSNR 44.0 → 40.6 → 40.1 dB);
over all frames the total error correlated more with spatial complexity (r = 0.46) than with frame-to-frame change
(r = 0.15). Limits: synthetic sources only (no camera footage, noise or grain), short clips (5–30 frames), mostly
320 × 180, no 4K, one encoder configuration and one FFmpeg build, RGB metrics only (no YUV/SSIM). Per-scene tables are
in `progress.md`.

(3) Measured H.264 characteristics — properties of the fixed format (CRF 18, preset medium, 8-bit 4:2:0, BT.709
limited), **not** pass/fail requirements:
- Most of the MP4 → canvas difference is present without any lossy encoding (the floor): on flat, static content the
  whole error is the RGB → YUV limited → RGB round trip (1–2 per channel, quantization adds nothing); in the eleven
  scenes with a total PSNR of 24–34 dB the floor alone is within 0.6 dB of the total and reaches max 193–255.
- The largest values sit in detailed areas, not flat ones (flat-area max ≤ 52, detailed-area max up to 255).
  Observation, not verified separately: these look like sharp edges of saturated colours, where 4:2:0 halves the
  chroma resolution.
- The lossy part alone stayed at 38.9–50.6 dB PSNR per scene (the flat, static scenes: lossless; p99 ≤ 13, p99.9 ≤ 26,
  single samples up to 94), larger for moving than for static content.

Consequences: no production change in Step 8 — every parity check passed against the Steps 1–7 code; one suspected
defect was a test error (a same-frame check on a scene whose frames are identical, removed there). The suite gained
`tests/ExportEndToEnd.Tests` `GeneratedMediaTests`, `ExportParityCanvasTests`, `ExportParityScaledTests`,
`ExportParityViewportTests`, `ExportParityEncodedTests` and the shared checks `ParityMetrics`; each criterion was
confirmed by mutations of the production code (restored byte for byte afterwards).

Status: Accepted.

---

## D024 — Phase 9 scope and constraints (Quality)

Date: 2026-09-25

Decision (product owner, 2026-09-25, Step 9.2; the audit of Step 9.1 accepted):
- Steps, in this order, each accepted separately: 9.1 audit (done), 9.2 scope formalization (this decision),
  9.3 stability & error handling, 9.4 thumbnails + cache, 9.5 waveform, 9.6 hotkeys, 9.7 performance baseline &
  optimization, 9.8 polish & cleanup, 9.9 CI / quality gates, 9.10 final verification & closeout. Scope,
  acceptance criteria, out-of-scope items and dependencies of each step: `docs/DEVELOPMENT_PLAN.md`,
  "Phase 9 — Quality: steps".
- Kinds of statements, kept apart in the plan and in every step report: **product requirements** (behaviour the
  product owner accepts), **measurement-only results** (data with its method, no threshold unless a later decision
  sets one), **quality gates** (pass/fail conditions on build, tests, CI, process) and **implementation details**
  (binding constraints on how; everything else is a routine engineering choice).
- Per step (summary; the plan is normative):
  - 9.3: fix the close hang (root cause); cancel media analysis when the project is replaced (New; Open and Recover
    likewise); bound analysis concurrency; survive audio device removal / default-device change; use the existing
    `Area=Ffmpeg` → `ffmpeg-*.log` sink for ffmpeg / ffprobe diagnostics; the damaged-project message must not
    promise a backup that does not exist — implement the backup if small and isolated, otherwise remove the
    promise. The single, unreproduced `Project.Tests` hang of Phase 8 is a watched regression concern, not a
    defect needing a workaround.
  - 9.4: real Media Browser thumbnails; project-scoped cache with invalidation; deterministic frame (the D009
    rule at a fixed source time); offline media not decoded; the cache of an unsaved project decided
    architecturally without changing the project format (`formatVersion` 2); `MediaAsset.ThumbnailPath` not removed
    or changed without need; caching never changes D009 / D022 frame selection. Timeline clip thumbnails are out.
  - 9.5: waveforms of audio on timeline clips, produced through the media / FFmpeg abstraction (never from UI),
    cached, consistent with mute / volume; speed / time mapping only as far as the timeline display needs. Not an
    audio editor.
  - 9.6: J / K / L, loop, shortcuts that follow from existing commands, no shortcut while typing, existing
    shortcuts unchanged, routing tests, optionally a small shortcut help. No configurable hotkeys.
  - 9.7: baseline first (Preview / render, export throughput, memory, processes / handles, Cancel latency, 1–8
    layers); no targets before it; then optimizations only with D023 semantics and the parity suite unchanged; no
    hardware decode / encode or other semantic change for speed without a separate decision.
  - 9.8: limited polish (loading / busy, disabled, errors, empty states, progress / cancel feedback, obvious issues
    found in Phase 9), no redesign; `PlaybackFrame.Picture` removed only if an audit shows no production reader and
    no runtime change.
  - 9.9: a minimal GitHub Actions gate — restore, build, test, FFmpeg for the tests that need it, in a fixed, known
    version compatible with the current tests; the job fails when ffmpeg-dependent tests are skipped unexpectedly.
    No coverage tooling (coverlet) yet.
  - 9.10: full verification, manual plans, documentation, acceptance.
- Constraints for the whole phase:
  - D023 is unchanged. L1-c stays an open product decision: no numeric tolerance for MP4 → export canvas, the
    Step 8.6 results and D023's Step 8 text are not modified.
  - Out of scope: HDR / 10-bit, colour management, export quality presets, bitrate policy, hardware encoding,
    hardware decoding as a required optimization, a full audio editor, configurable hotkeys, a large UI redesign.
  - Existing Preview ↔ Export parity tests are never weakened, re-baselined or removed to make a Phase 9 change pass.
- Sub-decisions left to the start of their step (proposed there, confirmed by the product owner, recorded as a
  refinement of this decision): default-device change behaviour and backup vs corrected message (9.3); thumbnail
  source-time rule, unsaved-project cache location and the thumbnail interface (9.4); waveform on video clips with
  sound and the mute / volume display rule (9.5); the meaning of J, loop details and the list of extra shortcuts
  (9.6); the measurement tool's form, the measurement method and practical criteria for resource leaks and, after
  the baseline, what to optimize (9.7); the polish list (9.8); the FFmpeg version used by CI (9.9).

Context: the development plan named Phase 9's topics (performance profiling, caching, error handling, polish,
hotkeys, waveform, thumbnails) without steps or acceptance criteria. The 9.1 audit found: no thumbnail, waveform or
cache implementation (only `IThumbnailService` / `IVideoEngine` declarations, `AppPaths` cache folders and
`MediaAsset.ThumbnailPath`); an `ffmpeg-*.log` sink nothing writes to; a damaged-project message promising a backup
while `ProjectFileStore` keeps none; the close hang and other known issues in `progress.md`; no CI; export throughput,
memory, handles and Cancel latency never measured (deferred from Phase 8); the playback model plays forward at 1×
only (relevant to J).

Consequences: Phase 9 work is planned and accepted per step; a step's open sub-decisions are asked before its
implementation, not guessed. A Phase 9 manual test plan (`docs/PHASE9_MANUAL_TEST_PLAN.md`) is written during the
steps and run at 9.10.

Refined in Step 9.3 (2026-09-25), stability & error handling (product owner decisions after the 9.3 audit; sub-steps
9.3a–f, each accepted separately; details and verification in `progress.md`):
- 9.3a, close hang. Cause: the host was disposed synchronously after the Avalonia lifetime had ended, and
  `PlaybackService`'s asynchronous disposal posted its continuations to the stopped dispatcher's synchronization
  context — a deadlock whenever decoders were open. Decision: playback resources (decoders, their ffmpeg processes, the
  audio device) are released on the UI thread once closing is agreed, before the window closes and the dispatcher
  stops (`MainWindowViewModel.PrepareToCloseAsync` → `PreviewViewModel.ReleasePlaybackAsync`). The release runs once
  (later calls return it) and makes the playback service inert from its first line: no snapshot, transport call or
  seek can open a decoder again. `Program.Main` clears the stopped dispatcher's synchronization context before the
  host's disposal — a defensive safeguard only, correctness does not depend on it.
- 9.3b, analysis generations. Every media analysis belongs to the project it was started for: New, Open and Recover
  (`IProjectService.ProjectChanged`) cancel the running generation; a result of a cancelled generation — also one
  that arrives after the switch — changes no asset and raises no event. The in-flight guard is per asset object, not
  per id (a project reopened while its own analysis runs has the same ids). ffprobe is killed with its process tree on
  cancellation, timeout or any other early end, not only released.
- 9.3c, bounded analysis concurrency: at most 4 analyses at once (an implementation detail, not configurable; chosen
  from a measurement). An analysis holds its slot for all its ffprobe runs, so at most 4 ffprobe processes run;
  waiting media stay "Analyzing"; the wait ends with its generation; ffprobe's timeout starts only when the probe runs,
  i.e. after the slot was obtained.
- 9.3d, FFmpeg diagnostics: one routing rule (`LogArea.ForSource`): the Video subsystem and the ffmpeg / ffprobe
  locators are `Area=Ffmpeg` and go to `ffmpeg-*.log` only (not duplicated into `app-*.log`; `errors-*.log` still
  collects errors of any area; no `export-*.log` writes). `FfmpegProcess` logs the command line at Debug and one end
  entry: a normal exit or an end by the app (seek, shutdown, cancellation) at Debug, a failure on its own as a Warning
  with command line, exit code and stderr. ffprobe runs with `-v error` (stdout stays the JSON); a failure, its timeout
  and a cancellation are logged apart (Warning, Warning, Debug).
- 9.3e, audio device (option B): the current default render device is checked at every start of the audio output —
  Play and the restart after a seek — and compared with the open device by endpoint id (never by name); a different
  id closes the old output and opens one on the new default, the same id keeps the output. No switching during
  playback and no device notifications: a device lost while playing keeps D013 (Stopwatch without a jump, silent until
  the next Play, which opens the default of that moment); a default that can't be opened fails the start as before.
- 9.3f, damaged project: the app keeps no backup of project files and none is added; no message may promise one
  (`ErrorTranslator`'s damaged-project text corrected; `ErrorTranslator` / `CorruptProjectFileException` stay unused
  for now — a possible 9.8 cleanup).
- Left as they are: New during a running import (`ImportManyAsync`) adds the picked files to the new project (known
  issue, out of scope); the single unreproduced `Project.Tests` hang (Phase 8) and failure (9.3d) stay a watched
  concern without a workaround; a real default-device change and a real device removal were not tried on hardware —
  they are manual-only scenarios of the Phase 9 manual test plan.

Refined in Step 9.4 (2026-09-28), thumbnails + cache (product owner decisions PO-1–PO-6 after the 9.4 audit,
2026-09-25, and the reviews of the sub-steps; sub-steps 9.4a–e, each accepted separately; details and verification in
`progress.md`):
- Product owner decisions:
  - PO-1: a saved project caches its thumbnails in `<project>/cache/thumbnails/`.
  - PO-2: a project that was never saved caches them in `%LOCALAPPDATA%\AiVideoEditor\cache\unsaved\<projectId>\thumbnails\`.
  - PO-3: source time `T = min(⌊Duration / 10⌋, 5 s)`, in ticks from the file's start time; D009 decides the frame at T.
  - PO-4: at most 2 thumbnails are made (decoded) at once — fixed, not configurable.
  - PO-5: `MediaAsset.ThumbnailPath` is kept for project-file compatibility, never used or filled; `project.json`
    unchanged (`formatVersion` 2).
  - PO-6: `<project>/cache/thumbnails` is the only cache path; `AppPaths.ProjectThumbnailsFolder`
    (`<project>/thumbnails`) removed.
  - Save As over a folder that already holds thumbnails of the same assets (option C, refined after a second review):
    per asset only its current variant is carried (the latest last-write time, on a tie the ordinally last name); the
    asset's other files in the target are removed only after that copy succeeded.
- 9.4a, interface and service: the thumbnail interface is Core `IThumbnailService` (its former unused
  `GetOrCreateThumbnailAsync → path` contract replaced): `TryGetCached(asset, cacheFolder)` never decodes,
  `GetOrCreateAsync(asset, cacheFolder, ct)` reads the cache or makes the thumbnail; a `Thumbnail` is packed BGRA,
  straight alpha. `IVideoEngine` is not used for it (it stays unimplemented). Implemented in Media
  (`ThumbnailService`) over the app's `IVideoDecoder` (software, scaled to fit 160 × 90 with the aspect ratio, upright)
  and `SourceFrameSelector`: the last frame at or before T, hold-first / hold-last — no `-ss` / `select` / `thumbnail`
  filters; the decoder and D009 / D022 unchanged. Cache: one file per asset, named by asset id, source size,
  last-write time (UTC ticks) and the rule version — any other name is a miss; an internal binary format (magic,
  version, size + BGRA, no PNG); written to a temporary name and moved into place; a missing, damaged or unreadable
  file is a silent miss and is regenerated. Offline media is never decoded: the last cached thumbnail, or none.
- 9.4b, cache location: Core `IThumbnailCacheLocation`, Project `ThumbnailCacheLocation` — the folder follows the
  current project (PO-1 / PO-2; a recovered project keeps its id and so its folder). The first Save moves the unsaved
  cache into the project folder, Save As copies it (the old project keeps its own); a failed carry-over is logged and
  only costs regeneration, never the save. At startup, unsaved caches without a recovery file are removed (best effort).
- 9.4c, queue: `ThumbnailCoordinator` (UI/Services, no bitmaps) requests thumbnails on `MediaAssetsChanged` for video
  and images whose analysis has completed and, cache only, for offline media; audio, pending and failed analysis get
  none. Generations as in 9.3b: New / Open / Recover cancel the previous project's work, whose results are never
  stored or reported; once per asset id and generation (a failure is not retried until the next project); a cache
  read takes no slot and starts no ffmpeg, making one takes one of 2 slots (PO-4); results are applied on the UI
  thread. Closing the app waits for it (`ShutdownAsync`, ≤ 5 s) after playback is released.
- 9.4d, Media Browser: a row shows its asset's thumbnail over the kind's colour tile (56 × 32, aspect kept, centred);
  the tile stays the placeholder for audio, pending, failed and offline media without a cache and while a thumbnail
  is made. The bitmap is made in the view (one per thumbnail instance), never in Core, Media or the coordinator.
  Playback and export never read the cache.
- Left as they are: no cache size limit, eviction or cache UI, no timeline thumbnails (out of scope); a thumbnail that
  could not be made is retried only with the next project (or by reopening it); media that comes back online during a
  session is not re-checked (no relink, as in 9.3); a Save As over a folder of an unrelated project leaves that
  project's thumbnail files there, never read; the generation check after a slot is obtained is defensive — no
  mutation reaches it (the waits of a cancelled generation always end first).

Refined in Step 9.5 (2026-09-28), waveform (product owner decisions PO-W1–PO-W4 after the 9.5 audit, PO-W5 after
9.5a; sub-steps 9.5a–e, each accepted separately; details and verification in `progress.md`):
- Product owner decisions:
  - PO-W1: video clips with sound show a waveform too, in the lower half of the clip; audio clips over the whole clip;
    video without sound, images and text none.
  - PO-W2: the height is linear in the clip's volume — a full-scale peak reaches the full height at 200 % (the maximum
    volume) and half of it at 100 %; a muted clip, or a clip on a muted track, keeps the same shape, dimmed; the
    envelope is `max(|L|, |R|)` on a linear scale.
  - PO-W3: waveforms are made only for media used by a clip on the timeline — nothing at import.
  - PO-W4: the cache lives next to the thumbnails — `<project>/cache/waveforms`, unsaved
    `%LOCALAPPDATA%\AiVideoEditor\cache\unsaved\<projectId>\waveforms` — with their life cycle and key; at most 2
    waveforms are made at once, a limit of its own (never shared with the thumbnails').
  - PO-W5 (refines the plan's "offline media shows no waveform"): offline media may show a waveform cached earlier —
    never decoded or made; without a cached one it shows none.
- 9.5a, data and service: Core `IWaveformService` (`TryGetCached` never decodes, `GetOrCreateAsync`) and `Waveform` —
  one byte peak (`⌈|a| · 255⌉`, capped, rounded up so any sound shows) per 256 source samples (48 kHz, from the
  file's start time), up to where the audio ends. Media `WaveformService` decodes the file's audio once with the app's
  `IAudioDecoder` (from the start, 1×, strict end) and places the samples by the stream's first sample index — the
  samples playback plays; no new ffmpeg path. Made for audio files and video with an audio stream whose analysis
  completed; a decode failure, also midway, caches nothing. Cache file `…-v1.peaks` (`AIVW` header + peaks) with the
  thumbnails' key (asset id, source size and last-write time, rule version), atomic write and offline lookup —
  shared code (`Media/Caching/SourceFileCache`).
- 9.5b, location: the logic of `ThumbnailCacheLocation` is shared (`Project/MediaCacheLocation`) by the thumbnail and
  the waveform kinds (`WaveformCacheLocation`); each kind touches only its own folder and files; an unsaved project's
  `<id>` folder is removed once nothing is left in it, and the startup cleanup removes a kind's folder per orphan.
  Core `IMediaCacheLocation` is the base of `IThumbnailCacheLocation` and `IWaveformCacheLocation`.
- 9.5c, queue: the orchestration of `ThumbnailCoordinator` is shared (`UI/Services/MediaCacheCoordinator<T>`: generations
  cancelled on project replacement with their results dropped, once per asset and generation, cache reads without a
  slot, a slot pool per coordinator, shutdown with the window); `WaveformCoordinator` requests the media of the
  timeline's clips (any track, also hidden or muted) on timeline and media changes. A clip removed from the timeline
  keeps its asset's waveform and work (an undo may bring it back).
- 9.5d, display: a timeline pixel column covers its timeline samples, mapped to the source by the clip's
  `AudioPlacement` — the placement rule of playback and export (D013 / D022) —, so trim and speed ≠ 1× show exactly
  the clip's source range; it shows the largest peak of that range (`UI/Common/WaveformLayout`). `WaveformView` draws
  only the columns inside the timeline's viewport. Display only: playback and export never read waveforms, and the
  waveform never changes the audio.
- Left as they are: no cache size limit, eviction or cache UI (as 9.4); no waveforms in the Media Browser, no audio
  editing, scrubbing or meters (out of scope); a waveform that could not be made is retried only with the next project
  (or by reopening it); media coming back online during a session is not re-checked; while a trim of a clip's start
  is dragged the waveform follows the model, not the preview, until the edit; the waveform starts at the clip border's
  inner edge (1–2 px); at 100 % (PO-W2) loud sound takes under half of the height and a muted quiet clip is faint;
  `AppPaths.UnsavedThumbnailCacheRoot` now names the unsaved root of both kinds (rename left to 9.8).

Refined in Step 9.6 (2026-09-28), hotkeys (product owner decisions PO-H1–PO-H4 after the 9.6 audit; sub-steps 9.6a–d,
each accepted separately; details and verification in `progress.md`):
- Product owner decisions (forward playback at 1× only, D010 / D011 — no reverse or faster playback):
  - PO-H1: J = back one second, the playback state kept (playing continues from there, paused stays paused).
  - PO-H2: K = pause (nothing when paused), L = play (nothing when playing; at the end from 0, D011); Space stays
    Play / Pause.
  - PO-H3: loop — a toggle button in the Preview transport and Ctrl+L; while on, reaching the end of the sequence
    continues from the start (the whole sequence, no in / out range); off, the D011 end rule is unchanged. Session
    state only: not in `project.json` (format v2 unchanged), not dirty, not undoable.
  - PO-H4: Ctrl+I Import Media, Ctrl+E Export, \ Zoom to Fit; no shortcut list in the UI (the optional item not chosen).
- Routing (9.6a): one table in `UI/Common/ShortcutRouter` — key with exact modifiers → an existing command; the main
  window's bubbling KeyDown only calls it. Nothing fires while a text input (`TextBox`, also the one inside a
  `NumericUpDown`) has focus or sent the key; a known shortcut whose command can't run (editing during an export,
  `EditingLock`) is consumed and does nothing. Every existing shortcut keeps its key and command.
- J is the existing one-second step (as Shift+←); K / L are Preview commands next to Play / Pause (9.6b). Loop lives in
  the Preview's tick (9.6c): an update that reached the end and paused there is followed by Play, which at the end
  starts from 0 — the D011 rule and `IPlaybackService` are unchanged; a pause the user made is never undone.
- 9.6d: the text-input guard, a known issue since Phase 4, checked in the running app (the Inspector's text box of a
  text clip and a number field).
- Left as they are: the shortcuts are fixed (configurable hotkeys out of scope); only one second back for J; loop
  covers the whole sequence; the end check of the loop (`Position ≥ Duration`) is defensive — no mutation reaches it.

Refined in Step 9.7 (2026-09-28), performance baseline & optimization (product owner decisions after the baseline, the
Preview measurement C and the memory diagnosis of A; details, method, tables and verification in `progress.md`):
- Measurement tool: a scratch console program outside the repository (as Step 8.6), driving the shipped code on
  generated scenarios (1 / 2 / 4 / 8 layers, 720p / 1080p, 4K opt-in); re-run on the same scenarios for every before /
  after. No performance thresholds in the default test suite.
- Leak criteria: a leak is 1) any ffmpeg / ffprobe process still running 1 s after the operation that started it ended
  (export done or cancelled, playback released, project replaced, window closed) or 2) handles or private memory (after
  a full GC) growing at every one of 10 repetitions of the same operation without levelling off. Growth once (warm-up,
  pools) and a change in memory or handles alone are not defects. None was found.
- Baseline findings: the export handled one frame at a time on about one core (half of it waiting for decoded frames);
  the Preview at 8 layers in the running app keeps the content's 30 fps (C: the baseline's CPU-rendered overrun was the
  tool's software rendering, not the app's GPU compositor) — no Preview optimization.
- Chosen and implemented: A — the export decodes ahead and in parallel, within D023. `ExportFrameSource` fetches every
  picture layer's frame of an output frame at once (`Task.WhenAll`; each reader's requests stay ascending, the layer
  order and the D009 / D022 selection unchanged; every fetch ends before a failure propagates); `ExportService` fetches
  frame n + 1 while frame n is rasterized and written — one frame ahead, never more, cancelled and awaited when the
  export ends early. Same frames, order, encoder, format and progress; the parity suite unchanged. Export real-time
  factor +26–35 % at 720p, +41–58 % at 1080p.
- Memory with A: up to 3 decoded frames alive per picture layer (2 before) — a bounded footprint by design, not a leak;
  the peak working set can be higher because of transient large-object-heap garbage between gen2 collections (not live
  data; no growth over repetitions). The earlier 720p × 8 anomaly was an artifact of the measurement method.
- Not now (each would need its own decision): B (overlapping rasterizing with encoding — encoding is 2–3 %), a decoded
  frame buffer pool, GC tuning, a smaller look-ahead for many layers; hardware decode / encode and every other semantic
  change for speed stay out of scope.

Refined in Step 9.8 (2026-09-28), polish & cleanup (the polish list proposed after the 9.8 audit and confirmed by the
product owner as a whole; details and verification in `progress.md`):
- Removed, no runtime change: the single-picture view `PlaybackFrame.Picture` / `IsPictureCurrent` with `PreviewPicture`
  / `PictureKind` (the audit confirmed no production reader; the playback service's buffering state keeps following the
  topmost picture layer, now as a flag of its own); the unimplemented `IVideoEngine` / `EngineProgress`; the unused
  `ErrorTranslator` / `UserFacingError` and their exception types (the damaged-project message comes from
  `ProjectSerializer` through `ProjectFileException`, not from them) with their test. The playback tests read the layers
  (`PlaybackFrameView` in the tests) without weakening; checked by the same mutations before and after (one new test
  on buffering after a ready seek replaces what the compatibility picture's content used to catch).
- Test infrastructure (found while verifying): test classes that create Avalonia controls share one xUnit collection
  (`AvaloniaControlsCollection`) — Avalonia's property metadata caches are not thread-safe, and running them in
  parallel made UI.Tests fail or hang intermittently.
- Renamed: `AppPaths.UnsavedThumbnailCacheRoot` → `UnsavedCacheRoot` (same folder); `IThumbnailCacheLocation.cs` →
  `IMediaCacheLocation.cs`; `StartupThumbnailCleanupTests` → `StartupCacheCleanupTests`. Stale comments corrected
  (`ModuleInfo` of every subsystem, `LayerPictureState.Unsupported`).
- UI (text only, no behaviour change): tooltips name the shortcuts (Ctrl+I, Ctrl+E, \, ←, →, Space / L / K / J); an empty
  timeline says how to add a clip (drag from the Media Browser, Add to Timeline, + Text) and lets drops through; the
  status bar says "Importing N files…" while the picked files are checked (the window renders it first), then the
  result as before — "Import didn't finish." if the check throws.
- Documentation: README, ARCHITECTURE's module table and playback section, `progress.md` known issues, the Phase 9
  manual test plan (legend, scenarios 57–59).
- Not done, by decision: elapsed / remaining time in the export dialog; cache eviction, retry and online re-check;
  a frame buffer pool, GC tuning, a smaller look-ahead; a workaround for the watched `Project.Tests` hang; replacing
  the tests' timed waits; the analysis coordinator's synchronization-context dependence; New during an import.

Refined in Step 9.9 (2026-09-28), CI / quality gates (product owner decisions after the proposal at the start of 9.9;
details and verification in `progress.md`):
- FFmpeg for CI: 9.0.1 essentials from gyan.dev (`GyanD/codexffmpeg` release `9.0.1`,
  `ffmpeg-9.0.1-essentials_build.zip`, SHA256 `fec81ae03971d9dd4be3ebe02e263bd2ec1d789483f931bdba5f5715e65da2e9`) —
  the archive the development machine uses (winget `Gyan.FFmpeg.Essentials` 9.0.1), on which D009, D022 and D023 were
  measured. Not `latest` and not 9.0.2 (unmeasured); no production change. The archive is cached, its SHA256 checked
  every run, and `ffmpeg` / `ffprobe` must resolve to it and report `9.0.1-essentials_build-www.gyan.dev`.
- Workflow `.github/workflows/ci.yml`: on pull requests to `main`, pushes to `main` and by hand; one job on
  `windows-2025` (no matrix), .NET SDK 8.0.424 via `actions/setup-dotnet` (no `global.json`), Debug; restore, build
  with `-warnaserror`, the full suite with `--blame-hang` (10 min) and TRX, results uploaded as an artifact; one run
  per ref at a time (`cancel-in-progress`), 30 min limit.
- Skip gate (`.github/scripts/Assert-TestResults.ps1` over the TRX): fails when fewer than 8 TRX files exist, when any
  test did not pass and was not skipped, and on any skip except the two 4K scenes of `ExportParityScaledTests`
  (`Source_4K_full_canvas`, `Source_4K_scaled_rotated_and_cropped`) with a reason starting "Heavy scenario:"; a skip
  because ffmpeg / ffprobe was not found is always a failure.
- A test adjusted for the runner: `AnalysisConcurrencyIntegrationTests` checked absolute end times of the analyses,
  which include process start-up (much slower on the runner); it now checks the property itself — queued analyses end
  at least one timeout after the first ones.
- Left for the owner / later: branch protection (making the check required) is a repository setting; the audio device
  test passes on the runner without a device (nothing verified there — manual at 9.10); the 4K scenes are not run in
  CI.

Refined in Step 9.10 (2026-09-29), final verification & closeout (product owner decisions after the formal run; details and
verification in `progress.md`):
- The Phase 9 manual test plan was run as a whole in the real app (scenarios 1–59; 14–17 with the product owner switching and
  unplugging the audio devices), `docs/EXPORT_MANUAL_TEST_PLAN.md` again as a regression; results in the plans' "Formal run" /
  result logs. No defect was found.
- Plan wording corrected (clarifications, not defects; product owner): scenario 55 — memory may grow after the first export and
  then stays on a plateau; it must not keep growing from run to run (the bounded footprint of Step 9.7 is kept; memory is not
  returned to the system after an export, and no work to change that is planned in Phase 9); scenario 53 — after an export no
  `ffmpeg.exe` of the export is left, the Preview's processes of the open project stay and are not a leak; scenario 52 — the
  modal progress window (D023) disables the main window during an export, so no click or shortcut reaches it (the routing
  rule — editing shortcuts inert under `EditingLock` — stays covered by tests).
- Left as they are (observed in the run, not defects): the status bar keeps "Playing without sound…" after the sound comes back
  at the next Play (it shows the last message until another one); letters can be typed into a number field (Phase 7: invalid
  text keeps the last value); the "Analyzing" tile phase is too short to see by eye; the Preview restarts its audio readers
  while a project loads.
- Known risk, unchanged in Phase 9 (product owner): the ffmpeg / ffprobe locators run a PATH probe (`-version`) with a 5 s
  timeout and treat a slower answer as "not found" for the rest of the app run; a heavily loaded machine could hit it. Seen
  once on CI, in a test that created a new locator for every analysis (the test now uses the ffprobe found once).
- CI robustness (after the closeout's first CI run failed twice on an unchanged tree): `AnalysisConcurrencyIntegrationTests`
  no longer samples processes or compares absolute times — the limit is checked while a gate holds the first analyses in
  their slots (no queued analysis starts any ffprobe), the timeout rule against the moment the first slot is freed;
  `WaveformIntegrationTests` analyses with the ffprobe found once. Test code only.

Status: Phase 9 complete — steps 9.1–9.10 done, the phase accepted by the product owner on 2026-09-29 (closeout `dcb86cb`,
last Step 9.10 commit `f27a4ab`, PR #8 merged as `409240b`).

---

## D025 — Phase 10: fades and cross dissolve (Transitions & basic effects)

Date: 2026-09-29

Decision (product owner, 2026-09-29, after the Step 10.1 audit; PO-1…PO-7). The MVP has exactly two features,
implemented one after the other: **fade in / fade out** of a clip (Steps 10.3–10.5, finished first), then **cross
dissolve** between two adjacent clips of one video track (Steps 10.6–10.8). Priority: the Preview and the export show
the same thing (D023) and nothing regresses — not the number of effects.

### 1. Model and project format (PO-7)

- `Clip.FadeIn`, `Clip.FadeOut`: `MediaTime` durations (≥ 0) on every clip kind (video, image, text, audio). One pair
  per clip, applied to the clip's picture and to the clip's own sound together (PO-2).
- `Transition` gains its anchor: `LeftClipId`, `RightClipId` (A and B, the clips left and right of the cut) next to the
  existing `Id`, `TransitionTypeId` (the only type: `"crossDissolve"`) and `Duration` (`MediaTime`). Transitions stay in
  `Track.Transitions`; only video tracks have them.
- Durations are times, not frame numbers (D001/D006): a fade or transition of `D` ticks covers
  `F = D.ToNearestFrame(rate)` frames of the project rate (derived on demand; ties go up, as `ToNearestFrame`). The edit
  service stores `MediaTime.FromFrame(F, rate)` for an `F`-frame value, so a stored value is exactly `F` frames; after a
  frame-rate change it keeps its length in time and is re-derived.
- `project.json` `formatVersion` **3**. It is written only as 3. Versions 1 and 2 are read as projects **without fades
  and without transitions**: fades are 0, and a v1/v2 `transitions` array (which no build could create — it has no
  anchor) is dropped on load. A file above 3 is refused by the existing "saved by a newer version" message (D014), which
  is what every v2 build does with a v3 file. The generic `Clip.Effects` list is unchanged: still persisted, never
  rendered; the MVP does not use it (typed properties follow D017 — validation, undo merging, Inspector).
- Loading a v3 file validates (damaged = the whole file refused, as D014): fades ≥ 0; a transition has an id, type
  `"crossDissolve"`, `F ≥ 2` frames, `LeftClipId ≠ RightClipId`, both clips on its track, `A.TimelineEnd == B.TimelineStart`
  exactly, at most one transition per cut, and its zone (§3) fits both clips, also against a transition on the other
  edge of the same clip. Source handles (§4) are **not** checked on load — media may be offline or changed; rendering
  never fails for missing handles (§4).

### 2. Fades (Steps 10.3–10.5)

Frames: the clip covers timeline frames `[s, e)`, `N = e − s`; frame `n` has clip index `i = n − s`.
- Effective lengths: `Fin = min(FadeIn.ToNearestFrame(rate), N)`, `Fout = min(FadeOut.ToNearestFrame(rate), N)`.
- Linear ramp (PO-3): `ramp(k, F) = (k + 1) / (F + 1)` for `0 ≤ k < F`, else 1.
  `in(i) = ramp(i, Fin)`, `out(i) = ramp(N − 1 − i, Fout)`, `fade(i) = in(i) · out(i)` (the two ramps multiply when
  they overlap: `Fin + Fout > N` is allowed). No frame inside a ramp is exactly 0 or 1; the frame before the clip and
  after it do not belong to the clip, so a fade in on the bottom track starts from black.
- Picture: the layer's opacity is `Visual.Opacity · fade(i)`, computed once in Core (`PlaybackSnapshot.LayersAt`) in
  `double`, so the Preview and the export draw the same value with the same painter (D023). A layer with `fade(i) < 1`
  never occludes the layers below (`OccludesBelow` needs opacity exactly 1). The edges where `fade` reaches or leaves 1
  (`s + Fin`, `e − Fout`) count as picture changes for the Preview's prefetch (`NextPictureChange`) — a lower layer
  that becomes visible there is opened ahead like at a clip edge.
- Sound (video clips with audio, audio clips): the clip owns timeline samples `[First, End)` (`AudioPlacement`:
  `⌈t · 48000 / 10⁷⌉`). The fade-in owns `[First, Sin)` with `Sin = ⌈FromFrame(s + Fin) · 48000 / 10⁷⌉`,
  `Nin = Sin − First`; the fade-out owns `[Sout, End)` with `Sout = ⌈FromFrame(e − Fout) · 48000 / 10⁷⌉`,
  `Nout = End − Sout`. Sample `k` gets `g(k) = ramp(k − First, Nin) · ramp(End − 1 − k, Nout)` and is mixed with
  `gain · g(k)` (`gain` = volume, 0 when muted). One shared Core function evaluates it for the Preview's mixer and the
  export (`AudioMix`), in the same order of operations; picture and sound ramps start and end at the same frame
  boundaries (to the sample boundary of the `AudioPlacement` rule). No equal-power curve.
- Fades are **presentation**: a fade change never reopens a decoder (`DiffersOnlyInPresentation`), like opacity/volume.
- Edits (all through `ITimelineEditService`, one undo step each):
  - Set: a property group of `SetClipProperties` (merged like the other properties, D017). Limits: `0 ≤ F ≤ N` each at
    the time of the edit; the stored value is `FromFrame(F)`.
  - Move, trim, speed change, frame-rate re-grid, move to another track: fades stay attached to the clip's edges (a
    trimmed start takes its fade in with it). Fades are timeline time: a speed change neither scales nor re-times them.
    **Changed by the product owner (2026-09-30, Step 10.9 manual run):** an edit that leaves the clip shorter than a
    stored fade cuts that fade to the clip's `N` frames in the same undo step (`EditPlan.ClampFades`; a speed change
    carries it in its speed step, which merges with the next speed changes; typing the first speed back restores
    them). Lengthening the clip again does not bring the longer fade back — only Undo does. Before: the stored value was
    kept and only clamped when rendered — the Inspector then showed a fade longer than the clip whose arrows did nothing
    (one frame less was still longer than the clip). A file holding a fade longer than its clip (saved before the
    change) still loads tick for tick; it renders clamped (`Fin`, `Fout` above) and is cut by the clip's next length
    edit.
  - Split at frame `a`: the left part keeps `FadeIn` and gets `FadeOut = 0`; the right part gets `FadeIn = 0` and keeps
    `FadeOut` (the inner edges have no fade). A part shorter than its fade gets the fade cut to its length (above), so
    a split inside a ramp shortens that ramp. Undo restores the original values exactly (part of the split's command).
  - Delete: the fades go with the clip.

### 3. Cross dissolve (Steps 10.6–10.8)

- A dissolve sits on a cut between A and B on one video track, `A.end == B.start`, at frame `c`. With `F ≥ 2` frames it
  is centred on the cut (PO-4): the **zone** is `[c − hB, c + hA)` with `hB = ⌊F/2⌋`, `hA = ⌈F/2⌉` — `hB` frames of A
  before the cut, `hA` frames of B after it. The clips stay non-overlapping on the timeline (D008 unchanged).
- In the zone both clips are layers of the same track, A below B: A continues past its end by `hA` frames and B starts
  `hB` frames early, both from their **source handles** (§4); outside the zone nothing changes. For zone frame `j`
  (`0 ≤ j < F`): `p = ramp(j, F) = (j + 1)/(F + 1)`; B is drawn over A with opacity `B.Opacity · p`, A with its own
  opacity (PO-6: B over A, no `A·(1−p) + B·p`). The clips keep their transform, crop and opacity in the zone.
- Fades inside the zone: a clip's fade applies only to its own frames `[s, e)`; the extended frames (A after `c`, B
  before `c`) have fade factor 1 from that side. A fade on an edge that has a dissolve is not applied (PO-8 below).
- Sound (PO-5): unchanged — a hard cut at `c`. Extended frames produce no sound.
- Occlusion: in the zone B has `p < 1`, so B never occludes A; A may occlude lower tracks as usual. The zone's start and
  end are picture changes for the Preview's prefetch.
- Any clip kind of a video track may take part (video, image, text).

### 4. Source handles

A handle is how far a clip's source reaches beyond its used range, in frames of the project rate at the clip's speed —
exactly the limits the trim already uses (so a dissolve needs what a trim of that edge outward could reach):
- A (after its end, `hA` frames needed), video clip with source duration `Dsrc`:
  1×: `MaxWholeFrames(Dsrc − SourceIn) − N ≥ hA`; speed `s ≠ 1`: `SpeedTiming.FramesFor(Dsrc − SourceIn, s, rate) − N ≥ hA`.
- B (before its start, `hB` frames needed), video clip: 1×: `s − CeilingFrame(B.TimelineStart − SourceIn) ≥ hB`
  (keeps the source time ≥ 0); speed `s ≠ 1`: `SpeedTiming.FramesFor(SourceIn, s, rate) ≥ hB`.
- Images and text have unlimited handles (a still frame / generated content).
- A video clip whose metadata (duration) is unknown has no handles: a dissolve cannot be created on it.
- Extended frames use the unchanged source-frame rule (D009/D022): `t(n) = SourceIn + (FromFrame(n) − S) · s` is exact
  for frames before `S` or after the clip's end too, so no new selection rule is introduced.
- Rendering never fails for missing handles (media replaced, a file loaded from elsewhere): the decoder holds the first
  frame before the source's first frame and the last after its last one (D009), identically in the Preview and the
  export. Handles are validated only when an edit creates or keeps a dissolve.

### 5. Dissolve and timeline edits (one undo / redo step each)

General rule: an edit that **separates the cut** removes the dissolve automatically in the same undo step (the status
bar says so); an edit that **keeps the cut** but would break the dissolve (handles, zone) is **rejected** with a
message — except a trim, which is clamped (as trims already are, D008).
- Add: A and B adjacent on one unlocked video track, no dissolve on that cut, `F ≥ 2`, the zone fits (below), handles
  suffice. Otherwise nothing is created and the message says why (with the longest `F` that would fit, if any).
- Zone fit: `hB ≤ N_A` and `hA ≤ N_B`; for a clip with dissolves on both edges, the two zone parts inside it do not
  overlap: `hA(left dissolve) + hB(right dissolve) ≤ N`.
- Move: moving both A and B by the same delta (and to the same track) keeps the dissolve (it moves with them to the
  other track). Moving one of them without the other, or both to different places, separates the cut → removed.
- Trim: the cut edges (A's end, B's start) can only open a gap (B / A are adjacent) → the dissolve is removed. The far
  edges (A's start, B's end) keep the cut; the trim is clamped so the zone still fits the clip. Handles do not depend
  on the far edge.
- Split at frame `a` of A or B: a split strictly inside the zone (`c − hB < a < c + hA`) is **rejected**. Elsewhere the
  split is allowed; a dissolve at A's end moves to A's right part (new `LeftClipId`), one at B's start stays with B's
  left part (the original clip). Handles are unchanged (the part next to the cut keeps its SourceOut / SourceIn).
- Delete A or B: removed.
- Speed change of A: A's end moves (D022: the start stays) → the cut separates or would overlap B (already rejected
  by the no-overlap rule) → removed when separated. Speed change of B: the cut stays; rejected when B's handle or the
  zone no longer fits at the new speed.
- Frame-rate re-grid (the first video fixes the rate): `F` is re-derived; a dissolve whose clips are no longer adjacent
  is removed; one whose zone or handles no longer fit rejects the whole operation (as other re-grid failures).
- Clip property edits (opacity, transform, fades, …) never touch a dissolve.
- A locked track rejects every edit on it, dissolves included (as now).

### PO-8 — a fade on an edge that has a dissolve (product owner, 2026-09-29)

- While a dissolve A → B exists, A's `FadeOut` and B's `FadeIn` — the fades of the two edges at that cut — are **not
  applied**: neither to the picture nor to the clip's sound (one pair per clip, PO-2; the sound is a hard cut, PO-5).
  In the rule of §2 that edge's effective length is 0 (`Fout = 0` for A, `Fin = 0` for B).
- The stored `FadeIn` / `FadeOut` are not reset: they stay in the model and in `project.json`. The Inspector shows such
  a fade as inactive, with its value.
- When the dissolve is removed (by the user or automatically, §5), the fade applies again at once — nothing to restore.
- Fades on free edges (A's fade in, B's fade out, an edge without a dissolve) work independently.
- A fade and the dissolve's opacity never multiply.
- Why: the dissolve already defines how the cut looks; a fade on the same edge would darken the picture further — an
  effect the user did not ask for.

Context: Step 10.1 audit — `Clip.Effects` / `Track.Transitions` existed only in the model and `project.json` (no anchor,
not rendered); the Preview and the export share `PlaybackSnapshot.LayersAt`, the `CompositionDrawPlan` and the painter,
and the audio `AudioMix` / `AudioPlacement` rule; readers are keyed by clip, so two clips of one track can be decoded
at once; the source-frame rule is exact outside a clip's range.

Consequences: `formatVersion` 3; the snapshot carries effective fades and dissolve zones; `AudioMix` gains a per-sample
envelope shared by the Preview and the export; the parity suite grows (new scenes) and is never weakened; edit
operations carry transition changes in their `EditPlan`, so every coupled change is one command.

Refined in Step 10.6 (2026-09-29), dissolve edits (implementation of §3–§5, no rule changed):
- `ITimelineEditService.AddTransition` / `RemoveTransition` / `SetTransitionDuration` (consecutive changes of one dissolve
  merge into one undo step) / `MaxTransitionFrames` (the longest the cut can take now: the largest `F` with
  `⌊F/2⌋ ≤ min(room in A, B's handle before)` and `⌈F/2⌉ ≤ min(room in B, A's handle after)`, room = the clip's frames
  not used by its dissolve on the other edge). A too long dissolve is rejected with the longest that fits, or "There is
  not enough media beyond the clips for the dissolve." when fewer than 2 frames fit.
- Every edit goes through one reconciliation (`EditPlan.ReconcileTransitions`): a dissolve whose clips were removed,
  landed on different tracks or no longer meet is removed in the same undo step, and the result carries the status note
  "A dissolve was removed: its clips no longer meet."; one whose two clips moved together to another track moves with
  them. Then the zones are validated against the planned clips, and the source handles only of the dissolves the edit
  creates, changes or whose clips it changes — an unrelated edit is never rejected because some other dissolve lost its
  handles (its media changed); rendering holds frames there (§4).
- A speed change that removes a dissolve is one composite step (it doesn't merge with the next speed change).
  Superseded in Step 10.8 (below): the removal is part of the clip's speed step, which merges with its next speed
  changes.

Refined in Step 10.7 (2026-09-29), dissolve composition (implementation of §3–§4, no rule changed):
- The snapshot carries each visible track's zones (`VideoLayer.Dissolves`: start, cut, end, F) and each picture's shown
  range (`PictureSpan.ExtendedStart` / `ExtendedEnd`); the timing anchor (`TimelineStart`, `SourceIn`) is unchanged, so
  the frames in a zone come from the handles by the D009 / D022 rule — the Preview's readers already decode outside a
  clip, the export's `ExportPictureReader` accepts the shown range. A dissolve is part of the timing: adding, changing
  or removing one reopens decoders.
- `LayersAt` in a zone: A (the clip covering the tick before the cut) below B (the clip at the cut), B's factor
  `(j + 1)/(F + 1)` times its own fade — only a fade on B's far edge can overlap a zone (a short B); the cut's edges
  have none (PO-8). The zone's start and end count as picture changes for the Preview's prefetch.
- A transition that doesn't sit on a cut of two clips of its track (impossible after validation) is not drawn.

Refined in Step 10.8 (2026-09-29), dissolve UI (no rule changed; every check stays in the edit service):
- **Dissolve** (timeline header) works on two selected clips; whether they meet on an unlocked video track, the zone
  and the handles are the service's (`MaxTransitionFrames`, `AddTransition`) and its message is shown when it refuses.
  Length: 1 s, or the longest that fits there when that is shorter (at least 2 frames) — the status bar says so;
  with fewer than 2 frames nothing is created ("There is not enough media beyond the clips for the dissolve.").
  Confirmed by the product owner at the 10.8 acceptance (2026-09-29; the alternative — refuse unless the full 1 s
  fits — not taken).
- The zone is drawn over the cut; clicking it selects the dissolve (no clip stays selected); Delete or the Inspector's
  Remove Dissolve removes it; the Inspector's DISSOLVE section edits the length in whole frames, range 2 … the longest
  that fits (a value outside is not applied, like every numeric field), with the time and "Longest that fits here".
  No keyboard shortcut to add one (out of scope).
- After the manual run: a zone never takes the press from a clip's trim handle (only a press on a clip body inside a
  zone selects the dissolve); a drag or trim preview shows the zones as the release will leave them; a speed change
  that removes a dissolve is part of the clip's speed step and merges with its next speed changes, so one Undo restores
  both; returning the clip to the speed that step started from (typed back, or the Speed field's own text undo) undoes
  the step, the dissolve included.
- Accepted with the real-app scenarios 13–20 (product owner, 2026-09-29), the length rule above confirmed.

Status: Accepted (2026-09-29; PO-8 the same day). Steps and acceptance criteria:
`docs/DEVELOPMENT_PLAN.md`, "Phase 10 — Transitions & basic effects: steps".

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
