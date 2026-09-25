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

## How to add a decision

When a major architectural decision is made, add:

- ID
- Date
- Decision
- Context
- Consequences
- Status

Do not rewrite old decisions merely because a newer approach is preferred. Supersede them explicitly.
