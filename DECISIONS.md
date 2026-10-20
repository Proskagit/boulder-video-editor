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

Status: Accepted. Phase 13 (D028, Step 13.5): the user may choose the rate (new or existing project) — the same re-grid,
one Undo step, and the chosen rate is locked; the automatic rule above is unchanged. D028 "Refined in Step 13.5".

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
split and re-grid follow D022's timing rule. Refined by D032 (Phase 17): the `VideoClip` with its own sound stays
the base model; its sound can be unlinked into an `AudioClip` of the same asset, and a video clip and an audio clip can be
explicitly linked.

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
  Step 8.6 data below must not be read as one. Whether and how to set one is an open product decision — **decided in
  Phase 13 Step 13.8: D028 "Refined in Step 13.8" (per-level bounds on the codec's own error, the level order, flat
  colour through the leg).** Unchanged
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

Status: Accepted. Phase 13 (D028, Step 13.7): the quality (CRF), the libx264 preset and the AAC bitrate are the project's
export settings, carried by the job; their default is this decision's fixed format (CRF 18, medium, 192 kbps), with the
same command lines. D028 "Refined in Step 13.7".

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
- Frame-rate re-grid (the first video fixes the rate, or the user changes it — D028 Step 13.5, where every dissolve's
  handles are checked, FR-1): `F` is re-derived; a dissolve whose clips are no longer adjacent
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

