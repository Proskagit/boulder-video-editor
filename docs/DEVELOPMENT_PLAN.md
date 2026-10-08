# Development Plan

Iterative phases, in order. Do not start a phase before the previous one builds
and runs cleanly.

- [x] **Phase 0 — Architecture.** Solution, projects, DI, logging, base MVVM,
      basic window, Git-ready structure. *(`0ccc9be`)*
- [x] **Phase 1 — Basic UI.** Toolbar, Media Browser, Preview, Timeline, Inspector
      panels laid out with mock data. Visual skeleton only. *(`88a608b`)*
- [x] **Phase 2 — Project state and real media import.** In-memory project state
      (`IProjectService`), native file picker, extension-validated import with
      duplicate detection, Media Browser wired to real imported files, Inspector
      shows real file properties. *(`e7a8ef0`)*
- [x] **Phase 3 — Media analysis foundation.** FFprobe-backed `IMediaAnalysisService`
      (Video subsystem), configurable ffprobe location (`IFfprobeLocator`,
      Infrastructure), background analysis after import via
      `MediaAnalysisCoordinator` (never blocks the UI thread), real technical
      metadata (duration/resolution/fps/codecs/bitrate/sample rate/channels) in
      the Media Browser and Inspector. No playback, timeline editing, or
      thumbnails yet. *(`a8e5bac`)*
- [x] **Phase 4 — Timeline.** Tracks, clips, selection, move, trim, split,
      delete, playhead, zoom, snapping — all as undoable commands. *(`33c5c02`)*
- [x] **Phase 5 — Preview.** Wire timeline playhead to the preview player;
      synchronize play/pause. Video and audio playback. *(`85ca216`, `acc1a49`)*
- [x] **Phase 6 — Project persistence.** Open/Save/Save As, project.json,
      autosave, missing-media detection. Save point, crash recovery,
      unsaved-changes prompt. *(branch `feat/phase-6-project-persistence`)*
- [x] **Phase 7 — Basic editing.** Speed, volume, opacity, transform, crop, text.
      *(branch `feat/phase-7-basic-editing`)*
- [x] **Phase 8 — Export.** Timeline → MP4 (H.264/AAC): offline rendering of the Preview with the
      Core composition rules, FFmpeg as the encoder (D023). *(branch `feat/phase-8-export`, `8786491`)*
