# Project Progress

## Current phase

Phase 7 — Basic editing: **in progress**, branch `feat/phase-7-basic-editing` (from `main`
`9fd38e7`, which contains Phases 5 and 6). Scope (DEVELOPMENT_PLAN): speed, volume, opacity,
transform, crop, text — static per-clip properties, no keyframes.

### Phase 7 — Basic editing (in progress)

Product decisions (product owner, 2026-09-23):
- Compositing: the Preview draws layers on the GPU with Avalonia `DrawingContext`; D010's
  "topmost track wins" is dropped. Core owns the pure geometry (matrix, crop, opacity), the
  Preview only renders it. The rules must be reproducible by the Phase 8 export (→ D018).
- Canvas: `ProjectSettings.FrameWidth × FrameHeight` only (default 1920×1080), never taken from
  the first video; no resolution UI in Phase 7. The Preview must stop assuming a fixed 960×540.
- Speed: 0.25×–4×, UI step 0.05×, exact rational (not `double`), pitch preserved. Changing speed
  keeps Start, recomputes Duration / source range, is rejected on overlap (no ripple).
  `project.json` v2 that still reads v1 (→ D019).
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