Closed (product owner, 2026-10-07, recorded at Step 15.2): the three tests were fixed in Phase 14 (D029, Steps 14.3–14.4)
and the D029 §5 measure was met — CI green on the first attempt on the Phase 14 pull request (PR #14, run 37590085671)
and on the first run on `main` after its merge (run 37590661647). This policy no longer applies: every CI failure is a
real failure, investigated and fixed (or reported to the product owner); no rerun counts towards a gate.

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

Refined in Step 13.5 (implementation, 2026-10-06; FR-1…FR-4 as confirmed at the start of Step 13.3):
- `ITimelineEditService.SetFrameRate(rate)` — no new interface. A rate not in `ProjectSettingsRules.SelectableFrameRates`
  (or an invalid one) is refused ("… FPS is not one of the project frame rates."). The current rate of a locked project
  is `NoChange`; of an unlocked one it is locked as one Undo step (`SetFrameRateCommand` alone, nothing re-gridded —
  FR-4). Any other rate: an `EditPlan` with the new rate, locked (`SetFrameRate(rate, locked: true)`), `FrameRateRegrid.Plan`
  and the usual `Validate` (`ReconcileTransitions`, `ClampFades`, `TimelineValidator` on every track, `TransitionRules` and
  the source handles); a failure of either refuses the whole change with "Can't change the frame rate to X FPS: <reason>"
  — nothing changed, no Undo step. Otherwise one "Set Frame Rate" step (`plan.BuildCommand`, absolute before / after
  states) in one `NotifyingCommand`: one `TimelineChanged`, dirty through the save point.
- Re-grid (`FrameRateRegrid`, D007's rule, unchanged in substance): every track, locked and hidden ones too (FR-3); each
  clip edge to its nearest frame of the new grid (ties up), so clips that met still meet and edges of other tracks at
  the same time land on the same frame; `SourceIn` kept, `SourceOut` follows the new length; a clip that would collapse
  grows by one frame into free space (right, else left), else the change is refused; a clip past its source is shortened
  to the source's whole frames; a clip with a speed keeps its source range and speed and takes `SpeedTiming.FramesFor`
  frames (D022). Order kept or refused; overlap refused.
- Kept in time: fades (`FadeIn` / `FadeOut` ticks unchanged, frames derived anew; cut to a shorter clip by `ClampFades`),
  dissolves (`Duration` ticks unchanged, `F` derived anew; removed with D025's note when their clips no longer meet,
  refused when `F < 2`, the zone doesn't fit or the handles don't suffice), markers (`MediaTime` unchanged, several on
  one frame allowed — FR-2). The playhead (session state, D015) goes to its nearest frame of the current grid after
  Execute, Undo and Redo (in the command's notification, never an Undo step of its own).
- **FR-1 fixed**: `EditPlan.IsTouched` counts every dissolve as touched when the plan changes the rate, so its source
  handles are checked although none of its clips moved (a rate change re-derives the zone's frames and their split
  around the cut: 0.1 s is 1 + 2 frames at 30 fps, 3 + 3 at 60). This applies to the first video fixing the rate as well
  (D007), where an unlocked project's existing dissolve could otherwise be left with too little source. Regression tests:
  30 → 60 fps with B one 30 fps frame into its source — refused; the same through the first video — refused; with
  enough source — kept.
- Clipboard: clips copied before a rate change are refused on paste — the existing D027 §5 rule ("The project frame
  rate changed since the clips were copied. Copy them again."); an Undo of the change makes the copy valid again.
- D007 changes only in "after locking, the rate never changes": the user may change it (D028); the automatic rule (the
  first video fixes an unlocked rate) is unchanged.
- Not in 13.5: the UI (13.6 — with the `EditingLock`), the export settings / encoder (13.7). `project.json` unchanged.

Refined at the start of Step 13.6 (product owner, 2026-10-06, on the step's UI audit) and in its implementation:
- `ITimelineEditService.SetProjectSettings(width, height, rate)` — the dialog's Apply. Both parts checked first (the canvas
  rules, the offered rates). Only the canvas changing → `SetCanvasSize`; only the rate (a different one, or the current one
  of an unlocked project) → `SetFrameRate`; neither → `NoChange`. Both: the rate part is planned (`EditPlan` with the
  rate locked, `FrameRateRegrid`, `Validate` — or the lock-only command) and then the canvas part (the scaled positions and
  font sizes and their limits), both on the unchanged model; a refusal of either returns before any change. Then one
  `CompositeCommand` "Change Project Settings" — the rate command first, the canvas commands after — in one
  `NotifyingCommand` (one `TimelineChanged`; the playhead snapped to the grid on Execute / Undo / Redo). Not two public
  calls in a row. The 13.4 / 13.5 methods share the planning code (`PlanCanvas`, `PlanFrameRate`) and keep their rules.
- The canvas part's property commands carry only the picture and text groups (`ClipPropertyValues(visual, null, text)`),
  so they never write back a fade that the re-grid in the same step cuts (`ClampFades`); the rate part's property commands
  run first and the canvas commands write the scaled positions over them. Undo runs the reverse. (A test with a clip
  whose stored fade the re-grid cuts and whose position the canvas scales pins this.)
- `ProjectSettingsRules.ContainFactor` is the one source of the CS-1 B factor (the service and the dialog's notice).
- New (decision 2): unchanged — a new project has 1920 × 1080 and the provisional 30 fps; no dialog is opened.
- Errors (decision 3): the canvas rules shown live under the size fields (Apply off while they fail); a refusal by the
  service shown under Apply with the dialog left open (the values can be corrected and applied again); a successful
  Apply closes the dialog and reports the service's message in the status bar.
- Draft: the dialog edits a copy of the size and the rate (`ProjectSettingsViewModel`); only Apply calls the service.
  Cancel, Esc and the title-bar close change nothing — no edit, no Undo step, no `TimelineChanged`, no dirty flag.
- Rates offered: the eight project rates, and before them the current rate kept as it is when the project is unlocked
  ("30 FPS (provisional)" — keeping it leaves the project unlocked; choosing the plain "30 FPS" locks it, FR-4) or when it
  is not offered ("15 FPS (current)", until another rate is applied). A kept rate with a new size goes to `SetCanvasSize`.
- Export lock: "Project Settings…" is disabled while `EditingLock` is held (the toolbar's `CanEdit`, refreshed on the
  lock's change); the workflow refuses with a status message if reached anyway; Apply checks the lock again right before
  calling the service ("The project settings can't be changed while a video is being exported."). The service layer
  has no dependency on the UI lock.
- UI: toolbar "Project Settings…" after Import Media, before Export, with separators; the tooltip "Project Settings —
  W × H · R FPS" (with "(provisional)") follows every timeline change, Undo / Redo and New / Open / Recover; no hotkey.
  The dialog (a modal window 460 px wide, `AvaloniaProjectSettingsDialog` hosting `ProjectSettingsView`): the current
  settings; FRAME SIZE — the presets (Landscape 3840 × 2160, 2560 × 1440, 1920 × 1080, 1280 × 720, 640 × 360; Portrait
  2160 × 3840, 1080 × 1920, 720 × 1280; Square 1080 × 1080; 4:5 1080 × 1350; Custom) with "⇄ Swap" beside them, then Width
  / Height (`CommitNumericUpDown`, step 2); the live rule message and the scale notice ("Positions and text sizes will be
  scaled by 56.25 %."); FRAME RATE with its notice; Apply / Cancel; the service's error. The real-app check at 1024 px
  found the four-digit sizes cut off with Swap in the size row — Swap moved next to the presets, re-checked at 1024 and
  1440 px.
- Not in 13.6: the export settings (13.7 / 13.9), L1-c (13.8).

Refined in Step 13.7 (the step's audit accepted by the product owner, 2026-10-06, and the implementation):
- One flow: `ProjectSettings.Export` → `ExportPreflight.Check` → `ExportJob.Encoding` → `ExportService` →
  `IExportEncoder.StartAsync(output, encoding, destination, ct)` → `FfmpegExportEncoder`. `ExportJob(snapshot, outputPath,
  ExportEncoding? encoding = null)` — null is `ExportEncoding.Default`, a value not offered an `ArgumentException`; the
  preflight passes `project.Settings.Export` when it makes the job — the same moment and thread as the snapshot. The
  record is immutable and the project only ever replaces it, so later changes of the project's settings never reach a
  job (tests: the preflight, a real export through the service). The export service and the encoder read no project.
- `FfmpegExportEncoder`: `-crf` = `encoding.Crf`, `-preset` = `encoding.PresetName`, `-b:a` = `AudioBitrateKbps × 1000` (bits
  per second, invariant culture); every other token, its value and the order are unchanged (libx264, AAC-LC, 48 kHz,
  stereo, yuv420p, BT.709 limited, the filter, the mapping, `-fps_mode passthrough`, `+faststart`, MP4). The encoder
  validates the settings too, before ffmpeg starts.
- One source of truth: `ExportFormat.VideoCrf`, `VideoPreset` and `AudioBitrateBps` are removed (no fallback); `ExportFormat`
  keeps only the fixed format (container, codecs, extension, 48 kHz, 2 channels). The comments of `IExportEncoder` /
  `FfmpegExportEncoder` no longer call the quality fixed.
- Default compatibility: golden command lines in `Video.Tests` — the full audio and video argument strings of Phase 12,
  taken from the Phase 12 code (the test was run against it before the change) — must equal the default's character for
  character; each quality level / preset / bitrate changes only its own token (`-crf`, `-preset`, `-b:a`); the numbers
  don't depend on the culture. A byte comparison of MP4 files across ffmpeg builds is not part of the suite (libx264 is
  deterministic only for the same build and thread count): the arguments and the unchanged canvases / PCM are the contract.
- Real encodes (validity, not picture criteria — those are L1-c, 13.8): every level at medium and every preset at High
  give a valid MP4 (frames, numbers, duration, audio length) whose x264 options string carries `crf=14.0 / 18.0 / 23.0 /
  28.0` and `subme=6 / 7 / 8` (fast / medium / slow); every AAC bitrate gives a valid AAC-LC track; 128–256 kbps within
  10 % of the request on noise. An export through the whole chain: default for a project without settings, other
  settings reaching the encoder with the canvases still the Preview's byte for byte.
- **Observation for 13.8 / 13.9 (not changed):** ffmpeg's native AAC encoder (FFmpeg 9.0.1) does not reach 320 kbps on
  stereo — about 243 kbps measured, below the ≈ 259 kbps it gives for 256 kbps. The request is passed exactly
  (`-b:a 320000`); whether 320 stays offered is a product decision for the measurement / UI steps.
- Not in 13.7: the export settings UI (13.9), L1-c (13.8), `project.json` (unchanged, v3).

Decided after Step 13.7 (product owner, 2026-10-06): 320 kbps stays offered. The contract of an AAC bitrate setting is
the request passed to ffmpeg (`-b:a 320000`) and a valid AAC-LC 48 kHz stereo track of the right length; the bitrate the
encoder reaches is not a criterion (FFmpeg 9.0.1's native AAC encoder reaches ≈ 243 kbps for a 320 kbps request on
stereo noise — its behaviour, recorded here). No test requires ≈ 320 kbps.

Refined in Step 13.8 — **L1-c decided** (2026-10-06; the measurement, the criteria and the mutation checks in one step, as
the product owner asked; the numbers below await the step's acceptance):
- Measured (M) with the Step 8.6 method, now in the suite (`ExportEndToEnd.Tests/ExportCodecLegTests`, `CodecLegMetrics`;
  every line written to the test output): the MP4 decoded by ffmpeg (BT.709 tags honoured) against the canvases the
  service encoded, R / G / B samples; split with a reference made from the same canvases through the same BGRA → BT.709
  limited 4:2:0 conversion but libx264 lossless (`-qp 0`): **floor** = reference vs canvas (conversion, chroma
  subsampling), **quant** = MP4 vs reference (the lossy encoding — what the level changes), **total** = MP4 vs canvas.
  Eleven scenes (flat colour; PNG with alpha over video; JPEG; text; the moving pattern; a rotated portrait video; 4×
  speed; mixed layers at 29.97; portrait 180 × 320 and square 240 × 240 canvases; a 1080p canvas) × six settings (the
  four levels at medium, fast and slow at High). Two runs gave identical numbers (FFmpeg 9.0.1, same machine).
- Data (lowest per-frame PSNR in dB over the scenes with detail; flat colour is lossless at every level):

  | | Maximum | High | Standard | Compact | High / fast | High / slow |
  |---|---|---|---|---|---|---|
  | quant (MP4 vs lossless reference) | 41.3 | 37.5 | 33.3 | 29.9 | 37.6 | 37.8 |
  | total (MP4 vs canvas) | 24.4 | 24.3 | 24.0 | 23.4 | 24.2 | 24.3 |

  The total is dominated by the floor, which no level changes (worst frame: mean |Δ| 3.6, 24.4 dB on mixed layers — sharp
  saturated edges through 4:2:0; on flat colour max 1). Quant falls with every level on every scene, at least 3.4 dB per
  step; the presets stay within 0.7 dB of medium. Worst-frame quant mean |Δ| at High: 0.17–1.26. File sizes relative to
  High: Maximum ≈ 1.3×, Standard ≈ 0.7×, Compact ≈ 0.45×; the export time of these short scenes barely changes (slow
  +10–20 %).
- **Criteria** (pass / fail, `ExportCodecLegTests`, every scene × every setting):
  1. the codec's own error per level — every frame's PSNR of the MP4 against the lossless reference at least
     **Maximum 39.0 · High 35.0 · Standard 31.0 · Compact 27.5 dB** (≈ 2.3–2.5 dB below the measured lowest values: thread
     count or rounding can't fail it, one level's CRF in another's place does); the fast and slow presets meet their
     level's bound;
  2. the levels in order on every scene with detail — each at least **2.0 dB** above the next (measured ≥ 3.4);
  3. flat colour through the whole leg within one YUV code step — **max |Δ| ≤ 2** — at every setting (guards the colour
     conversion: matrix, range).
  Reasoning: the quality setting changes only the quantization, so the criterion bounds the quantization per level, on
  the error that the level is responsible for; the floor is a property of the fixed format (8-bit 4:2:0 BT.709) and is not
  a quality knob — bounding the total by level would mostly bound the floor. Criterion 2 makes the levels mean something
  relative to each other (a swapped or collapsed CRF fails it); criterion 3 makes the conversion itself checked at every
  setting.
- Sound (unchanged criterion, every offered bitrate): the D023 Step 8.5 checks — the same length, lag and every burst onset
  within 10 ms, SNR ≥ 20 dB against the Preview's own audio — hold at 128, 160, 192, 256 and 320 kbps
  (`ExportParityEncodedTests.Sound_through_the_codec_at_every_offered_bitrate`, three sound scenes; measured SNR 24.5–38.4
  dB, lag 0, onsets 0 ms; not monotonic in the bitrate). Nothing depends on the bitrate the encoder reaches.
- Kept as they were: the canvas-level Preview ↔ export parity (byte-exact), the Step 8.3–8.5 criteria, the Step 6
  composition bound "mean |Δ| ≤ 3 per frame, MP4 vs canvas" on its nine scenes (it runs at the default, High, and stays as
  that level's total sanity bound there — not weakened, not replaced), the 13.7 golden command lines.
- The default path: its command lines are the Phase 12 ones (13.7 golden), so its encoding is unchanged; the High /
  medium row of the data is that path.
- Mutation checks (each reverted; caught by the L1-c tests unless noted): High → CRF 23; Standard collapsed onto 18;
  Maximum → 18; Compact → 23; Compact → 35; the encoder dropping `-crf`; a BT.601 matrix; full range tagged limited; one
  damaged frame inside the encoder; the AAC bitrate passed ten times too low (caught by the per-bitrate sound test). Not
  caught by L1-c and not meant to be: the fast preset mapped to `ultrafast` (still above High's bound) — caught by the 13.7
  tests (golden command lines, the x264 `subme` check) and `ProjectSettingsRulesTests`.

Refined in Step 13.9 (product owner, 2026-10-06, and the implementation):
- Where: an EXPORT section in the Project Settings dialog of 13.6 (not a step of the export flow, not a window of its
  own): Quality (Maximum / High / Standard / Compact; "CRF 14 — largest file", "CRF 18 — default", "CRF 23", "CRF 28 —
  smallest file" as the secondary text and tooltip), Encoding speed (Fast / Medium / Slow; "H.264 preset …") and Audio
  bitrate (128 / 160 / 192 / 256 / 320 kbps, "AAC"; 320 kbps shown as chosen — the bitrate reached is never shown). No
  new values. The current values (`ProjectSettings.Export`; `Default` for a project without `settings.export`) are
  selected when the dialog opens; the export settings are remembered with the project (question 3) and shown again on the
  next opening.
- Draft: the three choices are part of the dialog's draft (`ProjectSettingsViewModel.DraftExport`); changing them changes
  nothing in the project and makes no Undo step; Cancel, Esc and closing the window change nothing.
- Apply: one call — `ITimelineEditService.SetProjectSettings(width, height, rate or null, export)`. The method now takes the
  export settings (null = kept) and a nullable rate (null = the current rate kept as it is, also a provisional one — the
  dialog's "(provisional)" / "(current)" entries); every part is checked before the first change; one part changing goes
  to its own method (`SetCanvasSize`, `SetFrameRate`, the new `SetExportSettings`); several are one "Change Project
  Settings" step; a refusal of any part changes nothing (the export choices included) and stays in the open dialog.
- `SetExportSettings(export)` (EX-1): `SetExportEncodingCommand` replaces the immutable record — one Undo step, dirty, no
  step for the same settings, settings not offered refused; no timeline notification (the timeline doesn't change). The
  next export takes the settings through the 13.7 pipeline (`ExportPreflight` → `ExportJob.Encoding` → encoder); the UI
  never touches a job or the encoder.
- Export lock: unchanged from 13.6 — "Project Settings…" disabled during an export; Apply checks the lock again before the
  service call, so the export choices are refused with the rest.
- Checked in the real app (1024 px window, 125 % scaling): the section fits under the rate; Compact / Slow / 320 chosen as a
  draft (title clean, Undo off), Apply ("Export settings: Compact quality, slow encoding, AAC 320 kbps.", `*`), one Undo
  back to High / Medium / 192, Redo, reopening shows the applied values, Save writes `settings.export` = Compact / Slow /
  320.

Closeout (Step 13.10, 2026-10-06; the product owner accepted Steps 13.3–13.9):
- Local QG at `5c01aed`: build `--no-incremental -warnaserror` 0 / 0; the full suite 2585 passed, 2 skipped (only the two 4K scenes), 0 failed; three `--blame-hang` runs 2585 / 0 / 2 each, no hang, no
dump; the full suite with `AIVE_HEAVY_TESTS=1` 2587 passed, 0 skipped, 0 failed; `git diff --check` clean. No
  failure, no fix; no criterion weakened, re-baselined or removed.
- R1 (a project saved by the Phase 12 build — `c0cb600` extracted with `git archive`, built in a scratch folder; no
  worktree, branch or commit): the Phase 12 app opened the fixture, added a marker and saved (v3, no `settings.export`);
  Phase 13 opened it with the default export settings (High / Medium / 192 kbps in the dialog); a plain Save wrote the
  file byte for byte as Phase 12 had; after Standard / Fast / 256 applied and saved, the file differed only by
  `settings.export`; reopening showed Standard / Fast / 256; the Phase 12 app opened that file too (the property
  ignored).
- R2: Compact / Slow / 320 as a draft (clean), Apply (dirty, the status message), Undo → Standard / Fast / 256, Redo; an
  export through the UI (Project Settings… off while it ran): H.264 640 × 360 25 fps, 850 frames, 34.000 s, the x264
  options `crf=28.0` and `subme=8` (Compact, slow), the ffmpeg command with `-b:a 320000`, an AAC-LC 48 kHz stereo track of
  34.000 s (≈ 144 kbps reached on that tone — not a criterion). The canvas parity at non-default settings is the
  automated `ExportSettingsEndToEndTests` / `ExportCodecLegTests`; the export manual plan's other UI scenarios were run at
  12.9 / 13.6 and no export workflow code changed since (only the job's settings).
- R3 (canvas 1080 × 1080, 30 fps applied first): a track moved and undone, a marker added, Ctrl+C / Ctrl+D on the title,
  Ripple Delete and Undo, `Recent ▾` listing the projects, a file renamed outside → offline on window activation →
  Relink… → linked, Undo (offline again), Redo; saved: the dissolve, the 7 clips with fades and the marker kept, the title
  font scaled 96 → 162 (× 1.6875).
- Final state of the decision: canvas and frame rate are project settings (Project Settings…; D018 default only, D007
  user-changeable); the export settings are an EXPORT section of the same dialog — **a change against the Step 13.2 plan,
  which had them chosen in the export flow**, made by the product owner at Step 13.9; `settings.export` stays an optional
  property of `project.json` v3, a project without it reads as `ExportEncoding.Default`; 320 kbps stays an offered target
  (`-b:a 320000`, a valid track — the bitrate reached is not a criterion); L1-c decided with its measurement kept in the
  suite (`ExportCodecLegTests`); the Phase 8–12 export / parity criteria are unchanged. Phase 14 not started at that point (it followed — D029).
- Kept open after Phase 13 (not Phase 13 work): the `F(end − start)` test helpers; the known flaky CI tests (§8 policy);
  the observation that a new text clip takes `FontSize` 48 on any canvas.

Consequences: `ProjectSettings` becomes user-editable through new undoable commands; the export gains a settings model
used by `FfmpegExportEncoder` (`ExportOutput` and the preflight keep their roles); `project.json` stays v3 or becomes v4
by §5; the parity suite gains scenes for new canvas sizes and rates and per-level codec criteria (§6);
`docs/PHASE13_MANUAL_TEST_PLAN.md` holds the real-app scenarios.

After the merge (recorded at Step 14.2, 2026-10-06): Phase 13 accepted by the product owner on the Step 13.10
verification (closeout `7d4f6d8`); PR #13 merged into `main` as `ed40b74` (2026-10-06), CI green on the pull request
(first attempt). The first CI run on `main` after the merge (run 37470605596) failed on one test of the §8 list —
`ExecutableLocatorTests.RealLocator_FirstCallCancelledMidProbe_SecondCallStillFindsFfmpeg`, `Assert.NotNull() Failure:
Value is null` after 7 s, the known cause (the 5 s PATH probe answering "not found" on a loaded runner); 2584 passed, 2
skipped, 1 failed; not rerun. Kept open after Phase 13, taken into Phase 14 (D029): the known flaky CI tests and the
`F(end − start)` test helpers; deferred (D029 §3): a new text clip's `FontSize` 48 on any canvas.

Status: Accepted (2026-10-06, product owner). Phase 13 complete: Steps 13.1–13.9 accepted (`226c7f2`, `4514f09`,
`ec51247`, `010a1b8`, `66a0871`, `adf85e4`, `3d08c1b`, `5c01aed`); Step 13.10 (closeout, `7d4f6d8`) accepted; merged as
`ed40b74`, CI green on PR #13. Steps and acceptance criteria: `docs/DEVELOPMENT_PLAN.md`, "Phase 13 — Project & export settings: steps".

---

## D029 — Phase 14: stabilization / technical debt

Date: 2026-10-06

Decision (product owner, 2026-10-06, after the Step 14.1 audit). Phase 14 is a **stabilization / technical-debt** phase
with **no new user functionality**: its goal is a more deterministic CI and less technical debt without changing what
the user sees or does. It fixes the known flaky CI tests that D028 §8 only manages, cleans the `F(end − start)` test
helpers with a guard against their return, and decides the empty `src/Effects` project on the basis of an analysis.

Context (Step 14.1 audit, the code at `ed40b74`, `main` after the merge of PR #13):
- Phases 0–13 merged; 2585 tests pass, 2 skip (the 4K scenes), 2587 / 2587 with `AIVE_HEAVY_TESTS=1` (Step 13.10).
- No phase was planned after Phase 13 (ROADMAP, DEVELOPMENT_PLAN, DECISIONS); candidates reported: stabilization /
  technical debt (chosen), keyframes, AI features, colour / HDR, distribution.
- CI needed reruns: the PR #11 run green on attempt 2, the PR #12 run on attempt 3; the first run on `main` after PR #13 failed on
  `ExecutableLocatorTests.RealLocator_FirstCallCancelledMidProbe_SecondCallStillFindsFfmpeg` (D028 "After the merge").
  The D028 §8 list: the autosave timer (`Project.Tests`), an ffprobe timeout in a waveform test, the 5 s PATH probe of the
  ffmpeg / ffprobe locators (`Video.Tests`). Also watched: one unidentified `Project.Tests` hang (Step 8.4) and failure
  (Step 9.3d), never reproduced.
- `ExecutableLocator.IsExecutableAvailableAsync` runs `<tool> -version` with a fixed 5 s timeout; a slower answer is
  "not available", cached for the app run (D024 "Known risk"). The locator already has a probe seam (internal
  constructor) used by tests; `RealLocator_*` tests use the real process.
- Test helpers `TrackEditTests.cs:35`, `RippleEditTests.cs:27`, `TimelineRippleUiTests.cs:42` build a text clip's
  duration as `F(end − start)` instead of `F(end) − F(start)` (D027, a separate cleanup).
- `src/Effects`: see §6.

### 1. Scope

- 14.2 — sync after the merge of PR #13 and this decision (documentation only).
- 14.3 — the flaky autosave-timer test(s) of `Project.Tests` made deterministic; the watched `Project.Tests` hang /
  failure looked for in the stress runs.
- 14.4 — the flaky ffprobe-timeout (waveform) and PATH-probe (locator) tests of `Video.Tests` made deterministic.
- 14.5 — the `F(end − start)` helpers moved to `F(end) − F(start)` with a regression guard.
- 14.7 — `src/Effects`: removed or kept by the product owner's decision on §6's analysis.
- 14.8 — final verification and closeout; CI green without a rerun is the phase's measure.
- (14.6 is not taken — §3.)

### 2. Constraints

- No user-visible behaviour changes. Production code changes only where a test needs a behaviour-neutral seam (e.g. an
  injected `TimeProvider` — part of .NET 8, no new package); each listed with its reason. Anything that would change what
  the user sees — the locator's 5 s probe timeout or its cached "not found", the autosave interval or the recovery
  behaviour, any message — is proposed at its step with its effect and made only after a separate product owner
  confirmation.
- A flaky test is fixed by removing its dependence on timing or the environment, never by a looser bound, a retry
  loop, a skip, a removal or a weaker assertion. Every changed test keeps what it guards, shown by a mutation; its old and
  new form recorded in `progress.md`.
- Unchanged: every decision D001–D028 in substance; the canvas-level parity and the L1-c criteria; `project.json` (v3, no
  new property); the CI workflow's gates (`-warnaserror`, the skip gate). The D028 §8 policy stays in force until the
  closeout decides it.
- Builds and test runs on the product owner's command; push, pull request and merge only with the product owner's
  direct permission; each step accepted before the next one starts.

### 3. Out of scope

All new user functionality; the former candidate **14.6** — a new text clip's default `FontSize` relative to the canvas
and the status bar's "Playing without sound…" after the audio device returns — deferred by the product owner as product /
UX changes; AI features; keyframes; HDR / colour management; an installer; timeline virtualization; an undoable import;
`ProjectSettings.AudioSampleRate` (unused by D013); `MediaAnalysisCoordinator`'s captured `SynchronizationContext`;
the Phase 0–13 known issues not listed in §1.

### 4. Flaky tests and helpers — rules

- Each step starts with the measurement: the failing test named, its failure reproduced or its cause shown from the code
  and the CI logs, before any change.
- Determinism: a test does not depend on real elapsed time or machine load for its verdict, except a test whose purpose
  is a real process or device — that test keeps a real dependency but gets no verdict from a timeout the product does not
  need (e.g. a real locator test checks that a real ffmpeg is found, not the 5 s timeout under load).
- Stress evidence: the affected test classes repeated at least 50 times while the machine is loaded, without a failure,
  at the step and again at the closeout.
- The helpers (14.5) change no expected value; a value that would change is a found defect, reported.

### 5. Measure of the phase

CI green on the Phase 14 pull request without any rerun, on a tree with the fixes of 14.3–14.4. The closeout records
whether the D028 §8 policy can be closed (the product owner's decision).

### 6. `src/Effects` — analysis (Step 14.2; decision at the acceptance of D029)

Facts at `ed40b74`:
- `src/Effects/Effects.csproj` (references only Core) contains one file, `ModuleInfo.cs` — an `internal static class`
  with a `Name` constant and a comment saying the project exists "so the solution's dependency graph is ready" for an
  effect stack; no public type, nothing registered in DI.
- Its only reference is `App.csproj` (`ProjectReference`); no code in any project uses `AiVideoEditor.Effects`; no test
  project references it.
- The effect *data* does not live there: `Effect` and `Clip.Effects` are in Core (`Core/Entities/Clip.cs`), persisted by
  `Project` (`EffectDto`, `ProjectSerializer`) and copied by `Timeline` (`TimelineEditService` copy / paste). Removing the
  project changes none of them and nothing in `project.json`.
- Phase 10 deliberately put fades and dissolves into Core / Timeline / UI, not into this project (D025); ARCHITECTURE's
  module table marks it "Reserved for a generic effect stack … Empty". No plan names a phase that would fill it.
- Recreating a project later is a few minutes' work, so keeping it reserves nothing that would be lost.

Assessment: a dead architectural remnant of the Phase 0 skeleton. Proposal: **remove** it in Step 14.7 (the solution
entry, the `App.csproj` reference and the folder), keep `Effect` / `Clip.Effects` and their persistence untouched,
correct ARCHITECTURE's module table and the project counts (`CLAUDE.md`: 19 → 18 projects, 11 → 10 application
projects). The argument for keeping it — a ready slot in the dependency graph — does not hold against an empty assembly
built and shipped in every run. Decision: removal, accepted with D029 (answer 1 below).

### Open for the D029 acceptance (product owner)

1. `src/Effects`: remove in 14.7 as §6 proposes, or keep (the reason then recorded here)?
2. `tests/ExportEndToEnd.Tests/ExportSettingsEndToEndTests.cs` contains a literal NUL character (a char literal for the
   NUL that ends the x264 SEI string), so Git treats the file as binary (no text diffs). Replace it with the `'\0'`
   escape in 14.5 (behaviour-neutral, one line), or leave it?

Accepted by the product owner (2026-10-06, with Step 14.2), with the answers:
1. `src/Effects` is removed in Step 14.7 — a dead skeleton without functional consumers: the project out of the solution,
   the `App.csproj` reference and the folder removed; ARCHITECTURE and the project counts of `CLAUDE.md` updated. The
   behaviour and the project format must not change.
2. The literal NUL in `ExportSettingsEndToEndTests.cs` is replaced by `'\0'` in Step 14.5; no behaviour change.
3. The failed CI run 37470605596 is not rerun; its fix belongs to Step 14.4.

Refined in Step 14.3 (the autosave timer):
- Failing test, from the CI logs: `AutosaveServiceTests.Timer_autosaves_periodically_and_stop_ends_it` (PR #12 run
  37319512750, attempt 1: 705 ms, `Assert.False() Failure` on its last line — the recovery file was there again after
  `Stop`, a 100 ms pause and a delete). The other CI reruns found in the history (PR #11 attempt 1, Phase 9) were the
  waveform / concurrency tests of `Video.Tests` (Step 14.4).
- Cause — in the product, not only in the test: `Stop` disposed a `System.Threading.Timer`, which does not recall a
  callback already queued (on the thread pool, or posted to the UI thread through the captured
  `SynchronizationContext`); such a late callback started a full autosave after `Stop`. The test hid it behind a guessed
  100 ms ("let an in-flight tick finish"); on a slow runner the late tick came after the delete. The same window exists
  in the app: `ShutdownAsync(keepUnsavedChanges: false)` ("Don't Save" at close) stops the timer and deletes the recovery
  file, and a tick posted just before could write it again — the next start would then offer changes the user chose not
  to keep. This contradicts the documented contract (`IAutosaveService.Stop`: "stops the periodic autosave (an autosave
  already running completes)"; `ShutdownAsync`: "stops autosave and waits for a running one").
- Fix (the contract enforced, its semantics unchanged — the 2-minute interval, the first tick one interval after
  `Start`, ticks on the UI thread, one tick at a time, the recovery rules): `AutosaveService` takes a `TimeProvider`
  (.NET 8 BCL; the public constructor passes `TimeProvider.System`, DI unchanged); each `Start` creates a run token that
  `Stop` clears under a lock, and a tick starts only if its callback carries the current token — so a late callback
  starts nothing and `Stop` never interleaves with the start of a tick; `ShutdownAsync` awaits the running tick through
  the same lock. Product owner's rule respected: no user-visible behaviour is added or changed — only an autosave that
  the contract already excluded no longer happens.
- Tests: the real-timer test replaced by tests on a manual clock (`ManualTimeProvider` and a queueing
  `SynchronizationContext` in `Project.Tests`, no new package): first tick exactly after one interval and then every
  interval, a clean project writes nothing, `Stop` ends it (timer disposed), a callback queued before `Stop`, a tick
  posted to the UI thread before `Stop`, `Shutdown` stops the timer, a late tick after a "Don't Save" shutdown; one
  real-timer test kept (liveness with a 30 s limit; after `Stop` it awaits the running tick instead of guessing a delay).

Refined in Step 14.4 (ffprobe / PATH-probe tests; product owner, 2026-10-06: the app's limits stay, test-only limits
allowed where the limit is not the subject):
- Failures, from the CI logs: `ExecutableLocatorTests.RealLocator_FirstCallCancelledMidProbe_SecondCallStillFindsFfmpeg`
  (run 37470605596 on `main`: `Value is null` after 7 s — the real `-version` probe over its 5 s);
  `WaveformIntegrationTests.A_container_start_time_is_the_origin_of_the_peaks` (PR #12 attempt 1: "FFprobe took too long
  to respond" — the analysis' 20 s `ProcessTimeout`; PR #11 attempt 1: `NullReferenceException` after 33 s — the audio
  decoder's 20 s `FirstFrameTimeout`, the waveform then `null`); in Phase 9 "FFprobe could not be found" (a fresh
  locator's 5 s probe).
- Cause: tests whose subject is not a limit got their verdict from the app's limits on a loaded runner — the 5 s PATH
  probe (a new `FfprobeLocator` per analysis in seven test helpers; the real locator test), the 20 s ffprobe run, the 20 s
  first decoded frame. Also latent: `FfmpegTools` probed PATH once with 5 s, and a miss there would have skipped every
  ffmpeg test (a CI failure through the skip gate).
- Fix: the app unchanged — `ExecutableLocator.DefaultProbeTimeout` 5 s, `ProcessTimeout` 20 s, `FirstFrameTimeout` 20 s,
  the cached "not found". Test seams (internal): `ExecutableLocator` / `FfmpegLocator` / `FfprobeLocator` take a probe
  limit (one source, `ProbeTimeout`, also used by `ProbeAsync` of the resolution); `IsExecutableAvailableAsync` takes the
  limit; `Infrastructure` visible to `ExportEndToEnd.Tests`. `FfmpegTools` (shared by `Video.Tests` and
  `ExportEndToEnd.Tests`): one ffmpeg and one ffprobe locator per run with a 60 s probe limit, `Analysis()` (120 s
  `ProcessTimeout`) and `AudioDecoderSettings` (120 s first frame) for tests whose subject is not the limit — the seven
  helpers, the waveform tests and the real locator test use them. Tests of the limits keep their short ones
  (`FfmpegDiagnosticsTests`, `FfprobeTerminationTests`, `AnalysisConcurrencyIntegrationTests`, the seam tests); new
  `ExecutableProbeTimeoutTests`: the app's 5 s (both locators), the seam changing only its locator, the app's 20 s / 20 s
  and the helpers' limits, a scripted tool slower than a 500 ms limit counted missing and within a long one available —
  through the process probe and through a locator. The result checks of the waveform / analysis tests unchanged.

Refined in Step 14.5 (test helpers and the NUL):
- The `Text` helpers of `TrackEditTests`, `RippleEditTests` and `TimelineRippleUiTests` build the duration as
  `F(end) − F(start)`. Of their 42 calls, 6 had a clip end one tick off the grid at 30 fps with `F(end − start)`; every
  expected value of these classes reads edges through `ToNearestFrame` or compares snapshots, so none changed — the
  36 existing tests pass as before. A guard test per class runs the helper over every span of 1–30 frames from starts
  0–29 and asserts both edges on the grid (and that the rate makes the formulas differ); the old formula fails it in
  each class.
- `ExportSettingsEndToEndTests.cs`: the literal NUL in a char literal replaced by `'\0'`; Git reads the file as text again
  (stored with LF like every other text file — before, as a "binary" file, it was kept with CRLF); the test unchanged.

Refined in Step 14.7 (`src/Effects` removed, answer 1): the project left the solution (`dotnet sln remove`: its entry,
its four configuration lines and its solution folder line) and `App.csproj` (the only reference); `src/Effects`
(`Effects.csproj`, `ModuleInfo.cs`) deleted. Untouched: `Effect`, `Clip.Effects` (Core), `EffectDto` and the
`effects` array of `project.json` (Project), the copy of a clip's effects (Timeline). The solution now has 18 projects
— 10 application, 8 test (`CLAUDE.md`, ARCHITECTURE, README corrected). Debug and Release build 0 / 0.

Closeout (Step 14.8, 2026-10-07, at `90caae9`):
- Builds `--no-incremental -warnaserror`: Release 0 / 0, Debug 0 / 0 (18 projects).
- Tests (TRX + the CI gate script each time): the full suite 2600 passed, 2 skipped (only the two 4K scenes), 0 failed;
  three `--blame-hang --blame-hang-timeout 5m` runs 2600 / 2 / 0 each, no hang, no dump; with `AIVE_HEAVY_TESTS=1` 2602
  passed, 0 skipped, 0 failed. No retry, no rerun; no test weakened, skipped or re-baselined.
- Manual regression (`docs/PHASE14_MANUAL_TEST_PLAN.md`, against the Phase 13 build `ed40b74` built apart): R1 a Phase 13
  project with non-default canvas, rate and export settings opens with them and saves byte for byte; R2 the default export
  byte-identical to Phase 13's; R3 timer autosave, crash, recovery; R4 ffmpeg found / missing — the same behaviour and
  messages as Phase 13; R5 every Phase 7–13 clip property and `effects` kept, the saved file and the export identical to
  Phase 13's. All PASS. No user-visible change found.
- Final scope: 14.2, 14.3, 14.4, 14.5, 14.7, 14.8 done; 14.6 (a new text clip's font size, the audio status after a
  device returns) not taken — out of scope (§3), not unfinished work. The D028 §8 policy stays in force until the CI run
  of the Phase 14 pull request shows §5 (green without a rerun); its result is recorded there.

Consequences: CI no longer needs reruns for the known tests; D028 §8 can be closed at the closeout; the test helpers
produce on-grid clips; one project fewer in the solution. No change for the user.

After the merge (recorded at Step 15.2, 2026-10-07): Phase 14 accepted by the product owner; PR #14 (head `0482b80`)
merged into `main` as `7200976` (2026-10-07; its tree identical to `0482b80`). CI green on the **first attempt** on the
pull request (run 37590085671) and on the first run on `main` after the merge (run 37590661647) — the §5 measure met. The
D028 §8 policy is closed (product owner, 2026-10-07; D028 §8 "Closed"). On `main` after the merge: build 0 / 0, the full
suite 2600 passed, 2 skipped (the 4K scenes), 0 failed (the Phase 15 audit, Step 15.1). Taken into Phase 15 (D030 §9):
the two §3 items — a new text clip's font size relative to the canvas and the audio status after the device returns.

Status: Accepted (2026-10-06, product owner). Phase 14 complete: Steps 14.1–14.7 accepted (`0a50fe5`, `ed35f1f`, `0a4e6fe`,
`b8e6aac`, `90caae9`); Step 14.8 closeout (`0482b80`) accepted; PR #14 merged as `7200976`, CI green on the first attempt
on the pull request and on `main`. Steps and acceptance criteria: `docs/DEVELOPMENT_PLAN.md`, "Phase 14 —
Stabilization / technical debt: steps".

---

## D030 — Phase 15: editing tools — trim, track controls, slip, In / Out range

Date: 2026-10-07

Decision (product owner, 2026-10-07, after the Step 15.1 pre-analysis; variant A in full). Phase 15 adds the editing
tools that the everyday workflow of a bouldering video still lacks — cutting long phone recordings of attempts down to
the part that matters: **track controls** (mute, hide, lock; Step 15.3), **trim to the playhead**, plain and ripple
(15.4), **ripple trim by dragging an edge** (15.5), **slip** (15.6), a **timeline In / Out range** for loop playback and
range export (15.7), and the two UX fixes deferred by D029 §3 (15.8). Every project change is one undoable command
through `ITimelineEditService`; the rendering rules are unchanged, so the Preview and the export stay identical by
construction; `project.json` stays `formatVersion` 3 with no new property.

Context (Step 15.1 pre-analysis, the code at `7200976`, `main` after the merge of PR #14):
- Phases 0–14 merged; build 0 / 0; 2600 tests pass, 2 skip (the 4K scenes); CI green on the first attempt on PR #14
  and on `main`; 18 projects (10 application, 8 test).
- `Track.IsMuted` / `IsHidden` / `IsLocked` exist in the model and in `project.json` v3 (`TrackDto`), the playback
  snapshot honours them (`PlaybackSnapshotBuilder`: a hidden video track has no layer and no dissolve zones; a muted
  video track gives no sound from its video clips; a muted audio track is not mixed) and the edit service rejects edits
  on a locked track — but **no command sets them and no UI shows them**: a user cannot mute, hide or lock a track.
- Trim exists only as a drag of a clip edge (`TrimClip`, clamped — D008 / D022 / D025); there is no ripple trim, no trim
  to the playhead and no slip; D027 §1 left ripple trim and an In / Out range out of Phase 12.
- Loop (D024 Step 9.6, PO-H3) plays the whole sequence; the export always covers the whole sequence (`ExportJob` has no
  range). The keys `I`, `O`, `Q`, `W` are free (`Ctrl+I` is Import); the timeline uses `Ctrl` for toggle-select and
  `Ctrl`+wheel for zoom; `Shift` and `Alt` are unused by its gestures. The timeline header has no room left at 1024 px
  (D027 Steps 12.6 / 12.7); the UI has no context menus.
- A new text clip takes `FontSize` 48 on any canvas (`Clip.cs`); the status bar keeps "Playing without sound: no audio
  output is available." after the sound comes back (`PreviewViewModel` reports it once and never clears it).

### 1. Scope (locked by the product owner, 2026-10-07)

1. Track controls (15.3): mute, hide, lock — undoable commands with the D015 dirty / save-point semantics, persisted in
   the existing v3 track fields; playback and export respect them; a locked track rejects every ordinary edit.
2. Trim to the playhead (15.4): trim start / trim end to the playhead; a plain trim leaves the resulting gap, a ripple
   trim moves the later clips of the same track; the frame grid, the source mapping / speed rules and the fade /
   dissolve rules (D022, D025) preserved; a locked track rejects it.
3. Ripple trim UI (15.5): dragging an edge with the agreed modifier; the result previewed during the gesture; `Esc`
   cancels; the same semantics as the command of 15.4.
4. Slip (15.6): the source content inside a clip changes while its timeline boundaries stay; source limits and speed
   defined and tested explicitly; a locked track rejects it.
5. Timeline In / Out range (15.7): `I` sets In at the playhead, `O` sets Out at the playhead, a clear reset operation,
   the range shown on the timeline, loop playback uses the range when one is defined, the export can export only the
   range.
6. The D029 §3 UX fixes (15.8): a new text clip's default `FontSize = 48 × canvasHeight / 1080`; the stale "Playing
   without sound…" status reset when the sound is available again.

### 2. Constraints

- Unchanged in substance: D006 / D007 (frame grid, rate), D008 (no overlap on a track, one-frame minimum, trims clamped),
  D009 / D022 (source frame selection, the speed timing rule), D013 (mix), D014 / D016 (persistence, autosave,
  recovery), D015 (dirty / save point; session state), D018 / D023 (composition, export), D025 (fades, dissolves), D026,
  D027 (ripple delete, tracks, markers), D028 (settings, L1-c criteria). A rule of those decisions is applied as written;
  where Phase 15 needs a rule they do not state, it is derived from them, written here (§10) and confirmed by the
  product owner before it is implemented — never invented silently.
- `project.json` stays `formatVersion` 3, **no new property**: the track flags already exist; the In / Out range is not
  saved (§8). A step that finds it needs a format change stops and asks.
- The Preview ↔ Export parity suite and the L1-c criteria are never weakened, re-baselined or removed; new behaviour
  that reaches the picture or the sound (track flags, slip, range export) gets parity scenes.
- Every new project command is one undo step, is disabled while an export runs (`EditingLock`) and is rejected on a
  locked track, like the existing edits; a refused edit changes nothing and leaves no undo step.
- Keyboard (§11): `I`, `O`, `Q`, `W` as locked; no other new key unless confirmed; never while a text input has focus
  (the `ShortcutRouter` guard).
- No UI redesign: new controls fit the existing panels; the 1024 px minimum width is checked in the real app for every
  new control.
- Working rules (kept from Phases 11–14): builds and test runs on the product owner's command; no push, pull request or
  merge without direct permission; each step accepted before the next one starts; a step's sub-decisions proposed at
  its start and confirmed before its code changes.

### 3. Out of scope (explicit non-goals)

Roll edit; a source viewer / source editing; insert / overwrite editing; ripple across all tracks; track solo;
keyframes; text outline / shadow / background; dragging objects directly in the Preview; unlinking audio, J / L cuts,
audio crossfades; freeze frame; reverse; variable speed ramps; marquee selection; marker labels; filmstrips; frame
export; AI features; HDR / colour management; an installer; timeline virtualization; an undoable import.

Note (Phase 15 closeout): §§4–9 and "Open for the D030 acceptance" are the Step 15.2 proposals, kept as written. Where an
answer or a "Refined in Step 15.x" block below differs (e.g. Q5: the playhead moves after `Shift+Q`; Q11: no key clears
the range; Q4: a plain `Q` / `W` does not trim a dissolve's cut edge), the answer and the refinement are binding; the
summary "Final state at the Phase 15 closeout" lists the accepted result.

### 4. Track controls (Step 15.3)

- Video tracks: **mute** (the sound of their video clips), **hide** (their picture: clips, texts and dissolve zones),
  **lock**. Audio tracks: **mute**, **lock** — `IsHidden` has no effect on an audio track (the snapshot ignores it), so
  no hide control is offered there. The existing snapshot rules are the semantics; nothing in the composition or the mix
  changes: hide is picture only (a hidden video track's clips still sound unless the track is muted), mute is sound
  only.
- Each toggle is one undoable project change (one command per click, no merging), marks the project dirty, and Undo /
  Redo back to the save point make it clean (D015); saved in the existing `isMuted` / `isHidden` / `isLocked` of the
  track — a file from Phase 14 or earlier opens with its flags, a file saved by Phase 15 opens in Phase 14 with the same
  rendering (the flags existed and were honoured).
- Playback and export: the Preview and the export read the same snapshot, so a hidden track is absent from both, a
  muted track silent in both. The sequence length is unchanged by hide / mute (`Sequence.Duration` counts every clip;
  a hidden track's content past the other tracks' end exports as black, as an empty track would). The export preflight
  checks what the snapshot exports (it reads the snapshot's spans): offline media used only by a hidden and muted track
  does not block the export.
- Lock: every ordinary edit of `ITimelineEditService` on a clip of a locked track, or on the track itself (move, delete),
  is rejected with "Track … is locked." (existing); new Phase 15 edits (trim to the playhead, ripple trim, slip) too.
  Undo / Redo replay recorded commands and are not blocked by a lock set later (existing behaviour: the lock is checked
  when an edit is planned). Copy from a locked track stays allowed (D027 Step 12.6). Markers are not on a track and are
  not affected. Selection on a locked or hidden track stays possible.
- The toggles live in the track header (84 px column, next to ▲ / ▼ / ✕); their layout is checked at 1024 and 1440 px;
  disabled while an export runs. A hidden track's clips are drawn dimmed, a locked track is marked; a muted track's
  waveforms are already dimmed (D024 Step 9.5).
- Proposed default, confirmed at 15.3 (§ "Open" Q13): mute and hide stay toggleable on a locked track (lock protects
  the clips and the track's place, not its monitoring state).

### 5. Trim to the playhead (Step 15.4)

Terms: the playhead is on frame `p` (it is always on the grid); a clip covers frames `[s, e)`.
- Target (proposed, Q1): the **selected** clips whose frames contain `p` strictly inside, `s < p < e` — at most one per
  track (no overlap, D008). Selected clips that do not contain `p` are not trimmed; none containing it → refused with a
  status message, nothing changes.
- **Trim start to the playhead** (`Q`): the clip's new start is `p`; the frame `p` stays the clip's first frame, the
  frames `[s, p)` are removed. **Trim end to the playhead** (`W`): the clip's new end is `p`; the frames `[p, e)` are
  removed (the playhead frame is the first frame after the clip — the same cut point as Split at the playhead, D008).
- **Plain** (gap left): exactly the existing `TrimClip` of that edge to `p` — the same planner, the same clamps and the
  same results as dragging that edge to `p`. Since `s < p < e`, the trim is always inward: the neighbour and source
  clamps never apply and the one-frame minimum holds by construction; only the dissolve clamp of the far edge can apply
  (below). The content does not move on the timeline (a start trim moves `SourceIn` so the frame at `p` stays where it
  is — D008 / D022).
- **Ripple**: the same trim of the clip's source range and length, but the clip keeps its start frame `s` (a ripple
  trim start places the shortened clip back at `s`, so it shows from `s` the content that was at `p`); every clip of
  **the same track** that starts at or after the trimmed clip's original end moves by the change of the clip's length
  (left for a shorter clip), in whole frames with the move's timing rule (D027 Step 12.5, `PlanShift`) — the following
  clips close up. Gaps between the
  later clips are kept (they move with the clips after them); the gap before the trimmed clip is untouched. No overlap
  can arise (all later clips move by the same distance). Clips of other tracks, the markers and the In / Out range do
  not move (D027 §2: ripple only on the edited track; sync with other tracks may change, as for ripple delete).
- Source mapping and speed (D022, applied as written): 1× — a start trim moves `SourceIn` by
  `FromFrame(s + Δ) − FromFrame(s)`, an end trim sets `SourceOut = SourceIn + duration`; speed `≠ 1×` — the end trim
  sets `SourceOut = SourceIn + SourceLength(N)`, the start trim moves `SourceIn` by `SourceLength(Δ)` and keeps
  `SourceOut`, normalized only when the rounding misses the invariant. A ripple shift changes neither the length, the
  source range nor the speed of the shifted clips.
- Fades (D025 §2, applied as written): a fade stays attached to its edge — a trimmed start keeps the clip's fade in; an
  edit that leaves the clip shorter than a stored fade cuts that fade to the clip's length in the same undo step
  (`EditPlan.ClampFades`). This is **not** Split + Delete (a split gives the inner edges no fade). The shifted clips'
  fades are unchanged.
- Dissolves (D025 §5, D027 §2):
  - plain trim of a **cut edge** (A's end, B's start): it opens a gap → the dissolve is removed in the same undo step
    with D025's status note (existing);
  - plain or ripple trim of a **far edge**: the cut is kept; the trim is clamped so the dissolve's part inside the clip
    still fits (existing `minFrames`); proposed (Q6): the clamped result is applied and the status bar says the trim
    stopped where the dissolve needs its frames;
  - ripple trim of a **cut edge**: the clips still meet after the ripple (A's end and B move together; B keeps its start
    next to A) — a case D025 does not name. Derived from D025 §5's general rule ("an edit that keeps the cut but would
    break the dissolve is rejected — except a trim, which is clamped"): the dissolve is **kept**, the trim clamped so the
    zone fits the shorter clip and the handles still suffice; proposed for confirmation (Q4);
  - dissolves between later clips of the track move with them unchanged (D027 §2.2); none is created (D025);
  - the zones and the handles are validated by the existing `Validate` / `ReconcileTransitions` on the planned result.
- Playhead (proposed, Q5): stays where it is for `Q`, `W` and the ripple trim end (it then shows the new first frame of
  the clip, resp. the edit point); after a ripple trim start it moves to the clip's start `s` (the edit point, where the
  frame that was at `p` now is) — unlike D027's ripple delete, which never moves it.
- Undo / Redo: one step for the whole command (every trimmed clip, every shifted clip, every fade cut and dissolve
  change); Undo restores every tick exactly.
- Hidden / muted tracks: no influence on trimming (they are not locked); a locked track's selected clip refuses the
  whole command (as `CheckEditable` does for every multi-clip edit).
- How the ripple variant is reached from the keyboard / UI: Q2.

### 6. Ripple trim by dragging (Step 15.5)

- An edge drag with the ripple modifier (proposed `Shift`, Q3) plans a ripple trim of that edge to the pointer's frame
  through the **same planner** as §5 (the playhead replaced by the target frame, inward or outward); without the
  modifier the existing trim is unchanged.
- Outward with ripple: the clip lengthens and the later clips move right; the neighbour clamp does not apply (the
  neighbours move), the source limits do (`SourceIn ≥ 0`, the end of the source: D006 `MaxWholeFrames` / D022
  `FramesFor`), and for a cut edge with a dissolve the handle the dissolve needs (§5).
- Snapping as for the existing trim gesture; the gesture preview shows the trimmed clip and the moved later clips (and
  the zones) as the release will leave them; `Esc` cancels with no change and no undo step; a release that changes
  nothing leaves no undo step.
- Acceptance includes an equivalence check: for the same clip, edge and target frame, the drag's result equals the
  command's result tick for tick.

### 7. Slip (Step 15.6)

- Slip by `k` frames: the clip keeps `TimelineStart`, its frame count `N`, its speed, fades and properties; its source
  range moves — `SourceIn' = SourceIn + Δ`, where `Δ` is exactly how a start trim by `k` frames would move `SourceIn`
  (1×: `FromFrame(s + k) − FromFrame(s)`; `≠ 1×`: `±SourceLength(|k|)` — D022), and `SourceOut'` follows the clip's
  rule (1×: `SourceIn' + duration`; `≠ 1×`: `SourceIn' + SourceLength(N)`, normalized like D022's trim start).
- Limits: `SourceIn' ≥ 0` and the range ends within the source (the same limits as the trims: D006 `MaxWholeFrames`,
  D022 `FramesFor`); a dissolve on either edge keeps its source handle (D025 §4) — slip keeps the cuts, so by D025 §5 an
  edit that would break a dissolve is rejected; the gesture clamps to the allowed range, which the service reports (a
  query like `MaxTransitionFrames`), so a drag never ends in a refusal.
- Only video and audio clips slip (a video clip's sound slips with its picture); images and text have no source range →
  refused. A clip whose source duration is unknown is refused. Offline media with saved metadata can be slipped.
- A slip is a timing change (the snapshot's `SourceIn` changes, decoders reopen), never presentation-only; the export
  after a slip matches the Preview (parity scene).
- One undoable step per gesture (the drag commits once on release); `Esc` cancels; locked track → refused.
- UI (proposed, Q3 / Q12): `Alt` + drag on the clip body; dragging right shows earlier source content (the content moves
  with the pointer); during the gesture the timeline shows the new source in / out; the Preview shows the result after
  the release.

### 8. Timeline In / Out range (Step 15.7) — transient session state

- The range is **session state of the timeline view, not project state**: it is **not serialized** — no field in
  `project.json`, none in the recovery file, no format change. Setting or clearing it never makes the project dirty,
  never triggers an autosave and never enters undo / redo. Opening a project (Open, Recent, Recover, New) starts with
  **no** range; reopening the same project starts with none. (Unlike the playhead, which D015 saves in `project.json`
  as session state, the range is not saved at all — the product owner's decision.)
- `I` sets In at the playhead's frame, `O` sets Out at the playhead's frame (proposed, Q7: Out is the last frame of the
  range, inclusive — the range covers `[In, Out + 1 frame)`, so `I` and `O` on one frame give a one-frame range). Only
  In set: the range runs to the end of the sequence; only Out: from 0. Setting In at or after Out (or Out before In)
  clears the other point (proposed, Q8). A clear operation removes both (Q11).
- Edits never move In / Out (like markers, D027 §2); after a frame-rate change they keep their time, and "on a frame"
  means the nearest frame of the new grid (like markers, D027 Step 12.7). A range past the end of the sequence is kept
  and clamped where it is used.
- Shown on the timeline: a band on the ruler and a light shade over the tracks; not a snap target in Phase 15 (proposed).
- Loop (proposed, Q9): with Loop on and a range, playback reaching Out continues from In, and Play with the playhead
  outside the range starts at In; with Loop on and no range, the whole sequence loops (unchanged); with Loop off the
  range does not affect playback.
- Range export (proposed, Q10 / Q16): when a range exists, Export asks whether to export the range or the whole
  sequence. The range export renders timeline frames `[In, Out)` (clamped to the sequence) with the unchanged
  composition — output frame 0 is timeline frame In; fades, dissolves and speed clips crossing a boundary are rendered
  as they are at those frames — and the sound of timeline samples `[⌈In · 48000 / 10⁷⌉, ⌈Out · 48000 / 10⁷⌉)` (the
  `AudioPlacement` rule); duration `Out − In`. A range that is empty after clamping is refused with a message. The
  preflight checks the media used inside the range. Parity: the range export's frames and samples equal those of the
  whole timeline at the same timeline positions.
- The range belongs to the current project in the session: cleared on New / Open / Recover; available during an
  export (not a project change), but the running export keeps the range it started with.

### 9. UX fixes deferred by D029 §3 (Step 15.8)

- A new text clip (`AddTextClip`) takes `FontSize = 48 × canvasHeight / 1080` — exactly this formula, in `double`
  (1080 → 48, 2160 → 96, 720 → 32, 1920 → 85.333…), within the existing limits 1 … 1000 (`ClipPropertyLimits`) for
  every canvas the project settings accept (`ProjectSettingsRules.CanvasError`: 64 … 4096 px a side), with no clamp of
  its own (a hand-written file's out-of-range canvas is not guarded by new validation, as with D028's scaling); existing
  clips and the D028 canvas-size scaling (`ContainFactor`) are unchanged; a project file is unchanged (the value is
  stored as any font size). See Q14 for the difference to the canvas scaling.
- Audio status: when playback has sound again after "Playing without sound: no audio output is available." was
  reported, that status is cleared if it is still the one shown (another message since then is kept), and the report is
  re-armed so that a later loss is reported again (proposed, Q15). No change to the playback, the device handling or
  the mix.

### 10. Existing semantics that every Phase 15 edit preserves (inventory for Steps 15.4–15.6)

Collected from D006, D007, D008, D015, D022, D025 and D027 before any implementation; each item gets a test in the step
that touches it (the 15.4 / 15.6 test lists map to this table).

| Area | Rule (source) | Phase 15 consequence |
|---|---|---|
| Frame grid | Every clip edge on the project grid; edges from frame indices (D006) | Targets are frames (playhead / pointer frame); shifts in whole frames (`PlanShift`) |
| Source mapping 1× | `SourceOut = SourceIn + duration`; a start trim moves `SourceIn` with the edge so the content under the playhead stays (D008, D022) | Plain trim: content does not move; ripple: content moves with the clip; slip: §7 |
| Constant speed | One rounding rule `SourceLength(N)` / `FramesFor`, invariant `SourceLength(N) ≤ SourceOut − SourceIn < SourceLength(N+1)`, normalization by the smallest change (D022) | The trim / slip planners use it; ripple shifts keep length, range, speed |
| Minimum length | One frame (D008); larger when a dissolve part must fit (D025 §5) | `s < p < e` guarantees ≥ 1 frame; the dissolve clamp applies |
| Source limits | `SourceIn ≥ 0`, the end of the source (`MaxWholeFrames`, `FramesFor`) (D006, D022) | Outward ripple drag and slip clamp to them |
| Neighbours | No overlap on a track; trims clamp to neighbours (D008) | Plain trims to the playhead are inward (never clamp); ripple moves later clips instead of clamping |
| Fade truncation | Fades follow their edges; an edit leaving the clip shorter than a fade cuts it in the same step; a longer clip later does not restore it (D025 §2) | Applied to trimmed clips; shifted clips unchanged; slip never changes fades |
| PO-8 | A fade on an edge with a dissolve is inactive, kept stored (D025) | Unchanged; becomes active again when a trim removes that dissolve |
| Dissolve at a trimmed edge | Cut edge trimmed → gap → removed; far edge → clamped; handles validated for changed dissolves (D025 §5, Step 10.6) | Plain trim as is; ripple of a cut edge keeps the cut → kept and clamped (Q4) |
| Adjacent clips | Ripple moves clips at or after the end of the edited span; gaps keep their size (D027 §2) | Same rule for ripple trim |
| Locked tracks | Every edit on a locked track rejected, nothing changed, no undo step (D008, D027 §3) | All Phase 15 edits; the track toggles themselves per §4 |
| Hidden / muted | Not an edit restriction; snapshot only (existing) | Edits on hidden / muted tracks work |
| Markers | Never moved by any edit (D027 §2, Step 12.7) | Not moved by ripple trim, slip or range |
| Undo / redo | One command per edit, absolute before / after, exact restore; dirty by the save point (D008, D015) | One step per command / gesture; refused or unchanged → no step |
| Other tracks | Ripple only on the edited track (D027 §1) | Ripple trim only on the trimmed clip's track |
| Existing gaps | Kept, move with the clips after them (D027 §2) | Same; the gap before the trimmed clip is untouched |
| Session state | Playhead, zoom, snapping are session state (D015) | In / Out is session state and not saved at all (§8) |

### 11. Keyboard

`I` set In · `O` set Out · `Q` trim start to the playhead · `W` trim end to the playhead (plain). No separate key for
the ripple trim unless the product owner confirms an explicit modifier variant (Q2). Keys act only when no text input
has focus (`ShortcutRouter`); `Ctrl+I` stays Import.

### Open for the D030 acceptance (product owner) — all answered (Steps 15.3–15.8, below)

Semantic questions that the existing decisions do not answer; each carries the proposal above and is confirmed (or
changed) before the step that implements it starts:

1. Q1 — target of `Q` / `W`: the selected clips that contain the playhead strictly inside (proposed), or, with no
   selection, the clip under the playhead on some track?
2. Q2 — reaching the ripple trim to the playhead: `Shift+Q` / `Shift+W` as an explicit modifier on the same keys
   (proposed), or only a UI command (the header has no room at 1024 px and there are no context menus)?
3. Q3 — drag modifiers: `Shift` + edge drag = ripple trim, `Alt` + body drag = slip (proposed; `Ctrl` is toggle-select).
4. Q4 — ripple trim of a cut edge that has a dissolve: keep the dissolve, clamped to fit (proposed, derived from D025
   §5), or remove it like a plain trim of that edge?
5. Q5 — playhead after a ripple trim start: moves to the clip's start (proposed) or stays (as after a ripple delete)?
6. Q6 — `Q` / `W` stopped short by a dissolve on the far edge: apply the clamped trim with a status message (proposed,
   as the drag clamps) or refuse?
7. Q7 — `O` inclusive (the playhead frame is the range's last frame; proposed) or exclusive?
8. Q8 — In set at / after Out (or Out before In): clear the other point (proposed) or refuse?
9. Q9 — loop with a range as §8 (proposed)?
10. Q10 — choosing a range export: Export asks "In / Out range" / "Whole sequence" / "Cancel" when a range exists
    (proposed), or another way?
11. Q11 — clearing the range: a ✕ on the range band of the ruler plus a key (e.g. `Alt+X`; proposed), or a button
    elsewhere?
12. Q12 — slip feedback: source in / out shown during the drag, the Preview after the release (proposed), or a live
    Preview during the drag?
13. Q13 — mute / hide toggleable on a locked track (proposed) or blocked by the lock?
14. Q14 — the locked `48 × canvasHeight / 1080` differs from D028's canvas scaling (`ContainFactor`): a text made at
    48 on 1920 × 1080 becomes 27 after a change to 1080 × 1920, while a new text there gets 85.33. Keep both as they are
    (proposed — the formula is locked; the scaling is D028), and keep the exact value (no rounding)?
15. Q15 — audio status: clear it silently when the sound returns (proposed) or show a "sound is back" message?
16. Q16 — the preflight of a range export checks only the media used inside the range (proposed)?

Answered at the D030 acceptance / the start of Step 15.3 (product owner, 2026-10-07): Q13 — mute and hide may be changed
on a locked track; the lock blocks ordinary editing of clip / timeline content, not the track's mute / hide state. Also
confirmed: video tracks expose mute, hide, lock; audio tracks mute and lock, no hide control; the flags stay in the
existing v3 fields (no format change); hidden is picture only, muted sound only, the sequence length unchanged; the lock
is checked when an edit is planned, Undo / Redo are not blocked by the current lock state; every track-state command one
undo step with the dirty / save-point semantics, disabled during an export.

Refined in Step 15.3 (implementation, 2026-10-07):
- `ITimelineEditService.SetTrackMuted` / `SetTrackHidden` / `SetTrackLocked(trackId, value)` → one
  `SetTrackStateCommand` (Timeline: the track, the flag, the new value; Undo writes the value it found), wrapped in the
  usual `NotifyingCommand`, so the timeline, the Preview's snapshot and the waveforms refresh. Descriptions "Mute Track" /
  "Unmute Track", "Hide Track" / "Show Track", "Lock Track" / "Unlock Track". The same value → `NoChange`, no undo step;
  a missing track → "That track no longer exists."; hiding an audio track → "Track A1 is an audio track: it has no
  picture to hide." (nothing changes). No lock check in these commands (Q13).
- No rendering, mix, preflight or format code changed: the snapshot rules of §4 were already in place; a hidden or
  muted track and a locked track export as the Preview shows them (new end-to-end scenes); a file with flags set by
  Phase 15 opens in the Phase 14 build with the same rendering and is saved by it byte for byte.
- UI (the track header, 84 px column unchanged): the name on the left, small toggle buttons on the right of the same
  line — **M** (mute), **👁** (hide; video only), **🔒** (lock) — dim when off; when on, filled: mute red, hide blue,
  lock amber; ▲ / ▼ / ✕ stay on the second line. A locked lane is tinted amber-dark, a hidden lane's clips are drawn at
  35 % opacity. The lock emoji after the track name (`Label`, since Phase 4) is gone — the 🔒 toggle shows it. Status
  messages "Track V1 muted / unmuted / hidden / shown / locked / unlocked". The buttons are `Button`s with a state class
  (not `ToggleButton`s), so a refused or disabled click can never leave a toggle out of step with the model.

Answered at the start of Step 15.4 (product owner, 2026-10-07):
- Q1 — `Q` / `W` work only on the selected clips; a selected clip is eligible only when the playhead's frame is strictly
  inside its timeline interval; with no eligible clip nothing changes and the status says why; a clip under the playhead
  is never selected implicitly. Several eligible clips follow the existing multi-selection convention (as Split: every
  eligible selected clip, one undo step; the others are left as they are).
- Q2 — `Q` plain trim of the start, `W` plain trim of the end, `Shift+Q` / `Shift+W` the ripple variants.
- Q4 — a trimmed edge that is a dissolve's cut keeps its dissolve; the trim is limited to what keeps the dissolve valid;
  a dissolve is never removed silently. For a **plain** `Q` / `W` this means (confirmed separately, the product owner's
  choice between this and D025 §5's removal): any plain inward trim of a cut edge would open a gap, so that clip's edge
  is **not trimmed** (its limit is 0) and the status says to use `Shift+Q` / `Shift+W` or remove the dissolve first; the
  other eligible selected clips are still trimmed; if none is, nothing changes and no undo step is made. This
  supersedes, for `Q` / `W` only, §5's "plain = exactly the existing `TrimClip`" on a cut edge; the edge drag
  (`TrimClip`) keeps D025 §5 (the dissolve is removed with the status note).
- Q5 — after a successful ripple trim of the start the playhead goes to the clip's new start (= its unchanged start);
  with several clips to the earliest of their starts (confirmed separately). A ripple trim of the end leaves it.
- Q6 — a trim that reaches the frames a dissolve needs (on the far edge, or for a ripple on either edge) is applied up
  to that limit, not refused, and the status says "The trim stopped where a dissolve needs the clip's frames."; the clip
  and the dissolve stay valid.

Refined in Step 15.4 (implementation, 2026-10-07):
- `ITimelineEditService.TrimToPlayhead(clipIds, edge, playhead, ripple)` → one `EditPlan`, one undo step ("Trim Start
  to Playhead", "Trim End to Playhead", "Ripple Trim Start to Playhead", "Ripple Trim End to Playhead"); the new
  `TimelineEditResult.Playhead` carries Q5's position (session state, applied by the timeline view model as a seek).
  Refusals change nothing and leave no step: no clip selected ("Select the clip to trim: the playhead must be inside
  it."), none containing the playhead ("The playhead is not inside the selected clip(s)."), a locked track of an
  eligible clip ("Track V1 is locked."), or every eligible clip stopped at 0 by a dissolve.
- The limit per clip: `allowed = min(wanted, N − minFrames)` with `minFrames = max(1, parts)` — for a plain trim the far
  edge's dissolve part (`DissolveParts`, D025 §5's zone fit, as the edge drag), for a ripple the parts of both edges
  (the trimmed edge's cut and dissolve stay); a plain trim of a cut edge: 0 (Q4). An inward trim never shortens a
  dissolve's source handle, so the limit is the zone fit; the result still goes through `Validate` (zones, handles,
  fades), like every edit.
- The trimmed clip's timing is the edge trim's own rule, factored out of `PlanTrim` as `TrimmedState` (no behaviour
  change of the drag; its tests unchanged): 1× `SourceIn` moves with the edge, `≠ 1×` `SourceLength` / `Normalize`
  (D022). A ripple of the start then moves the trimmed clip back to its start with the move's rule (`ShiftedState`,
  which now also takes a planned state); the later clips of the same track — those starting at or after the clip's old
  end — move left by the trimmed frames (`PlanShift`), gaps between them unchanged. Only the trimmed clip's own track
  moves; it is unlocked (checked first), so a ripple never touches a locked track; other tracks (locked or not) and the
  markers stay.
- Fades follow D025 §2 (`ClampFades` in `Validate`): they stay on their edges and are cut to a shorter clip; this is not
  Split + Delete.
- Keys in `ShortcutRouter` with exact modifiers (`Q` / `W` none, `Shift+Q` / `Shift+W` Shift; Ctrl / Alt variants are
  no shortcut), so one key press runs one command; never while a text box has focus. No new button (the header has no
  room at 1024 px); the status bar names the done trim ("Trimmed the start of the clip to the playhead", "Ripple trimmed
  the end of 2 clips to the playhead", …).

Answered at the start of Step 15.5 (product owner, 2026-10-07): Q3 — an ordinary edge drag keeps the existing trim
exactly (D008 / D022 / D025 §5, including the removal of a dissolve whose cut the drag opens); **Shift + edge drag** is
the ripple trim; `Alt` + body drag belongs to slip (15.6); `Ctrl` keeps its meaning (toggle-select). The Q4 rule "a
plain trim does not trim a dissolve's cut edge" is specific to the keyboard `Q` / `W` and does not apply to the ordinary
drag. Shift+drag and Shift+Q / Shift+W share one planner, so an equivalent final edge gives an identical timeline.

Decided at the start of Step 15.5, before the implementation (Claude, within the confirmed Q3; no existing rule
contradicted):
- The mode is fixed when the edge is pressed: Shift held at the press → a ripple drag for the whole gesture; without it →
  the ordinary trim. Pressing or releasing Shift during the drag does not switch modes (a release that commits the
  other mode by accident is avoided; the press already distinguishes Ctrl = toggle-select).
- Playhead: an edge drag never moves the playhead — the existing drag rule (D015: the playhead is the user's, edits
  don't move it). Q5's move to the clip's start is specific to Shift+Q, where the edit is made at the playhead; a drag is
  made at the pointer. The equivalence with Shift+Q / Shift+W is therefore of the timeline (clips, their timing and
  source ranges, fades, dissolves, markers, tracks), not of the playhead.
- Inward, the ripple drag is the Shift+Q / Shift+W rule at the pointer's frame: the trimmed clip keeps its start, the
  later clips of its track move left, the limit is the dissolve parts of both edges (Q4 / Q6), the message the same.
- Outward (§6), the clip lengthens and the later clips of its track move right by the added whole frames; the
  neighbours don't limit it (they move), the source does (`SourceIn ≥ 0`, the end of the source — the edge trim's own
  clamp, silent as in the ordinary drag), and a dissolve on the dragged edge keeps the source handle it needs (D025 §4):
  the extension stops there with "The trim stopped where a dissolve needs the clip's frames." A ripple of the start
  outward keeps the clip's start and shows earlier source content from it (the clip at the same place, its in-point
  earlier).
- Preview: during the drag nothing is changed in the project; the edit service plans the ripple on every pointer move
  and returns the planned timing of the trimmed clip and of the moved clips (`PreviewRippleTrim`), which only the view
  shows (clips and dissolve zones laid out at the planned places). The release commits once (`RippleTrimClip`, one undo
  step); Esc, a lost pointer capture, an export starting or a release that changes nothing commit nothing.

Refined in Step 15.5 (implementation, 2026-10-07):
- One ripple planner, `PlanRippleTrim(plan, clip, track, edge, targetFrame)`, used by Shift+Q / Shift+W (the ripple branch
  of `TrimToPlayhead`) and by the drag (`RippleTrimClip` on release, `PreviewRippleTrim` while dragging). Tests compare
  the whole timeline (every clip's timing, source range, speed and fades, the dissolves, the markers, the track flags)
  of a drag and of Shift+Q / Shift+W at the same frame on the same project: identical at 23.976 / 25 / 29.97 / 30 / 60 fps
  × 0.25× / 0.5× / 1× / 2× / 4×, both edges, and with the dissolve limits and messages.
- The edge trim's timing rule (`TrimmedState`, `PlanTrimAtSpeed`) now works on a clip state and a given list of
  neighbours (the ordinary drag passes the track's other clips, as before; a ripple passes none). A ripple of the start
  is planned in the clip's own frame of reference (moved right by the frames to add, trimmed back to its start, moved
  to where it started), so a clip at 0 can grow at its start too.
- Inward the one-frame minimum stops a drag past the other edge silently (as the ordinary drag); the dissolve message
  only when a dissolve's frames stop it. Outward the source clamp is silent; a dissolve's handle on the dragged edge
  stops it with the message.
- Esc, a lost pointer capture or an export starting cancel the gesture: the view's layout is rebuilt from the
  (unchanged) model; no edit, no undo step, the dirty state and the playhead as they were. Every pointer move only
  plans (no notification, no undo step, nothing dirty); the release makes one `Commit`.
- The view: `TimelineGestureModifiers` (UI.Common) reads the press — Ctrl toggle-select, Shift (without Ctrl) a ripple
  trim; `TimelineViewModel.BeginTrim(…, ripple)` keeps it in the gesture; the preview lays out the planned clips and the
  dissolve zones (a zone moves with the clip that starts at its cut). An ordinary drag is unchanged — the same
  `PreviewTrim` / `TrimClip`, the neighbour clamp, D025 §5's removal of a dissolve whose cut it opens.
- Distinctions, as confirmed: the ordinary drag = the existing trim; Shift + drag = the ripple trim; Q / W = the keyboard
  trim of Step 15.4; Q4's "a plain trim does not trim a dissolve's cut edge" is a rule of Q / W only.

Answered at the start of Step 15.6 (product owner, 2026-10-07): Q12 — during an Alt + body drag the proposed Source In
/ Source Out are shown on the clip; the Preview is not updated while the pointer moves; the release commits the slip and
the Preview refreshes as after any edit; Esc (and a lost capture, an export starting) cancels with no change, no dirty
state, no undo step, no playhead move; a slip that reaches a source or dissolve limit is clamped to the valid range with
a status message. Slip is its own operation (not trim + move): the clip's timeline start, end and duration, its speed,
fades and dissolves, the neighbours, gaps, markers, track flags and the sequence length stay; only the source mapping
changes. Alt is read at the press, fixed for the gesture; Ctrl keeps toggle-select.

Reconciled with D022 and the code before the implementation (Claude, 2026-10-07; §7 applied, two points made exact):
- The source mapping (D009 / D022): timeline frame `n` of a clip at `S` shows source time
  `t(n) = SourceIn + (FromFrame(n) − S) · s`; a clip's source range `[SourceIn, SourceOut)` holds its `N` frames by
  D022's invariant (`SourceLength(N) ≤ SourceOut − SourceIn < SourceLength(N + 1)` at `s ≠ 1`; 1×: `SourceOut = SourceIn
  + duration`, a sub-frame tail accepted after a speed change). So a slip changes `SourceIn` only, and moves `SourceOut`
  by the same amount: the width `SourceOut − SourceIn` — and with it `N` and the invariant — is unchanged at every speed.
  (§7 wrote "`SourceOut'` follows the clip's rule"; that gives the same value for every clip whose width is already
  minimal and would otherwise trim a 1× clip's sub-frame tail — keeping the width is the exact "only the mapping
  changes".)
- The amount `Δ(k)` for a slip of `k` timeline frames is the existing start-trim rule (`TrimmedState` /
  `PlanTrimAtSpeed`, D022 — §7 as written): 1× `FromFrame(S + k) − FromFrame(S)` (the content moves by exactly `k` of the
  clip's frames), `s ≠ 1×` `±SourceLength(|k|, s)` — at 0.25× one timeline frame is a quarter of a source frame's time,
  at 4× four frames' time; never "one frame = one source frame". `Δ` is strictly increasing in `k`.
- Direction: a positive slip shows later source content (`SourceIn` grows). The drag moves the content with the pointer:
  the pointer `p` frames to the right is a slip of `−p` (earlier content comes in from the left).
- Limits: `SourceIn' ≥ 0` and `SourceOut' ≤` the source duration; a dissolve on the clip's start keeps the handle before
  it, one on its end the handle after it (`DissolveHandles`, D025 §4, the same functions the validator uses). The
  allowed range of `k` is the interval around 0 where all hold (searched on the monotone `Δ`); a request outside is
  clamped to its end with "The slip stopped at the start of the source." / "… at the end of the source." / "The slip
  stopped where a dissolve needs the clip's source."; nothing is ever removed. A clip whose current mapping already
  breaks a dissolve's handle (media replaced) can't slip (range 0).
- Which clips: §7 — video and audio clips (a video clip's sound slips with its picture; an audio clip has the same
  source mapping); images and text have no source range and are refused ("Only video and audio clips can be slipped.");
  a clip whose source duration is unknown is refused; a locked track refuses ("Track … is locked."). Offline media with
  saved metadata slips.
- The gesture: Alt held at the press on a clip's body (not on its trim handles, which keep the trim; Ctrl + Alt is a
  toggle-select) starts a slip of that clip, which becomes the selection; the view shows the planned Source In / Out
  (`PreviewSlip`, nothing applied, no notification — so the Preview, the waveforms and the timeline geometry stay); the
  release commits once (`SlipClip`, "Slip Clip", one undo step); a slip of 0 frames is no step. The playhead never moves.

Refined in Step 15.6 (implementation, 2026-10-07; the rules above as written):
- `ITimelineEditService.SlipClip(clipId, frames)` / `PreviewSlip(clipId, frames)` (+ `SlipPreview`, Core) over one
  planner (`PlanSlipOf`): refusals, the allowed interval of `k` (binary search of the monotone limits: the source, then the
  dissolve handles through `DissolveHandles`), the clamp and its note, `SlippedState` (D022's amount on `SourceIn` and
  `SourceOut` together), `Validate`, one `Commit` ("Slip Clip"). A clamp to 0 is refused with its note (no step); 0
  frames is `NoChange`. The preview raises no `TimelineChanged`, so the Preview, the waveforms and the timeline stay
  until the commit's single notification.
- Tested mappings at 25 fps: 0.25× four timeline frames = one source frame (0.04 s), 0.5× two = one, 1× one = one, 2× one
  = two (0.08 s), 4× one = four (0.16 s); at 23.976 / 25 / 29.97 / 30 / 60 fps × the five speeds `SourceIn` and `SourceOut`
  move by D022's amount computed independently in the test, and every other value of the timeline (all clips' timing,
  speed, fades, the other clips' source ranges, dissolves, markers, track flags, the sequence length, the playhead) is
  unchanged.
- The view: `TimelineGestureModifiers.IsSlip` (Alt without Ctrl) at the press on a clip's body (a dissolve zone there no
  longer takes an Alt press); `TimelineViewModel.BeginSlip` (refuses images and text with the status message, no
  gesture); the gesture's `SlipFrames` = minus the pointer's nearest whole frames; the clip shows "In hh:mm:ss:ff  Out
  hh:mm:ss:ff" (the planned Source In / Out at the project rate; "(limit)" when clamped; "Can't slip" when refused) over
  its body; the release commits `SlipClip` and reports its note; Esc / a lost capture / an export starting cancel through
  the existing `CancelGesture`, which also clears the text.
- End to end: the A/V sync source slipped by +5 frames exports its white frames and bursts 5 frames earlier at the same
  clip place and length, the Preview equal to the export canvas (`ExportRippleEndToEndTests`).

Answered at the start of Step 15.7 (product owner, 2026-10-07):
- Q7 — `I` sets In at the start of the frame containing the playhead, `O` sets Out at the end of that frame (the frame
  under the playhead is in the range); internally `[In, Out)`; I and O on one frame give one frame.
- Q8 — `I` / `O` are never refused: the new point is kept and the opposite one cleared when the order would be invalid;
  no message for it.
- Q9 — Loop on with a valid range plays exactly `[In, Out)`, and Play with the playhead outside it starts at In; Loop off:
  the range doesn't constrain playback; clearing the range restores the whole-sequence loop.
- Q10 — no valid range: the export as before; a valid range: Export asks "Range" / "Entire sequence" / "Cancel"; Range
  exports exactly `[In, Out)`, Entire sequence ignores the range, Cancel starts nothing; the range is never serialized.
- Q11 — an ✕ on the range bar of the timeline clears In / Out; no key for it; clearing is session state.
- Q16 — a range export's preflight checks only the media the composition uses inside the range; media used only outside
  doesn't block; every other check stays.
- The range is session state like the playhead but not saved at all: not in `project.json`, not in autosave / recovery,
  cleared on New / Open / Recover, never dirty, never an undo step; `project.json` stays v3.
- Q15 (D029 §3, taken into this step): the stale "Playing without sound…" status is cleared through the playback state
  when sound is available again; no undo or dirty change.

Refined in Step 15.7 (implementation, 2026-10-07):
- Core: `InOutRange` (the rules: `WithIn` / `WithOut` / `Regrid` / `Frames` — clamped to the sequence, only In = to the
  end, only Out = from 0), `PlaybackRange`, `ExportRange`. `ExportOutput.For(snapshot, range)`: the range's frames, its
  duration and the samples `[⌈In · 48000 / 10⁷⌉, ⌈Out · 48000 / 10⁷⌉)` (`FirstFrame`, `FirstSample`); `ExportJob` carries
  the range; `ExportFrameSource` renders output frame n from timeline frame `FirstFrame + n` and `ExportAudioSource`
  mixes from `FirstSample` — the same pipeline, offset, so the track flags, fades, dissolves and speed are as in the
  whole export: a range export's frames and samples are byte for byte the whole export's at the same timeline positions
  (tested at 29.97 fps across a cut, a fade, a 2× clip, a hidden and a muted track). `ExportPreflight.Check(…, range)`
  clamps the range to the sequence (an empty one: `EmptyRange`, "The In / Out range has no part of the timeline in it.")
  and checks a picture where it is shown (its dissolve zones included), a sound where it is placed, a text where it is.
- Playback: `IPlaybackService.SetPlaybackRange` / `PlaybackEnd` — `PlaybackService` starts Play outside the range at its
  start and pauses at its end as at the sequence's end; the `AudioMixer` makes every sample from Out on silent, so the
  buffered sound never runs past it. The Preview applies the range only while Loop is on (and the range is valid), and
  its existing loop restarts at `PlaybackEnd` — the range's end or the sequence's.
- UI: `InOutRangeService` (UI.Services, a singleton: the timeline, the Preview, the export) — `SetIn` / `SetOut` / `Clear`,
  cleared on `ProjectChanged`, re-gridded on a frame-rate change, never touching the project. The timeline view model:
  `SetIn` (I) / `SetOut` (O) / `ClearInOut` (the ✕), the bar (`RangeLeft` / `RangeWidth`), the In / Out lines, the
  playhead-in-range state (a brighter shade), all recomputed on the range, zoom, timeline changes and playhead moves;
  status "In set at …", "Out set after … (that frame is included).", "In / Out cleared.". `ExportWorkflow` asks first,
  then runs its preflights with the chosen range. Keys I / O in `ShortcutRouter` (Ctrl+I stays Import).
- Q15: `PreviewViewModel` re-arms the "Playing without sound…" report and sets the status back to "Ready." when sound is
  available again while playing, if that message is still the one shown. (The message is about the audio output, not
  about muted clips or tracks — a muted clip or track never produces it.)

Answered at the start of Step 15.8 (product owner, 2026-10-07): Q14 — a newly created text takes
`FontSize = 48 × canvasHeight / 1080` (720 → 32, 1080 → 48, 1440 → 64, 2160 → 96, 1080 × 1920 → 85.333…); a canvas change
of an existing project keeps D028 Variant B — the existing font size × `s = min(W'/W, H'/H)`, the text's Scale not
changed, rotation, crop, opacity and timing as D028 says. A 48 title on 1920 × 1080 becomes 27 on 1080 × 1920 (D028), a
new title there gets 85.333 (the new-text rule): two different operations, not a contradiction. Nothing else changes.

Refined in Step 15.8 (implementation, 2026-10-07): `ProjectSettingsRules.NewTextFontSize(canvasHeight)` (Core, next to
`ContainFactor`) — `48.0 × canvasHeight / 1080`, exact in `double`, no rounding (the Inspector shows it with its usual
`0.##`, e.g. 85.33); within the 1 … 1000 limits for every canvas the settings rules accept (64 … 4096 px high:
2.84 … 182.04) — guaranteed by those rules, not by a clamp: a canvas outside them (only possible in a file edited by
hand, which a load accepts when positive) gets the formula's value unclamped, as D028's scaling does.
`AddTextClip` is the only place that creates a text and the only caller; copies (paste, duplicate) keep their source's
size; the model's default 48 (`TextClip.FontSize`) is unchanged, so files load as before. `project.json` unchanged (v3).

Found and fixed in Step 15.9 (closeout regression R2, product owner's decision 2026-10-08 to fix it in both places): a
still image entered far into its clip was not decoded — the Preview's `SpanReader` and the export's
`ExportPictureReader` opened the decoder at the timeline frame's sample point, but a still's one frame is at its file's
start, beyond the decoder's 10 s preroll (`FrameNotReached`). It showed as a range export that failed when a long image
clip crossed In more than ~10 s after its start (the whole export, opening the reader at the clip's start, passed), and
as a placeholder instead of the image after a Preview seek into such a clip — the latter since Phase 5, the reader code
unchanged by Phase 15. Fix: one rule, `SourceFrameSelector.StillImage` — a still is always opened at the start of its
file (D009's hold-first; its timeline position never selects a frame). No other behaviour changes: video spans keep
their sample point; the default export stays byte for byte as before (R1). Tests: `ExportFrameSourceTests`,
`PlaybackServiceTests` (the request's point) and `ExportRangeEndToEndTests` (a range 16 s into a 20 s PNG clip equals the
whole export's frames, the Preview seeked there equal to them, real ffmpeg); each reader's old line restored fails its
tests.

Final state at the Phase 15 closeout (Step 15.9, 2026-10-08) — the accepted answers and rules, as implemented:
- Q1 — `Q` / `W` act on the selected clips whose interval strictly contains the playhead's frame; nothing implicit;
  several clips in one step. Q2 — `Q` / `W` plain, `Shift+Q` / `Shift+W` ripple (the later clips of the clip's own track
  move; other tracks and markers stay). Q4 — a dissolve on the trimmed edge is kept: plain `Q` / `W` don't trim a cut
  edge (status points to the Shift variant), the ripple variants trim it up to the dissolve's limit; the ordinary edge
  drag keeps D025 §5. Q5 — after `Shift+Q` the playhead goes to the clip's start (the earliest with several clips); a
  drag never moves it. Q6 — a trim stopped by a dissolve is applied up to the limit with a message.
- Q3 — Shift + edge drag = ripple trim (the same planner as `Shift+Q` / `Shift+W`), Alt + body drag = slip, the mode
  fixed at the press, Ctrl unchanged; the ordinary drag unchanged.
- Q12 — slip: the planned Source In / Out shown on the clip during the drag, the Preview after the release; Esc
  cancels. Slip applies to **video and audio clips** (a video clip's sound slips with it; an audio clip has the same
  source mapping); images and text are refused; only `SourceIn` / `SourceOut` move, by D022's amount; source and
  dissolve-handle limits clamp with a message.
- Q13 — mute and hide are allowed on a locked track; the lock refuses every ordinary edit (clips, track delete / move,
  trims, ripple, slip); video tracks M / 👁 / 🔒, audio tracks M / 🔒.
- Q7–Q11, Q16 — the In / Out range is **transient session state** with exact `[In, Out)` semantics (`I` = start of the
  playhead's frame, `O` = end of it, so that frame is included); never refused (Q8 clears the other point); not saved,
  not in autosave / recovery, not dirty, not undoable, cleared on New / Open / Recover, re-gridded on a rate change.
  Loop on with a range plays exactly `[In, Out)` (Play outside starts at In); Loop off ignores it. Export with a valid
  range asks Range / Entire sequence / Cancel; Range exports exactly `[In, Out)` — frames and samples equal to the whole
  export's at the same positions — with a preflight over the range's media only; ✕ on the range bar clears it, no key.
- Q15 — "Playing without sound: no audio output is available." is about the **audio output device**, not about mute; it is
  cleared ("Ready.") when sound is available again, if still shown, and a later loss is reported again.
- Q14 — a new text's `FontSize = 48 × canvasHeight / 1080`, exact `double`, **no clamp** (within 1 … 1000 for every canvas
  the settings accept); D028's scaling of existing text unchanged.
- Every project change is one undoable command, off during an export, refused on a locked track (except Q13);
  `project.json` stays **`formatVersion` 3** with no new property; no ffmpeg call in any edit.

Consequences: new commands in the Timeline subsystem (`ITimelineEditService`: track state, trim to the playhead with
ripple, slip and its limits), sharing the existing trim / shift planners and `EditPlan` reconciliation; a range on the
export job (`ExportJob` / preflight / encoder input) and in the Preview's loop; the timeline view gains the track
toggles, the ripple / slip gestures and the range band; `project.json` unchanged (v3);
`docs/PHASE15_MANUAL_TEST_PLAN.md` holds the real-app scenarios.

Status: Accepted (2026-10-07, product owner: scope §1, the In / Out range as unsaved session state, the keys `I` / `O` /
`Q` / `W`, the font-size formula, the non-goals §3). Q13 answered (Step 15.3); Q1, Q2, Q4, Q5, Q6 answered (Step
15.4); Q3 answered (Step 15.5); Q12 answered (Step 15.6); Q7–Q11, Q15, Q16 answered (Step 15.7); Q14 answered (Step 15.8) — every
open question is answered. Steps 15.2 (`d1296be`), 15.3 (`1d26165`), 15.4 (`f122c9a`), 15.5 (`efecd24`), 15.6
(`aa94da0`), 15.7 (`46eae30`) and 15.8 (`f9a2253`, review corrections `9de14c6`) accepted. **Phase 15 complete**: Step 15.9
closeout done (2026-10-08) — final validation, the R1–R10 regression in `docs/PHASE15_MANUAL_TEST_PLAN.md` and the
still-image fix it led to (above); accepted; PR #15 merged as `a3793a4`. Steps and acceptance criteria:
`docs/DEVELOPMENT_PLAN.md`, "Phase 15 — Editing tools: steps".

After the merge (recorded at Step 16.2, 2026-10-08): Phase 15 accepted by the product owner; PR #15 (head `4ded033`)
merged into `main` as `a3793a4` (its tree identical to `4ded033`); CI green on the first attempt on the pull request (run
37743022102) and on `main` (run 37743623800). The Known issue "the Preview stays black after adding a clip with the
playhead beyond the previous end" was fixed after it by PR #16 (`a246f9f`, merged as `1bdeb95`; CI runs 37756880756 and
37757808666): playback seeks to a playhead parked past the end when an edit makes the sequence longer; inside
[0, Duration] the playhead keeps its time, past the new end it is clamped to it — D010 / D011 / D012 unchanged.

---

## D031 — Phase 16: source viewer & three-point editing

Date: 2026-10-08

Decision (product owner, 2026-10-08, after the Phase 16 pre-flight; variant A — the plan and SQ1–SQ16 approved as
proposed, the A ↔ D boundary approved). Phase 16 lets the user cut long recordings of attempts into the sequence with a
few keys: open a video or audio asset in a **Source** mode of the Preview panel, mark a source In / Out, and **Insert**
(`,`) or **Overwrite** (`.`) that range at the timeline playhead on the target track. Every project change is one
undoable command through `ITimelineEditService`, built from the existing split / trim / shift rules and `EditPlan`;
the rendering rules are unchanged, so the Preview and the export stay identical by construction; `project.json` stays
`formatVersion` 3 with no new property.

Context (pre-flight, the code at `1bdeb95`, `main` after the merge of PR #16):
- Phases 0–15 merged; build 0 / 0; 2856 tests pass, 2 skip (the 4K scenes); CI green on PR #15 / #16 and on `main`.
- One `PlaybackService`, one `IAudioOutput` (WASAPI, one sample source) and one `IReferenceClock`, all singletons
  (`ServiceCollectionExtensions`); the playback snapshot is built from the project (`PlaybackSnapshotBuilder`), at the
  project rate and canvas; the timeline playhead and playback are wired in `MainWindowViewModel` (D011).
- The Preview column is about 456 px wide at the 1024 px minimum window (`MainWindow.axaml`: 260 + 300 side panels):
  two monitors side by side do not fit.
- `ShortcutRouter` is global (no focus context); `I` / `O` are the timeline In / Out (D030 §11); `,` and `.` are free.
- A video file is one `VideoClip` that carries its own sound (D008); an `AudioClip` refers to an audio asset only
  (`ProjectSerializer`). The timeline selection is a list of clips whose last one is the primary (`TimelineViewModel`);
  there is no track selection.

### 1. Scope

1. Source mode (16.4 / 16.5): the Preview panel switches between **Timeline** and **Source**; Source plays one video or
   audio asset through the same `PlaybackService` and audio output; the timeline playhead does not follow it.
2. Source In / Out (16.5): set at the source position, shown on the Source bar, cleared with its ✕; session state.
3. Insert / Overwrite (16.3 core, 16.6 UI): the source range at the timeline playhead on the target track, one undo step.

### 2. Rules (SQ1–SQ16, approved)

- SQ1 — a **mode** of the Preview panel (a Timeline / Source switch), not a second panel.
- SQ2 — **one** `PlaybackService` and audio output: in Source mode playback gets a snapshot of the one asset; back in
  Timeline mode the timeline snapshot is rebuilt and playback seeks to the timeline playhead. While Source is shown, the
  playback position never moves the timeline playhead and timeline changes do not reach playback (they are applied on
  the way back). `PlaybackService` is not changed for this.
- SQ3 — the source In / Out is **session state** per asset: not saved, not dirty, not undoable, cleared on New / Open, an
  asset's range dropped when the asset leaves the project.
- SQ4 — keys by **mode**, not focus: in Source mode `I` / `O`, `Space`, `J` / `K` / `L`, `←` / `→`, `Shift+←` / `Shift+→`,
  `Home` / `End` act on the source; in Timeline mode they act as before. Never while a text input has focus.
- SQ5 — **`,` Insert, `.` Overwrite** (no modifier), and Insert / Overwrite buttons on the Source bar; in both modes.
- SQ6 — Insert ripples **only the target track**: a clip with the insert point strictly inside is split there (the split
  rule) and its right part, with every clip of the track that starts at or after the point, moves right by the inserted
  length (the move rule). Other tracks stay (ripple across all tracks is a D030 §3 non-goal).
- SQ7 — markers do not move (D027 §2).
- SQ8 — the target track: the track of the **latest selected clip** on a track of the source's kind (video → a video
  track, audio → an audio track; the selection's order, the primary last); without one, the first track of that kind
  (V1 / A1, as "Add to Timeline"). A locked target refuses the edit with a message; no other track is tried.
- SQ9 — transitions and fades follow the existing rules: a dissolve whose cut is gone is removed with the usual note
  (D025 §5, `EditPlan.ReconcileTransitions`); an edit point inside a dissolve's zone is refused, as Split refuses it; the
  split clip's fades stay on its outer edges (D025 §2); a clip Overwrite shortens keeps the trim rule's fades (clamped).
  Overwrite of a partly covered clip trims it with the trim rule (D008 / D022 / D025), a covered clip is removed, a clip
  covering the whole range is split at its start and trimmed at its end.
- SQ10 — after Insert / Overwrite the timeline playhead moves to the end of the new clip, which becomes the selection.
- SQ11 — Source opens video and audio assets; not images (no own length — "Add to Timeline" adds them), not a missing /
  offline asset or one whose analysis is not complete (a message instead).
- SQ12 — the source grid: the project rate when it is locked (the source frames are then chosen by D009); the asset's
  rate when the project rate is still provisional and the asset is a video (the first insert locks that rate, D007);
  the range is re-gridded when the project rate changes, as the timeline In / Out is (D030 §8).
- SQ13 — the inserted clip plays at 1×.
- SQ14 — Loop in Source mode loops [In, Out) of the source (the D030 §8 playback range); without a range the whole asset.
- SQ15 — a video asset goes in as **one `VideoClip`, picture and sound together** (D008).
- SQ16 — during an export the Source viewer plays; Insert / Overwrite are off (`EditingLock`).
- Without a source In the range starts at the asset's start, without an Out it ends at the asset's last whole frame;
  a range shorter than one frame is refused.

### 3. Boundary A ↔ D (approved)

In Phase 16: the picture and the sound of a video file stay one `VideoClip`; an audio file goes onto an audio track as an
`AudioClip`; Insert ripples the target track only, so a video clip's own sound never drifts. Left to a later audio
phase (D): an `AudioClip` from a video asset, unlinking, linked V / A pairs, source patching (picture only / sound only,
separate targets), J / L cuts, audio crossfades, syncing other tracks on Insert, and an "insert without sound" option
(a half-unlink — decided with D). D008 stays as it is.

### 4. Constraints

- Unchanged in substance: D006 / D007 / D009 / D022 (grid, rate, frame selection, speed), D008, D010 / D011 / D012,
  D013, D014 / D016, D015, D018 / D023, D025, D026, D027, D028, D030. `project.json` v3, no new property.
- The Preview ↔ Export parity suite, the L1-c criteria and every existing expected value unchanged.
- Every new command one undo step, refused on a locked track and during an export, a refused or unchanged edit leaving
  no undo step; no ffmpeg in an edit.
- New controls checked in the real app at 1024 and 1440 px.

### 5. Non-goals

Everything of D in §3; four-point editing and fit-to-fill; a source In / Out saved in the project; a second monitor;
waveforms or filmstrips in the Source viewer; images in Source; replace edit; ripple across all tracks; keyframes, text
styling, AI, HDR.

Consequences: `ITimelineEditService` gains `InsertClip` / `OverwriteClip` (the service, its design-time stub and the test
stub); the per-clip split of `Split` becomes a shared planner step; a single-asset snapshot builder in Core; the Preview
gains the Source mode and a source state service; `ShortcutRouter` maps keys by mode; the manual plan
`docs/PHASE16_MANUAL_TEST_PLAN.md`.

Refined in Steps 16.3–16.7 (implementation sub-decisions inside the approved rules; recorded at the closeout):
- 16.3 — the per-clip split of `Split` became `PlanSplitClip` and the first video's rate lock of `AddClip` became
  `PlanFirstVideoRate`, both unchanged in behaviour; Insert / Overwrite work on the plan's states of the target track
  (re-gridded when the edit locks the rate). SQ9 read as the trim rule (D025 §5): Overwrite refuses only a split or a trim
  that would cut into the frames a dissolve that stays needs; a dissolve whose cut the range covers is removed with the
  note — also when an edge of the range lies inside its zone (`Overwrite_with_an_edge_inside_a_dissolve_zone_…`). Insert
  at a point inside a zone is refused (it splits there). Images are refused by the service (SQ11).
- 16.4 — the source snapshot is `PlaybackSnapshotBuilder.Build` of a transient one-clip project (the asset at 1×), so the
  source plays by the timeline's own rules; its canvas is the asset's display size (the project canvas for audio or an
  unknown size). The last source position is kept per asset for the session (back to Source, or the asset opened again,
  shows it); an asset that goes missing closes Source and keeps its range, one that leaves the project loses it.
  `ITimelineEditService.GetSourceGrid` is the one SQ12 rule for the viewer and the placement.
- 16.5 — End in Source seeks to the source's end (its last frame shown), O there marks after the last frame; J in Source
  steps one second back, as in Timeline mode. A double click in the Media Browser opens a video or audio file in Source —
  before Phase 16 it added the item to the timeline; an image keeps that (it has no source range), and "Add to Timeline"
  and dragging are unchanged for every kind. Opening in Source takes the focus off the media list, so its arrows / Home
  / End reach the source (found in the real-app run, `ac0e3ac`).
- 16.6 — Source stays shown after Insert / Overwrite (the next range can be marked at once). The Insert / Overwrite
  buttons are on the source bar, shown in Source mode; `,` / `.` work in both modes (SQ5).

Final state at the Phase 16 closeout (Step 16.7, 2026-10-08): every rule of §2 implemented and tested (Timeline.Tests
`InsertOverwriteTests`, UI.Tests `SourceViewerTests`, ExportEndToEnd.Tests `ExportInsertOverwriteEndToEndTests`), the
gates and the manual plan in `progress.md` and `docs/PHASE16_MANUAL_TEST_PLAN.md`; `project.json` v3 unchanged (a Phase 15
save opened and saved byte for byte), the default export byte for byte as Phase 15's.

After the merge (recorded at Step 17.2, 2026-10-08): Phase 16 accepted by the product owner; PR #17 (head `f5a1ec2`)
merged into `main` as `79e4e78` (its tree identical to `f5a1ec2`); CI green on the pull request (run 37776542826) and on
`main` (run 37778354822). The Phase 17 baseline on `79e4e78`: `dotnet build --no-incremental -warnaserror` Release and
Debug 0 / 0; the full suite 2894 passed, 2 skipped (the 4K scenes), 0 failed. The D side of §3 is taken by D032.

Status: Accepted (2026-10-08, product owner: variant A, the plan, SQ1–SQ16, the A ↔ D boundary). **Phase 16 complete**
(Step 16.7 closeout, 2026-10-08), accepted and merged (PR #17, `79e4e78`). Steps and acceptance criteria:
`docs/DEVELOPMENT_PLAN.md`, "Phase 16 — Source viewer & three-point editing: steps".

---

## D032 — Phase 17: audio editing — unlink / link, J / L cuts, sound-only insert, audio crossfades

Date: 2026-10-08

Decision (product owner, 2026-10-08, after the Phase 17 pre-flight; variant A — the D side of D031 §3, without a separate
stabilization phase before it). Phase 17 lets the user edit the sound of a recording apart from its picture: unlink a
video clip's sound into an independent audio clip, link a video clip and an audio clip explicitly, make J / L cuts,
insert or overwrite only the sound of a source, and cross-fade audio clips. Every project change is one undoable command
through `ITimelineEditService`, built on `EditPlan` and the existing rules; the sound of the Preview and of the export comes
from the same Core rules, so they stay identical by construction. `project.json` becomes `formatVersion` **4**.

Context (pre-flight and pre-implementation audit, the code at `79e4e78`, `main` after the merge of PR #17):
- Phases 0–16 merged; build 0 / 0; 2894 tests pass, 2 skip (the 4K scenes); CI green on PR #17 and on `main`.
- A video file is one `VideoClip` carrying its own sound (`Volume`, `IsMuted`, the clip's fades) — D008, D031 SQ15.
  `AudioClip` (`MediaAssetId`, source range, `Speed`, `Volume`, `IsMuted`, fades) exists and lives on audio tracks only
  (`TimelineValidator.IsCompatible`); it is created only from audio assets (`TimelineEditService.CreateClip`) and the
  serializer refuses an `AudioClip` of another asset kind (`ProjectSerializer.KindMatches`).
- Playback and export already accept the sound of a video asset in an `AudioClip`: `PlaybackSnapshotBuilder.AudioStatus`
  takes audio and video assets; a `VideoClip` with an audio stream adds an `AudioSpan` (on any video track, hidden or not;
  none on a muted track). Both the Preview's `AudioSpanReader` / `AudioMixer` and the export's `ExportAudioReader` place and
  mix by the shared `AudioPlacement`, `AudioFadeEnvelope` and `AudioMix` (D013, D023, D025 §2).
- Transitions (D025): `Track.Transitions` with `LeftClipId` / `RightClipId`; `TransitionRules` (zone, `MinFrames`,
  `MaxFrames`) refuses any transition on an audio track ("Only video tracks have transitions."); `DissolveHandles` gives
  every non-`VideoClip` unlimited handles; `EditPlan.ReconcileTransitions` / `ClampFades` and the service's `Validate`
  keep transitions and fades consistent for every edit. A dissolve's sound is a hard cut (D025 PO-5).
- Fades already exist on every clip kind, audio clips included (D025 §1; Inspector `HasFades`); audio split and trim
  already exist for `AudioClip` (the same planners as for video).
- No link concept exists anywhere (model, `EditPlan`, serializer, UI). Every edit planner works per clip on its own track,
  except the ripple edits (target track only, D027 §2, D030 §3, D031 SQ6).
- The UI's "Dissolve" command adds `DefaultDissolveDuration` (1 s) or the longest that fits (`MaxTransitionFrames`).
  Gestures: Shift + edge = ripple trim, Alt + body = slip, Ctrl = toggle select; Alt + edge is free.
- Audit result: **no contradiction** between the approved model below and the architecture. The model change is additive
  (two clip properties, one transition type, one relaxed kind rule); links fit `EditPlan` the way transitions do (planned,
  reconciled, validated before one command). The two oversized files (`TimelineEditService.cs` 1819 lines,
  `TimelineViewModel.cs` 1317 lines) are extended with `partial` files as in Phase 16, without a split for its own sake.
  The two ROADMAP stabilization items ("the In / Out update order", "deep-seek decode coverage") are not defined anywhere
  in the documents and no concrete defect or dependency of Phase 17 on them was found: they are not Phase 17 tasks.

### 1. Scope

Unlink Audio; Link / Unlink; J-cut and L-cut; sound-only Insert and Overwrite; audio split / trim; audio fade in / out;
audio crossfade; Undo / Redo; locked-track protection; Preview ↔ export parity; the ripple of linked clips.

### 2. Rules decided by the product owner (2026-10-08)

- L1 — `project.json` v4. A v3 (v1 / v2) project opens without any change of meaning and is saved as v4; v4 → v3 is not
  supported (a v3 build refuses a v4 file with the existing "saved by a newer version" message, D014).
- L2 — The base model stays D008 / D031 SQ15: a video file is one `VideoClip` with its own sound. The timeline is **not**
  remodelled so that every video clip is a `VideoClip` + `AudioClip` pair.
- L3 — **Unlink Audio** moves a video clip's sound into an independent `AudioClip` (the same asset); afterwards the two
  are independent.
- L4 — **Link** sets an explicit link between an existing video clip and an existing audio clip; **Unlink** removes it;
  after Unlink the clips are fully independent.
- L5 — Explicitly linked clips are edited as one where an edit touches both; a ripple keeps their relative offset; an
  edit that would have to change a **locked** partner is refused as a whole — no partial application and no fallback to
  another track.
- L6 — Audio crossfade: 1 s by default, linear (unless that contradicted the architecture — it does not, §3 R9), limited
  by the available handles and clip edges, built on the existing transition rules and `EditPlan` reconciliation, not on
  parallel rules.
- L7 — Out of scope: ducking, keyframes, speed ramps, waveform work, a full mixer, volume automation; a separate
  stabilization phase (only local refactoring needed by Phase 17). The Phase 16 notes for later (the Source message for an
  unanalysed asset, the duration text cut at 1024 px) are not mandatory for Phase 17. The real audio device check R10
  (D030) is part of the Phase 17 manual plan.

### 3. Technical rules (derived from the audit; binding for the steps)

- R1 — Model (Core): `Clip.LinkId` (`Guid?`, only on `VideoClip` / `AudioClip`): the clips sharing a `LinkId` form a link;
  a link is exactly **one `VideoClip` and one `AudioClip` on different tracks**. `VideoClip.IsAudioDetached` (`bool`):
  the clip's own sound was unlinked — the clip produces no sound; its `Volume` / `IsMuted` are kept but unused.
  `AudioClip` may refer to a **video asset with an audio stream**. A new transition type `"crossfade"`, allowed only on
  audio tracks; `"crossDissolve"` stays video-only. Both are D017-style typed properties, not `Clip.Effects`.
- R2 — Format v4 (`ProjectSerializer`): `linkId` written only when set, `isAudioDetached` only when true, so a v3 project
  saved again as v4 differs only in `formatVersion` (tested byte for byte apart from that number). Loading v4 validates
  (damaged = the whole file refused, D014): a link of exactly one video and one audio clip on different tracks; an audio
  clip refers to an audio or a video asset (the audio stream is not checked on load — media may change; a clip whose
  source has no sound plays silence, as a `VideoClip` without sound does); crossfades by the D025 zone rules on audio
  tracks. v1–v3 have no links, no detached sound, no audio
  transitions and no audio clip of a video asset (refused as now).
- R3 — Unlink Audio (one undo step): for a `VideoClip` with an audio stream, not detached, not linked, on an unlocked
  track: a new `AudioClip` with the same asset, timing, source range, speed, volume, mute and fades is placed on an audio
  track (AQ8), the video clip gets `IsAudioDetached = true`. The output does not change: the export before and after is
  byte for byte the same (tested) where no other sound overlaps the clip — the mix sums spans in snapshot order (video
  tracks first) in `float`, so with other overlapping sound a sample may differ in its last bits; except where the video
  track was muted (AQ8) and for a fade on an edge with a video dissolve (stored, not applied to the sound by PO-8 —
  AQ11). Refused (with a message) for a clip without sound, a detached or linked clip, a locked track or no possible
  target.
- R4 — Link / Unlink (one undo step each): Link takes the selection (AQ9); Unlink clears the `LinkId` of both. A link
  never changes timing. `IsAudioDetached` is not touched by Link / Unlink: a linked pair made from an unlinked recording
  stays one picture + one sound.
- R5 — Links in edits: every planner that changes a linked clip adds its partner by the link rule of the edit (AQ1–AQ7)
  into the same `EditPlan`; a partner on a locked track refuses the whole edit (L5); the partner's own track is checked by
  the usual validation (overlap, frame grid, source range), so a partner that cannot follow refuses the edit with a
  message. `EditPlan` gains link reconciliation like `ReconcileTransitions`: a link whose partner the plan removes (Delete
  of one side, track delete, media removal, Overwrite) is cleared in the same step, so no half link is ever saved;
  `DeleteTrack`, which builds no `EditPlan` today (a bare `RemoveTrackCommand`), gains the same clearing in its command;
  a partner on a locked track — AQ12. Copy /
  Paste / Duplicate: both partners copied → the copies get a new shared `LinkId`; one side alone → pasted unlinked.
  A frame-rate re-grid re-grids each partner by D007 (their offset may round to the new grid).
- R6 — J / L cuts: on unlinked clips by the ordinary trims; on a linked pair by trimming one side alone (AQ3). No new
  timing rule: the trim, ripple trim and slip rules of D008 / D022 / D030 apply unchanged to the clip that changes.
- R7 — Sound-only Insert / Overwrite (D031 §3 "source patching", sound side only): the source range of a video asset with
  sound goes in as an `AudioClip` of that asset onto the target audio track (D031 SQ8 with the kind "audio"), by the D031
  SQ6 / SQ9 / SQ10 / SQ13 rules; how the user asks for it — AQ10. A picture-only insert stays out (D031 §3's "insert
  without sound" is not in L-scope).
- R8 — Audio fades: the existing D025 §2 fades of `AudioClip`s, unchanged; Phase 17 covers them for audio clips of video
  assets and for unlinked sound (tests), and suppresses a fade on an edge that has a crossfade (as PO-8 does for a
  dissolve).
- R9 — Audio crossfade (`"crossfade"`, audio tracks): on the cut `c` between A and B of one audio track (A's end is B's
  start), `F ≥ TransitionRules.MinFrames` frames of the project rate, the D025 zone `[c − ⌊F/2⌋, c + ⌈F/2⌉)`; A plays on
  past its end and B starts early from their **source handles**, computed by the D025 §4 rule now applied to `AudioClip`
  too (`DissolveHandles`; audio clips had unlimited handles only because they had no transitions). Over the zone's
  timeline samples `[Z0, Z1)` (`AudioPlacement`'s `⌈t · 48000 / 10⁷⌉`), `M = Z1 − Z0`, sample `k`: B's gain factor
  `ramp(k − Z0, M) = (k − Z0 + 1)/(M + 1)`, A's `1 − ramp(k − Z0, M)` — linear, the two factors sum to 1 (no equal-power
  curve, as D025 §2). It multiplies the clip's volume / mute and fade factor. Extended samples use the unchanged
  placement rule (`k + d` at 1×, D022 otherwise). Add / Remove / Duration and every edit's reconciliation reuse the D025
  §5 rules and the `EditPlan` transition machinery; the UI's default is 1 s or the longest that fits (`MaxTransitionFrames`).
  Video-track dissolves keep their hard sound cut (D025 PO-5 unchanged).
- R10 — Snapshot and mix (Core, shared by the Preview and the export): a `VideoClip` with `IsAudioDetached` adds no
  `AudioSpan`; an `AudioSpan` gains its crossfade extension and ramps; `AudioPlacement` / `AudioFadeEnvelope` / `AudioMix`
  evaluate them for both readers in the same order of operations. A crossfade change is a timing change for the readers
  (the extended range), not a presentation-only change.
- R11 — Relink (D026) counts the audio clips of an asset: a candidate without an audio stream for an asset whose sound is
  used by audio clips is reported among the warnings.
- R12 — Detached sound in the UI: a detached video clip shows no audio group in the Inspector and no waveform; the timeline
  marks linked clips. Selection, playhead, In / Out stay session state (D015).
- R13 — Every new command: one undo step, exact on Undo / Redo, dirty by the save point (D015), refused on a locked track
  and while an export runs (`EditingLock`), a refused or unchanged edit leaving no undo step; no ffmpeg in an edit.

### 4. Open questions (proposals; the product owner's answers are needed before the step that uses them)

- AQ1 — Selection: clicking a linked clip selects its partner too; Ctrl + click toggles the pair. *Proposed: yes.*
- AQ2 — Delete / Ripple Delete of a linked clip removes its partner too (Ripple Delete closes up each track). *Proposed:
  yes.*
- AQ3 — Trim of a linked clip's edge moves the partner's same edge by the same frames (clamped as one: the smaller
  possible change for both); **Alt + edge drag** trims only that clip — the way to make a J / L cut on a linked pair
  (today Alt + edge is an ordinary trim; only linked clips change). Q / W
  (trim to the playhead) act on the selection, the partner included (AQ1), each edge to the playhead. *Proposed: yes.*
- AQ4 — Ripple (Insert, Ripple Delete, Close Gap, ripple trim): the partners of the clips a ripple moves move by the same
  frames; the other clips of the partners' tracks do not move (no ripple across all tracks, D030 §3, D031 SQ6); a partner
  that would then overlap a clip refuses the whole edit with a message. *Proposed: yes.*
- AQ5 — Split of a linked clip also splits its partner when it covers the split point; the two right parts form a new
  link. *Proposed: yes.*
- AQ6 — A speed change of a linked clip applies the same speed to its partner (one step, refused as a whole if one can't
  take it). *Proposed: yes.*
- AQ7 — Move: the partner moves by the same frames in time; dragging one clip to another track changes only that clip's
  track. Slip slips both by the same source frames. *Proposed: yes.*
- AQ8 — Unlink Audio's target: the first audio track (A1, A2, … in order) free over the clip's range, otherwise a new audio
  track added in the same undo step. The new audio clip is muted when the video track was muted (the output stays
  unchanged); unmuting it is the user's choice. *Proposed: yes.*
- AQ9 — Link takes exactly one video clip and one audio clip of the selection, on different tracks, both unlocked and not
  yet linked; any offset and any assets. Unlink Audio, Link and Unlink are buttons of the timeline toolbar (no new
  shortcut keys); for a selection with a linked clip the toolbar offers Unlink, for a video clip with sound Unlink Audio.
  *Proposed: yes.*
- AQ10 — Sound-only Insert / Overwrite: a **"Sound only"** toggle on the Source bar (session state, shown for a video asset
  with sound); `,` / `.` and the buttons then insert the sound as an `AudioClip` on the target audio track (SQ8, kind
  audio). Alternative: the target follows the selected track's kind. *Proposed: the toggle.*
- AQ11 — Unlink Audio of a video clip with a fade on an edge that has a video dissolve (the fade stored but not applied
  to the sound, PO-8): the new audio clip gets the **effective** fade (0 on that edge), so the output stays unchanged; or
  the stored fade (the sound changes). *Proposed: the effective fade.*
- AQ12 — An edit that removes one side of a link (track delete, media removal, Overwrite, Delete if AQ2 = no) while the
  partner is on a locked track: refuse the whole edit (L5 read strictly), or allow clearing the partner's link as a
  link-only change (as mute / hide on a locked track, D030 Q13). *Proposed: refuse (L5).*
- AQ13 — Insert / Overwrite that splits or trims a linked clip on the target track (D031 SQ6 / SQ9): the partner is split
  (AQ5) or trimmed (AQ3) too, the edit refused as a whole when the partner can't follow or is locked; or the partner is
  left alone. *Proposed: the partner follows.*

### 5. Constraints

- Unchanged in substance: D006 / D007 / D009 / D022 (grid, rate, frame selection, speed), D010–D013 (playback, the audio
  clock), D014 / D016 except the format version, D015, D017, D018 / D023 (composition, export), D025 (fades, dissolves on
  video tracks), D026, D027, D028 (settings, L1-c), D030, D031 (refined only by R7 and AQ4 for linked partners).
- The Preview ↔ Export parity suite, the L1-c criteria and every existing expected value unchanged, except the format
  version tests that pin `formatVersion` 3 on save (changed to 4 with the reason in their step) and the rule tests that
  pin "no transitions on audio tracks" (replaced by the crossfade rule).
- A v3 project of Phase 16 opens, renders and exports exactly as before (export byte for byte) and is saved as v4
  differing only in `formatVersion`.
- New controls checked in the real app at 1024 and 1440 px.

### 6. Non-goals

L7; equal-power or other crossfade curves; crossfades on video tracks' sound; a picture-only insert; links of more than
two clips or of images / text; sync markers or "out of sync" offsets display; merging an unlinked sound back into its
video clip; ripple across all tracks; keyframes, AI, HDR.

Consequences: `Clip.LinkId`, `VideoClip.IsAudioDetached`, the `"crossfade"` type; `ProjectSerializer` v4; `TransitionRules`
and `DissolveHandles` per track type; `EditPlan` link reconciliation; new `ITimelineEditService` commands (Unlink Audio,
Link, Unlink) and the link rule in the existing planners (new code in `partial` files); `PlaybackSnapshotBuilder`,
`AudioSpan`, `AudioPlacement`, `AudioFadeEnvelope` / `AudioMix` for the crossfade; the Source bar's sound-only toggle; the
timeline toolbar buttons, link marks, linked selection, Alt + edge trim; the manual plan `docs/PHASE17_MANUAL_TEST_PLAN.md`.

Answers (product owner, 2026-10-08, after Step 17.2): **AQ1–AQ13 accepted exactly as proposed**; also the smaller
choices — Unlink Audio of an already linked clip is refused (R3), one side of a linked pair is pasted unlinked (R5), a
relink to a file without sound when the asset's sound is used by audio clips gives a warning (R11), the non-goals of §6
stay. AQ11 stated: Unlink Audio keeps the sound's current **effective** fade, so the operation never changes what is heard.
The whole phase is carried out without a stop between its steps; an architectural contradiction with D032 / AQ1–AQ13 or
a scope change stops the work. Binding for the implementation: linked clips go through `EditPlan` and the existing
split / trim / ripple / transition rules (no parallel rules for linked clips); a locked partner always refuses the whole
edit; one undo step per user command; the Preview and the export stay identical.

Refined in Step 17.3 (model and format v4): `LinkId` is a property of `MediaBackedClip` (the link rule forbids it on
images); `LinkRules` (Core) checks links and is part of `TimelineValidator.ValidateSequence` and of loading; the
transition type follows the track kind (`TransitionRules.TypeFor`: "Only video tracks have dissolves." / "Only audio
tracks have crossfades." replace D025's "Only video tracks have transitions."); `DissolveHandles` gives `AudioClip` the
video rule. A v3 file is read without links and unlinked sound (stray v4 properties ignored); an audio clip of a video
file or a crossfade in a v1–v3 file is damaged. `CloneClip` copies `IsAudioDetached` but never the link. The snapshot
gives a detached video clip no sound (R10, needed for the format to mean what it says).

Refined in Step 17.4 (Unlink Audio, Link, Unlink): the link state is changed through `EditPlan.SetLink` and one
`SetClipLinkCommand` per clip, in the edit's single undo step; `EditPlan.ReconcileLinks` (called by the service's
`Validate` and by Remove Media) clears a link left without its other clip, and a cleared link on a locked track refuses the
edit (AQ12); `DeleteTrack` clears its partners' links in its own command (and `GetDeleteTrackBlockReason` names a locked
partner's track). AQ8 read so that nothing heard changes: Unlink Audio's target is the first audio track that is
**unlocked, not muted** and free over the clip (a muted or locked track is skipped, never used), otherwise a new track
named and ordered as "Add Track"; the new clip copies timing, speed, volume and mute (muted when the video track is), and
its fades are the effective ones — a stored fade that applies is kept tick for tick, one on a dissolve edge becomes 0
(AQ11). R11 needed no new code: the relink check's existing "no sound" warning of a video file (D026) covers the audio
clips of its sound too (tested). Messages: "Sound unlinked to track A1.", "The clip has no sound.", "The clip's sound is
already unlinked.", "The clip is linked: Unlink it first.", "Select one video clip and one audio clip to link them.",
"A selected clip is already linked: Unlink it first.", "Track … is locked.".

Refined in Step 17.5 (linked editing; `TimelineEditService.Linked.cs`): no rule of its own — the partner is planned into the
same `EditPlan` by the rule the edit applies, and a locked partner refuses the whole edit (L5). (1) The move rule: the
service's `Validate` gives every clip the plan moves (frame count unchanged, start changed) its partner, moved by the same
frames on its own track — so Move, the ripple shifts of Insert, Ripple Delete, Close Gap and ripple trims all take partners
along (AQ4 / AQ7) and nothing else of the partner's track moves; a partner that then overlaps refuses the edit. A drag to
another track with the pair selected (AQ1) changes the track of the clips of the target's kind only; the partner of the
other kind moves in time. (2) Trim (AQ3): the partner's same edge by the same frames, by the trim rule with its own
neighbours, dissolves and source; where one is limited both take the smaller change (also the drag preview);
`TrimClip` / `PreviewTrim` gain `alone` for Alt + edge, which trims one clip by itself (R6: J / L cuts) — allowed even when
the partner's track is locked, since the partner does not change. Q / W take the partners covering the playhead. (3) Ripple
trim: the partner takes the same edge change (its start stays, its end moves by the same frames), limited by the room after
it, since its track does not ripple (AQ4); in Shift+Q / Shift+W the video clip of a selected pair leads and the audio clip
follows. (4) Split (AQ5): the partner covering the point is split too — also by Split under the playhead, which is then
refused when that partner is locked; the two right parts get a new link, the left parts keep theirs; a partner not under
the point stays linked to the left part. (5) Speed (AQ6): the partner takes the same speed in the same (mergeable) speed
step, its frame count from its own source range. (6) Slip (AQ7): the same timeline frames, the nearer limit of the two.
(7) Delete / Ripple Delete (AQ2): the partner goes too; Ripple Delete closes up each track by its own removed clips (a pair
with a J / L offset may then move by different lengths on its two tracks — the D027 §2 rule per track; **replaced for linked
clips by the product owner's change of 2026-10-12, below: both tracks close by the target clip's length**). (8) Insert /
Overwrite (AQ13): a clip they split or trim takes its partner along by the same insert / overwrite rule applied to the
partner alone on its own track (nothing placed there); a clip Overwrite removes leaves its partner, link cleared (R5,
AQ12). (9) Copy / Paste / Duplicate (R5): the clipboard copy keeps the link; both clips of a link pasted → a new link,
one alone → unlinked.

Refined in Step 17.6 (audio crossfade and fades): the transition edits (Add, Duration, Remove, `MaxTransitionFrames`) take
an audio cut and create the type of the track's kind; their messages, undo steps and the auto-removal note name a
"crossfade" there ("Add Crossfade", "Change Crossfade Duration", "Remove Crossfade", "A crossfade was removed: its clips no
longer meet.", "There is not enough media beyond the clips for the crossfade.", "Can't split / insert / overwrite inside a
crossfade."); every video message is unchanged. The playback snapshot gives an audio track the dissolve's zones
(`Dissolves`) and each `AudioSpan` its `CrossfadeIn` / `CrossfadeOut` zone; `PlayStart` / `PlayEnd` are the samples the
clip plays — `AudioPlacement.Of(span)` covers them while keeping the source offset of the clip's own start, so the
extension plays exactly the source a trim would (D025 §4), and every user of the placement follows: both readers, the
export source, the export preflight window; the Preview's look-ahead window and reader key use the played range.
`AudioFadeEnvelope` multiplies the fade by the crossfade ramps (A `1 − ramp`, B `ramp` over the zone's samples), in the one
`AudioMix.Add` both sides call. A fade on an edge with a crossfade is not applied (the existing PO-8 rule, by track
transitions). A crossfade change is a timing change for the Preview (not presentation-only).

Refined in Step 17.7 (sound-only Insert / Overwrite): `InsertClip` / `OverwriteClip` take `soundOnly`; for a video file it
places an `AudioClip` of the file on the audio target (SQ8 with the kind "audio"; an explicit video track is refused as for
any audio), by the same SQ6 / SQ9 / SQ13 code — the clip is the only difference; a video without sound is refused
("… has no sound."); for an audio file the flag changes nothing. Its grid is the Source viewer's (SQ12), so the first
sound-only insert of a video file fixes the project rate as the file itself would (D007). "Sound only" is a toggle of the
source bar, shown for a video with sound, held by `SourceViewerService` for the session (not saved, not undoable; it
applies only while the open asset has sound); the status says "Inserted the sound of …".

Refined in Step 17.8 (UI): the timeline toolbar gets two buttons — one unlink button labelled "Unlink" while the selection
holds a linked clip (Unlink, R4) and "Unlink Audio" otherwise (enabled when the primary is a video clip with sound, R3),
and "Link" (exactly one video and one audio clip, none linked, AQ9); no shortcut keys. A linked clip carries a "🔗" mark
before its name. Selecting a linked clip selects its partner first and the clip last (it stays the Inspector's primary);
Ctrl + click toggles the pair; the commands that need one or two clips count the primary's partner once — Close Gap acts
on the primary, Dissolve on the two selected clips of the primary's track (so a crossfade between two linked audio clips
can be added). Alt + edge (`TimelineGestureModifiers.IsTrimAlone`) trims the clip alone and selects only it; a plain edge
drag shows the partner's edge following. The Dissolve command on an audio cut adds a crossfade ("Crossfade added: …"); the
Inspector's section reads CROSSFADE / "Remove Crossfade" there. A video clip whose sound was unlinked shows no audio group
and no waveform. Every new control is off during an export (`CanEdit`).

Refined in Step 17.9 (closeout): the real-app run at 1024 px found the two new toolbar buttons pushing −, + and Fit past
the window's edge; the timeline tools are now a `WrapPanel` (the same buttons and order; at 1440 px one row as before, at
1024 px −, + and Fit on a second row) — the only layout change of the phase. After the independent closeout review (FAIL on
one missing test): the AQ12 refusal of an Overwrite that removes a linked clip whose partner is locked is now tested
(picture and sound-only; its mutation caught); and Alt changes only a **linked** clip's edge — on an unlinked clip Alt +
edge is the ordinary trim and Shift + Alt + edge the ripple trim, as before Phase 17 (AQ3: "only linked clips change").
Ripple Delete of a linked pair closed up each track by its own removed clips (AQ2, "Refined in Step 17.5" (7)); a later
pair with a J / L offset could then move by different lengths on its two tracks — asked of the product owner, answered
below.

**Changed by the product owner (2026-10-12, after the Phase 17 closeout): Ripple Delete of a linked pair.** Both tracks
close by the same frames — the length of the deleted pair's **target** clip (the clip the user deletes: the one named
last, the selection's primary, or the one named) — so every later clip of both tracks moves by the same frames and later
linked pairs keep their relative offset, J / L cuts included. This replaces "Refined in Step 17.5" (7) for linked clips;
unlinked clips still close their track by their own lengths (D027 §2). Where closing a track by the target's length would
put a later clip over an earlier one (the partner shorter than the target), the whole edit is refused with the overlap
message; a locked partner or track refuses it as before (L5). One undo step.

Final state at the Phase 17 closeout (Step 17.9, 2026-10-12): every rule of §2–§4 implemented and tested (Project.Tests
`AudioEditingPersistenceTests`, Core.Tests `LinkRulesTests` / `TransitionRulesTests`, Timeline.Tests `LinkEditTests` /
`LinkedEditTests` / `CrossfadeEditTests` / `SoundOnlyInsertTests` / `AudioModelTests`, Export.Tests
`ExportAudioContractTests`, UI.Tests `TimelineLinkUiTests` / `SourceViewerTests`, ExportEndToEnd.Tests
`ExportUnlinkAudioEndToEndTests` / `ExportCrossfadeEndToEndTests` / `ExportInsertOverwriteEndToEndTests`); the gates and the
manual plan in `progress.md` and `docs/PHASE17_MANUAL_TEST_PLAN.md`; a Phase 16 project saved again differs only in
`formatVersion`, the default export byte for byte as Phase 16's, a v4 file refused by the Phase 16 build. R10 (a real
audio device) not run: it needs the product owner.

Status: Accepted (2026-10-08, product owner: variant A, L1–L7, AQ1–AQ13). R1–R13 are the pre-implementation audit's
reading of the architecture (Step 17.2), checked by an independent review (PASS WITH NOTES; its notes taken in: R3's
mix-order and dissolve-fade limits, `DeleteTrack` in R5, AQ11–AQ13). **Phase 17 complete** (Step 17.9 closeout,
2026-10-12), accepted and merged (PR #18, `fa96468`). Steps and acceptance criteria: `docs/DEVELOPMENT_PLAN.md`, "Phase 17
— Audio editing: steps".

After the merge (recorded at Step 18.2, 2026-10-13): PR #18 (head `1564c02`) merged into `main` as `fa96468` on
2026-10-13, its tree identical to `1564c02`; CI green, checked by the product owner (no separate post-merge audit, by
the product owner's choice). R10 (a real audio device) stays an open manual check outside the phases.

---

## D033 — Phase 18: HDR → SDR of phone footage (HLG and PQ, BT.2020)

Date: 2026-10-13

Decision (product owner, 2026-10-13, after the Phase 18 pre-flight — variant C — and the measurement series on real
footage; HQ1–HQ6, the L2 chain, the superwhite handling and PQ approved). Phase 18 makes HDR footage from phones look
right: every HDR video asset (HLG or PQ, BT.2020, 10 bit) is converted to the editor's SDR picture (BT.709, 8-bit BGRA)
by one fixed, exactly specified rule — the same for the Preview, the thumbnails and the export, so they stay identical
by construction (hardware decoding in the Preview, HQ5, is the one place where equality is measured, not constructed). SDR assets are not touched: their decoding stays byte for byte as before. `project.json` stays
`formatVersion` 4; the media metadata gains optional colour properties.

**Revised 2026-10-13 (Revision R1, at the end of this decision), after the product owner's visual check of the L2 export
failed (too bright, washed out):** the rule of §3 is now **S1** — a scene-light rule (the variant the product owner chose
as "Telegram-match-sat100"), rule version 2. Everything else in this decision — the formats, the metadata, the graph,
the LUT and its cache, the SDR path, the parity design, OQ1 — is unchanged. The L2 rule (rule version 1) is kept in R1
for the record.

Context (pre-flight and measurements, the code at `fa96468`, `main` after the merge of PR #18; scripts and results kept
outside the repository, in the session's scratch folder):
- Phases 0–17 merged; CI green on PR #18 (checked by the product owner).
- `FfmpegVideoDecoder.BuildArguments` decodes every video with `-vf scale=…,format=bgra` (8-bit BGRA, swscale, no
  transfer or primaries conversion); one `IVideoDecoder` serves the Preview (`SpanReader`, `HardwareDecoding.Auto`), the
  thumbnails (`ThumbnailService`, `Disabled`) and the export (`ExportFrameSource` / `ExportPictureReader`, `Disabled`).
  ffprobe reads no colour property (`FfprobeStream`, `MediaMetadata`); the analysis already reads the first frame's side
  data (`FfprobeMediaAnalysisService`, display matrix). The thumbnail cache name carries
  `ThumbnailService.CurrentRuleVersion` (1). `MediaMetadataDto` already took optional properties without a format change
  (D019 display size: older metadata kept and re-probed in the background, the project not dirty).
- The product owner's 13 files (Vivo X300 Pro ×11, Huawei P60 Pro ×2; kept in `VideosForTest/`, excluded locally through
  `.git/info/exclude`, never committed): every one HEVC Main 10, 3840 × 2160, `yuv420p10le`, limited range, BT.2020 NCL,
  **HLG** (`arib-std-b67`); Vivo: Dolby Vision profile 8.4 (RPU, HLG base layer), 60 fps, no mastering / content-light
  metadata; Huawei: HDR Vivid (CUVA) dynamic metadata, mastering display 1000 nit, MaxCLL 0, 30 fps; 9 of 13 portrait
  (display matrix −90°); three with dropped frames (VFR, D009 handles them). No PQ file.
- The current picture (V0) is washed out: about half the saturation of the phone's own display (CIELAB C\*/L\* of wall,
  skin and cloth patches 0.11–0.29 vs 0.21–0.62 on the phone's gallery screenshots).
- ffmpeg 9.0.1 essentials (the pinned CI build) has `zscale` (zimg), `tonemap`, `lut3d` and `libx265`; no libplacebo.
  zimg (source checked: release-3.0.6, `colorspace/gamma.cpp`, `operation.cpp`; FFmpeg `vf_zscale.c`) applies the HLG OOTF
  **per channel** (`arib_b67_eotf = ootf_1_2(inverse_oetf(x))`, "a per-channel correction instead of the iterative method
  specified in Rec.2100") unless `agamma=0`, which selects the BT.2100 OOTF on luminance; `zscale`'s `agamma` defaults to
  1. Measured on the pinned build: `agamma=1` equals the per-channel form (relative error 1.5·10⁻⁴), `agamma=0` the
  BT.2100 form (1.5·10⁻⁷). The per-channel OOTF raises saturation — the cause of the warm, over-saturated look of the
  first candidates. `zscale`'s `t=bt709` output is the inverse BT.1886 EOTF (L^(1/2.4)), not the BT.709 OETF (measured).
  `vf_tonemap.c`'s mobius works on max(R, G, B) and scales the three channels by one factor; `desat=0` skips its
  desaturation; an explicit `peak` ignores side data.
- Variants compared on 25 moments of 5 clips and on 3 moments matched (SSIM) to the product owner's gallery screenshots:
  clip, hable, reinhard, mobius at `npl` 100 / 203, `desat` 2, the per-channel and the luminance OOTF, max-RGB and
  per-channel curves, with and without gamut compression. Chosen: **L2** (§3). Its relative saturation matches the
  screenshots within a few per cent on skin, wall and cloth; the per-channel curve turns the blue cloth cyan and magenta
  pink (rejected); clip blows faces and lamps, hable is dull, `desat=2` washes the picture out again (rejected).
  *(L2 superseded by S1 — Revision R1; the screenshots turned out not to be a colour reference, §8.)*
- The L2 chain as a 65³ LUT (`lut3d`, tetrahedral) against a double-precision reference of the same formulas: neutral
  ramp ≤ 0.03 LSB and monotonic; real 4K crops ΔE76 ≤ 1.0 (p99.9 ≤ 0.85); synthetic saturated / gamut-edge colours
  ΔE76 ≤ 1.5 (`DOMAIN_MAX 1.1`: ≤ 2.0); the largest channel errors (up to 14 LSB) only on channels the reference clips to
  0, ~10⁻⁴ in linear light; no visible structure in a ×40 difference image. The LUT of the per-channel OOTF reproduces
  `zscale` to 3 decimals, and `zscale agamma=0` + `tonemap` reproduces the reference (ΔE76 ≤ 0.03): the formulas are
  verified by two independent implementations.
- Throughput (ffmpeg alone, 16 cores, 10 s of a 4K 60 fps clip): the current chain about 155 fps; L2 by LUT at 4K about
  56 fps (`zscale` + `tonemap` about 31 fps); at the Preview bound (≤ 1280 × 720, scaled first) about 140 fps in software,
  about 195 fps with `dxva2`. Hardware and software decoding gave bit-identical frames on this machine (3 clips × 20
  frames, two runs each).
- The pre-LUT `zscale` stage changes only the matrix and the range (checked: R'G'B' in = out within 10-bit
  quantization). The app's Preview bound gives odd sizes for portrait video (405 × 720), which `zscale` refuses on 4:2:0
  ("image dimensions must be divisible by subsampling factor").

### 1. Scope

1. Colour metadata of video assets (transfer, primaries, matrix, range, bit depth, mastering display peak, MaxCLL, the
   presence of Dolby Vision / HDR Vivid dynamic metadata) read by the analysis, kept in the media metadata, shown in the
   Inspector.
2. The HDR → SDR rule (§3) in Core, its 3D LUT generated by the application.
3. The decoder applies it to HDR assets (HLG and PQ) for the Preview, the thumbnails and the export; SDR assets decode as
   before.
4. Tests (§5), the manual plan with the product owner's real footage, the regression against the Phase 17 build.

### 2. Product decisions (product owner, 2026-10-13)

- HQ1 — Formats. HLG / BT.2020 / 10 bit: the main, verified format. Dolby Vision profile 8.4 and HDR Vivid: by their HLG
  base layer, the dynamic metadata not applied. **PQ / BT.2020 / 10 bit: in scope**, implemented by its own correct
  branch (§3, PQ), covered by synthetic tests of the EOTF, the ranges, the metadata and the fallback when it is absent —
  not by swapping one transfer function for another. If a correct PQ needs a substantially larger phase, the work stops
  and the product owner is told before that part is implemented; PQ is not cut silently.
- HQ2 — `project.json` stays v4: optional colour properties of the media metadata (written only when known); older
  metadata without them is kept and the media is probed again in the background when the file is available (the D019
  precedent: the asset stays Completed, the project does not become dirty). D028 §5: a Phase 17 build ignores the new
  properties and shows the file as before (no tone mapping) — reads it without the setting, not wrongly.
- HQ3 — The conversion: **S1** (§3; Revision R1 — it replaced L2), a 65³ 3D LUT generated by the application,
  tetrahedral interpolation.
- HQ4 — No per-asset switch (no "without tone mapping" option) in this phase.
- HQ5 — Hardware decoding allowed; the frame goes through the same post-decode chain (software filters after the
  download). Equality is measured, not required bit for bit; the tests decode in software.
- HQ6 — Performance is a measurement (M), not a pass / fail criterion: the Preview cost is small, a 4K export slower
  (about 56 fps of decoding per stream, measured in ffmpeg); the in-app measurement is part of the phase.
- Superwhites (signal above 1.0): **kept up to 1.1** by the LUT's input domain (`DOMAIN_MAX 1.1`); values above 1.1 are
  limited to 1.1, negative values to 0. *(R1: the knee's peak is now HLG's superwhite top — the scene light of 10-bit
  limited code 1019 — so an HLG signal of 1.0 renders below white (code 241.5) and the superwhites up to code 1019 keep a
  gradation; L2's peak was 1000 nit.)* Known limitation: the benefit
  for coloured light sources (the channel ratios of a coloured highlight kept instead of one channel cut) is shown
  mathematically and on synthetic values, **not yet on a real HDR clip** — the product owner's footage has no saturated
  coloured light source; neutral highlights render white in every variant (with a 1000 nit peak there is no gradation
  above 1000 nit, and the footage carries none: the sun is clipped by the camera).

### 3. The HDR → SDR rule (normative)

**Which assets.** The HDR path applies to a video asset when its metadata says transfer `arib-std-b67` (HLG) or
`smpte2084` (PQ), primaries `bt2020`, matrix `bt2020nc`, and a bit depth ≥ 10. Every other asset — SDR, untagged, or an
HDR variant outside this list (BT.709 primaries with HLG, `bt2020c`, 8-bit HDR) — takes the existing SDR path unchanged
(an unsupported HDR variant is logged, named in the Inspector and warned about by the export preflight — OQ1; nothing is
guessed from an untagged file).

**The rule S1** (Revision R1, rule version 2): the picture is rendered from **scene light**, the way a camera renders
SDR — exposure, a soft knee, the BT.709 OETF — not from display light normalized to 203 nit (L2). HLG is scene light
already; PQ (display light) is first brought to the equivalent HLG scene light by the inverse BT.2100 OOTF (the
BT.2408 PQ → HLG conversion at 1000 nit), then both take the same steps.

**Order of operations** (each step exactly once; no second OOTF, tone curve, saturation change or transfer function
anywhere):

| # | Step | HLG | PQ |
|---|---|---|---|
| 0 | `zscale`: Y'CbCr → R'G'B' (matrix BT.2020 NCL, the file's range → full), float `gbrpf32le`; transfer and primaries unchanged | `tin=arib-std-b67:t=arib-std-b67`, `pin=p=bt2020` | `tin=smpte2084:t=smpte2084`, `pin=p=bt2020` |
| 1 | Range of the signal (the LUT's input domain `[0, 1.1]`) | `< 0` → 0, `> 1.1` → 1.1 | the same; then PQ signal `> 1` → 1 (1.0 = 10 000 nit) |
| 2 | Signal → light, once | BT.2100 HLG inverse OETF (a = 0.17883277, b = 0.28466892, c = 0.55991073), continued above 1 → scene light E (1.0 = HLG's nominal peak) | BT.2100 / ST 2084 PQ EOTF per component (m1 = 2610/16384, m2 = 2523/4096·128, c1 = 3424/4096, c2 = 2413/4096·32, c3 = 2392/4096·32) → display light F in nit |
| 3 | Display light → scene light, once | **none** — no OOTF: HLG is scene light | the **inverse** BT.2100 OOTF on luminance at Lw = 1000 nit, γ = 1.2 (BT.2408's PQ → HLG conversion): `Fd = F / 1000`, `Yd = 0.2627 Fd_R + 0.6780 Fd_G + 0.0593 Fd_B`, `E = Fd · Yd^((1 − γ) / γ)` (`Yd = 0` → 0); never per channel. 203 nit → E 0.2648 ≈ HLG 75 % (0.2650) |
| 4 | Primaries | BT.2020 → BT.709, linear light (the matrix of "Refined in 18.3", unchanged) | the same |
| 5 | Gamut compression | ACES Reference Gamut Compression 1.3, unchanged (thresholds 0.815 / 0.803 / 0.880, limits 1.147 / 1.264 / 1.312, power 1.2, on linear BT.709; scale-invariant, so it is the same before or after step 6) | the same |
| 6 | Exposure | × k, **k = 0.69 · 2^−0.55 / 0.265 = 1.778 43**: the scene's reference white (HLG 75 %, E 0.265) → 0.4713, i.e. 0.55 stop below the scene light 0.69 that the BT.709 OETF encodes as 0.85 | the same |
| 7 | Tone mapping | mobius on `sig = max(R, G, B)` with **j = 0.375** and **peak = k · P**, all channels × `mobius(sig) / sig` (the `vf_tonemap.c` formula, as before); **P = 1.64** (the scene light of 10-bit limited code 1019, 1.6402, rounded as approved) | the same with **P = (Lpeak / 1000)^(1/1.2)** (the scene light of a neutral at `Lpeak`, below); skipped (the clamp only) when `k · P ≤ 1`, i.e. `Lpeak` ≤ 501 nit |
| 8 | Saturation | **none** (×1.0 — the chosen variant; the measured alternative ×0.85 was rejected, R1) | the same |
| 9 | Clamp | `[0, 1]` | `[0, 1]` |
| 10 | Encoding, once | **BT.709 OETF**: `V = 4.5 L` for `L < 0.018`, else `1.099 L^0.45 − 0.099` → R'G'B' BT.709 full range | the same |
| 11 | Output | 8-bit BGRA (the decoder's existing output) | the same |

Checks of the formulas (neutral, SDR code = V · 255): HLG 0 → 0; HLG 0.5 → 93.45; **HLG 75 % → 172.58**; HLG 1.0 →
241.5; HLG 1.0902 (code 1019) and above → 255. PQ at `Lpeak` 1000: 203 nit → 173.01, 1000 nit → 255; PQ at `Lpeak` 400
(no knee): 400 nit → 232.28; PQ at `Lpeak` 4000: 1000 nit → 234.69. The rule reproduces the approved
"Telegram-match-sat100" LUT on all 274 625 nodes within 5·10⁻⁷ (the `.cube` file's 6 decimals).

**PQ in detail (S1).** Per pixel, in this order (R, G, B in BT.2020; the symbols of the table):

1. `s_c = min(max(signal_c, 0), 1)` (step 1; the LUT's domain reaches 1.1, PQ uses `[0, 1]`).
2. `F_c = 10 000 · ( max(s_c^(1/m2) − c1, 0) / (c2 − c3 · s_c^(1/m2)) )^(1/m1)` nit — the ST 2084 EOTF per component.
3. `Fd_c = F_c / 1000` — normalization to the 1000-nit display of BT.2100's reference HLG conversion (a fixed constant,
   **not** `Lpeak`: the scene exposure must not depend on the metadata).
4. `Yd = 0.2627 Fd_R + 0.6780 Fd_G + 0.0593 Fd_B`; `E_c = Fd_c · Yd^((1 − γ) / γ)` with γ = 1.2, i.e. `Fd_c · Yd^(−1/6)`;
   `Yd ≤ 0` → `E = (0, 0, 0)`. One factor for the three channels (the inverse of the BT.2100 OOTF on luminance, so a
   colour's channel ratios are kept); never per channel, never the forward OOTF.
5. From here exactly the HLG steps 4–11 of the table (BT.2020 → BT.709, RGC, × k, mobius, no saturation, clamp, BT.709
   OETF), with the knee's `P = (Lpeak / 1000)^(1/γ)` — the scene light of a neutral pixel at `Lpeak` by steps 2–4.

Content above `Lpeak` is not limited to it before the knee: mobius continues above its peak (> 1) and step 9 clamps it,
so it renders white; without a knee (`k · P ≤ 1`) everything whose `k · E ≥ 1` is clamped to white — a neutral at about
501 nit or more.

**PQ peak** (`Lpeak`, nit, an integer; the rule as implemented in 18.3 / 18.4, unchanged):

| Metadata (as read by T1: the stream's side data, else the first frame's; the stream's value wins when both exist) | `Lpeak` |
|---|---|
| MaxCLL > 0 | MaxCLL — whatever the mastering display says (MaxCLL describes this content; the mastering display only the monitor it was graded on) |
| MaxCLL absent or ≤ 0; mastering display maximum luminance > 0 | the mastering maximum |
| MaxCLL absent or ≤ 0 and the mastering maximum absent, ≤ 0 or unreadable (a rational with denominator 0, text that is not a number, NaN) | **1000** (the fallback) |
| the chosen value < 203 | 203 |
| the chosen value > 10 000 | 10 000 |
| a fraction | rounded to a whole nit, halves away from zero (999.6 → 1000) |

MaxCLL and the mastering maximum are **stated metadata, taken as given — not a guaranteed property of the content**:
the rule never measures the picture, never checks a value against the frames and never corrects it; a file's metadata
may be missing, stale or wrong (an editor or a transcoder can keep or invent it), and `Lpeak` is only the knee's
parameter derived from it.

Not used: the mastering display's minimum luminance and primaries, MaxFALL, any dynamic metadata (HDR10+, Dolby Vision
RPU — HQ1). Contradictions are resolved by the order above and never reported as an error: MaxCLL above the mastering
maximum (e.g. 4000 vs 1000) → 4000; MaxCLL far below it (e.g. 300 vs 1000) → 300. A wrong MaxCLL is not detected: if
the content is brighter than the stated value, what lies above it renders white (above); if it is darker, the knee
starts higher than needed (nothing clips, the brightest content stays below white).

`Lpeak` sets only step 7's peak; when `k · P ≤ 1` (`Lpeak` ≤ 501 nit) step 7 is skipped, so the degenerate mobius
(`peak ≤ 1`) is never evaluated. The transition is continuous: `Lpeak` 501 → `k · P` 0.999 77, no knee, 501 nit →
code 254.97; `Lpeak` 502 → 1.001 44, knee, 502 nit → 255. For the same input the two settings differ by at most 0.18
code (neutral, 0.05–1000 nit; largest at 501 nit: 254.97 vs 254.79), because mobius with a peak just above 1 is almost
the identity: 400 nit → 232.28 / 232.18, 490 → 252.65 / 252.48, 500 → 254.76 / 254.58, 501 → 254.97 / 254.79, 502, 503 and 600 → 255 / 255; both are monotonic in the input. Examples (neutral, code): `Lpeak` 203: 100 nit → 127.88, 203 →
174.45, 400 → 232.28; `Lpeak` 300: 300 → 205.95, 600 → 255; `Lpeak` 1000: 0.1 nit → 0.95, 1 → 6.45, 5 → 24.55, 100 →
127.88, 203 → 173.01, 500 → 224.81, 1000 → 255; `Lpeak` 4000: 1000 → 234.69, 4000 → 255; `Lpeak` 10 000: 1000 →
231.61. HLG always uses P = 1.64, whatever its metadata says (its key keeps `Lpeak` 1000).

**LUT.** Steps 1–9 are one 3D LUT: 65 × 65 × 65 nodes, `.cube`, `DOMAIN_MIN 0 0 0`, `DOMAIN_MAX 1.1 1.1 1.1`, red
changing fastest, values written with the invariant culture and 6 decimals; applied by `lut3d` with tetrahedral
interpolation on float input. Generated by the application from the Core rule — a pure, deterministic function of
(transfer, `Lpeak`, the rule version) — once per key, into `%LOCALAPPDATA%\AiVideoEditor\cache\color-luts\` (the
application's cache, not the project; a few files of ~8 MB, one per key), written atomically (temporary file, then
rename); the file name carries the rule version and the key, so a rule change makes a new file; files of other rule
versions are deleted at startup; a file that fails to load is generated again. The path is escaped for the filter graph
(`:` and `\`). Nothing else in the graph converts colour.

**Graph** (HDR asset): `-vf <scale to the bound, sizes even>,zscale=<step 0>,format=gbrpf32le,lut3d=file=<LUT>:interp=
tetrahedral,format=bgra,showinfo=…` — the scaling first (in Y'CbCr, as the current graph does), both sizes even. The SDR
graph is not changed (its arguments byte for byte as before).

### 4. Technical rules (binding for the steps)

- T1 — Analysis: the stream's `color_transfer`, `color_primaries`, `color_space`, `color_range` and `pix_fmt` (→ bit depth)
  from the existing `-show_streams` call; the Dolby Vision profile from the stream's "DOVI configuration record"; the
  mastering display and content light level from the stream side data, else from the first frame's side data — the
  first-frame probe (today run only when the stream has no display hint, `FfprobeMediaAnalysisService`) runs as well for
  a video whose transfer is HLG or PQ, and reads those entries too (HDR Vivid presence included).
- T2 — Persistence: the new `MediaMetadataDto` properties are optional; for a video asset analysed by Phase 18 the
  **pixel format is always written** (every decodable video has one) and marks "colour probed"; the colour properties are
  written when the file states them (an untagged file has none, and is SDR). A v4 file without them reads unchanged; a
  Phase 17 build reads a Phase 18 file without them (D014: unknown properties ignored).
- T3 — Older metadata (a video asset with saved metadata but no pixel format): probed again in the background when its
  file is available (the D019 path), not dirty; a failed probe or a missing ffprobe keeps the old metadata, and the asset
  is then treated as SDR, exactly as in Phase 17. Until its colour is known the asset decodes by the SDR path. When the
  probe completes and changes the asset's path to HDR, its open Preview readers / decoders are reopened and its thumbnail
  is regenerated (the thumbnail's rule version and the asset's colour are in its cache key). The export preflight blocks
  (with the existing "still being analysed" reason) only while such a background probe of a used asset is **pending or
  running**; it never blocks an SDR, untagged or unprobeable asset — no regression of Phase 17 exports.
- T4 — One builder of the decode graph for the Preview, the thumbnails and the export; the same LUT file for the same
  key; `ThumbnailService.CurrentRuleVersion` → 2 (old thumbnails regenerated once).
- T5 — Hardware decoding: the frame is downloaded and then goes through the same filters; the decoder's existing software
  fallback unchanged.
- T6 — No project mutation is added: nothing new is undoable or dirty; the edits, the `EditingLock`, locked tracks and
  `project.json` semantics are untouched; no ffmpeg in an edit.
- T7 — Core holds the rule and the LUT generator (pure, unit-tested); Video builds the graph; UI only shows the
  metadata. No ffmpeg filter string in Core.

### 5. Tests (minimum, each with a mutation the test must catch)

- Core *(R1: S1)*: HLG OETF / inverse round trip; 75 % → E 0.264962, 1.0 → 1.0; **no OOTF on HLG** (a scene-light value
  reaches step 4 unchanged); PQ EOTF at 0 / 100 / 203 / 1000 / 10 000 nit and its inverse; the **inverse OOTF on PQ** on
  luminance (`Fd` (0.8, 0.7, 0.6) → (0.844955, 0.739336, 0.633716) — fails if the per-channel form (0.8303, 0.7429,
  0.6533) or the forward OOTF is used); the exposure k; mobius as `vf_tonemap.c` with j = 0.375 (identity below j, 1 at
  the peak, monotonic), the HLG peak k · 1.64, the PQ peak k · (Lpeak / 1000)^(1/1.2) and its skip at `k · P ≤ 1`;
  gamut compression unchanged; the BT.709 OETF (linear part below 0.018; fails if the inverse BT.1886 returns); no
  saturation step; the whole chain: neutral in → neutral out, monotonic, black → 0, the check values of §3, a double
  transfer / OOTF / tone map would fail it.
- LUT domain and boundaries: inputs < 0, 0, 1.0, between 1.0 and 1.1, 1.1 and above 1.1 (HLG: > 1.1 equals 1.1; PQ: > 1
  equals 1); neutral and coloured.
- *(R1)* **LUT accuracy, shadows and gradients** — a measurement with a pass / fail criterion, run for HLG and for PQ at
  `Lpeak` 203, 1000 and 4000:
  - Method — **identical inputs, the same colour space on both sides**: each sample is one triplet of the LUT's own
    input — the HDR **signal** R'G'B' in BT.2020 at full range, exactly what step 0 hands to `lut3d` — written once as
    float32 into a `gbrpf32le` frame (the plane order G, B, R mapped explicitly) and run through the application's
    generated `.cube` by ffmpeg's `lut3d` (`interp=tetrahedral` — the application's own filter); the reference is
    `HdrToSdr` in double precision on **the same float32 values** (widened, not regenerated), so neither side gets a
    Y'CbCr conversion, a range change or an input rounding the other does not. Both outputs are R'G'B' BT.709 full range
    in the S1 encoding (BT.709 OETF), read as float (no 8-bit rounding) for C1–C4 and rounded to 8 bits only for C5.
    For PQ the samples are **PQ signal values** (10-bit codes, or nit values encoded by the ST 2084 inverse EOTF), and
    both sides take the whole PQ chain on them — EOTF → ÷ 1000 → inverse OOTF → the S1 steps; never linear light or
    HLG-coded values fed to a PQ LUT, and a PQ LUT is checked with the `Lpeak` of its own key. The harness proves its
    own path first: the §3 check values (HLG 0.5 / 0.75 / 1.0 / code 1019; PQ 0.1 / 1 / 5 / 100 / 203 / 1000 nit at the
    listed `Lpeak`) come out of both sides within the criteria before any other sample is evaluated.
  - Error **e = |V_LUT − V_rule| · 255 per channel, in 8-bit code values (LSB)** — ΔE is not used near black, where it
    hides large relative errors.
  - Samples: **(a) black** — signal (0, 0, 0) and negative inputs; **(b) near black, neutral** — every 10-bit
    limited-range input code (`(code − 64) / 876`) whose rule output is ≤ code 32 (the darkest eighth of the range),
    which also covers the points between LUT nodes (the nodes are ≈ 15 input codes apart); **(c) near black,
    coloured** — the same input codes along six fixed directions in signal space: (1, 0, 0), (0, 1, 0), (0, 0, 1),
    skin (1, 0.75, 0.6), warm (1, 0.85, 0.5), cool (0.5, 0.7, 1), each scaled so its largest channel steps through
    those codes; **(d) gradients** — the neutral ramp and the six directions over every 10-bit input code from 64 to
    1019, then on to the 1.1 domain in the same steps.
  - Criteria: **C1** black → exactly 0 (|V| ≤ 10⁻⁶) on every channel; **C2** near black (b, c): max e ≤ 0.5 LSB;
    **C3** gradients (d): neutral max e ≤ 0.5 LSB, coloured max e ≤ 1.0 LSB; **C4** monotonic: on every ramp each
    channel the rule makes non-decreasing stays non-decreasing in the LUT's output, and the neutral ramp stays neutral
    (|R − G|, |G − B| ≤ 0.1 LSB); **C5** banding — an **additional** check of bands and jumps, **not a replacement**
    for the absolute-error criteria C1–C3 (all of C1–C5 must pass): after rounding to 8 bits, the LUT's code differs
    from the rule's by at most 1 on every sample, and the LUT never steps by ≥ 2 codes between adjacent samples where
    the rule steps by ≤ 1. The earlier criteria stay as well: real 4K crops ΔE76 ≤ 1.0, synthetic saturated /
    gamut-edge colours ΔE76 ≤ 1.5 (the 1.1 domain ≤ 2.0).
  - Report (recorded in `progress.md`), **separately for HLG and for PQ at `Lpeak` 203, 1000 and 4000**: per criterion
    the maximum error, pass / fail, and the worst samples' coordinates — the sample set and direction, the input signal
    R'G'B' (with its 10-bit code, and the nit value for PQ), V_rule and V_LUT per channel; the error curves of (b)–(d).
  - If any criterion fails, the step stops: the measured results and the options to fix it are presented to the
    product owner; no option — a larger LUT, a shaper curve before the LUT, a change of the graph — is chosen without
    the product owner's decision.
  - **Refined after Step 18.9 (product owner, 2026-10-14) — for HLG; S1, the 65³ LUT and tetrahedral interpolation
    unchanged.** Step 18.9 stopped on C3 / C4 and the 1.1 domain; a study (dense random sampling, real frames; outside
    the repository) located the LUT's error at the kinks of the rule itself — where the largest channel of max(R, G, B)
    changes while the knee is active, at the top clamp, at the clamp to 0 of out-of-gamut colours — and found it
    accurate wherever the rule is smooth (real frames ≤ 0.61 code). The criteria, as now binding for HLG:
    - **C1, C2** unchanged (measured 0 and ≤ 0.20 code).
    - **C3** unchanged (≤ 0.5 neutral / ≤ 1.0 coloured) on every sample whose LUT cell is smooth for the rule (measured
      0.05 / 0.20). **Exception, ≤ 1.0 code:** samples whose LUT cell crosses the clip — one of the cell's eight nodes
      (65³ grid) has a channel whose value before the clamp of step 9 is ≥ 1, so the rule has a kink inside that
      cell and the linear interpolation cuts its corner (error ≈ slope jump × cell size / 4). Applies to C3 only, only
      to those cells; measured 0.52 (the brightest superwhite, code 1019: 254.48 instead of 255; the cool ramp near
      code 1004).
    - **C4:** the neutral ramp strictly non-decreasing and neutral (unchanged; measured 0 drops); on every ramp no
      reversal of the 8-bit output code where the rule does not decrease (measured 0) and the largest channel never
      decreasing (measured 0); a non-largest channel may drop in float by **≤ 0.1 code** — a tenth of an 8-bit step,
      below what the 8-bit output can show, and the reversal check above still catches any visible one. Measured: ≤
      0.0094 code, only where that channel of the rule is nearly constant (its step ≤ 0.014 code per input code) — the
      piecewise-linear pieces of the tetrahedra there take the opposite sign; each drop is smaller than the LUT's own
      absolute error at the point.
    - **C5**, synthetic saturated / gamut-edge colours (ΔE76 ≤ 1.5; measured 1.21) and real 4K crops (ΔE76 ≤ 1.0;
      measured 0.72) unchanged.
    - **The 1.1 domain:** the threshold ΔE76 ≤ 2.0 kept; the measured 2.001 is recorded as a borderline deviation in a
      cell that crosses the clip (a yellow-green superwhite), not a reason to change the threshold.
    - **Real footage (a bench criterion, added):** the application's LUT against the rule on every pixel of 39 frames —
      three per clip (20 / 50 / 80 % of the duration) of the product owner's 13 HLG clips, area-scaled to 1920 on the
      long side, float output — **not every frame of the videos**. Criterion: max ΔE76 < 2.3, the distribution recorded.
      Measured: 80.9 M pixels, max e 3.84 code, p99.99 2.13, e > 1 code 0.17 %, **e > 2 codes 0.014 %**, **max ΔE76
      1.65**, no pixel ≥ 2.3; by cell: smooth (86.2 % of the pixels) ≤ 0.61 code / ΔE 0.13; the max(R, G, B) ridge
      (13.1 %) ≤ 3.40 / 1.65; the top clip (0.018 %) ≤ 3.84 / 1.53; the clamp to 0 (0.67 %) ≤ 2.01 / 0.12. ΔE76 < 2.3 is a
      bench threshold, **not a guarantee that a difference is invisible**; the visual check of the application's export
      remains required (Step 18.12).
    - PQ: the same bench (the ramps, synthetic colours, PQ derived from the HLG frames) shows larger errors at the clip
      (C3 up to 3.9 codes at `Lpeak` 203, synthetic ΔE76 up to 7.0); these are properties of the algorithm on synthetic
      data, **not evidence about real PQ video**. PQ is not verified on real PQ footage (§8); no PQ criterion is
      relaxed or declared passed.
- PQ peak: MaxCLL wins; mastering maximum when MaxCLL is 0 / absent; 1000 when both absent; the limits 203 / 10 000.
- LUT generator: 65³ entries, the order, `DOMAIN_*` lines, two generations byte-identical, nodes equal to the rule, the
  name changes with the rule version and the key.
- Video (ffmpeg): the SDR arguments byte for byte as before; the HDR graph as specified (even sizes, `lut3d`
  tetrahedral); synthetic HLG and PQ fixtures generated with ffmpeg / libx265 (PQ with and without mastering / CLL
  metadata) decoded by the app's decoder within ΔE76 ≤ 1 of the reference, neutral patches neutral; the metadata read.
- Persistence: colour properties round trip; a v4 file without them reads unchanged; an older-metadata asset is
  re-probed without dirtying the project.
- ExportEndToEnd: an HLG and a PQ scene — the Preview, the thumbnail and the export equal on flat patches within 1 LSB;
  every existing parity / golden / L1-c expected value unchanged.

### 6. Constraints

- Unchanged in substance: D001, D006–D009 (an HDR file's timing and frame selection as any file's), D010–D013, D014
  except the optional properties, D015–D032; the SDR decode graph and every existing expected value.
- A Phase 17 project opens, renders and exports as before for SDR media (the default export byte for byte); saved again,
  it differs only by the new optional metadata properties (after the background probe).
- New UI checked in the real app at 1024 and 1440 px.

### 7. Non-goals

HDR export (HDR10 / HLG output) and HDR display output; applying Dolby Vision RPU or HDR Vivid dynamic metadata; other
tone curves, a user-adjustable curve, exposure or colour grading; a per-asset switch; LUT files supplied by the user;
ICC / display colour management; HDR images (photos); the HDR variants listed as unsupported in §3; deinterlacing, SAR,
other decoder limitations of `progress.md`.

### 8. Known limitations

- Coloured superwhites (§2): the gain of the 1.1 domain shown mathematically, not on a real clip.
- The gallery screenshots are orientation marks, not a colour reference (a lossy, downscaled capture of an HDR display
  path; about 25 L\* darker than any SDR render); the remaining warm shift of the gym wall (hue 66° vs 73° on the phone)
  is unexplained (Dolby Vision processing or the phone's white balance are possible causes).
- PQ is verified only on synthetic files. *(R1: S1's PQ branch — the inverse OOTF at 1000 nit and the knee peak from
  `Lpeak` — is a design from the standards, not checked against any real PQ footage or a phone's own PQ → SDR rendition;
  at `Lpeak` ≤ 501 nit there is no knee, so such a file's brightest content renders below white, e.g. 400 nit → code 232.)*
- Hardware / software equality measured on one machine (`dxva2`).
- *(R1)* S1's parameters were chosen on a limited set of real scenes: one reference clip (the apartment, warm indoor
  light) rendered to SDR by the phone through Telegram, and the visual judgement on five clips of two phones (Vivo X300
  Pro, Huawei P60 Pro), all HLG. Whether that rendition came from the phone's codec or Telegram's own converter is not
  known. They are to be checked again in the finished application on real editing work; the rule may be revised then (a
  new rule version).
- *(R1)* Against that reference S1 is deliberately more saturated (C\* +17 %; ΔE76 2.80 vs 1.90 for the ×0.85 variant;
  skin ΔE 6.3, chroma +5.5, the hue within 1.2°) — the product owner's choice, closer to the phone's own screen. Both
  variants render skin about 3 L\* darker and about 2° redder in hue than the reference (a property of the scene-light
  rule, not of the saturation).
- *(R1)* Shadows are deep (the BT.709 OETF's linear toe at −0.55 EV; reference p5 L\* ≈ 4, the same as the phone's
  rendition); a "texture loss" in dark clothes was noted for a darker variant (−0.5 EV) during the comparison.
- *(R1)* The Huawei clips (HDR Vivid) and other phones were judged visually only, not against a phone-made SDR reference.
- *(R1, Step 18.9)* HLG through the 65³ LUT: the LUT's error lies mostly at the kinks of the rule — where the largest
  channel of max(R, G, B) changes with the knee active, at the top clamp, at the clamp to 0 — up to 3.8 codes there
  (ΔE76 ≤ 1.65, 0.014 % of the measured pixels > 2 codes); the brightest superwhite (code 1019) may come out as 254
  instead of 255. Measured on 39 frames (three per clip) of the 13 clips, not on every frame; the product owner's visual
  check of the application's export is still to be done (Step 18.12).
- *(R1)* **PQ is not verified on real PQ footage.** Its results come from synthetic tests and from PQ data derived from
  the HLG clips; they do not show the quality on real PQ video, and full PQ support is **not** considered confirmed. The
  PQ implementation and its tests are kept as they are.
- The CI's pinned ffmpeg (the same 9.0.1 essentials build) has `libx265`, `zscale` and `lut3d`, so the synthetic HDR
  fixtures need no new skip; without ffmpeg the Video tests skip as today (ARCHITECTURE CI section).

Answered by the product owner (2026-10-13):
- OQ1 — An unsupported HDR variant (§3) takes the SDR path; it is named in the Inspector **and** the export preflight shows
  a non-blocking warning naming the file and the reason; the export is allowed. Only a file whose metadata states an HDR
  transfer (HLG or PQ) can be an unsupported HDR variant: an SDR file, or a file without colour tags, is never warned
  about, and missing metadata alone never counts as HDR.

Refined in Steps 18.3–18.5 (implementation sub-decisions inside D033):
- 18.3 — `Core.Color`: `HdrToSdr` (the rule, `RuleVersion` 1), `HdrColor` (classification, PQ peak, the Inspector text),
  `HdrLut`. The BT.2020 → BT.709 matrix is derived in double precision from the primaries and D65 (each row sums to 1;
  the rounded BT.2087 matrix tinted neutral grey by ~10⁻⁴). At `Lpeak` ≤ 203 step 7 is skipped (§3). The `.cube` carries a
  `TITLE` line (ignored by `lut3d`); the file is `hdr-r<rule>-<hlg|pq>-<peak>.cube`.
- 18.4 — ffprobe's "unknown" / "unspecified" / "reserved" count as not stated; a picture stream without `pix_fmt` is
  written as "unknown" (the T2 marker, so it is probed once); the bit depth is not stored (it follows from the pixel format;
  float formats have none); the Dolby Vision base-layer compatibility id is read and shown ("Dolby Vision 8.4 (not
  applied)"); the Inspector's row is "Color" (e.g. "HDR · HLG · BT.2020 · 10-bit · Dolby Vision 8.4 (not applied)",
  "SDR · BT.709 · 8-bit"); the runtime flag is `MediaAsset.IsMetadataRefreshing`; the preflight's wait uses the existing
  "has not been analysed yet" message; OQ1's warning is `ExportIssueKind.HdrNotConverted`.
- 18.5 — `IHdrLutStore` (Core.Interfaces) / `HdrLutStore` (Media.Caching), `AppPaths.ColorLutFolder`; the LUT is
  generated once per key and session and compared with the file (a different or damaged file is written again); a
  failure is not cached. The decoder takes the store; an HDR request without one, or whose LUT can't be prepared, fails
  with `DecoderFailed`. The thumbnail name carries the conversion (`…-v2-hlg1000.thumb`); `ThumbnailCoordinator` restarts
  an asset whose conversion changes. `PlaybackAsset.Hdr` / `AssetState.Hdr` make a conversion change a rebuild of the
  Preview.

Consequences: `MediaMetadata` / `MediaMetadataDto` colour properties; `FfprobeStream` and the analysis's first-frame
probe; a Core colour rule and LUT generator; a LUT cache; `FfmpegVideoDecoder.BuildArguments` with the HDR graph; the
thumbnail rule version; the export preflight's unknown-colour check; the Inspector's colour line; the manual plan
`docs/PHASE18_MANUAL_TEST_PLAN.md`.

Status: Accepted (2026-10-13, product owner: variant C, HQ1–HQ6, L2, LUT generated by the application at 65³, superwhites
variant B, PQ in scope). Independent review of the Step 18.2 documents: PASS WITH NOTES — its notes taken in (T1 the
probes that carry the colour data, T2 / T3 the "colour probed" marker and the preflight block only while a probe is pending,
the step-0 options written out, `Lpeak` = 203 skipping step 7, the check value's `Lpeak`, RGC in BT.709 stated, the LUT
cache location, the HQ5 qualification). OQ1 answered (2026-10-13). The product owner approved the implementation on
2026-10-13: Steps 18.3–18.7 without a stop between them, 18.8 as the closeout, branch `feat/phase-18-hdr-sdr` from
`fa96468`. Binding for the implementation: the SDR filter graph unchanged; colour processing only for HDR files with
supported metadata; a failure of the HDR conversion is reported as a decode error, never silently replaced by another
processing; no change of the project format or of other decisions beyond D033.

Refined in Steps 18.6–18.8: the end-to-end parity of an HLG and a PQ scene (the export canvas byte for byte the Preview's,
the rule within the test's ΔE76 ≤ 1 — measured at most 0.74 —, the thumbnail's flat colours equal); the re-review notes — the LUT store's clean-up once before
any write, a newly imported HDR file's thumbnail made once; the Inspector's values wrap (the Color row is wider than the
300 px panel); found in the closeout's real-app run: with an isolated profile the LUT folder could be relative, so the
store resolves it to a full path. Measured (HQ5, HQ6): in the app's decoder the conversion costs nothing measurable (4K
12.5 vs 12.7 fps — bound by the BGRA pipe; the Preview bound 162 vs 159 fps); hardware = software, 20 / 20 frames
identical (Vivo, Huawei; `dxva2`).

Final state at the Phase 18 closeout (Step 18.8, 2026-10-13; **L2** — superseded by Revision R1): every rule of §2–§4 and OQ1 implemented and tested
(Core.Tests `HdrToSdrTests`, `HdrSnapshotTests`, preflight tests; Video.Tests `FfprobeColorMetadataTests`,
`HdrDecodeTests`, `HdrLutStoreTests`, `HdrThumbnailTests`; Project.Tests `MediaColorPersistenceTests`; UI.Tests
`MediaColorRefreshTests`, `ThumbnailCoordinatorTests`; ExportEndToEnd.Tests `ExportHdrEndToEndTests`); the gates and the
manual plan in `progress.md` and `docs/PHASE18_MANUAL_TEST_PLAN.md`; the SDR graph unchanged (pinned), a Phase 17 SDR
project's default export byte for byte the Phase 17 build's, a Phase 17 save differing after the Phase 18 save only by the
optional colour properties, a Phase 18 save opened by Phase 17. Open: the product owner's visual check of an exported HDR
clip on an SDR screen; §8.
**Phase 18 complete** on `feat/phase-18-hdr-sdr`; awaiting the product owner's acceptance; not pushed.

### Revision R1 — the colour rule S1 (2026-10-13)

Why. The product owner's visual check of an exported HLG clip (L2) on an SDR screen **failed**: too bright, washed
out. Diagnosis (no implementation error — an independent audit and measurements confirmed the code matches §3 as it
then stood): phone footage places diffuse surfaces at about 100–280 nit, i.e. 0.5–1.4 × L2's 203-nit reference white,
so 50–75 % of the pixels lay above mobius' knee (j 0.3) and were compressed into codes 200–255, while the inverse
BT.1886 lifted the shadows (L\* p5 14–18). The gallery screenshots, L2's reference, proved unusable as a colour
reference: Android stores an HDR frame as a darkened SDR capture with HDR headroom (mean L\* 35–40, no white above
L\* ≈ 70).

Measurement series (scripts and results outside the repository, in the session's scratch folder; every variant rendered
by the application's own export pipeline with only the LUT file swapped — the L2 render byte-identical to the app's
export; 1920 × 1080, 25 fps, SDR BT.709 tags):
- Controlled variants of L2 (−0.5 / −1 EV, BT.2390 EETF, the BT.709 OETF, longer linear part) and scene-light variants
  (scene light → exposure → mobius → BT.709 OETF, BT.2408 style) at 0, −0.25, −0.5 EV; split-screen comparisons; the
  product owner preferred the scene-light family between −0.25 and −0.5 EV.
- **A phone-made SDR reference:** the apartment clip passed through Telegram on the phone (HDR → SDR, 480 × 852, the same
  1423 frames). The scene-light model fitted to it (exposure, knee j, saturation, shadow blend) at six moments: EV −0.55,
  j 0.375, saturation ×0.85, the pure BT.709 OETF — ΔE76 1.94 (the level of the reference's compression), the same
  parameters and 1.9–2.0 on each held-out moment; L2 16.1, scene −0.5 EV 3.3. The phone's own HDR → SDR is a scene-light
  rendering, not L2's display-light one.
- "Telegram-match" (×0.85) and "Telegram-match-sat100" (×1.0) compared on all five scenes and measured against the
  reference: ΔE76 1.90 / 2.80, C\* 13.1 / 15.5 (reference 13.2), L\* equal; skin ΔE 3.25 / 6.28, the poster 1.00 / 5.59,
  yellow-lit surfaces 2.79 / 4.38; no hue rotation toward red from the higher saturation (skin Δh −2.2° / −1.2°).

Decision (product owner, 2026-10-13): **S1 = "Telegram-match-sat100"** (§3) — saturation ×1.0, because it matches the
phone's own screen better, accepting the larger ΔE against the Telegram rendition. The basis for the rest of Phase 18;
to be re-checked in the finished application on real editing work and revised if needed (a new rule version). PQ by the
same principles (§3: the inverse OOTF to HLG-equivalent scene light, the knee peak from `Lpeak`).

What changes against the implemented L2 (rule version 1):
- §3 steps 2–10 (the OOTF removed for HLG, the inverse OOTF added for PQ, the 203-nit normalization replaced by the
  exposure k, mobius j 0.3 → 0.375 with the peak k · P, the inverse BT.1886 replaced by the BT.709 OETF; no saturation
  step); `HdrToSdr.RuleVersion` 1 → 2, so every LUT is generated again (the existing key and the start-up clean-up of
  other rule versions).
- The thumbnail of an HDR asset carries the rule version in its colour tag (today `…-v2-hlg1000.thumb` has none, so a
  rule change would keep the old picture): e.g. `…-v2-r2hlg1000.thumb`; SDR thumbnails keep their names (no
  regeneration).
- The HDR expected values of the tests (§5 Core, the Video fixtures' reference, the HDR end-to-end scenes) follow the
  new rule; the SDR expected values, the SDR graph and every parity / golden / L1-c value stay unchanged.

Unchanged: §1, §2 (except HQ3), the asset classification and OQ1, step 0 and the graph, the LUT (65³, `DOMAIN_MAX 1.1`,
tetrahedral, the `.cube` format, the cache, its key `transfer + Lpeak + rule version`), the BT.2020 → BT.709 matrix and
the gamut compression, the PQ peak rule, §4 T1–T7, the metadata and `project.json` v4, hardware decoding.

The superseded rule L2 (rule version 1, implemented in `bb7da2e`–`b321087`), for the record: HLG inverse OETF → the
BT.2100 OOTF on luminance (Lw 1000, γ 1.2) → ÷ 203 → BT.2020 → BT.709 → RGC → mobius j 0.3 on max RGB, peak 1000 / 203
(PQ: `Lpeak` / 203, no OOTF) → clamp → `L^(1/2.4)`; HLG 75 % → 217.

Status of R1: **accepted** — the text and the plan (docs/DEVELOPMENT_PLAN.md, Steps 18.9–18.12) approved by the product
owner (docs committed as `1e07a73`, 2026-10-13); Step 18.9 stopped on the LUT accuracy criteria, the product owner
accepted S1 with the 65³ LUT for HLG and refined the criteria (§5 "Refined after Step 18.9", 2026-10-14); PQ stays
unverified on real footage (§8). Steps 18.10–18.12 done (2026-10-14): thumbnails and LUTs keyed by the colour rule
(`…-v2-r2hlg1000.thumb`, the rule-1 LUTs removed before the LUT store's first write in a session), the real footage (the app's LUT within 1 code of the
approved cube on all 13 clips; the UI export within the encoder's noise of the approved render), the closeout gates green
(passed 3196, skipped 2, failed 0; heavy 3198 / 0 / 0). Open: the product owner's visual check of the S1 export; PQ on
real PQ footage. Found at 18.11: one LUT node differs from the approved cube by 2.48·10⁻⁴ (0.06 code) — it lies on the
BT.709 OETF's break at L = 0.018, where the standard's rounded constants jump from 0.081 to 0.08124.

---

## D034 — AI hold markup MVP

Date: 2026-10-14

Decision (product owner, 2026-10-14, specification revision 5, after the AI pre-analysis and the experiments E1 / E2
run outside the repository). The product's main function is AI processing of bouldering video; its first function is
the markup of a route's holds with their real contours. Implementation not started — it needs a separate permission;
the phase number is assigned then.

### 1. Product scenario

- On a paused frame of a selected **video clip** (Timeline mode, the playhead inside the clip) the user switches the
  Preview into a markup mode (Inspector, section "Holds"). The frame under the playhead is decoded at full resolution by
  the existing decoder with the same HDR rule as the Preview (D033) and encoded by the model in the background, with
  progress and cancellation.
- A click on a hold creates a **candidate** (mask preview: contour and translucent fill, not yet in the markup). For a
  single-point candidate the wheel / Tab cycles through SAM's three masks; masks larger than 2 % of the frame are hidden.
  *(Amended 2026-10-15: masks over 2 % up to 20 % are offered when the model's score is ≥ 0.9 — implementation notes
  below.)*
- **Additive click** (Shift+click) adds a positive point to the **current candidate**; the model recomputes one mask from
  all its points. It refines one hold and never creates a new one; a multi-point candidate has no alternatives; without
  a candidate Shift+click acts as a plain click.
- Enter / a button confirms the candidate: one hold added to the clip's markup, one undo step. Esc discards the whole
  candidate. A click on a confirmed contour (no active candidate) selects it; Delete removes it. Zoom / a loupe for
  precise clicks. "Done", Seek or Play leave the mode; the confirmed markup stays, a candidate is discarded.
- Confirmed contours are drawn over the clip for its whole duration as one route outline.
- Not in the MVP: automatic route detection, automatic proposals, the route animation and camera motion compensation
  (future ideas — ROADMAP, DEVELOPMENT_PLAN "Future ideas"), tracking, occlusion handling or reconstruction of hidden
  edges, vertex editing, exclusive clicks, per-hold colours, fills, numbers, the Source viewer, images and text clips,
  batch markup, the Tiny / Base+ models.

### 2. Model and ONNX — "sam2.1-large-v1"

- SAM 2.1 Hiera Large: checkpoint `sam2.1_hiera_large.pt` (release 092824,
  `dl.fbaipublicfiles.com/segment_anything_2/092824/`), SHA-256
  `2647878d5dfa5098f2f8649825738a9345572bae2d4350a2468587ece47dd318`; code `facebookresearch/sam2` at commit
  `2b90b9f5ceec907a1c18123530e92e794ad901a4`.
- Exported with samexporter 0.5.0 under PyTorch 2.14.1, ONNX opset 18 (byte for byte reproducible in that environment —
  checked; other versions may give other files, which the application does not accept):
  `sam2.1_large.encoder.onnx` SHA-256 `5ca0f505138811202a60445427383559fb84afcd80e7a5e2d1ce5d9f546e3362` (852 451 097
  bytes), `sam2.1_large.decoder.onnx` SHA-256 `d2ef7f723ed226f424d590cd382bfef135fdfd4cb74f3a76f6bd563a63b28f10`
  (16 519 627 bytes).
- Placed by hand in `%LOCALAPPDATA%\AiVideoEditor\models\sam2.1-large\` — one copy per machine; never in the repository
  or a project; never downloaded, replaced or repaired by the application. The checksums are verified at the first load
  in a session; a missing or mismatching file disables **only** the AI hold markup, with a message naming the file, the
  folder and the expected version; the rest of the work is never blocked.
- Licences: SAM 2 code and checkpoints Apache-2.0 (`cc_torch` BSD-3-Clause, not part of the ONNX); samexporter, ONNX
  Runtime MIT; onnx Apache-2.0. `docs/AI_MODEL.md` (written with the implementation, before acceptance) records the
  checked licences and notices of every pinned component — the exact `LICENSE` and the presence / content of `NOTICE` of
  SAM 2 at the pinned commit (not yet confirmed: GitHub answered 503 at the check), `LICENSE_cctorch`, the checkpoint
  licence statement, samexporter 0.5.0 with its third-party licences, ONNX Runtime, onnx — each with source, date and
  conclusion; and the full reproducible export recipe (Python version, exact package versions and sources, the
  checkpoint download and SHA-256, the export command, the expected ONNX SHA-256s). Should the weights ever be
  distributed: the Apache-2.0 text, the attribution notices and NOTICE (if any), a statement of the ONNX conversion. An
  export script is not part of the MVP.

### 3. Inference

- Local only: ONNX Runtime with the DirectML execution provider (`Microsoft.ML.OnnxRuntime.DirectML`, MIT). Core holds the
  markup types and `IHoldSegmentationService` without any ML dependency; a new project hosts the ONNX implementation
  (pre- / post-processing as in E1: resize to 1024, ImageNet normalization, logits upsampled, threshold 0, outer
  contour, simplification); the session is created lazily and reused.
- CPU fallback: on a known error creating the DirectML session (no provider, no DirectX 12 adapter, a driver error) or a
  known DirectML run error (device removed, out of memory) the operation is retried once on the CPU where that is safe
  and the session stays on the CPU, with a non-blocking status-bar message ("AI runs on the CPU — processing may be
  slower"). Unknown errors and errors of the CPU path abort the operation with a message; no recovery is promised.
- Cancellation through ONNX Runtime's run cancellation. The project changes only through the confirm command:
  cancellation or an error never applies a partial result, never makes the project dirty, never blocks the Timeline or
  the export.

### 4. Stroke

- Output stroke = `StrokeWidth1080 × (short side of the canvas / 1080)`, default `StrokeWidth1080` = 4 (1920 × 1080 and
  1080 × 1920 → 4 px, 1280 × 720 → 2.67 px, 3840 × 2160 → 8 px); set by the user. The output is the project canvas
  (D028), so a canvas change rescales it. Independent of the clip's scale (the pen lives in canvas coordinates). The
  width includes the core and the keyline. One rule for the Preview and the export; the Preview alone never draws a
  line thinner than one screen pixel (the export is unaffected).

### 5. Route colour and keyline

- One outline colour for all holds of the clip; never per-hold colours.
- **Colour samples per hold:** when a hold is confirmed, from the frame it was marked on: `SurfaceLab` — robust surface
  colour in OKLab (mask eroded by ~10 % of the equivalent radius, at least 2 px; the darkest and brightest 10 % dropped;
  median); `WallLab` — the same for a ring outside the mask (~15 % of the equivalent radius, at least 4 px, other masks
  excluded); `SourceTime` — the markup frame time. Stored with the hold; never changed afterwards.
- **Auto:** from a fixed palette (12 saturated colours — red, orange, yellow, lime, green, teal, cyan, blue, indigo,
  violet, magenta, pink — plus white and black; adjustable at the threshold calibration) the colour maximizing over the
  palette the minimum over the holds of `min(ΔE_OK(c, SurfaceLab_i), 2 × ΔE_OK(c, WallLab_i))`; ties → the larger mean,
  then the palette order. Recomputed only when holds are added or removed (and by the undo / redo of that), from the
  stored samples of all holds — holds marked on different frames each contribute their own samples; **frames are never
  decoded again**; never on open, playback, export, lighting changes, trims, slips, speed, crop or transforms; with an
  unchanged set the colour stays. A deliberate product decision: a stable colour matters more than following the light.
- **Manual:** any colour; kept as set; the samples stay stored, so switching back to Auto yields the colour of the
  current set at once.
- **Keyline** (whole route; inside the stroke width — core 50 %, keyline 25 % each side; black when the core is light,
  `L_OK > 0.6`, otherwise white): **Auto** — on when the route's minimum score for the colour in use is below the
  threshold T; **On** — always; **Off** — never; the same rule for the automatic and the manual colour; re-evaluated
  with the colour and on a change of the manual colour or the keyline mode.
- **Threshold T** is not fixed: starting hypothesis 0.20; calibrated on the E1 frames, with examples with and without the
  keyline shown to the product owner before acceptance.
- Known limit: a route of very different hold colours has no single colour contrasting with all; the maximin minimizes
  the worst case and the keyline provides the readability.

### 6. Rendering, edits, data, format

- Rendering: one draw operation of the clip's layer in `CompositionDrawPlan`, painted by `CompositionPainter` — the
  Preview and the export identical by construction (D018 / D020 / D023); points: normalized source → crop → layer matrix
  → canvas; clipped to the crop rectangle; follows the layer transform; multiplied by the layer's opacity, so the
  contours fade with the clip's fades (D025) and dissolves and vanish with an opacity-0, hidden or occluded layer (D018);
  not drawn for offline media (kept). A markup change is presentation-only (`DiffersOnlyInPresentation`).
- Edits: undoable commands through `ITimelineEditService` / `EditPlan` — a confirmed hold, a removal, a style change =
  one step each; style changes merge (D017); refused on a locked track and during an export (`EditingLock`). Trim, slip,
  speed, move and track changes leave the markup as it is; Split, Duplicate and Paste give independent copies (samples
  included); crop clips the contours; position / scale / rotation carry them.
- Data: `VideoClip.HoldMarkup` (null = none) — `StrokeWidth1080`; `ColorMode` (Auto / Manual) and `ColorHex` (the resolved
  colour, stored in Auto too); `Keyline` (Auto / On / Off) and `KeylineActive` (the resolved state); `ModelId`; `Holds`:
  `Id`, `Points` (polygon in normalized coordinates of the display-oriented source frame, D019, simplified to ~1 source
  pixel), `SurfaceLab`, `WallLab`, `SourceTime` (ticks). Never stored: click points, candidates, alternatives,
  embeddings.
- Format: `project.json` **v5**; a v4 project opens without markup and is written as v5 on save; builds before v5 refuse
  a v5 file with the existing "saved by a newer version" check.

### 7. Tests

- **Regular CI** (no weights, no GPU; the segmentation service is a test double): geometry and crop clipping; the stroke
  on 1280 × 720, 1920 × 1080, 3840 × 2160 and a 1080 × 1920 canvas (±0.5 px in the exported frame of a synthetic scene),
  independent of the clip's scale, proportional in the Preview at several control sizes, the one-screen-pixel minimum in
  the Preview only; colour samples on synthetic masks with shadows and highlights; the maximin with the wall; Auto
  recomputed on add / remove from stored samples, also for holds from different frames, **without decoding a frame**;
  no recomputation with an unchanged set; Manual; the keyline Auto / On / Off for the automatic and the manual colour;
  determinism; Preview / export parity; opacity, fades, dissolves; undo / redo; Split / Duplicate / Paste (samples
  copied); the v4 → v5 migration and the refusal of v5 by a v4 build; a missing model, a wrong SHA-256; the CPU fallback
  through a simulated DirectML failure; cancellation without a partial result.
- **Serialization of the colour samples (required):** a v5 project with holds whose `SurfaceLab`, `WallLab` and
  `SourceTime` are known — including holds from different markup frames and non-round values — is saved, reopened and
  saved again: every hold's `SurfaceLab` (L, a, b), `WallLab` (L, a, b) and `SourceTime` (ticks) restored exactly, the
  second file identical to the first; after the reopen, adding / removing a hold recomputes Auto from the restored
  samples alone (no frame decoded) and yields the same colour as before saving. A file missing a hold's samples or with
  non-finite values is rejected as damaged.
- **Local suite with the real weights** (a separate test project kept out of the regular CI run — a CI configuration
  change of the implementation; required and recorded before acceptance): ONNX load and SHA-256; mask quality on the E1
  clicks; the three alternatives in the order and within median IoU ≥ 0.98 of the Python ONNX reference; refinement
  with several points giving one mask; DirectML vs CPU (median IoU ≥ 0.98); performance (frame preparation ≤ 1 s GPU /
  ≤ 10 s CPU, click preview ≤ 150 ms, on an RTX 4070 SUPER with a 4K frame). User footage never enters the repository;
  the real data stay local.

### 8. Acceptance criteria

1. The scenario of §1 complete: candidate, alternatives, additive click, confirm, delete, zoom / loupe, leaving the mode.
2. Every confirm, removal and style change one undo / redo step; style changes merge.
3. Edits, opacity, fades and dissolves as §6.
4. The stroke as §4 on 720p, 1080p, 4K and portrait; the Preview and the export consistent.
5. The colour and keyline as §5; T calibrated, its examples shown to and accepted by the product owner.
   5a. Auto recomputed from the stored samples on add / remove, also for holds marked on different frames, no frame
   decoded; unchanged with an unchanged set.
   5b. Keyline Auto by T, On always, Off never — for the automatic and the manual colour.
6. The model and its errors as §2: only the AI hold markup disabled, nothing replaced.
   6a. `docs/AI_MODEL.md` records the checked licences and notices (NOTICE included) and the full export recipe that
   yields the pinned SHA-256s.
7. The CPU fallback, the status message and cancellation as §3.
8. Format v5: the save / open cycle lossless, the colour samples included (§7); v4 opens; an older build refuses v5.
9. The regular CI green without weights or a GPU; the local real-weights suite passed and recorded.
10. On **the same E1 set** (the same 7 SDR clips, frames and 164 click points) and **the same grading protocol** (the
    highest-scoring mask, masks over 2 % of the frame dropped *(the original protocol; under the amended rule of
    2026-10-15 the result is reported separately — implementation notes)*; the product owner's О / К marks on numbered sheets of
    the same format) the share of clicks without marked problems in the C# pipeline is **at least 155/164**. It shows
    the implementation is not worse than the experiment on this set; it is not an accuracy of the model on arbitrary
    clicks and is not generalized beyond the set.
11. A manual check on 2–3 of the product owner's clips: markup, export, viewing; the contours readable and equal to the
    Preview; the time to mark a route recorded.

Context: AI pre-analysis and experiments of 2026-10-14, outside the repository (the session's scratch folder; no
repository change). E1 — SAM 2.1 Large, one click per hold, on 7 SDR clips (164 clicks) graded by the product owner:
155/164 = 94.5 % without marked problems — О 4 (the wall instead of a hold: c6 №16, c1 №16, c8 №8, c8 №24), К 5 (c0 №19
— an edge hidden by a hand, an occlusion; c1 №3 — a donut hole; c1 №8 — a C-shaped concavity cut straight; c1 №18 —
extra area; c8 №16 — the lighter part of a two-tone hold missing); a result of this sample and protocol only. HDR (the
pilot clip through S1, portrait; deliberately biased): 22/24, К 2 (donut holes). Among SAM's alternatives there was a
whole-hold mask in every donut-hole case; C-shaped concavities and occluded edges remain. ONNX + DirectML matches
PyTorch (median IoU 0.985 on 50 clicks); Large: encode ~0.2 s on the GPU (0.32 s cold) / ~4.2 s on the CPU, a click
~13 ms; GPU memory +3.0 GB. E2 — 12 SDR clips (tripod): static contours drift ≤ 6.3 px (p95 ≤ 4.2 px) at 1080p; a global
2D compensation would leave ≤ 2.8 px (an estimate) — not in the MVP; the HDR pilot clip (78 s, phone stabilization on)
drifted up to 13–14 px. Automatic proposals (SAM's grid generator) found the holds but cost ~25 s per frame (Tiny at half
resolution) to minutes; RF-DETR-Seg was not tried (no dataset with a verifiable licence; not needed after E1).

Consequences: a new dependency `Microsoft.ML.OnnxRuntime.DirectML` (DirectML is in sustained engineering; WinML is the
successor path); a ~870 MB model outside the repository with pinned checksums; a new project in the solution;
`project.json` v5; pointer input in the Preview; a CI configuration change keeping the real-weights suite out of the
regular run; `docs/AI_MODEL.md`. ROADMAP's "left out: AI features" no longer holds (updated 2026-10-14).

Status: Accepted (2026-10-14, product owner: specification revision 5). Implemented in Phase 19 (branch
`feat/ai-hold-markup`, 2026-10-14, the product owner's permission of the same day); the decision text above unchanged. Vertex editing (§1 "Not in the MVP") is taken up for confirmed holds by D035 (2026-10-15, Phase 20).

Implementation notes (Phase 19; technical choices within the decision, not changes of it):
- Multi-point prompts: the pinned decoder was exported with `multimask_output = True` and always returns three masks; a
  candidate refined by Shift+click shows the highest-scoring visible one (masks over 2 % hidden) — "one mask, no
  alternatives" as §1 (`docs/AI_MODEL.md`).
- `DirectML.dll`: the redistributable (`Microsoft.AI.DirectML`, a dependency of the ONNX Runtime package) is under the
  Microsoft Software License Terms, not a permissive licence; it is not shipped — ONNX Runtime loads the Windows copy and
  the §3 CPU fallback covers its failure. Shipping it is left to the product owner.
- SAM 2 has no `NOTICE` file at the pinned commit (confirmed 2026-10-14 through the GitHub API listing of the commit's
  tree; the §2 check had been blocked by a 503).
- Threshold T calibrated at Step 19.6 to **0.12** (from the 0.20 hypothesis) on 24 E1 routes; the Auto colour scores
  ≥ 0.50 on every one of them, so T decides for manual colours. The product owner's acceptance of the examples: manual
  plan item 16.
- T set to **0.16** (product owner, 2026-10-15) after the T report: below 0.12 the keyline is needed, in 0.12–0.20 it
  trades a crisper edge for half the coloured core. Same samples and protocol: of the 24 E1 routes outlined in their
  own hold colour the keyline turns on for 9 at 0.16 (5 at 0.12, 16 at 0.20; added: c10 blue, c10 yellow, c6 red, c7
  teal); the Auto colour turns it on for none (Auto scores ≥ 0.33). Examples: `T016_examples.png` with the calibration.
- Auto colour — saturated colours first (product owner, 2026-10-15; refines the §5 Auto rule, whose palette D034 lets
  the calibration adjust; the §5 text above kept as accepted): the maximin over the 12 saturated colours is taken when
  its route score reaches T (the line reads without a keyline — the readability bound calibrated on the E1 frames, no
  other weight); otherwise the better of white and black, when it reaches T; otherwise the maximin over all 14 with
  the Auto keyline. Recomputed only when the holds change, as before. On the 24 E1 routes: saturated on all 24 (lime 14,
  magenta 7, yellow 2, indigo 1; before: black 14, white 5, lime 5), the minimum route score 0.325–0.659 (median 0.460;
  before 0.502–0.773, median 0.545) — at least twice T, no keyline anywhere. Comparison and examples:
  `palette_old_vs_new.png` and `T_REPORT.md` with the calibration.
- §1 candidate rule — **amended 2026-10-15** (product owner, variant A; the original rule above kept as history):
  masks up to 2 % of the frame are offered as before; masks over 2 % and up to 20 % are offered when the model's score
  is ≥ 0.9; masks over 20 % stay hidden. Offered masks keep the order by score (the best one first) — no Tab step is
  added to the main flow. Reason: in a close-up a real hold can exceed 2 % of the frame; on the owner's clip
  `VID_20251223_193501.mp4` three holds of 2.48 / 3.39 / 6.18 % had the model's best mask (score 0.99, IoU ≈ 0.95 with the
  owner's outline) hidden, so the app offered only parts (IoU 0.07–0.71) or nothing, and Shift+click could not help (it
  converged on the same hidden mask). **A high score does not guarantee the right object:** a large mask can take the
  wall, the floor, the ceiling or a person. Found with scores ≥ 0.9 on four more of the owner's close-ups (clips 5, 15,
  29, 32 of 2026-10-15): a wall panel 18.7 %, the floor 9.0 % and 18.4 %, the ceiling 5.6 % and 2.3 %, a wall-bottom strip
  2.4 %, graffiti areas 2.8 / 3.8 %, people 2.3–4.9 %. A click on the hold still gives the hold; a click on such an area
  can now offer it as the candidate (confirmed only by Enter). The bounds 2 % / 20 % / 0.9 are provisional — from E1,
  one problem frame and four close-ups — and are not universally safe. Data: `<E1>/results/large-holds-20261015/`.
  The correction of a wrong boundary without cycling alternatives is the next product stage (`docs/DEVELOPMENT_PLAN.md`,
  "Future ideas"), not Phase 19.
- §8 criterion 10 under the amended rule: the original result **156/164** (the 2 % rule, recorded 2026-10-14 / 15) stays
  the accepted metric; the same set and grading rule with the amended candidate rule gives **156/164** as well (local
  suite `<E1>/results/20261015-121935/`, RECHECK 0 — the amended rule changes the choice on none of the 164 clicks: every
  E1 mask over 2 % scores ≤ 0.58). The two are reported separately.
- The markup frame is decoded by the application's decoder (software, the D033 HDR rule) at the asset's display size;
  on the E1 clip c6 at 0.5 s it is bit-identical to the E1 reference frame (local `MarkupPipelineTests`).
- §7 performance — reading of the criterion (product owner, 2026-10-15; the §7 text and its thresholds unchanged): the
  §7 text "frame preparation ≤ 1 s GPU / ≤ 10 s CPU, click preview ≤ 150 ms" does not name the statistic or the stages.
  It is read as: **frame preparation** = from a decoded frame in memory to the model being ready to return masks for
  the first click (the preprocessing and the image encoder), with the sessions loaded; the GPU **median** must be
  ≤ 1 s. The frame's **decode** is measured and reported separately and is not part of it. The **cold start** (the
  SHA-256 check and the session creation, once per session) is measured and reported separately, is not a failure of
  the metric, and must be visible to the user through the markup's progress. The **full user scenario** (from Mark
  holds to clickable, with the decode and, when needed, the cold start) is reported as well. Definitions, conditions
  and values: `docs/AI_MODEL.md`, "Performance".

---

## D035 — Manual correction of hold contours (Phase 20)

Date: 2026-10-15

Decision (product owner, 2026-10-15: the Phase 20 assignment; extends D034, whose §1 listed "vertex editing" as not in the
MVP — that line is superseded by this decision for confirmed holds; nothing else of D034 changes). The user corrects a
wrong boundary of a confirmed hold directly (a mask that took part of the floor or the wall, a part of a hold) instead of
cycling the model's alternatives. Built on the proposal of 2026-10-15 (`docs/DEVELOPMENT_PLAN.md`, formerly "Future ideas").

### 1. Gestures (markup mode, a selected confirmed hold, no candidate)

- The selected hold shows **handles** at its vertices (white squares; the one under the pointer yellow). Only in the
  markup mode's overlay — never in the composition, so never in the export.
- **Drag** a handle: the contour follows the pointer live; the release is **one** undo step. A point outside the frame is
  clamped to it. Esc during a drag abandons it (nothing changes).
- **Alt+click on the outline** (within 6 view pixels of an edge) inserts a vertex there; it becomes a handle. On a dense
  contour, where an edge is about a pixel long, a click within half a frame pixel of a hidden vertex makes that vertex a
  handle instead (no geometry change, no undo step). With Alt held the outline wins over a handle near by; Alt+click
  away from the outline only says where to click; it never starts a candidate.
- **Delete with the pointer over a handle**, or a **double-click on a handle**, removes it; Delete elsewhere removes the
  selected hold as in D034. At least **three** handles stay (so at least three vertices): a removal below that is refused
  with a message.
- **No self-crossing**: while a drag crosses or touches the contour it is drawn red and its release is refused with a
  message; an insert or a removal that would cross is refused the same way (also by the edit service). Crossings the
  traced contour already had among the edges an edit does not change (a mask can touch itself at a one-pixel neck) never
  block an edit elsewhere.

### 2. Dense contours — simplified handles, lossless geometry

- A contour from the model is simplified to ~1 source pixel (D034 §6) and can have hundreds of vertices. Up to 32
  vertices every vertex is a handle; above that the handles are the Douglas–Peucker subset at 2 % of the contour's
  bounding-box diagonal (at least 1.5 source pixels). The handles are then thinned to at least 14 screen pixels apart for
  the Preview's current size (never fewer than three; re-derived when the size changes by more than a quarter) — found in
  the real-app check, where a small hold in the Preview showed overlapping handles.
- The stored polygon is **never simplified** by the editor: a drag moves the handle's vertex to the pointer and the hidden
  vertices between it and its neighbouring handles by a share of the move growing linearly with their arc length towards
  it; every other vertex stays exactly as it was and the vertex count is unchanged. A removal replaces the stretch between
  the removed handle's neighbours by a straight edge (that stretch's hidden vertices go with it — the user's edit). An
  insert adds one vertex on an edge of the full polygon.

### 3. Data, undo, locks

- A correction replaces the hold's `Points` under the **same id**; `SurfaceLab`, `WallLab` and `SourceTime` stay as taken
  at the confirm (D034 §5: never re-sampled), so the route colour and the keyline do not change. Limits as D034 (3–8192
  points inside [0, 1]). `ITimelineEditService.ReplaceHoldContour` → one `SetHoldMarkupCommand` (field `Contour`, "Edit
  Hold Outline"), never merged with another step; an unchanged contour is no step.
- Refused on a locked track (the edit service) and during an export (`EditingLock` — the mode leaves, a running drag is
  dropped). Split / Duplicate / Paste copy the corrected contour like any other.
- `project.json` stays **v5** with the same members — no format change (a corrected contour is an ordinary polygon).

### 4. Preview and export

Unchanged by construction: both draw the stored polygon through the one `CompositionDrawPlan` outline path (D034 §6).

Not in this decision: negative segmentation points, new recognition modes, the route animation, timeline / localization /
other UX work.

Context: Phase 19's amended candidate rule offers large masks that can take the wall, the floor or a person (D034
implementation notes); the product owner wants a fast correction with as few actions as possible.

Consequences: no format or dependency change; one new edit method and command field; the markup overlay grows the handle
gestures. Editing needs the markup mode, so the model must be installed (the frame is prepared as for marking).

Status: Accepted (2026-10-15, product owner: the Phase 20 assignment). Implemented in Phase 20 (branch
`feat/phase-20-hold-contour-editing`); the implementation accepted by the product owner on 2026-10-15 after their check on
their own clips.

---

## D036 — Animation of the hold outlines (Phase 21)

Date: 2026-10-15

Decision (product owner, 2026-10-15: the Phase 21 specification with the answers to its five questions — the start bound
to the source time, Bottom to top by default with Marking order supported, the defaults 0.5 / 0.25 / 3 / 0.5 s, format v6,
the dashed outlines of the markup mode). Extends D034 (whose §1 listed "the route animation" as not in the MVP — superseded
for the outlines' appearance by this decision) and keeps D035 as it is. The idea of 2026-10-14 (`docs/DEVELOPMENT_PLAN.md`,
"Future ideas") is taken up here.

### 1. Scenario

- With the animation on, a clip's hold contours: hidden before the start; from the start the holds appear one by one
  every *Interval*, each fading in over *Appear fade*; an appeared hold stays; after the last one every contour stays
  *Stay after last* and then all disappear together over *Disappear fade* — or, with **until clip end**, stay to the
  clip's end. With the animation off everything is as before: every contour for the whole clip.
- Inspector, section "Holds", block "Animation": **Animate** (a check box; off = no animation); **Order** — *Bottom to
  top* (default: from the lowest hold up, by the centre of each contour — routes usually go up) or *Marking order*;
  **Start** (a source time in seconds) with **Set to playhead**; **Interval** 0.5 s; **Appear fade** 0.25 s (0 = at once);
  **Stay after last** 3 s with **until clip end**; **Disappear fade** 0.5 s. A line gives the animation's length; when it
  runs past the clip's end a warning (not an error) says so — the animation is cut there. Switching on starts at the
  source time under the playhead when it is on the clip, otherwise at the clip's first source frame.

### 2. Data, timing and format

- `HoldMarkup.Animation` (`HoldAnimation`, immutable; null = off): `Order` (`BottomToTop` / `MarkingOrder`),
  `StartSourceTime` (ticks — a time of the **source**, as `Hold.SourceTime`), `Interval`, `AppearFade`, `StayAfterLast`,
  `DisappearFade` (ticks of **timeline** — output — time), `StayUntilClipEnd`. Limits: Interval 0.05–10 s, the fades 0–5 s,
  Stay 0–60 s, the start 0–24 h.
- Why the source time: the animation stays on a moment of the video (the start of the climb) — a trim of the clip's head,
  a slip or a speed change never move it against the picture, and the second half of a split continues it without a
  restart. The durations are output time, so the clip's speed does not stretch them.
- On timeline frames, like the fades of D025 (`Core/Playback/HoldAnimationRule`, `HoldSchedule`): the start frame `n0` is
  the first timeline frame whose source time `t(n) = SourceIn + (FromFrame(n) − S) · speed` (D009 / D022) is at or after
  `StartSourceTime` — `T0 = S + (StartSourceTime − SourceIn) / speed` moved up to the grid, exact in integers; it may lie
  before or after the clip. Durations become frames by `TransitionRules.Frames` (`ToNearestFrame`). The hold of rank `r`
  appears at `A_r = n0 + Frames(r · Interval)` (the product rounded once, so a rounded interval never drifts); its opacity is
  0 before `A_r`, then `FadeRule.Ramp(n − A_r, Fa)`; the disappear starts at `D = A_last + Fa + Frames(StayAfterLast)` —
  "after the last" counts from the last hold fully shown — and multiplies by `(Fd − j)/(Fd + 1)` for `j = n − D < Fd`, 0
  after (the D025 ramps); with "until clip end" there is no `D`.
- Bottom to top: by the contour's area centroid (the vertex mean only for a degenerate one, so dense vertices on one side
  do not bias it), larger Y (lower in the frame) first; level centres (within 1e-9) left to right; then the marking order.
- `project.json` **v6**: the optional `holdMarkup.animation` (every member required when present; values outside the
  limits refuse the file as in D034). A v5 file opens without animation and is saved as v6; a v5 build refuses a v6 file
  by its existing newer-version check (had the member been added to v5, an older build would silently drop it on a save).

### 3. Edits

- `ITimelineEditService.SetHoldAnimation(clipId, animation?)` → one `SetHoldMarkupCommand` with the
  `HoldMarkupFields.Animation*` field of what changed. Consecutive changes of one setting merge into one undo step
  (D017), switching on or off is always its own step; unchanged values are no step. Refused outside the limits, for other
  clips, on a locked track (the service) and during an export (`EditingLock` in the view-model). The colour and the keyline
  are not resolved again.
- A presentation-only change (`DiffersOnlyInPresentation`): decoding is not restarted.
- Split / Duplicate / Paste copy the animation with the markup; Trim, Slip, Speed and Move leave it (§2).
- The contour correction of D035 is unchanged — the animation only reads `Points`; a correction, an added or a removed
  hold shows at once, and with Bottom to top a correction may change the order when the contour's centre moved.

### 4. Preview and export — one rule

- Each hold's opacity is computed in `PlaybackSnapshot.LayersAt(time)` — the one place both the Preview and the export take
  a frame from: `PictureLayer.OutlineOpacities`; `CompositionDrawPlan.Outline` keeps the shown contours with their
  opacities (`OutlineDraw.PolygonOpacities`; the hidden ones left out; all at 1 → exactly the outline without animation).
- `CompositionPainter` draws the contours of one opacity as a group with the combined opacity hold × layer (clip opacity
  × fade or dissolve, D025 / D018), through an opacity **mask** — a layer — not a plain opacity, which in Avalonia's Skia
  backend fades every draw call on its own: a fading hold's keyline then never shows through its core and its overlapping
  segments (the round caps at the vertices) never darken. Found by the rendering test; it also changes, for the better,
  an outline with a keyline on a fading or translucent clip (Phase 19 drew the keyline and the core faded separately).
- Paused and after a seek the Preview shows the animation's state on the frame under the playhead — what the export
  writes for that frame.
- Markup mode: the composition keeps the honest animated state; the overlay draws every confirmed contour thin and dashed
  (`HoldMarkupViewModel.AnimatedContours`, only while the mode is on and the route is animated), so a hold hidden on that
  frame can be selected and corrected. Like the D035 handles, an editing aid only — never in the export.

### 5. Not in this decision

Tracking or camera motion compensation (the contours stay where they are), groups of holds, per-hold intervals, reordering
by hand, per-hold animation, keyframes, markers of the animation on the clip in the timeline.

Context: the route animation idea of 2026-10-14 after the hold markup (D034) and the contour correction (D035).

Consequences: `project.json` v6; a new rule class in Core and an init property on `PictureLayer` / `OutlineDraw`; one edit
method and seven command fields; the Inspector's "Holds" section grows the "Animation" block; the outline painter
composites groups through a layer.

Status: Accepted (2026-10-15, product owner: the Phase 21 specification). Implemented in Phase 21 (branch
`feat/phase-21-hold-animation`); the main functionality accepted by the product owner on 2026-10-15 after their check in the
real app (`docs/PHASE21_MANUAL_TEST_PLAN.md`).

---

## D037 — Timeline selection, magnetic snapping and making room (Phase 22)

Date: 2026-10-15

Decision (product owner, 2026-10-15: the Phase 22 specification, revision 2, with the answers Q1–Q11 — Q3 variant B, Q8
new media only, Q9 refuse, Q10 every placement edit, Q11 refuse; the clarification that the exact editing commands keep
their meaning). Extends D008 (selection stays view state; "overlapping clips on a track are rejected" is refined by §3:
a placement edit now makes room on the right instead of being rejected, a collision on the left is still rejected), D027 §5
(copy / paste / duplicate), D030 (track lock), D032 AQ1 (a linked clip is selected with its partner) and AQ4 / AQ7 / L5
(a partner follows a shift by the same frames; a locked partner refuses the edit). `project.json` stays **v6**, no new
property.

### 1. Selection (view state, never an undo step — D008)

- **Ctrl+A** selects every clip of every unlocked track (Q1); hidden and muted tracks included. A selected clip brings its
  link partner (AQ1) — also when the partner is on a locked track, exactly as a click does (an edit is then refused by L5,
  as today). The primary (the Inspector's clip) is the earliest clip, a tie to the topmost track. The status says "N clips
  selected". Not in the markup mode (D034), never while typing (the existing text-input guard).
- **Esc** (no gesture, no marquee, no markup mode, not typing) clears the selection; during a drag it cancels the drag
  (as before), during a marquee it restores the selection from before the marquee.
- **Marquee:** a press on an empty place of a track lane (not a clip, not a dissolve zone, not the ruler) and a drag past
  the drag threshold draws a rectangle in the lanes, across tracks. Every clip the rectangle **intersects** (Q2) is
  selected, except the clips of locked tracks (Q1); partners come along (AQ1). Without Ctrl the marquee replaces the
  selection, with Ctrl it adds to the selection it started with. The selection follows the rectangle live; the primary is the
  earliest selected clip (a tie: the topmost track). A plain press and release without a drag clears the selection as
  before; with Ctrl it leaves it. In the markup mode a marquee is a change of the selection like a click on a clip or
  an empty place: it leaves the markup mode (D034, unchanged); only Ctrl+A is off there.
- **Inspector:** with more than one clip selected the clip's name reads "<name> (1 of N selected)" (Q7); N counts the
  selected clips, partners included. Properties are still edited for the primary only (Phase 7).
- Not in Phase 22: Shift+click range selection (Q6), editing several clips' properties at once.

### 2. Magnetic snapping

- On by default (`Timeline.SnappingEnabled`, as since Phase 4), the **Snap** toggle and **N** unchanged; its persistence
  unchanged (D015). Targets as before: 0, the playhead, the markers (D027 §6), the edges of every clip not being dragged;
  the tolerance 8 px.
- **A snap never makes an overlap.** A target whose snapped placement would overlap a clip (on any track the placement
  touches) while the unsnapped one would not is skipped; the next nearest target within the tolerance is taken, or none.
  An overlap the pointer itself asks for is resolved by §3, never created by the magnet.
- **Dragging clips** (one or a group): the group's outer edges (its earliest start and latest end) are the candidates, as
  before, with the rule above.
- **A drop from the Media Browser:** the new clip's start and end are candidates; while dragging over a lane the snap
  indicator shows where it will land. **Q8:** a drop onto a track with no clip, Snap on, places the clip at 0 — only for new
  media from the Media Browser; existing clips moved onto an empty track keep the threshold snap only. With Snap off the clip
  lands exactly under the pointer and gaps stay.
- **Not snapped** (they keep their exact meaning): Add to Timeline (the end of the first track, as before), Paste and
  + Text (at the playhead), Duplicate (right after the selection), Insert / Overwrite (D031), the trims to the playhead
  (D030), the keyboard nudge (§4).
- Not in Phase 22: a magnetic main track that closes gaps automatically (CapCut-like) — a candidate for the future.

### 3. Making room on the right (Q3 — variant B, Q9, Q10, Q11)

- Applies to every placement edit: a move by drag (one clip or a group, on its tracks or onto another track as before), the
  keyboard nudge (§4), a drop from the Media Browser (`AddClip` at a position), Paste, Duplicate and + Text (Q10). Not to
  Insert / Overwrite (their own rules, D031), trims (they clamp at the neighbour), Split, Slip, a speed change, Unlink Audio.
- On each track the placed clips (the moved or new ones, with the partners that move with them) are fixed. A clip **S** of
  that track that is not placed and that a placed clip **X** overlaps:
  - with `S.start ≥ X.start` (it starts at or after X): S moves right by exactly the overlap — to X's end;
  - with `S.start < X.start` (X starts inside S): a **collision on the left** — the whole edit is refused (Q11), as before.
  The track's later clips move only when the moved clip now overlaps them, each by its own overlap: a gap absorbs the push,
  the rest of the track stays. A clip pushed into a placed clip (one that would itself have to move) refuses the edit.
- **Linked clips (variant B):** a pushed clip's partner moves by the same frames on its own track by the move rule
  (AQ4 / AQ7) — it does not push that track's clips. If the partner would overlap a clip there, or its track is locked
  (L5), the whole edit is refused: a link is never put out of sync, and a push never spreads beyond the track it starts on.
  (Variant A, the cascade across linked tracks, is not taken; it can be added later on top of this rule.)
- **Dissolves:** pushed neighbours that touch move by the same frames, so a dissolve between them is kept. A push that would
  open the cut of a dissolve between a pushed clip and a clip that is not moved is **refused** (Q9) — a dissolve of clips
  the user did not touch is never removed. A moved clip's own dissolves follow D025 §5 as before.
- **Atomic, one undo step:** the whole result is planned in one `EditPlan` and validated as a whole (D008); a refusal
  changes nothing and leaves no undo step; a success is one undo step named as the edit ("Move Clips", "Paste Clips" …).
  A locked track (D030) or a running export (`EditingLock`) refuses as before.
- **The drag's preview** shows the clips the release would push at their planned places (as the ripple trim's preview,
  D030 §6); a refused placement is marked invalid (red) and the release changes nothing. The Preview panel changes only
  after the release (one snapshot update), as before.
- Ripple Delete, Close Gap (D027 §2) and Insert / Overwrite (D031) keep their rules; they share only the move's timing
  rule (`ShiftedState`), unchanged.

### 4. Moving

- **Alt+← / Alt+→** move the selection by one frame, **Alt+Shift+← / Alt+Shift+→** by one second (the step of Shift+← /
  Shift+→: the nominal frame count of a second, e.g. 30 at 29.97 fps); each press is its own undo step (Q4); not snapped; §3 applies (a push to the right; a collision on
  the left refused). Refused — no undo step — on a locked track, during an export, or before the start of the timeline. Not
  while typing.
- **Auto-scroll:** while dragging a clip, a clip's edge or a marquee with the pointer near the left or right edge of the
  timeline's view (within 32 px, or past it), the view scrolls, faster the farther in; the gesture follows the content
  under the pointer.
- Not in Phase 22: moving a selection from several tracks to other tracks (Q5) — still "only in time".

### 5. Not in this decision

The magnetic main track (CapCut-like gap closing); variant A (a cascade across linked tracks); Shift+click ranges; moving a
multi-track selection between tracks; editing the properties of several clips. The backlog stays outside Phase 22
(`ROADMAP.md` "Follow-ups and backlog"): FU-21.1 (the marker navigation status message), FU-21.2 (clip-bound markers), the
export of vertical video without unwanted black bars.

Context: the Phase 22 pre-flight of 2026-10-15 found multi-selection (Ctrl+click), group moves and drag snapping already in
place, and no Ctrl+A, no marquee, no keyboard nudge, no auto-scroll, no snapping of a drop, and every collision rejected.

Consequences: no format change; the edit service's placement edits (`MoveClips`, `AddClip` at a position, `PasteClips`,
`DuplicateClips`, `AddTextClip`) make room on the right; a move preview of every clip it changes; a snap that can skip a
target; a selection helper and the marquee in the timeline view-model and view; new shortcuts Ctrl+A, Esc, Alt+arrows.

Status: Accepted (2026-10-15, product owner: the Phase 22 specification, revision 2, and the answers Q1–Q11). Implemented in
Phase 22 (branch `feat/phase-22-timeline-ux`); accepted by the product owner on 2026-10-16 after the real-app run of
`docs/PHASE22_MANUAL_TEST_PLAN.md` (25 / 25 PASS).

---

## D038 — Direct Move, Scale and Crop in the Preview (Phase 23)

Date: 2026-10-16

Decision (product owner, 2026-10-16: the Phase 23 specification with the answers Q1–Q7 as recommended — Q1 anchored crop,
Q2 the Inspector's crop unchanged, Q3 a click selects the topmost layer, Q4 two buttons and no new shortcuts, Q5 text moves,
Q6 whole canvas / source pixels, Q7 gestures only while paused — and the addition of scaling by the frame's corner handles).
Extends D017 (property edits and undo merging: a gesture's edits merge into one step, §4) and D020 (the Inspector's Transform
and Crop stay as they are). D018 (the composition model) is unchanged: every gesture writes ordinary `VisualProperties`.
`project.json` stays **v6**, no new property.

### 1. Tools

- Two toggle buttons in the Preview's top bar, **Move** and **Crop**, at most one on (pressing one turns the other off); both
  off = the Preview as before (it takes no pointer input except the hold markup's). No keyboard shortcuts for them.
- Available only in the Preview's Timeline mode and not during an export (`EditingLock`): the Source mode or an export turns
  the tool off (an export also cancels a running gesture, §4). The hold markup mode (D034) and a tool exclude each other:
  turning a tool on leaves the markup mode, entering the markup mode turns the tool off.
- A tool stays on across seeks, undo / redo and selection changes. **Gestures only while paused** (Q7): while playing the
  frame and handles are hidden and a press does nothing; Play during a gesture cancels it.
- Esc: during a gesture cancels it (§4); otherwise turns the tool off. While a gesture runs every other key is swallowed
  (no Undo, Redo, Save, Play or timeline key reaches its command).

### 2. The target and its frame

- The target is the timeline's primary selected clip (D037 §1) when it is a video, image or text clip whose span covers the
  playhead; its geometry comes from the model (`CompositionMath.Layout` / `TextTransform`, the asset's display size as in
  `PlaybackSnapshotBuilder.DisplaySize`), so it is shown also when the layer is culled under an opaque video, hidden, or
  faded out. Not a target (the bar says why): no clip selected, an audio clip, the playhead outside the clip, a picture of
  unknown size. Several clips selected: only the primary changes.
- The frame: the visible picture — the crop rectangle through the layer transform (a rotated rectangle) — or a text clip's
  text box (the painter's layout: `MaxTextWidth × Height` centred on the local origin). Drawn in the overlay only (never in
  the composition, never in the export) in a light blue with a dark keyline, the centre marked. On a locked track the frame
  is grey and dashed, no handles; a press says "Track … is locked." and changes nothing.
- **Selecting by a click** (Q3): a press outside the target's frame (and outside its handles) selects the topmost visible
  layer of the Preview under the pointer (`PlaybackFrame.Layers`, top to bottom; a picture by its frame, a text by its box,
  placeholders included; layers of unknown size are skipped) — the same as a click on that clip in the timeline (its link
  partner comes along, the Inspector follows). Inside the target's frame the target wins even when another layer is on top.
  In Move the same press then drags the newly selected clip; a press on no layer changes nothing.

### 3. Gestures

Coordinates: the pointer (view pixels) → canvas through the Preview's viewport (`CompositionDrawPlan.Viewport`, frozen at
the gesture's start), → source pixels through the target's layer transform (`LayerGeometry.Transform` extended by
`SourceRect`'s origin, also frozen). A press becomes a gesture after 3 view pixels of movement; a click without movement
changes nothing (apart from a selection change, if any).

- **Move** (Move tool; a press inside the frame): `Position` moves by the pointer's canvas delta, **rounded to whole canvas
  pixels** (Q6; the delta is rounded, so a fractional starting position keeps its fraction and a return to the start is the
  start exactly). Shift keeps the axis with the larger delta. Rotation, scale and crop do not affect it (Position is in
  canvas pixels, D018). Position is clamped to the limit (±100 000 px). Video, image and text.
- **Scale** (Move tool; a press on one of the frame's four **corner handles**): the scale changes **proportionally around
  the clip's centre**; `Position` never changes. The new scale = the start scale × the projection of the grabbed corner's
  new place (the pointer minus the grab offset) on the centre→corner diagonal at the start, divided by that diagonal's
  length — so it follows the corner along the diagonal for any rotation, and a pointer moved across the diagonal does not
  change it. Rounded to 0.01 % (the Inspector's shown precision). Clamped to the limits [1 %, 1000 %]: at a limit the frame
  stops and the pointer runs on; a pointer past the centre gives the minimum. Video, image and text (text has a scale too).
  The corner handles exist only in Move — Scale never starts a crop.
- **Crop** (Crop tool; a press on one of the eight **handles** of the crop rectangle — four corners, four edge midpoints):
  video and image only (text has no crop; the bar says so). The full source frame is outlined dashed around the crop.
  The dragged edge(s) follow the pointer in source pixels, **rounded to whole source pixels** (of the display size); the
  edges not dragged keep their exact values. **Anchored** (Q1): the picture that stays visible does not move or resize on
  the screen — the gesture writes the crop together with a compensating `Scale = k / fit′` and `Position` (the new crop
  centre placed where the source showed it), where `k = fit · Scale` at the start and `fit′` is D018's fit of the new crop.
  The compensation values are stored unrounded. Constraints, applied to the dragged edge (it stops, the anchoring stays
  exact): the crop stays inside the source; at least `max(4 px, 1 % of the side)` stays on each axis; the compensating
  scale stays within [1 %, 1000 %] (it is `k · max(w / canvasW, h / canvasH)` — the edge stops where it would leave the
  range); a value the validator would still reject leaves the crop as it was. Rotation is honoured (the handles lie on the
  rotated rectangle; the pointer is mapped back through the transform).
- The **Inspector's** crop fields keep D018's re-fit (Q2): a crop typed there re-centres and re-fits the picture as before.
  After an anchored crop the Inspector shows the new Crop, Scale and Position values.

### 4. Model updates, undo, saving

- The model is changed **live** during a gesture through `ITimelineEditService.SetClipProperties` (the Preview, the
  Inspector and an export all read the same values — Preview = export by construction, D023), only when the computed value
  differs from the model.
- **One undo step per gesture:** `ClipPropertyChange.Gesture` (a `ClipPropertyGesture` object per gesture, with the step's
  description "Move Clip" / "Scale Clip" / "Crop Clip") is carried into `SetClipPropertiesCommand`; edits of the same gesture
  merge with each other whatever properties they change (the fields of the step are the union), and never with an edit of
  another gesture or without one. Edits without a gesture (the Inspector) merge exactly as in D017.
- **Esc cancels** a gesture by writing the values from its start: the merged step returns to its own "before" and is
  removed (D017's rule), so no step and no redo entry stay. The same happens when the pointer capture is lost, playback
  starts, the selection or the target changes, the tool turns off, the Preview switches to Source, an export starts or the
  project is saved / replaced.
- **Saving during a gesture** (Save, Save As, the unsaved-changes prompt of New / Open / Close): the gesture is **cancelled
  first**, then the project is saved. This keeps the save point on a completed step (a save inside a gesture would make the
  rest of the gesture a second step, D017 (b)) and the saved file free of a half-made change. Autosave (recovery files)
  does not set the save point and is unaffected.
- A locked track refuses every gesture (the edit service and the overlay); during an export the tool is off.

### 5. Not in this decision

Rotation by the mouse; guides and snapping in the Preview; transforming several clips at once; scaling the crop rectangle
as a whole or moving the crop window; arrow-key nudges in the Preview; shortcuts for the tools; transforming during
playback; changing the Inspector's crop semantics; keyframes; the dimmed picture of the cropped-away part (only its outline
is drawn). The backlog stays outside Phase 23 (`ROADMAP.md`): FU-21.1, FU-21.2, FU-22.1, the export of vertical video without
unwanted black bars.

Context: the Phase 23 audit of 2026-10-16 found the composition model and its exact view ↔ canvas ↔ source mapping in
place (D018, D020; used by the hold markup overlay, D034 / D035), no pointer input on the Preview except the hold markup,
D018's crop re-fit (a literal crop drag would move the opposite edge and, for a portrait picture on a landscape canvas,
change its size), and no way to keep two gestures of the same properties apart in the undo history (D017's merge rule).

Consequences: no format change and no new dependency; a pure gesture-math class in Core; a gesture object on
`ClipPropertyChange` and the merge rule of `SetClipPropertiesCommand`; a tool view-model and an overlay over the Preview; the
two buttons in the Preview bar; `ShortcutRouter` gives a running gesture Esc and swallows the rest; the save workflow cancels
a gesture before saving.

Implementation notes (2026-10-19, Phase 23): only the handles are grab targets (not the frame's edge lines); the overlay
reaches over the Preview's 16 px margin so the handles of a clip that fills the canvas are whole, and it clips to the
Preview so a frame past the canvas never covers the panels around it (both found in the real-app run); the model is written
on every pointer move whose rounded result differs (no throttling was needed in the real-app run).

Status: Accepted (2026-10-16, product owner: the Phase 23 specification and the answers Q1–Q7, with Scale by the corner
handles added). Implemented in Phase 23 (branch `feat/phase-23-preview-transform`); verified by Claude on 2026-10-19 (the
gates and the real-app run of `docs/PHASE23_MANUAL_TEST_PLAN.md`, 23 / 24 PASS, row 23 NOT RUN); accepted by the product owner
on 2026-10-19 with the status PASS WITH NOTES.

---

## D039 — Russian localization and interface polish (Phase 24)

Date: 2026-10-19

Decision (product owner, 2026-10-19: the Phase 24 specification with the recommended answers — Q1 variant A, Q2 every
user-visible text including the service layer, Q3 the glossary below, Q4 new text clips read «Текст» — and two additions:
a limited icon pass over the frequent commands, and an inventory of numeric fields with sliders proposed where they help).
No format change: `project.json` stays **v6**; editing and export behaviour unchanged.

### 1. Language model (Q1, variant A)

- English is the neutral language of the resources; Russian is a satellite (`*.ru.resx`). The language is chosen at
  start from `appsettings.json` `App:Language` (default `"ru"`; `"en"` or an empty value = the neutral English; an unknown
  value = English and a warning in the log). No language switch in the interface.
- The language never comes from the operating system: the UI culture of the resources is set explicitly at start
  (`UiLanguage.Apply`); until then (tests, tools) it is the neutral English, whatever the OS language. Number and date
  formatting (`CurrentCulture`) is unchanged — it follows the OS as before (`NumericInput`).
- Logs (Serilog), internal exceptions (`InvalidOperationException` and the like for programming errors), undo descriptions
  (`IUndoableCommand.Description`, never shown) and the repository's documentation stay English.

### 2. What is translated (Q2)

Every text the user sees: labels, buttons, tooltips (with their shortcuts), section headings, empty states, the status bar,
dialogs (title, message, buttons), the export and project-settings windows, file-picker titles and filters, the window
title (apart from the product name), drop-down items (alignment, colour mode, animation order — no enum names shown), the
media kind, units, and the messages of the service layer that reach the user: edit refusals (`TimelineEditResult`), relink
results, export preflight issues and failures, project open / save / autosave / recovery errors, clip-property validation.
Technical detail from FFmpeg / FFprobe / the OS stays as it is, in parentheses after the translated sentence, where it
helps diagnosis.

Not translated: the product name «AI Video Editor»; codec and format names (H.264, AAC, MP4, HDR, SDR); key names (Ctrl,
Shift, Alt, Esc, Enter); timecode; the user's data — track names (V1, A1, …), clip texts, file and project names. New
text clips read «Текст» (Q4); existing texts are never changed.

### 3. Strings

- Two resource pairs, generated into strongly typed classes by MSBuild itself (`EmbeddedResource` metadata
  `StronglyTyped*`; no package): `src/Core/Localization/Messages.resx` (+ `.ru`) for the service layer (Core, Timeline,
  Project, Export, Video, Vision reference Core) and `src/UI/Localization/Strings.resx` (+ `.ru`) for the interface. AXAML
  uses `{x:Static}`; code uses the properties and `string.Format` with numbered placeholders — never sentences glued from
  pieces.
- Keys: `Area_Name` (e.g. `Toolbar_Save`, `Toolbar_SaveTip`, `Edit_TrackLocked`). Placeholders are the same in both
  languages.
- Plural forms: a value with forms separated by `|` — English two (`one|other`), Russian three (`one|few|many`:
  1 клип / 2 клипа / 5 клипов) — chosen by `Plural.Format(count, forms)` from the number of forms, never from the culture.

### 4. Glossary (Q3)

| English | Русский |
|---|---|
| Timeline | Таймлайн |
| Track | Дорожка |
| Clip | Клип |
| Media / Media Browser | Медиа |
| Source | Источник |
| Inspector | Свойства |
| Dissolve | Наплыв |
| Crossfade | Перекрёстное затухание |
| Fade In / Fade Out | Появление / Исчезание |
| Ripple Delete | Удалить со сдвигом |
| Close Gap | Закрыть промежуток |
| Snap / Snapping | Привязка |
| Split | Разрезать |
| Unlink Audio / Unlink / Link | Отделить звук / Разъединить / Связать |
| Marker | Маркер |
| Holds / Mark holds | Зацепки / Разметить зацепки |
| Keyline | Обводка |
| Move / Crop / Scale | Переместить / Кадрировать / Масштаб |
| Export | Экспорт |
| Project Settings | Параметры проекта |
| Import Media | Импорт медиа |
| Relink | Перепривязать |
| Offline | Офлайн (файл не найден) |
| Undo / Redo | Отменить / Повторить |

Units: Б / КБ / МБ / ГБ, «с» (seconds), «кадр.» (frames), «пикс.» (pixels), «к/с» (frames per second), «%», «°», «×».

### 5. Guards

- An AXAML test: no literal with a Latin letter in `Text`, `Content`, `ToolTip.Tip`, `Title`, `Watermark`, `Header`,
  `PlaceholderText` or a binding's `StringFormat` in `src/UI` / `src/App` — only `{x:Static}` / bindings; a short
  allowlist of symbols and technical tokens.
- A resource test: both pairs have the same keys, no empty value, the same placeholders, valid plural forms; a Russian
  value equal to the English one only for an allowlisted technical token.
- A source test: string literals of two or more Latin words in the files that build user text are refused unless they are
  logging, an internal exception or allowlisted (the allowlist names each file and literal).
- Tests in Russian: a few flows run with the Russian UI culture and check the real texts.

### 6. Layout and polish

Checked in the real app at 1024 × 640 and 1440 × 900: no clipped label or button text. The Inspector's label column is one
style (wider, wrapping); the status bar trims with an ellipsis and shows the whole message as a tooltip; Play / Pause keeps
one width; the timeline's tool bar takes at most two rows at 1024 px; the main tool bar fits at 1024 px. Shortcuts work in
the Russian keyboard layout (they are matched by key, not by the typed character).

### 7. Icons (addition)

A small vector icon set drawn for the project (path geometries in one resource dictionary, one stroke style, no external
file, dependency or licence question) for the frequent commands: transport (previous / next frame, Play / Pause, Stop —
icon only, as their glyphs are today; Loop — icon and label), Undo / Redo, Split, Delete, Snap, Move / Crop, a track's hide
and lock (replacing the emoji). An icon goes with a short localized label except where the button is already a bare glyph;
the full command name and its shortcut stay in the tooltip. The other buttons stay text only. No other redesign.

### 8. Numeric fields and sliders (addition)

Inventory and proposal:

| Field | Range / unit | Proposal |
|---|---|---|
| Opacity | 0–100 % | **numeric + slider (Phase 24)** — bounded, judged by eye |
| Volume | 0–200 % | **numeric + slider (Phase 24)** — bounded, judged by ear |
| Fade In / Fade Out | 0…clip length, frames | numeric (exact frames; the maximum depends on the clip) — slider: backlog |
| Speed | 0.25–4 ×, step 0.05 | numeric (exact values matter) — slider with detents: backlog |
| Scale / Rotation / Position | 1–1000 % / ±360° / ±100 000 px | numeric (Preview gestures cover dragging, D038) — drag on the label: backlog |
| Crop L / T / R / B | 0–100 % | numeric (the Preview's Crop tool covers dragging) |
| Dissolve duration | frames | numeric |
| Text size | px | numeric |
| Hold line width | px at 1080 | numeric — slider: backlog |
| Hold animation (start, interval, fades, stay) | seconds | numeric (exact timing) |
| Project width / height | px | numeric |

A slider and its field are bound to the same property; a drag is one undo step by D017's merge of consecutive changes of
one field; the field keeps exact typing.

### 9. Not in this decision

A language switch in the interface; a general redesign, themes, new panels; translated documentation; sliders other than
Opacity and Volume (backlog FU-24.1); the backlog FU-21.1, FU-21.2, FU-22.1 and the export of vertical video without
unwanted black bars.

Context: the Phase 24 audit of 2026-10-19 found no localization infrastructure (no resources, no culture handling, no
Cyrillic in `src/`), about 700 English user-visible strings in AXAML, view-models and the service layer, about 675 test
assertions on English text, an OS UI culture of ru-RU on the development machine and en-US on CI, fixed 80 px Inspector
labels, a main tool bar without wrapping and a status bar without trimming.

Consequences: no new dependency; two resource pairs and their generated classes; a culture switch at start; every
user-visible literal replaced by a resource; English tests keep their assertions; three guard tests.

Implementation notes (2026-10-19, Phase 24):
- Resources: 557 interface keys (`Strings`), 317 service-layer keys (`Messages`); the guards found ~850 English literals at
  the start; 228 remain in `tests/UI.Tests/Localization/untranslated-allowlist.txt`, in six groups with their reasons (undo
  descriptions, damaged-file reasons, FFmpeg / decoder / model runtime detail, log templates, data and names, design-time
  stand-ins). The reasons of a damaged `project.json` stay English in parentheses after the translated sentence, as the
  FFmpeg detail (§2). Sentences that named «dissolve / crossfade» or a verb have one resource per noun and the verb as an
  infinitive resource («Нельзя {0}: …»), never a noun glued into a sentence.
- Layout (§6): the main tool bar is a wrap panel (one row at 1024 and 1440 px); the Inspector's label column is 116 px and
  wraps; the fade / dissolve time is shown below its field (it was cut at the panel's edge, also in English); the Media
  panel's offline strip puts its heading above the wrapping buttons; the status bar trims and the bar carries the tooltip.
  While the hold markup mode is on, Переместить / Кадрировать show only their icons, so the mode's buttons fit at 1024 px
  (the picture keeps its size, D034); the «preparing» progress bar has no 200 px minimum width any more.
- Icons (§7): Undo / Redo, the transport and Loop are icon-only buttons (Loop: with a label the timecode no longer fitted
  the Preview at 1024 px); Split, Delete, Snap, Move and Crop have an icon and a label; the eye and the lock replace the
  emoji. The geometries are path data parsed when an icon is shown (a geometry needs the platform's renderer).
- Sliders (§8): Opacity and Volume, under their fields; a drag is one undo step by D017's merge (checked in the app).
- The configuration (`appsettings.json`) is read from the process's current directory (`Host.CreateApplicationBuilder`,
  unchanged by Phase 24): started from another folder the app runs with the defaults, `ru` included. Recorded, not changed.

Status: Accepted (2026-10-19, product owner). Implemented in Phase 24 (branch `feat/phase-24-localization`); verified by Claude
on 2026-10-19 (the gates, the `project.json` v6 byte comparison with `c652667`, the real-app run of
`docs/PHASE24_MANUAL_TEST_PLAN.md`); accepted by the product owner on 2026-10-19.


---

## D040 — Technical backlog and small UX improvements (Phase 25)

Date: 2026-10-19

Decision (product owner, 2026-10-19: the post-Phase 24 audit's proposal "Phase 25 — technical backlog and small UX
improvements" with the answers to its questions). No format change: `project.json` stays **v6**; the composition, the mix,
the decoders and the export unchanged.

### 1. Configuration (FU-24.2)

`appsettings.json` is read from the application's folder (`AppContext.BaseDirectory` as the host's content root), never
from the process's current directory; the command-line arguments go to the host builder. Recorded at Phase 24 (D039
implementation notes) as an observation: started from another folder, the app ran with the defaults and without the
`Ffmpeg:*` paths.

### 2. Marker navigation (FU-21.1)

Previous / Next marker: a successful jump reports a localized status with the marker's timecode (it replaces any earlier
message); a refusal ("no marker before / after the playhead") keeps its message. Which marker is the previous / next one is
decided on the playhead's frame, as before (D027 §6). The marker model is unchanged (FU-21.2 stays deferred).

Refined in Step 25.2 — the cause: a jump to a marker past the sequence's end seeks there; playback clamps the seek to the
end and the Preview reported that clamped frame back, so the playhead was pulled back to the end — but only when the end
frame had not been reported before (a second press found it reported and stayed). From the end the next Previous could
find no marker before it («Перед указателем нет маркеров»), and a successful jump never replaced that message. Fixed: the
user's seek past the end does not report the clamped frame back (`PreviewViewModel.Seek`), so the playhead stays where
the user put it at the first seek as at the next ones — for a marker, a click on the ruler past the end or a step; the
resync after an edit (PR #16) still clamps the playhead to a new end before it. A jump reports «Переход к маркеру
{timecode}» (`Marker_Reached`). Tests: `UI.Tests/MarkerNavigationTests`.

### 3. The hold markup mode left on its own (FU-22.1)

Reproduce first. If the mode is left by the playhead's own adjustment while the markup frame is prepared, D034 is refined:
that internal adjustment is not a user seek and does not leave the mode; every user seek still leaves it. If the two cannot
be told apart reliably, or another change of agreed behaviour is needed, the product owner decides before the code.

Refined in Step 25.3 — not reproduced, no code changed, D034 unchanged. Only two things move the timeline playhead: a user
action (`TimelineViewModel.SetPlayhead` — the ruler, the arrows, the markers, Go to start / end) and playback's report of
its position (`ShowPlaybackPosition`); the markup mode's preparation (the model check and load, the frame decode, the
encoder) reads the playhead and never seeks, and a paused playback clock does not move. Replayed with the real shell and
playback, the Preview's timer running while the model loads, the playhead put by the ruler on 00:00:10:00 (a clip's first
frame) and inside a clip: the mode stays on and the playhead does not move (`HoldMarkupUiTests`). The Step 22.6 run drove
the app with synthetic keys, and in that run arrows sent without `KEYEVENTF_EXTENDEDKEY` lost their modifier (progress.md,
Phase 22): a bare Left arrow is a one-frame step back and a user seek, which leaves the mode by D034; 00:00:10:00 →
00:00:09:23 at 25 fps is two such frames (one step gives 00:00:09:24, checked in the real app). The most likely cause is
therefore stray keys of the harness, not the product — not proven, as the 22.6 key log is gone. Real app (Step 25.6, row 5
of `docs/PHASE25_MANUAL_TEST_PLAN.md`): the ruler click on 00:00:10:00, the clip selected, «Разметить зацепки» — the mode
on for 12 s with the model loaded, the playhead unmoved; a Left arrow then left it (00:00:09:24).

### 4. Sliders (FU-24.1, in part)

Fade In / Fade Out of video and audio clips and the hold line width get a slider under their numeric field, as Opacity and
Volume (D039 §8): bound to the same property, the field keeps exact typing, one finished drag is one undo step (D017's
merge of consecutive changes of one field); the Fade slider's maximum is the field's maximum and follows the clip's length.
No Speed slider: exact values matter and a speed change moves the clip's length and placement.

### 5. Icons on the timeline's tool bar

The D039 §7 icon set (one stroke style, path geometries, no dependency) extended to + Text, + Video / Audio track, Paste,
Duplicate, Ripple Delete, Close Gap, Dissolve, Link and Fit; each keeps its localized label and its tooltip (name and
shortcut) — no button becomes nameless for compactness. Checked at 1024 × 640 and 1440 × 900.

### 6. Verification

The Phase 24 manual gaps (sound, model errors, a damaged project, a failed export, the track and marker tooltips) are run in
the real app or recorded as NOT RUN. `project.json` v6 is compared byte for byte with a save by `d3df0ba` (`ru` and `en`).

### 7. Not in this decision

FU-21.2 (markers bound to a clip — the marker model unchanged); the export of vertical video without black bars (a separate
future phase); a Speed slider, dragging on labels; a general redesign; new dependencies; unrelated backlog.

Context: the post-Phase 24 audit of 2026-10-19 — the next phase was not defined in the documentation; the backlog held
FU-21.1, FU-21.2, FU-22.1, FU-24.1, the vertical-video export and the `appsettings.json` observation; several Phase 24 manual
rows were "auto only"; the documentation lagged the merges of Phases 19, 20, 22 and 24.

Consequences: no new dependency, no format change; a few new resources (en + ru); the guards unchanged.

Implementation notes (2026-10-19, Phase 25):
- §1: `App/Composition/AppHostBuilder` (the content root `AppContext.BaseDirectory`, the `args`); checked in the real app with
  another current directory and ffmpeg off PATH.
- §2: see "Refined in Step 25.2" — the fix also keeps a click on the ruler past the end where it was put at the first click
  (before, the first such seek in a non-empty project was pulled back to the end, the next ones not).
- §3: see "Refined in Step 25.3" — not reproduced; no code changed.
- §4: Fade In / Out and the hold line width, as Opacity / Volume; the Volume slider now has its own test and was checked in
  the app.
- §5: eleven icons; Unlink was taken with Link (the two are one pair of buttons, the label stays bound); − / + stay glyphs.
  The «Insert» of the specification is the bar's «Вставить» (Paste).
- Found outside the scope (Step 25.6): the Inspector's numeric fields cut a three-digit value (FU-25.1) — fixed afterwards at
  the product owner's request (2026-10-19): the Inspector's numeric-field style narrows Fluent's spinner buttons to 22 px and
  the text padding (selectors with an activator, as Fluent's own theme styles have one); width 104, height and behaviour
  unchanged. Found at that check: FU-25.2 (a field with a slider lost a typed fraction, since Phase 24) — decided by the
  product owner on 2026-10-19 (variant 1): Opacity, Volume and the hold line width take whole values — the view models round a
  typed value, a half up; the slider's converter no longer rounds (it rounded every write-back, also the one after the field
  moved the slider, and hid a saved fraction); a fraction saved before is shown as it is («0.####») until it is changed; the
  other numeric fields keep their precision. This refines D039 §8 for these three fields ("exact typing" → whole values).

Status: Accepted (2026-10-19, product owner). Implemented in Phase 25 (branch `feat/phase-25-backlog-ux`); verified by Claude
on 2026-10-19 (the gates, the `project.json` v6 byte comparison with `d3df0ba`, the real-app run of
`docs/PHASE25_MANUAL_TEST_PLAN.md`); accepted by the product owner on 2026-10-19 — PASS WITH NOTES (with FU-25.1 and FU-25.2);
**merged**: PR #26 into `main` as `58c48cb` (2026-10-19), its tree identical to `1a1ea76`.

---

## D041 — Vertical video and framing (Phase 26)

Date: 2026-10-20

Decision (product owner, 2026-10-20: the post-Phase 25 audit's variant A, "Vertical video and framing", with the answers
Q1–Q4). No format change: `project.json` stays **v6**; D018's composition (crop → fit "contain" → scale → rotation →
position), the mix, the decoders and the export unchanged. Goal: a phone video — portrait, as most climbing clips are —
gives an export without unwanted black bars in a few clicks.

### 1. The canvas offered by the first video (Q1 — a dialog)

When the first video clip is added to the timeline of an empty project with the default canvas, the editor offers to change
the canvas to the size and orientation of the source. Never silently.
- **When.** The add (from the Media Browser — a drop or the add command — or Insert / Overwrite from the Source viewer)
  succeeded and created a video clip with its picture (a sound-only Insert / Overwrite creates none); the timeline had no
  clip on any track before it; the canvas is the default 1920 × 1080; no offer was made in this project's session yet and
  the canvas was not changed in it; the asset's orientation is known — `MediaMetadata.DisplayRotation` is 0, 90, 180 or 270
  (null — an odd angle, a mirrored matrix, unknown — means no offer) — and so is its display size (`DisplayWidth ×
  DisplayHeight`, the frames as the decoder delivers them, after the rotation); the offered canvas differs from the current
  one. Never during an export (no add is possible then).
- **The offered canvas** (`ProjectSettingsRules.SuggestedCanvas`): the shape of the source at the default canvas's class —
  the short side 1080 (as the default 1920 × 1080), the long side `1080 × long / short` rounded to the nearest even number
  (a half up); a square source gives 1080 × 1080. A long side above `MaxCanvasSide` (4096) is set to 4096 and the short side
  derived the same way. The result passes `ProjectSettingsRules.CanvasError` (even sides for libx264 4:2:0, the side and area
  limits) — otherwise no offer. Examples: 2160 × 3840 and 1080 × 1920 → 1080 × 1920; 1080 × 2400 → 1080 × 2400; 3840 × 2160
  → 1920 × 1080, the current canvas → no offer; 1440 × 1080 (4 : 3) → 1440 × 1080. The source's own resolution never raises
  the canvas above the default class (no 4K canvas offered); Project Settings stay the way to choose another size. Rounding
  to even may leave the canvas's aspect a fraction of a pixel off the source's (a picture of an odd side).
- **The dialog** names the file, its size and the current and offered canvas; «Сменить холст» (default) / «Оставить».
  Accept → `ITimelineEditService.SetCanvasSize`, the existing D028 change: **one Undo step of its own**, after the add's step
  (Undo first restores the canvas, a second Undo removes the clip); the clip's position follows the canvas by D028's rule
  (0 stays 0). Decline or close → nothing changes. Either way the offer is not made again in this project's session (New /
  Open / Recover start a new one); a project opened with clips never gets it (its timeline is not empty).
- **Limitation**: "the user already changed the canvas" is known only within the session (a canvas other than 1920 × 1080,
  or any canvas change in the session); a project saved earlier with an explicitly chosen 1920 × 1080 and an empty timeline
  counts as default — nothing in `project.json` v6 records the difference.

### 2. Fill frame / Fit in frame (Q2 — one-off commands)

Two commands for a video or image clip: **«Заполнить кадр»** (Fill — the picture covers the canvas, the excess cut off by the
canvas edges) and **«Вписать в кадр»** (Fit — the whole picture visible, bars allowed). Each computes a `Scale` and writes
it into the clip — no mode, nothing new stored: a later canvas change does not recompute it (D028 leaves `Scale` as it is).
- **The rule** (Core, `CompositionMath.FillScale` / `FitScale`): on the clip's cropped source rectangle (`w × h`, display
  pixels of the asset), at its current `RotationDegrees` θ, with D018's fit `f`, for the picture **centred** on the canvas
  `W × H`: Fit `= min(W / (f·(w·|cos θ| + h·|sin θ|)), H / (f·(w·|sin θ| + h·|cos θ|)))`; Fill
  `= max((W·|cos θ| + H·|sin θ|) / (f·w), (W·|sin θ| + H·|cos θ|) / (f·h))`. Without rotation Fit is exactly 1 (D018's
  "contain") and Fill `max(W/w, H/h) / f`. For multiples of 90° the value is computed exactly (rational) and rounded outward
  to a double — Fill up, Fit down — so the picture provably covers / stays inside the canvas by `CoversCanvas`'s exact test;
  other angles in double with the margin `CoversCanvas` uses (10⁻⁶ of the picture size).
- **Unchanged**: `PositionX / Y`, rotation, crop, opacity and every other property. With a zero position Fill leaves no bar
  and Fit shows the whole picture; with an offset the picture keeps its offset (it is not moved back to the centre), so Fill
  may leave a strip at the far edge and Fit may push a part past it — the documented behaviour, no surprise shift.
- **One Undo step** per command (the edit as a gesture of its own — `ClipPropertyGesture`, D038 §4 — never merged with a
  typed Scale before or after it); the value already there → no step, a status says so. Refused, nothing changed, the status
  saying why: a text or audio clip, a clip whose source size is unknown, a locked track, a value outside `ClipPropertyLimits`
  Scale [1 %, 1000 %] (no clamping), during an export (`EditingLock`).
- **Where**: the Inspector (two buttons under Масштаб, for the selected video / image clip) and a new context menu of a clip
  on the timeline (right click: the two commands for the clicked clip; no other items in this phase).
- **Parity**: the Preview and the export draw the stored values through the one D018 path, so they agree by construction.

### 3. No warning before the export (Q3)

The export preflight is unchanged; an orientation mismatch is not reported.

### 4. Not in this decision (Q4 and the agreed exclusions)

A blurred background instead of bars; an automatic reframe or tracking; a persistent Fill / Fit mode; `project.json` v7;
FU-21.2; AI features; the technical-debt refactoring; new dependencies; unrelated improvements. A need to change the format,
the architecture or agreed behaviour beyond §1–§3 is reported with variants before the change.

Context: the post-Phase 25 audit of 2026-10-20 — the vertical-video export recorded as a separate future phase (D040 §7);
portrait canvas presets exist since Phase 13 (D028) and the display orientation is read since Phase 7, but the canvas is
never taken from a video (D018) and no command fitted a picture to the canvas other than the default "contain".

Consequences: no format change, no new dependency; a few new resources (en + ru); one Core rule set and one service edit;
the timeline's first context menu.

Implementation notes (2026-10-20, Phase 26):
- §1: `ProjectSettingsRules.SuggestedCanvas` returns the size as a pair (`DefaultCanvasWidth / Height` are now the
  constants `ProjectSettings` uses); the offer lives in `TimelineViewModel.Framing` (`AddMediaAt` and the Source viewer's
  Insert / Overwrite call it with "the timeline was empty"); a closed dialog counts as «Оставить». The sizes in the dialog
  and the status are joined by non-breaking spaces (found at the real-app run: «1080 / × 1920» broke across lines).
- §2: `ITimelineEditService.FrameClip(clipId, ClipFraming)`; the timeline had no context menu — `TimelineView` builds
  it on a right click (which also selects the clicked clip, as a plain click); `CompositionMath.FillScale` keeps twice
  `Covers`' margin for odd angles (a scale ≈ 4·10⁻⁶ larger than the exact cover).

Status: Accepted (2026-10-20, product owner). Phase 26, branch `feat/phase-26-vertical-video` from `58c48cb`; implemented and
verified by Claude on 2026-10-20 (the gates, the `project.json` v6 byte comparison with `58c48cb`, the real-app run of
`docs/PHASE26_MANUAL_TEST_PLAN.md`); **accepted by the product owner on 2026-10-20**; push and a pull request permitted.

## D042 — Product concept, scope boundaries and the order of the remaining phases

Date: 2026-10-20

Decision (product owner, 2026-10-20). A planning decision: it fixes what the product is, what it deliberately is not, and
the provisional order of the phases after Phase 26. It changes no code, no format and no earlier decision; each planned
phase still gets its own decision (scope, steps, acceptance criteria) when it is taken up. The plan itself:
`docs/DEVELOPMENT_PLAN.md`, "Planned phases after Phase 26 (D042)"; the status summary: `ROADMAP.md`.

### 1. The product

A specialised, simplified video editor for finished bouldering videos — not a general replacement of CapCut, Premiere or
DaVinci Resolve. The main user path (the target of the functional core; not a statement that all of it exists):
1. Import videos from the climbing gym.
2. Cut away the excess, split and arrange the clips on the timeline.
3. Recognise holds, choose the wanted ones and overlay their outlines.
4. Later: holds proposed automatically, and their outlines following the holds while the camera moves.
5. Add text with an animated appearance and disappearance.
6. Add transitions between clips.
7. Add music, set its volume, apply a small set of ready-made effects.
8. Set the framing, check the Preview and export the video.

State on 2026-10-20: 1, 2, 3 (D034–D036), the music track with volume and fades (D017, D025, D032), framing (D038, D041 —
Phase 26), the Preview and the export (D018, D023) exist; text exists without its own animation beyond the clip fades
(D021, D025); transitions are the cross dissolve and the fades only (D025); no ready-made effect exists (`Clip.Effects` is
persisted but never rendered, D025); 4 is excluded from the MVP by D034 and D036 and is a future extension.

### 2. Principles

- The AI hold markup and, later, the outlines following the holds are the main specialisation and the product's key
  difference (since 2026-10-14, D034).
- Basic editing is complete and reliable, but not overloaded.
- Text, simple animations, transitions, music and a few ready-made effects belong to the intended core.
- The reference size of the future libraries: about **five transitions** and about **five ready-made effects**; the exact
  sets are decided in their phases.
- The width of the functionality is reduced, never its quality: reliability, a predictable Undo / Redo (one step per user
  action), the Preview matching the export (D018, D023), `MediaTime` precision (D007) — the standing rules of
  `docs/DEVELOPMENT_PLAN.md` — hold for every planned phase.

### 3. Boundaries

- No goal of parity with CapCut, Premiere or DaVinci Resolve; a function is not added because a competitor has it.
- No unlimited library of effects or transitions.
- Not planned unless a separate justification appears: advanced multi-track editing beyond the current tracks, a complex
  audio studio (reverb, a mixer, volume automation, ducking), the synchronisation of many sources (multicam), full motion
  design (keyframes, speed ramps, motion paths).
- The specialisation on bouldering videos, the AI hold markup and the outline tracking is kept.
- A new **large** function enters the plan only after a separate justification and the product owner's approval (a new
  decision). Obvious UX defects found during development are fixed when found (as FU-25.1 / FU-25.2) and do not wait for the
  Final UX Pass.

### 4. The candidates recorded earlier

The candidates named in the Phase 16–18 pre-flights and D037 §5 (`ROADMAP.md`, "Future phases") are subordinate to §3:
keyframes, speed ramps, freeze frame / reverse and ducking are **not planned** (§3); the magnetic main track (D037 §5), text
styling and timeline polish stay backlog candidates that a planned phase or the Final UX Pass may take with a justification;
dragging objects in the Preview is done (Phase 23, D038). Nothing earlier is superseded: D034's and D036's exclusions of
automatic proposals and camera motion compensation stay valid for those decisions — the planned AI phases (§5) extend
them only through new decisions after their research.

### 5. The remaining phases (provisional order)

Phases get their numbers when they are taken up (the next one is Phase 27); the letters below are working labels.
Research stages (R) change no product code (experiments outside the repository, as E1 / E2 of D034) and end in a go / no-go
and a specification for the product owner.
- Phase 26 — vertical video and framing (D041, current): its closeout 26.4.
- A — text appearance / disappearance animation.
- B — the transition library (≈ 5, the cross dissolve included).
- C — ready-made effects (≈ 5) and the music scenario.
- G — stabilisation of the main path (a release candidate: the eight steps of §1 end to end).
- R1 → D — research, then (on a go) automatic hold proposals.
- R2 → E — research, then (on a go) outlines that follow the holds while the camera moves.
- R3 → F — research, then «COROS Pace 4: пульс и телеметрия в видео» (§6).
- H — «Final UX Pass: аудит и упрощение интерфейса» (§7), the last large phase.
Dependencies: B and C after A (they all change the composition; one at a time); F after A (its overlay uses the animated
overlay path A establishes) and after R3; D after R1, E after R2 (and after D when D is taken); H after the planned core
including F, with a regression run of the main path. R1, R2 and R3 may run in parallel with A–C. The order of the AI
phases against B / C may be changed by the product owner once the research results are known.

### 6. COROS Pace 4: heart rate and telemetry in the video (preliminary scope)

The original idea, restored: heart-rate data from a COROS Pace 4 watch, synchronised with the bouldering video and shown in
the export. Preliminary — not specified and **not technically solved**.
- **Only COROS Pace 4** in the first version; no general support of Garmin, Apple Watch or other devices. An extension is
  possible later if the first version proves useful, through its own decision.
- **The source** is a training file exported from the watch's ecosystem; no direct connection to the watch or a cloud
  service is assumed.
- **The format**: FIT as the preliminary direction, TCX as the fallback. Recorded here for the first time (the earlier
  discussion was not in the repository); to be confirmed on real COROS Pace 4 exports in R3, including how a parser is
  obtained (an own reader or an SDK — a dependency to be justified under the project's rules).
- **Synchronisation**: the heart rate placed on the video's timeline preferably from the video's internal timestamps and the
  training file's timestamps, not only from a manual offset; a correction (an offset the user sets, and whatever R3 shows to
  be needed, e.g. for drift) when the automatic match is not enough. Whether the phone footage's timestamps exist, their
  precision, time zone and drift, and the heart-rate sampling rate and gaps are open questions of R3. A design hint, to be
  confirmed: bound to the video clip's source time, as the holds are (D034, D036), so Trim / Split / Move keep the match.
- **The overlay** in the Preview and the export: a numeric value, a graphic representation and a simple animation; its look
  and position adjustable within a small set of options — not a full sports-telemetry editor.
- **R3 (research)**: real Pace 4 exports and real gym videos; the supported format; timestamps, sampling, gaps; the
  available video timestamps and their accuracy and drift; a specification. **F (implementation)**: synchronisation;
  overlay settings and Preview; the export rendering (parity); tests of synchronisation, seeking, Trim / Split, incomplete
  data; a manual check on a real gym video with a real watch export.

### 7. Final UX Pass (the last large phase)

After the planned core (including F) and the stabilisation of the main scenarios. Goal: not more functionality — the
existing functions simpler and faster to use. **Research**: CapCut, DaVinci Resolve and Kdenlive (official materials and
demonstrations, real user scenarios, user reviews and discussions of video-editor UX); concrete scenarios compared, not the
looks — selection, moving and trimming clips, the timeline, the Inspector, numeric fields and sliders, hotkeys, context
menus, text and animation, transitions, music, the export and the AI hold markup; the findings matched against this
product, its architecture and its audience; a prioritised list of about 10–15 justified improvements with benefit,
complexity and risk. No interface is copied without an assessment. **Implementation**: only the improvements that reduce the
actions of the typical scenarios, make the behaviour more predictable, make the editor easier to learn, improve the
handling of holds and their outlines, and do not turn the editor into a professional editing system. **Success
criterion**: a user without deep video-editing experience goes from importing a source to a finished bouldering video
without learning a professional editing system. The Final UX Pass is the place for a systematic, agreed interface change;
until then the rule "no redesign of unrelated UI" (D040 §7, `CLAUDE.md`) stays.

Context: the product owner's planning request of 2026-10-20, during Phase 26: the long-term product strategy and the order
of the remaining phases were nowhere recorded; the COROS idea of the earliest discussion was not in the repository; the
"Future phases" of `ROADMAP.md` listed candidates (keyframes, speed ramps) that conflict with the intended simplicity.

Consequences: documentation only — `DECISIONS.md` (this decision), `docs/DEVELOPMENT_PLAN.md` (the planned phases),
`ROADMAP.md` (the status and the remaining phases), `progress.md`. No code, no format, no dependency, no change of Phase 26's
scope (D041).

Status: Accepted (2026-10-20, product owner) as the concept and the boundaries; the phase order and the scopes of A–H are
provisional and are fixed by each phase's own decision.

### Refined after the merge of Phase 26 (product owner, 2026-10-20)

Phase 26 merged (PR #27, `41dadf2`). The owner's decisions on the order and on S1:
- **The order is confirmed**: A (text animation) is the next phase to implement — **Phase 27**, specification D043; then B
  (transitions), then C (effects and the basic music scenario), then G (stabilisation and the minimum useful release S1)
  after A–C.
- **R1 and R2** (hold proposals; outlines following the camera) may run in parallel with A–C, **without any change of the
  product code**. The implementation of D / E is agreed separately after their results — a research go is not an approval.
- **R3** (COROS Pace 4) is a separate research of real files and timestamps. **F** is possible only after the technical
  feasibility is confirmed and a separate agreement. The first version is for COROS Pace 4 only; other devices are not in
  it. Until real watch exports and a video of the same session are in hand, FIT is **not** the final format and the
  synchronisation by timestamps is **not** confirmed; R3 checks the real data, the timestamps' accuracy, the gaps and the
  drift (§6 unchanged in substance).
- **H** (Final UX Pass) stays the last large phase, after the core, F and the stabilisation.
- **S1**: a portable Windows build is enough; no installer. The criterion: the application starts outside the
  development environment (no SDK, no repository, its own folder with its dependencies, the AI model installed as
  `docs/AI_MODEL.md` describes) and the main path of §1 (without step 4) is completed in it — import to export.
- **Phase A's scope** (detailed in D043): the appearance / disappearance animation of text; the colour, size and position
  of text; one basic readability aid (an outline or a background); sensible defaults and a simple interface. No full
  title editor, no complex motion design. The UX and the acceptance criteria are specified before the implementation.
---

## D043 — Text animation and readability (Phase 27, D042 Phase A)

Date: 2026-10-20

Decision (product owner, 2026-10-20: the Phase 27 specification approved with the answers Q1–Q6 — Q1, Q2, Q5 as proposed;
Q3 the outline and the background both allowed, each on its own; Q4 a 3 × 3 grid; Q6 the defaults as given below — and
the condition that Step 27.1 is a separate technical check of the outline that stops for the owner's decision when its
result is not satisfactory). D042's Phase A. Goal: a text over a bouldering video (a route name, a grade, a gym, a caption)
that appears and disappears nicely and stays readable on a colourful wall — in a few clicks, without a title editor.

### 0. What exists (audit of `41dadf2`)

- `TextClip` (D021): `Text`, `FontFamily` (installed families; a missing one falls back), `FontSize` (canvas px; a new clip
  `48 × canvasHeight / 1080`, D030), `ColorHex` (`#RRGGBB`), `Alignment` (left / centre / right inside the box),
  `PositionX / Y` (canvas px from the centre), `Scale`, `RotationDegrees`, `Opacity`; `FadeIn / FadeOut` (D025, every clip
  kind; suppressed on an edge with a dissolve).
- The Inspector's TEXT section: a multiline box, the font list, the size field, a `#RRGGBB` field with a swatch, the
  alignment; the TRANSFORM section (position, scale, rotation, opacity) and the FADES section with sliders (D040 §4).
- The Preview's Move tool moves and scales a text by its box (D038). A new text: "+ Text" at the playhead, 5 s, «Текст».
- One rendering path: `PlaybackSnapshot.LayersAt` → `TextLayer` (transform, opacity × fade) → `CompositionDrawPlan.TextDraw`
  → `CompositionPainter` (Avalonia `FormattedText`, no wrapping, the box centred on the local origin) — the Preview and the
  export (D023) alike.
- So colour, size and position already exist; missing: any motion, a readability aid, quick placement, a colour choice
  without typing a hex code, bold, defaults made for a video caption.

### 1. Appearance and disappearance (In / Out) — Q1, Q2

- In the Inspector, for a text clip, a block **«Анимация»** with two rows, **«Появление»** and **«Исчезновение»**: a kind and a
  duration each.
- Kinds (Q1): **«Нет»**, **«Проявление»** (opacity only), **«Выезд»** (opacity + a slide; a direction: снизу / сверху / слева /
  справа), **«Увеличение»** (opacity + a scale from 70 % to 100 % around the text box's centre). The same kinds for the Out,
  mirrored in time (a slide away toward the chosen direction, a shrink to 70 %). No typewriter.
- Duration (Q1): **0.1–3 s** for a value set in the block — a field with a slider like the fades; both together never longer
  than the clip — the effective lengths are clamped as D025's fades are.
- **The data (Q2)**: the duration of «Появление» **is** the clip's `FadeIn` and of «Исчезновение» its `FadeOut`; a new
  `TextClip.MotionIn / MotionOut` (`None` / `Slide` + direction / `Zoom`) adds the motion over that same ramp. «Нет» = fade 0;
  «Проявление» = a fade without motion. For text clips the FADES section is replaced by this block (one place, no two
  controls of one value).
- **The existing texts keep their look (Q2, required)**: a v6 text clip has no motion, so it renders exactly as before —
  its fades are D025's ramps unchanged. Its fades show as «Проявление» with their durations. A stored fade outside 0.1–3 s
  (D025 allows any length up to the clip) is kept, rendered and shown as it is until the user changes it; the 0.1–3 s range
  limits what the block sets, not what a file may hold (D025's file validation unchanged).
- **The rule** (Core, on timeline frames like D025 / D036, so the Preview and the export agree on every frame): for clip
  frame `i` the fade's progress `p = Ramp(i, Fin)` (D025's linear `(k + 1)/(F + 1)`) is eased `e = 1 − (1 − p)³`
  (ease-out; the Out with `p` of the out ramp — the motion ease-in by symmetry); the opacity factor stays D025's linear
  ramp (exactly today's fade); Slide offsets the text by `(1 − e) · d` toward the start side, `d = 8 %` of the canvas's
  short side, along the canvas axis (not rotated with the text); Zoom multiplies the text's scale by `0.7 + 0.3 · e`,
  around the box centre. Applied in `PlaybackSnapshot.LayersAt` to the `TextLayer`'s transform — no renderer state. An edge
  with a dissolve has no fade (D025) and so no motion.
- Edits: a split keeps In on the left part and Out on the right part, with their motions (D025's rule for the fades); a
  trim clamps; Duplicate / Paste copy; a speed does not apply to text.

### 2. Readability: an outline and a background — Q3

- In the TEXT section two independent rows (Q3: both may be on together, each switched and set on its own):
  - **«Обводка»** — on / off, a colour (default black), a thickness in % of the font size, 2–20 %, default 8 %; drawn
    outside the glyphs under the fill, round joins.
  - **«Подложка»** — on / off, a colour (default black), an opacity 0–100 %, default 60 %; a rectangle around the text box with
    a padding of 25 % of the font size and a slightly rounded corner (fixed); it moves, scales, rotates and fades with the
    text.
- Drawing order: the background, then the outline, then the fill. A setting switched off keeps its values (switching on
  again restores them).
- Part of `TextProperties`, so the Preview, the export and the Move tool's frame (the box with the padding when Подложка is
  on) use the same data.

### 3. Colour, size, position — made quick — Q4, Q5

- **Colour**: a row of eight swatches (white, black, yellow, orange, red, green, light blue, pink) next to the existing
  `#RRGGBB` field — a click applies one colour (one Undo step); the hex field stays for any colour. The same swatches for
  the outline and the background colour. No colour-picker package (D021).
- **Size**: the existing field gets a slider (as Opacity); the range unchanged.
- **Position (Q4)**: a **3 × 3 grid** of buttons (top / middle / bottom × left / centre / right) that places the text box at
  a safe margin of 8 % of the canvas's short side from the canvas edges it is placed against (centred on the other axis
  for the middle row / centre column); one Undo step; the box size from the painter's own layout rule (the padding of
  Подложка and the outline included). The grid sets the position only — the text's alignment inside the box is unchanged.
  The X / Y fields and the free move in the Preview (D038) stay as they are.
- **Bold (Q5)**: a check box **«Жирный»** — a typeface weight in `TextProperties`; a family without a bold face falls back
  as Avalonia does, the same in the Preview and the export.

### 4. Defaults of a new text — Q6

«Текст», Segoe UI, **white, bold**, the size of D030 (48 at 1080), centred alignment, placed **bottom centre** (the grid's
bottom-centre cell), 5 s; **Подложка black 60 %** on, Обводка off (its defaults kept: black, 8 %); Появление **«Выезд» снизу
0.4 s**, Исчезновение **«Проявление» 0.3 s**. Existing text clips keep what they have (the new properties absent = none: no
outline, no background, no motion, not bold).

### 5. Data and format

- `TextClip` (and `TextProperties`): `MotionIn`, `MotionOut` (kind + direction), `Bold`, `Outline` (enabled, colour,
  thickness), `Background` (enabled, colour, opacity). Validation in `ClipPropertyValidator`; values outside the limits
  refuse the file as for other properties.
- `project.json` **v7**: the new members optional on a text clip (absent = none). A v6 file opens unchanged in meaning and is
  saved as v7; a v6 build refuses a v7 file by its newer-version check (D014), as with every earlier version — had they
  been added to v6, an older build would silently drop them on a save. A project without the new properties, saved again,
  differs only in `formatVersion` (the Phase 17 check).

### 6. Undo / Redo

- Every change through `ITimelineEditService.SetClipProperties` (D017): consecutive changes of one field (a duration, a
  thickness, an opacity, typing a hex) merge into one step; choosing a kind, a direction, a swatch, switching the outline
  or the background, Bold or a grid cell is one step each; an unchanged value is no step. Refused on a locked track,
  outside the limits and during an export (`EditingLock`). A presentation-only snapshot change (D021): no decoder restarts.
- "+ Text" with the defaults of §4 stays one step ("Add Text").

### 7. Preview / export parity

- The motion in `LayersAt` (one value per frame), the outline, the background and the weight in `TextDraw` →
  `CompositionPainter` — the one painter of the Preview and the export; paused or after a seek the Preview shows the frame
  the export writes.
- Regression: a project without the new properties renders byte for byte as before (the existing parity suites and every
  expected value unchanged).

### 8. Localization

Every new text in `Strings` / `Messages` with `.ru` (the D039 guards); the kinds, directions, the grid's tooltips and the
style rows through resources, never enum names; the layout checked at 1024 × 640 and 1440 × 900 (the Inspector's width is
the risk: the rows wrap rather than cut).

### 9. Verification and acceptance criteria

Step 27.1 first (the outline's feasibility, §12): the glyph contours' quality at small and large sizes, the export's cost
per frame, correct work off the UI thread (as the export runs the painter), Preview ↔ export parity. When the result is
not satisfactory the work **stops** and the results and the alternatives go to the product owner; a lower-quality fallback
is never chosen automatically.

Automated:
- Core: the motion rule — offsets and scales on every frame of the ramps, the ease, the clamp to the clip, both edges on a
  short clip, no motion on an edge with a dissolve, a frame-rate change (durations in time, D025); the grid placement (nine
  cells, with and without Подложка / Обводка, a portrait and a landscape canvas).
- Edits: one step per action and the merges, refusals, split / trim / duplicate / paste, Undo / Redo restoring every value.
- Format: v7 round trip of every new member; v6 opened and saved as v7 differing only in `formatVersion`; damaged values
  refused; the newer-version refusal; a v6 text with fades outside 0.1–3 s kept.
- Rendering: Preview ↔ export parity per kind, the outline, the background and both together (pixel comparison as the
  existing suites); the outline under the fill, the background under both, with the text's transform and fade; a v6 text
  byte for byte as before; the existing export suites unchanged.
- UI: the view-model and AXAML tests (the block for text clips only, kinds ↔ the fades, the two style rows, the swatches,
  the grid, Bold, the defaults of "+ Text", the localization guards).
- Gates: Release / Debug `--no-incremental -warnaserror` 0 / 0; the full suite, heavy, three `--blame-hang` runs; `git diff
  --check`.
Manual (`docs/PHASE27_MANUAL_TEST_PLAN.md`): a caption on the product owner's portrait gym clip with each kind, the outline,
the background and both; the defaults of "+ Text"; the grid and the free move in the Preview; Undo / Redo of each action;
save / reopen; the export watched in a player against the Preview; a Phase 26 v6 project with text opened and its export
compared with the Phase 26 build's; `ru` and `en`; 1024 × 640 and 1440 × 900.

Acceptance criteria:
1. A new text appears and disappears with the default animation and is readable on a colourful wall without any setting.
2. Each kind, direction, style and grid cell is chosen in at most two clicks; every action is one Undo step.
3. **v6 projects open, and their existing text clips look exactly as before the update** (their fades as «Проявление»,
   nothing else changed; checked automatically by the rendering suites and manually against the Phase 26 build).
4. **The Preview and the export use one rendering model and give the same result for the animation, the outline and the
   background** (automated parity; by eye in the manual run).
5. **Undo / Redo, save / reopen, the localization en / ru and the export regressions are checked** (the automated suites
   above and the manual plan).
6. v7 saves and reopens every new value; a v6 build refuses a v7 file with its existing message.
7. Nothing cut at 1024 × 640; no untranslated text.
8. **No function beyond the agreed scope of Phase 27** (§1–§4) is added; a need beyond it is reported with variants first.
9. The gates green.

### 10. Not in this decision

A title editor (several styles or lines with their own formatting, text templates), keyframes or motion paths, per-letter
or per-word animation (a typewriter), a shadow, gradients, emoji / image stickers, text along a curve, wrapping to a width,
a font from a file, animation of other clip kinds, new dependencies, a change of D025's fade for pictures and sound.

### 11. Questions — answered (product owner, 2026-10-20)

- **Q1** — the kinds Нет / Проявление / Выезд (4 directions) / Увеличение 70–100 %, 0.1–3 s; no typewriter. *As proposed.*
- **Q2** — the In / Out durations are the clip's fades; FADES replaced by «Анимация» for text; the v6 texts' look kept. *As
  proposed (variant a).*
- **Q3** — the outline and the background **both allowed together**, each switched and set on its own. *Changed from the
  proposal (one at a time).*
- **Q4** — a **3 × 3 grid**; the free move in the Preview kept. *Changed from the proposal (three buttons).*
- **Q5** — «Жирный» in the phase. *As proposed.*
- **Q6** — white bold, bottom centre, Подложка black 60 %, Появление «Выезд» снизу 0.4 s, Исчезновение «Проявление» 0.3 s.
  *As proposed.*

### 12. Risks

- The outline: Avalonia's glyph geometry (`FormattedText.BuildGeometry` or the equivalent) in the painter, which runs off
  the UI thread in the export — checked first in Step 27.1 (quality, cost, threading, parity); a fallback (e.g. the text
  drawn several times with offsets) is of lower quality and is never taken without the product owner's decision.
- The cost per frame of a geometry outline in the export — a cache per text and style, measured.
- The text box size for the grid and the Move frame must come from the painter's own layout rule (one rule).
- The Inspector's height grows (two style rows, the grid); the blocks must stay compact (the 1024 × 640 check).
- Reusing the fades (Q2) makes the text's fade fields look different from a picture's — intended, to be checked in use.

Context: D042 §5, Phase A; the product owner's decisions of 2026-10-20 ("Refined after the merge of Phase 26" in D042); the
specification proposed and approved the same day.

Consequences: `project.json` v7; Core (the motion and placement rules), the text model and its validation, persistence,
the painter, the Inspector; new resources (en + ru). No new dependency.

Status: Accepted (2026-10-20, product owner). Phase 27, branch `feat/phase-27-text-animation` from `41dadf2`. Step 27.1 (the
outline's feasibility) is a gate: the implementation of 27.2 and later only after its result and the owner's go. Implemented
and verified by Claude on 2026-10-20 (Steps 27.2–27.6, `progress.md`); the product owner's acceptance pending.

### Refined in Step 27.1 — the outline's feasibility (2026-10-20; **variant B chosen by the product owner the same day** — see "Decision" below)

Checked with Avalonia 11.1.3 in `Rendering.Tests/TextOutlineFeasibilityTests` (no product code): the candidate routine
draws as `CompositionPainter` does; the export's path (a render target on a thread-pool thread) and the Preview's (a
control rendered on the UI thread) compared pixel for pixel; a gallery on a coloured "wall" looked at; the cost measured.
- **D043's candidate as written fails off the UI thread.** `FormattedText.BuildGeometry` creates a `DrawingGroup` — an
  `AvaloniaObject`, whose constructor verifies the UI thread: «Call from invalid thread» in every export-path case. Drawing
  a `Geometry` made on the UI thread fails the same way later (`DrawGeometry` reads `Geometry.Transform`, a styled
  property). The thread-free route — the glyph runs turned into platform geometry by the render interface — needs
  `AvaloniaLocator.Current` and `Geometry.PlatformImpl`, which the public reference assemblies of 11.1.3 do not expose
  (private API only). `DrawingContext` cannot be subclassed (an internal constructor) to capture the glyph runs.
- **What works — variant B**: the geometry built on the UI thread (a dispatcher call from the export's thread, once per text
  and style, cached) and drawn there once, so Avalonia creates and caches its platform geometry; afterwards it is drawn on
  any thread. Results: the export path equals the Preview path byte for byte in every case (Latin and Cyrillic, 16–160 px,
  regular and bold, outlines 2–20 %, rotation and scale, Times / Consolas, a missing font); four exports in parallel
  identical; the geometry lies on the glyphs (ink boxes within 1 px of `DrawText`'s); the cost per frame at 1080 × 1920 /
  1920 × 1080 with a wall of 1 479 rectangles: the text alone 1.4–1.7 ms, with a cached outline 1.5–2.2 ms (+0.1–0.6 ms;
  rebuilt every frame, with the dispatcher call, 2.5–2.9 ms).
- **Quality finding**: the fill drawn by `DrawText` in a render target is ClearType (sub-pixel): coloured fringes between a
  white fill and a black outline; with grayscale anti-aliasing the fringes go, but at 16 px the hinted fill sits off the
  unhinted outline (the outline uneven). **The fill drawn from the same glyph geometry** is clean and even at every size —
  the variant to use for an outlined text. (The same ClearType fringes exist today on every exported text, outline or not —
  an observation, not changed.)
- **Why it is not simply taken**: variant B makes the export depend on the UI thread's dispatcher for each new text style
  (no deadlock today — the UI awaits the export asynchronously — but a cancelled or closing app must not leave the export
  waiting), and it relies on an undocumented Avalonia behaviour (the platform geometry cached by the first draw) that an
  Avalonia upgrade could change (a guard test would catch it). D023 says the painter runs on any thread independently.
- Alternatives put to the product owner: B as above (recommended); a Skia drawing operation (`ICustomDrawOperation` with
  `ISkiaSharpApiLeaseFeature`) that draws the outlined text itself — thread-free, but a direct SkiaSharp reference and its
  own text layout to keep equal to Avalonia's; Avalonia's private API (not recommended); an outline of offset copies (lower
  quality — not taken without a decision); Подложка only in Phase 27, the outline deferred (a scope change of Q3).

**Decision (product owner, 2026-10-20): variant B.** The glyph geometry of an outlined text is prepared on the UI thread and
cached; the fill of an outlined text is drawn from the same geometry (its slight softening against a text without an
outline accepted). Conditions, to be met by the implementation (Step 27.4) and shown by its tests before it is accepted:
1. **Only outlined text changes.** A text without an outline — every v6 text — is drawn by `DrawText` exactly as before,
   byte for byte (the existing parity suites and expected values unchanged); the geometric fill only with «Обводка» on.
2. **Cache invalidation.** The key holds everything that shapes the glyphs — the text, the family, the size, Bold, the
   alignment and the box (origin) — and nothing that does not (the colours, the thickness, the opacity, the transform go
   to the pen, the brush and the pushed state); a change of any key part never reuses an old geometry; the cache is
   bounded (old entries dropped), so a long edit or export does not grow it without limit.
3. **Thread safety.** The geometry is created and first drawn only on the UI thread; afterwards it is only read. The cache
   is safe for the Preview (UI thread) and an export (its own thread) at once, and one cached geometry drawn by several
   threads gives identical pixels (a test with a shared entry, not only separate ones).
4. **Cancellation and closing.** The export's request to the UI thread never blocks without an end: it is awaited with the
   export's cancellation token; a cancelled export stops while waiting; the application closing (the dispatcher shut down)
   ends the wait and the export reports a failure or a cancellation — never a hang, never a partial file kept as a good
   one. Tests: a cancel while the UI thread is busy, a dispatcher that no longer runs.
5. **A guard against Avalonia's behaviour.** The tests of variant B stay in the suite (`TextOutlineFeasibilityTests`):
   drawing the prepared geometry off the UI thread, the export equal to the Preview, parallel exports, and D043's candidate
   still failing off the UI thread (if it starts to work, the dependency can be dropped). An Avalonia upgrade that breaks
   them stops the work.
6. **Kept checks**: the quality at small sizes (16 px, 2–20 %, the gallery looked at), Preview ↔ export parity, parallel
   exports, the cost per frame (cached: +0.1–0.6 ms at 1080 × 1920 measured here).
7. **Stop rule.** If a guard test fails, or the correct end of an export (cancel, close) cannot be guaranteed, the work
   stops and the problem goes to the product owner; no automatic switch to a worse variant (offset copies, no outline).

### Implementation notes (Steps 27.2–27.5, 2026-10-20)

- **Condition 3, refined before the implementation (the pre-start check):** the 27.1 test of one geometry drawn by 16
  threads at once shows that no difference was observed — Avalonia documents no thread safety for drawing one `Geometry`
  concurrently, so the implementation does not rely on it: one `TextGlyphCache` per drawing consumer (the Preview's
  `CompositionView` — UI thread; each export's `AvaloniaCompositionRasterizer` — its thread, used sequentially), so a
  geometry is created and first drawn on the UI thread and afterwards drawn by one thread only. The 16-thread test stays
  as an observation in the guard suite. Within the approved variant B; no architecture change.
- Model (27.2): `TextClip` / `TextProperties` — `Bold`, `Outline`, `Background`, `MotionIn`, `MotionOut` as init members
  defaulting to none (`TextProperties.IsPlain` — a text drawn exactly as before); `project.json` v7 writes them only when
  set (`bold: true`; `outline` / `background` when they differ from their defaults, off ones included; a motion when not
  None); a v6 file ignores them; members of an object all required. `CloneClip` copies them (split, duplicate, paste; a
  split's fades follow D025). The canvas change scales `FontSize` as before — the outline's thickness is relative to it.
- Core (27.3): `TextMotionRule` and `TextPlacement` as specified; the motion only when a kind is set, so a text without
  motion keeps its transform bit for bit. `AddTextClip(start, measureBox)` — the §4 defaults (the fades 0.4 s / 0.3 s
  rounded to whole frames: 10 / 8 at 25 fps, 12 / 9 at 30, 24 / 18 at 60) and the bottom-centre position by the measured
  box in the same "Add Text" step; centred when no measurer is given.
- Rendering (27.4): `CompositionPainter` — a plain text by the unchanged `DrawText` path (tested against a replica of the
  old routine, pixel for pixel); a styled one: the background (a rounded rectangle, padding 25 %, corner 12 % of the size,
  its own opacity), the outline (a round-join pen of twice the visible width), the fill from the same geometry when the
  outline is on, else `DrawText` (bold or a background only). An outlined text without a cache is refused (never drawn
  without its outline). `ExportFailure.RenderFailed` (`Export_FailRender`, en + ru) for a wait that ends by the shutdown or
  the 60 s timeout; a cancel stays a cancellation. `ICompositionRasterizer.Render(…, CancellationToken)` (a default
  method; `ExportService` passes its token). The Move tool's frame includes the background's padding.
- Inspector (27.5): «Анимация» replaces the fades section for a text; switching from «Нет» to a kind gives the §4 default
  (0.4 s In, 0.3 s Out, clamped to the clip); a Zoom keeps the stored side (not shown); the durations are shown as the
  seconds of the stored frames (two decimals). Swatches: white, black, yellow #FFD600, orange #FF8C00, red #E53935, green
  #43A047, light blue #4FC3F7, pink #F06292. The thickness and the opacity take whole percent (as FU-25.2). Discrete
  choices carry a `ClipPropertyGesture` (one step, never merged). `ITextBoxMeasurer` is injected (the app registers the
  painter's `TextBoxMeasure`), so the view models work without a running platform in tests; `TimelineClipSelection`
  carries the canvas.
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
