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
  compositing, opacity, transform or crop yet. Audio: every VideoClip with an audio
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

Status: Accepted (implementation in progress).

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

## How to add a decision

When a major architectural decision is made, add:

- ID
- Date
- Decision
- Context
- Consequences
- Status

Do not rewrite old decisions merely because a newer approach is preferred. Supersede them explicitly.
