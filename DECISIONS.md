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
  reopened. Relink is out of scope. *(Superseded by D026 §2–§3, Phase 11: re-check during the session and relink of
  missing media.)*

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
renders the same layers with the same Core rules offline and uses ffmpeg only to encode. Phase 13 (D028, Step 13.4): the
canvas is a user setting (default 1920 × 1080, never taken from a video); a canvas change scales the positions and text
sizes kept in canvas pixels — D028 "Refined at the start of Step 13.4".

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
  session is not re-checked (no relink, as in 9.3 — superseded by D026 §2, Phase 11); a Save As over a folder of an
  unrelated project leaves that project's thumbnail files there, never read; the generation check after a slot is obtained is defensive — no
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
  (or by reopening it); media coming back online during a session is not re-checked (superseded by D026 §2); while a
  trim of a clip's start is dragged the waveform follows the model, not the preview, until the edit; the waveform starts at the clip border's
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

Status: Accepted (2026-09-29; PO-8 the same day; the §2 fade cut 2026-09-30). Phase 10 accepted by the product owner on
2026-10-01. Steps and acceptance criteria:
`docs/DEVELOPMENT_PLAN.md`, "Phase 10 — Transitions & basic effects: steps".

---

## D026 — Phase 11: media availability, relink of missing media, recent projects

Date: 2026-10-01

Decision (product owner, 2026-10-01, after the Step 11.1 audit; PO-1…PO-9). Phase 11 has two features, implemented in
this order: the **media life cycle** — re-checking whether media files are there (Step 11.3) and **relinking** missing
media to a file the user picks, alone (11.4) or in a batch found next to it (11.5), with its UI (11.6) — then **recent
projects** (11.7 core, 11.8 UI). The relink keeps every `MediaAssetId` and the timeline as they are; nothing is adapted
to the new source.

Context (Step 11.1 audit, the code at `2e758f1`):
- D014: each media file is saved with its absolute path and its path relative to the project folder; Open takes the
  absolute path if the file exists, else the relative one, else keeps the absolute path and marks the asset missing
  (`ProjectSerializer.ResolveMediaPath`, `ProjectService.MarkMissingMedia` on Open and Recover). A project moved
  together with its media already opens without a relink.
- Missing (`MediaAsset.IsMissing`) is runtime state, set once per Open / Recover: a file that comes back stays offline
  until the project is reopened, a file that disappears during the session stays "online" until a decode or the export
  fails. `IProjectService.DetectMissingMedia()` exists but has no production caller and raises no event.
- Offline media: never probed (`MediaAnalysisCoordinator`), never decoded for thumbnails / waveforms (cached results
  only, `MediaCacheCoordinator.CanMake`, `SourceFileCache`), a "Media offline" placeholder in the Preview and the Media
  Browser, an export preflight error (`ExportPreflight`, which also checks `File.Exists` itself), not addable to the
  timeline (`TimelineEditService`).
- The thumbnail and waveform coordinators handle an asset once per project generation (by asset id); a generation
  starts only on `ProjectChanged`. The playback snapshot rebuilds when an asset's `FilePath` or `IsMissing` changes
  (`PlaybackSnapshotBuilder.AssetState`), so the Preview reopens its decoders by itself.
- `TimelineValidator` rejects a clip whose `SourceOut` is beyond its media's `Metadata.Duration`: a relink to a shorter
  file would make every later edit of such a clip fail.
- No settings are persisted per user; `AppPaths.ConfigFolder` (`%LOCALAPPDATA%\AiVideoEditor\config`) exists unused. The
  toolbar is a row of buttons (no menu bar, no context menus).

### 1. Scope and constraints

- Only missing (offline) media is relinked (PO-6). Replacing the file of an online asset is a separate, future feature.
- `project.json` stays `formatVersion` 3: a relink changes only fields that already exist (the media's absolute and
  relative path, `FileSizeBytes`, the saved metadata). The recent-projects list is not part of a project.
- Unchanged: D009 / D022 (source frame selection), D013 (mix), D018 / D023 (composition, export), D025 (fades,
  dissolves — handles are still not validated on load and never make rendering fail), D007 (a relink never changes the
  project frame rate and never re-grids), D008 (timeline rules), D014 path resolution on Open, D016 (autosave, recovery).
  L1-c stays open.
- Out of scope: replacing online media; fuzzy or recursive search; `FileSystemWatcher`; detecting that a present file
  was changed in place (size / time — the thumbnail / waveform cache key already ignores a stale entry); copying media
  into the project; a start screen; opening the last project at startup; a menu bar; a general UI redesign; hotkeys for
  the new commands.

### 2. Media availability re-check (PO-5, Step 11.3)

- A re-check sets `IsMissing` of every asset of the current project from the file system. The file system is read off
  the UI thread (a disconnected network or USB drive may take seconds); the result is applied on the UI thread to the
  project that was current when the check started — a result for a project that has been replaced is dropped. Checks do
  not overlap: a request during a running check is folded into one more check after it.
