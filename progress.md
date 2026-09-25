# Project Progress

## Current phase

Phase 8 — Export: **complete** (accepted by the product owner on 2026-09-25; commit `8786491`, PR #6), branch `feat/phase-8-export` (from `2f0e26f`, the Phase 7 closeout).
Scope (DEVELOPMENT_PLAN): Timeline → MP4 (H.264/AAC) with everything Phase 7 added. Decisions: D023.

### Phase 8 — Export (complete)

Product decisions (product owner, 2026-09-24; recorded as D023):
- Architecture A: C# compositor + FFmpeg as the encoder only; the export is an offline rendering of the
  Preview from the same `PlaybackSnapshot` with the Core rules (D018/D009/D022/D013), no filtergraph.
  Core must not depend on Avalonia; the rasterizer is backend-specific and chosen by a spike in Step 3.
- Offline / unsupported / not analysed media block the export (preflight lists all of them); a decode
  error aborts it (no substitute frames/silence, partial output deleted); a missing font is a warning and
  falls back as in the Preview.
- Fixed format: CRF 18 + preset medium, no presets/bitrate; size = canvas, rate = exact project rate;
  always an AAC 48 kHz stereo track (silence if needed).
- Modal progress + Cancel, editing blocked; `LastExportSettings` is session state (not dirty, no undo);
  HDR/10-bit out of scope; performance secondary to correctness and parity.

Planned steps: 1 contract + D023 + preflight · 2 offline source-frame reading · 3 rasterizer spike +
compositor (shared draw plan in Core) · 4 offline audio mix (shared placement) · 5 FFmpeg encoder (Video) ·
6 `ExportService` orchestration · 7 export UI · 8 end-to-end Preview ↔ Export parity + measurement ·
9 closeout.

- Step 1 done (D023) — export contract, no rendering/encoding yet:
  - Core `Core/Export`: `ExportFormat` (MP4, H.264 CRF 18 / medium, AAC 48 kHz stereo 192 kbps, `.mp4`),
    `ExportOutput.For(snapshot)` (canvas, exact rate, whole frames covering the duration, matching
    48 kHz sample count), `ExportJob` (snapshot + full output path), `ExportProgress` / `ExportStage`,
    `ExportException` / `ExportFailure`, `ExportPreflight.Check(project, outputPath, environment)` →
    `ExportPreflightResult` (all issues, errors before warnings; job only without errors; issues grouped per
    media file / font with every affected clip in timeline order; only clips that reach the output).
  - `IExportService` now takes an `ExportJob` (was the mutable `Sequence` + media list + `ExportSettings`)
    and has `IsAvailableAsync` for the preflight.
  - `ExportSettings`: `Width`, `Height`, `FrameRate` (double), `VideoBitrateBps`, `AudioBitrateBps` removed
    (entity and DTO). Older `project.json` values are ignored on read (unknown properties, D014), never make
    a file damaged and are not written again; `formatVersion` stays 2; unknown format enums stay damaged.
  - Stale comments fixed (`SpanStatus.Unsupported`, `PlaybackSnapshotBuilder`, `IVideoEngine`, Export
    `ModuleInfo`, `ProjectSettings.AudioSampleRate` documented as unused); D010 marked partly superseded,
    D018/D021 point to D023; ARCHITECTURE (Export section), DEVELOPMENT_PLAN, ROADMAP updated.
  - Found while testing: `Path.GetFullPath` in .NET 8 accepts characters Windows can't store (`|`), so the
    preflight checks invalid file-name/path characters itself.
  - Tests: Core `ExportPreflightTests` (30 incl. theory rows), `ExportOutputTests` (10); Project
    `ExportSettingsPersistenceTests` (10: a Phase 7 `lastExportSettings` block loads, legacy values never
    damage, saving writes only path + format and stays v2, null/missing → defaults, unknown enums damaged);
    3 existing Project tests adjusted to the removed fields. Mutations (all caught): no relevance filter →
    1 failure, no "file gone now" check → 1, case-sensitive font grouping → 1, no invalid-character check → 1.
  - Full suite green: 1161 (Core 315, Timeline 257, Project 259, UI 193, Video 137); build 0 warnings.
  - Open before Steps 2–5: see the Step 1 report (rasterizer spike, move of `CompositionDrawPlan` to Core,
    extraction of the audio placement, `LastExportSettings` update API in Step 7).
- Step 1 accepted by the product owner (2026-09-24). Added before Step 2: `ExportOutputTests.Frame_count_boundary`
  (23.976 / 29.97 / 25: exactly N frames → N, last index N − 1; one tick more → N + 1; one tick less → N; less
  than one frame → 1) and, in Export.Tests, `FrameCount == FrameMath.CeilingFrame(Duration)` for 2 000 random
  durations per rate (playback's last frame is `CeilingFrame − 1`).
- Step 2 done (D023 refinement) — offline source-frame selection, no compositor/audio/encoder yet:
  - `src/Export`: `ExportFrameSource` (public; output frame n → `LayersAt(FromFrame(n))`, a decoded frame per
    picture layer, text layers without frame; ascending frames within `[0, FrameCount)`; readers only for the
    picture layers of the current frame) and `ExportPictureReader` (internal; per clip, sequential and
    blocking: opens at the layer's first visible frame with that frame's sample point, then advances while the
    next frame `IsAtOrBefore` the point — `SourceFrameSelector.Select` semantics incl. hold-first/hold-last;
    stills decoded once; full resolution (max 16384), software decoding; every failure → `ExportException`).
    `ExportDecodeSettings`. Export references Core only.
  - Reused Core: `PlaybackSnapshot` / `LayersAt` / `PictureLayer` / `TextLayer`, `SourceFrameSelector`
    (`SamplePoint` with `ClipSpeed`, `IsAtOrBefore`), `ExportOutput.FrameCount`, `IVideoDecoder` /
    `VideoDecodeRequest` / `DecodedFrame`, `MediaTime.FromFrame`. Nothing of D009/D018/D022 re-implemented.
  - New test project `tests/Export.Tests` (Core, Project, Timeline, Export; links `PlaybackFakes.cs` and
    `TimelineFixture.cs`; Timeline and Export grant it internals). `FakeVideoDecoder.FailAfter(…, software:)`
    added (a genuine failure also in software; default unchanged) and a stream of a source without frames
    now starts at 0 (was index −1, which produced a bogus frame).
  - Tests: Export `ExportFrameSelectionContractTests` (12 cases: every output frame vs the Preview's
    `VideoPipeline` playing from 0 and after seeks, same snapshot and fake sources — 23.976 / 29.97 × 1× /
    0.25× / 4× with trim start/end, split, a moved half (gap), a 25 fps 1.35× layer over both halves; hold-first
    (stream starting 3 frames late) at 23.976 / 29.97; hold-last (50-frame stream under a longer clip) at 1× and
    4×; culling under an opaque full-canvas video with an image and text — culled clip closed and reopened,
    still decoded once) and `ExportFrameSourceTests` (16: stalled decoder blocks instead of returning an
    earlier frame, mid-stream failure, open failures incl. ffmpeg missing, stream without frames, offline /
    unsupported span is an error, full resolution + software request, forward skipping in one stream and
    ascending order, cancellation and dispose, reader closed when the layer leaves, Export doesn't reference
    Timeline, FrameCount = CeilingFrame); Video `ExportFrameSourceIntegrationTests` (real ffmpeg, new
    1920 × 1080 test file: export frames 1920 × 1080 vs the Preview's ≤ 1280 × 720 — same frame numbers at 1×,
    0.25×, 4×, and equal to ⌊in + (n − start)·s + ½·min(s, 1)⌋; every stream closed); Core
    `ExportOutputTests.Frame_count_boundary` (3).
  - Mutations (all caught): speed ignored → 7 failures; next frame instead of the selected one → 15; no
    hold-last → 3; no culling → 1; no hold-first → 2; readers kept while culled → 2.
  - Found while testing: the new Video test first compared the process-wide `FfmpegProcess.LiveProcesses` and
    was flaky; replaced by counting its own streams. Its extra second then exposed a pre-existing race: the
    ffmpeg-decoding `DisplayOrientationIntegrationTests` ran outside the media collection, in parallel with the
    lifecycle tests that assert the global counter (`FfmpegAudioDecoderIntegrationTests.PlaybackLifecycle…`
    failed 1 in 3). Fixed by putting it in the media collection (Video.Tests 6 consecutive runs green; without
    the new test the race was not observed in 8 runs).
  - Full suite: 1193 (Core 318, Timeline 257, Project 259, UI 193, Export 28, Video 138), 3 consecutive runs
    green; `dotnet build --no-incremental` 0 warnings.
  - Open before Step 3: see the Step 2 report.
- Step 2 accepted by the product owner (2026-09-24).
- Step 3 done (D023 refinement) — shared composition plan + rasterizer; no audio/encoder/service/UI yet:
  - Refactor: `MediaTime.ToFrameCeiling` (Core); `FrameMath.CeilingFrame` delegates to it; Export's copies removed.
  - Spike (scratchpad, not in the repo), Avalonia 11.1.3 with the app's platform init: `RenderTargetBitmap` +
    `DrawingContext` off the UI thread work and match UI-thread bytes; text (3 families incl. a missing one × 3
    alignments) identical on both threads, a missing family falls back to the default (Segoe UI); 1 200 1080p renders
    on 4 threads deterministic; handles 586 → 586 and ~72–76 MB private over 3 × 400 renders; ~3.6 ms per 1080p
    frame. `SolidColorBrush` off the UI thread throws → immutable brushes only. First attempt hung: awaiting on the
    thread that ran `SetupWithoutStarting` posts continuations to a dispatcher nobody pumps (test harness now runs
    the dispatcher on its own thread). Decision: Avalonia offscreen, no SkiaSharp reference.
  - Core `Composition/CompositionDrawPlan.cs`: `ResolvedLayer`, `DrawOperation` → `FrameDraw` / `TextDraw` (text box
    rule documented), `CompositionDrawPlan` (`Build`, `Operation`, `Viewport`, `Bounds`, black `Background`),
    `ICompositionRasterizer`. UI: `PreviewDrawPlan` (Preview-only `PlaceholderDraw`, pending skipped) replaces the
    UI `CompositionDrawPlan`; `CompositionPainter` (the drawing routine formerly inside `CompositionView`, immutable
    brushes/pens, `Layout(text)`), `FrameBitmap` (straight-alpha bitmaps), `AvaloniaCompositionRasterizer`;
    `CompositionView` now = `PreviewDrawPlan` + `CompositionPainter`. Export: `ExportFrame` carries the canvas and
    `ResolvedLayer`s (was `ExportLayerPicture`) and builds its plan with `DrawPlan()`. UI.csproj allows unsafe code
    (pinning the caller's span for `CopyPixels`).
  - Found and fixed (Preview): layer bitmaps were `AlphaFormat.Opaque` → transparent image areas drawn opaque; now
    `Unpremul` (ffmpeg's straight alpha, verified). Video frames unchanged. Needs a manual look with a PNG with
    transparency in the running app.
  - Tests: Core `CompositionDrawPlanTests` (28: background/clip, vertical canvas, crop of each side at two decoded
    sizes, contain fit, scale/rotation 0/90/180/270/30/−90 and position = D018 transform, exact quarter turn,
    opacity + frame passed through, unknown source size, missing frame, order, culled/transparent/blank layers,
    text transform, viewport composition) and `MediaTimeTests` `ToFrameCeiling` (3); UI `CompositionDrawPlanTests`
    kept with unchanged expected values (API renamed) + "Preview plan without playback states = Core plan"; Export
    `ExportFrame.DrawPlan` test; new project `tests/Rendering.Tests` (50): `RasterGeometryTests` (black background,
    11 transform rows incl. sub-pixel position, 30°/45°, clipping, 5 crop rows with a quadrant pattern, opacity
    blend, straight alpha, order, vertical canvas, Preview letterbox clip, Preview == export bytes for pictures,
    rasterizer reuse over 300 frames with stable handles, contract errors) and `TextRenderingTests` (14 Preview ==
    export byte-equality cases: one/several lines, Left/Center/Right, 12/40/200 px, Segoe UI/Consolas/Times New
    Roman/missing family, rotation, scale, opacity, clipping at the canvas edge, trailing spaces; fallback = default
    family bytes; another family really used; box centred ±1 px; line alignment ±1 px; size and scale ×2 ±3 px;
    90° turn; colour and opacity; letterbox clip; preview at half size = export geometry / 2 ±2 px).
  - Mutations (all caught): old Opaque alpha → 1 failure (Rendering); viewport not applied → Core 1, UI 2; text box
    not centred → 6; crop not normalized → Core 5, UI 2; no clip → 2; no background → 17; alignment ignored → 1.
  - Full suite: 1276 (Core 349, Timeline 257, Project 259, UI 194, Export 29, Rendering 50, Video 138), 3
    consecutive runs green; build 0 warnings. App starts (shell initialized, no errors in the log).
  - Open before Steps 4/5: see the Step 3 report.
- Step 3 manually checked and accepted by the product owner (semi-transparent PNG in the running Preview; `Unpremul` stays).
- Step 4 done (D023 refinement) — offline audio PCM; no encoder/AAC/muxing/service/DI/UI:
  - Core `Playback/AudioPlacement.cs`: `AudioPlacement` (owned samples, source position for a timeline sample,
    placement of a decoded stream, decode request; 1× and other speeds) and `AudioMix` (gain, add, clamp).
    `AudioSpanReader` uses the placement instead of its inline formulas (ring buffer and underrun semantics unchanged;
    its mixing loop now `AudioMix.Add` over the ring's two contiguous pieces), `AudioMixer` uses `AudioMix.Clamp`,
    `AudioPipeline` `AudioMix.Gain`.
  - Export: `ExportAudioSource` (public, sequential `ReadAsync`, exactly `AudioSampleCount` frames of 48 kHz stereo
    float, silence where nothing plays) and `ExportAudioReader` (internal, per audible span, blocking, errors →
    `ExportException`). Muted clips / volume 0 are not decoded.
  - Test infrastructure: `FakeAudioDecoder.Fail(path, error)` and `FailAfter(path, samples)` (defaults unchanged);
    Export.Tests links `AudioFakes.cs`.
  - Tests: Core `AudioPlacementTests` (bounds, partition, span = timing, 1× exact ×4, speeds ×3, worked 0.25×/4× examples,
    resume/pause ×3, split ×3, trim ×3, SourceIn/SourceOut ×4, request) and `AudioMixTests` (6); Export
    `ExportAudioContractTests` (13: export == Preview pipeline + mixer, sample for sample — 1×/0.25×/4×/1.35× with trim,
    split, gap, hidden video track at 0.5, overlapping clip at 2, muted clip, muted track; exact 1× values; split without
    a seam; late-starting and short sources; decoder prerolls 20 000 / −3 000 / 0; clipping at +1 and −1 after the sum;
    silent project) and `ExportAudioSourceTests` (15: sequential exact count, whole frames, slow decoder waited for,
    open failures incl. ffmpeg missing, mid-stream failure, empty stream = silence, audible offline/unsupported → error,
    inaudible offline → silence without decoding, decoder only while the clip plays, cancellation, dispose); Video
    `ExportAudioIntegrationTests` (real ffmpeg, 0.25×/1×/4×: export == Preview exactly, bursts ±5.07 ms / ±0.05 ms at 1×,
    no drift, one stream opened and closed).
  - Mutations (all caught): speed placement with the 1× rule → Core 6, Timeline 1; pre-window samples not dropped → 1
    (needed the new preroll test — the fake's 100-sample preroll never produced a whole chunk before the window); export
    without clamp → 2; muted clips decoded → 6; Preview mixer without clamp → Timeline 1, Export 2; readers never
    released → 1.
  - Found: the ffmpeg decoder streams don't treat a non-zero exit after output as an error (audio: never; video: only
    before the first frame) — a mid-stream process failure would export as silence / a held frame. Not changed
    (touches Step 2 and realtime behaviour); proposal in the Step 4 report.
  - Full suite: 1338 (Core 380, Timeline 257, Project 259, UI 194, Export 57, Rendering 50, Video 141), 3 consecutive
    runs green; `--no-incremental` build of every project 0 warnings (App built to a scratch folder: the product owner's
    running app held its bin folder). No test leaves an ffmpeg process (the two running belonged to that app).
  - Open before Step 5: see the Step 4 report.
- Step 4 accepted (2026-09-24); follow-up before Step 5 — strict end of stream for the export:
  - Core: `VideoDecodeRequest.StrictEnd`, `AudioDecodeRequest.StrictEnd` (default false = the old behaviour).
  - Video: `FfmpegProcess.AbnormalExitAsync` (the one exit-code check); `FfmpegVideoFrameStream` throws `DecoderFailed` at the
    end when ffmpeg failed and nothing was delivered (unchanged) or the request is strict ("ended early after N frames");
    `FfmpegAudioStream` does the same when strict (before: never checked). `FfmpegVideoDecoder` / `FfmpegAudioDecoder` pass
    the flag through.
  - Export: `ExportPictureReader` and `ExportAudioReader` request `StrictEnd = true`. Preview readers don't set it.
  - Tests: Video `StrictEndOfStreamTests` (15; the "ffmpeg" is a script running the real ffmpeg with fixed arguments, then
    exiting with a chosen code): video and audio stream end × exit 3/0 × strict/not; cancellation while the end is pending
    (the script keeps stdout open 10 s) → `OperationCanceledException` within 8 s, no process left; `ExportFrameSource`
    with a crash after 10 frames → `DecodeFailed` at frame 9, normal exit → hold-last; `ExportAudioSource` crash after
    0.5 s → `DecodeFailed`, normal exit → silence; Preview `VideoPipeline` / `AudioPipeline` with the crashing decoder →
    last frame held (no DecodeError) / samples then silence. Every test checks `FfmpegProcess.LiveProcesses` and that no
    ffmpeg/ping it started is alive.
  - Mutations (all caught): video strict ignored → 2; audio strict ignored → 2; export video not strict → 1; export audio
    not strict → 1; strict everywhere (Preview changed) → 2.
  - Full suite: 1353 (Core 380, Timeline 257, Project 259, UI 194, Export 57, Rendering 50, Video 156), 3 consecutive runs
    green; every project built `--no-incremental` with 0 warnings (App to a scratch folder while the product owner's app
    was running). Afterwards the only ffmpeg alive belonged to that app.
- Step 5 done (D023 refinement) — ffmpeg encoder; no service/DI/UI/orchestration:
  - Experiments first (scratchpad, FFmpeg 9.0.1): colour (untagged = BT.601 matrix, red Y 81; `-colorspace` alone leaves
    primaries/transfer unknown; explicit scale + setparams = BT.709 limited ±1, all tags, RGB round trip ≤ 3), AAC priming
    (1024 samples, pts −1024, removed by the edit list; decoded = written samples; identical for single-pass and two-pass
    with stream copy), rational rates (exact pts and durations at 23.976/29.97/25/59.94/24/50, one frame included).
  - Core `Export/IExportEncoder.cs` (`IExportEncoder`, `IExportEncoding`). Video `FfmpegExportEncoder` +
    `FfmpegExportEncoding` (two passes, temp files next to the destination, move on success, cleanup otherwise);
    `FfmpegProcess`: optional stdin (`redirectStdin`, `Stdin`, `CloseStdin`), `Dispose` tolerates an unflushable stdin.
  - Tests (Video, real ffmpeg): `FfmpegExportEncoderTests` (17: command lines; 23.976/29.97/25/59.94 exact pts, duration,
    nb_frames, H.264 High yuv420p, BT.709 tags, every frame once in order; sub-frame project = 1 frame + 1 602 samples;
    colours ×2 (normal and padded stride): YUV vs BT.709 formula ±1, RGB round trip ≤ 3, byte order; AAC-LC 48 kHz
    stereo, 192 429 bit/s, peak unchanged; silent track; priming: pts −1024, start 0, duration_ts = samples, burst
    +0.5 sample; A/V sync 23.976/29.97/25, short project, audio starting later, several bursts: |Δ| ≤ 0.035 ms, audio end
    − video end ∈ [0, 1 sample]; audio up to the end / ending earlier) and `FfmpegExportEncoderFailureTests` (12: ffmpeg
    missing, folder missing, failing audio/video pass after reading and at once — existing destination untouched,
    success replaces the destination, cancelled write, write blocked by an encoder that never reads ended by
    cancellation < 5 s, cancelled completion, dispose of an unfinished encoding, order/count violations; every test:
    process counter at baseline, no ffmpeg/ping left, no temporary file).
  - Mutations: no BT.709 handling → 7 failures; rate as a rounded decimal → 6; output written straight to the
    destination → 5; exit code ignored → 2. An explicit kill-on-cancel for blocked writes survived its mutation (.NET
    already cancels the pending pipe write) and was removed again; the blocked-write test stays as the guard.
  - Full suite: 1382 (Core 380, Timeline 257, Project 259, UI 194, Export 57, Rendering 50, Video 185), 3 consecutive runs
    green; every project `--no-incremental` 0 warnings (App to a scratch folder, the product owner's app still running);
    only that app's ffmpeg alive afterwards.
  - Open before Step 6: see the Step 5 report.
- Step 5 accepted by the product owner (2026-09-24).
- Step 6 done (D023 refinement) — `ExportService` orchestration; no export UI / dialog / settings / lifecycle:
  - Export `ExportService : IExportService`: Preparing (one rasterizer from `Func<ICompositionRasterizer>`, encoder
    start) → Audio (`ExportAudioSource` → `WriteAudioAsync`, 0.5 s chunks, until the source ends) → Video (per frame
    `ExportFrameSource` → `ExportFrame.DrawPlan()` → rasterizer → one reused BGRA canvas, stride = width · 4 →
    `WriteFrameAsync`) → Finalizing (`CompleteAsync`). `Task.Run`; progress = existing `ExportProgress` (Preparing 0/1,
    Audio samples, Video frames, Finalizing 0/1 → 1/1 after success). No preflight re-checks, no composition/audio/output
    logic; failures and cancellation propagate unchanged; `await using` disposes sources, rasterizer and the unfinished
    encoding on every other exit. `IsAvailableAsync` = `IFfmpegLocator`. Export.csproj: Logging.Abstractions (as Video).
  - App DI: `IExportEncoder` → `FfmpegExportEncoder`, `Func<ICompositionRasterizer>` → `new AvaloniaCompositionRasterizer()`,
    `IExportService` → `ExportService` (resolved from `AddAiVideoEditor()` in a scratch console: ffmpeg found, available).
  - Tests: Export `ExportServiceTests` (21, fakes: stage order + exact audio = `ExportAudioSource`'s PCM + every frame's
    plan/canvas/stride; progress monotonic per stage, 1/1 only after completion; off the calling thread; one rasterizer
    per export; cancellation during audio / video / completion / blocked encoder writes (audio, frame) / before start;
    video + audio decode failure, ffmpeg missing in the decoder, encoder failures at start/audio/frame/complete incl.
    `OutputFailed` — same exception instance; rasterizer failure; every part disposed, encoding never completed).
    New project `tests/ExportEndToEnd.Tests` (26, real ffmpeg + Avalonia, links `RenderHarness.cs`, `FfmpegTools.cs`,
    `ExportEncoderHarness.cs`; Timeline/Video grant it internals): project → preflight → service → MP4, every export
    checked with ffprobe (H.264 yuv420p, size, exact `r_frame_rate`, `nb_frames`, AAC-LC 48 kHz stereo, decoded sample
    count = `AudioSampleCount`, duration ±1.1 ms), only the MP4 in its folder, `FfmpegProcess.LiveProcesses` = 0.
    Composition (9 scenes: solid, crop, scale, rotation, opacity, text, layers, vertical 180 × 320, hidden track):
    decoded MP4 vs encoded canvases mean |Δ| ≤ 3, Preview (its `VideoPipeline` + `CompositionView` at canvas size)
    byte-equal to the export canvas at frames 0/12/24, scene-specific pixels. Audio: one clip, overlap + muted clip +
    muted track, hidden video track keeps sound (black picture), 2× speed (bursts on the 0.25 s grid ±10 ms), no
    audio → silent AAC track. A/V sync at 23.976/29.97/25 (white frames vs burst onsets ±1 ms, no drift), 3-frame and
    1-frame projects. Failures: success replaces an existing file; cancellation in audio/video/finalizing; source
    deleted after preflight → `DecodeFailed`; folder deleted → `OutputFailed`; ffmpeg missing → `EncoderUnavailable` —
    destination byte-identical, no temporary file, no process.
  - Mutations (all caught): export on the caller's thread → 3 failures; Finalizing 1/1 before `CompleteAsync` → 4;
    encoding not disposed → 8; failures wrapped into `ExportException` → 8; cancellation turned into
    `ExportException` → 5; a rasterizer per frame → 3; frame source not disposed → 8; audio / frame write without the
    token → 1 each (needed the new blocked-encoder test). The first "caller's thread" mutation hung the off-thread test
    (its blocking factory waited forever); the wait is now bounded so the mutation fails instead.
  - Found: at 2× a burst starting exactly at the clip's `SourceOut` leaves ~10 ms of energy before the clip end
    (atempo window, Step 4 placement — Preview identical); not changed, the speed test ends the clip away from a burst.
- Step 6 accepted by the product owner (2026-09-25).
- Step 7 done (D023 refinement) — export UI; no pipeline change, no Step 8:
  - UI: `ExportWorkflow` (UI/Services, like `ProjectFileWorkflow`): `ExportPreflight` without the output file (its
    output-file issues wait for the second check) → errors listed apart from warnings, stop; warnings → Continue /
    Cancel → save-file picker (`IFilePickerService.PickSaveFileAsync`, new; `.mp4` filter, starts at
    `LastExportSettings.OutputPath` or the project folder + name; no overwrite prompt of its own) → `ExportPreflight`
    with the file (job + snapshot) → "Replace file?" for an existing file → `EditingLock` + modal progress window →
    `IExportService.ExportAsync` → close window, unlock → outcome: success (dialog + status with the path,
    `LastExportSettings.OutputPath` set, not dirty, no undo step), cancelled (status only), failure per `ExportFailure`
    (dialog + status, logged as warning), anything else = unexpected error (generic dialog, logged as error with the
    exception). Picker cancelled → silent.
  - `ExportProgressViewModel` (stage, done/total, percent = done/total, detail text, Cancel = `cts.Cancel()` once,
    "Cancelling…"); the service's reports only store the latest value, the window's `DispatcherTimer` pulls it
    (Preview tick pattern) — no marshalling, nothing after close. `IExportProgressDialog` /
    `AvaloniaExportProgressDialog` (modal, built in code like `AvaloniaDialogService`; title-bar close = Cancel; closes
    only via `Close()` after `ExportAsync` returned).
  - `EditingLock` (singleton, shared): Toolbar New/Open/Save/Save As/Undo/Redo/Import/Export, Timeline Split/Delete/
    +Video/+Audio/+Text and drag/trim/drop, Inspector clip fields (`IsEditingAllowed`, edits rejected and fields
    re-synced), Media Browser Import/Add to Timeline — all disabled while locked; playhead, zoom, snapping, selection,
    playback unchanged. Optional constructor parameters (old call sites unchanged); undo/redo semantics unchanged.
  - DI: `EditingLock`, `IExportProgressDialog` → `AvaloniaExportProgressDialog`, `ExportWorkflow`; the whole graph
    validated with `ValidateOnBuild` in a scratch console.
  - `LastExportSettings`: session state on the project, updated only after a successful export, never dirty/undoable.
    No new settings. (Serialization removed at the closeout, see below.)
  - Tests: UI `ExportWorkflowTests` (22: command availability/lock; empty project; errors vs warnings; warnings accept /
    stop; ffmpeg missing; picker cancelled silently; picker defaults; `.mov` reported not renamed; replace confirmation;
    success path / status / `LastExportSettings` / not dirty / no undo / nothing imported; progress values stage by stage
    and success only after the task; snapshot fixed at start; cancel = token only, window and lock kept while unwinding,
    no second export, not an error, destination unchanged; closing the window cancels; editing lock across panels incl.
    an Inspector edit and a drop while locked; 4 failure categories; unexpected error logged with the exception;
    distinct messages). Rendering `ExportProgressDialogTests` (2, real Avalonia window: timer shows progress, title-bar
    close only cancels, Cancel once, `Close` closes). UI internals visible to Rendering.Tests (window accessor).
  - Mutations (all caught): no editing lock → 2; no replace question → 1; cancellation as failure → 3;
    `LastExportSettings` on every outcome → 5; window closed on cancel → 1; first preflight ignored → 3; Inspector not
    guarded → 1; Timeline not locked → 1; unexpected error not logged → 1.
  - Manual test plan: `docs/EXPORT_MANUAL_TEST_PLAN.md` (14 scenarios) — run at the closeout, see below.
- Step 7 accepted by the product owner (2026-09-25) with one contract correction, done:
  - `LastExportSettings` is session-only (D023 corrected): `ProjectSerializer` no longer writes or reads it
    (`ProjectFileDto.LastExportSettings`, `ExportSettingsDto` and both mappings removed; recovery files use the same
    DTO). A `lastExportSettings` of an older file is ignored as an unknown property (D014) — any content, also formats
    that used to make the file damaged; an opened project starts empty. `ExportWorkflow` unchanged.
  - Tests: Project `ExportSettingsPersistenceTests` rewritten (10: project file and recovery file never contain it; an
    older file's value is not restored; 6 older values incl. unknown formats, null, number, string, array load; Save
    after an export through `ProjectService` writes no `lastExportSettings`, keeps the value for the session, Open
    starts empty); `ProjectSerializerRoundTripTests` expects it not restored; one validation test no longer removes it.
- Step 7 manual test (2026-09-25): `docs/EXPORT_MANUAL_TEST_PLAN.md` 14/14 PASS, 0 FAIL, 0 BLOCKED — the current build driven
  through its real UI (UI Automation; scenario projects written with `ProjectSerializer`, outputs mostly at the picker's
  suggested path). Successful MP4s checked with ffprobe (H.264 High yuv420p BT.709, exact rate/frames, AAC-LC 48 kHz
  stereo, full decode clean, flashes vs bursts ≤ 0.02 ms) and played with Windows Media Foundation; Preview == export on
  text / transforms / hidden track; cancel in Audio / Video / Finalizing and a source vanishing mid-export leave an
  existing output byte-identical and no temporary file; Replace only after confirmation; re-export byte-identical.
  Observed, not export defects: the Preview's idle decoders keep the sources under the playhead open (they can't be
  renamed while the project is open — Phase 5 behaviour); the known close hang after opening a project reproduced
  (not investigated, still open).
- Step 7 closed (2026-09-25).
- Step 8 — end-to-end Preview ↔ Export parity + measurement (D023 "Refined in Step 8"); tests only, no production change.
  Product owner decisions at the discovery (2026-09-25): A1 + A2 + A3 (1–2 cases) + A4; B software decoding in the tests
  (`Hardware = Auto` not a criterion); C measurement as a scratch tool, not in the suite, no `ExportService`
  instrumentation; D stop and report any production divergence (none was found); E sources PNG alpha, JPEG,
  display-matrix 90°, speed 0.25×/2×/4×, source rate ≠ project rate, 4K (no VFR/HEVC); F 4K/long/measurement scenarios
  out of the fast suite (`HeavyFfmpegFact` — only with `AIVE_HEAVY_TESTS=1`). Sub-steps, each accepted separately:
  - 8.1 generators (`E2EMedia`: `Pattern(rate, w, h, seconds, crf)`, `Pattern1080`, `Pattern4K`, `PngAlpha`, `Jpeg`,
    `Rotated90`; `ProjectBuilder.Image`) and `GeneratedMediaTests` — every generated file checked on itself with
    ffprobe/ffmpeg and as the app sees it (analysis, decoder, `PlaybackSnapshotBuilder`); 6 mutations caught.
  - 8.2 `ExportParityCanvasTests` (15, A1): the Preview draws the export canvas byte-equal for PNG alpha (plain and
    transformed), JPEG (plain and cropped), rotated video (landscape pillarbox, portrait), speed 0.25×/2×/4×, five
    source/project rate pairs and a mixed scene; independent oracle: the source frame chosen by an exact D009/D022
    computation, decoded by ffmpeg, against the canvas. 5 mutations caught (the ones in shared code only by the
    independent checks: oracle, alpha bands, pillarbox).
  - 8.3 `ExportParityScaledTests` (4 × 1080p + 2 heavy 4K, A2): decisions 1a (flat colour R ≤ 4, G ≤ 3, B ≤ 4) and 2a
    (geometry ±1 px on luma); bar edges by the mid-level crossing (the Preview's upscaled edges are soft). 5 mutations
    caught (shift 2 px, scale 1.01, B +5, G +4, next frame).
  - 8.4 `ExportParityViewportTests` (2, A3) and the shared `ParityMetrics`: 1920×1080 in 960×540 (scale ½) and
    1080×1920 pillarboxed in 960×540 (scale 0.28125, offset 328.125) against an exact area reduction of the canvas;
    4 mutations caught. One `Project.Tests` host hung once (1 of 23 runs, not reproducible, project unchanged) → final
    `--blame-hang` check at 8.7.
  - 8.5 `ExportParityEncodedTests` (5 picture + 4 sound, A4), decision 5c: MP4 → Preview = geometry (≤ 0.01 % of the
    pixels outside the ±1 px luma mask, decision 5a), bar edges ±1 px, same source frame (not in the static PNG scene);
    colour not a criterion there. Sound: AAC vs the Preview's `AudioPipeline` + `AudioMixer` — same length, lag and
    onsets ≤ 10 ms, SNR ≥ 20 dB sanity bound; 4/4 (SNR 28.4–39.5 dB, lag 0, onsets ≤ 0.48 ms). Mutations: P1 shift,
    P2 scale, P3 next frame, P5 BT.601 matrix tagged BT.709, S1 audio +15 ms, S2 volume ×0.5 caught; P4 (export colour
    B+6) intentionally not caught by this leg — the canvas-level parity (8.2, Step 6 composition) and `Rendering.Tests`
    catch it. The codec leg MP4 → export canvas: decision L1-c — no tolerance, measurement first.
  - 8.6 measurement only (scratch `CodecMeasure`, outside the repository; no thresholds, suite unchanged; two identical
    runs): MP4 → export canvas on the 30 existing scenes, every frame (820), 8-bit RGB, split into floor (same BT.709
    4:2:0 conversion, libx264 `-qp 0`, vs canvas) and quant (MP4 vs that lossless reference). Summary and limits in D023
    (2); per scene:

    | Scene | Size, frames | Total mean / p99 / max / PSNR | Floor mean / max / PSNR | Quant mean / p99 / max / PSNR |
    |---|---|---|---|---|
    | comp/solid | 320×180, 25 | 1.00 / 1 / 1 / 48.1 | 1.00 / 1 / 48.1 | 0.00 / 0 / 0 / lossless |
    | comp/opacity | 320×180, 25 | 1.33 / 2 / 2 / 45.1 | 1.33 / 2 / 45.1 | 0.00 / 0 / 0 / lossless |
    | comp/hidden | 320×180, 25 | 1.00 / 1 / 1 / 48.1 | 1.00 / 1 / 48.1 | 0.00 / 0 / 0 / lossless |
    | comp/text | 320×180, 25 | 0.14 / 3 / 62 / 50.5 | 0.01 / 1 / 67.7 | 0.14 / 3 / 62 / 50.6 |
    | canvas/png-alpha | 320×180, 25 | 2.60 / 52 / 197 / 26.7 | 2.55 / 193 / 26.7 | 0.21 / 4 / 19 / 50.4 |
    | canvas/jpeg | 320×180, 25 | 1.49 / 14 / 47 / 38.5 | 1.30 / 40 / 39.3 | 0.45 / 5 / 13 / 46.7 |
    | comp/crop | 320×180, 25 | 0.78 / 13 / 70 / 39.9 | 0.62 / 36 / 42.2 | 0.37 / 8 / 62 / 43.8 |
    | comp/scale | 320×180, 25 | 1.99 / 62 / 255 / 26.5 | 1.87 / 255 / 26.6 | 0.36 / 8 / 84 / 43.3 |
    | comp/rotation | 320×180, 25 | 1.11 / 17 / 75 / 37.3 | 0.86 / 43 / 38.8 | 0.51 / 9 / 67 / 42.5 |
    | comp/vertical | 180×320, 25 | 2.32 / 62 / 255 / 26.3 | 2.18 / 255 / 26.4 | 0.43 / 9 / 65 / 42.2 |
    | comp/layers | 320×180, 25 | 2.39 / 28 / 255 / 31.3 | 2.00 / 254 / 31.9 | 0.93 / 12 / 60 / 39.9 |
    | canvas/png-alpha-transformed | 320×180, 25 | 2.52 / 32 / 245 / 29.9 | 2.13 / 239 / 30.4 | 0.94 / 11 / 77 / 40.1 |
    | canvas/jpeg-crop | 320×180, 25 | 3.02 / 47 / 236 / 27.2 | 2.72 / 236 / 27.4 | 0.79 / 10 / 60 / 41.1 |
    | canvas/rotated-landscape | 320×180, 25 | 2.35 / 65 / 255 / 26.2 | 2.21 / 255 / 26.4 | 0.44 / 9 / 64 / 42.2 |
    | canvas/rotated-portrait | 180×320, 25 | 1.92 / 24 / 74 / 34.9 | 1.59 / 50 / 36.1 | 0.75 / 11 / 64 / 41.0 |
    | canvas/mixed | 320×180, 30 | 4.02 / 73 / 255 / 24.4 | 3.61 / 255 / 24.5 | 1.10 / 13 / 94 / 38.9 |
    | canvas/rate-24000_1001-30000_1001 | 320×180, 30 | 1.64 / 18 / 85 / 36.5 | 1.33 / 39 / 38.5 | 0.72 / 11 / 77 / 40.8 |
    | canvas/rate-25_1-30000_1001 | 320×180, 30 | 1.63 / 18 / 75 / 36.6 | 1.33 / 39 / 38.5 | 0.72 / 11 / 59 / 40.8 |
    | canvas/rate-60000_1001-25_1 | 320×180, 30 | 1.64 / 19 / 81 / 36.6 | 1.33 / 39 / 38.5 | 0.72 / 11 / 62 / 41.0 |
    | canvas/rate-50_1-24000_1001 | 320×180, 30 | 1.64 / 19 / 72 / 36.6 | 1.33 / 38 / 38.5 | 0.73 / 11 / 60 / 41.0 |
    | canvas/rate-30000_1001-25_1 | 320×180, 30 | 1.64 / 19 / 72 / 36.5 | 1.33 / 39 / 38.5 | 0.73 / 11 / 68 / 40.8 |
    | canvas/speed-5 | 320×180, 25 | 1.53 / 16 / 72 / 37.4 | 1.36 / 39 / 38.4 | 0.55 / 7 / 50 / 44.0 |
    | canvas/speed-40 | 320×180, 25 | 1.67 / 19 / 72 / 36.2 | 1.35 / 43 / 38.2 | 0.76 / 11 / 76 / 40.6 |
    | canvas/speed-80 | 320×180, 25 | 1.74 / 20 / 75 / 35.9 | 1.40 / 43 / 37.8 | 0.83 / 12 / 62 / 40.1 |
    | scaled/1080p-full | 1920×1080, 5 | 1.08 / 11 / 77 / 40.6 | 0.94 / 37 / 43.0 | 0.27 / 8 / 58 / 44.7 |
    | scaled/1080p-scaled | 1920×1080, 5 | 0.60 / 12 / 255 / 33.4 | 0.57 / 255 / 33.5 | 0.12 / 4 / 66 / 47.8 |
    | scaled/1080p-rotated | 1920×1080, 5 | 0.90 / 19 / 255 / 32.3 | 0.82 / 255 / 32.4 | 0.21 / 6 / 84 / 45.5 |
    | scaled/1080p-on-720p | 1280×720, 5 | 2.11 / 43 / 255 / 29.2 | 2.02 / 240 / 29.4 | 0.38 / 10 / 63 / 42.7 |
    | viewport/landscape | 1920×1080, 5 | 1.27 / 10 / 173 / 40.0 | 1.12 / 141 / 41.8 | 0.33 / 7 / 57 / 44.9 |
    | viewport/portrait | 1080×1920, 25 | 1.29 / 14 / 142 / 38.0 | 1.21 / 112 / 38.4 | 0.30 / 5 / 75 / 47.3 |

    Not measured in Step 8: export throughput, memory, handles, cancel latency (discovery C1) — 8.6 was scoped to the
    codec error by the product owner; throughput stays Phase 9.
  - 8.7 closeout (this entry): D023 "Refined in Step 8" separates the normative criteria, the 8.6 results and the
    measured H.264 characteristics; no codec tolerance added (open decision); ARCHITECTURE and ROADMAP updated,
    DEVELOPMENT_PLAN unchanged (Phase 8 stays unchecked until acceptance); verification below. The planned Step 9
    (closeout) is done as part of 8.7.
- Step 8 closed (2026-09-25); Phase 8 awaits the product owner's review of Step 8.7.
- Phase 8 accepted by the product owner (2026-09-25): Step 8.7 reviewed, work committed as `8786491`, PR #6 to
  `main`. The codec leg MP4 → export canvas stays an open product decision (L1-c, no numeric tolerance).

## Phase 7 (complete)

Phase 7 — Basic editing: **complete** — manually accepted by the product owner on 2026-09-24
(Steps 1–9, last checkpoint `48a3f54`; Step 10 closeout, see below), branch `feat/phase-7-basic-editing`
(from `main` `9fd38e7`, which contains Phases 5 and 6). Scope (DEVELOPMENT_PLAN): speed, volume, opacity,
transform, crop, text — static per-clip properties, no keyframes.

### Phase 7 — Basic editing (complete)

Product decisions (product owner, 2026-09-23):
- Compositing: the Preview draws layers on the GPU with Avalonia `DrawingContext`; D010's
  "topmost track wins" is dropped. Core owns the pure geometry (matrix, crop, opacity), the
  Preview only renders it. The rules must be reproducible by the Phase 8 export (→ D018).
- Canvas: `ProjectSettings.FrameWidth × FrameHeight` only (default 1920×1080), never taken from
  the first video; no resolution UI in Phase 7. The Preview must stop assuming a fixed 960×540.
- Speed: 0.25×–4×, UI step 0.05×, exact rational (not `double`), pitch preserved. Changing speed
  keeps Start, recomputes Duration / source range, is rejected on overlap (no ripple).
  `project.json` v2 that still reads v1 (→ D022; D021 is Step 8, text clips).
- Text: multiline; only the existing properties (text, font, size, color, alignment, position,
  scale, rotation, opacity). No outline/background/shadow/stroke.
- Transform UX: numeric Inspector fields only; no handles on the Preview.
- Volume: 0–200 % linear in the UI, linear gain internally; no dB.
- Mute: a separate state (never Volume = 0) for every clip that can carry audio, incl. VideoClip.
- Only the primary selected clip is edited; no multi-selection editing.
- Playhead / zoom / snapping are session state (D015, decided).

Steps: 1 D015 + zoom/snapping reload fix · 2 clip property editing foundation (edit service,
command, undo merging that respects the save point) · 3 load validation of property ranges ·
4 volume/mute end to end · 5 composition model in Core · 6 multi-layer playback with reader reuse
across property-only snapshot updates · 7 Preview rendering + Transform/Opacity/Crop in the
Inspector · 8 text clips · 9 speed · 10 closeout.

- Step 1 done — D015 decided (session state). Fixed: `TimelineViewModel` read zoom and snapping
  only in its constructor, so after New/Open/Recover it kept the previous project's values and
  the snapping toggle wrote them into the new sequence. On `ProjectChanged` it now cancels any
  gesture, clears the selection and reads zoom/snapping from the new sequence.
  Tests: `UI.Tests/TimelineSessionStateTests.cs` (3: New, Open restores saved values, playhead/
  zoom/snapping never dirty or undoable). Mutation: old handler → 2 failures.
- Step 2 done (D017) — clip property editing foundation, no UI/playback/persistence yet:
  `ITimelineEditService.SetClipProperties(clipId, ClipPropertyChange)` with typed
  `VisualProperties` / `AudioProperties` / `TextProperties` (Core/Entities/ClipProperties.cs) and
  `ClipPropertyLimits`; `ClipPropertyValidator` (kind, ranges, finite, crop, `#RRGGBB`; moved to
  Core in Step 3; locked track → reject without changes); `ClipPropertyValues` (capture/apply/diff) and
  `SetClipPropertiesCommand` (absolute before/after); `IMergeableCommand` + merging in
  `UndoRedoService` (not into the save point, not after Undo, new instance on merge, step removed
  when edits cancel out); `NotifyingCommand` forwards merging. Model: `VideoClip.IsMuted` added
  (split copies it). Tests: `Core.Tests/UndoRedoMergeTests.cs` (10),
  `Timeline.Tests/ClipPropertyEditTests.cs` (55 incl. theory cases; bit-exact undo/redo of all
  properties). Mutations: no save-point guard → 3 failures; merge after Undo → 2; no cancel-out → 1.
  Pending for later steps: persist `VideoClip.IsMuted` (Step 3), honour it in playback (Step 4).
- Step 3 done (D017) — load validation + `VideoClip.isMuted` in format v1 (optional, no version
  bump; older files load unmuted). `ClipPropertyValidator` moved to Core and gained
  `ValidateCurrent(clip)`; `ProjectSerializer` runs it on every clip read (project.json and
  recovery files share this path), so NaN/∞, out-of-range values, crop edges outside [0, 1) or
  opposite edges summing to ≥ 1, empty/missing font, bad color, over-long text → `ProjectFileException`
  before the current project is touched. Properties of another clip kind can't be represented
  (one DTO per clip type); stray JSON properties stay ignored (D014).
  Tests: `Project.Tests/ClipPropertyPersistenceTests.cs` (45 incl. theory cases: bit-exact round
  trip of every Phase 7 property, mute written/read, a literal Phase 6 v1 file without `isMuted`,
  30 damaged values, 8 non-finite literals, foreign properties ignored) and
  `ProjectServiceTests.Failed_open_of_a_project_with_an_invalid_clip_property_changes_nothing` (3:
  project, history, save point and events untouched). Finding: System.Text.Json reads `1e999` as
  ∞ — only the new validation rejects it; `NaN`/`"Infinity"` are already refused by the parser.
  Mutations: no validation on load → 36 failures; `isMuted` not read → 2, not written → 2; crop
  sum allowed to reach 1 → 2; NaN passing the range check → 4 (edit tests; on load the parser
  already blocks NaN).
- Step 4 done (D013 refinement, D017) — volume/mute end to end:
  - Inspector: AUDIO section (volume 0–200 %, step 1, linear; Mute checkbox, not focusable so
    Space stays Play/Pause) for AudioClips and VideoClips with an audio stream. Edits only via
    `SetClipProperties`; `ShowClip` fills the fields under a sync guard; rejected edit → status
    message + fields back to the model. `InspectorViewModel` now takes `ITimelineEditService` +
    `StatusService`.
  - Playback: `AudioSpan.IsMuted` / `EffectiveGain`; muted VideoClips and AudioClips stay in the
    snapshot (reader keeps running); `PlaybackSnapshot.DiffersOnlyInMix`; `PlaybackService` handles
    such updates without a resync (same VideoPipeline, readers, seek generation, picture;
    `AudioPipeline.UpdateMix`). Found by the existing `SupersededPipelines` test while
    implementing: the picture "current" version must be recorded where pipelines are created,
    otherwise a seek after a mix-only update never became current (fixed; regression test).
  - Tests: Core `PlaybackSnapshotMixTests` (6) + builder test updated; Timeline
    `MixUpdatePlaybackTests` (7: no pipeline/reader/generation change and no buffering over 9 edits
    incl. undo/redo and an opacity edit, paused, seek after mix-only update, mute silences only the
    clip's own audio and unmute restores its volume, volume 0 ≠ mute, clip starting muted, picture
    change reuses every audio reader); UI `InspectorAudioTests` (11: visibility, no echo edits incl.
    a 1/3 volume, one undo step / merged spins, undo/redo refresh, mute vs volume, rejection,
    selection follow, save → reopen, playing without decoder reopen); Video
    `MixUpdateIntegrationTests` (real ffmpeg: no ffmpeg process started, sine silent when muted,
    half amplitude at 50 %). `SupersededPipelines…` now makes a real timeline change (an identical
    snapshot is mix-only now). Mutations: no mix-only path → 4 failures (Timeline 2, UI 1, Video 1);
    mixer ignoring mute → 3; muted audio clips dropped from the snapshot → 4; no Inspector sync
    guard → 1; video mute not passed to the snapshot → 4.
- Step 5 done (D018) — composition model, pure Core (no playback/UI change):
  - `Core/Composition`: `Affine2D` (column vectors, Y down, exact quarter turns), `FrameSize`, `RectD`,
    `PointD`, `CompositionMath.Layout` / `TextTransform`, `LayerGeometry`, `CompositionLayer` →
    `PictureLayer` (`OccludesBelow`) / `TextLayer`, internal `ExactRational` (BigInteger) for the
    coverage proof. `ClipPropertyValidator.ValidateVisual` public; `VisualProperties.Default`.
  - Semantics fixed from the existing model: Position = centre offset from the canvas centre in canvas
    pixels (+Y down); rotation clockwise around the picture centre; fit = contain, before scale.
  - Snapshot: `PictureSpan.Visual` / `SourceSize` (from metadata), `TextSpan` + `VideoLayer.Texts`,
    `PlaybackSnapshot.Canvas`, `LayersAt(time)`. `DiffersOnlyInMix` → `DiffersOnlyInPresentation`
    (also ignores picture/text properties, source size, canvas) so a property edit still keeps every
    decoder (one-line call change in `PlaybackService`; Step 4 regression tests unchanged and green).
  - Tests: `Core.Tests/CompositionMathTests.cs` (40 incl. theory rows; exact matrices: landscape,
    portrait, letterbox, crop of each side, square crop, scale < 1 / > 1, 0/90/180/270/−90/±360 and
    30°, clockwise sign, position, opacity 0/0.5/1, all combined, five order-of-operation proofs,
    vertical and other canvases, exact-edge coverage incl. the double nearest 4/3, text transform,
    invalid input); `CompositionLayersTests.cs` (20: bottom-to-top by track order, time ranges,
    culling and its limits — transparency, 0.999 opacity, scale 0.99, crop, 0.5 px offset, rotation —,
    provable cover with crop/zoom and at 45°, images/offline/unknown size never cull, invisible
    clips, text layers, text and media on one track, vertical project, PictureAt unchanged);
    `PlaybackSnapshotMixTests` extended to presentation changes. Mutations (all caught): position
    before rotation → 2 failures, fit before crop → 2, rotation around the canvas origin → 11, scale
    not around the centre → 7, no culling → 3, occluder ignoring opacity → 2, image as occluder → 1,
    double-rounded coverage → 1, rotation sign flipped → 7, opacity-0 layers kept → 2. (Scale and
    rotation commute for uniform scale, so their mutual order is not observable.)
  - Coverage review (product owner): for arbitrary angles `CoversCanvas` never uses the bounding
    box — it inverts the layer matrix, maps all four canvas corners into the crop rectangle and
    requires them inside by a 10⁻⁶ margin (doubt → false). Added: 45° picture whose AABB covers the
    canvas but whose corners don't → false and the layer below stays in `LayersAt`; 30° × 2 with all
    corners inside → true and culls; conservative edge at 45°. Mutation "AABB instead of inverse
    containment" → 6 failures (incl. both new regression tests). Core tests: 243.
  - Flaky tests found before the checkpoint commit (full parallel runs, ~1 in 8): `MixUpdatePlayback
    Tests.VolumeAndMuteWhilePaused…` ("video reader reopened") and the Phase 5 `AudioPipelineTests.
    SnapshotUpdate_ReusesReaders…`. Cause: decoder-open counts depended on background timing — a
    reader of a pipeline retired right after creation still called `OpenAsync` from its task (with a
    cancelled token), and a new reader's open could land before or after the assertion.
    Product fix (pre-existing since Phase 5): `SpanReader` / `AudioSpanReader` check cancellation
    right before opening, so a reader retired before its task ran no longer starts an ffmpeg process
    only to kill it. Test fixes: `PlaybackService.RetiringSettledAsync()` (internal; Timeline internals
    now visible to UI.Tests) — count-based tests capture only after every retired pipeline is
    disposed; `AudioPipelineTests` waits for the opens it expects. Verified: 10 consecutive full-suite
    runs green (824 tests each).
  - Open for Step 6: rotation metadata of phone videos (ffprobe gives the coded size; D018
    consequences); how placeholders (offline/unsupported/decode error) are drawn in a composited
    preview.

- Step 6 (in progress) — product decisions (2026-09-23): orientation + per-layer playback state in
  Step 6, multi-layer rendering stays Step 7; `PlaybackFrame` keeps a compatibility picture for the
  current Preview and adds the full `LayerPicture` list; stale metadata is re-probed silently on Open
  (not dirty, missing files stay offline); Resolution shows the display size; placeholders take the
  clip's geometry (fallback: whole canvas) and never cull; a layer uncovered without a seek is
  Pending (no new seek generation, no Buffering); no reader reuse across structural changes; no
  decoder limit (measure); rotation 0/90/180/270 only, odd/mirrored → log + decoder-size fallback;
  SAR out of scope.
  - 6a done — orientation/display size: `MediaMetadata.DisplayRotation` (clockwise, null = not a
    right angle) + `DisplayWidth/DisplayHeight` (size of the frames the decoder delivers), `Width/Height`
    stay coded; `NeedsDisplaySizeProbe`. Probe (`Video/DisplayOrientation`): stream display matrix or
    legacy `rotate` tag → else first-frame display matrix (EXIF JPEGs) → else 0°, interpreted exactly
    like ffmpeg's autorotate (measured with ffmpeg 9: ±90 → transposed; 180 same size; odd angle →
    coded size; mirror detected by the matrix determinant, hflip alone reads −180). Decoder passes
    `-autorotate` explicitly. `project.json` v1: optional `displayRotation/displayWidth/displayHeight`;
    older metadata is kept (Duration etc. stay usable, missing files stay offline) and re-probed in the
    background by `MediaAnalysisCoordinator` (status stays Completed, not dirty, failure keeps the saved
    metadata); inconsistent values drop the metadata (D014). Builder: `SourceSize` = display size
    (null when unknown → no geometry, never culls); `AssetState` includes it. Inspector / Media Browser
    show the display size (+ "Rotated 90°"). Found + fixed: with an odd display angle ffmpeg prints a
    warning without a newline, gluing showinfo's time-base line onto it — `ShowInfoParser` no longer
    requires its prefix at the line start (such files decoded as DecodeError before).
    Tests: Video `DisplayOrientationTests` (rule: 26 cases; real ffmpeg: 9 files incl. EXIF JPEG —
    probed display size == decoded frame size), 2 `ShowInfoParserTests`; Project
    `MediaOrientationPersistenceTests` (10); Core 3 builder/asset-state tests (+ fixture display sizes);
    UI `MediaOrientationRefreshTests` (6). Full suite 876 green.
  - 6b/6c done (D019) — playback contract + multi-layer `VideoPipeline`: `LayerPicture` /
    `LayerPictureState` (Frame, Text, Pending, Offline, Unsupported, DecodeError; `IsCurrent` per layer;
    `PlaceholderArea(canvas)` = clip geometry or whole canvas), `PlaybackFrame.Layers` (bottom to top) +
    `Canvas`, compatibility `Picture` = topmost picture layer (the Preview is unchanged). Pipeline:
    readers for every decodable layer of `LayersAt` (+ prefetch at the next edge), keyed by clip,
    closed when no longer needed; per-layer late frames; runtime failures become placeholders and stop
    occluding (`LayersAt(time, mayOcclude)`); Ready = every layer at the start frame;
    `UpdatePresentation(snapshot)` from `PlaybackService` for presentation-only snapshots (same
    generation, no buffering, `_pictureSnapshotVersion` follows).
  - 6d done — tests: Timeline `MultiLayerPlaybackTests` (15: order, culling without reader, text,
    opacity 1 → 0.9 while playing with a gated decoder = Pending then Frame without new generation/
    buffering, back to 1 closes reader + stream, 20 toggles without leaks, per-layer late frame via
    the fake's new `HoldAfter`, seek ready for either slow layer, offline/unsupported placeholders
    with geometry that never cull, unknown size → whole canvas, failing occluder uncovers the layers
    below, vanished file → Offline, prefetch, dispose); Video `MultiLayerIntegrationTests` (real
    ffmpeg: one process per visible layer, opened/closed with opacity, none after Dispose). All
    Phase 5 and Step 4/5 tests unchanged and green. Mutations (9): UpdatePresentation ignored → 5
    failures, presentation change resyncing → 4, failed layer still occluding → 2, readers never
    closed → 1, no per-layer late frame → 1, Ready on the first layer only → 1 (after making the seek
    test a theory over both layers — the first version missed it), compatibility picture from the
    bottom → 5, placeholder always full canvas → 1, no prefetch → 2.
  - Load (temporary measurement, not in the repo): 1/2/4 layers of 1080p30 H.264, hardware and
    software decoding — 0 % late frames over 4 s, one ffmpeg process per layer; no decoder limit needed
    so far (rendering in Step 7 and 4K sources not measured).
  - Found: `SeekAsync` without `Update()` calls never completes when a source's preroll exceeds
    `BufferFrames` (e.g. MPEG-TS at frame 40) — unchanged since Phase 5, the Preview always ticks;
    documented in D019, not changed.
  - Full suite: 892 tests, 5 consecutive runs green. App starts (shell initialized, no errors).

- Step 6 checkpoint: commit `8592d10`.
- Step 7 (D020) — multi-layer Preview + visual Inspector:
  - 7a: `PreviewViewModel.Layers` / `Canvas` / `AreLayersCurrent`; keeps polling while a layer is
    pending or late (a layer uncovered while paused appears), keeps the previous layers while
    buffering, clears them on project change.
  - 7b/7c: `UI/Rendering`: `CompositionDrawPlan` (pure: viewport canvas → control, per-layer
    operations bottom to top, decoded-pixel source rect, geometry from the decoded size when unknown,
    placeholders in `PlaceholderArea`, text, pending skipped), `RenderConversions` (Affine2D → Avalonia
    Matrix), `CompositionView` (DrawingContext; clip to canvas; two WriteableBitmaps per layer).
    `PreviewView` hosts it (real canvas proportions, no fixed 960 × 540). Visually checked offscreen
    (scratch harness, not in the repo): transforms, opacity, crop, text, placeholder, clipping,
    vertical canvas.
  - 7d: Inspector Transform (Position X/Y, Scale %, Rotation, Opacity %) and Crop (L/T/R/B %, not for
    text), per-field edits with merge, sync guard, rejection.
  - UI compatibility path removed (view-model `CurrentFrame` / `PictureKind` / `PlaceholderText` /
    `IsPictureCurrent`, the view's bitmap copy); `PlaybackFrame.Picture` stays in Core (test oracle).
  - Tests: UI `PreviewLayersTests` (5), `CompositionDrawPlanTests` (21), `InspectorVisualTests` (7:
    kinds, exact values per field, merged steps + undo/redo refresh without edits, 1/3-precision
    no-echo, crop rejection, locked track, and opacity/scale/rotation edits while playing — same
    VideoPipeline and seek generation, no buffering, lower layer Pending → Frame, reader closed
    again); `PlaybackUiIntegrationTests` / `InspectorAudioTests` moved to the layers.
    Mutations: no polling while a layer is pending → 2 failures (the condition sits in two places;
    removing one alone is not observable — redundant by design); no visual sync guard → 1.
  - Performance (scratch measurement): copy/update 0.11–1.31 ms, offscreen render 0.8–13.9 ms per frame
    for 1–8 layers at 1280 × 720 — no optimization needed.
  - Full suite 925 green (3 consecutive runs); app starts without errors.
  - Open: manual visual check by the product owner. `PlaybackFrame.Picture` stays in Core — product
    owner decision 2026-09-24: deferred cleanup, not part of Phase 7 (≈ 47 assertions of the Phase 5–7
    playback tests use it as their oracle).
- Step 7 manual-check findings (2026-09-24; Step 7 checkpoint: commit `a1cf682`):
  - Numeric fields (all 10 Inspector fields) accepted spaces and foreign characters: NumericUpDown
    parses with `NumberStyles.Any` by default (ru-RU group separator is a space → "9 0" = 90; also
    "(5)", "5-", "1e2", currency). Fixed in one place: `UI/Common/NumericInput.ParsingStyle`
    (leading sign + decimal point only), applied to every NumericUpDown by a style in
    `InspectorView`. Invalid text keeps the last value (model untouched) and the field shows it
    again on blur. Found while checking: an emptied field made the value null, the `decimal`
    binding failed and the Inspector showed an InvalidCastException text (pre-existing since
    Step 4 for Volume). The 10 view-model fields are now `decimal?`: null is never an edit; on blur
    the view calls `InspectorViewModel.ShowModelValues()` (the existing `SyncFromModel`).
    Known, unchanged: fields commit per keystroke (existing behaviour), so a valid prefix typed
    before an invalid character ("9" of "9 0") is applied; the rest is rejected.
  - Clip edge resize not updating the timeline width: **closed as not reproduced** (product owner,
    2026-09-24): observed once during Step 7, not reproduced by the later checks below, no exact
    reproduction steps; no fix. VM geometry is correct
    (drag preview and committed Left/Width, start and end edge, after property edits, undo/redo),
    and in the running app nine scenarios resized correctly (end/start edge, after Inspector spinner
    and typed edits with focus kept, two tracks, zoom Fit, during playback, undo). No timeline code
    changed since Phase 7 Step 1.
  - Tests: UI `NumericInputTests` (25 incl. theory cases: plain numbers ru/en, 16 rejected inputs,
    the old default documented, emptied field → no edit + restore on blur), `TimelineTrimLayoutTests`
    (4). Mutations: `ParsingStyle = Any` → 11 failures; no relayout on TimelineChanged → 4.
  - Full suite 954 green (3 consecutive runs); app started, all 10 fields checked in the running
    app (UI Automation read-back): empty/"abc"/" 50" → model value, "9 0" → 9, "-4 5" → −4,
    "1e2" → 1, "5-" → 5, "1 000" → 1, "-12,5" and "150" accepted.

- Step 8 checkpoint: commit `5d81fe5`.
- Step 8 (D021) — text clips:
  - `ITimelineEditService.AddTextClip(start)`: topmost video track, frame-grid start, 5 s, defaults
    "Text" / Segoe UI / 48 / #FFFFFF / Center, one "Add Text" undo step; rejected without changes on
    overlap, locked track or no video track (no track is created). `EditPlan` passes its description
    to an insert-only command (the add used to be named "Add Clip").
  - "+ Text" in the timeline header (playhead, selects the new clip).
  - Inspector TEXT section: multiline text, font (installed fonts, `IFontCatalog` /
    `AvaloniaFontCatalog`; a missing font is listed first), size (`NumericInput`), `#RRGGBB` color +
    swatch (applied only when complete and valid per D017 — `ClipPropertyValidator.IsHexColor` made
    public), alignment; live, merged per field, sync guard, rejection → status + model value; the
    blur handler now restores any Inspector text field that holds a value it doesn't apply.
  - Timeline label: first line / `(empty text)`, recomputed on every refresh; `Name` is observable.
  - Tests: Timeline `TextClipEditTests` (10: defaults and placement, topmost track, 29.97 grid,
    negative start, one undo step + dirty, overlap/locked/no-track rejections, frame rate not locked,
    trim/move/split of text), UI `TextClipUiTests` (23: "+ Text" selection/Inspector/Preview layer/
    rejection/undo-redo; TEXT fields, per-field edits, merging, no echo edits, color while typing and on
    blur, rejected/empty values, whitespace text draws no layer, font list incl. a missing font, text
    edits while playing keep pipeline and seek generation; label rules and label through edit/undo/redo
    and split). Mutations: no label refresh → 2 failures; no text sync guard → 2; color applied
    unchecked → 1; not the topmost track → 6 (UI 4, Timeline 2).
  - Full suite 987 green (3 consecutive runs). Running app checked: "+ Text" on V2 at the playhead
    with the Inspector TEXT section and the Preview text; multiline text, size, color + swatch,
    alignment, font from the system list (MV Boli rendered); undo back to the added clip and redo
    through all five text edits, with fields, Preview and label following ("Text" ↔ "Hello"); incomplete color + Tab → model color; whitespace text →
    `(empty text)` and no Preview text.
  - Known risk (Phase 8, not solved here): a font missing on the machine silently falls back in the
    Preview, but ffmpeg `drawtext` in the export needs a font file.

- Step 9 checkpoint: commit `48a3f54` (together with the `ExecutableLocator` fix below).
- Step 9 (D022) — speed:
  - Product decisions (2026-09-24): 0.25×–4× in steps of 0.05×, exact fraction; a speed change keeps
    start, SourceIn and SourceOut, the duration is whole frames; one rounding rule for SetSpeed / trim
    / split / regrid / validation / loading; video and audio clips only; v1 files with speed ≠ 1 are
    damaged, saving writes v2; pitch kept via ffmpeg atempo; 1× keeps its exact path; a 1× clip may
    keep a source tail < 1 frame after a speed change back to 1× (owner's choice); no speed label, no
    high-speed decoding optimization.
  - Scratchpad measurement, FFmpeg 9.0.1 (MP4/AAC, 1 kHz bursts, energy centroids): PTS after atempo
    count output samples (source position must come from ashowinfo before atempo); pitch exact at
    0.25/0.5/2/4×; constant latency 449–483 source samples (one atempo), 542 (0.5·0.5 chain), 1573
    (2·2 chain → 4× uses one atempo=4); ≤ 3.4–5.5 ms residual after removing it, no drift; without apad
    the output ends 16–180 ms early, `apad=pad_dur=0.25` delivers the source to the end of the file.
  - Core: `ClipSpeed` (k/20, default = 1×), `SpeedTiming` (SourceLength/FramesFor/Fits); speed-aware
    `SourceFrameSelector.SamplePoint`, `AudioTiming.SourceTimeAt` / `TimelineSampleOfStreamStart`;
    `PictureSpan`/`AudioSpan.Speed`; the "only 1× can be played" status is gone.
  - Timeline: `SetClipSpeed` + `SetClipSpeedCommand` (mergeable); `ClipState.Speed`,
    `ClipState.Normalize`; speed paths in move/trim/split (`PlanTrimAtSpeed`) and `FrameRateRegrid`;
    validator uses the invariant for every speed; the D008 edit ban is lifted. Reader: tempo stream
    placement; `AudioDecodeRequest.Speed`.
  - Video: FFmpeg audio decoder `apad` + atempo chain, latency compensation in FirstSampleIndex
    (480 source samples, 540 below 0.5×).
  - Project: format v2 (`speedRatio`), v1 read with speed exactly 1.
  - UI: Inspector SPEED section (0.25–4.00×, step 0.05).
  - Tests (new): Core `ClipSpeedTests` (23) and `SpeedMappingTests` (6, BigInteger references);
    Timeline `SpeedEditTests` (27: worked examples, boundary speeds 0.25/0.5/0.95/1/1.05/2/4, round trips
    1→1.35→1 and 1→1.35→0.75→1, undo/redo with a non-multiple duration, merging, rejections, trim/move/
    split/regrid at speed, the two real one-tick normalization cases at 29.97 found by a search, 1× with
    a tail, randomized edits with speeds at 29.97/23.976/25) and `SpeedPlaybackTests` (12); Project
    `SpeedPersistenceTests` (22); Video `FfmpegSpeedIntegrationTests` (21, real ffmpeg); UI
    `SpeedInspectorTests` (8). Changed by decision: 4 tests that used speed 2 as "unsupported" (now a
    video clip on audio-only media), the D008 ban test (removed), 3 format-version expectations (v2),
    the v2 "+1 tick at 1×" corruption case (+1 frame now), the recovery "newer version" test's literal.
  - Mutations: speed change rewriting SourceOut → 10 failures; no normalization → 2; audio reader or
    frame selector ignoring the speed → 6 each; no latency compensation → 3 (the slow speeds; the 10 ms
    bound itself guards the fast ones); no apad → 10.
  - Full suite 1105 green. Running app: v1 with speed 2 refused as damaged; valid v1 opened; Speed
    field 2×/0.5×/0.25×/4× — clip lengths 5 / 20 / 40 / 2.48 s and the preview's burned-in source
    time 8.08 / 2.00 / 1.00 / 8.00 s at 4.04 / 4 / 4 / 2 s; playing at 2× ran ffmpeg with
    `…,ashowinfo,apad=pad_dur=0.25,atempo=2`; trim end, split, move of the 2× clip (source 6.000 s at
    5.00 s); undo ×3 / redo ×3; saved as v2 (`speedRatio` 2/1, no `speed`).
  - Found while testing: `ExecutableLocator` passed the first caller's token into the one-time ffmpeg
    probe; if that caller was cancelled (a reader retired right after opening a project) the probe
    failed and "ffmpeg not found" was cached for the whole run (on `5d81fe5` in 4 of 4 open + seek
    runs). Fixed in `48a3f54`: a caller-cancelled probe rethrows without caching; a genuine miss is
    still cached (`Video.Tests/ExecutableLocatorTests`, 5). Final Step 9 app check: 5 open + seek +
    play runs, ffmpeg found every time.

- Step 10 — closeout (2026-09-24, no product code changed):
  - Audit: the Phase 7 scope of `docs/DEVELOPMENT_PLAN.md` (speed, volume, opacity, transform, crop,
    text) and the product decisions above are implemented; D017–D022 match the code; ARCHITECTURE,
    ROADMAP and README brought up to date.

    | Property | Step | Automated tests | Manual check |
    |---|---|---|---|
    | Volume 0–200 %, mute (video + audio clips) | 2–4 | Timeline `ClipPropertyEditTests`, Core `PlaybackSnapshotMixTests`, Timeline `MixUpdatePlaybackTests`, UI `InspectorAudioTests`, Video `MixUpdateIntegrationTests`, Project `ClipPropertyPersistenceTests` | Step 4; Step 10 smoke (values; mute is not audible to automation) |
    | Opacity, transform (position, scale, rotation) | 2, 5–7 | Core `CompositionMathTests`, `CompositionLayersTests`, Timeline `MultiLayerPlaybackTests`, UI `CompositionDrawPlanTests`, `InspectorVisualTests`, `NumericInputTests`, Project `ClipPropertyPersistenceTests` | Step 7 (+ owner findings); Step 10 smoke |
    | Crop | 2, 5–7 | as transform, plus the crop cases of `ClipPropertyPersistenceTests` (load validation) | Step 7; Step 10 smoke |
    | Text clips | 8 | Timeline `TextClipEditTests`, UI `TextClipUiTests`, `CompositionDrawPlanTests` | Step 8 |
    | Speed 0.25–4× | 9 | Core `ClipSpeedTests`, `SpeedMappingTests`, Timeline `SpeedEditTests`, `SpeedPlaybackTests`, Project `SpeedPersistenceTests`, UI `SpeedInspectorTests`, Video `FfmpegSpeedIntegrationTests` | Step 9; Step 10 smoke |
    | Multi-layer playback / Preview | 6–7 | Timeline `MultiLayerPlaybackTests`, UI `PreviewLayersTests`, Video `MultiLayerIntegrationTests` | Steps 6–7 |
    | project.json v2, v1 read | 3, 9 | Project `ClipPropertyPersistenceTests`, `SpeedPersistenceTests`, `ProjectSerializer*Tests`, `RecoverySerializerTests` | Step 9; Step 10 smoke |

  - Coverage gap found by the audit: no automated test put speed ≠ 1 and the other properties on one
    clip (persistence maps them independently, split copies both through one `CloneClip`, speed
    commands touch only `ClipState`). Closed on the product owner's decision by one test:
    `SpeedPersistenceTests.A_clip_at_another_speed_keeps_every_phase7_property_in_v2` (speed 27/20,
    volume 1.5, mute, position, scale, rotation, opacity, crop → v2 with `speedRatio` 27/20 → load →
    every value exact → byte-identical re-serialization).
  - Automated: `dotnet build --no-incremental` 0 errors, 0 warnings; full suite 3 consecutive runs,
    1111 passed each (Core 275, Timeline 257, Project 249, UI 193, Video 137), 0 failed, 0 skipped.
  - Smoke (running app, scratch project, UI Automation): one video clip with speed 2×, volume 150 %,
    muted, position (200, −100), scale 60 %, rotation 15°, opacity 70 %, crop 10/5/10/5 %. Open → frame
    steps + seek → the decoder's source positions advance 0.08 s per 0.04 s timeline frame (2×); Play
    shows the rotated, scaled, offset, cropped, translucent picture; the Inspector shows every value;
    opacity edited to 80 % in the Inspector (title `*`) → Save → `project.json` v2 with `speedRatio`
    2/1, no `speed`, all values incl. the edit; reopen → same values, clean title, 2× mapping and
    playback again. Mute itself is not audible to automation (covered by
    `MixUpdateIntegrationTests`). Closing the app hung in this smoke even without playback — see the
    known issue below (not a Phase 7 regression).
  - Carried forward / deferred (not Phase 7):
    - Phase 8 constraints: the export must reproduce the composition rules of D018 exactly (crop → fit
      → scale → rotation → position → opacity; canvas = project frame size); a font missing on the
      machine silently falls back in the Preview, but ffmpeg `drawtext` needs a font file (D021); the
      speed rules of D022 (exact mapping, atempo with pitch kept) apply to the export as well.
    - `PlaybackFrame.Picture` cleanup (deferred, see Step 7).
    - The known issues below, in particular the close hang.

### Phase 6 — Project persistence (complete)

Branch `feat/phase-6-project-persistence` (from `acc1a49`), Phase 6 commit, merged into `main`.
Decisions: DECISIONS.md D014–D016.

Product decisions (2026-09-23): project = folder with `project.json` + `cache/` (no
`.aveproj`); autosave writes a separate recovery file every 2 min and never overwrites
`project.json`; save point is part of Phase 6; saved ffprobe metadata is reused on Open;
failed Open leaves the current project untouched; atomic save; missing media opens as
offline. Out of scope: relink, recent projects, copying media into the project.

- Step 1 done — format/serializer: `Project/Persistence/ProjectFileDto.cs` (format v1, DTOs
  separate from entities, `MediaTime` as long ticks, `FrameRate` as {num, den}, no runtime
  state), `ProjectSerializer` (validating load: ids, references, clip kind/track, frame grid,
  overlaps; absolute + relative media path), `ProjectFileStore` (atomic temp + `File.Replace`),
  `Core/Interfaces/ProjectFileException`. Tests: `tests/Project.Tests` (new project).
- Step 2 done — `ProjectService` Open/Save/SaveAs; save point in `UndoRedoService`
  (`CurrentPosition`, `MarkSavePoint`, `IsAtSavePoint`); dirty = history not at save point or
  a media import since the save; `SaveStateChanged` event. Open replaces the project only after
  full load + validation.
- Step 3 done — missing media: marked on the loaded project before it replaces the current one
  (first playback snapshot already Offline); not dirty, not saved. `ProjectFileWorkflow` (UI):
  open → analyse only present media without saved metadata → status message with the missing
  count. `MediaAnalysisCoordinator` never probes missing files. Media Browser row shows
  "Media offline".
- Step 4 done — autosave/recovery: `AutosaveService` (Project, implements the Core
  `IAutosaveService`, every 2 min, snapshot on the UI thread, atomic write) into
  `%LOCALAPPDATA%\AiVideoEditor\recovery\<projectId>.json` via `RecoveryStore` (one app-wide
  folder so startup can find recovery without recent projects; never `project.json`). Recovery
  file = project DTO + original folder, time, writer process. Obsolete (deleted) after a
  successful Save (`IProjectService.ProjectSaved`), when this session finds the project clean,
  on Discard, and at a clean shutdown; a per-project generation stops an autosave snapshotted
  before a Save from rewriting it. Startup (`ProjectFileWorkflow.StartSessionAsync`, on window
  Opened): scan → damaged files renamed `*.damaged`, files older than project.json removed,
  files of a running instance ignored → Recover / Discard / Not now dialog (`IDialogService`,
  `AvaloniaDialogService`) → `IProjectService.RestoreRecoveryAsync` (same validation as Open;
  project dirty, original folder) → autosave starts. Closing (`MainWindow.OnClosing` →
  `PrepareToCloseAsync`): a dirty project keeps a final recovery file, a clean one leaves none.
  (Step 5 then put the unsaved-changes prompt in front of this.) `AppPaths.AutosaveFile`
  (project cache) replaced by `AppPaths.RecoveryFolder`.
- Step 5 done — UI workflow: `ProjectFileWorkflow` New / Open (folder picker) / Save (Save As
  if never saved) / Save As (folder picker; confirms replacing another project's
  project.json) / Close, all behind one "save changes?" prompt (Save / Don't Save / Cancel via
  `IDialogService`; a Save that doesn't happen counts as Cancel; edits made during that save →
  asked again). "Don't Save" removes the discarded project's recovery file only after New/Open
  succeeded (a failed Open keeps project, changes and recovery); on Close it is
  `IAutosaveService.ShutdownAsync(keepUnsavedChanges: false)`. `IAutosaveService`:
  `DiscardCurrentRecoveryAsync()` → `DiscardRecoveryAsync(Guid)`. Toolbar commands are async
  (no re-entry), Save As button added; window title "Name[*] — AI Video Editor" follows
  `SaveStateChanged`; shortcuts Ctrl+N / Ctrl+O / Ctrl+S / Ctrl+Shift+S (like the other
  shortcuts, not while a text box has focus). `IFilePickerService.PickFolderAsync` added.
- Step 6 done — docs: DECISIONS D014 (format, Open/Save, missing media), D015 (save point,
  open question below), D016 (autosave, recovery, unsaved changes); ARCHITECTURE (projects
  table, "Project persistence" section, stale Phase 3–5 statements), ROADMAP,
  docs/DEVELOPMENT_PLAN.md (Phases 4–6 checked), README status.

Verification (closeout):
- Automated: 618 tests passed (Project 168, Core 162, Timeline 132, Video 76, UI 80);
  `dotnet build` 0 errors, 0 warnings.
- UI smoke test (UI Automation script, not in the repo): leftover recovery → dialog → Recover
  → title "Smoke Film* — …" → close → prompt → Cancel keeps the window → close → Don't Save →
  exit 0, recovery file removed.
- Manual UI check by the product owner (Step 5): passed — incl. the native folder pickers and
  the keyboard shortcuts.

Deferred / out of scope: relink of missing media, recent projects, copying media into the
project, re-checking missing media while the project is open, offering more than one
recovery file per start (older ones are offered at later starts).

Open questions carried forward from Phase 6:
- ~~Playhead position, zoom and snapping: project or session state?~~ Decided in Phase 7
  Step 1: session state (D015).
- Missing state is detected once on Open; a file that reappears later stays offline until the
  project is reopened (no relink in Phase 6).

### Phase 5 — Preview/playback (complete)

Branch `feat/phase-5-playback`. Decisions: DECISIONS.md D009–D013. Video playback (checkpoint
`85ca216`) and audio playback (Phase 5 closeout commit) are implemented, covered by automated
tests and manually validated by the product owner.

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
- Deferred (explicitly): volume/mute UI; audio device unplug / default-device change handling;
  limiter, crossfade/declick; J/K/L, loop, timeline autoscroll, scrub
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

Audio (implemented, manually verified by listening, accepted) — architecture approved (audio device = master clock,
48 kHz stereo float32, AudioPipeline/AudioSpanReader/AudioMixer, silence on underrun and
errors, reader reuse across snapshot updates). D013 to be written once implementation/tests
confirm it.
- Step A1 done: WASAPI clock spike (scratch console app, not in the repo; NAudio.Wasapi 2.2.1 —
  3.x requires net9.0) on a Sound BlasterX G6, shared mode, mix format 48 kHz / 8 ch float:
  - `WasapiOut.GetPosition()` = bytes of `OutputWaveFormat` actually played (IAudioClock),
    not queued: written − position ≈ 130–140 ms with latency 100; rate ≈ 48 000 frames/s.
  - `OutputWaveFormat` equals the requested format (48k/2ch float; also 44.1k): WASAPI
    converts to the 8-ch mix itself; position units follow the requested format.
  - Monotonic while playing; **0 after Stop and restarts from 0** → the output must keep a
    cumulative base (read the position right before Stop). First non-zero position 30–65 ms
    after Play (start-up latency).
  - **`Pause()` only stops feeding**: the position keeps rising until the queued audio has
    played → never use Pause; pause = Stop (flush).
  - COM objects are apartment-bound: an `MMDevice` created on an MTA thread fails on an STA
    thread (E_NOINTERFACE). Creating device + WasapiOut on the STA (UI-like) thread works;
    `GetPosition()` then also works from MTA and thread-pool threads.
- Step A2 done: Core contracts `AudioFormat`, `IAudioSampleSource`, `IAudioOutput`,
  `IAudioDecoder`, `AudioDecodeRequest`, `IAudioSampleStream`, `AudioDecodeException`
  (Core/Playback/AudioContracts.cs) and exact sample math `AudioTiming`
  (clip samples `[ceil(S·fs), ceil(E·fs))`, per-clip source offset rounded once);
  `Core.Tests/AudioTimingTests.cs`.
- Step A3 done (awaiting review; D013): `AudioSpanReader`, `AudioMixer`, `AudioPipeline`
  (Timeline/Playback); `PlaybackService` drives the device (Play/Pause/Seek/UpdateSnapshot,
  device failure → Stopwatch, `IsAudioAvailable`); `FfmpegAudioDecoder` + shared
  `FfmpegProcess` (Video; the video stream now uses `FfmpegProcess` too, behaviour unchanged);
  `WasapiAudioOutput` (Audio, NAudio.Wasapi 2.2.1); DI registrations; one UI status message
  when playing without sound. Public `SetMasterClock` removed (the service owns the master).
- Findings: remuxing AAC from MP4 to MPEG-TS keeps the 1024-sample encoder priming at the
  container start (the MP4 edit list skipped it), so such a TS really plays 1024 samples later —
  our decoder matches ffmpeg's own plain decode there. A 1 ms preroll on AAC/MP4 needed 3 seek
  attempts; the default 200 ms needed 1.
- Tests: `Timeline.Tests/Playback/AudioPipelineTests.cs` (7), `AudioPlaybackServiceTests.cs` (8),
  fakes in `AudioFakes.cs`; `Video.Tests/FfmpegAudioDecoderIntegrationTests.cs` (10, incl. an
  A/V sync test: click at 2.02 s heard while video frame 50 is shown) and
  `WasapiAudioOutputDeviceTests.cs` (real device, STA thread; passes with a note if no device).
- Manual listening test in the real app by the product owner (2026-09-23): audio plays
  correctly. Not verified yet: device unplug during playback, default-device change while
  running (not handled: the output stays on the device it opened).
- Step A4 done (lifecycle hardening): superseded pipelines and readers retired by
  `VideoPipeline.Retain` / `AudioPipeline.Maintain` were disposed fire-and-forget; they are now
  tracked, and `PlaybackService.DisposeAsync` awaits all of them — after Dispose no reader,
  stream, background task or ffmpeg process is left (deterministic, not just eventual).
  New tests: Play→Pause→Play cycles (continuous audio, monotonic position, no reader pile-up),
  seeks backward/forward while playing, 20 snapshot updates while playing (continuity, reuse,
  no leaks), end of timeline releases readers, Dispose with pending opens and slow-closing
  streams; real device: clock monotonic over 6 Start/Stop sessions; real ffmpeg: full playback
  lifecycle leaves no ffmpeg process after Dispose.

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
- Phase 5 (video checkpoint `85ca216`, audio in the closeout commit)
- Phase 6
- Phase 7 (accepted 2026-09-24)
- Phase 8 (accepted 2026-09-25)

## Known issues

- Export codec leg (D023 Step 8, decision L1-c): MP4 → export canvas has no numeric tolerance; the Step 8.6
  measurement is data for a future product decision, not a criterion. Open.
- `Project.Tests` hang seen once in Step 8.4 (1 of 23 runs, test not identified): not reproduced — the 8.4/8.5 runs
  and the three final `--blame-hang` runs of the closeout were clean. Watch for it; no fix.

- Speed (D022): the atempo latency compensation is measured for FFmpeg 9.0.1; another ffmpeg version
  may shift it — `FfmpegSpeedIntegrationTests` (10 ms bound) catches that.
- ~~`ExecutableLocator`: a cancelled first ffmpeg probe caches "not found" for the app run~~ — fixed
  2026-09-24: a caller-cancelled probe now rethrows `OperationCanceledException` without caching
  (and kills the probe process); only a genuine miss/failure/5 s timeout is cached. Regression tests:
  `Video.Tests/ExecutableLocatorTests`.
- Closing the main window hangs the process (window gone, no "Shutting down." in the log; host
  disposal never finishes) once a project with decodable media was open — also without ever
  playing, and also after Pause/Stop. Closing an app without such a project exits normally. Found
  2026-09-24; reproduced with only the `ExecutableLocator` fix applied on `main` `9fd38e7` (before
  Phase 7) and on every Phase 7 checkpoint (`7ab3693`, `8592d10`, `a1cf682`, `5d81fe5`), so it is not
  a Phase 7 regression; earlier it was masked because the locator bug often left the preview without
  ffmpeg. Not fixed yet (separate task).

- Text clips (D021): the Preview (Avalonia) silently substitutes a font that isn't installed. Phase 8
  (D023) renders text like the Preview (no `drawtext`), so the export falls back the same way; the
  preflight warns about it. Embedding fonts in a project is out of scope.

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

2026-09-25 (Phase 8 closeout, Step 8.7; docs only, no code change):
- Working tree before the closeout identical to the end of Step 8.5 (hashes of every changed file); leftover MSBuild
  nodes from the 8.5 mutation runs stopped with `dotnet build-server shutdown`.
- `dotnet build AiVideoEditor.sln --no-incremental`: 0 errors, 0 warnings.
- `dotnet test`: 1 plain run + 3 consecutive runs with `--blame-hang --blame-hang-timeout 5m`, each 1494 passed,
  2 skipped (the 4K scenes), 0 failed — Core 380, Timeline 257, Project 259, UI 216, Export 78, Rendering 52,
  Video 185, ExportEndToEnd 67 (+2 skipped); no hang, no dump. Heavy 4K scenes once with `AIVE_HEAVY_TESTS=1`: 2/2.

2026-09-24 (Phase 7 closeout, Step 10):
- `dotnet build --no-incremental`: 0 errors, 0 warnings. `dotnet test`: 3 consecutive runs, 1111
  passed each (Core 275, Timeline 257, Project 249, UI 193, Video 137), 0 skipped.
- Running app: Phase 7 integration smoke passed (details in Step 10 above); close hang observed
  (known issue, pre-Phase 7).

2026-09-23 (Phase 5 audio, lifecycle):
- `dotnet build`: 0 errors, 0 warnings. `dotnet test`: 391 passed (Core 153, Timeline 132,
  UI 30, Video 76); audio tests repeated (Timeline 3×, Video 2×) without failures.
- Real device: 6 Start/Stop sessions sampled every ~2 ms — never backwards, frozen after Stop.
  Real ffmpeg lifecycle: 2 live processes while playing, 0 right after Dispose.
- Mutation: Dispose not awaiting retiring pipelines → the Dispose test fails (1 stream left).

2026-09-23 (Phase 5 audio):
- `dotnet build`: 0 errors, 0 warnings. `dotnet test`: 384 passed (Core 153, Timeline 127,
  UI 30, Video 74); audio tests repeated (Timeline 4×, Video 2×) without failures.
- Mutations: reader trusting the request instead of the real first sample → 5 failures;
  device clock not cumulative → device test fails; no reader reuse → 2 failures.
- App started in Development (DI incl. audio validated). No listening test by automation.

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
