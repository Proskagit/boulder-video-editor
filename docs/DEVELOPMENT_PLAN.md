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
- [ ] **Phase 11 — Media relink & recent projects.** Re-checking media availability during the session, relink of
      missing media (one file, then a batch found next to it) as an undoable change, and a list of recent projects in
      the toolbar. Scope, steps and acceptance criteria: section below and DECISIONS.md D026 (product owner decisions
      PO-1…PO-9). *(branch `feat/phase-11-relink-recent-projects`, from `2e758f1`; in progress)*

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

### 11.3 — Media availability re-check *(done — awaiting acceptance; choices in D026 "Refined in Step 11.3")*
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

### 11.4 — Relink: core
Scope: D026 §3 — the relink operation in Core / Project with its validation, the probe, the warnings and the undoable
command.
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

### 11.5 — Relink: batch search
Scope: D026 §4 — after a relink, the other missing assets found in the chosen file's folder by exact name.
- PR: matches are only in that folder, not in subfolders, by exact name (case-insensitive); the summary lists matches,
  the ones that can't be used and why, and the warnings; nothing is applied before confirmation; unmatched assets stay
  offline.
- PR: the confirmed batch is undoable (granularity decided at the start of 11.5) and restores every asset exactly.
- QG: tests with temporary folders (subfolder ignored, name case, a hard-rejected match, a duplicate name, cancel).
- Depends on: 11.4.

### 11.6 — Relink: UI
Scope: D026 §5 — Relink in the Media Browser for offline media, the file picker, the dialogs, the batch summary, the
status messages; `EditingLock`.
- PR: Relink is offered only for offline media and is disabled during an export; the picker starts in the old file's
  folder when it exists and filters the asset's kind; rejections, warnings and the batch summary are clear; Undo / Redo
  from the toolbar and Ctrl+Z / Ctrl+Y work on it.
- QG: view-model and workflow tests with fake pickers / dialogs; routing unchanged (no new hotkey).
- Manual: `docs/PHASE11_MANUAL_TEST_PLAN.md` relink scenarios (moved, renamed, temporarily unavailable files).
- Depends on: 11.5.

### 11.7 — Recent projects: core
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

### 11.8 — Recent projects: UI
Scope: `Recent ▾` in the toolbar next to Open (PO-7).
- PR: choosing an entry asks about unsaved changes, then opens like Open; an unavailable entry is shown as such and can be
  removed; an entry that fails to open leaves the current project and the entry as they are, with a message; disabled
  during an export. A "Clear list" item: decided at the start of 11.8.
- QG: view-model and workflow tests; manual scenarios.
- Depends on: 11.7.

### 11.9 — Final verification & closeout
- QG: `dotnet build --no-incremental` 0 / 0; full suite once plus three times with `--blame-hang`; heavy scenes once
  with `AIVE_HEAVY_TESTS=1`; CI green.
- PR: `docs/PHASE11_MANUAL_TEST_PLAN.md` run in the real app; `docs/EXPORT_MANUAL_TEST_PLAN.md` re-run as a regression.
- Documentation: ARCHITECTURE, D026 refinements, ROADMAP, README, `progress.md`; this plan's Phase 11 checkbox only
  after the product owner's acceptance.
- Depends on: 11.3–11.8.

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