- [x] **Phase 9 — Quality.** Performance profiling, caching, error handling,
      polish, hotkeys, waveform, thumbnails. Scope, steps and acceptance criteria:
      section below and DECISIONS.md D024. *(branch `feat/phase-9-quality`, closeout `dcb86cb` / `f27a4ab`,
      PR #8 merged as `409240b`; accepted 2026-09-29)*
- [x] **Phase 10 — Transitions & basic effects.** Fade in / fade out of a clip (picture and its own sound) and a
      cross dissolve between adjacent clips of a video track, identical in the Preview and the export; `project.json`
      v3. Scope, steps and acceptance criteria: section below and DECISIONS.md D025.
      *(branch `feat/phase-10-transitions-effects`, last verified commit `ddf45df`, closeout `ee0527d`; accepted
      2026-10-01; PR #10 merged into `main` as `2e758f1`)*
- [x] **Phase 11 — Media relink & recent projects.** Re-checking media availability during the session, relink of
      missing media (one file, then a batch found next to it) as an undoable change, and a list of recent projects in
      the toolbar. Scope, steps and acceptance criteria: section below and DECISIONS.md D026 (product owner decisions
      PO-1…PO-9). *(branch `feat/phase-11-relink-recent-projects`, from `2e758f1`, closeout `ca20352`; accepted by the
      product owner 2026-10-05 on the local verification; PR #11 merged into `main` as `47ed2fa` (2026-10-05), CI green.
      Not run: optional export scenario 9 and the "without ffmpeg" check; export scenario 14 checked by decoding the 8
      outputs, not watched in a player; L1-c stays open, outside Phase 11)*
- [x] **Phase 12 — Editing essentials.** Track delete / reorder, removing media from the project, ripple delete and
      close gap, copy / paste / duplicate of clips, markers on the timeline, and the fix of New during a running
      import. Scope, steps and acceptance criteria: section below and DECISIONS.md D027 (product owner decisions,
      2026-10-05). *(branch `feat/phase-12-editing-essentials`, from `47ed2fa`; steps 12.2–12.8 `8e2b109`…`d467a84`,
      closeout `6761c1d`; accepted by the product owner on the Step 12.9 verification; PR #12 merged into `main` as
      `c0cb600` (2026-10-05), CI green. Open after it: the `F(end − start)` test helpers (a separate cleanup); L1-c
      moved into Phase 13)*
- [x] **Phase 13 — Project & export settings.** The canvas size and the frame rate as project settings (new and
      existing projects, undoable), export settings (quality, encoder speed, audio bitrate) on the existing MP4 /
      H.264 / AAC encoder, and the codec-leg criteria of L1-c. Scope, steps and acceptance criteria: section below and
      DECISIONS.md D028 (product owner decisions, 2026-10-06). *(branch `feat/phase-13-project-export-settings`, from
      `c0cb600`; Steps 13.2–13.9 `226c7f2`…`5c01aed`, all accepted; closeout `7d4f6d8`; accepted by the product owner on
      the Step 13.10 verification; PR #13 merged into `main` as `ed40b74` (2026-10-06), CI green on the pull request. The
      first CI run on `main` after the merge failed on the known flaky locator test (D028 §8) — fixed in Phase 14)*
- [x] **Phase 14 — Stabilization / technical debt.** A more deterministic CI and less technical debt without any change
      of the user functionality: the known flaky CI tests (autosave timer, ffprobe / PATH probe), the `F(end − start)`
      test helpers with a regression guard, and a decision on the empty `src/Effects` project. Scope, steps and
      acceptance criteria: section below and DECISIONS.md D029 (product owner decisions, 2026-10-06). *(branch
      `feat/phase-14-stabilization`, from `ed40b74`; Steps 14.2–14.7 `0a50fe5`…`90caae9`, all accepted, 14.6 out of scope;
      closeout 14.8 done 2026-10-07 — build 0 / 0, 2600 passed / 2 skipped, three blame-hang runs clean, heavy 2602 / 0 / 0,
      R1–R5 passed; accepted; PR #14 merged into `main` as `7200976` (2026-10-07), CI green on the first attempt on the
      pull request and on `main`; the D028 §8 policy closed)*
- [x] **Phase 15 — Editing tools.** Track controls (mute, hide, lock), trim to the playhead (plain and ripple), ripple
      trim by dragging an edge, slip, and a timeline In / Out range (unsaved session state) for loop playback and range
      export; a new text clip's font size relative to the canvas and the audio status after the sound returns. Scope,
      steps and acceptance criteria: section below and DECISIONS.md D030 (product owner decisions, 2026-10-07).
      *(branch `feat/phase-15-editing-tools`, from `7200976`; Steps 15.1–15.8 accepted (`d1296be` … `f9a2253`, review
      corrections `9de14c6`); closeout 15.9 done 2026-10-08 — build 0 / 0 Release and Debug, the full suite, heavy and three
      blame-hang runs green, R1–R9 run (R10's device part not run), the still-image decode fix found by R2; accepted;
      PR #15 merged into `main` as `a3793a4` (2026-10-08), CI green on the first attempt on the pull request and on
      `main`; the follow-up Preview fix PR #16 merged as `1bdeb95`)*
- [ ] **Phase 16 — Source viewer & three-point editing.** A Source mode of the Preview with a source In / Out (session
      state) and Insert / Overwrite of that range at the timeline playhead on the target track; picture and sound of a
      video file stay one clip (unlinking and other audio editing left to a later phase). Scope, steps and acceptance
      criteria: section below and DECISIONS.md D031 (product owner decisions, 2026-10-08). *(branch
      `feat/phase-16-source-viewer`, from `1bdeb95`; Steps 16.2–16.6 `392db82` … `22d2ef4`; closeout 16.7 done
      2026-10-08 — build 0 / 0 Release and Debug, passed 2893, skipped 2, failed 0, three blame-hang runs and heavy green, the manual plan and the
      Phase 15 regression in the real app, an independent review and its corrections; not yet merged — push / pull
      request on the product owner's command; checked after the product owner's acceptance)*

## Phase 9 — Quality: steps (D024)

Formalized in Step 9.2 (product owner, 2026-09-25). Steps run in this order; each step is accepted by the
product owner before the next one starts. Every acceptance item is labelled:

- **PR** — product requirement: behaviour the product owner accepts (automated tests and/or a manual check).
- **QG** — quality gate: a pass/fail condition on the build, the tests or the process; not user-visible behaviour.
- **M** — measurement only: recorded data with its method; no pass/fail threshold unless a later decision sets one.
- **Impl** — implementation constraint: binding on *how*, not a product behaviour. Details not listed here are
  routine engineering choices, recorded in `progress.md` (and as a D024 refinement when architectural).

Gates for every step 9.3–9.8 (QG): `dotnet build` 0 errors / 0 warnings; the full `dotnet test` green (only the
4K heavy scenes skipped); the Preview ↔ Export parity suite (`ExportEndToEnd.Tests`, `Rendering.Tests`,
`Export.Tests`) green with unchanged criteria and expected values — no test weakened or removed to make a change
pass; new behaviour covered by automated tests wherever it is testable, the rest listed in the Phase 9 manual
test plan (`docs/PHASE9_MANUAL_TEST_PLAN.md`, written during the steps); `progress.md` updated.

Constraints for the whole phase: D023 semantics unchanged; L1-c stays an open product decision (no numeric
tolerance for MP4 → export canvas, Step 8.6 results and D023 unchanged); out of scope: HDR / 10-bit, colour
management, export quality presets, bitrate policy, hardware encoding, hardware decoding as a required
optimization, a full audio editor, configurable hotkeys, a large UI redesign.

### 9.1 — Audit *(done)*
Scope, tests, build/CI, known issues and Phase 8 carry-overs audited; no change (report in `progress.md`).

### 9.2 — Scope formalization *(done)*
This section, D024, `progress.md`, `ROADMAP.md`. Documentation only.

### 9.3 — Stability & error handling *(done — sub-steps 9.3a–f; accepted 2026-09-25)*
Scope: the close hang; cancelling media analysis when the project is replaced; a concurrency limit for media
analysis; audio device removal / default-device change; the existing `ffmpeg-*.log` sink; the backup promised by
the damaged-project message.
- PR: closing the main window after a project with decodable media was open — idle, after playback, after
  Pause/Stop, after an export — ends the process normally ("Shutting down." logged, host disposed, no ffmpeg /
  ffprobe child process left). The root cause is identified and documented.
- PR: New Project cancels the previous project's queued and running analyses (Open and Recover the same way —
  they also replace the project); no old result is written into the new project; no probe process of it keeps
  running.
- PR: at most a fixed number of media analyses run at once; importing many files completes and the UI stays
  responsive.
- PR: removing the audio device during playback neither crashes nor hangs; playback continues on the Stopwatch
  clock without a position jump (D013). Default-device change: behaviour chosen by the product owner at the start
  of 9.3 (stay on the opened device, as now, or follow the new default) — chosen: follow the new default at every
  Play / restart after a seek, no switch during playback (D024 Step 9.3).
- PR: an ffmpeg / ffprobe failure leaves a diagnostic entry (arguments, exit code, end of stderr) in
  `ffmpeg-*.log` through the existing `Area=Ffmpeg` sink.
- PR: the damaged-project message promises nothing that does not exist: either a backup of the previous
  `project.json` is kept on save and the message says where, or the promise is removed. Backup if it is a small
  isolated change of the atomic save (D014), otherwise the text is corrected; the choice is recorded — chosen: no
  backup, the text corrected (D024 Step 9.3).
- QG: a regression test per item where automatable (the close path at least up to host disposal); the rest in the
  manual plan. Full suite with `--blame-hang` at the end of the step. The one-time `Project.Tests` hang (Phase 8)
  is a watched regression concern: a recurrence is reported, no workaround.
- Impl: the concurrency limit value is an implementation detail (documented); cancellation reaches the ffprobe
  process; D014 atomicity (temp + `File.Replace`) kept.
- Out of scope: relink / re-checking missing media, device selection UI, WASAPI exclusive mode, recovery-flow
  changes beyond the message, other log areas unless needed for the items above.
- Depends on: 9.1 only.

### 9.4 — Thumbnails + cache *(done — sub-steps 9.4a–e; accepted 2026-09-28)*
Scope: real thumbnails in the Media Browser; a project-scoped cache with invalidation; the cache of a project that
has not been saved yet; offline media.
- PR: every analysed, online video and image in the Media Browser shows a thumbnail of its content; audio, pending,
  failed and offline media show a placeholder; thumbnails appear without blocking the UI.
- PR: deterministic frame: the source time is given by a fixed rule (proposed at the start of 9.4, confirmed by
  the product owner) and the frame at it is selected by the existing D009 rule — the same file always gives the
  same image — chosen: `T = min(⌊Duration / 10⌋, 5 s)` in ticks from the file's start time (PO-3, D024 Step 9.4).
- PR: reopening a project reuses its thumbnails without decoding when the source is unchanged; a changed source
  (path, size or last-write time — exact key Impl) is regenerated; a missing or unreadable cache entry is
  regenerated silently, never an error for the user — chosen key: asset id, source size, last-write time (UTC
  ticks) and the thumbnail rule version.
- PR: offline media is never decoded for a thumbnail (D014: missing files are not probed); it shows a placeholder —
  refined: or its last cached thumbnail, if one exists (D024 Step 9.4).
- PR: a project that has not been saved yet gets thumbnails too; where its cache lives before the first save and
  what happens on Save / Save As is decided at the start of 9.4 and recorded — `project.json` stays
  `formatVersion` 2, `MediaAsset.ThumbnailPath` is kept as is unless a change is needed and justified — chosen:
  saved `<project>/cache/thumbnails`, unsaved `%LOCALAPPDATA%\AiVideoEditor\cache\unsaved\<projectId>\thumbnails`; the
  first Save moves the cache, Save As copies it; `ThumbnailPath` kept, unused (PO-1, PO-2, PO-5, PO-6).
- Impl: generation behind a Core interface (the existing `IThumbnailService`; `IVideoEngine` or a narrower
  interface — decided and recorded) implemented outside UI; the UI never starts ffmpeg; background work bounded
  and cancelled on project replacement like 9.3's analysis; the decoder, `SourceFrameSelector` and D009/D022 are
  used, not changed; playback and export never read the cache — chosen: Core `IThumbnailService` (contract
  replaced) implemented in Media over `IVideoDecoder`; `IVideoEngine` not used; at most 2 thumbnails made at once
  (PO-4).
- QG: tests for the frame rule, determinism, invalidation, a damaged cache, offline media, cancellation.
- Out of scope: timeline clip thumbnails / filmstrips, hover scrubbing, text-clip thumbnails, cache size limits,
  eviction or a cache-management UI.
- Depends on: 9.3 (analysis cancellation and concurrency policy; clean shutdown with more background work).

### 9.5 — Waveform *(done — sub-steps 9.5a–e; accepted 2026-09-28)*
Scope: waveforms of audio on timeline clips, generated through the media abstraction, cached, consistent with
mute / volume and the clip's time mapping.
- PR: an audio clip on the timeline with analysed, online media shows the waveform of exactly its source range;
  move / trim / split / undo update it from cached data without decoding the file again. Whether video clips with
  sound show one too is decided at the start of 9.5 — chosen: yes, in the lower part of the clip (D024 Step 9.5).
- PR: a clip at speed ≠ 1× shows its source range mapped onto its timeline length (display only — no audio
  processing) — done by the clip's `AudioPlacement`, the playback / export rule (D024 Step 9.5).
- PR: mute / volume: display rule decided at the start of 9.5 (e.g. height follows the volume, muted clip or
  track drawn dimmed); the waveform never changes the audio — chosen: height linear in the clip's volume (200 % reaches
  the clip's edge), a muted clip or track drawn dimmed with the same shape; envelope max(|L|, |R|), linear scale.
- PR: offline, unanalysed or soundless media shows no waveform, no error, no decode; generation runs off the UI
  thread, is cancelled on project replacement, and the timeline stays responsive. Chosen: waveforms are made only for
  media used by a clip on the timeline (not at import). Refined (PO-W5): offline media may show a waveform cached
  earlier — never decoded or made; none without a cached one.
- Impl: peak data produced outside UI (Video / media side, through the existing audio decoding or an interface
  next to 9.4's), stored in 9.4's cache with the same invalidation; the UI receives peaks only; playback and export
  audio unchanged (D013 / D022 / D023) — chosen: Core `IWaveformService`, Media `WaveformService` over `IAudioDecoder`;
  `<project>/cache/waveforms` / `…\cache\unsaved\<projectId>\waveforms` with the thumbnails' life cycle and key; at
  most 2 made at once, separate from thumbnails.
- QG: tests for peak computation (fake decoder), trim / split / speed mapping, cache reuse, offline.
- Out of scope: audio editing (envelopes, keyframes, fades), audio scrubbing, spectral views, meters,
  normalization, waveforms in the Media Browser.
- Depends on: 9.4 (cache and background generation).

### 9.6 — Hotkeys *(done — sub-steps 9.6a–d; accepted 2026-09-28)*
Scope: J / K / L; loop; playback / navigation shortcuts that follow from existing commands; the text-input guard;
routing tests; a small shortcut help.
- PR: K pauses, L plays forward. J within what the playback model supports (forward playback at 1×, D010 / D011;
  no reverse or shuttle speeds): its meaning is proposed at the start of 9.6 and confirmed — reverse or faster
  playback would be a semantic change with its own decision. Chosen (D024 Step 9.6): J = back 1 s, the playback
  state kept (playing continues from there, paused stays paused); K = pause (nothing when paused); L = play (nothing
  when playing; at the end from 0, like Play); Space stays Play / Pause.
- PR: loop on/off; while on, reaching the sequence end continues from the start (the whole sequence — the model has
  no in/out range); off, the D011 end rule is unchanged. Chosen: a Loop toggle button in the Preview transport and
  Ctrl+L; session state only — not in `project.json` (format v2 unchanged), not dirty, not undoable.
- PR: further shortcuts only for existing commands (list proposed at the start of 9.6, confirmed). Chosen: Ctrl+I
  Import Media, Ctrl+E Export, \ Zoom to Fit.
- PR: no shortcut fires while a text field has focus (Inspector fields, text content), checked in the running app;
  every existing shortcut keeps its key and command; editing shortcuts stay inert during an export (EditingLock).
- PR (optional): a small shortcut list in the UI — not chosen (product owner, start of 9.6).
- QG: automated tests key + modifiers → command, including the text-focus guard and every existing shortcut.
- Out of scope: configurable hotkeys, reverse playback, shuttle speeds, in/out marks.
- Depends on: 9.3 (stable app for manual checks); technically independent of 9.4 / 9.5.

### 9.7 — Performance baseline & optimization *(done — accepted 2026-09-28)*
Scope: measure first, then optimize only within D023.
- M (baseline, before any optimization): Preview update / copy / render time and late frames with 1 / 2 / 4 / 8
  layers; export throughput per scenario (layers, 720p / 1080p, 4K opt-in) as a real-time factor; peak memory;
  ffmpeg processes and handles during and after repeated exports / playback; Cancel latency per export stage
  (Cancel → `ExportAsync` returned, temporary files gone). Recorded with hardware, ffmpeg build, media and method;
  reproducible generated scenarios (as Step 8.1). No targets are set before the baseline.
- Decision point: the product owner reviews the baseline and chooses what to optimize (and any target); recorded
  as a D024 refinement.
- PR: explicit, reproducible resource leaks (processes, handles, memory) confirmed by the chosen measurement method
  are defects and are fixed in 9.7; the method and the practical criteria are defined at the start of 9.7. A change
  in memory or handles alone is not a defect.
- QG: every optimization keeps the parity suite green unchanged (byte-equal canvases where required now, no new
  tolerance) and is re-measured on the same scenarios (before / after).
- Impl: the measurement tool is a scratch tool outside the repository (as Step 8.6) or opt-in tests behind an
  environment variable — never thresholds in the default suite; choice recorded at the start of 9.7.
- Out of scope: hardware decode / encode, an ffmpeg filtergraph, reduced-resolution or non-blocking export
  decoding, any other semantic change for speed without its own decision; Step 8.6 and L1-c.
- Depends on: 9.3–9.6 (the baseline measures the feature set that ships, including thumbnail / waveform load).

### 9.8 — Polish & cleanup *(done — accepted 2026-09-28)*
Scope: limited polish (loading / busy, disabled, errors, empty states, progress / cancel feedback, obvious UX
problems found during Phase 9); `PlaybackFrame.Picture` cleanup, conditionally.
- PR: the polish list (collected during 9.3–9.7 plus a pass over the categories above) is proposed at the start of
  9.8 and confirmed before implementation; each item is verified; panels and layout stay as they are.
- PR / QG: `PlaybackFrame.Picture` / `IsPictureCurrent` are removed only if an audit confirms no production code
  reads them (today only `PlaybackService` fills them) and the removal changes no runtime behaviour; the ≈ 48 test
  uses move to the layer view without weakening. Otherwise they stay and the reason is recorded.
- Out of scope: redesign, theming, new panels, localization.
- Depends on: 9.3–9.7.

### 9.9 — CI / quality gates *(done — accepted 2026-09-28)*
Scope: a minimal GitHub Actions workflow — restore, build, test, FFmpeg for the tests that need it.
- QG: runs on pull requests to `main` and on pushes to `main`, on a Windows runner (WASAPI, Avalonia Win32 / Skia
  rendering tests).
- QG: build without warnings; the full test suite; ffmpeg / ffprobe installed on the runner in a fixed, known
  version compatible with the current tests (the version is chosen at the start of 9.9 after checking what the
  tests require); the job fails when ffmpeg-dependent tests are skipped unexpectedly (only the 4K heavy scenes
  may skip).
- QG: green on the Phase 9 pull request.
- Out of scope: coverage tooling (coverlet), OS / configuration matrices, heavy 4K tests in CI, release /
  packaging pipelines, repository settings such as branch protection (the owner's action).
- Depends on: a stable suite (9.3–9.8).

### 9.10 — Final verification & closeout *(done — formal manual run, regression, quality gates; Phase 9 accepted 2026-09-29, closeout `dcb86cb` / `f27a4ab`)*
- QG: `dotnet build --no-incremental` 0 / 0; full suite once plus three times with `--blame-hang`; heavy scenes
  once with `AIVE_HEAVY_TESTS=1`; CI green.
- PR: `docs/PHASE9_MANUAL_TEST_PLAN.md` run in the real app, results logged; `docs/EXPORT_MANUAL_TEST_PLAN.md` re-run
  as a regression.
- Documentation: ARCHITECTURE (thumbnails, waveform, cache, CI), D024 refinements, ROADMAP, `progress.md`; this
  plan's Phase 9 checkbox only after the product owner's acceptance.
- Depends on: 9.3–9.9.

## Phase 10 — Transitions & basic effects: steps (D025)

Formalized in Step 10.2 (product owner decisions PO-1…PO-7, 2026-09-29). The normative rules — model, format v3, ramps,
handles, edit coupling — are D025; this section lists the steps and their acceptance. Labels as in Phase 9: **PR**
product requirement, **QG** quality gate, **M** measurement only, **Impl** implementation constraint. Fades
(10.3–10.5) are finished — accepted by the product owner — before the dissolve starts (10.6–10.8).

Gates for every step 10.3–10.8 (QG): `dotnet build` 0 errors / 0 warnings; the full `dotnet test` green (only the 4K
heavy scenes skipped); the existing Preview ↔ Export parity suite (`ExportEndToEnd.Tests`, `Rendering.Tests`,
`Export.Tests`) green with unchanged criteria and expected values — new scenes are added, none is weakened, re-baselined
or removed; a project without fades and dissolves renders byte-identically to before (every existing parity scene is
such a project); new behaviour covered by automated tests wherever testable, the rest in
`docs/PHASE10_MANUAL_TEST_PLAN.md`; CI green; `progress.md` updated.

Constraints for the whole phase: D023 semantics unchanged for everything that is not a fade or a dissolve; L1-c stays
open; D008 (no overlap on a track), D009 / D022 (source frame selection) and D013 (mix) unchanged in substance — the
mix only gains the fade envelope. Out of scope: other transition types (wipe, slide, dip to black), keyframes, a generic
effect stack (`Clip.Effects` stays unused), audio crossfade and transitions on audio tracks or across tracks, ripple /
overlap editing, presets, transition hotkeys, GPU / hardware paths.

### 10.1 — Audit *(done, accepted 2026-09-29)*
Model, format, timeline rules, composition, audio mix, undo, `EditingLock`, parity tests audited; no change.

### 10.2 — Scope formalization *(done)*
D025, this section, ROADMAP, `progress.md`, `docs/PHASE10_MANUAL_TEST_PLAN.md` (skeleton). Documentation only.

### 10.3 — Model and project format v3 *(done)*
Scope: `Clip.FadeIn` / `FadeOut`; the transition anchor (`LeftClipId`, `RightClipId`); `project.json` v3 with the load
validation of D025 §1; v1 / v2 read as projects without fades and transitions.
- PR: a project with fades and dissolves round-trips exactly (every tick of every fade and transition, ids, anchors);
  a v1 and a v2 file open as before (fades 0, no transitions — a v2 `transitions` array is dropped) and save as v3; a
  file above v3 is refused with the "newer version" message; damaged fades / transitions (negative, unknown type,
  `F < 2`, missing or foreign clip, not adjacent, two on one cut, zone not fitting) refuse the file as damaged.
- PR: nothing renders or edits fades / dissolves yet; a project without them behaves exactly as before (the parity
  suite unchanged). Split copies no fade yet (10.4 sets the rule).
- QG: serializer tests (round trip, v1 / v2 / v3 / v4, every damaged case); the existing persistence tests unchanged
  except the one test data set that carried an unanchored transition (it gets an anchor — a model change, not a
  weakened assertion) and the five assertions of the version a save writes (2 → 3, the bump itself).
- Impl: DTOs stay separate from entities (D014); durations as long ticks; no new package.
- Depends on: 10.2.

### 10.4 — Fades: Core rule, edits, Preview / export composition and mix *(done — accepted 2026-09-29)*
Scope: the D025 §2 rule in Core; the fade property group; split / trim / speed / move behaviour; the snapshot, `LayersAt`,
occlusion and prefetch edges; the per-sample envelope in `AudioMix` for the Preview's mixer and the export.
- PR: `fade(i)` and `g(k)` exactly as D025 §2 (ramp `(k+1)/(F+1)`, clamp to `N`, product of the two ramps, frame and
  sample boundaries); a fade change is presentation-only (no decoder reopened).
- PR: split / trim / move / speed / re-grid keep the stored fades as D025 §2 (since 2026-09-30: cut to a clip left
  shorter than them, in the same step); split sets the inner edges to 0; every
  edit and its fade changes are one undo step; undo / redo restore every value exactly.
- PR: a fading layer never occludes the layers below; the Preview opens a lower layer ahead of a fade edge (no
  placeholder at the fade's start during playback).
- QG: Core unit tests (ramps, clamps, rounding at 23.976 / 29.97 / 25 / 30, boundaries in samples); Timeline edit /
  undo tests; `Export.Tests` contracts — the export's frames use the same layer set and opacities, and every audio
  sample of the export equals the Preview mixer's (fades on audio clips and video with sound, speed ≠ 1×, muted, 200 %
  volume, a split inside a ramp, overlapping ramps); playback prefetch test with the fake decoder.
- Depends on: 10.3.

### 10.5 — Fades: UI and end-to-end parity *(done — accepted 2026-09-29, real-app fade scenarios passed)*
Scope: Fade In / Fade Out in the Inspector (frames of the project rate, shown with their time; D017 merging); the ramps
drawn on the timeline clip; `EditingLock`; end-to-end parity scenes; the manual plan's fade section.
- PR: the fields show and edit the stored values within `0…N` frames; locked tracks and a running export disable them;
  text input never triggers shortcuts (9.6 guard).
- PR (parity, byte-equal Preview canvas = export canvas, D023 Step 8 criteria): fade in / out of a video at 1× on one
  layer; a V2 video fading over a V1 video (the lower layer uncovered during the ramp), with 2 and 8 layers; speed
  0.25× and 2×; image and text fades; a clip shorter than its fades (clamp, overlapping ramps); 23.976 / 29.97 fps;
  seeking into the middle of a ramp gives the same frame as playing into it.
- PR: audio end-to-end with real ffmpeg: the export's envelope follows `g(k)` (a constant tone fades in / out over the
  expected samples at 29.97 fps), A/V sync unchanged.
- PR: cancelling an export while a fade is being rendered ends as before (no ffmpeg left, no temporary file).
- Manual: `docs/PHASE10_MANUAL_TEST_PLAN.md` fade section in the real app (Preview, playback, export, save / reopen,
  undo / redo). The product owner accepts fades here, before 10.6 starts.
- Depends on: 10.4.

### 10.6 — Dissolve: edits and validation *(done — accepted 2026-09-29)*
Scope: add / remove / change duration; handle and zone validation (D025 §3–§4); the coupling with move, trim, split,
delete, speed and re-grid (D025 §5); transition changes carried in `EditPlan` so every coupled change is one command.
- PR: every case of D025 §5 — kept, removed automatically (status message) or rejected / clamped — with undo / redo
  restoring clips and transitions exactly; insufficient handles never create a dissolve (the message names the longest
  that fits); split inside the zone rejected.
- PR: PO-8 — a fade on an edge with a dissolve is not applied while the dissolve exists, and applies again once it
  is removed (stored values untouched).
- QG: Timeline tests per rule, at 1× and at other speeds, video / image / text neighbours, both edges of one clip,
  23.976 / 29.97 fps (odd `F`: `hB = ⌊F/2⌋`, `hA = ⌈F/2⌉`).
- Depends on: 10.5 accepted.

### 10.7 — Dissolve: composition in the Preview and the export *(done — accepted 2026-09-29; its real-app scenarios passed with 10.8)*
Scope: the snapshot carries the zone (A extended by `hA`, B by `hB`), `LayersAt` returns both clips of a track in the zone
(A below, B at `B.Opacity · p`), prefetch at the zone's start, the export's picture readers read extended frames.
- PR (parity, byte-equal): video → video at 1×; speed ≠ 1× on A and on B; image and text neighbours; odd `F` at 29.97;
  dissolves at both edges of one clip; a dissolve on V1 under a partly covering V2 and one on V2 over V1; handles
  missing at render time (source changed) — the held first / last frame, identical on both sides.
- PR: the frame shown in a handle is the one of the D009 / D022 rule — checked against ffmpeg's own decode where a
  part of A stays uncovered by B (a partly covering B).
- PR: the sound is unchanged by a dissolve (the export's samples equal those of the same project without it); the
  export preflight counts the extended frames as used media (an offline clip in a zone blocks like any offline clip);
  the Preview shows no placeholder at the zone's start while playing.
- PR: cancelling an export inside a zone ends as before.
- Depends on: 10.6.

### 10.8 — Dissolve: UI *(done — accepted 2026-09-29, real-app scenarios 13–20 passed)*
Scope: add a dissolve on the selected cut (command and button), select it, its duration in the Inspector, Delete, the
zone drawn on the timeline; `EditingLock`; the manual plan's dissolve section.
- PR: every edit goes through the edit service (one undo step); the zone is shown where it renders; the status bar
  explains rejections and automatic removals.
- QG: view-model and routing tests; manual scenarios.
- Depends on: 10.7.

### 10.9 — Final verification & closeout *(done — accepted 2026-10-01: the manual plan run in the real app, R7 export regression 13 / 13, build `-warnaserror` 0 / 0, 1988 passed / 2 skipped, 4K 88 / 88; Phase 10 accepted)*
- QG: `dotnet build --no-incremental` 0 / 0; full suite once plus three times with `--blame-hang`; heavy scenes once
  with `AIVE_HEAVY_TESTS=1`; CI green.
- PR: `docs/PHASE10_MANUAL_TEST_PLAN.md` run in the real app; `docs/EXPORT_MANUAL_TEST_PLAN.md` re-run as a regression.
- Documentation: ARCHITECTURE, D025 refinements, ROADMAP, `progress.md`; this plan's Phase 10 checkbox only after the
  product owner's acceptance.
- Depends on: 10.3–10.8.

## Phase 11 — Media relink & recent projects: steps (D026)

Formalized in Step 11.2 (product owner decisions PO-1…PO-9, 2026-10-01, after the Step 11.1 audit). **The approved
scope of Phase 11 is based on PO-1…PO-9 as recorded in D026; every step's implementation must conform to them.** A
change of any of them is a product owner decision, recorded as a D026 refinement before the code changes. The normative
rules are D026; this section lists the steps and their acceptance. Labels as in Phases 9–10: **PR** product requirement,
**QG** quality gate, **M** measurement only, **Impl** implementation constraint. Steps run in this order; each is
accepted by the product owner before the next one starts. The media life cycle and relink (11.3–11.6) come before the
recent projects (11.7–11.8), so the open path of projects is changed in one context (product owner, 2026-10-01).

The decisions, in short (full text: D026):
- **PO-1** — a relink is an `IUndoableCommand`; the project becomes dirty; Undo / Redo restore the path together with the
  metadata and analysis state, consistently.
- **PO-2** — hard reject: the file doesn't exist; a wrong media type; a duration too short for the `SourceIn` /
  `SourceOut` already used; the path belongs to another `MediaAsset`. Warning + confirmation: a different resolution,
  frame rate, no audio stream, other differing characteristics, source handles too short for an existing dissolve. No
  automatic adaptation of clips; `MediaAssetId`s and the timeline are kept.
- **PO-3** — without ffprobe the relink is allowed (the file exists, the type passes by extension); the asset becomes
  `Pending` and is analysed later; what couldn't be checked never becomes an irreversible error.
- **PO-4** — batch search: only the folder the user chose, no recursion, exact file name, a summary before anything is
  applied, applied after the user confirms; files not found stay offline. No fuzzy or recursive search.
- **PO-5** — re-check when the main window becomes active (throttled), no `FileSystemWatcher`; always before operations
  that need the files (export, relink); files gone during the session become offline, files back become online with
  their processing restarted; never on the UI thread.
- **PO-6** — only missing / offline media is relinked; replacing an online file is out of Phase 11.
- **PO-7** — `Recent ▾` in the toolbar next to Open, at most 10 entries; no start screen, no automatic opening of the
  last project, no menu bar, no general UI redesign.
- **PO-8** — added after a successful Open, Save As, and Recover of a project with a folder; never after a failed Open;
  unavailable projects are not removed automatically — shown as unavailable, with a clear way to remove the entry.
- **PO-9** — a path that belongs to another `MediaAsset` is rejected with a clear message; assets are never merged and no
  `MediaAssetId` changes.

Gates for every step 11.3–11.8 (QG): `dotnet build` 0 errors / 0 warnings; the full `dotnet test` green (only the 4K
heavy scenes skipped); the Preview ↔ Export parity suite (`ExportEndToEnd.Tests`, `Rendering.Tests`, `Export.Tests`)
green with unchanged criteria and expected values — none weakened, re-baselined or removed; new behaviour covered by
automated tests wherever testable, the rest in `docs/PHASE11_MANUAL_TEST_PLAN.md`; CI green; `progress.md` updated.
Builds and test runs are started on the product owner's command.

Constraints for the whole phase: `project.json` stays `formatVersion` 3 (a v3 file of Phase 10 opens and saves
unchanged apart from the paths a relink changes); D007, D008, D009 / D022, D013, D014 path resolution, D016, D018 / D023
and D025 unchanged in substance; L1-c stays open. Out of scope: replacing online media, fuzzy / recursive search,
`FileSystemWatcher`, detecting a present file changed in place, copying media into the project, a start screen,
automatic opening of the last project, a menu bar, a UI redesign, new hotkeys.

### 11.1 — Audit *(done, accepted 2026-10-01)*
Git state (Phase 10 merged as `2e758f1`), project persistence, missing media, caches, analysis, the open path, the
toolbar and the documentation audited; no change (report in `progress.md`).

### 11.2 — Scope formalization *(done, accepted 2026-10-01)*
D026, this section, ROADMAP, `progress.md`, ARCHITECTURE, README, `docs/README.md`, `docs/PHASE11_MANUAL_TEST_PLAN.md`
(skeleton), the outdated statements found by the audit. Documentation only.

### 11.3 — Media availability re-check *(done — accepted 2026-10-01 after the real-app run of scenarios 1–6; `db0feba`;
choices in D026 "Refined in Step 11.3")*
Scope: D026 §2 — a re-check of every asset's file off the UI thread, applied on the UI thread; triggers: the window
becoming active (throttled), before the export, before a relink; offline ⇄ online transitions with the processing
restarted per asset.
- PR: a file removed during the session makes its asset offline at the next check (Media Browser "Media offline", the
  Preview's placeholder, the export blocked by the preflight); a file that comes back makes it online again without
  reopening the project (picture in the Preview, thumbnail, waveform, analysis if it had no metadata).
- PR: the export re-checks the media before its preflight, whatever the last background check found.
- PR: a re-check never makes the project dirty, never enters undo / redo, changes nothing in `project.json`; a result of a
  check that started before New / Open / Recover is dropped; an analysis of an asset whose state changed meanwhile
  writes nothing.
- PR: the UI stays responsive while a check waits on a slow or disconnected drive (the file system is never read on the
  UI thread).
- QG: tests with a fake file system / temporary files for both transitions, the throttle, the overlap folding, the
  generation drop, the export trigger, the per-asset restart of thumbnails / waveforms / analysis (`ThumbnailCoordinator`,
  `WaveformCoordinator`, `MediaAnalysisCoordinator`), the playback snapshot rebuild.
- Impl: the throttle interval and the per-asset restart API of the coordinators are recorded in `progress.md`;
  `IProjectService.DetectMissingMedia` (no production caller) is replaced or completed — its XML comment made true.
- Depends on: 11.2.

### 11.4 — Relink: core *(done — accepted 2026-10-01, `0e002dc`; sub-decisions and implementation in D026 "Refined in Step 11.4")*
Scope: D026 §3 — the relink operation (Core contract, Timeline service and command — next to the timeline rules it
checks) with its validation, the probe, the warnings and the undoable command.
- PR: a relinked asset keeps its `Id`; every clip keeps `MediaAssetId`, source range, speed, properties, fades and
  dissolves; the Preview and the export use the new file; save and reopen give the new absolute and relative path.
- PR: each hard reject of PO-2 / PO-9 refuses with its own message and changes nothing; each warning of PO-2 is reported
  for confirmation and the relink is applied only after it; an asset that is online (PO-6) is not relinked.
- PR: without ffprobe the relink is applied as PO-3 (asset `Pending`, analysed when ffprobe is available, the user
  told that compatibility was not checked).
- PR: the relink is one undo step; the project becomes dirty; Undo / Redo restore `FilePath`, `FileSizeBytes`,
  metadata, analysis status / error and the missing state consistently; thumbnails, waveforms, the Preview and the
  export follow; an analysis started for an undone state is dropped.
- Decided at the start of 11.4: the exact list of compared characteristics; a later analysis that finds the file
  incompatible; a probe that fails on an existing file (D026 §3 proposals).
- QG: Project / Timeline / UI tests per rule; a probe integration test with real ffprobe (`Video.Tests`); the timeline
  validator accepts edits of a relinked clip whose source is long enough.
- Depends on: 11.3.

### 11.5 — Relink: batch search *(done — accepted 2026-10-01, `4180b4a`; D026 "Refined in Step 11.5")*
Scope: D026 §4 — the missing assets found in one chosen folder by exact name (e.g. the folder of the file just
relinked), each checked by the 11.4 relink check, applied after confirmation as one step.
- PR: matches are only in that folder, not in subfolders, by exact name (case-insensitive); the summary lists matches,
  the ones that can't be used and why, and the warnings; nothing is applied before confirmation; unmatched assets stay
  offline.
- PR: the confirmed batch is undoable (granularity decided at the start of 11.5) and restores every asset exactly.
- QG: tests with temporary folders (subfolder ignored, name case, a hard-rejected match, a duplicate name, cancel).
- Depends on: 11.4.

### 11.6 — Relink: UI *(done — accepted 2026-10-02 after the manual run of scenarios 7–21; `145d484`, the fixes of D1–D5 `5c4d2d9`; D026 "Refined in Step 11.6" and "Refined after the Step 11.6 manual run")*
Scope: D026 §5 — Relink in the Media Browser for offline media, the file picker, the dialogs, the batch summary, the
status messages; `EditingLock`.
- PR: Relink is offered only for offline media and is disabled during an export; the picker starts in the old file's
  folder when it exists and filters the asset's kind; rejections, warnings and the batch summary are clear; Undo / Redo
  from the toolbar and Ctrl+Z / Ctrl+Y work on it.
- QG: view-model and workflow tests with fake pickers / dialogs; routing unchanged (no new hotkey).
- Manual: `docs/PHASE11_MANUAL_TEST_PLAN.md` relink scenarios (moved, renamed, temporarily unavailable files).
- Depends on: 11.5.

### 11.7 — Recent projects: core *(done — accepted 2026-10-02, `55606fd`; D026 "Refined in Step 11.7")*
Scope: D026 §6 — the store and the rules of the list.
- PR: at most 10 entries, most recent first, no duplicates by full path (case and trailing separator ignored); added
  after a successful Open, Save As and Recover with a folder, never after a failed Open; unavailable entries are kept
  and reported as unavailable; an entry can be removed.
- PR: the list is outside every project, written atomically; a damaged or unreadable list never prevents startup; two
  running instances don't lose each other's entries.
- QG: store and rule tests (order, limit, dedupe, damaged file, atomic write, concurrent writers, availability check off
  the UI thread).
- Impl: the file format and the store's place in the projects (Core interface, implementation outside UI) recorded in
  `progress.md`.
- Depends on: 11.6.

### 11.8 — Recent projects: UI *(done — accepted 2026-10-04, `7bf4ed8`; its real-app run covered 22, 25, 26, 29 and 30–38, 23 only from the list — 23 through Open, 24, 27 and 28 were run at 11.9; D026 "Refined in Step 11.8"; no "Clear list")*
Scope: `Recent ▾` in the toolbar next to Open (PO-7).
- PR: choosing an entry asks about unsaved changes, then opens like Open; an unavailable entry is shown as such and can be
  removed; an entry that fails to open leaves the current project and the entry as they are, with a message; disabled
  during an export. A "Clear list" item: decided at the start of 11.8.
- QG: view-model and workflow tests; manual scenarios.
- Depends on: 11.7.

### 11.9 — Final verification & closeout *(done — local verification 2026-10-05, `ca20352`; Phase 11 accepted 2026-10-05; CI green on PR #11, merged as `47ed2fa`; D026 "Refined in Step 11.9")*
- QG: `dotnet build --no-incremental` 0 / 0; full suite once plus three times with `--blame-hang`; heavy scenes once
  with `AIVE_HEAVY_TESTS=1`; CI green.
- PR: `docs/PHASE11_MANUAL_TEST_PLAN.md` run in the real app; `docs/EXPORT_MANUAL_TEST_PLAN.md` re-run as a regression.
- Documentation: ARCHITECTURE, D026 refinements, ROADMAP, README, `progress.md`; this plan's Phase 11 checkbox only
  after the product owner's acceptance.
- Depends on: 11.3–11.8.

## Phase 12 — Editing essentials: steps (D027)

Formalized in Step 12.2 (product owner decisions of 2026-10-05, after the Step 12.1 audit). The normative rules are
D027; this section lists the steps and their acceptance. Labels as in Phases 9–11: **PR** product requirement, **QG**
quality gate, **M** measurement only, **Impl** implementation constraint. Steps run in this order; each is accepted by
the product owner before the next one starts, and a step's sub-decisions (D027 "Left to the start of …") are proposed
at its start and confirmed before its code changes.

Gates for every step 12.3–12.8 (QG): `dotnet build` 0 errors / 0 warnings; the full `dotnet test` green (only the 4K
heavy scenes skipped); the Preview ↔ Export parity suite (`ExportEndToEnd.Tests`, `Rendering.Tests`, `Export.Tests`)
green with unchanged criteria and expected values — none weakened, re-baselined or removed; every new project change
is one `IUndoableCommand` with exact Undo / Redo and the save point (dirty) respected; new behaviour covered by
automated tests wherever testable, the rest in `docs/PHASE12_MANUAL_TEST_PLAN.md`; `progress.md` updated. Builds and
test runs are started on the product owner's command; push, pull request and merge only with the product owner's
direct permission.

Constraints for the whole phase: `project.json` stays `formatVersion` 3 (a project of Phase 11 opens and saves
unchanged); D007, D008, D009 / D022, D013, D014, D016, D018 / D023, D025 and D026 unchanged in substance; L1-c stays
open; every new command is disabled during an export (`EditingLock`) and rejected on a locked track. Out of scope: AI
features, export settings, HDR / colour management, an installer, timeline virtualization, an undoable import, ripple
trim, a time-range (in / out) selection, automatic dissolves, a system-wide clipboard, a menu bar, a UI redesign.

### 12.1 — Audit *(done, accepted 2026-10-05)*
Git state (PR #11 merged as `47ed2fa`), build and tests (0 / 0; 2213 passed, 2 skipped), documentation, known issues
and the editing gaps audited; no change (report in `progress.md`).

### 12.2 — Sync after the merge and scope formalization *(done, accepted 2026-10-05)*
`main` fast-forwarded to `47ed2fa`, branch `feat/phase-12-editing-essentials`; the Phase 11 statements made
outdated by the merge (CI pending, branch not published) corrected; D027, this section, ROADMAP, README,
`progress.md`, ARCHITECTURE, `docs/README.md`, `docs/PHASE12_MANUAL_TEST_PLAN.md` (skeleton). Documentation only.

### 12.3 — Tracks: delete and reorder *(done — accepted 2026-10-05; D027 "Refined at the start of Step 12.3"; manual scenarios 1–7 at 12.9 at the latest)*
Scope: D027 §3.
- PR: a track can be deleted; a track with clips only after a confirmation, its clips and dissolves with it; Undo
  restores the track at its place with its order, name, flags, clips and dissolves.
- PR: a track can be moved up / down among the tracks of its kind; the timeline, the Preview and the export show the
  same layer order afterwards (video: the higher track on top); Undo restores the order.
- QG: tests of the commands (delete empty / with clips / with dissolves, reorder, undo / redo, save → reopen keeps the
  order), of the snapshot's layer order after a reorder, and an export of a reordered project matching its Preview
  (an added parity scene; existing scenes unchanged).
- Impl: `Track.Order` stays the only source of the layer order; the sub-decisions of D027 §3 recorded as a refinement.
- Depends on: 12.2.

### 12.4 — Removing media from the project *(done — accepted 2026-10-05; D027 "Refined in Step 12.4"; manual scenarios 8–13 at 12.9 at the latest)*
Scope: D027 §4.
- PR: an unused asset is removed at once; a used one after a confirmation naming the number of clips, together with
  those clips and their dissolves; one undoable step; Undo restores the asset with its id, path, metadata and analysis
  state, its clips and dissolves.
- PR: the file on disk is never touched; the Media Browser, the timeline, the Preview, thumbnails and waveforms show the
  state after the removal and again after Undo; work still running for the removed asset (a thumbnail, a waveform, an
  analysis) ends normally, is not shown while the asset is out of the project and is there after Undo (D027 §4).
- QG: tests of the command (unused / used / offline asset, dissolves, undo / redo, save → reopen), of the cache and
  analysis coordinators (nothing shown while removed, shown again after Undo without being made anew, a running
  analysis completing into the removed asset), of the confirmation flow.
- Depends on: 12.3 (the track and clip removal paths are shared).

### 12.5 — Ripple delete and close gap *(done — accepted 2026-10-05; D027 "Refined in Step 12.5"; the export QG met by `ExportRippleEndToEndTests`; manual scenarios 14–21 at 12.9 at the latest)*
Scope: D027 §2.
- PR: ripple delete removes the selected clips and moves the later clips of the same track left by the removed length;
  close gap removes an empty span of a track the same way; other tracks, the playhead and the markers stay; no overlap.
- PR: a dissolve of a removed clip is removed (with D025's status note); every other dissolve keeps its length and
  zone; no dissolve is created where clips meet only because of the ripple; one undoable step restores everything.
- QG: tests of the shift rule (one clip, several clips, several tracks, gaps kept, gap at the start), of each dissolve
  case of D027 §2, of locked tracks, undo / redo; an export after a ripple matching its Preview *(met: a real ripple
  delete, then a real export of 20 frames matching the Preview byte for byte at the new cut — `ExportRippleEndToEndTests`)*.
- Depends on: 12.4.

### 12.6 — Copy / paste / duplicate *(done — accepted 2026-10-05; D027 "Refined in Step 12.6"; Copy by Ctrl+C only; manual scenarios 22–28 at 12.9 at the latest)*
Scope: D027 §5.
- PR: copy keeps the selected clips with timing and properties (incl. text and fades, no dissolve); paste puts them at
  the playhead with their distances kept, on their own tracks when possible; a paste that would overlap or break a rule
  is rejected whole with a message; duplicate in one command; each one undoable step; pasted clips have new ids.
- QG: tests of copy (every property), paste (distances, tracks, rejection cases, ids), duplicate, undo / redo.
- Impl: hotkeys only if `ShortcutRouter` takes them without a structural change (decided at the step's start).
- Depends on: 12.5.

### 12.7 — Markers *(done — accepted 2026-10-05; D027 "Refined in Step 12.7"; the corner buttons checked in the real app; manual scenarios 29–32 at 12.9 at the latest)*
Scope: D027 §6.
- PR: add a marker at the playhead, remove a marker (both undoable, saved in v3), markers drawn on the timeline, go to
  the next / previous marker; a ripple does not move markers.
- QG: tests of the commands, navigation, save → reopen (v3 unchanged), the timeline drawing.
- Impl: snapping to markers only if it fits the existing snapping without a structural change, otherwise backlog.
- Depends on: 12.6.

### 12.8 — New during a running import *(done — accepted 2026-10-05; D027 "Refined in Step 12.8"; the guarded path automated, the real race not reproducible by hand)*
Scope: D027 §7.
- PR: a New / Open / Recover during an import leaves the new project without the picked files; the status bar says the
  import was dropped; an import without a project change works as before.
- QG: tests of the workflow with the project replaced at each await (status yield, file check), nothing added, no
  analysis queued.
- Depends on: 12.2 (independent of 12.3–12.7 in code; kept last by the product owner's order).

### 12.9 — Final verification & closeout *(done locally — 2026-10-05: build `-warnaserror` 0 / 0; the full suite 2324 passed, 2 skipped (only the two 4K scenes), 0 failed; three `--blame-hang` runs 2324 / 0 / 2, no hang, no dump; the 4K scenes with `AIVE_HEAVY_TESTS=1`: ExportEndToEnd 90 / 90; the manual plan, R1, R2, R3 in the real app; accepted by the product owner; PR #12 merged as `c0cb600`, CI green)*
- QG: `dotnet build --no-incremental -warnaserror` 0 / 0; full suite once plus three times with `--blame-hang`; heavy
  scenes once with `AIVE_HEAVY_TESTS=1`; CI green.
- PR: `docs/PHASE12_MANUAL_TEST_PLAN.md` run in the real app; `docs/EXPORT_MANUAL_TEST_PLAN.md` re-run as a regression.
- Documentation: ARCHITECTURE, D027 refinements, ROADMAP, README, `progress.md`; this plan's Phase 12 checkbox only
  after the product owner's acceptance.
- Depends on: 12.3–12.8.

## Phase 13 — Project & export settings: steps (D028)

Formalized in Step 13.2 (product owner decisions of 2026-10-06, after the Step 13.1 audit). The normative rules are
D028; this section lists the steps and their acceptance. Labels as in Phases 9–12: **PR** product requirement, **QG**
quality gate, **M** measurement only, **Impl** implementation constraint. Steps run in this order; each is accepted by
the product owner before the next one starts, and a step's sub-decisions (D028 "Left to the start of …") are proposed
at its start and confirmed before its code changes. No implementation before D028 is accepted.

Gates for every step 13.3–13.9 (QG): `dotnet build` 0 errors / 0 warnings; the full `dotnet test` green (only the 4K
heavy scenes skipped); the canvas-level parity (Preview ↔ export canvas: `Rendering.Tests`, `Export.Tests`, the canvas
checks of `ExportEndToEnd.Tests`) green with unchanged criteria and expected values — none weakened, re-baselined or
removed; codec-leg criteria (MP4 → export canvas) changed only in Step 13.8 by the L1-c decision (D028 §6); new canvas
sizes and rates covered by added parity scenes; every new project change is one `IUndoableCommand` with exact Undo /
Redo, the save point (dirty) respected and disabled during an export (`EditingLock`); new behaviour covered by
automated tests wherever testable, the rest in `docs/PHASE13_MANUAL_TEST_PLAN.md`; `progress.md` updated. Known flaky
CI tests: the D028 §8 policy (a recorded rerun of that named test for diagnosis only; never instead of a fix, never a
rerun-until-green). Builds and test runs are started on the product owner's command; push, pull request and merge only
with the product owner's direct permission.

Constraints for the whole phase: D028 §2 — D001 / D006, D008, D009 / D022, D013, D014–D016 (except D028 §5), D018's
composition math, D023's architecture, D025, D026 and D027 unchanged in substance; MP4 / H.264 / AAC, 8-bit 4:2:0
BT.709 limited, 48 kHz stereo stay fixed. Format: D028 §5 — the project settings need no format change; export
settings saved with the project keep `formatVersion` 3 as an optional property unless that is impossible, then v4
without backward migration (repository project JSON rewritten in v4). Out of scope: D028 §3.

### 13.1 — Audit *(done, accepted 2026-10-06)*
Git state (PR #12 merged as `c0cb600`, the local `main` behind it), documentation, the canvas / rate / export code and
the candidates audited; no change (report in `progress.md`).

### 13.2 — Sync after the merge and scope formalization *(done, accepted 2026-10-06 with D028)*
`main` fast-forwarded to `c0cb600`, branch `feat/phase-13-project-export-settings`; the Phase 12 statements made
outdated by the merge (CI pending, branch not published, complete locally) corrected and the acceptance / PR #12 /
`c0cb600` / CI green recorded; D028 (incl. the flaky-test policy, §8), this section, ROADMAP, README, `progress.md`,
`docs/README.md`, `docs/PHASE13_MANUAL_TEST_PLAN.md` (skeleton). Documentation only.

### 13.3 — Settings model and project format *(done — accepted 2026-10-06, `4514f09`; D028 "Refined at the start of Step 13.3": v3 kept, `settings.export` optional; the FR-1 regression test moves to 13.5 with its fix, the preflight's canvas limits to 13.4)*
Scope: D028 §1, §5, §7 (model only).
- PR: the project settings (canvas width × height, frame rate) are validated by one Core rule (even sizes, limits, a
  supported rate); an invalid value is refused with a message and changes nothing.
- PR: the export settings (quality level, speed preset, audio bitrate) exist as a Core model with today's output as the
  default; stored as D028 question 3 decides.
- QG: tests of the validation; persistence round trip of every setting (save → reopen, recovery file); if the format
  stays v3: a file without the new property reads as the defaults and a Phase 12 file opens and saves unchanged; if v4:
  the inline test JSON and the `tools/manual` fixture scripts rewritten in v4, the v1–v3 handling as decided at the
  step, `ExportSettingsPersistenceTests` changed to the new rule.
- Impl: the v3 / v4 choice by D028 §5's rule, recorded as a D028 refinement with its reason before the code.
- Depends on: 13.2 (D028 accepted).

### 13.4 — Canvas size change *(done — accepted 2026-10-06, `ec51247` (acceptance recorded at Step 14.2); D028 "Refined at the start of Step 13.4": CS-1 B, B-1…B-4, P-1; `ITimelineEditService.SetCanvasSize`, no UI — the `EditingLock` with the dialog of 13.6)*
Scope: D028 §4 (canvas).
- PR: the canvas of a new or existing project can be changed; one undoable step; the Preview and the export show the
  project at the new size (pictures re-fit by D018; positions and text sizes by the rule decided at the step's start).
- PR: an odd or out-of-limits size can't be chosen (the preflight's `InvalidCanvas` stays as the last guard).
- QG: tests of the command (undo / redo, dirty, refused values, `EditingLock`), of the snapshot / composition at the new
  canvas, and added parity scenes — at least a portrait (e.g. 1080 × 1920) and a non-16:9 canvas — whose export
  canvases match the Preview byte for byte; existing scenes unchanged.
- Impl: D018 changed only in its "default 1920 × 1080" (refinement recorded); `PlaybackSnapshot.Canvas` stays the only
  canvas the renderers read.
- Depends on: 13.3.

### 13.5 — Frame rate change *(done — accepted 2026-10-06, `010a1b8`; D028 "Refined in Step 13.5": `ITimelineEditService.SetFrameRate`, FR-1 fixed with regression tests, no UI — the `EditingLock` with the dialog of 13.6)*
Scope: D028 §4 (frame rate).
- PR: the project frame rate can be chosen for a new project and changed in an existing one; the timeline is
  re-gridded as one undoable step (D007's rule); a change that can't be made exactly is refused whole with a message;
  fades, dissolves and markers keep their time and stay valid; a user-chosen rate is not changed by a later first video.
- QG: tests of the re-grid (clips, collapse → one frame, refusal, fades, dissolves incl. a zone that no longer fits,
  markers, the clipboard rule), undo / redo incl. `IsFrameRateLocked`, save → reopen; an added parity scene exported
  after a rate change matching its Preview.
- Impl: D007 refined (the user may change a locked rate); the existing first-video lock and its re-grid reused, not
  duplicated.
- Depends on: 13.3 (independent of 13.4 in code; kept after it).

### 13.6 — Project settings UI *(done — accepted 2026-10-06, `66a0871`; D028 "Refined at the start of Step 13.6": `SetProjectSettings`, the draft dialog, New unchanged, errors in the dialog; manual scenarios 4–17 and 8a / 12a run in the real app at 1024 and 1440 px)*
Scope: D028 §1, §4 (new project).
- PR: a project settings dialog (canvas presets incl. portrait / square plus custom, the frame rate) reachable from the
  main window; applying it is the 13.4 / 13.5 command (one Undo step for a change of both); the New-project behaviour as
  decided at the step's start; disabled during an export; texts for refused values.
- QG: view-model tests (presets, custom values, validation messages, apply / cancel, one Undo step, `EditingLock`), the
  dialog at the minimum window width (1024 px) in the real app.
- Depends on: 13.4, 13.5.

### 13.7 — Export settings: core and encoder *(done — accepted 2026-10-06, `adf85e4`; D028 "Refined in Step 13.7": the job carries the settings, golden default command lines, the old constants removed; the AAC 320 kbps observation left to 13.8 / 13.9)*
Scope: D028 §7 (13.7).
- PR: an export uses the chosen quality, speed and audio bitrate; the default settings produce today's output.
- QG: tests of the encoder arguments for every offered value; real encodes per level (valid MP4, duration, frame count,
  audio length, the requested CRF / preset / bitrate visible in the stream metadata where ffprobe reports it); the
  existing parity suite green at the default settings.
- Impl: the container, codecs, pixel format and colour tags stay constants of `ExportFormat`; `ExportOutput` and the
  preflight keep their roles.
- Depends on: 13.3.

### 13.8 — L1-c: codec-leg criteria *(done — accepted 2026-10-06, `3d08c1b`; D028 "Refined in Step 13.8": quant PSNR per level 39 / 35 / 31 / 27.5 dB, levels ≥ 2 dB apart, flat colour ≤ 2; sound checks at every bitrate; `ExportCodecLegTests`)*
Scope: D028 §6.
- M: every offered quality level and speed preset measured with the Step 8.6 method over the existing scenes and the
  new canvas sizes (method, tool and tables recorded).
- PR: the product owner sets the codec-leg criterion per level from the data (or validity checks only).
- QG: the criteria in the suite; CRF-18-bound sanity checks replaced by the criterion of the level they test (each
  replacement listed with its old and new bound); the canvas-level parity and the audio checks unchanged; each new
  criterion confirmed by a mutation (a wrong CRF / a damaged frame fails it).
- Impl: L1-c closed in D028 (and pointed to from D023); nothing else in the suite changes.
- Depends on: 13.7.

### 13.9 — Export settings UI *(done — accepted 2026-10-06, `5c01aed`; D028 "Refined in Step 13.9": an EXPORT section of the Project Settings dialog, not the export flow — the product owner's choice; one Apply with the size and the rate)*
Scope: D028 §7 (13.9).
- PR: the export settings are chosen in the export flow, remembered by D028 question 3's rule, disabled during an
  export; the export manual plan still passes with the defaults.
- QG: view-model tests (choice, remembering, cancel, `EditingLock`); the dialog at 1024 px in the real app.
- Depends on: 13.7, 13.8.

### 13.10 — Final verification & closeout *(done locally — 2026-10-06: build `--no-incremental -warnaserror` 0 / 0; the full suite 2585 passed, 2 skipped (only the two 4K scenes), 0 failed; three `--blame-hang` runs 2585 / 0 / 2 each, no hang, no dump; the full suite with `AIVE_HEAVY_TESTS=1` 2587 passed, 0 skipped, 0 failed; the manual plan, R1, R2, R3 in the real app; closeout `7d4f6d8`; accepted; PR #13 merged as `ed40b74`, CI green on the pull request, the first run on `main` failed on the known flaky locator test — D028 §8, fixed in Step 14.4)*
- QG: `dotnet build --no-incremental -warnaserror` 0 / 0; full suite once plus three times with `--blame-hang`; heavy
  scenes once with `AIVE_HEAVY_TESTS=1`; CI green (D028 §8 policy for the known flaky tests).
- PR: `docs/PHASE13_MANUAL_TEST_PLAN.md` run in the real app; `docs/EXPORT_MANUAL_TEST_PLAN.md` re-run as a regression
  (R2) at the default and at one non-default setting; R1 — a Phase 12 project opens and saves unchanged if the format
  stayed v3 (if v4: the v1–v3 handling decided at 13.3 checked instead); R3 — Phase 11–12 features (relink, recent
  projects, tracks, ripple, copy / paste, markers) at a non-default canvas and rate.
- Documentation: ARCHITECTURE, D028 refinements and status, D007 / D018 / D023 pointers to D028, ROADMAP, README,
  `progress.md`; this plan's Phase 13 checkbox only after the product owner's acceptance.
- Depends on: 13.3–13.9.

## Phase 14 — Stabilization / technical debt: steps (D029)

Formalized in Step 14.2 (product owner decisions of 2026-10-06, after the Step 14.1 audit). The normative rules are
D029; this section lists the steps and their acceptance. Labels as in Phases 9–13: **PR** product requirement, **QG**
quality gate, **M** measurement only, **Impl** implementation constraint. Steps run in this order; each is accepted by
the product owner before the next one starts; a step's sub-decisions are proposed at its start and confirmed before its
code changes. No implementation before D029 is accepted.

Goal: a more deterministic CI and less technical debt **without any change of the user functionality** (D029 §1).

Gates for every step 14.3–14.7 (QG): `dotnet build` 0 errors / 0 warnings; the full `dotnet test` green (only the 4K
heavy scenes skipped); no user-visible behaviour changed — a production-code change only as a behaviour-neutral test
seam, each listed with its reason (a change of what the user sees needs a separate product owner confirmation, D029 §2);
no test weakened, skipped, removed or given a looser bound to pass (a test changed only to remove its dependence on
timing or the environment, with its old and new form recorded, and a mutation showing it still catches the defect it
guards); the canvas-level parity, the L1-c criteria (D028 §6) and every other expected value unchanged; `project.json`
unchanged (v3, no new property); `progress.md` updated. The D028 §8 flaky-test policy stays in force until Step 14.8.
Builds and test runs are started on the product owner's command; push, pull request and merge only with the product
owner's direct permission.

Out of scope: D029 §3 — among them the former candidate 14.6 (a canvas-relative font size of a new text clip; the status
after an audio device returns), deferred as product / UX changes.

### 14.1 — Audit *(done, accepted 2026-10-06)*
Git state (PR #13 merged as `ed40b74`, the local `main` behind it), documentation, technical debt and candidates
audited; no change (report in `progress.md`).

### 14.2 — Sync after the merge and scope formalization *(done, accepted 2026-10-06 with D029)*
`main` fast-forwarded to `ed40b74`, branch `feat/phase-14-stabilization`; the Phase 13 statements made outdated by the
merge corrected (ROADMAP, README, this plan, D028 status, ARCHITECTURE, `progress.md`, the Phase 13 manual plan), the 13.4
acceptance and the failed first CI run on `main` recorded; D029, this section, `docs/PHASE14_MANUAL_TEST_PLAN.md`
(skeleton), `docs/README.md`; the `src/Effects` analysis for 14.7 (D029 §6). Documentation only.

### 14.3 — Flaky test: the autosave timer (`Project.Tests`) *(done — D029 "Refined in Step 14.3": the cause a late timer callback after `Stop` in `AutosaveService`; `TimeProvider` + a run token; stress 50 / 50 and 10 / 10)*
Scope: D029 §4.
- M: the failing test(s) identified (CI history, the code) and the root cause shown — expected a dependence on wall-clock
  timing; the finding recorded before the fix.
- QG: the test(s) deterministic — no dependence on real elapsed time beyond what the behaviour itself needs; the
  autosave behaviour (2-minute interval, recovery file, the save point) unchanged and still covered; a mutation of the
  autosave (no write / a wrong interval) still caught; a stress run — the affected test class repeated at least 50 times
  and `Project.Tests` at least 10 times in full while the machine is loaded — without a failure.
- QG: the watched `Project.Tests` hang / unidentified failure (Steps 8.4, 9.3d): if the stress runs reproduce it, it is
  identified and fixed or reported to the product owner; if not, recorded as not reproduced.
- Impl: a time abstraction only if needed (e.g. .NET 8 `TimeProvider`, no new package), behaviour-neutral, listed.
- Depends on: 14.2 (D029 accepted).

### 14.4 — Flaky tests: ffprobe timeout in a waveform test, the 5 s PATH probe of the locators (`Video.Tests`) *(done — D029 "Refined in Step 14.4": the app's 5 s / 20 s / 20 s unchanged; test-only limits through internal seams and `FfmpegTools`; locators found once)*
Scope: D029 §4.
- M: the failing tests identified — among them
  `ExecutableLocatorTests.RealLocator_FirstCallCancelledMidProbe_SecondCallStillFindsFfmpeg` (the first CI run on
  `main` after PR #13: `Value is null` after 7 s) — and the root cause shown (the real `-version` probe's 5 s timeout
  under a loaded runner) before the fix.
- PR: **no user-visible change** without a separate product owner confirmation: the production probe timeout, the cached
  "not found" for the rest of the app run and the "FFmpeg missing" handling stay as they are, unless the product owner
  confirms a change proposed at the step's start with its user-visible effect.
- QG: each test keeps what it guards (e.g. a cancelled first call caches nothing — checked through the existing probe
  seam, deterministically) and no longer fails because the real probe is slow; a mutation of the guarded rule still
  caught; a stress run — the affected classes repeated at least 50 times while the machine is loaded — without a failure;
  the tests that need a real ffmpeg / ffprobe keep running on CI (no new skip).
- Depends on: 14.3.

### 14.5 — Test helpers: `F(end) − F(start)` and a regression guard *(done — D029 "Refined in Step 14.5": three helpers fixed, a guard per class, no expected value changed; the NUL replaced)*
Scope: D029 §4.
- QG: the helpers of `TrackEditTests`, `RippleEditTests` and `TimelineRippleUiTests` build a clip's duration as
  `F(end) − F(start)`; a guard test fails when a helper produces a clip off the frame grid (a mutation back to
  `F(end − start)` caught); no expected value of an existing test changes (if one has to, it is reported as a found
  defect, not adjusted silently).
- QG: the literal NUL in `ExportSettingsEndToEndTests.cs` replaced by `'\0'` (D029 answer 2); the file a text file for
  Git again; the test's behaviour unchanged.
- Depends on: 14.4.

### 14.6 — *(not taken — D029 §3: a canvas-relative font size of a new text clip and the status after an audio device returns are deferred product / UX changes)*

### 14.7 — `src/Effects` removal *(done — D029 "Refined in Step 14.7": the project out of the solution and `App`, the folder deleted; the effect model and its persistence untouched; 18 projects)*
- The project removed from the solution and from `App.csproj`; `Clip.Effects`, `Effect`, its
  persistence and copy (Core, Project, Timeline) untouched — `project.json` reads and writes exactly as before;
  ARCHITECTURE's module table and the project counts (`CLAUDE.md`, README if stated) corrected; no behaviour change.
- QG: the step gates; the solution builds without the project; R1 of 14.8 covers the format.
- Depends on: 14.5.

### 14.8 — Final verification & closeout *(done 2026-10-07 at `90caae9` — Release / Debug 0 / 0; 2600 passed, 2 skipped (only the 4K scenes); three `--blame-hang` runs 2600 / 2 / 0, no hang; heavy 2602 / 0 / 0; R1–R5 PASS against the Phase 13 build; D029 "Closeout"; accepted; PR #14 merged as `7200976`, CI green on the first attempt on the pull request and on `main`)*
- QG: `dotnet build --no-incremental -warnaserror` 0 / 0; the full suite once plus three times with `--blame-hang`;
  heavy scenes once with `AIVE_HEAVY_TESTS=1`; the stress runs of 14.3 / 14.4 repeated on the final tree; CI green on the
  pull request **without any rerun** (if a rerun is still needed, the phase goal is not met — reported to the product
  owner, not hidden by the D028 §8 policy); the D028 §8 policy closed or kept by the product owner's decision.
- PR: no user-visible change — `docs/PHASE14_MANUAL_TEST_PLAN.md` (regression only) in the real app: R1 a project saved
  by the Phase 13 build opens and saves byte for byte; R2 the export manual plan's default export; R3 autosave /
  recovery; R4 FFmpeg found / missing; R5 a full project round trip if `src/Effects` was removed.
- Documentation: ARCHITECTURE (if 14.7 changed the modules), D029 refinements and status, ROADMAP, README,
  `progress.md`, Known issues (the fixed flaky tests removed, anything not fixed stated); this plan's Phase 14 checkbox
  only after the product owner's acceptance.
- Depends on: 14.3–14.7.

## Phase 15 — Editing tools: steps (D030)

Formalized in Step 15.2 (product owner decisions of 2026-10-07, after the Step 15.1 pre-analysis). The normative rules
are D030; this section lists the steps and their acceptance. Labels as in Phases 9–14: **PR** product requirement,
**QG** quality gate, **M** measurement only, **Impl** implementation constraint. Steps run in this order; each is
accepted by the product owner before the next one starts; a step's sub-decisions (among them the open questions Q1–Q16
of D030 that concern it) are proposed at its start and confirmed before its code changes.

Goal: the everyday editing workflow of a bouldering video — cutting long recordings of attempts down to the part that
matters — with fewer actions and precise control: track controls, trim to the playhead, ripple trim, slip, an In / Out
range for loop playback and range export (D030 §1).

Gates for every step 15.3–15.8 (QG): `dotnet build` 0 errors / 0 warnings (`-warnaserror`); the full `dotnet test`
green (only the two 4K heavy scenes skipped); the Preview ↔ Export parity suite, the L1-c criteria (D028 §6) and every
existing expected value unchanged — new behaviour that reaches the picture or the sound adds parity scenes; `project.json`
unchanged (`formatVersion` 3, **no new property**; a Phase 14 project opens and saves byte for byte unless the user
edits it); every new project command one undo step, exact on Undo / Redo, dirty / clean by the save point (D015),
refused on a locked track and while an export runs (`EditingLock`), a refused or unchanged command leaving no undo step;
each rule of D030 §10 that the step touches covered by a test, and a mutation of the rule caught by that test; new
controls checked in the real app at 1024 and 1440 px; `docs/PHASE15_MANUAL_TEST_PLAN.md` scenarios of the step completed
(texts, automated coverage, development-time run); `progress.md` updated. Builds and test runs are started on the
product owner's command; push, pull request and merge only with the product owner's direct permission.

Out of scope: D030 §3 — roll edit, a source viewer, insert / overwrite, ripple across all tracks, track solo, keyframes,
text styling, dragging in the Preview, unlinking audio / J-L cuts / audio crossfades, freeze frame, reverse, speed ramps,
marquee selection, marker labels, filmstrips, frame export, AI, HDR / colour management, an installer, timeline
virtualization, an undoable import.

### 15.1 — Pre-analysis *(done, accepted 2026-10-07)*
Repository state (PR #14 merged as `7200976`, CI green on the first attempt on the pull request and on `main`; build
0 / 0; 2600 passed, 2 skipped), the delivered functionality, the gaps of the bouldering workflow, 3–5 candidate themes
with value, scope, risks and complexity; variant A (editing tools) recommended and chosen in full. No change in the
repository (report in `progress.md`).

### 15.2 — Sync after the merge and scope formalization *(done, accepted 2026-10-07 — `d1296be`)*
`main` fast-forwarded to `7200976`, branch `feat/phase-15-editing-tools`; the Phase 14 merge and both first-attempt CI
runs recorded (ROADMAP, README, this plan, D029, `progress.md`); D028 §8 closed; D030 (the locked scope, the In / Out
range as unsaved session state, the keys, the font-size formula, the non-goals, the D030 §10 inventory of the existing
semantics, the open questions Q1–Q16); this section; `docs/PHASE15_MANUAL_TEST_PLAN.md`; `docs/README.md`.
Documentation only — no production code, no test.

### 15.3 — Track controls: mute, hide, lock *(done, accepted 2026-10-07 — `1d26165`; D030 "Refined in Step 15.3": `SetTrackMuted` / `SetTrackHidden` / `SetTrackLocked` → `SetTrackStateCommand`, Q13 mute / hide allowed on a locked track, M / 👁 / 🔒 in the header)*
Scope: D030 §4.
- PR: video tracks get mute / hide / lock toggles, audio tracks mute / lock, in the track header; each toggle one undoable
  command (`ITimelineEditService`), the project dirty, clean again by Undo to the save point; saved in the existing v3
  track fields; a hidden track drawn dimmed, a locked one marked; all disabled during an export.
- PR: the Preview and the export respect the flags through the shared snapshot — a hidden video track has no picture
  (clips, texts, dissolve zones) but its sound plays unless muted; a muted track is silent; the sequence length is
  unchanged.
- QG: a test over every ordinary edit of `ITimelineEditService` on a locked track (refused, nothing changed, no undo
  step); end-to-end exports with a hidden track and a muted track equal to the Preview (parity scenes); a Phase 14 file
  with flags set opens with them and saves byte for byte; a Phase 15 file with flags uses only the existing fields.
- Decisions at the start: Q13 (toggles on a locked track); the header layout at 1024 px.
- Depends on: 15.2 (D030 accepted).

### 15.4 — Trim to the playhead: core, plain and ripple *(done, accepted 2026-10-07 — `f122c9a`; D030 "Refined in Step 15.4": `TrimToPlayhead`, Q / W and Shift+Q / Shift+W, a plain trim never trims a dissolve's cut edge, trims stop at a dissolve's frames with a message, the playhead to the clip's start after Shift+Q)*
Scope: D030 §5, §10, §11.
- M (before any code): the D030 §10 inventory re-checked against the code at the step's start (`PlanTrim`,
  `PlanTrimAtSpeed`, `PlanShift`, `EditPlan.ClampFades` / `ReconcileTransitions`, `DissolveParts`) and each row mapped to
  a planned test; a difference between the decisions and the code is reported before the implementation.
- PR: `Q` / `W` trim the target clips' start / end to the playhead (plain: the gap is left); the ripple variant moves the
  later clips of the same track by the change of length; the frame `p` is the new first frame (`Q`) / the first frame
  after the clip (`W`); one undo step for the whole command.
- QG: tests for every §10 row the step touches — the frame grid at 23.976 / 25 / 29.97 / 30 / 60 fps; 1× and speeds
  0.25× / 0.5× / 1.5× / 4× (the D022 invariant after every trim, the content under the playhead kept for a plain trim);
  fade kept / cut (D025 §2) and the PO-8 inactive fade; a dissolve on the cut edge (plain: not trimmed, Q4 as answered;
  ripple: per Q4) and on the far edge (clamped, per Q6); gaps between later clips kept; clips of other tracks, markers
  and the range not moved; the target rules (Q1); a locked track; the playhead not strictly inside → refused; Undo /
  Redo exact to the tick; the result of a plain trim identical to `TrimClip` of that edge to `p` (except a cut edge, Q4); an end-to-end export
  after a ripple trim equal to the Preview.
- Decisions at the start: Q1, Q2, Q4, Q5, Q6.
- Depends on: 15.3.

### 15.5 — Ripple trim by dragging an edge *(done, accepted 2026-10-07 — `efecd24`; D030 "Refined in Step 15.5": Shift at the press, one planner with Shift+Q / Shift+W (identical timelines), the preview plans only, one undo step on release, Esc restores, the playhead never moves, the ordinary drag unchanged)*
Scope: D030 §6.
- PR: an edge drag with the ripple modifier (Q3) previews and commits a ripple trim, inward or outward, through the
  15.4 planner; without the modifier the existing trim is unchanged; snapping as for the trim; `Esc` cancels.
- QG: equivalence — for the same clip, edge and target frame the drag result equals the 15.4 command's result tick for
  tick (a table over speeds, rates, fades, dissolves, gaps); outward limits (source start / end, a dissolve's handle);
  the preview shows the moved later clips and the zones as the release leaves them; `Esc` and an unchanged release
  leave no undo step; the existing trim, move and select gestures unchanged (their tests green, `Ctrl` toggle kept).
- Decisions at the start: Q3 (modifier).
- Depends on: 15.4.

### 15.6 — Slip *(done, accepted 2026-10-07 — `aa94da0`; D030 "Refined in Step 15.6": Alt at the press on a clip's body, SourceIn / SourceOut moved together by D022's start-trim amount, clamped to the source and the dissolve handles with a message, the planned Source In / Out on the clip during the drag, one undo step on release; video and audio clips)*
Scope: D030 §7, §10.
- PR: `Alt` (Q3) + drag on a video / audio clip's body slips its source range while its start, length, speed, fades and
  properties stay; clamped to the allowed range; one undo step per gesture; `Esc` cancels.
- QG: tests — the 1× and `≠ 1×` source rules (the D022 invariant after every slip), the limits (`SourceIn ≥ 0`, the end
  of the source, a dissolve's handles on either edge), images / text / unknown duration refused, a locked track refused,
  a video clip's sound slipped with its picture; the limit query equals what the service accepts; an end-to-end export
  after a slip equal to the Preview (parity scene, picture and sound).
- Decisions at the start: Q3, Q12.
- Depends on: 15.5.

### 15.7 — Timeline In / Out range: loop and range export *(done, accepted 2026-10-07 — `46eae30`; D030 "Refined in Step 15.7": I / O with the playhead's frame included, the opposite point cleared, the ✕ on the bar, Loop over exactly [In, Out) with silence from Out, Range / Entire sequence / Cancel, a range export byte for byte the whole export's frames and samples, the preflight of the range's media only; the Q15 audio status fix taken here from 15.8)*
Scope: D030 §8.
- PR: `I` / `O` set In / Out at the playhead; a clear operation (Q11); the range shown on the ruler and over the tracks;
  loop uses the range (Q9); the export can export only the range (Q10), refused when it is empty.
- PR: **transient session state** — never in `project.json` or the recovery file, never dirty, never an undo step;
  New / Open / Recent / Recover start without a range; edits do not move it.
- QG: tests — the range not serialized (a saved project and a recovery file without any range field, `formatVersion`
  3), not dirty, not in undo / redo, cleared on New / Open / Recover; In / Out rules (Q7, Q8), the clamp to the
  sequence, a frame-rate change; the loop boundary (playback reaching Out continues at In; start outside the range);
  the range export end to end — the frames equal the Preview at the same timeline frames, the samples equal the whole
  timeline's mix at the same positions (incl. a fade, a dissolve and a speed clip crossing In / Out), the duration
  `Out − In`, the preflight limited to the range (Q16); the default whole-sequence export unchanged.
- Decisions at the start: Q7, Q8, Q9, Q10, Q11, Q16.
- Depends on: 15.6.

### 15.8 — The D029 §3 UX fixes *(done, accepted 2026-10-08 — `f9a2253`, review corrections `9de14c6`; D030 "Refined in Step 15.8": a new text's `FontSize = 48 × canvasHeight / 1080`, exact; D028's canvas scaling of existing text unchanged. The audio status part was done in 15.7)*
Scope: D030 §9.
- PR: a new text clip takes `FontSize = 48 × canvasHeight / 1080` (exact, `double`, within 1 … 1000 for every canvas the settings accept); existing clips
  and the canvas-size scaling unchanged (Q14).
- PR: the "Playing without sound…" status cleared when the sound is available again, only if it is still shown; a later
  loss reported again (Q15).
- QG: tests at 1080 / 2160 / 720 / 1920 / 360 canvas heights (incl. a custom size), a project file unchanged apart from
  the new clip's value; the status sequence (loss → recovery → loss) through the playback seam; the real-app check with
  a real audio device unplugged and plugged back.
- Decisions at the start: Q14, Q15.
- Depends on: 15.7.

### 15.9 — Final verification & closeout *(done 2026-10-08 — results in `progress.md` and `docs/PHASE15_MANUAL_TEST_PLAN.md`; the R2 run found a still-image decode defect, fixed here — D030 "Found and fixed in Step 15.9"; PR #15 merged as `a3793a4`, CI green on the first attempt on the pull request and on `main`)*
- QG: `dotnet build --no-incremental -warnaserror` Release and Debug 0 / 0; the full suite once plus three times with
  `--blame-hang`; the heavy scenes once with `AIVE_HEAVY_TESTS=1`; `git diff --check` clean; CI green on the pull request
  without a rerun (D028 §8 is closed: any CI failure is a real failure).
- PR: `docs/PHASE15_MANUAL_TEST_PLAN.md` in the real app — the feature scenarios not yet run and the regression
  R1–R10 (Phase 14 project round trip, the bouldering workflow, track controls, trims, slip, In / Out, dissolve / fade
  edges, the Phase 10–14 regression, no ffmpeg, the audio device).
- Documentation: ARCHITECTURE (the Timeline commands, the snapshot / export range, the timeline view), D030 refinements
  and status, ROADMAP, README, `progress.md` (Known issues: the two D029 §3 items removed if fixed), this plan's Phase 15
  checkbox only after the product owner's acceptance.
- Depends on: 15.3–15.8.

## Phase 16 — Source viewer & three-point editing: steps (D031)

Formalized in Step 16.2 (product owner decisions of 2026-10-08: variant A, the plan and SQ1–SQ16, the A ↔ D boundary).
The normative rules are D031; labels as in Phases 9–15 (**PR**, **QG**, **M**, **Impl**). The product owner asked for the
whole phase without a stop between its steps; push, pull request and merge only on a separate command.

Goal: cut long recordings into the sequence with a few keys — open an asset in the Source mode of the Preview, mark a
source In / Out, Insert (`,`) or Overwrite (`.`) it at the timeline playhead (D031 §1).

Gates for every step 16.3–16.6 (QG): `dotnet build` 0 errors / 0 warnings (`-warnaserror`); the full `dotnet test` green
(only the two 4K heavy scenes skipped); the Preview ↔ Export parity suite, the L1-c criteria (D028 §6) and every existing
expected value unchanged; `project.json` unchanged (`formatVersion` 3, **no new property**; a Phase 15 project opens and
saves byte for byte unless the user edits it); every new project command one undo step, exact on Undo / Redo, dirty /
clean by the save point (D015), refused on a locked track and while an export runs (`EditingLock`), a refused or unchanged
command leaving no undo step; each rule of D031 §2 that the step touches covered by a test; new controls checked in the
real app at 1024 and 1440 px; `docs/PHASE16_MANUAL_TEST_PLAN.md` completed for the step; `progress.md` updated.

Out of scope: D031 §3 (the D side) and §5.

### 16.1 — Pre-flight *(done 2026-10-08)*
### 16.2 — Sync after the merge and scope formalization *(done 2026-10-08)*
- Documentation only: the Phase 15 / PR #16 merge recorded (ROADMAP, README, this plan, D030, `progress.md`), D031, this
  section, the manual plan's skeleton.

### 16.3 — Insert / Overwrite: core *(done — `cb50435`; review corrections `2216865`)*
- PR: `ITimelineEditService.InsertClip` / `OverwriteClip` (asset, source In / Out, the timeline point, the target track)
  with D031 SQ6–SQ9, SQ12, SQ13, SQ15 and the range defaults (§2).
- Impl: built only from the existing planners — the per-clip split of `Split` (extracted, unchanged), the trim rule
  (`TrimmedState`), the move rule (`ShiftedState` / `PlanShift`), `EditPlan` with `ReconcileTransitions` / `ClampFades`,
  `Validate`, `Commit`; the first video locks the rate as `AddClip` does (shared). The new code in
  `TimelineEditService.Insert.cs` (a `partial` file); no existing code moved.
- QG: `Timeline.Tests` — insert at the start, inside a clip, in a gap, at the end, at a cut with a dissolve, inside a
  dissolve's zone (refused), at 0.5× / 2× neighbours; overwrite of every overlap shape, across a dissolve, a clip covering
  the range; locked target; a range shorter than one frame; Undo / Redo exact; equivalence with the existing commands
  (split + ripple shift + add / trim give the same timeline).

### 16.4 — Source playback *(done — `22d2ef4`)*
- PR: D031 SQ2, SQ3, SQ11, SQ12, SQ14: a single-asset snapshot (Core), the Source / Timeline mode of `PreviewViewModel`,
  a source state service (asset, position, In / Out per asset) cleared on New / Open and when the asset leaves the
  project.
- Impl: `PlaybackService` unchanged; one snapshot version counter for both modes; in Source mode the playback position
  never reaches the timeline playhead and timeline changes are applied when Timeline mode returns.
- QG: `UI.Tests` with the real `PlaybackService` and a fake decoder — mode switches keep the timeline playhead, the
  timeline frame is shown again after the way back, a timeline edit while in Source, New / Open while in Source, Loop
  over the source range, an audio-only asset.

### 16.5 — Source viewer UI *(done — `22d2ef4`; the focus fix found in the real app `ac0e3ac`)*
- PR: D031 SQ1, SQ4, SQ11: the Timeline / Source switch of the Preview, opening an asset by a double click in the Media
  Browser (refusals with a message), the source time and duration, the In / Out band with its ✕, the keys by mode.
- QG: `UI.Tests` for the view model and `ShortcutRouter` (both key maps, exact modifiers, `Ctrl+I` / `Ctrl+L` unchanged);
  real app at 1024 / 1440 px.

### 16.6 — Insert / Overwrite from the UI *(done — `22d2ef4`; review corrections `2216865`)*
- PR: D031 SQ5, SQ8, SQ10, SQ16: `,` / `.` and the Insert / Overwrite buttons; the target track; the playhead and the
  selection after the edit; the status messages.
- QG: an end-to-end UI test (Source → In / Out → `,` / `.` → the timeline, Undo, Redo); an export test: a timeline built
  with Insert / Overwrite exports byte for byte like the same timeline built with the existing commands.

### 16.7 — Final verification & closeout *(done 2026-10-08 — results in `progress.md` and `docs/PHASE16_MANUAL_TEST_PLAN.md`)*
- QG: `dotnet build --no-incremental -warnaserror` Release and Debug 0 / 0; the full suite once plus three times with
  `--blame-hang`; the heavy scenes once with `AIVE_HEAVY_TESTS=1`; `git diff --check` clean; an independent phase review.
- PR: `docs/PHASE16_MANUAL_TEST_PLAN.md` in the real app, with the regression (a Phase 15 project round trip, export
  unchanged, Phase 15 tools, no ffmpeg).
- Documentation: ARCHITECTURE, D031 refinements and status, ROADMAP, README, `progress.md`, this plan's checkbox.

## Architectural rules that must hold at every phase

- UI (`src/UI`, `src/App`) contains no business logic — only view models that
  call into Core service interfaces.
- Moving/trimming a clip changes project state only; FFmpeg only runs for
  analysis, playback decoding, thumbnails or export — never as part of an edit.
- Every user-triggered project mutation is an `IUndoableCommand` executed
  through `IUndoRedoService`.
- Internal time is `MediaTime` (100ns ticks), never a raw frame integer;
  frame numbers are derived on demand for a given FPS.
- Heavy work is `async` and off the UI thread.