- Triggers: the main window becomes active (throttled — at most one check per interval; the interval is an
  implementation detail, recorded at 11.3), and, independently of that, before every operation that needs the files:
  the export (before its preflight; the preflight's own `File.Exists` stays as the last line) and the relink (§3). The
  check on Open / Recover stays as it is (before the project replaces the current one).
- A file that is gone now: the asset becomes offline (the Preview's placeholder, the export blocks, no analysis, no new
  thumbnail or waveform; cached ones may be shown — D024). A file that is back: the asset becomes online and its
  processing is restarted — analysis if it has no metadata (or needs the display-size refresh), thumbnail and waveform
  requested again for this asset in the current generation; the playback snapshot rebuilds by itself.
- `IsMissing` stays runtime state (D014): a re-check never makes the project dirty, never enters undo / redo, is never
  saved. An analysis of an asset whose state changed while it ran (offline now, or relinked / undone, §3) never writes
  its result onto the asset.

### 3. Relink (PO-1, PO-2, PO-3, PO-9, Step 11.4)

- Relink = the asset keeps its `Id`; its `FilePath` becomes the chosen file's full path, `FileSizeBytes` that file's size,
  and its metadata the new probe's (or none, below). Every clip keeps its `MediaAssetId`, `SourceIn` / `SourceOut`,
  speed, properties, fades and dissolves. The relative path follows on the next save (D014).
- Only an asset that is missing at the time of the relink (after the re-check, §2) can be relinked (PO-6).
- **Hard reject** (nothing changes, a message says why): the chosen file does not exist; its media type does not fit the
  asset (by the import's extension rules and, when probed, by its streams); for video / audio, the probed duration is
  shorter than the source range any clip of the asset uses (the `TimelineValidator` rule `SourceOut ≤ Duration`, every
  clip on every track); the path already belongs to another `MediaAsset` of the project (PO-9 — assets are never merged,
  no `MediaAssetId` changes).
- **Warning + confirmation** (the user may relink anyway): a different resolution, frame rate, no audio stream where the
  old metadata had one, other differing technical characteristics (codecs, sample rate, channels, rotation, start
  time — the exact list fixed at 11.4), and source handles too short for an existing dissolve of a clip of the asset
  (D025 §4: rendering holds the first / last frame). Characteristics are compared only when both the old and the new
  metadata exist; with no old metadata there is nothing to compare.
- **ffprobe unavailable** (PO-3): the relink is not blocked. A file that exists and passes the type check by its
  extension is relinked; the asset gets no metadata and the status `Pending` (analysed afterwards, as after an import);
  the user is told that compatibility could not be checked. What could not be checked is never turned into an
  irreversible error later. Settled at the start of 11.4: what a later analysis that finds the file incompatible
  (shorter than a clip's source range) does — the proposal is a status / log message, the relink kept and undoable;
  and how a probe that runs but fails on an existing file is treated (proposal: a hard reject as an unreadable media
  file, since its type can't be confirmed).
- **Undo** (PO-1): a relink is an `IUndoableCommand` executed through `IUndoRedoService` — the project becomes dirty
  through the save point (D015). Undo / Redo restore the asset consistently: `FilePath`, `FileSizeBytes`, `Metadata`,
  `AnalysisStatus`, `AnalysisError`, and `IsMissing` as it was when the command was made (corrected by the next
  re-check); the thumbnail / waveform of the asset are requested again and the playback snapshot follows; an analysis
  running for a state that undo / redo left is dropped (§2).
- No automatic adaptation of clips (no trim, no speed or frame-rate change, no re-grid, no removal of dissolves).

### 4. Batch relink (PO-4, Step 11.5)

- After a successful relink, the other missing assets are looked for **only in the folder of the chosen file**, without
  recursion, by **exact file name** (compared case-insensitively, as the Windows file system does).
- Every match goes through §3 (hard rejects are listed as not applicable, warnings are shown); a summary lists what was
  found, what can't be used and why; nothing is applied before the user confirms. Assets without a match stay offline.
- Undo granularity of the batch (one step for the confirmed batch, the first relink being its own step, is the
  proposal) and two missing assets with the same file name are settled at the start of 11.5.

### 5. Relink UI (Step 11.6)

- Offered only for offline media (PO-6), from the Media Browser; the picker is a single-file picker with the asset's
  kind filter, starting in the folder of the old path when that folder exists. Rejections and warnings use the
  existing dialog / status services; the `EditingLock` disables relink during an export. The exact placement (a
  Media Browser button enabled for a selected offline item is the proposal — the app has no context menus) and the
  wording of the Open status message pointing to it are settled at the start of 11.6.

### 6. Recent projects (PO-7, PO-8, Steps 11.7–11.8)

- A per-user list of at most **10** project folders, most recent first, no duplicates (full path, compared
  case-insensitively, trailing separator ignored), stored outside every project (`AppPaths.ConfigFolder`), written
  atomically; a damaged or unreadable list never prevents startup. Several running instances must not lose each other's
  entries (read again before every write). The file format and the store's location in the projects are
  implementation details recorded at 11.7.
- Added after a **successful** Open (also from the list itself), Save As (the new folder) and Recover of a project that
  has a folder. A failed Open adds nothing and changes nothing.
- Unavailable projects (no `project.json` in the folder now) are **never removed automatically**: they stay in the list,
  shown as unavailable, with a clear way to remove the entry. Availability is checked off the UI thread.
- UI: **`Recent ▾`** in the toolbar next to Open. Choosing a project goes through the unsaved-changes prompt and then
  the same open path as Open (D014, D016); a project that can't be opened leaves the current one and the list entry as
  they are, with a message. Disabled during an export like Open. Whether a "Clear list" item is added besides removing
  single entries is settled at the start of 11.8.

Consequences:
- D014 "detection happens once per Open … Relink is out of scope" is superseded by §2–§3 (the rest of D014 stands). The
  D024 Step 9.4 / 9.5 notes "media that comes back online during a session is not re-checked" are
  superseded by §2.
- The thumbnail / waveform coordinators and the analysis coordinator gain a per-asset restart within a generation
  (Impl at 11.3); `IProjectService.DetectMissingMedia` (no production caller, no event) is replaced or completed at 11.3.
- New code follows the existing layering: Core interfaces, implementations in Project / Media / Video, orchestration in
  UI services, no file system access from view models.

Refined in Step 11.3 (2026-10-01), media availability re-check (implementation choices within §2; no PO decision
changed):
- `IProjectService.RecheckMediaAsync()` (replaces `DetectMissingMedia`, which had no caller) and the event
  `MediaAvailabilityChanged` (`Returned`, `Gone`), raised before `MediaAssetsChanged`. `ProjectService` captures the
  project and each asset's path on the caller's thread, runs `File.Exists` for them in one `Task.Run`, and applies the
  result on the caller's context only if the same project is current and the asset still has that path; a file-system
  error counts as missing. One check at a time: a request while one runs is folded into one more check after it, and the
  returned task always covers a check that started after the call. Open / Recover use the same file check.
- Trigger on activation: `MediaAvailabilityMonitor` (UI service), `Interval` = **3 s**, measured from the start of the
  last check; an activation inside the interval schedules **one trailing check** at its end (later ones in the interval
  add nothing), so a file restored right after a check is seen without another window switch, while switching windows
  back and forth stats the files at most once per 3 s. Stopped when the window starts closing. Every change found is
  reported in the status bar (`"x" is missing now and is shown as offline.` / `"x" is available again.`, counts for
  several).
- The export re-checks before its preflight, unthrottled (`ExportWorkflow`); the relink will (11.4).
- Returned files: `MediaAnalysisCoordinator` analyses an asset that is `Pending` or `Failed` (its file may have gone
  while it was probed) and refreshes metadata without the display size; saved metadata is not probed again (an online
  file is not re-validated, PO-6). An analysis or orientation refresh whose asset is missing now or has another path when
  the probe ends writes nothing; a missing asset gets back the status and error it had when queued (so it is analysed
  when it returns), an asset with another path is left to whoever changed it (11.4).
- Thumbnails / waveforms: `MediaCacheCoordinator.Restart` — the returned assets are forgotten as handled in the current
  generation and requested again (made, not only read from the cache); the result shown while offline stays until the
  new one is ready; work started before the restart publishes nothing (each handling has an identity, checked before
  publishing). Gone files need no restart: what is shown may stay (D024), nothing new is decoded.
- The Preview needs nothing new: `IsMissing` is part of `PlaybackSnapshotBuilder.AssetState`, so a change rebuilds the
  snapshot (placeholder ⇄ picture, decoders reopened); the Media Browser rebuilds its rows on `MediaAssetsChanged`; the
  export preflight reads `IsMissing` and its own `File.Exists`.

Refined in Step 11.4 (2026-10-01), relink core — the three sub-decisions left by §3, confirmed by the product owner at the
start of 11.4, and the implementation:
- Compared characteristics (warnings, only when the old and the new metadata both exist): display size, frame rate,
  the audio stream gone, rotation, start time, video / audio codec, sample rate, channels; dissolve source handles too
  short for an existing dissolve (computed with the new metadata, whatever the old). Duration is no warning: shorter than
  the largest `SourceOut` of the asset's video / audio clips (any track, any speed) is a hard reject, equal or longer is
  accepted silently.
- ffprobe available but the probe fails (`InvalidMedia`, `UnsupportedMedia`, `ProbeProcessFailed`, `InvalidOutput`):
  a hard reject ("can't be read as media"). Strictly apart from `ProbeToolUnavailable` (PO-3): allowed after the
  existence and extension checks, with the `NotChecked` warning.
- A later analysis that finds a file incompatible with its clips (shorter than they use, or without the stream of its
  kind — e.g. relinked without ffprobe, analysed at a later Open): a status-bar message and a log warning, nothing undone
  or adapted; the user may Undo. Raised for any analysis whose metadata doesn't fit (a new metadata object is looked at
  once).
- Without ffprobe the relinked asset keeps no metadata (not the old file's) and stays `Pending`; it is not queued for
  analysis in this run (the ffprobe location is fixed for the app run — a probe would only fail), so it is analysed at the
  next Open. Until then the existing rules stand: `TimelineValidator` refuses edits of its track ("duration … unknown"),
  the export preflight reports it as not analysed — said in the warning.
- Implementation: Core `IMediaRelinkService` (`CheckAsync` → `RelinkCheck` with `Rejection` / `Warnings` / `Metadata`,
  `ApplyAsync` → `RelinkResult`, event `RelinkedMediaFoundIncompatible`), `MediaFileTypes` (the extension → kind table,
  shared with the import); Timeline `MediaRelinkService` (next to the edit service: the length and handle rules are the
  timeline's — `TimelineValidator`, `DissolveHandles`) and `RelinkMediaCommand` (absolute `MediaFileState` before / after
  per asset — path, size, metadata, analysis status and error, missing — so a batch is one command, 11.5).
  `CheckAsync` re-checks the media first (PO-5), `ApplyAsync` again and validates what may have changed since the check
  (another project, the asset online again, the path taken, the file gone or another size, a clip lengthened). The
  command notifies `IProjectService.NotifyMediaRelinked` on Execute and Undo → `MediaRelinked` (then
  `MediaAssetsChanged`): `MediaCacheCoordinator.Restart(…, dropResults: true)` drops the old file's thumbnail / waveform at
  once and requests the new one; `TimelineViewModel` refreshes the clip waveforms on `MediaAssetsChanged` (a file without
  sound loses its waveform); the Preview rebuilds through `AssetState.FilePath`; analyses for a path the asset no longer
  has are dropped (Step 11.3). The project frame rate is never touched (D007).

Refined in Step 11.5 (2026-10-01), batch relink search (PO-4 as restated by the product owner at the start of 11.5; the
§4 items left to this step):
- Scope: one folder the user gives (the relink UI of 11.6 decides how it is chosen — e.g. the folder of the file just
  relinked); only the files directly in it; every offline item's file name looked for exactly, compared
  case-insensitively as the Windows file system does; no fuzzy or similar-name matching, no recursion. Items without a
  match stay offline.
- Two or more offline items with the same file name: the file is given to **none** of them (no automatic choice); they
  are listed as ambiguous ("relink them one by one"). Online items don't count — a file that belongs to another item is
  a `PathInUse` reject of the relink check.
- No second set of rules: every match goes through the relink check of 11.4 (`CheckCoreAsync`, the same code
  `CheckAsync` runs after its re-check), so type, length, path in use, probe and warnings are exactly a single relink's.
  The media are re-checked once per search (PO-5); the folder is listed off the UI thread.
- Nothing is applied by the search. Its result (`RelinkSearch`: an entry per offline item — found, rejected with the
  check's reason, not found, ambiguous — and `Summary()`: found items with their warnings, the unusable ones with why,
  those not in the folder) is shown for confirmation; `ApplyAllAsync(search.Applicable)` applies the confirmed items as
  **one** undoable step (one `RelinkMediaCommand`; a single relink is the same path with one item). Declining the summary
  or cancelling the search changes nothing.
- Apply validates every item again, as a single relink, and also within the batch (a file for one item only, an item
  once). Items that fail now (file gone or changed, path taken, the old file back, a clip lengthened, another project)
  are returned with their reason and stay offline; the others are still applied as one step — nothing is applied that
  the user didn't confirm, nothing invalid is applied, and every refusal is reported.

Refined in Step 11.6 (2026-10-01), relink UI (the §5 items left to this step, confirmed by the product owner with the
11.6 plan):
- Placement: a second row of the Media Browser's header, shown only while some media is offline ("OFFLINE" — Relink… ·
  Find Missing…); the app has no context menus and the panel is too narrow for four buttons in one row. Relink… is
  enabled for a selected offline item, Find Missing… while any media is offline; both are disabled during an export
  (`EditingLock`) and while a relink workflow runs.
- Relink…: a single-file picker with the item's kind (the import's extensions), starting in its old folder when that is
  there (checked off the UI thread) → the relink check → a rejection: "Can't relink" with the service's reason, nothing
  applied → warnings: "Relink with differences?" (Relink Anyway / Cancel) → Apply → a refusal at Apply (the file gone or
  changed, the item online again, the path taken, another project): "Relink not applied" with the reason. Only after a
  relink that was **applied**, and only while other media are offline, "Find other missing media?" offers the search in
  the chosen file's folder (Search / Not Now) — never after a cancel, a rejection or a refused Apply.
- Find Missing…: a folder picker (starting in the first offline item's folder when that is there) → the search → its
  `Summary()` (found with their warnings, "Can't be used:" with the reason — ambiguous names included —, "Not in the folder
  (stay offline):") with "Relink N Files" / Cancel, or OK alone when nothing can be applied → `ApplyAllAsync` (one step) →
  items refused at Apply: "Relinked X of Y" with each reason.
- The `EditingLock` is never taken by the relink (it is the export's); it is looked at before the workflow starts, after
  each await (the picker, the check, the search) and right before applying — an export started meanwhile stops the
  relink ("An export is running — nothing was relinked."). One workflow at a time.
- The UI keeps no relink state: the Media Browser rebuilds on `MediaAssetsChanged` and keeps its selection by asset id
  (no longer by path, which a relink changes); thumbnails, waveforms and the Preview follow the 11.4 events; Undo / Redo
  are the toolbar's and Ctrl+Z / Ctrl+Y. A `Pending` row reads "Not analysed yet" (a relink without ffprobe). Long dialog
  text scrolls. The Open / Recover message about missing media ends with "— use Relink or Find Missing in the Media
  Browser."

Refined after the Step 11.6 manual run (2026-10-02; defects D1–D5 found in the real app, fixes ordered by the product
owner):
- D1: the Inspector's state line of a media item follows the Media Browser's row — "Media offline" first, then
  "Analyzing…" only while an analysis runs, "Not analysed yet" for `Pending` (a relink without ffprobe), the error for a
  failed analysis. It no longer showed "Analyzing…" for every `Pending` and for offline media.
- D2: after an Undo of a relink (single or batch) an offline item showed the thumbnail / waveform made from the file it
  had been relinked to: the cache keeps one file per asset (the older is deleted when a new one is written) and offline
  media takes the last one (D024). `IProjectService.MediaRelinked` now carries, per asset, the path it had before
  (`MediaFileReplacement` — the minimal extension needed: the coordinators can't know which file an asset leaves);
  `MediaCacheCoordinator` keeps, for the project generation, what was shown for each (asset, file) — a result or none, once
  it had settled — and shows it again when an Undo / Redo returns the asset to that file, without reading the cache. Work
  for a replaced file still publishes nothing. Media offline for any other reason follows D024 unchanged. Limitation:
  kept in memory only — after an Undo, a save and a reopen, the offline item gets the cache's last file again (D024).
- D3: "is an audio file" (both kinds named as files). D4: without ffprobe the confirmation says the technical
  compatibility "was not checked" ("Relink without a compatibility check?"), never that the file differs. D5: a search
  whose matches all can't be used reports "Files with matching names were found in the folder, but none of them can be
  used." — "No offline media file was found in the folder." only when nothing matched.

Refined in Step 11.7 (2026-10-02), recent projects core (implementation choices within §6, agreed with the product
owner after the Step 11.7 audit; no PO decision changed):
- `IRecentProjectsStore` (Core: `GetAsync`, `AddAsync`, `RemoveAsync`, `IsAvailableAsync`; `RecentProject(FolderPath,
  Name, LastUsedAt)`), implemented by `RecentProjectsStore` (Project / Persistence), registered in the app's composition
  root, given to `ProjectFileWorkflow` as its last optional constructor parameter.
- File `%LOCALAPPDATA%\AiVideoEditor\config\recent-projects.json` (`AppPaths.RecentProjectsFile`; the folder is created
  only when the list is written — `AppPaths.ConfigFolder` creates it on every read, so it isn't used at startup):
  `{ "format": "AiVideoEditor.RecentProjects", "formatVersion": 1, "projects": [ { "folderPath", "name", "lastUsedAt" } ] }`,
  camelCase, most recent first. Availability is runtime state, never stored.
- An entry's key is the folder's full path without a trailing separator (`GetFullPath`), compared ignoring case. Adding a
  listed folder moves it first with the new name, time and path spelling; the 11th entry and beyond are dropped. A moved
  or renamed folder is a new path; the old entry stays (unavailable). Accepted limitation: one folder reached by different
  paths (a UNC path and a drive letter, `subst`, a symbolic link, an 8.3 name) gives different entries.
- Added only by `ProjectFileWorkflow`, after the operation succeeded: `OpenAsync(folder)` (the opened project's folder and
  name — also the future open from the list), `SaveAsAsync` (the written folder, named after it — also Save As into the
  same folder and the first Save of a new project), the recovery offer's Recover when the recovered project has a folder
  (also when that folder is gone). Save, New, Close, a cancelled picker or question and a failed operation change
  nothing. `ProjectChanged` / `ProjectSaved` are not used: they don't tell Open from Recover or Save from Save As.
- Errors never fail the project operation and show nothing in the status bar (logged only). Reading: no file → empty; not
  JSON, not this `format`, no valid `formatVersion` or `projects` → empty, the file set aside as `*.<time>.damaged`;
  single bad entries (no absolute path, no valid time) dropped, duplicates and entries beyond 10 removed (the file is
  written clean at the next change); a newer `formatVersion` → empty, and changes are refused so it is never overwritten;
  a file that can't be read → empty, and changes are refused. Writing: `ProjectFileStore.WriteAtomicAsync` (temporary
  file, flush, replace) — a failed write leaves the previous file and no temporary file.
- Several instances: every change reads the file again and writes it under a lock shared by all instances
  (`recent-projects.lock` next to the list, opened with `FileShare.None`) and an in-process semaphore; a lock not obtained
  within about 2 s skips the change (logged), the list unchanged. A read takes the lock too, so a damaged file is set aside
  only if no other instance replaced it meanwhile; without the lock it is still read, nothing set aside.
- All file work runs off the calling thread. `IsAvailableAsync` (a `project.json` in the folder; any error = unavailable)
  has no time limit — a disconnected network path may answer late; the UI of 11.8 shows "checking" until then — and
  never takes the lock, so it never holds up a change of the list.

Refined in Step 11.8 (2026-10-02), recent projects UI (implementation choices within §6; the product owner left the
"Clear list" question to the minimal safe option):
- `Recent ▾` is a `DropDownButton` right after Open in the toolbar, with a flyout of fixed width (400, inside the Fluent
  flyout's maximum): a header, an empty state ("No recent projects yet. Projects you open or save appear here."), and per
  entry an open button (the project's name, its folder below in smaller grey type — trimmed at the start, so the
  folder's own name stays visible — and the state on the right) followed by a ✕ remove button. Long names and folders
  are trimmed with an ellipsis; the full folder is in the tooltip. Disabled while the `EditingLock` is held (export).
- No "Clear list": with at most 10 entries, each removable with one click, a command that empties the whole list adds a
  confirmation, a partial-failure state and an accidental-loss risk for no real gain; D026 requires only a clear way to
  remove an entry. It can be added later without changing the store.
- Every opening of the drop-down reads the list again (`GetAsync`) — nothing is cached from startup — and checks each
  entry with `IsAvailableAsync` (started off the UI thread): **Checking** (not openable) → **Available** (openable) or
  **Unavailable** (not openable; a check that fails counts as unavailable). Removing works in every state. No time limit
  (Step 11.7): an entry on a disconnected drive stays Checking until its check answers, without holding up the UI, the
  other entries or changes of the list.
- Stale results: every reading makes new entry objects; a check's result only reaches the object it was started for, and
  an object replaced by a later reading or removed can't open or remove anything. A reading overtaken by a later one, or
  started before a removal, is dropped — a removed entry never comes back. One check per project at a time: an opening
  while a project's check runs waits for that check instead of starting another.
- Choosing an available entry closes the drop-down and calls `ProjectFileWorkflow.OpenFolderAsync` — the part of Open
  after the folder picker (the unsaved-changes question, `OpenAsync`, the recovery file of discarded changes), now shared
  by Open and the list. The list is updated only by the workflow after a successful open (Step 11.7); a cancel or a
  failure keeps the current project, its unsaved changes and the entry, with the workflow's message. One action at a
  time: while an entry is opened or removed, no entry can be opened or removed. If the drop-down was opened again
  meanwhile, the list is read again when the open ends.
- ✕ calls `RemoveAsync`; the entry disappears only once the store removed it. A removal that fails (false or an
  exception) is reported in the status bar ("Couldn't remove "X" from the recent projects.") and the list is read again.
  The project's folder is never touched.

Refined in Step 11.9 (2026-10-05), final verification & closeout (no PO decision changed, no code changed):
- Decided by the product owner for the closeout: CI is not part of the local closeout — the branch is not published, so
  CI hasn't run (pending until the branch is published); the manual run is variant (b) of the Step 11.9 audit — the
  scenarios not yet run in the real app (23 through Open, 24, 27, 28), R1, R2 (`docs/EXPORT_MANUAL_TEST_PLAN.md`) and a
  short regression of relink and recent projects; the scenarios checked in the real app during 11.3–11.8 count with
  their recorded results and were not re-run.
- Every manual check ran with an isolated profile: the test build was started with `USERPROFILE` / `LOCALAPPDATA`
  pointing to a scratch folder, which .NET's `LocalApplicationData` (and so `AppPaths.AppDataRoot`) follows — the
  configuration, recovery files, caches and logs of the run never touched the user's profile, no code or configuration
  change needed.
- Found: the Windows folder picker returns a picked folder in its canonical spelling, so through Open a folder can't
  reach the app spelled in another case; the case-insensitive key (Step 11.7) matters for paths that arrive as typed —
  recovery files, the Debug `--open-project` argument, lists from another machine — and was checked with the latter.
  Not a defect.
- No Phase 11 defect found in the closeout (results: `progress.md`, `docs/PHASE11_MANUAL_TEST_PLAN.md`).

Status: Accepted (2026-10-01, PO-1…PO-9). Steps and acceptance criteria: `docs/DEVELOPMENT_PLAN.md`, "Phase 11 — Media
relink & recent projects: steps"; the implementation must follow PO-1…PO-9 as recorded here.

---

## D027 — Phase 12: editing essentials

Date: 2026-10-05

Decision (product owner, 2026-10-05, after the Step 12.1 audit). Phase 12 adds the everyday editing operations the
timeline and the media list still lack, in this order: **track management** (delete, reorder; Step 12.3), **removing
media from the project** (12.4), **ripple delete** (12.5), **copy / paste / duplicate of clips** (12.6), **markers**
(12.7), and the fix of **New during a running import** (12.8). Every new project change is one undoable command; the
rendering rules are unchanged, so the Preview and the export stay identical by construction.

Context (Step 12.1 audit, the code at `47ed2fa`, `main` after the merge of PR #11):
- `ITimelineEditService` has add / move / trim / split / delete clips, add track, clip properties, speed and the
  dissolve edits; there is no track removal or reordering, no ripple, no clipboard. `IProjectService` has no way to
  remove a media asset.
- Tracks: `Sequence.VideoTracks` / `AudioTracks`, each `Track` with an `Order` (persisted). The playback snapshot draws
  video tracks by `Order` (higher on top, `PlaybackSnapshotBuilder`), the timeline lists video tracks by descending and
  audio tracks by ascending `Order`; a new track gets `max(Order) + 1` and the first free name `V<n>` / `A<n>`; a clip
  added without a track goes to the first track of its kind in the list.
- Markers: `Sequence.Markers` (`Marker`: `Id`, `Position` as `MediaTime`, `Label`, `ColorHex`) is already written and
  read by `project.json` v3 (`MarkerDto`); nothing creates, shows or uses one.
- Dissolves (D025): anchored to `LeftClipId` / `RightClipId`, valid only while the two clips meet on one video track;
  every edit goes through `EditPlan.ReconcileTransitions` (a dissolve whose clips were removed or no longer meet is
  removed in the same undo step with a status note; one whose two clips moved together moves with them); deleting A or
  B removes the dissolve (D025 §5); handles do not depend on the timeline position.
- Import: `MediaImportWorkflow.RunAsync` picks files, reports "Importing N files…", yields once to the dispatcher so
  the status is drawn, runs `IMediaImportService.ImportManyAsync` (synchronous, on the UI thread) and adds the result
  with `IProjectService.AddMediaAssets` to whatever project is current then. A New (or Open) that runs during the yield
  replaces the project, so the picked files land in the new one (known issue since Step 9.3, D024 "Left as they are").
- Cache: thumbnails and waveforms are kept per asset id (`MediaCacheCoordinator`, D024 9.4 / 9.5); there is no cache
  eviction.

### 1. Scope and constraints

- In scope: 12.3–12.8 as below.
- `project.json` stays `formatVersion` **3**: tracks (`Order`), clips, dissolves and markers already have every field
  Phase 12 needs. A step that finds it needs a new field stops and asks the product owner before changing the format.
- Unchanged: D007 / D008 (no overlap on a track, the frame grid), D009 / D022 (source frame selection), D013 (mix),
  D014 / D016 (persistence, autosave, recovery), D018 / D023 (composition, export), D025 (fades, dissolves — applied as
  written, §2 below), D026. The Preview ↔ Export parity suite is never weakened, re-baselined or removed. L1-c stays
  open (product owner, 2026-10-05).
- Every new command is disabled while an export runs (`EditingLock`) and rejected on a locked track, like the existing
  edits.
- Out of scope: AI features, export settings, HDR / colour management, an installer, timeline virtualization, an
  undoable import, ripple trim, ripple on other tracks than the edited one, a time-range (in / out) selection, snapping
  to markers unless it is simple (§6), automatic creation of dissolves, a system-wide clipboard, a menu bar, a UI
  redesign.

### 2. Ripple delete (Step 12.5)

- Ripple delete removes the selected clips; on each track that loses a clip, every remaining clip that starts at or
  after the end of a removed clip moves left by the total length of the removed clips that end at or before its start.
  Clips on other tracks, the playhead and the markers do not move. Gaps that are not removed stay (they move with the
  clips after them).
- Close gap: the empty span between two clips of one track (or before the first clip) is removed — every clip of that
  track from the gap's end on moves left by the gap's length.
- No overlap can arise: a clip right of a removed span moves left by at most the removed length, so it ends up at or
  after the end of the clip left of that span (D008 holds without clamping). The validator still checks the result.
- One undoable step per ripple delete / close gap; Undo restores every clip, position and dissolve exactly.
- **Dissolves** (the rule follows from D025 §5 and `ReconcileTransitions`; no new rule):
  1. A dissolve that has a removed clip as A or B is removed in the same undo step, with D025's status note — exactly
     as a plain delete does (D025 §5 "Delete A or B: removed").
  2. Every other dissolve keeps its clips, length and zone: its two clips meet (`A.end == B.start`), so no removed clip
     or gap lies between them and both move by the same distance — the case of D025 §5 "moving both A and B by the
     same delta keeps the dissolve". A move changes neither the clips' lengths nor their source ranges or speed, so the
     zone fit and the handles are unchanged; `ReconcileTransitions` and the zone validation still run on the result.
  3. Clips that meet only because of the ripple (the clip before and the clip after a removed clip or gap) get **no**
     dissolve: D025 never creates one automatically.
  4. Fades are untouched (a ripple changes no clip length).
  - Considered and not taken, because they contradict D025: joining the clips around a removed clip with a new dissolve
    (an automatic creation; the handles of those clips were never checked), and refusing a ripple delete of a clip
    that has a dissolve (D025 removes the dissolve of a deleted clip).
  - Result: after a ripple, every dissolve is either unchanged in length, zone and handles, or removed; an invalid
    `Transition` cannot arise, and the Preview and the export read the same snapshot as before.
- Left to the start of 12.5: how the command is reached (a button / the timeline's Delete with a modifier) and how a
  gap is chosen.

### 3. Tracks (Step 12.3)

- Delete a track: a track with clips is deleted only after a confirmation; its clips and their dissolves go with it.
  One undoable step; Undo restores the track at its place in the list with its `Order`, name, flags, clips and
  dissolves.
- Reorder: a track moves up or down among the tracks of its kind (video among video, audio among audio); `Order` and the
  list follow, so the timeline, the Preview and the export use the same order (video: higher `Order` on top). One
  undoable step.
- Left to the start of 12.3: whether the last video / audio track can be deleted, whether a locked track can be deleted
  or moved, the names of the remaining tracks (kept or renumbered), and how the commands are reached.

### 4. Removing media from the project (Step 12.4)

- An unused asset is removed at once; an asset used by clips is removed only after a confirmation that names how many
  clips go with it — then the asset, every clip that uses it and their dissolves are removed in one undoable step.
  Undo restores the asset (same id, path, metadata, analysis state), the clips and the dissolves exactly.
- The user's file on disk is never deleted. Offline assets can be removed like online ones.
- Thumbnails / waveforms stay consistent: a removed asset's results are no longer shown (it has no row and no clip);
  work still running for it ends normally and keeps its result; after an Undo it is shown again at once, without being
  made anew. The cache files are not deleted by the removal (product owner, 2026-10-05).
- Analysis (product owner, 2026-10-05, after Step 12.4): an analysis already running when its asset is removed is not
  cancelled; it may end after the removal and writes its result into the asset object; while the asset is out of the
  project the result is simply not shown; an Undo brings the asset back with its current analysis state.
- Left to the start of 12.4: where the command sits (Media Browser), several assets at once, and whether cache files of
  removed assets are cleaned up later (no eviction exists today).

### 5. Copy / paste / duplicate (Step 12.6)

- Copy keeps the selected clips with their timing (length, source range, speed) and properties (volume / mute,
  opacity, transform, crop, text, fades). A dissolve is never copied.
- Paste puts the copied clips at the playhead: the earliest copied clip starts at the playhead and the others keep
  their distances to it; each clip goes to the track it was copied from when it can. If a clip would overlap another
  clip or break a rule (a locked track, a track that no longer exists, a media asset that is missing from the project),
  the whole paste is rejected with a message — nothing is pasted partially, nothing overlaps.
- Duplicate copies and pastes the selection in one command (placement left to 12.6).
- Every paste and duplicate is one undoable step; pasted clips get new ids.
- Hotkeys are optional: added only if `ShortcutRouter` takes them without a structural change (decided at 12.6).
- Left to the start of 12.6: the clipboard's life (cleared on New / Open), where a duplicate goes, a track that no
  longer exists (rejected or another track of its kind), an offline asset, and what is selected after a paste.

### 6. Markers (Step 12.7)

- The existing `Sequence.Markers` model gets its UI: add a marker (at the playhead), remove a marker, markers drawn on
  the timeline, go to the next / previous marker. Adding and removing are undoable project changes (saved in v3);
  moving the playhead to a marker is not.
- A ripple does not move markers (§2). Snapping to markers only if it fits the existing snapping without a structural
  change — otherwise it goes to the backlog (decided at 12.7).
- Left to the start of 12.7: labels / colours (editing them is not in the minimal scope), two markers at one position,
  hotkeys.

### 7. New during a running import (Step 12.8)

- The import belongs to the project it was started in. `MediaImportWorkflow` takes the current project before the file
  picker opens and checks after every await (the status yield, `ImportManyAsync`) that it is still the current one
  (`ReferenceEquals`, the pattern of the relink and of the re-check, D026). If the project was replaced (New, Open,
  Recover), nothing is added, no analysis is queued, and the status bar says the import was dropped because another
  project was opened (text at 12.8).
- Considered and not taken: cancelling the import through a token on `ProjectChanged` (more plumbing, and the check of
  the picked files is synchronous today, so a token cannot interrupt it — the identity check is needed anyway);
  blocking New / Open while an import runs (a user-visible change; `EditingLock` is the export's lock); adding the files
  to the old project (it is no longer current and was not saved with them).
- An undoable import stays out of scope (product owner, 2026-10-05).

Consequences: new commands in the Timeline subsystem (`ITimelineEditService` grows track, ripple, paste and marker
operations) and a media removal through the project / timeline services, each with its `EditPlan` reconciliation; the
UI gains commands in the timeline header, the track headers and the Media Browser; `project.json` unchanged (v3);
`docs/PHASE12_MANUAL_TEST_PLAN.md` holds the real-app scenarios.

Confirmed at the Step 12.2 acceptance (product owner, 2026-10-05):
- Ripple (§2) applies to the selected clips, also on several tracks; close gap works on an existing empty span of one
  track; no ripple of an arbitrary in / out range in Phase 12.
- The dissolve rule of §2 as written: a removed clip's dissolve is removed; the others are kept with their geometry
  unchanged; none is created automatically; fades unchanged.
- §7 as written: the workflow keeps the project the import started in, checks it after each await, adds nothing to
  another project and says so; no New / Open lock and no cancellation token in Phase 12.

Refined at the start of Step 12.3 (product owner, 2026-10-05) and in its implementation:
- The last track of the timeline can't be deleted: at least one track (of either kind) always stays. A timeline left
  without a track of one kind refuses media of that kind with the existing message ("The timeline has no audio
  track.") until one is added.
- A locked track is neither deleted nor moved; a move that would take an unlocked track past a locked neighbour is
  refused too (it would change the locked track's place). Refused edits change nothing and leave no Undo step.
- No renaming (no UI); names stay as they are (a new track still takes the first free `V<n>` / `A<n>`).
- A move changes `Track.Order` only — the lists, the clips, their timing and the dissolves stay. It swaps the two
  tracks' orders; when they are equal (a file from elsewhere), the tracks of that kind are numbered 0, 1, … in the new
  order (`SetTrackOrderCommand`, absolute before / after values). Neighbours are taken in the order the playback
  snapshot composites (`Order`, equal orders by their place in the list); the timeline now lists video tracks by the
  same rule (before, equal orders were listed the other way round from how they were drawn). Audio tracks are listed
  by order; their mix does not depend on it.
- Delete: `RemoveTrackCommand` takes the track object out of its list; Undo puts the same object back at the same index,
  so its order, name, flags, clips, fades and dissolves are restored exactly. `GetDeleteTrackBlockReason` lets the UI
  skip the confirmation when the service would refuse.
- UI: ▲ / ▼ / ✕ in each track header (the header column 84 px instead of 70). ▲ / ▼ follow the timeline as shown —
  video top layer first (▲ = a higher order), audio by order (▲ = a lower one) — and are enabled only next to a track
  of the same kind; ✕ asks "Delete Track" / "Cancel" when the track has clips (naming their number); all disabled
  during an export, and the answer is ignored when an export started meanwhile.
- `formatVersion` stays 3.

Refined in Step 12.4 (implementation, 2026-10-05; confirmed by the product owner the same day — one asset, ✕ on the
row, no cache cleanup, the analysis rule, the re-import edge as a known Phase 12 limitation):
- One asset at a time (the Media Browser selects one): ✕ on the selected row — the header has no room for a third
  button at the panel's 260 px. An unused asset goes at once; a used one after "Remove" / "Cancel" naming the number of
  clips and that the file on disk stays. A clip of the asset on a locked track blocks the removal ("… is used on track
  A1, which is locked."), asked about never; disabled during an export and while a relink runs.
- The edit: `ITimelineEditService.RemoveMedia` (with `CountClipsUsing`, `GetRemoveMediaBlockReason`) — the clips through
  an `EditPlan` (their dissolves by `ReconcileTransitions`, D025's status note), then `RemoveMediaAssetCommand`, one
  `CompositeCommand`; Undo puts the same asset object back at its index, so id, path, size, metadata, analysis state
  and the offline flag come back unchanged. An unused asset's removal touches no timeline (no `TimelineChanged`).
- Thumbnails / waveforms (§4 made precise): the coordinators keep their results per asset id for the project's
  generation, so a removed asset's result is simply not shown (it has no row and no clip) and Undo shows it again at
  once, without making it anew; work still running when the asset is removed ends normally and its result is there
  after an Undo. No cache file is deleted (no eviction exists, D024).
- Analysis (§4 above, confirmed by the product owner 2026-10-05): an analysis still running when its asset is removed
  ends normally and writes its result into the removed asset, so an Undo brings it back analysed. Dropping it would
  leave an Undo-restored asset "Analyzing" with no analysis running (nothing queues one on Undo).
- Known edge, not handled: after a removal, importing the same file again makes a new asset (a removed asset is no
  longer in the list the import checks); an Undo of the removal after that brings back a second asset with the same
  path (the import itself is not undoable, D027 §1). Both play; relinking either to the other's path is refused (PO-9).

Refined in Step 12.5 (product owner's rules of 2026-10-05 at its start, and the implementation; confirmed by the
product owner the same day):
- Ripple delete works on the selected clips only (also on several tracks), never on an in / out range; each track that
  loses a clip closes by its own removed clips; clips of other tracks, the playhead and the markers stay. The shift is
  computed in whole frames (the move's timing rule, shared with Move: frame count, speed and source range go along),
  so the clips stay on the frame grid at any rate.
- Close gap works only on an existing empty span of one track (between two clips or before the first one); a point in a
  clip or after the track's last clip is no gap. Reached from the UI as "the gap right before the one selected clip"
  (`CloseGapBefore`); the service's general form is `CloseGap(track, at)`.
- Dissolves as §2 (confirmed at 12.2); the moved dissolves are validated like any edit's — their zones and, as D025
  "Refined in Step 10.6" says for every dissolve whose clips an edit changes, their source handles. A move changes
  neither, so a valid dissolve stays valid; one whose media changed since (a relink to a shorter file) would make the
  ripple refuse with the validator's message rather than leave an invalid dissolve.
- UI: "Ripple Delete" (the selected clips; the selection cleared) and "Close Gap" (exactly one selected clip; the
  selection kept) in the timeline header next to Delete; disabled during an export; no hotkeys.

Refined in Step 12.6 (implementation, 2026-10-05; rules 1–7 of the step's report confirmed by the product owner the same
day):
- Copy takes detached copies of the selected clips (`ITimelineEditService.CopyClips` → `TimelineClipboard`): timing,
  speed, source range, every property, text, fades, the media asset they refer to (never a file), the track each came
  from and the project frame rate; no dissolve. It is no project change (no Undo step, not dirty) and is allowed from a
  locked track and during an export. Later edits of the clips don't change what was copied.
- Paste (`PasteClips`): new clips with new ids, the earliest at the playhead (snapped to the frame grid), the others at
  their copied distances, each on the track it was copied from; the pasted clips become the selection. Rejected as a
  whole — nothing pasted, no Undo step — when a clip would overlap another (the timeline's validation), its track no
  longer exists or is locked, its media is no longer in the project (removed after the copy — an Undo of the removal
  makes the paste work again), or the project frame rate changed since the copy (the first video fixed it): "Copy them
  again". No other track is tried.
- Offline media after the copy: pasted like any other clip — the copy refers to the same asset, as its original does;
  it is shown and exported (the export preflight blocks offline media) exactly like the original, and a relink brings
  both back. Unlike Add to Timeline, which refuses offline media because nothing of it is on the timeline yet.
- Duplicate (`DuplicateClips`): copy and paste in one step, the earliest copy starting where the last selected clip
  ends, every copy on its clip's track; rejected like Paste (also when a copy would overlap the next clip).
- The clipboard is the timeline panel's session state for the current project: emptied on New / Open / Recover (its
  media and tracks belong to that project); not the system clipboard; not saved.
- The move's timing rule (`PlanShift`) now computes the state in `ShiftedState`, used for pasted copies too (same
  frame count, speed and source range at the new place).
- UI: "Paste" and "Duplicate" in the timeline header; Copy has no button — Ctrl+C only (the real-app check at the
  minimum window width, 1024 px: with a Copy button the header overflowed — Fit cut at the right edge, no gap after
  the frame rate — so, as the product owner had decided for that case, only the Copy button was removed). Ctrl+C /
  Ctrl+V / Ctrl+D through `ShortcutRouter` (three rows of its table, no structural change; never while a text input has
  focus, so a text box keeps its own Ctrl+C / Ctrl+V). Paste and Duplicate are disabled during an export.

Refined in Step 12.7 (implementation, 2026-10-05; rules 1–6 of the step's report confirmed by the product owner the same
day):
- Add Marker puts a marker at the playhead, on the frame grid, with the model's default label (empty) and colour
  (`#4FC3F7`); one marker per frame — a second one on the same frame is refused ("A marker is already there.").
  `Sequence.Markers` is kept sorted by position. One Undo step each (`AddMarkerCommand`, `RemoveMarkerCommand`, the same
  object back on Undo / Redo); the project becomes dirty; v3 unchanged (`MarkerDto` existed).
- Remove Marker removes the marker on the playhead's frame — the way to choose one is to go to it; none there is
  refused ("There is no marker at the playhead.").
- Previous / Next go to the nearest marker strictly before / after the playhead's frame (a seek, like any user move of
  the playhead); none is a status message. Not a project change, so also available during an export.
- Markers keep their time when the frame rate changes (like fades and dissolves, D025); "on a frame" means the
  nearest frame of the current grid.
- Snapping to markers is included: one more kind of target in the existing snap list (`ITimelineEditService.Snap`), no
  structural change.
- A ripple, a move or any other edit never moves a marker (D027 §2); markers never lengthen the timeline.
- Not in Phase 12: editing a label or a colour, moving a marker by dragging, clicking a marker, hotkeys.
- UI: four small buttons in a compact block in the corner left of the ruler (◀ previous, ◆+ add, ◆− remove, ▶ next) —
  the timeline header has no room left at 1024 px (Step 12.6); the track header column stays 84 px. The real-app check
  found the block wider than the column (▶ partly under the ruler, at any window width); as the product owner chose
  (variant A), only the block's button padding (3,0 → 1,0) and spacing (2 → 1) were reduced — now all four fit. Markers
  are drawn on the ruler as a flag and a line in their colour, not hit-testable (a click goes to the ruler, which moves
  the playhead).

Refined in Step 12.8 (implementation, 2026-10-05; §7 as confirmed at the Step 12.2 acceptance):
- `MediaImportWorkflow.RunAsync` takes `IProjectService.Current` before the file picker opens and compares it
  (`ReferenceEquals`) with the current project after each await: the picker, the yield that shows "Importing N
  files…", and `ImportManyAsync`. On a change nothing is added (`AddMediaAssets` is not called), no analysis is queued,
  and the status bar says "Import stopped: another project was opened, so nothing was added." (also logged). A
  cancelled picker stays silent, a failing check still says "Import didn't finish." — both unchanged.
- No New / Open / Recover lock and no cancellation token (§7). The import still runs on the UI thread as before; only
  what happens after it is guarded. The next import into the new project works normally.
- Confirmed by the product owner (2026-10-05) as written. In the running app the check of the picked files is
  synchronous on the UI thread, so a New can only come in between the picker closing and the "Importing N files…" yield
  — about one frame; nine scripted attempts at the real race never hit it (the click was lost while the picker closed,
  or came after the import, which then went into the project it started in and New asked about unsaved changes). The
  guarded path is covered by the automated tests; the rule is the same for New, Open and Recover (any other project
  object).

Closeout (Step 12.9, 2026-10-05; the product owner's decisions for it):
- Local QG: build `-warnaserror` 0 / 0; the full suite 2324 passed, 2 skipped (only the two 4K scenes), 0 failed; three `--blame-hang` runs 2324 / 0 / 2, no hang, no dump; the 4K scenes with `AIVE_HEAVY_TESTS=1`: ExportEndToEnd 90 / 90; `git diff --check` clean. No failure, no fix.
- The manual plan in the real app by Claude (isolated profile, small fixture projects from the Phase 10 fade fixture);
  the checks made in the real app at Steps 12.6–12.8 count where they match the code. Not reproducible by hand and
  recorded as such: removing media while its analysis runs (the analysis of the fixture ends in a fraction of a second)
  and New during an import (the one-frame window, Step 12.8) — both covered by automated tests.
- R1: the Phase 11 state (`47ed2fa`, extracted with `git archive`, built in a separate artifacts folder — no worktree,
  branch or commit) saved a real project; Phase 12 opened it unchanged (4 media, 13 clips), and after an edit (a marker)
  saved it as `formatVersion` 3 with clips, media, tracks and the dissolve byte-for-byte as Phase 11 wrote them.
- R2: the export plan's UI and file scenarios again (1 / 14, 7, 10, 11, 12, 13); the rendering scenarios (2, 3, 5, 6,
  8) not re-run — Phase 12 changed no export, playback, rendering or audio code, and the parity suites and the 4K scenes
  are green; 9 optional, not run. R3: relink, recent projects, a fade in the Preview, a dissolve removed and added.
- Kept open: L1-c; the test helpers `F(end − start)` of the 12.3 / 12.5 tests (a separate cleanup, product owner).
- Observation, not a Phase 12 change: at the default zoom a short dissolve's zone (0.4 s, about 20 px) is covered by the
  two clips' trim handles, which win the press (D025 "Refined in Step 10.8"), so selecting it needs a zoom in.

After the merge (recorded at Step 13.2, 2026-10-06): Phase 12 accepted by the product owner on the Step 12.9 verification
(closeout `6761c1d`); PR #12 merged into `main` as `c0cb600` (2026-10-05), CI green. Kept open after Phase 12: the
`F(end − start)` test helpers (a separate cleanup); L1-c moved into Phase 13 (D028 §6).

Status: Accepted (2026-10-05, product owner decisions of 2026-10-05). Phase 12 complete: closed on 2026-10-05 (Step
12.9), merged as `c0cb600`, CI green. Steps and acceptance criteria: `docs/DEVELOPMENT_PLAN.md`, "Phase 12 — Editing
essentials: steps". Sub-decisions were proposed at the start of their step, confirmed by the product owner and recorded
as a refinement here.

---

## D028 — Phase 13: project & export settings

Date: 2026-10-06

Decision (product owner, 2026-10-06, after the Step 13.1 audit). Phase 13 replaces the fixed 1920 × 1080 canvas with
real **project settings** — the canvas size and the frame rate, chosen by the user and changeable in an existing
project as one undoable step — and adds **export settings**: the quality (libx264 CRF), the encoder speed (libx264
preset) and the audio bitrate. Because the quality becomes configurable, **L1-c** (the open tolerance of the codec leg
MP4 → export canvas, D023 Step 8) is decided inside the phase. The container and codecs stay MP4 / H.264 / AAC and the
export stays the offline rendering of the Preview (D023), so the Preview and the export canvases stay identical by
construction at any canvas size.

Context (Step 13.1 audit, the code at `c0cb600`, `main` after the merge of PR #12):
- `ProjectSettings` (`Core/Entities/Project.cs`) has `FrameWidth` / `FrameHeight` (default 1920 × 1080), `FrameRate`,
  `IsFrameRateLocked` and an unused `AudioSampleRate`. No code outside `ProjectSerializer` assigns the size: a new
  project is always 1920 × 1080 and only a hand-edited `project.json` gives another one (the Phase 12 manual fixtures
  used 640 × 360 that way). D018: the canvas is "never taken from a video".
- The frame rate is a provisional 30 fps until the first video fixes it (D007: re-grid of the existing clips in the same
  undo step, atomic rejection); after that it never changes. Fades, dissolves (D025) and markers (D027 "Refined in Step
  12.7") keep their time when the rate changes; a clip's frame count comes from its edges on the grid.
- The canvas, the rate and `IsFrameRateLocked` are already in `project.json` v3 (`ProjectSettingsDto`); the reader
  accepts versions 1–3 and refuses a newer one ("saved by a newer version").
- Composition (D018): positions (`PositionX/Y`) and text sizes are canvas pixels; the picture fit ("contain") is
  recomputed from the canvas, so pictures follow a canvas change and pixel values don't. The playback snapshot carries
  the canvas (`PlaybackSnapshot.Canvas`); a canvas change is presentation-only for the decoders
  (`DiffersOnlyInPresentation`).
- Export (D023): `ExportFormat` constants — MP4, libx264 CRF 18 preset medium, 8-bit 4:2:0 BT.709 limited, AAC-LC 48 kHz
  stereo 192 kbps; `FfmpegExportEncoder` builds its arguments from them; `ExportOutput.For(snapshot)` takes the size and
  rate from the snapshot; the preflight refuses an odd width or height (`ExportIssueKind.InvalidCanvas`).
  `ExportSettings` (`Project.LastExportSettings`) is session state only — the last output path and single-value enums,
  never saved, never dirty (`ExportSettingsPersistenceTests` checks that it is not written).
- Parity (D023 Step 8): the Preview ↔ export canvas checks are byte-exact; the codec leg has no tolerance (L1-c), but
  some end-to-end tests carry sanity bounds measured at CRF 18 (e.g. `ExportCompositionEndToEndTests`, "mean |Δ| ≤ 3 per
  frame, MP4 vs canvas" on its nine scenes). The Step 8.6 measurement (one configuration: CRF 18 / medium) is the only
  data on the codec error.
- Project JSON in the repository: no fixture files; the tests write `project.json` inline (`Project.Tests`, a few
  `Timeline.Tests`), and the manual fixture scripts `tools/manual/New-Phase10*Fixture.ps1` write `formatVersion = 3`.

### 1. Scope

- Project settings: the canvas width × height (presets for common sizes and orientations plus a custom size; even
  values, within limits fixed at 13.3) and the project frame rate (a list of common rates; custom rates only if 13.5
  shows they are needed). Set for a new project and changed in an existing one; each change is one `IUndoableCommand`
  with exact Undo / Redo, the save point respected, disabled during an export (`EditingLock`).
- Export settings: the quality (CRF, offered as named levels with today's CRF 18 among them), the encoder speed (libx264
  preset) and the AAC bitrate, applied by the existing encoder; the output stays MP4 / H.264 / AAC at the size and rate
  of the project.
- L1-c: criteria for the codec leg MP4 → export canvas for every offered quality level (§6).
- UI: a project settings dialog and the export settings in the export flow; no other UI redesign.

### 2. Constraints

- Unchanged in substance: D001 / D006 (ticks, rational rates), D008 (frame grid, no overlap), D009 / D022 (source frame
  selection), D013 (the mix at 48 kHz stereo — `AudioSampleRate` stays unused), D014–D016 (persistence, save point,
  autosave, recovery) apart from the format rule of §5, D018's composition math (only the canvas value varies), D023's
  architecture (C# compositor, Avalonia rasterizer, FFmpeg only as the encoder), D025, D026, D027.
- Changed explicitly by this decision, each recorded as a refinement at its step: D018 "default 1920 × 1080" becomes a
  user setting (a canvas from a video: question 1 below); D007 "after locking, the rate never changes" — the user may
  change it (§4); D023 "fixed format, no user settings" — quality, speed and audio bitrate become settings, while the
  container, codecs, pixel format and colour tags stay fixed; L1-c is closed by §6.
- The canvas-level parity (Preview ↔ export canvas, byte-exact: `Rendering.Tests`, `Export.Tests`, the canvas checks of
  `ExportEndToEnd.Tests`) is never weakened, re-baselined or removed. New canvas sizes and rates get added scenes;
  existing scenes keep their expected values.
- Every new project change is one undoable command; a refused change changes nothing and leaves no Undo step.
- Builds and test runs on the product owner's command; push, pull request and merge only with the product owner's
  direct permission; each step accepted before the next one starts.

### 3. Out of scope

AI features; HDR / 10-bit / colour management (incl. tone mapping and colour tags other than BT.709); other containers or
codecs (HEVC, VP9 / AV1, ProRes, …); hardware encoding; bitrate targets or two-pass encoding; an audio sample rate or
channel layout other than 48 kHz stereo; exporting a time range; an export size or rate different from the project's
(unless the D028 acceptance decides otherwise — question 2 below); fit policies other than D018's "contain"; an
installer; timeline virtualization; an undoable import; fixing the known flaky CI tests (§8); the `F(end − start)`
test-helper cleanup (separate, product owner); a UI redesign.

### 4. Project settings rules (sub-decisions at the steps' start)

- Canvas (13.4): a canvas change keeps every clip, its timing and its stored properties; pictures re-fit by D018. Left
  to the start of 13.4: whether `PositionX/Y` and text sizes stay in pixels or scale with the canvas; the presets. The
  size limits (minimum, maximum — the Preview decodes at most 1280 × 720 per D023 Step 8.3, the export renders at the
  canvas size) are fixed at the start of 13.3 with the validation rule.
- Frame rate (13.5): a rate change re-grids the timeline like D007's first lock — every clip edge to its nearest frame
  of the new grid, a clip that would collapse grows by one frame into free space, otherwise the change is refused
  atomically; fades, dissolves and markers keep their time (D025, D027) and are validated on the result (a dissolve
  whose zone no longer fits makes the change refuse rather than leave an invalid one). A user-chosen rate sets
  `IsFrameRateLocked`, so a later first video no longer changes it. Left to the start of 13.5: the offered rates,
  custom rates, whether Undo of the change restores the "unlocked" state too; the clipboard keeps D027's rule (a paste
  after a rate change is refused).
- New project (13.6): whether New asks for the settings or opens with defaults that can be changed later; the defaults.

### 5. Project format

- The canvas size, the frame rate and `IsFrameRateLocked` are already stored in v3: the project settings need **no**
  format change.
- Export settings: saved with the project or kept as an application default — question 3 below, settled at the latest
  at the start of 13.3. If they are saved, the format is decided at 13.3 by this rule: `formatVersion` stays **3** when
  the new data is an optional property whose absence reads as today's behaviour (CRF 18, medium, 192 kbps) and no
  existing property changes its meaning — the precedent of the Phase 7 metadata fields added to v3 without a bump. A
  **v4** is introduced only if that is not possible (an existing property changes its meaning, or a build that ignores
  the new data would read the file wrongly rather than just without the setting) — never only because it is cleaner.
- If v4 is introduced: **no backward migration** — reading v1–v3 projects is not a Phase 13 requirement (product owner,
  2026-10-06: no user projects in v3 need to be kept). No conversion code is written; 13.3 decides whether the existing
  v1–v3 reading code stays as it is or old files are refused with a clear message. The repository's project JSON — the
  inline `project.json` of the tests and the `tools/manual` fixture scripts — moves to v4 at once (rewritten, not
  converted); the recovery file follows the project format. Tests of v1–v3 reading are kept only if that reading code is
  kept.
- If v3 stays: a project of Phase 12 opens and saves unchanged (R1 at the closeout, as before).

### 6. L1-c — codec-leg criteria

- Included because the quality becomes configurable (product owner, 2026-10-06). The old CRF-18 sanity bounds are not
  kept artificially: a bound measured for CRF 18 (e.g. "mean |Δ| ≤ 3, MP4 vs canvas") is replaced by the L1-c criterion
  of the level it tests.
- Method (Step 13.8): every offered quality level (and every offered speed preset) measured with the Step 8.6 method —
  the MP4 decoded against the export canvas; total / floor / quant; mean, p99, p99.9, max, PSNR — over the existing
  scenes plus the new canvas sizes; the product owner sets the criterion per level from that data (or decides that a
  level gets only validity checks: duration, frame count, decodability); the criterion goes into the suite and into
  this decision.
- Never changed by L1-c: the canvas-level parity (§2); the audio checks (length, lag and onsets within 10 ms — the SNR
  sanity bound may be set per audio bitrate by the same method); the encoder's colour contract (BT.709 limited, 4:2:0).
- A codec-leg criterion changes only in Step 13.8 (or by a later product owner decision), with its data — never to make
  an unrelated change pass.

### 7. Export settings rules (sub-decisions at the steps' start)

- 13.7: a settings model in Core for the values that become choices (the container, codecs, pixel format and colour
  tags stay constants); the encoder's arguments built from it; the default is today's output (CRF 18, medium, 192 kbps),
  so an export with the default settings is what Phase 12 produced. Left to the start of 13.7: the levels and their CRF
  values, the offered presets and bitrates.
- 13.9: where the settings are chosen (before the save picker or in a dialog of their own), how they are remembered (per
  §5), the texts; disabled during an export.

### 8. Known flaky CI tests — policy (product owner, 2026-10-06)

Known tests that have failed intermittently on CI before Phase 12 without a product cause found: the autosave timer
(`Project.Tests`), an ffprobe timeout in a waveform test, and the 5 s PATH probe of the ffmpeg / ffprobe locators in
`Video.Tests` (Known issues; D024 Step 9.10). Not fixed in Phase 13.
- A rerun of one of these specific tests (or of the CI job in which only it failed) is allowed to diagnose or confirm the
  known cause: the failing test is named, its failure message matches the known cause, and the rerun is recorded (test,
  message, run) in `progress.md`.
- A rerun is never a substitute for fixing a real regression and is not a general way to get a green gate: a failure of
  any other test, or of a known one with a different message, is a real failure — investigated and fixed (or reported
  to the product owner) before the gate counts. Rerunning until the suite passes is not allowed.
- A flaky test is never weakened, skipped or removed to make a gate pass.

### Open for the D028 acceptance (product owner)

1. Canvas from the first video: keep D018 "never taken from a video" — the user sets the canvas (recommended: simple and
   predictable) — or let the first video set the canvas of a new project, as D007 does for the rate?
2. Export size / rate: always the project's (recommended: the Preview and the export stay identical by construction; a
   smaller file comes from a smaller canvas or a lower quality) or an export resolution of its own?
3. Export settings: saved per project (recommended: a project exports the same way after reopening; settles §5) or an
   application-wide default?

Accepted by the product owner (2026-10-06), with the answers:
1. The canvas is never taken from a video: it is an independent project setting set by the user. D018 changes only from
   "always 1920 × 1080" to "1920 × 1080 by default, changeable by the user".
2. No export resolution of its own: the export size is always the project canvas; no second size setting.
3. The export settings are saved in `project.json` with the project.
4. No `formatVersion` 4 in advance: Step 13.3 first checks whether the export settings fit v3 as an optional property
   (§5); v4 only if objectively needed, and even then without a v3 → v4 migration.

Refined at the start of Step 13.3 (product owner, 2026-10-06, on the step's analysis) and in its implementation:
- Format (F-1): `formatVersion` stays **3**. The canvas, the rate and `IsFrameRateLocked` were already stored; the export
  settings are the optional `settings.export`. No existing property changes its meaning; a file without it (every file
  of Phases 6–12) reads as the default = the Phase 8–12 output; a Phase 12 build ignores the unknown property (the
  serializer does not refuse unknown members), so it reads the file without the setting rather than wrongly. No
  migration code.
- Placement (M-1): `ProjectSettings.Export` (`ExportEncoding`), JSON `settings.export`. The top-level
  `lastExportSettings` of Phases 6–8 (another shape: size, double rate, bitrates) is not reused and stays ignored;
  `Project.LastExportSettings` (the output path) stays session-only, never saved.
- `ExportEncoding` (Core, immutable record): `Quality` (`ExportQuality`: Maximum = CRF 14, **High = CRF 18**, Standard =
  CRF 23, Compact = CRF 28), `Preset` (`ExportSpeedPreset`: fast / **medium** / slow), `AudioBitrateKbps` (128 / 160 /
  **192** / 256 / 320); `Default` = (High, Medium, 192), equal to the `ExportFormat` constants (EX-2). The file stores the
  quality's **name**, not the CRF. `settings.export` is written only when the settings differ from the default (a
  project with the default settings is saved exactly as before — a Phase 12 project opens and saves unchanged);
  `"export": null` reads as the default. When present, every field is required; `quality` / `preset` must be the exact
  enum names ("High", "Medium"; no numbers, other spellings or combinations), the bitrate one of the offered values;
  anything else makes the file damaged (EX-4, as D014). Unknown properties inside it are ignored, as elsewhere in the file.
- Canvas limits (L-1, `ProjectSettingsRules.CanvasError`): both sides even, each 64–4096 px, the area at most
  9 437 184 px (36 864 macroblocks, the H.264 level 5.1 frame size). The presets proposed at the step (landscape 3840 ×
  2160, 2560 × 1440, 1920 × 1080, 1280 × 720, 640 × 360; portrait 2160 × 3840, 1080 × 1920, 720 × 1280; square 1080 ×
  1080; 4:5 1080 × 1350; custom; swap W / H) are accepted for the UI of 13.6.
- Frame rates (L-2, `ProjectSettingsRules.SelectableFrameRates`): 24000/1001, 24, 25, 30000/1001, 30, 50, 60000/1001,
  60. A rate outside the list already in a project (e.g. fixed by the first video, D007) is kept on load but can't be
  chosen as a new value.
- Load (CS-2): the file rules are unchanged — a positive canvas and any positive rational rate open; a canvas outside
  the user limits is not applied by the UI, and the export preflight checks the limits too (implemented with the canvas
  change, 13.4).
- Frame rate change (for 13.5): FR-1 — the source handles of **every** dissolve are checked when the rate changes, also
  where its clips didn't move (the zone's split around the cut changes in time with the rate); this also applies to the
  first video fixing the rate (a production change of the transition validation — made in 13.5, with its regression
  test). FR-2 — markers keep their `MediaTime`; several on one frame are allowed. FR-3 — locked tracks are re-gridded
  too. FR-4 — choosing the current rate of an unlocked project locks it as one Undo step; of a locked one, nothing.
- Export settings changes (EX-1): a project change — undoable and dirty; no command when nothing changes (13.9).
  EX-3: L1-c in 13.8 measures the four levels at medium and the three presets at High; the other combinations get
  validity checks only.
- Not decided (product owner): CS-1 — what happens to `PositionX/Y`, `Scale`, `FontSize` and other clip properties when
  the canvas changes is decided at 13.4 after the canvas coordinates and the transform model are reviewed; until then a
  canvas change touches no clip property and no hidden fix-up exists. M-2 — the command architecture for the canvas and
  the rate is decided at 13.4 / 13.5 from the existing `ITimelineEditService` / `EditPlan` / commands; Step 13.3 adds no
  service or interface.
- Implementation (13.3): `Core/Entities/ExportEncoding.cs` (the record and enums, `Crf`, `PresetName`, `Validate`),
  `Core/Entities/ProjectSettingsRules.cs` (canvas limits, selectable rates), `ProjectSettings.Export`;
  `ProjectFileDto` (`ExportEncodingDto`, written when not null) and `ProjectSerializer` (`ReadExportEncoding`); nothing
  uses the settings yet — no UI, no command, the encoder still reads `ExportFormat` (13.7), `FrameRateRegrid` unchanged.

Refined at the start of Step 13.4 (product owner, 2026-10-06, on the step's analysis of the canvas semantics) and in its
implementation:
- The model as it is (D018): `PositionX / PositionY` are canvas pixels (the offset of the centre from the canvas centre);
  a picture's `Scale` multiplies its contain-fit, so a picture's size already follows the canvas; text has no fit — its
  `FontSize` is canvas pixels and its `Scale` an absolute multiplier. A canvas change that kept every value (variant A)
  moves positioned elements towards the centre or off the canvas (e.g. a logo at 10 % from the left edge of 1920 × 1080
  lands at x = −228 on 1080 × 1920) and changes text sizes relative to the frame, even at the same aspect ratio.
- **CS-1 = B**: with `s = min(W' / W, H' / H)` — the factor by which D018's contain fits the old canvas into the new one
  (the axis chosen by exact cross-multiplication, as `CompositionMath.Layout`) — `PositionX / PositionY` of every video,
  image and text clip and `FontSize` of every text clip are multiplied by `s`. Nothing else changes: `Scale` (pictures:
  it is fit-relative already; text: the user's multiplier — B-2), rotation, crop, opacity, timing, source ranges, speed,
  fades, dissolves, markers, audio, the playhead, the rate. At the same aspect ratio the composition is kept exactly;
  at another one the old frame is contained in the new canvas, so no positioned element or text leaves it; pictures
  whose aspect differs from the old canvas follow their own fit.
- Accepted property of B: the exact round trip is Undo / Redo, **not** a change of the size and back — s is the contain
  factor both ways, so 1920 × 1080 → 1080 × 1920 → 1920 × 1080 multiplies by 0.5625² = 0.31640625. No hidden history
  restores the old values (a regression test pins this).
- B-1: a scaled value outside `ClipPropertyLimits` (`FontSize` 1–1000, positions ±100 000) refuses the whole change —
  no clamping; the message names the size, the clip (a text clip by its first line, a media clip by its file) and its
  track, and the validator's reason. Nothing is changed before the refusal.
- B-3: every video / image / text clip is scaled, on locked and hidden tracks too (the canvas belongs to the whole
  composition); a locked track does not block the change.
- B-4: clips copied before a canvas change are refused on paste — "The project frame size changed since the clips were
  copied. Copy them again." — like after a rate change (D027 §5); `TimelineClipboard.Canvas` records the canvas of the
  copy; an Undo of the change makes the old clipboard valid again.
- Command (M-2): `ITimelineEditService.SetCanvasSize(width, height)` — no new interface. `ProjectSettingsRules.CanvasError`
  first (refused: nothing changes, no Undo step); the same size is `NoChange`; otherwise one `CompositeCommand` "Set
  Frame Size" = `SetCanvasSizeCommand` (width and height together, absolute old / new) + one `SetClipPropertiesCommand`
  per clip whose values change (absolute before / after; Undo / Redo write back the stored values), wrapped in one
  `NotifyingCommand` — one `TimelineChanged`, dirty through the save point, clean again by Undo to it. No media event:
  thumbnails and waveforms are untouched. The export lock (`EditingLock`) is held by the UI, as for every edit: the
  service does not know it, and the command is disabled during an export by the dialog of 13.6.
- Snapshot: the canvas, the positions and the text style are presentation (`DiffersOnlyInPresentation` unchanged), so a
  canvas change keeps every decoder (a test checks the pipeline, the seek generation and the decoder requests); the
  Preview takes the new canvas from the rebuilt snapshot, the export from its snapshot.
- P-1: `ExportPreflight.Check` refuses any canvas `ProjectSettingsRules.CanvasError` refuses (`ExportIssueKind.InvalidCanvas`,
  "The frame size W × H can't be exported. <reason>"); a loaded file may still carry any positive size (CS-2). The
  encoder keeps its own even-size guard.
- D018 changes only in "Canvas: … default 1920 × 1080; never taken from a video": the canvas is a user setting with that
  default, never taken from a video (D028 answer 1); its composition rules are unchanged.
- Implementation: `ITimelineEditService.SetCanvasSize`, `TimelineClipboard.Canvas`, `SetCanvasSizeCommand`
  (`TimelineCommands.cs`), `TimelineEditService.SetCanvasSize` / the paste check, `ExportPreflight`. Not in 13.4: the UI
  (13.6), the frame-rate change (13.5), the export settings and the encoder (13.7, 13.9).
- Not changed, noted for later: a new text clip still gets the model's `FontSize` 48 whatever the canvas.

Consequences: `ProjectSettings` becomes user-editable through new undoable commands; the export gains a settings model
used by `FfmpegExportEncoder` (`ExportOutput` and the preflight keep their roles); `project.json` stays v3 or becomes v4
by §5; the parity suite gains scenes for new canvas sizes and rates and per-level codec criteria (§6);
`docs/PHASE13_MANUAL_TEST_PLAN.md` holds the real-app scenarios.

Status: Accepted (2026-10-06, product owner). Step 13.3 accepted (`4514f09`); Step 13.4 done — awaiting acceptance. Steps and acceptance criteria: `docs/DEVELOPMENT_PLAN.md`, "Phase 13 — Project & export settings: steps".

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
