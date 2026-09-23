# Project Progress

## Current phase

Phase 5 — Preview/playback, branch `feat/phase-5-playback`. Decisions: DECISIONS.md
D009–D012. Video playback and preview integration are implemented, manually validated by
the product owner (2026-09-23, no functional complaints) and committed as the checkpoint
"Phase 5: playback engine and preview integration". Audio playback (D010: NAudio output,
mixer, audio master clock) is **not started** — it is the next step.

### Phase 5 checkpoint summary
- Implemented: exact source-frame selection (D009); ffmpeg discovery; ffmpeg CLI video
  decoder with PTS from `showinfo`, bounded time-based preroll and hardware → software
  fallback; anchor-based playback clock (Stopwatch now, audio-ready reference); immutable
  playback snapshot + builder; `PlaybackService` / `VideoPipeline` (seek generation and
  snapshot version guards, prefetch of the next clip, still images as one cached frame,
  Offline / Unsupported / DecodeError kept distinct, underrun rule D012); Preview UI
  (DispatcherTimer tick → `Update()`, WriteableBitmap), Play/Pause/Stop/Space, playhead ↔
  seek wiring without feedback, snapshot rebuild on timeline/media/project changes (skipped
  for media changes that don't affect the timeline).
- Tests: 335 passed (Core 130, Timeline 112, UI 30, Video 63 — the Video tests run real
  ffmpeg on generated media); build 0 errors, 0 warnings.
- Deferred (explicitly, not done in this checkpoint): audio decoding, NAudio/WASAPI output,
  mixer and audio master clock; volume/mute UI; J/K/L, loop, timeline autoscroll, scrub
  cache; compositing (opacity/transform/crop), text rendering, speed ≠ 1, transitions;
  HDR/10-bit tone mapping and color management; RequestAnimationFrame-synced ticking;
  dropped/late frame counter; export (Phase 8).

Step 1 (accepted) — source-frame selection foundation:
- `Core/Common/SourceTimestamp.cs` (`TimeBase`, `SourceTimestamp`),
  `Core/Playback/SourceFrameSelector.cs` (D009), `MediaMetadata.StartTime` /
  `AvgFrameRate`; unit + property tests.

Step 2 (accepted, closed) — FFmpeg video decoder:
- `Core/Playback/DecodedFrame.cs` (BGRA + `SourceTimestamp`), `Core/Playback/IVideoDecoder.cs`
  (`IVideoDecoder`, `VideoDecodeRequest`, `IVideoFrameStream`, `VideoDecodeException`).
- `IFfmpegLocator` (Core) / `FfmpegLocator` (Infrastructure): `Ffmpeg:FfmpegPath`, then PATH.
  Shared lookup logic in `Infrastructure/ExecutableLocator.cs` (FfprobeLocator uses it too).
- `Video/FfmpegVideoDecoder.cs`, `FfmpegVideoFrameStream.cs`, `ShowInfoParser.cs`:
  `-copyts`, bounded time-based preroll with validation/retry, `-fps_mode passthrough`,
  PTS from `showinfo` (internal), `-hwaccel auto` with software retry.
- DI: `IFfmpegLocator`, `IVideoDecoder` registered (not used by UI yet).
- `tests/Video.Tests` (new project): integration tests on generated media.
- Hardware fallback: an attempt with `-hwaccel` that fails, times out or yields no frames
  is relaunched without `-hwaccel` (tested with a rejected accelerator).

Step 3 (accepted) — playback core (D011, D012), no UI / NAudio:
- Mid-stream HW → software fallback keeps already decoded frames, drops duplicates by PTS,
  keeps the seek generation (fixed after review; deterministic test with a gated reopen).
- Review of PlaybackService/VideoPipeline (10 points): one defect fixed — `UpdateSnapshot`
  re-anchored the clock on every resync, dropping the real time between reading the position
  and the new anchor; it now re-anchors only when the position must be clamped.
  `PlaybackSnapshot.Version` renamed to `SnapshotVersion`. Regression tests added: resync
  keeps clock time, lower clip resumes after an overlapping top clip, superseded pipelines
  release decoder streams, no ffmpeg process left after seeks + dispose
  (`FfmpegVideoFrameStream.LiveProcesses`, internal diagnostic counter).

Step 4 (accepted, manually validated) — UI integration on the Stopwatch clock (no NAudio):
- `PreviewView` code-behind: a `DispatcherTimer` (10 ms, UI thread, only while attached) calls
  `PreviewViewModel.Tick()`; frames are copied into two alternating `WriteableBitmap`s.
- `PreviewViewModel`: Play/Pause/Stop over `IPlaybackService`; `Tick()` polls `Update()` only
  while playing, buffering, late, or right after a transport/seek/snapshot change; exposes
  `CurrentFrame`, `PictureKind`, `PlaceholderText`, `IsPlaying`, `IsBuffering`,
  `IsPictureCurrent`; rebuilds the `PlaybackSnapshot` on `TimelineChanged`,
  `MediaAssetsChanged` and `ProjectChanged` (the latter also pauses and seeks to the new
  project's playhead).
- `TimelineViewModel`: user playhead moves (`SetPlayhead` and everything built on it) raise
  `SeekRequested`; playback positions arrive through `ShowPlaybackPosition`, which never does —
  the only feedback guard. `MainWindowViewModel` wires SeekRequested → `Preview.Seek` and
  `PlaybackPositionChanged` → `ShowPlaybackPosition`. The Stop button now calls
  `IPlaybackService.Stop()` (the old `GoToStartRequested` event is gone). Space → Play/Pause.
- `MediaAssetsChanged` rebuilds the snapshot only if an asset referenced by a timeline clip
  (any track) changed in a playback-relevant way (`PlaybackSnapshotBuilder.CaptureAssetStates`);
  importing/analysing unrelated media no longer resyncs playback or reopens decoders.
- Tests: `UI.Tests/PlaybackUiIntegrationTests.cs` (12) — real shell wiring, project/timeline
  services and PlaybackService with the fake decoder/clock (shared from Timeline.Tests).
- Manual check in the running app done by the product owner: Play/Pause, Space, Stop,
  playhead click and drag while playing, consecutive clips, gap, upper/lower video tracks,
  image clip, missing file, timeline edits while playing and paused, long playback without
  drift.
- Core/Playback: `PlaybackClock` + `IReferenceClock`, `PlaybackSnapshot` (+ `PictureSpan`,
  `AudioSpan`, `PlaybackAsset`, `SpanStatus`), `PlaybackSnapshotBuilder`, new
  `IPlaybackService` (`PlaybackFrame`, `PreviewPicture`, `PictureKind`). The old unused
  `IPlaybackService` in `IProjectService.cs` was removed.
- Timeline/Playback: `PlaybackService`, `VideoPipeline`, `SpanReader`,
  `StopwatchReferenceClock`, `PlaybackSettings`. Registered in DI.
- Tests: `Core.Tests/PlaybackClockTests.cs`, `PlaybackSnapshotBuilderTests.cs`;
  `Timeline.Tests/Playback/*` (fake decoder + fake clock); `Video.Tests/PlaybackServiceIntegrationTests.cs`
  (real ffmpeg, two clips + gap, software and -hwaccel auto).

Next: audio decoding + NAudio output/mixer with the audio device as master clock (D010).

Phase 4 — Timeline: implemented, accepted and merged into `main`.

## Last known state

Phases 0–3 are complete and committed (see `ROADMAP.md`). Work continues on
branch `feat/phase-3-media-analysis` (per product owner decision).

Phase 4 implemented (decisions: DECISIONS.md D006–D008):
- Exact time model: rational `FrameRate`, integer frame grid in `MediaTime`
  (double-based frame conversions removed; D001 unchanged).
- Project frame rate: provisional 30 FPS, fixed by the first video (from
  `r_frame_rate`, fallback 30 FPS stated explicitly), existing clips re-gridded in the
  same undo step, atomic rejection if impossible.
- `ITimelineEditService` / `TimelineEditService`: add (button → end of V1/A1,
  drag-and-drop → track + position), move (incl. between tracks), trim (clamped),
  split (selection or everything under the playhead), delete, add track, snapping.
  All undoable; validation before execution.
- Default tracks V1/A1; `TimelineChanged` + IsDirty on any timeline change.
- Timeline panel: real tracks/clips, ruler (visible range only), playhead
  (ruler click/drag, frame steps), zoom (Ctrl+wheel at pointer, buttons/hotkeys at
  playhead, fit), selection (click, Ctrl+click), drag move with red invalid preview,
  trim handles, snap indicator.
- Inspector shows the selected clip (non-drop-frame timecode); Transform hidden until Phase 7.
- Preview shows the real playhead and sequence duration; Play reports "Phase 5".
- Hotkeys: Ctrl+Z, Ctrl+Y / Ctrl+Shift+Z, Delete/Backspace, S, N, ←/→,
  Shift+←/→, Home/End, Ctrl+= / Ctrl+−; ignored while a TextBox has focus.

## Completed

- Phase 0
- Phase 1
- Phase 2
- Phase 3
- Phase 4
- Phase 5 checkpoint: video playback + preview integration (audio playback still to do)

## Known issues

- Preview color: footage from the Vivo X300 Pro (HDR / 10-bit) may look overexposed /
  washed out in the Preview. This is not a Phase 5 playback-correctness issue: the preview
  pipeline does not yet have the final HDR/10-bit → SDR color / tone-mapping pipeline
  (ffmpeg converts straight to 8-bit BGRA). Proper HDR/10-bit/color-management handling is
  deferred to the later color/export pipeline phase. Do not add ad-hoc ffmpeg color filters
  or tone-mapping hacks to the preview decoder to compensate in the meantime.

- MPEG-TS: ffmpeg `-ss` lands on the keyframe *after* the target, so TS seeks need
  preroll retries (3–4 decoder launches observed); correct but slower to open.
- `Video.Tests` needs ffmpeg/ffprobe on PATH; its tests are skipped otherwise.
- Playback underrun (D012): while decoding is slower than real time, `Update()` returns the
  previous picture with `IsPictureCurrent = false`; late frames are flagged but not counted
  yet (no dropped-frame counter).
- Video decoder limitations (deliberately out of scope for now): HDR / 10-bit (no tone
  mapping), interlaced (no deinterlacing), SAR (non-square pixels ignored), rotation
  metadata (ffmpeg autorotate applies, not handled explicitly), resolution changes
  mid-stream (untested), phone-specific VFR quirks beyond the tested cases. A hardware
  failure after the first frame is not retried by the decoder (the caller must reopen).

- Hotkey guard for text input is implemented but could not be exercised in the
  running app: Phase 4 UI has no visible text field (Inspector Transform is hidden).
- `ffmpeg-*.log` is never written: nothing tags log events with `Area=Ffmpeg`.
- Media analysis has no concurrency limit and no cancellation on New Project.
- `MediaAnalysisCoordinator` relies on the captured UI SynchronizationContext.
- Timecode is non-drop-frame only (29.97 timecode drifts from wall clock by design).
- Timeline canvas is a plain ItemsControl/Canvas; very long timelines at maximum
  zoom are not virtualized (ruler is).
- Media import is not undoable (unchanged from Phase 2).

## Verification

2026-09-23 (Phase 5 checkpoint):
- `dotnet build`: 0 errors, 0 warnings. `dotnet test`: 335 passed (Core 130, Timeline 112,
  UI 30, Video 63).
- Manual validation of video playback in the running app by the product owner: no
  functional complaints or blocking issues.

2026-09-23 (Phase 5 UI integration):
- `dotnet build`: 0 errors, 0 warnings. `dotnet test`: 335 passed (Core 130, Timeline 112,
  UI 30, Video 63); UI tests repeated 3× without failures.
- Mutations: playback position through the user path (seek feedback) → 1 failure; playhead
  moves not wired to seek → 4 failures; no snapshot rebuild on TimelineChanged → 11 failures;
  unconditional rebuild on MediaAssetsChanged → the unused-asset test fails.
- App started in Development (DI validated): window responsive, low idle CPU with the tick
  timer. Interactive playback in the running app was not exercised by automation.

2026-09-23 (Phase 5 playback core):
- `dotnet build`: 0 errors, 0 warnings. `dotnet test`: 323 passed (Core 130, Timeline 112,
  UI 18, Video 63). Playback service tests repeated 6× without failures.
- Mutations: holding the clock while buffering → 2 failures; accepting an unconfirmed
  frame → 4 failures; re-anchoring on every resync → 1 failure; not disposing superseded
  pipelines → 1 failure; clearing the buffer on HW fallback → 1 failure. Removing the explicit (version, generation) check → no failure: the
  service only reads the current pipeline and superseded readers stop publishing under a
  lock, so the check is a second line of defence (its only effective window — a pipeline
  becoming ready just before being superseded — is not reproducible deterministically).
- App started in Development (DI validated on build): shell initialized, no errors.

2026-09-23 (Phase 5 decoder):
- `dotnet build`: 0 errors, 0 warnings.
- `dotnet test`: 286 passed (Core 116, Timeline 92, UI 18, Video 60) with ffmpeg 9.0.1.
- `-hwaccel auto` verified to use DXVA2 (RTX 4070 SUPER) with the decoder's exact
  arguments for H.264, H.265 and TS; software and hardware select identical frames/PTS.
- Mutations: accepting the first frame without preroll validation → 6 failures;
  pairing PTS with the next frame → 52 failures; disabling the software relaunch →
  the fallback test fails (ffmpeg exit −22).

2026-09-23 (Phase 5 foundation):
- `dotnet build`: 0 errors, 0 warnings.
- `dotnet test`: 226 passed (Core.Tests 116, Timeline.Tests 92, UI.Tests 18).
- Mutation check: replacing δ by "frame start" or "frame midpoint only" makes the new
  tests fail (7 and 20+ Core failures; 10/12 property tests).
- ffprobe JSON checked on generated .ts/.mkv/.mp4: `start_time` "1.400000" /
  "0.000000" and `avg_frame_rate` present. Parsing itself has no unit test (no Video
  test project).

2026-09-23 (Phase 4):
- `dotnet build`: 0 errors, 0 warnings.
- `dotnet test`: 160 passed (Core.Tests 62, Timeline.Tests 80, UI.Tests 18).
- Manual run (Windows, generated test media incl. 29.97 FPS video): import, add image/
  audio/video, FPS lock to 29.97 with status message and re-grid, Inspector timecode,
  drag move, trim, ruler playhead, split via S, undo ×3, drag-and-drop from the Media
  Browser, add track + Ctrl+Z, S/N hotkeys.

## Instructions for Claude

Update this file after substantial milestones.

Do not fabricate progress.

When uncertain, inspect the repository and Git history first.
