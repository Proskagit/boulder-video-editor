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
- [x] **Phase 16 — Source viewer & three-point editing.** A Source mode of the Preview with a source In / Out (session
      state) and Insert / Overwrite of that range at the timeline playhead on the target track; picture and sound of a
      video file stay one clip (unlinking and other audio editing left to a later phase). Scope, steps and acceptance
      criteria: section below and DECISIONS.md D031 (product owner decisions, 2026-10-08). *(branch
      `feat/phase-16-source-viewer`, from `1bdeb95`; Steps 16.2–16.6 `392db82` … `22d2ef4`; closeout 16.7 done
      2026-10-08 — build 0 / 0 Release and Debug, passed 2893, skipped 2, failed 0, three blame-hang runs and heavy green, the manual plan and the
      Phase 15 regression in the real app, an independent review and its corrections; accepted; PR #17 merged into `main`
      as `79e4e78` (2026-10-08), CI green on the pull request and on `main`)*
- [x] **Phase 17 — Audio editing.** Unlink a video clip's sound into an independent audio clip, link a video clip and an
      audio clip explicitly (edited as one, the ripple keeping their offset, a locked partner refusing the whole edit),
      J / L cuts, sound-only Insert / Overwrite, audio split / trim / fades, audio crossfades on audio tracks; Preview ↔
      export parity; `project.json` v4. Scope, steps and acceptance criteria: section below and DECISIONS.md D032 (product
      owner decisions, 2026-10-08). *(branch `feat/phase-17-audio-editing`, from `79e4e78`; Steps 17.1–17.8 done; AQ1–AQ13
      answered (as proposed); closeout 17.9 done 2026-10-12 — build 0 / 0 Release and Debug, passed 3030, skipped 2, failed
      0, three blame-hang runs and heavy green, the manual plan and the Phase 16 regression in the real app (R10 for the
      product owner); accepted, the Ripple Delete change of 2026-10-12 included; PR #18 merged into `main` as `fa96468`
      (2026-10-13), CI green)*
- [x] **Phase 18 — HDR → SDR of phone footage.** HDR video (HLG and PQ, BT.2020, 10 bit; Dolby Vision 8.4 and HDR Vivid
      by their HLG base layer) converted to the editor's SDR picture by one exact rule (S1 since D033 Revision R1: scene
      light, BT.2020 → BT.709, gamut compression, exposure, mobius on max RGB, the BT.709 OETF) as an application-generated 65³ LUT — the same for
      the Preview, the thumbnails and the export; SDR media decoded byte for byte as before; colour metadata read, kept
      (optional properties, `project.json` stays v4) and shown. Scope, steps and acceptance criteria: section below and
      DECISIONS.md D033 (product owner decisions, 2026-10-13). *(Steps 18.1–18.2 done 2026-10-13; implementation
      approved 2026-10-13 — branch `feat/phase-18-hdr-sdr`; Steps 18.3–18.7 done; closeout 18.8 done 2026-10-13 — build 0 / 0
      Release and Debug, passed 3150, skipped 2, failed 0, three blame-hang runs and heavy green, the manual plan and the
      Phase 17 regression in the real app; the product owner's visual check of the L2 export failed → D033 Revision R1, the
      rule S1, Steps 18.9–18.12 done 2026-10-14 — build 0 / 0 Release and Debug, passed 3196, skipped 2, failed 0, three
      blame-hang runs and heavy (3198 / 0 / 0) green; HLG measured on the 13 clips (the visual check open), PQ not verified on real PQ footage; the
      product owner's visual check of the S1 export open; PR #19 merged into `main` as `c29a767` (2026-10-14), CI green;
      still open: the product owner's visual check of the S1 export, PQ on real PQ footage)*
- [x] **Phase 19 — AI hold markup.** On a paused frame of a video clip the user clicks holds; SAM 2.1 Large (pinned model
      `sam2.1-large-v1`, ONNX, local only, DirectML with a CPU fallback) returns their real contours; the confirmed holds
      are drawn over the clip for its whole duration as one route outline, identical in the Preview and the export;
      undo / redo; `project.json` v5. Scope, steps and acceptance criteria: section below and DECISIONS.md D034
      (accepted 2026-10-14, specification revision 5). *(Pre-analysis and the experiments E1 / E2 done 2026-10-14
      outside the repository; branch `feat/ai-hold-markup` from `c29a767`; Steps 19.0–19.8; PR #20 merged into `main` as
      `2d0a10b` (2026-10-15), CI green)*
- [x] **Phase 20 — Manual correction of hold contours.** In the markup mode a selected hold shows handles: drag a vertex,
      Alt+click to add one, Delete / double-click to remove one (at least three stay), no self-crossing; one undo step per
      gesture; `project.json` stays v5. Scope, steps and acceptance criteria: section below and DECISIONS.md D035.
      *(Steps 20.1–20.4; accepted by the product owner on 2026-10-15; PR #21 merged into `main` as `a657bff` (2026-10-15))*

- [x] **Phase 21 — Animation of the hold outlines.** With "Animate" on, a clip's hold contours are hidden before a start
      bound to the source time, appear one by one (Bottom to top or the marking order) with a fade, stay, and disappear
      together with a fade — or stay to the clip's end; computed on timeline frames in `PlaybackSnapshot.LayersAt`, so the
      Preview and the export agree on every frame; undo / redo; `project.json` v6. Scope, steps and acceptance criteria:
      section below and DECISIONS.md D036 (accepted 2026-10-15). *(Branch `feat/phase-21-hold-animation` from `a657bff`;
      Steps 21.1–21.6; the main functionality accepted by the product owner in the real app on 2026-10-15;
      PR #22 merged into `main` as `7318ba8` (2026-10-15))*
- [x] **Phase 22 — Timeline selection, snapping and making room.** Ctrl+A, Esc, marquee selection, "1 of N selected" in
      the Inspector; magnetic snapping that never makes an overlap, also for a drop from the Media Browser (a new clip on
      an empty track at 0); placement edits push the clips on the right instead of being refused (one track, a partner
      follows or the edit is refused, atomic, one undo step); Alt+arrow nudges; auto-scroll while dragging. `project.json`
      stays v6. Scope, steps and acceptance criteria: section below and DECISIONS.md D037 (accepted 2026-10-15).
      *(Branch `feat/phase-22-timeline-ux` from `7318ba8`, the merge of PR #22; Steps 22.0–22.6 `3cc9bac` … `0f03ba3`;
      accepted by the product owner on 2026-10-16; PR #23 merged into `main` as `1aaedc8` (2026-10-16))*
- [x] **Phase 23 — Direct Move, Scale and Crop in the Preview.** Move (drag; the frame's corner handles scale around the
      centre) and Crop (eight handles, anchored) tools in the Preview's bar; one undo step per gesture; `project.json` v6.
      Scope, steps and acceptance criteria: section below and DECISIONS.md D038. *(Branch
      `feat/phase-23-preview-transform`; accepted by the product owner on 2026-10-19 — PASS WITH NOTES; PR #24 merged into
      `main` as `c652667`)*
- [x] **Phase 24 — Russian localization and interface polish.** The whole interface in Russian (English the neutral
      language of the resources, Russian chosen in `appsettings.json`), the service layer's user messages included; guard
      tests against untranslated text; layout checked at 1024 × 640 and 1440 × 900; vector icons for the frequent
      commands; sliders for Opacity and Volume. `project.json` stays v6. Scope, steps and acceptance criteria: section
      below and DECISIONS.md D039 (accepted 2026-10-19). *(Branch `feat/phase-24-localization` from `c652667`; Steps
      24.0–24.8; accepted by the product owner on 2026-10-19; PR #25 merged into `main` as `d3df0ba` (2026-10-19), CI green
      on the pull request and on `main`)*
- [x] **Phase 25 — Technical backlog and small UX improvements.** `appsettings.json` read from the application's folder
      (FU-24.2); the marker navigation status (FU-21.1); the hold markup mode left on its own (FU-22.1); sliders for Fade
      In / Out and the hold line width (FU-24.1, in part); icons on the timeline's tool bar; the Phase 24 manual gaps.
      `project.json` stays v6. Scope, steps and acceptance criteria: section below and DECISIONS.md D040 (accepted
      2026-10-19). *(Branch `feat/phase-25-backlog-ux` from `d3df0ba`; Steps 25.0–25.6, FU-25.1 and FU-25.2; accepted by
      the product owner on 2026-10-19 — PASS WITH NOTES; PR #26 merged into `main` as `58c48cb` (2026-10-19), CI green on
      the pull request)*
- [x] **Phase 26 — Vertical video and framing.** The first video of an empty project with the default canvas offers a
      canvas of its shape (a dialog, one Undo step); «Заполнить кадр» / «Вписать в кадр» compute a clip's Scale (the
      Inspector and the clip's context menu, one Undo step each). `project.json` stays v6. Scope, steps and acceptance
      criteria: section below and DECISIONS.md D041 (accepted 2026-10-20). *(Branch `feat/phase-26-vertical-video` from
      `58c48cb`; Steps 26.0–26.4 done and verified by Claude on 2026-10-20; accepted by the product owner on
      2026-10-20; PR #27 merged into `main` as `41dadf2` (2026-10-20), CI green on the pull request)*
- [x] **Phase 27 — Text animation and readability (D042 Phase A).** Appearance / disappearance of a text clip (Нет,
      Проявление, Выезд, Увеличение) over the clip's fades, an outline and a background (each on its own), colour swatches,
      a 3 × 3 placement grid, bold, defaults for a caption; `project.json` v7. Scope, steps and acceptance criteria: section
      below and DECISIONS.md D043 (accepted 2026-10-20). *(Branch `feat/phase-27-text-animation` from `41dadf2`; Steps
      27.0–27.6 done, up to `3c45c9d`; accepted by the product owner on 2026-10-20; PR #28 merged into `main` as
      `36a1a58` (2026-10-21), CI green on the pull request)*
- [x] **Phase 28 — Transition library (D042 Phase B).** Наплыв, Затемнение, Вспышка, Наезд, Сдвиг on D025's model;
      durations in seconds stored as whole frames; a 1.0 s minimum between the peaks of Flash / Dip to black;
      `project.json` v8. Scope, steps and acceptance criteria: section below and DECISIONS.md D044 (approved 2026-10-20).
      *(Branch `feat/phase-28-transitions` from `3c45c9d`, Phase 27's accepted tip; Steps 28.0–28.5 done; accepted by the
      product owner on 2026-10-21; PR #29 merged into `main` as `4f0e66a` (2026-10-21), CI green on the pull request)*
- [x] **Phase 29 — Visual templates and the music scenario (D042 Phase C).** Four ready-made templates for bouldering
      videos — REC, VHS, «Боулдер» (route, grade, a manual TOP / ПОПЫТКА status with its moment), «Хайлайт»; WASTED
      deferred — and the audit of the existing music path (no defect); `project.json` v9. Scope, steps and acceptance
      criteria: section below and DECISIONS.md D045 (approved 2026-10-21). *(Branch `feat/phase-29-templates` from
      `4f0e66a`; Steps 29.0–29.6 and the corrections of D045 §17 done; accepted by the product owner on 2026-10-22;
      PR #30 merged into `main` as `e28a9d3` (2026-10-22), CI green on the pull request)*
- [x] **Phase 30 — Stabilisation of the current base (D042 Phase G; technical closeout).** The technical stabilisation
      of the main path and the portable build is **complete by the product owner's decision of 2026-10-24**: the local
      technical verification passed (builds 0/0, full and heavy test suites 0 failed, three `--blame-hang` runs clean,
      `git diff --check` clean, v9 byte-for-byte round-trip covered by `Project.Tests`, the portable folder rebuilt and the
      available Step 30.2 rows re-run). The main path (D042 §1 without step 4) was run in the portable build on 2026-10-23
      and checked by the owner on 2026-10-24 (B1–B9 PASS); the performance of a typical project was measured and the targets
      C1–C6 agreed on 2026-10-24. **The full acceptance of S1 is not declared**: FFmpeg distribution, licence notices review,
      a clean-environment check without the current machine's limitations, the C4 live-preview observation, **CI green**
      (the Phase 30 branch has not been pushed/merged, so no CI run exists; the last green CI was PR #30 `e28a9d3`) and the
      product owner's final sign-off remain open and are deferred to a separate release-preparation task. Scope, steps and the historical results: section below and
      DECISIONS.md D046 (approved 2026-10-22). *(Branch `feat/phase-30-stabilization` from `e28a9d3`; Steps 30.0–30.7
      technically complete 2026-10-24; S1 acceptance pending a later task.)*
- [ ] **Planned (D042; numbers given when taken up):** R1 → D — automatic hold proposals · R2 → E — outlines following the
      camera · R3 → F — COROS Pace 4 heart rate and telemetry · H — Final UX Pass. Section below.
- [x] **D048 approved 2026-10-28, as implemented** (DECISIONS.md D048; numbers 32–34 still proposals):
      - stage 1 — the route selection at `T`: implemented, then **paused / deferred**; «Авторазметка» hidden, the code
        and the research kept (`docs/HOLD_AUTO_DETECTION_RESEARCH.md`);
      - stage 2 — the timing model, the template ↔ clip anchor and `project.json` v10: done;
      - stage 3 — the overlay strip: done, **variant B** (the segments inside the «Боулдер»), the lane Debug-only.

      Manual markup stays the main scenario. Section "D048: route selection, intro / final timing and the overlay strip"
      below (written before the approval).
- **Phase 31 technically complete (D047 approved 2026-10-25):** Steps 31.1–31.7 are complete: Core proposal types and
      pipeline, proposal generation at downscaled resolution, full-resolution refinement of accepted proposals, the proposal
      review UI / ViewModel, integration with the manual D034 flow / project mutation, the manual test plan / performance
      optimization, and final verification / closeout. The default grid was changed from 16×16 to 15×15 during Step 31.6: it
      is faster and has higher recall on the E1 set, so the ≤8 s acceptance criterion is met for both 4K and 1080p on the
      development machine. Full user acceptance is pending the interactive A/B/E checks that require the product owner's own
      footage and manual UI actions. D047 clarifications: ≤8 s on a 4K frame is the acceptance performance criterion, ≤5 s
      desired but not a blocker; refinement runs automatically after Accept and reports failure instead of creating an
      incorrect Hold; proposal pass resolution is the internal default 1920×1080; default prompt grid is 15×15.

## Planned phases after Phase 26 (D042)

The normative text is DECISIONS.md D042 (2026-10-20): the product concept, its boundaries and this order — confirmed by the
product owner after the merge of Phase 26 (D042 "Refined after the merge of Phase 26"). Each phase gets its own decision,
steps, acceptance criteria and manual plan when it is taken up, and its number then. A is Phase 27 (D043; merged, PR #28,
`36a1a58`); B is Phase 28 (D044; merged, PR #29, `4f0e66a`); C is Phase 29 (D045; merged, PR #30, `e28a9d3`); G is
Phase 30 (D046; approved 2026-10-22, **technically complete 2026-10-24** — local verification passed; the full S1 acceptance
is deferred to a separate release-preparation task). R1 → D (automatic hold proposals) is **complete as a research phase**
(conditional GO; Step R1.1 and R1.2 done in `tools/r1-hold-proposals/`); phase D is not started and requires its own
decision. R2 → E, R3 → F and H are neither started nor approved. The scopes of the
phases not yet taken up are sketches to be refined — not specifications. Every phase keeps the standing rules at the end of this file: one Undo step per user action, the
Preview identical to the export, `MediaTime` ticks, heavy work off the UI thread, the localization guards (D039), the gates
of the recent phases (build `-warnaserror`, the full suite, heavy, `--blame-hang`, `project.json` compared byte for byte
when the format does not change).

Kinds: **core** — the functional core of D042 §1; **research** — no product code, experiments outside the repository, a
go / no-go and a specification for the product owner; **AI**, **telemetry**, **UX** — the specialisation phases.
Sizes are rough, relative to the past phases (small ≈ Phase 25's size, medium ≈ Phases 21–23, large ≈ Phases 17–19);
no calendar estimate is fixed here.

### Order and dependencies

```
A (27) ─► B ─► C ─► G                             S1 — the minimum useful release
R1 ─(go)─► D                                        S2 — with D and / or E
R2 ─(go)─► E   (after D when D is taken)
R3 ─(go)─► F   (also after A)                       S3
G + D / E as taken + F ─► regression run ─► H       S4 — the full plan
```

- A, B and C each change the composition (`PlaybackSnapshot.LayersAt`, `CompositionDrawPlan`) — one at a time, never in
  parallel sessions.
- R1, R2 and R3 change no product code and may run in parallel with A–C; R3 needs the product owner's real files (COROS
  Pace 4 exports and a video of the same session). A research go is not an approval: D, E and F are agreed separately
  after the results (F only after the technical feasibility is confirmed).
- F uses the animated overlay path that A establishes; D and E use the hold model of D034–D036.
- The order of D / E against B / C may be changed by the product owner once R1 / R2 report (D042 §5).

### A — Text animation and readability *(core, medium; Phase 27 — DECISIONS.md D043, accepted 2026-10-20)*
- Scope decided by the product owner (D042, 2026-10-20): the appearance / disappearance animation of text; its colour,
  size and position; one basic readability aid (an outline or a background); sensible defaults and a simple interface.
  No full title editor, no complex motion design.
- The specification with its UX, Undo / Redo, parity, localization, checks, acceptance criteria and risks: D043; the steps:
  "Phase 27 — Text animation and readability: steps" below.

### B — Transition library *(core, medium; Phase 28 — DECISIONS.md D044, approved 2026-10-20)*
- Today: the cross dissolve of one video track (D025) and the fades.
- The set (D044, after a research of what editors use; the owner's answers in D044 §11): Наплыв 1.0 s,
  Затемнение 0.6 s, Вспышка 0.2 s (0.1–0.5 s), Наезд 0.3 s (0.2–1.0 s, `K = 1.25`, the change of clip at the peak), Сдвиг
  0.3 s (≥ 0.2 s, four directions, B from the right by default, no motion blur); no Wipe. Only Dissolve and Push need source
  handles. Durations in seconds stored as whole frames (D044 §5); a 1.0 s minimum between the peaks of Flash / Dip to black,
  a breaking edit rejected; `project.json` v8; Step 28.1 a visual check of Zoom on the product owner's clips (a gate: Z2 / Z3
  proposed, never chosen alone).
- The steps: "Phase 28 — Transition library: steps" below (branch from `3c45c9d`; the pull request after Phase 27's merge).
- Out: a transition editor, custom curves, motion blur, an unlimited library.

### C — Visual templates and the music scenario *(core, medium; Phase 29 — DECISIONS.md D045; accepted 2026-10-22; merged, PR #30, `e28a9d3`)*
- Refined by the product owner on 2026-10-21: the colour looks of the first sketch (black and white, vivid, warm, cool,
  vignette) are dropped. Instead five ready-made visual templates made for bouldering videos — REC / Camcorder, VHS /
  Analog tape, «Боулдер» (a compact plate «ТРАССА 7 · 7A» with a manual TOP / ПОПЫТКА status; revised at the gate from a
  Competition overlay, whose richer layout is kept only as a future extension «Соревнование»), Highlight («Хайлайт»).
  WASTED (a reconstruction of GTA V's device) was prototyped at the gate and **deferred on 2026-10-21** — the sound accent
  unsatisfactory, no suitable sound chosen; not in Phase 29, not blocking it (D045 §15).
- The music path audited end to end (import, placement under the video, volume, trims, fades, export) — only the defects
  and material UX gaps the product owner agrees to are fixed; no mixer, ducking, equaliser, reverb, looping or automatic
  fitting.
- The specification, the research and the architecture proposal: D045; the steps: "Phase 29 — Visual templates and the
  music scenario: steps" below.
- Out: colour correction and grading, an effect library or stacks, keyframes, free styling, any automatic detection, a
  third party's assets, WASTED (deferred, D045 §15).

### G — Stabilisation of the main path *(core, medium; the release candidate; Phase 30 — DECISIONS.md D046, approved 2026-10-22)*
- Specified by D046 (the product owner's refinements of 2026-10-22: FFmpeg in the delivery after a licence audit, the
  licence notices, performance measured before targets, HDR / PQ and R10 not blocking, Windows Sandbox or a clean VM,
  .NET 8 kept, blocker / major defects fixed and minor / UX ones brought to the owner); the steps: "Phase 30 —
  Stabilisation and the portable build S1: steps" below. The sketch as first recorded:
- The eight steps of D042 §1 (without step 4) run end to end in the real app on the product owner's footage at 1024 × 640
  and 1440 × 900; the defects found fixed; the open manual checks of earlier phases run or recorded; the performance of a
  typical project (several minutes, a few tracks, holds, text, transitions) measured.
- S1 (the product owner, 2026-10-20): a **portable Windows build** is enough, no installer. Criterion: the application
  starts outside the development environment (no SDK, no repository; its own folder with its dependencies; the AI model
  installed as `docs/AI_MODEL.md` describes) and the main path (D042 §1 without step 4) is completed in it, import to
  export.

### R1 → D — Automatic hold proposals *(research complete, then AI large; Phase 31 — DECISIONS.md D047, approved 2026-10-25)*
- **R1** (research, no product code): completed 2026-10-24. The harness in `tools/r1-hold-proposals/` evaluated grid-prompt
  proposals against the E1 reference holds. Best tested configuration: SAM 2.1 Large ONNX, 16×16 grid, frame decoded and
  evaluated at 1920×1080 — 247.9 s on the 15-frame E1 set (2.27× faster than full-resolution Large), 85/164 reference holds
  matched (51.8 % recall), 10.1 % precision. R1 concluded with a **conditional GO**. The default grid was later changed
  to 15×15 in Step 31.6 (53.7 % recall, ~7.4 s on a warm 4K frame).
- **D / Phase 31** (approved 2026-10-25): automatic hold proposals in the markup mode. The user reviews ranked proposals,
  accepts or rejects them individually or in batch; accepted proposals are refined at full resolution and become ordinary
  D034 holds, or the user is informed if refinement fails. The existing manual click flow is preserved. The full
  specification, acceptance criteria, steps, non-goals and risks: DECISIONS.md D047. The implementation steps: "Phase 31
  — Automatic hold proposals: steps" below. Steps 31.1–31.7 are complete; full user acceptance is pending the interactive A/B/E checks.

### Phase 31 — Automatic hold proposals: steps

Normative specification: DECISIONS.md D047. The phase changes no product code until approved; the steps below are the
planned implementation order after approval. Branch from `ad5a514` (Phase 30 technical closeout).

1. **Step 31.0 — Specification approval** (no code). The product owner approves D047; the phase number is assigned
   (Phase 31). Create the branch and the manual plan reference.
2. **Step 31.1 — Core proposal types and service interface** (no UI). Add `IHoldProposalService`, `HoldProposal`, the
   proposal pipeline (grid → predict → filter → NMS → rank), contour normalization and centroid calculation. Implement
   with a fake backend for unit tests. No changes to `project.json`.
3. **Step 31.2 — Proposal generation at downscaled resolution**. Integrate with the existing decoder and
   `OnnxHoldSegmentationService`; decode the markup frame at 1920×1080 for the proposal pass, run the encoder once at
   1024×1024 via `SamImageProcessing.Preprocess`, probe the 15×15 grid, filter with `HoldCandidateRules`, suppress
   duplicates efficiently (spatial index or downscaled-mask IoU, not O(n²) full-resolution mask-IoU), rank by score.
   Add cancellation and progress reporting.
4. **Step 31.3 — Full-resolution refinement** (complete 2026-10-26). For accepted proposals, decode the full-resolution
   frame, re-prompt at the normalized centroid, keep the best mask, trace and normalize the refined contour, take
   `SurfaceLab` / `WallLab` samples at full resolution, create a `Hold`. If refinement fails for a proposal, report it to
   the user and do not create an incorrect `Hold`. Implemented in `IHoldProposalRefiner`/`HoldProposalRefiner` with unit
   tests and a single-frame real-model smoke test (~6.75 s on one 4K frame).
5. **Step 31.4 — Proposal review UI / ViewModel** (complete 2026-10-26). Inspector panel: ranked list of proposals,
   accept / reject / accept-all / reject-all, default top-30 with 10/20/30/50/100 chooser, keyboard shortcuts (Enter, Delete,
   Tab, Shift+Tab, Ctrl+A, Ctrl+D, Esc), visual selection distinct from confirmed holds, switch to manual Shift+click flow.
   Automatic full-resolution refinement on accept; accept and accept-all are one Undo step each; reject / reject-all are
   not Undo steps. Implemented with tests and D039 localization.
6. **Step 31.5 — Integration with manual D034 flow and project mutation** (complete 2026-10-26). Entering markup mode on a
   clip without holds offers proposals; re-entering a clip with holds skips generation. Rejected proposals are not persisted
   and cancel during generation leaves the project unchanged. `HoldMarkupRules.MaxHolds` is enforced. Covered by integration
   tests.
7. **Step 31.6 — Manual test plan and quality check** (complete 2026-10-26). A headless real-model surrogate
   (`tests/Vision.ModelTests/Phase31ManualSurrogateTests.cs`) ran on an E1 4K clip and a 1080p clip; it measured proposal
   pass wall time and verified accept-all, Undo, persistence and the default top-30 behaviour. Profiling showed the
   prediction loop (ONNX decoder + upsample + contour extraction) dominates. Decoder batching is not viable because the
   exported SAM 2.1 decoder treats a multi-point prompt as one prompt and returns only three masks total. Grid-size
   experiments on the E1 set at 1920×1080 showed 15×15 is faster than 16×16 and improves recall (53.7 % vs 51.8 %),
   so the default grid was changed to 15×15. The ≤8 s criterion is met for both 4K and 1080p on the development machine;
   the ≤5 s aspirational is not met on 4K but was non-blocking. Interactive checks (A/B/E in
   `docs/PHASE31_MANUAL_TEST_PLAN.md`) need the product owner's own footage and manual UI actions and are recorded as
   NOT RUN.
8. **Step 31.7 — Final verification and closeout** (complete 2026-10-26). `dotnet build AiVideoEditor.sln -c Release
   -warnaserror` 0/0; full test suite green (0 failed, 2 skipped opt-in 4K `ExportParityScaledTests` without
   `AIVE_HEAVY_TESTS=1`); `project.json` v9 byte-for-byte round-trip covered by the surrogate; Preview/export parity
   unchanged (accepted proposals become normal D034 holds); D039 localization for all new user-visible text;
   `docs/PHASE31_MANUAL_TEST_PLAN.md` and `progress.md` updated. Interactive A/B/E checks remain NOT RUN pending the
   product owner's own footage and manual UI actions.

Gates before the phase is declared complete: build `-warnaserror` 0/0; affected tests green; Preview ↔ Export parity and
every expected value unchanged; a v9 project saved byte for byte; every new user-visible text in `Strings` / `Messages`
has a `.ru` entry (D039); `docs/PHASE31_MANUAL_TEST_PLAN.md` and `progress.md` updated.

### R2 → E — Outlines following the holds while the camera moves *(research — the highest uncertainty, then AI large)*
- R2: camera motion on real gym videos (hand-held, pans, zoom); approaches (feature tracking and a homography of the wall
  plane, optical flow, re-segmentation per frame); accuracy, jitter, cost per frame, behaviour on occlusion by the climber;
  how the result would be stored (per-frame transforms bound to the source time) and its format impact; a go / no-go.
- E (on a go): the holds' outlines follow the camera in the Preview and the export identically; a manual correction when
  the tracking is wrong; D036's animation unchanged on top of it.
- **Status (recorded 2026-10-27, not a completion mark).** What E means in this plan: the implementation, "on a go", of
  the three points of the E bullet above — (1) the outlines follow the camera identically in the Preview and the export,
  (2) a manual correction when the tracking is wrong, (3) D036's animation unchanged on top. R2 itself was done as research
  (`docs/PHASE_HOLD_TRACKING.md`; the owner's decisions R-1…R-9 there, including "occlusion a separate later stage").
  - **Confirmed**: (1) — merged into `main` in PR #31 (`024f3fb`), covered by tests (the shared draw plan by the shown frame's
    PTS, the export preflight waits for a running analysis) and by the owner's check in the live Release app on
    2026-10-27 (outlines follow the camera on a real clip, the export completed); (3) — the animated route of the owner's
    check was tracked, D036 unchanged.
  - **Partly**: (2) — the minimal correction only: "Follow the camera" off / on per clip and "Retry" after a failure, as
    session state; no correction by key frames, nothing persisted across sessions (both need stored data — a format
    decision).
  - **The owner's decision (2026-10-27): the current implementation is accepted for continued development; E is not
    marked complete.** (1) confirmed by the tests and the manual check; (2) the current manual correction is enough to
    continue — a richer correction (key frames, persisted state) and adding / removing holds on the tracking are
    **deferred until needed**, not implemented; (3) the animation over the tracking accepted; (4) the two manual checks
    NOT RUN (an export during a running analysis, "Retry" after a real failure) do not block and are **deferred** — taken
    up again when a slow machine or a real blocking-message scenario appears. Recorded in D048 §1.17 (proposed). Occlusion
    was part of the R2 concept, not of E's three points, and stays a separate stage.
- **Concept: tracking and occlusion of the hold outlines** *(product owner, 2026-10-22; a direction for R2 / E — not a
  specification, not a decision, no step; out of Phase 30, D046 §9)*:
  - **What exists (checked in the code on 2026-10-22).** The user enters the markup mode on a paused frame, the frame
    under the playhead is segmented (D034), a click gives a candidate contour, Enter confirms it; the holds of the route
    are marked one after another (each `Hold`: a normalized outer contour and the `SourceTime` of the frame it was
    marked on); D035 corrects a confirmed contour's vertices; D036 animates the outlines (the holds appear one by one
    every *Interval*, default 0.5 s, 0.05–10 s, then stay 3 s). A confirmed contour is drawn at the same normalized
    place on every frame of the clip: it follows the layer's transform and crop, not the camera — there is no tracking.
    **Not confirmed:** a processing interval of "about 5 s" set by the user — no such setting is found in the code or
    the decisions (the nearest: D036's *Interval*, and "about 5 s of the chosen holds" of the automatic rough cut, D042,
    not a function); to be clarified with the product owner.
  - **A continuation of that scenario**, not another way of choosing holds: the user keeps choosing and confirming holds
    as in D034 / D035; tracking adds where each confirmed hold is on the other frames. The contour is not assumed to
    keep the coordinates of its marking frame.
  - **Three separate tasks.** *Hold tracking* — the position and the geometry of the hold over time, from its marking
    frame. *Occlusion estimation* — internally, which parts of the hold the climber, a hand or another object covers on
    a frame. *Visible contour generation* — from both, the segments of the hold's own contour that are visible on that
    frame.
  - **Occlusion hides only the covered segments of the contour**; the visible ones stay drawn and a covered segment
    reappears when it is uncovered. Never the whole outline hidden because a part is covered, never a line along the
    occluder's silhouette, never the outline of the visible area instead of the hold's. Example: a climber covers the
    right half of a hold — the right part of its contour is hidden, the left part stays.
  - **Internal data are not the result.** Masks of the hold and of the occluders may be intermediate data; they are
    never shown as a fill, a translucent mask or a separate exported object. The user sees only the hold's contour, on
    its visible segments, and the Preview and the export draw the same segments through the one outline path of D034 §6.
    How the visible segments are computed is not fixed.
  - **To research:** less jitter of the contour between frames; no flicker at the edges of an occlusion; robust
    following while the camera moves; a manual correction of the position / contour on key frames carried to the
    neighbouring frames; processing a chosen time range rather than necessarily the whole video.
  - **To assess before an implementation:** local work with no required cloud; the size of models and new dependencies;
    performance on the CPU and GPU acceleration; the licences of libraries and models; the Preview / export consistency;
    the format impact (data per frame bound to the source time).
  - The technical approach is chosen at a separate research stage (R2) after the stabilisation; no experiment, test
    footage or comparison of algorithms is started before it. "Occlusion" here is a cover inside the picture — unrelated
    to the layer occlusion of D018.

### D048: route selection, intro / final timing and the overlay strip *(approved 2026-10-28 as implemented; stage 1 paused / deferred; strip variant B; the text below written before the approval)*

The target scenario: `docs/TARGET_WORKFLOW.md`; the decision draft with the specification, the acceptance criteria, the
risks and the open questions: DECISIONS.md D048. Nothing here starts before the owner approves D048.

- **Stage 1 — route selection at `T` and review** *(proposed Phase 32; AI medium; no format change; needs D047 and the hold
  tracking, both in `main`)*: the user on frame `T` (the clean frame just before the climb) marks a control hold and
  presses "Place holds" → the clean frame at `T − 5 s` (timeline time, mapped through speed and trim to the source) → the
  control point carried by the camera-motion estimator (an unreliable transfer is an error, no markup) → colour
  classification on the clean frame → the D047 review with a tolerance → accept (one Undo step) → back to `T`. Three
  separately tested parts (locator, transfer, classification). No overlay interval change, no timer, no layout.
- **Stage 2 — the timing model, the template ↔ clip relation and `project.json` v10** *(proposed Phase 33; core
  medium–large)*: an explicit animation duration (`Duration` mode beside D036's `Interval` mode) with the 80 % appear /
  20 % whole-route split and its exact per-hold formula (D048 §3.2), the orders left to right / right to left, the standard
  layout as a **separate explicit action** (plate `T − 7 s`, animation `T − 5 s … T`, Top / Attempt the last 5 s) with the
  2 : 5 short-intro rule and its frame-exact edge cases (the lilac never before `T` — shorter than 5 s when `T` lies in the
  clip's last 5 s, none with no frame after `T`), the template ↔ clip relation (option A:
  `TemplateClip.AnchorClipId` — confirmed by the owner with its operation table: move and ripple carry the template,
  delete removes it, split per D048 §3.6), the speed / extension / trim rules (D048 §3.8: a speed change scales every
  linked boundary with the clip's duration, a too short green kept at one frame, a too short orange or lilac deleted, a
  conflict on the template's track refused as a temporary limitation; a boundary change of a clip or a split fragment
  changes no overlay by itself — the protective rules apply only to segments a trim cuts into, as exceptions to that
  rule; a template with no frame left in the clip is deleted for it (the markup only by its own rule): a head trim into
  the green clamps its start (one frame may remain), into the orange shortens the rest proportionally (no 2 : 5
  rebuild), reaching the lilac deletes only the lilac; a tail trim into the orange deletes the whole markup, the lilac shifts with its length
  kept and only the lilac is deleted on a conflict with the green or the orange, the green kept even at one frame), Delete
  of the orange = the whole markup, «Анимировать» off ≠ Delete (§3.9), the copy / delete / Undo rules
  (§3.10: a whole clip or a split fragment, independent copies anchored to the new clip, no range copy; one Undo restores
  a clip with its overlays; the derived tracking may be analysed again), the split rule for `Duration` animations (the
  markup goes with the animation's start; a cut inside the orange deletes it), **no** magnetic track or cascade and no
  change of the existing conflict rules (§3.11 — a future compatibility requirement only), the v9 → v10 migration (v9
  renders unchanged and is converted on save; no compatibility with older builds, no v10 → v9) and a compatibility check
  with Phase Coros' schema before the implementation.
- **Stage 3 — the overlay strip** *(proposed Phase 34; UX medium; needs stage 2)*: one strip per video clip on a thin
  lane directly above its row (the prototype's direction) — green (plate) → orange (animation, over the green) → green →
  lilac (Top / Attempt) — as a view of the existing entities; edge and body drags mapped to the owning entity's edits with
  the limits and overlap rules of D048 §4.2 (the green segment not movable as a whole; a lilac edit that would overlap the
  green or the orange, and an orange edit that would overlap the lilac, refused with a message — an invariant checked
  in the edit service for every manual path: the strip, the template clip's own move / trim, the Inspector's status and
  animation fields; an anchored template kept inside its clip); the
  orange segment selectable and deleted by Delete (D048 §3.9: the whole hold markup deleted, «Анимировать» off, one Undo
  restores both); a shared left boundary of green and orange taken by the green first; one drag = one Undo step, none
  when back at the start; Preview = export; v10 persistence.
- Order: stage 1 is independent of the format and may start first; stage 2 may run in parallel with stage 1 but before
  stage 3; stage 3 after stage 2's model and anchor are approved and merged. Every stage keeps the standing rules (one Undo
  step per action, Preview = export, `MediaTime` ticks, the D039 localization, the gates).

### R3 → F — «COROS Pace 4: пульс и телеметрия в видео» *(research, then telemetry large)*

The original idea of the project (D042 §6): heart-rate data from a COROS Pace 4 watch, synchronised with a bouldering video
and shown in the export. Only COROS Pace 4 in the first version; no Garmin, Apple Watch or other devices in this scope; the
data from an exported training file, no direct connection to the watch or a cloud service. **The synchronisation by
timestamps is not technically solved** — R3 decides whether and how it works.

R3 — research (no product code):
1. Real COROS Pace 4 exports of climbing sessions: which formats the ecosystem offers; FIT the preliminary direction, TCX the
   fallback — **not final until real watch exports and a video of the same session are examined**; confirmed or changed
   on the real files; how a FIT reader is obtained (an own minimal reader or an SDK — a new
   dependency only with a justification, its licence checked).
2. The heart-rate data: the timestamps (absolute time, time zone), the sampling interval, gaps and pauses, the start and
   end of the recording.
3. The video side: which timestamps the product owner's phone footage carries (creation time in the container, its
   meaning — the start or the end of the recording —, its precision, time zone), whether ffprobe exposes them, the clock
   difference and drift between the phone and the watch over a session.
4. A specification for the product owner: the automatic match and its expected accuracy, the correction the user needs
   (an offset; more only if drift is shown), the overlay's options, the format impact, the tests.

F — implementation (after R3's go and A):
1. The import of a COROS Pace 4 training file in the format chosen by R3, with its validation and messages (a damaged or
   foreign file refused with a reason).
2. The synchronisation with the video's timeline — preferably from the timestamps, bound to the clip's source time (a design
   hint to be confirmed: as the holds, D034 / D036, so Trim / Split / Move keep the match) — and the correction of the
   offset; one Undo step per change.
3. The overlay: the numeric value, a graphic (e.g. a small curve or a bar) and a simple animation; its look and position
   chosen from a few options; the Preview.
4. The export rendering through the one composition path (Preview = export).
5. Tests: the synchronisation (automatic and corrected), seeking, Trim / Split / Move, the data's gaps, the start and the
   end outside the recording, an incomplete or damaged file, persistence and Undo / Redo.
6. A manual check on a real gym video with a real Pace 4 export.
- Out: other devices; a sports-telemetry editor; maps, GPS, pace and other fields unless R3 justifies one; a direct
  device or cloud connection.

### H — «Final UX Pass: аудит и упрощение интерфейса» *(research, then UX medium–large; the last large phase)*

After the planned core (including F), G and a regression run of the main path. Goal: not more functionality — the
existing functions simpler and faster to use (D042 §7). Obvious UX defects found earlier are fixed when found and do not
wait for H.

Research:
1. CapCut, DaVinci Resolve and Kdenlive: official materials and demonstrations, real user scenarios, user reviews and
   discussions of video-editor UX problems.
2. Concrete scenarios compared, not the looks: selection, moving and trimming clips; the timeline; the Inspector; numeric
   fields and sliders; hotkeys; context menus; text and animation; transitions; music; the export; the AI hold markup.
3. The findings matched against this product, its architecture and its audience; no interface copied without an
   assessment of its use.
4. A prioritised list of about 10–15 justified improvements, each with its benefit, complexity and risk, for the product
   owner's choice.

Implementation — only the chosen improvements that reduce the actions of the typical scenarios, make the behaviour more
predictable, make the editor easier to learn, improve the handling of holds and their outlines, and do not turn the editor
into a professional editing system.

Success criterion: a user without deep video-editing experience goes from importing a source to a finished bouldering
video without learning a professional editing system (checked with the product owner on the main path).

### Deferred, not blocking a release

FU-21.2 (markers bound to a clip); dragging on labels (FU-24.1's rest); the magnetic main track (D037 §5); a blurred
background for vertical video (D041 §4); the technical-debt split of `TimelineEditService` / `TimelineViewModel`; the open
manual checks — R10 (a real audio device, Phase 17), the product owner's visual check of the S1 HDR export and PQ on real PQ
footage (Phase 18), the NOT RUN rows of Phases 23 and 25; an installer (left out by the product owner). Not planned (D042
§3): keyframes, speed ramps, freeze frame / reverse, ducking, multicam, an audio studio.

### Long-term direction: the automatic rough cut *(not a phase; DECISIONS.md D042 "Recorded later", 2026-10-20)*

An editable rough cut of a training assembled from the video and, later, the COROS Pace 4 export: the watch's events (only
the data that really exist) structure it, routes missing from the video are left out, about 5 s of outlined holds then the
attempt per route, the heart rate as an animated heart with the BPM, grade / result / flash from the user's data, local AI,
the user polishing the result. No specification and no change of the order above; it needs R3 → F, D and its own
decision. R1, R2 and R3 do not by themselves authorise D, E or F.

## Future ideas (not phases)

Recorded so they are not lost. None of them is a phase, has acceptance criteria or is approved for implementation.

### Hold contour correction — the next product stage *(proposal, 2026-10-15; taken up as Phase 20, DECISIONS.md D035 — the sketch below kept as history; D035 differs: dense contours keep every vertex, the handles are only a display subset)*

The product owner's priority: as few actions as possible — show the model's best mask at once and, when its boundary is
wrong (a large mask that took part of the wall, a part of a hold, a printed logo), let the user correct it directly
instead of cycling alternatives. Not in Phase 19; needs a D034 extension (a new gesture and edit) before implementation.

Minimal variant — editing the vertices of a confirmed hold's contour in the markup mode:
- **Gestures.** A click on a confirmed hold selects it (exists); its contour shows handles at its vertices. Drag a handle
  to move a vertex; Alt+click on an edge inserts a vertex there; Delete (or a double-click on a handle) removes the vertex
  under the pointer — at least 3 stay; Esc ends the edit. A self-intersecting result is refused with a message. The
  contour comes from the mask simplified with Douglas–Peucker at 1 px, so a large hold has dozens to hundreds of
  vertices; for handles the edit view shows a coarser simplification (e.g. ≈ 0.5 % of the hold's size) and edits that
  polygon.
- **Storage.** No mask is stored today — a hold is its normalized outer contour plus `SurfaceLab`, `WallLab` and
  `SourceTime`; an edit replaces the hold's `Points` under the same id and the same limits (3–8192, inside [0, 1]). The
  colour samples stay as taken at the confirm (D034 §5: never recomputed from a frame; the route colour changes only
  when holds are added or removed), so an edit never changes the route colour. `project.json` stays v5 (same members);
  an optional "edited" flag only if the product needs it.
- **Undo / Redo.** One edit operation per gesture: a drag, an insert or a delete is one `SetHoldMarkupCommand` (fields
  Holds, before / after markup) through `ITimelineEditService` — a new `ReplaceHold(clipId, holdId, points)` with the
  same validation and locked-track / export refusal as `AddHold`; consecutive drags are not merged, so each can be
  undone. Split / Duplicate / Paste copy the edited contour like any other.
- **Preview / export parity.** Unchanged by construction: both draw the stored polygon through the one
  `CompositionDrawPlan.OutlineDraw` path; the edit handles are an overlay of the markup mode only (like the candidate and
  the loupe), never in the export.
- **Tests.** The command's undo / redo and validation, the polygon limits and self-intersection, the overlay's mapping of
  a dragged handle through crop and rotation (the existing `HoldMarkupOverlay` mapping), parity of an edited hold in the
  rendering tests; a manual check on the owner's close-ups with the known wrong large masks.

### Route animation after the AI hold markup *(idea, 2026-10-14; taken up as Phase 21, DECISIONS.md D036 — the sketch below kept as history; D036 decides the open points: the start bound to the source time, Bottom to top by default, the fade parameters, no grouping)*

Once the holds of a route are marked (the AI hold markup, DECISIONS.md D034 — accepted, not implemented yet), the user
can switch on an animation of the holds appearing over the video:
1. At the start all hold contours are hidden.
2. The holds appear one after another at a configurable interval (e.g. 0.5 s).
3. A hold that has appeared stays visible while the next ones appear.
4. After the last hold all contours stay visible for a set time (e.g. 3 s).
5. Then all contours disappear at once; a fade-out may be a parameter of the implementation.

Showing several holds as a group at the same time is desirable but not required for a first version, which may show
one hold at a time with one common interval.

Boundaries: a separate future function, not part of the AI hold markup MVP, whose scope it does not change. Keeping the
contours on the holds while the camera moves is a separate motion compensation / tracking task, not a requirement of
the MVP. Open, to be decided with the function: the order in which the holds appear, the moment the animation starts,
the fade parameters, the grouping.

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

### 16.7 — Final verification & closeout *(done 2026-10-08 — results in `progress.md` and `docs/PHASE16_MANUAL_TEST_PLAN.md`; PR #17 merged as `79e4e78`, CI green on the pull request (run 37776542826) and on `main` (run 37778354822))*
- QG: `dotnet build --no-incremental -warnaserror` Release and Debug 0 / 0; the full suite once plus three times with
  `--blame-hang`; the heavy scenes once with `AIVE_HEAVY_TESTS=1`; `git diff --check` clean; an independent phase review.
- PR: `docs/PHASE16_MANUAL_TEST_PLAN.md` in the real app, with the regression (a Phase 15 project round trip, export
  unchanged, Phase 15 tools, no ffmpeg).
- Documentation: ARCHITECTURE, D031 refinements and status, ROADMAP, README, `progress.md`, this plan's checkbox.

## Phase 17 — Audio editing: steps (D032)

Formalized in Step 17.2 (product owner decisions of 2026-10-08: variant A, L1–L7; the technical rules R1–R13 from the
pre-implementation audit). The normative rules are D032; labels as in Phases 9–16 (**PR**, **QG**, **M**, **Impl**). Push,
pull request and merge only on a separate command. AQ1–AQ13 answered by the product owner (2026-10-08, as proposed); the
whole phase runs without a stop between its steps.

Goal: edit the sound of a recording apart from its picture — unlink, link, J / L cuts, sound-only insert, crossfades —
with the Preview and the export identical and every edit one undo step (D032 §1).

Gates for every step 17.3–17.8 (QG): `dotnet build` 0 errors / 0 warnings (`-warnaserror`); the full `dotnet test` green
(only the two 4K heavy scenes skipped); the Preview ↔ Export parity suite, the L1-c criteria (D028 §6) and every existing
expected value unchanged, except the D032 §5 exceptions changed in their step with the reason; `project.json` v4 per
D032 R2 (a Phase 16 v3 project opens, renders and exports exactly as before and saves as v4 differing only in
`formatVersion`); every new project command one undo step, exact on Undo / Redo, dirty / clean by the save point (D015),
refused on a locked track — a locked **partner** included (L5) — and while an export runs (`EditingLock`), a refused or
unchanged command leaving no undo step; no ffmpeg in an edit; each rule of D032 §3 that the step touches covered by a test
that a mutation of the rule would fail; new controls checked in the real app at 1024 and 1440 px;
`docs/PHASE17_MANUAL_TEST_PLAN.md` completed for the step; `progress.md` updated.

Out of scope: D032 L7 and §6.

### 17.1 — Pre-flight *(done 2026-10-08)*
### 17.2 — Sync after the merge, audit and scope formalization *(done 2026-10-08)*
- Documentation only: the Phase 16 / PR #17 merge recorded (ROADMAP, README, this plan, D031, `progress.md`), the
  pre-implementation audit of the audio / `VideoClip` / `EditPlan` model, D032, this section, the manual plan's skeleton.

### 17.3 — Model and format v4 *(done)*
- PR: D032 R1, R2: `Clip.LinkId`, `VideoClip.IsAudioDetached`, `AudioClip` of a video asset, the `"crossfade"` type on
  audio tracks; `project.json` v4 written, v1–v4 read; v4 load validation (links, kinds, crossfade zones).
- Impl: `TimelineValidator`, `TransitionRules` (type per track kind) and `DissolveHandles` (D025 §4 for `AudioClip`)
  updated; nothing renders the new properties yet beyond R10's "detached → no sound" if needed by the tests.
- QG: `Project.Tests` — a v3 file (every Phase 10–16 property) opens unchanged in meaning and saves as v4 differing only
  in `formatVersion`; v4 round trip tick for tick; damaged v4 files (a link of one clip, two video clips, the same track,
  a crossfade on a video track, a dissolve on an audio track, an audio clip of an image) refused; a v5 file refused with
  the existing "newer version" message. The tests pinning `formatVersion` 3 on save changed to 4 (D032 §5).

### 17.4 — Unlink Audio, Link, Unlink: core and sound *(done)*
- PR: D032 R3, R4, R10 (detached sound), R13; AQ8, AQ9, AQ11, AQ12 (track delete clearing links).
- Impl: new commands in a `partial` file of `TimelineEditService`; `EditPlan` link reconciliation (R5) introduced here;
  `PlaybackSnapshotBuilder`: a detached `VideoClip` adds no `AudioSpan`.
- QG: `Timeline.Tests` (every refusal, Undo / Redo exact, the target track and the new-track case, mute copy);
  `ExportEndToEnd.Tests`: the export before and after Unlink Audio byte for byte where no other sound overlaps (1×, a
  non-1× speed, fades, a dissolve on the video track per AQ11).

### 17.5 — Linked editing *(done)*
- PR: D032 R5, R6; AQ1–AQ7, AQ12, AQ13: move, move to a track, trim and the one-side trim, Q / W, ripple trim, slip, split, speed,
  delete, ripple delete, close gap, Insert / Overwrite ripple partners, copy / paste / duplicate, track delete and media
  removal clearing links; a locked partner refuses the whole edit; a partner that can't follow refuses with a message.
- Impl: the link rule added to the existing planners through one shared helper; no planner's rule for a single clip
  changes (the existing tests stay as they are).
- QG: `Timeline.Tests` per edit — linked vs. unlinked results compared clip for clip with the same edit applied to each
  clip; the locked partner for every edit kind; J / L cut shapes; Undo / Redo exact; mutations of the partner rule caught.

### 17.6 — Audio crossfade and audio fades *(done)*
- PR: D032 R8, R9, R10: Add / Remove / Duration of a crossfade on an audio cut (1 s or the longest that fits), every edit's
  reconciliation (D025 §5), the mix of the zone in the Preview and the export, a fade suppressed on a crossfade edge.
- Impl: `AudioSpan` extension + ramps, `AudioPlacement` / `AudioFadeEnvelope` / `AudioMix` shared by `AudioSpanReader` and
  `ExportAudioReader`; every place that uses a span's own bounds honours the extended range — `AudioPipeline` (reader key
  and windows), `ExportAudioSource`, `ExportPreflight` (`window.Plays`, range exports); `AudioFadeEnvelope` keeps the
  clip's own fade bounds.
- QG: `Core.Tests` (the ramp, the sample bounds, the zone split), `Timeline.Tests` (edits and reconciliation as the D025
  dissolve tests), `UI.Tests` (playback mixer through the seam), `ExportEndToEnd.Tests` — the exported PCM of a crossfade
  equals the reference formula sample for sample, and the Preview's mix of the same zone equals the export's; audio fades
  of an audio clip of a video asset.

### 17.7 — Sound-only Insert / Overwrite *(done)*
- PR: D032 R7; AQ10, AQ13.
- QG: `Timeline.Tests` (the D031 SQ6 / SQ9 shapes for an `AudioClip` of a video asset, partners rippled per AQ4);
  `UI.Tests` (the Source bar toggle, the target track); an export test: a sound-only insert exports like the same timeline
  built with Unlink Audio + delete of the picture.

### 17.8 — UI *(done; the real-app check at 1024 / 1440 px in the closeout run)*
- PR: D032 R12, the crossfade UI of R9, AQ1, AQ3, AQ9: the Unlink Audio / Link / Unlink buttons, link marks on the
  clips, the linked selection, Alt + edge drag, the "Dissolve" command adding a crossfade on an audio cut, the Inspector
  for a crossfade and for a detached video clip, no waveform on a detached video clip.
- QG: `UI.Tests` for the view models and gesture modifiers; real app at 1024 / 1440 px.

### 17.9 — Final verification & closeout *(done 2026-10-12 — results in `progress.md` and `docs/PHASE17_MANUAL_TEST_PLAN.md`; the 1024 px toolbar overflow found and fixed)*
- QG: `dotnet build --no-incremental -warnaserror` Release and Debug 0 / 0; the full suite once plus three times with
  `--blame-hang`; the heavy scenes once with `AIVE_HEAVY_TESTS=1`; `git diff --check` clean; an independent phase review.
- PR: `docs/PHASE17_MANUAL_TEST_PLAN.md` in the real app, with the regression (a Phase 16 project opened and saved as v4,
  the default export byte for byte, the Phase 15–16 tools, no ffmpeg) and R10 with a real audio device unplugged and
  plugged back (D030).
- Documentation: ARCHITECTURE, D032 refinements and status, ROADMAP, README, `progress.md`, this plan's checkbox.

## Phase 18 — HDR → SDR of phone footage: steps (D033)

Formalized in Step 18.2 (product owner decisions of 2026-10-13: variant C, HQ1–HQ6, the L2 chain, a LUT generated by the
application at 65³, superwhites kept to 1.1, PQ in scope). The normative rule is D033 §3; labels as in Phases 9–17 (**PR**,
**QG**, **M**, **Impl**). Push, pull request and merge only on a separate command; builds and test runs on the product
owner's command. The branch is created when the implementation starts.

Goal: HDR footage from phones looks right in the Preview, the thumbnails and the export — the same picture everywhere,
SDR footage unchanged (D033 §1).

Gates for every step 18.3–18.7 (QG): `dotnet build` 0 errors / 0 warnings (`-warnaserror`); the full `dotnet test` green
(only the two 4K heavy scenes skipped); for SDR media the decoder's arguments byte for byte as before, and the Preview ↔
Export parity suite, the L1-c criteria (D028 §6) and every existing expected value unchanged; `project.json` v4 (D033
HQ2, T2: only optional metadata properties; a Phase 17 project opens and renders as before); no new undoable command and
nothing new that makes a project dirty (D033 T6); each formula and boundary of D033 §3 that the step touches covered by a
test that a mutation of it would fail (D033 §5); new UI checked in the real app at 1024 and 1440 px;
`docs/PHASE18_MANUAL_TEST_PLAN.md` completed for the step; `progress.md` updated.

Out of scope: D033 §7.

### 18.1 — Pre-flight *(done 2026-10-13)*
- `origin/main` = `fa96468` (PR #18, tree = `1564c02`); candidates A keyframes, B on-canvas manipulation + text styling,
  C HDR → SDR, D freeze / reverse, E stabilization; C chosen. Inventory of the product owner's 13 HDR files, the
  comparison of tone-mapping variants on 5 clips, the zimg OOTF finding, the LUT accuracy, the superwhites and the PQ
  feasibility — D033 "Context".

### 18.2 — Sync after the merge and scope formalization *(done 2026-10-13)*
- Documentation only: the merge of PR #18 (ROADMAP, README, this plan, D032, `progress.md`), the ROADMAP's undefined
  stabilization items removed (D032 "Context"), D033, this section, the manual plan's skeleton. Local `main`
  fast-forwarded to `fa96468`.

### 18.3 — Colour rule and LUT generator (Core) *(done — `bb7da2e`)*
- PR: D033 §3 as pure functions: HLG inverse OETF / OETF, the BT.2100 OOTF on luminance, PQ EOTF / inverse, BT.2020 →
  BT.709, the gamut compression, mobius on max RGB, inverse BT.1886; the PQ peak rule; the LUT generator (65³,
  `DOMAIN_MAX 1.1`, the `.cube` text, a key of transfer + `Lpeak` + rule version).
- QG: `Core.Tests` per D033 §5 "Core", "LUT domain and boundaries" (< 0, 0, 1.0, 1.0–1.1, 1.1, > 1.1; HLG and PQ),
  "PQ peak" and "LUT generator" — with a double-precision reference in the test project, the LUT's nodes equal to it,
  two generations byte-identical.

### 18.4 — Colour metadata *(done — `a5a6d2f`; review notes taken in at 18.5)*
- PR: D033 T1–T3: ffprobe colour properties, bit depth, mastering / content light level, Dolby Vision / CUVA presence in
  `MediaMetadata`; the optional `MediaMetadataDto` properties (the pixel format always written as the "colour probed"
  marker); the background probe of older metadata (not dirty; a failed probe → SDR as in Phase 17); the
  Inspector's colour line (e.g. "HDR · HLG · BT.2020 · 10-bit · Dolby Vision 8.4 base layer"; unsupported variants
  named).
- QG: `Project.Tests` (round trip, a v4 file without the properties unchanged, a Phase 17 save read; written only when
  known); `Video.Tests` with synthetic fixtures generated by ffmpeg / libx265 (HLG; PQ with and without mastering / CLL;
  SDR); `UI.Tests` for the Inspector line; the re-probe path.

### 18.5 — Decoder and LUT cache *(done — `0532e76`; full suite 3147 / 2 / 0)*
- PR: D033 §3 "Graph", T4, T5: the HDR graph in `FfmpegVideoDecoder.BuildArguments` (even sizes, the pre-LUT `zscale`,
  `lut3d` tetrahedral), the SDR graph unchanged; the LUT cache (generated once per key, atomic, the path escaped for the
  filter graph); hardware decoding through the same filters; `ThumbnailService.CurrentRuleVersion` 2; the Preview and the
  thumbnail refreshed when an asset's colour becomes known; the export preflight blocking only while a used asset's
  background probe is pending or running (T3); OQ1 (the unsupported-variant warning) as answered.
- QG: `Video.Tests` — golden arguments (SDR byte for byte, HDR as specified, portrait bounds even); HLG and PQ fixtures
  decoded within ΔE76 ≤ 1 of the reference, neutral patches neutral; the LUT cache (one file per key, concurrent first use,
  a broken file regenerated); `Export.Tests` for the preflight rule.

### 18.6 — Parity, export and measurement *(done)*
- PR: the Preview, the thumbnail and the export of an HLG and a PQ scene equal on flat patches within 1 LSB; SDR output
  unchanged.
- QG: `ExportEndToEnd.Tests` (HLG, PQ scenes; the existing scenes unchanged); M: in-app decode / export throughput of a 4K
  HLG clip (software and hardware) recorded, no pass / fail (HQ6).

### 18.7 — UI *(done; the real-app check in the closeout run)*
- PR: the Inspector's colour line placed and checked at 1024 / 1440 px; no switch (HQ4).
- QG: `UI.Tests`; real app.

### 18.8 — Final verification & closeout *(done 2026-10-13 — results in `progress.md` and `docs/PHASE18_MANUAL_TEST_PLAN.md`; the relative LUT folder found and fixed; open: the product owner's visual check of an exported HDR clip)*
- QG: `dotnet build --no-incremental -warnaserror` Release and Debug 0 / 0; the full suite once plus three times with
  `--blame-hang`; the heavy scenes once with `AIVE_HEAVY_TESTS=1`; `git diff --check` clean; an independent phase review.
- PR: `docs/PHASE18_MANUAL_TEST_PLAN.md` in the real app with the product owner's footage (the five comparison clips) and
  the regression against the Phase 17 build (an SDR project's default export byte for byte; a Phase 17 project opened and
  saved — only the optional colour properties added; a Phase 18 file in the Phase 17 build); the product owner's visual
  check of an exported HDR clip on an SDR screen.
- Documentation: ARCHITECTURE (the colour rule, the LUT cache, the decode graph), D033 refinements and status, ROADMAP,
  README, `progress.md`, this plan's checkbox.

### Revision R1 — the colour rule S1 (D033 Revision R1) *(approved 2026-10-13, docs `1e07a73`; 18.9 criteria refined 2026-10-14; Steps 18.9–18.12 done 2026-10-14)*

The product owner's visual check of the L2 export failed; after the measurement series the product owner chose S1
("Telegram-match-sat100", D033 §3 as revised). The steps below replace the rule and only what depends on it, on the
same branch `feat/phase-18-hdr-sdr`. The gates of 18.3–18.7 hold for every step; in addition: for SDR
media nothing changes (the decoder's arguments byte for byte, the parity suite, the goldens, L1-c, the SDR thumbnail
names); only HDR expected values move, each to a value computed from D033 §3 (S1) — never adjusted to whatever the code
produces. A step whose criterion fails stops and is reported, not loosened.

#### 18.9 — The rule S1 in Core *(done — `867952d`; criteria refined in `dae89b4`)*
- PR: `HdrToSdr` per D033 §3 (S1): HLG without the OOTF and without ÷ 203; PQ → the inverse BT.2100 OOTF on luminance
  at 1000 nit (γ 1.2); the exposure k = 0.69 · 2^−0.55 / 0.265; mobius j 0.375 with the peak k · P (HLG P = 1.64, PQ
  P = (Lpeak / 1000)^(1/1.2), skipped when k · P ≤ 1); no saturation step; the BT.709 OETF instead of the inverse
  BT.1886; `RuleVersion` 2; the LUT's `TITLE` names the rule. `HdrConversion`, the PQ peak rule, the matrix, RGC, the
  LUT generator's format and key unchanged.
- QG: `Core.Tests` per D033 §5 (R1) — every listed formula with a mutation it catches (an OOTF returned to HLG, the PQ
  inverse OOTF per channel or forward, j 0.3, the old peak, the inverse BT.1886, a saturation ≠ 1, k off by 1 %); the
  §3 check values (HLG 75 % → 172.58, 1.0 → 241.5, 1019 → 255; PQ 203 nit @1000 → 173.01, 400 nit @400 → 232.28, 1000
  nit @4000 → 234.69); the independent evaluation of §3 rewritten for S1; LUT nodes equal to the approved
  "Telegram-match-sat100" cube within 1·10⁻⁶ on a fixed sample of nodes (values copied into the test, not the 8 MB file).
- QG / M: the LUT accuracy measurement of D033 §5 "LUT accuracy, shadows and gradients" (R1) — HLG and PQ at `Lpeak`
  203 / 1000 / 4000; the application's `.cube` through ffmpeg `lut3d` tetrahedral on float input against `HdrToSdr` in
  double precision **on the identical float32 signal triplets** (R'G'B' BT.2020 full range, the LUT's own input; the
  `gbrpf32le` plane order mapped explicitly) and compared in the same space (R'G'B' BT.709 full range, S1 encoding,
  float); PQ samples are PQ signal values that both sides take through the EOTF → ÷ 1000 → inverse OOTF → S1 chain,
  each PQ LUT with its own `Lpeak`; the harness first reproduces the §3 check values on both sides. e in 8-bit LSB per
  channel; samples (a) black, (b) near-black neutral (every 10-bit input code up to rule output code 32), (c)
  near-black along six colour directions, (d) full neutral and coloured ramps up to the 1.1 domain; criteria C1 black
  exactly 0, C2 near black ≤ 0.5 LSB, C3 ramps ≤ 0.5 LSB neutral / ≤ 1.0 LSB coloured, C4 monotonic and neutral, and
  **C5 bands / jumps as an additional check — not instead of C1–C3**; plus real 4K crops ΔE76 ≤ 1.0 and synthetic
  saturated colours ≤ 1.5 (1.1 domain ≤ 2.0).
- Report of 18.9 (in `progress.md` and to the product owner), **separately for HLG and PQ at `Lpeak` 203 / 1000 /
  4000**: per criterion the maximum error, pass / fail and the coordinates of the worst samples (set, direction, input
  signal and 10-bit code, nit for PQ, V_rule and V_LUT per channel); the error curves.
- Stop rule: the BT.709 OETF's toe and the PQ inverse OOTF are steeper near black than L2's encoding; if any criterion
  fails, 18.9 stops before 18.10, and the measured results with the possible fixes (e.g. a larger LUT, a 1D shaper
  before the 3D LUT, another graph) and their costs are presented — none is chosen without the product owner.
- PQ specifics (D033 §3 "PQ in detail"): Core tests for the order EOTF → ÷ 1000 (not ÷ `Lpeak`) → inverse OOTF on
  luminance → the S1 steps; `Yd = 0` → black; the knee peak from `Lpeak`; content above `Lpeak` white; the `Lpeak`
  table (MaxCLL wins, the mastering maximum when MaxCLL is absent / ≤ 0, the 1000 fallback, the limits 203 / 10 000,
  rounding, an unreadable rational, a contradiction such as MaxCLL 4000 vs mastering 1000 or 300 vs 1000 taken by the
  order, never as an error — the metadata taken as stated) — the existing PQ-peak tests kept, the new rows added.
- The `Lpeak` 501 / 502 boundary (D033 §3): `k · P` 0.99977 → the knee skipped, 1.00144 → applied (a mutation moving
  the threshold or evaluating mobius at `peak ≤ 1` fails); the outputs at 400, 490, 500, 501, 502, 503 and 600 nit
  equal the §3 values for both settings (e.g. 501 nit → 254.97 / 254.79, 502 nit and above → 255 / 255); continuity —
  for every neutral input 0.05–1000 nit the two settings differ by ≤ 0.25 code (measured 0.18) and each is monotonic
  in the input; the same two cases through the LUT within the accuracy criteria.
- **Refined after the stop (product owner, 2026-10-14; D033 §5 "Refined after Step 18.9").** 18.9 stopped on C3 / C4
  and the 1.1 domain (HLG) and on C3–C5 / synthetic (PQ). Accepted for HLG with S1, the 65³ LUT and tetrahedral
  interpolation unchanged; the binding HLG criteria: C1, C2, C5, synthetic, 4K crops unchanged; C3 unchanged in smooth
  cells, ≤ 1.0 code only in LUT cells that cross the clip (a node with a pre-clamp channel ≥ 1); C4 — neutral strict, no
  8-bit reversal, the largest channel never decreasing, a non-largest channel's float drop ≤ 0.1 code; the 1.1 domain
  ΔE76 ≤ 2.0 kept (2.001 recorded as a borderline deviation in a clip cell); added: the real-footage bench (39 frames,
  three per clip of the 13 HLG clips, not every frame) max ΔE76 < 2.3 with the distribution recorded (measured 1.65, e >
  2 codes 0.014 %) — a bench threshold, not a guarantee of invisibility; the visual check stays in 18.12. PQ: the bench
  results (synthetic, PQ derived from HLG) recorded, not passed; PQ not verified on real footage (D033 §8). No other
  option (a larger LUT, another curve) taken.

#### 18.10 — Decoder, cache keys, thumbnails *(done — `a09efb8`)*
- PR: the decode graph unchanged except the LUT file name (`hdr-r2-…`); the LUT store's start-up clean-up removes the
  rule-1 files (existing behaviour); an HDR thumbnail's colour tag carries the rule version (`…-v2-r2hlg1000.thumb`), so
  HDR thumbnails are made again once and SDR thumbnails keep their names; `ThumbnailCoordinator` unchanged otherwise.
- QG: `Video.Tests` — the HDR golden arguments with `hdr-r2-…`, the SDR arguments byte for byte; the HLG and PQ fixtures
  (PQ with and without mastering / CLL) decoded within ΔE76 ≤ 1 of the S1 reference, neutral patches neutral; the
  rule-1 LUT removed before the store's first write in a session; `HdrThumbnailTests` with the new tag; `ThumbnailServiceTests` (SDR names unchanged).

#### 18.11 — End to end, PQ and the real footage *(done — `46a9e38`; PQ not verified on real footage)*
- PR: no new code expected; fixes found by the checks only.
- QG: `ExportEndToEnd.Tests` — the HLG and PQ scenes (Preview = export canvas byte for byte, the rule within ΔE76 ≤ 1,
  the thumbnail's flat colours equal), every SDR scene unchanged.
- PQ (synthetic, no real PQ footage exists): the inverse OOTF, the `Lpeak` from MaxCLL / mastering / the 1000 fallback,
  the knee skipped at `Lpeak` ≤ 501 nit, the 203-nit reference white of PQ within 2 codes of HLG 75 %.
- M / manual (`docs/PHASE18_MANUAL_TEST_PLAN.md` rows 5–8, 10–12 again, with the five comparison clips): the
  application's export of the fixture project compared with the approved `fair_tgfit-sat100` render — the generated LUT
  equal to the approved cube within 1·10⁻⁶ per node, the frames within 1 LSB; the Preview, thumbnails and export the same
  picture; hardware = software on 20 frames.

#### 18.12 — Closeout of R1 *(done 2026-10-14 — build -warnaserror Release / Debug 0 / 0; passed 3196, skipped 2, failed 0; three blame-hang runs the same; heavy 3198 / 0 / 0; git diff --check clean; the product owner's visual check of the S1 export open)*
- QG: `dotnet build --no-incremental -warnaserror` Release and Debug 0 / 0; the full suite once plus three times with
  `--blame-hang`; the heavy scenes once; `git diff --check`; an independent phase review of R1 against D033.
- PR: the product owner's visual check of the application's own export (the five clips) on a phone and an SDR monitor;
  after acceptance, the real editing trial (D033 §8: the parameters are to be re-checked in the finished application).
- Documentation: ARCHITECTURE (the S1 rule), D033 R1 status and refinements, `progress.md`, ROADMAP / README, the manual
  plan, this plan's checkbox.

Commits per step on the branch after the product owner approves this plan; push, pull request and merge only on a
separate command.

## Phase 19 — AI hold markup: steps (D034)

Formalized on 2026-10-14 (DECISIONS.md D034, specification revision 5, accepted by the product owner). The normative
text is D034; labels as in Phases 9–18 (**PR**, **QG**, **M**, **Impl**). Branch `feat/ai-hold-markup` from `c29a767`.
**The implementation needs the product owner's separate permission**; each step is accepted before the next one starts;
commits only with permission; push, pull request and merge only on a separate command; builds and test runs on the
product owner's command.

Goal: the user marks the holds of a route with their real contours by clicking on a paused frame; the route outline is
drawn over the clip, the same in the Preview and the export (D034 §1).

Gates for every implementation step 19.1–19.7 (QG): `dotnet build` 0 errors / 0 warnings (`-warnaserror`); the full
`dotnet test AiVideoEditor.sln` green (only the two 4K heavy scenes skipped) **without the model weights or a GPU**; the
Preview ↔ export parity suite, the L1-c criteria and every existing expected value unchanged; Core gets no ML
dependency; new UI checked in the real app at 1024 and 1440 px; `docs/PHASE19_MANUAL_TEST_PLAN.md` completed for the
step; `progress.md` updated.

Reference data (outside the repository, never committed — the product owner's footage):
`%USERPROFILE%\Desktop\AiVideoEditor-E1-Reference\` (frames, the 164 SDR + 24 HDR clicks, the graded masks and sheets,
the owner's О / К marks, the pinned ONNX model, the reference ONNX masks, the scripts; `README.md`, `MANIFEST.sha256`).

Local real-weights suite (D034 §7): the test project `tests/Vision.ModelTests`, **not part of `AiVideoEditor.sln`**, so
the regular CI never sees it (no CI configuration change). Run: `dotnet test tests/Vision.ModelTests/Vision.ModelTests.csproj
-c Release`; `AIVE_AI_MODEL_DIR` (default `%LOCALAPPDATA%\AiVideoEditor\models\sam2.1-large`) and `AIVE_AI_E1_DIR`
(default the reference folder). Its report (Markdown + JSON, `<reference>\results\<timestamp>\`): model id and SHA-256s,
ONNX Runtime, OS, GPU and driver; the three alternatives against the reference ONNX masks (score order, IoU); refinement
with several points; DirectML vs CPU; performance; the criterion-10 table of all 164 SDR clicks (IoU, inherited mark or
RECHECK). Required and recorded before the phase is accepted.

Criterion 10 (D034 §8) — protocol: the same 164 SDR clicks, frames and grading rule; a click whose C#-pipeline mask has
IoU ≥ 0.98 with the graded mask inherits the owner's mark, otherwise (no mask, ambiguous) it is re-checked by the product
owner on a sheet of the same format; IoU and the final mark kept for every click; pass ≥ 155/164. Before the
implementation (variant B, product owner 2026-10-14): the graded masks come from PyTorch with bf16, so the 38 SDR clicks
where the pinned ONNX model's mask has IoU < 0.98 with them are re-graded once on the ONNX masks; the re-grade is kept
apart from the original marks and the protocol records which marks are inherited and which re-graded. The C#
preprocessing reproduces SAM 2's own (a bilinear antialiased resize to 1024 × 1024, ImageNet normalization; the logits
upsampled bilinearly, threshold 0) — `reference/onnx_reference.json`.

Amended candidate rule (D034 §1, 2026-10-15): masks over 2 % up to 20 % of the frame are offered when the model's score is
≥ 0.9, over 20 % stay hidden. Criterion 10 is reported under both: the original rule **156/164** (the accepted metric) and
the amended rule **156/164** (`<E1>/results/20261015-121935/`, no choice changed). The bounds are provisional; known
wrong large masks on close-ups (wall panel, floor, ceiling, people) — D034 implementation notes.

Out of scope: D034 §1 "Not in the MVP" — among them tracking, camera motion compensation, automatic route detection and
the route animation (future ideas above).

### 19.0 — Documentation *(done, `a8f656a`)*
- The Phase 18 merge (README, ROADMAP, this plan, `progress.md`); Phase 19 and these steps; the skeleton of
  `docs/PHASE19_MANUAL_TEST_PLAN.md`.

### 19.1 — Hold markup model and project format v5 *(done, `4a83032`)*
- Impl: Core — `HoldMarkup`, `Hold`, the OKLab colour type, validation; Project — `formatVersion` 5, v4 read and written
  as v5 on save, damaged markup refused; Timeline — the markup copied by Split, Duplicate and Paste (`CloneClip`,
  `CopyWithLink`). No UI.
- QG: **the serialization of every hold's `SurfaceLab`, `WallLab` and `SourceTime`** (D034 §7: exact round trip, a
  second save identical to the first, holds from different frames, non-round values; a hold without samples or with
  non-finite values refused); v4 opens; a file newer than v5 refused; Split / Duplicate / Paste copies independent.

### 19.2 — Edits, route colour and keyline *(done, `4a83032`)*
- Impl: `ITimelineEditService` commands through `EditPlan` — add a hold, remove a hold, set the style; style changes merge
  (D017); refused on a locked track and during an export. A pure colour service in Core (palette, maximin over hold and
  wall, keyline Auto / On / Off), recomputed only when the set of holds changes, from the stored samples.
- QG: undo / redo of every edit; the colour on synthetic OKLab samples incl. holds from different frames; the colour
  service has no decoder dependency (no frame decoded); no recomputation with an unchanged set; Manual; the keyline for
  the automatic and the manual colour.

### 19.3 — One rendering path for the Preview and the export *(done, `90de6f4`)*
- Impl: the markup in the playback snapshot (`DiffersOnlyInPresentation`); a new `DrawOperation` of the clip's layer in
  `CompositionDrawPlan` (normalized source → crop → layer matrix → canvas; clipped to the crop; the layer's opacity; the
  stroke per the canvas's short side; the keyline 50 / 25 / 25), painted by `CompositionPainter`; the one-screen-pixel
  minimum in the Preview only.
- QG: geometry; the stroke on 1280 × 720, 1920 × 1080, 3840 × 2160 and 1080 × 1920 canvases (±0.5 px in the exported
  frame of a synthetic scene), independent of the clip's scale, proportional in the Preview at several control sizes;
  Preview ↔ export parity; fades, dissolves, hidden track, offline media.

### 19.4 — `src/Vision`: local inference *(done, `8f2c0dc`)*
- Impl: `IHoldSegmentationService` in Core; `src/Vision` on `Microsoft.ML.OnnxRuntime.DirectML` — the model folder and the
  SHA-256 check (`sam2.1-large-v1`), a lazily created, reused session; the CPU fallback for known DirectML start / run
  errors with the status-bar message; cancellation; SAM 2's pre- / postprocessing; the colour samples of a confirmed
  hold from its mask and frame. `tests/Vision.ModelTests` outside the solution.
- QG (regular CI, no weights): a missing model, a wrong SHA-256, a simulated DirectML failure → CPU, cancellation without
  a partial result — through a test double of the session. Local suite: green (the three alternatives within median
  IoU ≥ 0.98 of the reference, refinement gives one mask, DirectML vs CPU, performance: frame ≤ 1 s GPU / ≤ 10 s CPU,
  click ≤ 150 ms).

### 19.5 — Markup mode (UI) *(done; M pending)*
- Impl: the full-resolution frame under the playhead through `IVideoDecoder` (the HDR rule of D033), prepared in the
  background with progress and cancellation; pointer input over the Preview; candidate, alternatives, Shift+click, Enter /
  Esc / Delete routed to the mode (never to the timeline), zoom / loupe, leaving the mode; the Inspector section (stroke,
  colour Auto / Manual, keyline Auto / On / Off).
- QG: view-model tests of the scenario and of the key routing; M: the scenario in the real app.

### 19.6 — Threshold T calibration *(done: T = 0.16, the product owner's decision of 2026-10-15)*
- Outside the repository on the E1 frames: the keyline threshold and the palette; examples with and without the keyline
  shown to the product owner; the accepted values become constants with a test.

### 19.7 — `docs/AI_MODEL.md` *(done)*
- The checked licences and notices of every pinned component (SAM 2 `LICENSE` and `NOTICE` at `2b90b9f`,
  `LICENSE_cctorch`, the checkpoint statement, samexporter 0.5.0 with its third-party licences, ONNX Runtime, onnx — source,
  date, conclusion); the full reproducible export recipe giving the pinned SHA-256s; installing the model; the error
  messages. ARCHITECTURE, CLAUDE.md (the project count), `docs/README.md`.

### 19.8 — Final verification & closeout *(done by Claude 2026-10-14; the PO items open)*
- QG: `dotnet build --no-incremental -warnaserror` Release and Debug 0 / 0; the full suite once plus three times with
  `--blame-hang`; the heavy scenes once; `git diff --check`; **the local real-weights suite** with its report; criterion 10
  on the E1 set (≥ 155/164, the protocol above); an independent phase review against D034.
- M: `docs/PHASE19_MANUAL_TEST_PLAN.md` — the product owner's 2–3 clips: markup, export, viewing; the time to mark a route
  recorded; the threshold examples accepted.
- Documentation: D034 status, ARCHITECTURE, `progress.md`, ROADMAP / README, the manual plan, this plan's checkbox.

## Phase 20 — Manual correction of hold contours: steps (D035)

Assigned on 2026-10-15 by the product owner after the merge of PR #20 (`2d0a10b`); the normative text is DECISIONS.md D035.
Branch `feat/phase-20-hold-contour-editing` from `origin/main` = `2d0a10b`. The product owner's permission covers the code,
tests, documentation and commits on this branch; **no push, pull request or merge without a separate command**.

Goal: the user corrects a wrong boundary of a confirmed hold by hand — drag a vertex, Alt+click to add one, Delete /
double-click to remove one — without cycling the model's alternatives; the corrected contour is saved in the project and
drawn the same in the Preview and the export.

Gates: `dotnet build -warnaserror` 0 / 0; the full `dotnet test AiVideoEditor.sln` green (the two 4K heavy scenes skipped)
without the model weights or a GPU; every existing expected value unchanged; `project.json` stays v5; the gestures checked
in the real app (`docs/PHASE20_MANUAL_TEST_PLAN.md`); `progress.md` updated.

Out of scope (D035): negative points, new recognition modes, the route animation, timeline / localization / other UX.

### 20.1 — Contour geometry (Core) *(done)*
- `Core/Holds/ContourEditing`: the handles (every vertex up to 32, else Douglas–Peucker at 2 % of the size; thinned to a
  spacing on the screen, at least three), a drag with
  the falloff over the hidden vertices (every other vertex exact), insert on an edge, removal of a handle (at least three
  stay), the check that an edit introduces no crossing / touch / zero area; `MaskContour.SimplifyIndices`.
- Tests: `Core.Tests/ContourEditingTests`.

### 20.2 — The edit (Timeline) *(done)*
- `ITimelineEditService.ReplaceHoldContour` → `SetHoldMarkupCommand` with `HoldMarkupFields.Contour` (never merged); the
  same hold, samples and colour; validation, crossing, locked track.
- Tests: `Timeline.Tests/HoldMarkupEditTests` (undo / redo, separate steps, refusals, Split, the drawn outline);
  `Project.Tests/HoldMarkupPersistenceTests` (save / open / save of a corrected contour, v5);
  `Rendering.Tests/HoldOutlineRenderTests` (a corrected contour, Preview and export).

### 20.3 — Markup mode gestures (UI) *(done)*
- `HoldMarkupViewModel`: handles of the selected hold, drag / insert / delete as one step each, the live crossing state,
  Esc, Delete over a handle, the export lock, undo / redo refreshing the selection; `HoldMarkupOverlay`: handle and edge
  hit-testing in view pixels, the drag with pointer capture and a 3 px threshold, the handles and the red invalid contour.
- Tests: `UI.Tests/HoldMarkupUiTests` (Phase 20 section).

### 20.4 — Verification & closeout *(done 2026-10-15; accepted by the product owner after their check on their own clips)*
- The full suite; the real-app check of `docs/PHASE20_MANUAL_TEST_PLAN.md` (rows 1–12 passed; three defects found and
  fixed in the run — overlapping handles on a small hold, Alt+click ignored on a dense contour and taken by a near
  handle); documentation (D035, ARCHITECTURE, ROADMAP, README, `progress.md`).
- The product owner's check on their own clips (plan row 16): passed, the implementation accepted (2026-10-15); merge
  only on a separate confirmation.


## Phase 21 — Animation of the hold outlines: steps (D036)

Assigned on 2026-10-15 by the product owner (the specification and the answers to its five questions); the normative text
is DECISIONS.md D036. Branch `feat/phase-21-hold-animation` from `origin/main` = `a657bff` (the merge of PR #21, Phase 20).
The permission covers the code, tests, documentation and commits on this branch; **no push, pull request or merge without a
separate command**.

Goal: the route's holds appear one by one from a moment of the video, stay, and disappear together — the same on every
frame of the Preview and the export.

Gates: `dotnet build -warnaserror` 0 / 0; the full `dotnet test AiVideoEditor.sln` green; `project.json` v6 (v5 read);
the Phase 20 tests unchanged and green; the scenarios of `docs/PHASE21_MANUAL_TEST_PLAN.md` in the real app and on the
product owner's clips; `progress.md` updated.

Out of scope (D036 §5): tracking / motion compensation, groups, per-hold intervals or animation, reordering by hand,
keyframes, markers on the clip.

### 21.1 — Model and timing rule (Core) *(done)*
- `HoldAnimation` / `HoldAnimationOrder` on `HoldMarkup` (null = off), `HoldMarkupRules.ValidateAnimation`;
  `Core/Playback/HoldAnimationRule` (the start frame from the source time, `SourceTimeAt` for "Set to playhead", the ranks,
  the area centroid) and `HoldSchedule` (appear frames, the D025 ramps).
- Tests: `Core.Tests/HoldAnimationRuleTests` (24 / 30 / 60 / 29.97 fps, every speed, no drift, ramps, ties, a corrected
  contour).

### 21.2 — Snapshot and drawing (Core, UI rendering) *(done)*
- `HoldOutline.Schedule` (built with the clip's timing), `PictureLayer.OutlineOpacities` from `LayersAt`,
  `OutlineDraw.PolygonOpacities` / `OpacityGroups`; `CompositionPainter` draws each opacity group through an opacity mask
  with the combined hold × layer opacity.
- Tests: `Core.Tests/HoldAnimationSnapshotTests`; `Rendering.Tests/HoldAnimationRenderTests` (Preview = export pixels in
  the fades, a hidden contour, the keyline group, no darkened corners); `ExportEndToEnd.Tests/ExportHoldAnimationEndToEndTests`.

### 21.3 — The edit (Timeline) *(done)*
- `ITimelineEditService.SetHoldAnimation` → `SetHoldMarkupCommand` with `HoldMarkupFields.Animation*` (one setting merges;
  on / off never).
- Tests: `Timeline.Tests/HoldAnimationEditTests` (undo / redo, merging, refusals, a correction / add / remove reordering,
  Trim, Slip, Speed, Move, Split continuing, Duplicate, Paste).

### 21.4 — Format v6 (Project) *(done)*
- `HoldAnimationDto`, `CurrentFormatVersion = 6`, v5 read without animation.
- Tests: `Project.Tests/HoldAnimationPersistenceTests`; the current-version assertions of the older persistence tests moved
  to 6.

### 21.5 — Inspector and markup mode (UI) *(done)*
- `HoldMarkupViewModel`: the "Animation" block (Animate, Order, Start + Set to playhead, the four durations, until clip end,
  the summary and the warning; the export lock); `AnimatedContours` while the mode is on; `HoldMarkupOverlay` draws them
  thin and dashed; `InspectorView.axaml`.
- Tests: `UI.Tests/HoldMarkupUiTests` (Phase 21 section).

### 21.6 — Verification & documentation *(done 2026-10-15; the main functionality accepted by the product owner)*
- The full suite, `-warnaserror`, the diff review; D036, ARCHITECTURE, ROADMAP, README, `progress.md`,
  `docs/PHASE21_MANUAL_TEST_PLAN.md`.
- Windows (Claude, `8e28d32`): Release build with `-warnaserror` 0 errors / 0 warnings; full suite passed 3407, failed 0,
  skipped 2 (base `a657bff`: 3352 / 0 / 2 — no new failures).
- The product owner's real-app check (2026-10-15): the animation and fades in the Preview, the dashed contours in the
  markup mode, the contour correction, Trim / Slip / speed / Split, the timeline display, the export (the video reopened)
  and save + reopen passed; the main functionality accepted. The other rows of `docs/PHASE21_MANUAL_TEST_PLAN.md` stay
  NOT RUN in the real app. Follow-ups outside the phase (two marker issues, the vertical-video export framing): `ROADMAP.md`
  "Follow-ups and backlog".

## Phase 22 — Timeline selection, snapping and making room: steps (D037)

Assigned on 2026-10-15 by the product owner (the specification, revision 2, and the answers Q1–Q11); the normative text is
DECISIONS.md D037. Branch `feat/phase-22-timeline-ux` from `origin/main` = `7318ba8` (the merge of PR #22, Phase 21). The
permission covers the code, tests, documentation and commits on this branch; **no push, pull request or merge without a
separate command**.

Goal: selecting and moving clips with fewer actions — select all, a marquee, a magnet that never makes an overlap, room made
on the right instead of a refusal, exact nudges, a view that follows the drag.

Gates for every step (QG): `dotnet build -warnaserror` 0 / 0; the full `dotnet test AiVideoEditor.sln` green (only the two 4K
heavy scenes skipped); `project.json` v6 unchanged (a Phase 21 project opens and saves byte for byte); the Preview ↔ export
parity suites and every expected value outside the refined rule of D037 §3 unchanged — a test that asserted a refused
overlap now asserts the push or the left-collision refusal, with a note; every new edit one undo step, a refused one none;
each rule of D037 covered by a test that a mutation of the rule fails; `docs/PHASE22_MANUAL_TEST_PLAN.md` and `progress.md`
updated.

Out of scope (D037 §5): the magnetic main track, the cascade across linked tracks (variant A), Shift+click ranges, moving a
multi-track selection between tracks, multi-clip property editing; FU-21.1, FU-21.2 and the vertical-video export framing.

### 22.0 — Sync and documentation *(done)*
- `main` fast-forwarded to `origin/main` = `7318ba8`; the branch; D037; these steps; the manual plan skeleton; ROADMAP and
  `progress.md`.

### 22.1 — Selection: Ctrl+A, Esc, the primary, "1 of N selected" *(done)*
- `TimelineViewModel`: Select All (unlocked tracks, partners), Escape (cancel the gesture / marquee, else clear), the primary
  rule; the selection count in `TimelineClipSelection`; the Inspector's name line; `ShortcutRouter` Ctrl+A / Esc (not in the
  markup mode, not while typing).
- Tests: `UI.Tests/TimelineSelectionUiTests` (Select All, Esc, the Inspector line) and `ShortcutRoutingTests`.

### 22.2 — Marquee *(done)*
- The view-model's marquee (begin / update / end / cancel; intersect; locked tracks skipped; Ctrl adds; live selection);
  the view: a press on an empty lane, the rectangle overlay, Esc.
- Tests: `UI.Tests/TimelineSelectionUiTests` (the rectangle → the clips live, Ctrl, locked, partners, Esc, a click without
  a drag). In the markup mode a marquee leaves the mode as a click does (D037 §1); only Ctrl+A is off there.

### 22.3 — Making room (Timeline) *(done)*
- The push planner on one track (D037 §3) used by `MoveClips` / `CanMoveClips`, `AddClip` at a position, `PasteClips`,
  `DuplicateClips`, `AddTextClip`; the partners follow or refuse; the dissolve refusal; `PreviewMove` with every clip the
  move changes.
- Tests: `Timeline.Tests` make-room tests (the exact overlap, a gap absorbing it, a chain, the left collision, a pushed clip
  into a placed one, a group on several tracks, a partner following / blocked / locked, dissolves kept / refused, speed ≠ 1,
  the frame grid, undo / redo, no step on a refusal) — `Timeline.Tests/MakeRoomEditTests`; the tests that asserted a
  refused overlap (`EditOperationTests`, `ClipboardEditTests` ×2, `LinkedEditTests`, `TimelineClipboardUiTests`) now
  assert the push or the left-collision refusal; `TextClipEditTests` / `TextClipUiTests` pass unchanged (a text inside a
  clip is a left collision, its message still names the overlap).

### 22.4 — Snapping: no overlap, the drop *(done)*
- `Snap` with a filter of targets that would overlap; the drag uses it; the drop snaps the new clip's start / end and shows
  the indicator while dragging over a lane; a drop on an empty track with Snap on lands at 0; the drag preview of pushed
  clips.
- `ITimelineEditService.Snap(…, accept)`, `SnapEngine.FindAll`, `AddedClipDuration`, `PreviewMove`.
- Tests: `UI.Tests/TimelineSelectionUiTests` (a snap that would push skipped, one that would not taken, the drag preview,
  the drop: empty track → 0, Snap off, an edge with the indicator, a drop inside a clip refused, a drop making room).

### 22.5 — Nudge and auto-scroll *(done; the auto-scroll timer itself only in the real app)*
- Alt+← / → (one frame), Alt+Shift+← / → (one second) through `MoveClips`; the auto-scroll speed rule in the view-model and
  the timer in the view.
- Tests: `ShortcutRoutingTests`, `UI.Tests/TimelineSelectionUiTests` (one step each, refusals with no step, the export lock,
  a push and a left collision, the auto-scroll speed rule).

### 22.6 — Verification & closeout *(done by Claude 2026-10-16; Phase 22 accepted by the product owner on 2026-10-16)*
- Build `-warnaserror` Release and Debug, the full suite, the heavy scenes, three `--blame-hang` runs, `git diff --check`;
  the manual plan in the real app at 1024 and 1440 px; a Phase 21 project opened and saved byte for byte; the docs.
- Result (code `f4383c3`, no product code changed in the step): Release and Debug `--no-incremental -warnaserror` 0 / 0;
  the full suite passed 3446, skipped 2 (the 4K scenes), failed 0; `AIVE_HEAVY_TESTS=1` 3448 / 0 / 0; three
  `--blame-hang --blame-hang-timeout 5m` runs 3446 / 2 / 0 each, no hang, no dump; `git diff --check` clean. The manual
  plan's 25 rows run in the real app (UI Automation and system input, Debug build): all PASS, the parts left to the
  automated tests named in the rows; the Phase 21 build (`7318ba8`) and Phase 22 save the fixture byte for byte (v6).
  No defect found in the product; one harness error found and corrected (arrow keys sent without the extended-key flag
  lose Shift on Windows). An observation outside D037 recorded in ROADMAP (FU-22.1).

## Phase 23 — Direct Move, Scale and Crop in the Preview: steps (D038)

Assigned on 2026-10-16 by the product owner (the specification with the answers Q1–Q7, Scale by the corner handles added);
the normative text is DECISIONS.md D038. Branch `feat/phase-23-preview-transform` from `origin/main` = `1aaedc8` (the merge
of PR #23, Phase 22). The permission covers the code, tests, documentation and local commits on this branch; **no push, pull
request or merge without a separate command**.

Goal: the selected clip moved, scaled and cropped with the mouse directly in the Preview — Move with corner handles for a
proportional scale around the centre, Crop with eight handles and an anchored crop — Preview, Inspector and export agreeing,
one undo step per gesture.

Gates for every step (QG): `dotnet build -warnaserror` 0 / 0; the full `dotnet test AiVideoEditor.sln` green (only the two 4K
heavy scenes skipped); `project.json` v6 unchanged; the Inspector's Transform / Crop behaviour and every existing expected
value unchanged; the Preview ↔ export parity suites unchanged; every gesture one undo step, a cancelled one none; each rule of
D038 covered by a test that a mutation of the rule fails; `docs/PHASE23_MANUAL_TEST_PLAN.md` and `progress.md` updated.

Out of scope (D038 §5): rotation by the mouse, guides / snapping, several clips at once, scaling or moving the crop rectangle
as a whole, arrow nudges and shortcuts for the tools, transforming during playback, the Inspector's crop semantics,
keyframes, the dimmed picture of the cropped-away part; FU-21.1, FU-21.2, FU-22.1 and the vertical-video export framing.

### 23.0 — Sync and documentation *(done)*
- The branch from `origin/main` = `1aaedc8`; D038; these steps; the manual plan skeleton; ROADMAP and `progress.md`.

### 23.1 — Gesture math (Core) *(done)*
- `Core/Composition/PreviewTransformMath`: the frame of a picture / text (local rectangle, corners, containment), Move (the
  canvas delta rounded to whole pixels, Shift's axis, the limit), Scale from a corner (the projection on the start diagonal,
  0.01 %, the limits), the anchored crop (the dragged edges in whole source pixels; the minimum size; the compensating
  scale and position; the scale-limit and validator constraints).
- Tests: `Core.Tests/PreviewTransformMathTests` — the anchoring kept exactly (the source → canvas map unchanged to 1e-9) at
  0°, 90°, 180°, 37°, scales 0.5 / 2, a portrait source on a landscape canvas and the reverse; every handle; the limits
  (the edge stops, the result valid); rounding; Move and Scale with rotation; containment of a rotated frame.

### 23.2 — One undo step per gesture (Timeline) *(done)*
- `ClipPropertyGesture` and `ClipPropertyChange.Gesture`; `SetClipPropertiesCommand` merges the edits of one gesture
  (fields united, its description) and never across gestures or with an edit without one; D017's rule unchanged otherwise.
- Tests: `Timeline.Tests` — one step for a gesture changing different fields, two gestures two steps, a gesture after an
  Inspector edit of the same field not merged and the reverse, a return to the start removes the step and leaves no redo,
  the save point, a locked track, exact undo / redo.

### 23.3 — Tool view-model and modes (UI) *(done)*
- `PreviewTransformViewModel`: Move / Crop (exclusive), availability (Timeline mode, no export), the hold markup exclusion,
  the target (the primary selection under the playhead; its frame from the model), the bar's text, gestures (begin / update
  / end / cancel in canvas coordinates, the frozen geometry), the cancellations of D038 §4, the save hook in
  `ProjectFileWorkflow`, the selection by a click (`TimelineViewModel.SelectClipFromPreview`); `ShortcutRouter` (Esc, other
  keys swallowed during a gesture); the shell wiring.
- Tests: `UI.Tests/PreviewTransformUiTests`.

### 23.4 — Overlay: the frame, selection and Move (UI) *(done)*
- `PreviewTransformOverlay` over the Preview's `CompositionView` (view ↔ canvas through the viewport; the frame, its
  keyline and centre; the topmost layer under the pointer; the press / drag / release / capture loss; cursors); the two
  buttons and the bar's text in `PreviewView.axaml`.
- Tests: `UI.Tests/PreviewTransformUiTests` (the hit test through the viewport with a letterbox, a rotated frame, text,
  the topmost layer, the target winning inside its frame).

### 23.5 — Scale by the corner handles (UI) *(done)*
- The four corner handles in Move; the scale gesture; the handles' priority over the inside; no handle in Crop.
- Tests: `UI.Tests/PreviewTransformUiTests` (a corner drag through the view, rotation, the limits, Position unchanged).

### 23.6 — Crop handles (UI) *(done)*
- The eight handles in Crop (pictures only), the full source outline, the anchored crop gesture.
- Tests: `UI.Tests/PreviewTransformUiTests` (an edge / corner drag through the view, the anchoring on the screen, a
  portrait clip, rotation, the limits, text refused).

### 23.7 — Verification & closeout *(done by Claude 2026-10-19; accepted by the product owner on 2026-10-19 — PASS WITH NOTES)*
- Build `-warnaserror` Release and Debug, the full suite, the heavy scenes, three `--blame-hang` runs, `git diff --check`;
  Preview ↔ export parity of transformed clips; the manual plan in the real app; the docs.

## Phase 24 — Russian localization and interface polish: steps (D039)

Assigned on 2026-10-19 by the product owner (the audit's specification with the recommended answers Q1–Q4, the icon pass
and the slider inventory added); the normative text is DECISIONS.md D039. Branch `feat/phase-24-localization` from
`origin/main` = `c652667` (the merge of PR #24, Phase 23). The permission covers the code, tests, documentation and local
commits on this branch; **no push, pull request or merge without a separate command**.

Goal: the whole interface in Russian with one terminology, nothing clipped at 1024 × 640 and 1440 × 900, the frequent
commands compact and recognizable; behaviour, the project format and the export unchanged.

Gates for every step (QG): `dotnet build -warnaserror` 0 / 0; the affected test projects green, the full suite at the
closeout (only the two 4K heavy scenes skipped); `project.json` v6 unchanged; the Preview ↔ export parity suites and every
expected value unchanged — English assertions stay as they are (the neutral language), Russian ones are added;
`docs/PHASE24_MANUAL_TEST_PLAN.md` and `progress.md` updated.

Out of scope (D039 §9): a language switch, a general redesign, translated documentation, sliders other than Opacity and
Volume; FU-21.1, FU-21.2, FU-22.1 and the vertical-video export framing.

### 24.0 — Sync and documentation *(done, `e328112`)*
- `main` fast-forwarded to `origin/main` = `c652667`; the branch; D039; these steps; the manual plan skeleton; ROADMAP and
  `progress.md`.

### 24.1 — Infrastructure and guards *(done, `9d2c6bb`)*
- `Core/Localization/Messages.resx` (+ `.ru`), `UI/Localization/Strings.resx` (+ `.ru`), generated classes; `UiLanguage`
  (the neutral default, `Apply` at start from `App:Language`); `Plural`; the three guard tests with an allowlist of what
  is left (shrinking step by step); the first strings (the status bar's «Готово.»).
- Tests: `Core.Tests` (plural forms, `UiLanguage`), `UI.Tests` (the guards, a Russian resource read).

### 24.2 — The shell: tool bar, recent projects, Media Browser, dialogs and workflows *(done, `246aead`)*
- `ToolbarView`, `RecentProjectsViewModel`, `MediaBrowserView` / view-models, project settings, export (dialogs, progress,
  the file picker), import, relink, open / save / autosave / recovery, the window title.

### 24.3 — The Preview *(done, `246aead`)*
- Modes, transport, the source bar, Move / Crop, the hold markup bar and its messages.

### 24.4 — The timeline and its service messages *(done, `246aead` / `7cdf952`)*
- The tool bar, track headers, markers, the empty state, selection, statuses; `TimelineEditService` (all partials),
  `TimelineValidator`, `MediaRelinkService`; new text clips «Текст».

### 24.5 — The Inspector *(done, `246aead` / `7cdf952`)*
- Every section, the drop-downs without enum names, `ClipPropertyValidator` and the other Core validators that reach the
  user.

### 24.6 — Project, export, video and units *(done, `7cdf952`)*
- `ProjectService`, `ProjectSerializer`, `AutosaveService`, export preflight / encoder messages shown to the user, decoder
  messages; `FileSizeFormat`, the media kind, frame rates.

### 24.7 — Layout, icons, sliders, keyboard *(done, `932027a`)*
- The Inspector's label style, the status bar's trimming and tooltip, Play / Pause width, the two tool bars at 1024 px;
  the icon set (D039 §7); the Opacity and Volume sliders (D039 §8); shortcuts in the Russian layout.
- Tests: view binding tests for the sliders (one undo step per drag), the icons' presence; the real app at 1024 × 640 and
  1440 × 900.

### 24.8 — Verification & closeout *(done by Claude 2026-10-19 — fixes `1e43e26`; results in `progress.md` and the manual plan)*
- The allowlist holds only accepted technical tokens; Release and Debug `--no-incremental -warnaserror`, the full suite,
  `git diff --check`; the manual plan in the real app (both sizes, the Russian layout); a Phase 23 project opened and saved
  byte for byte; the docs.

## Phase 25 — Technical backlog and small UX improvements: steps (D040)

Assigned on 2026-10-19 by the product owner (the post-Phase 24 audit's proposal with the answers to its questions); the
normative text is DECISIONS.md D040. Branch `feat/phase-25-backlog-ux` from `origin/main` = `d3df0ba` (the merge of PR
#25, Phase 24). The permission covers the code, tests, documentation and local commits on this branch; **no push, pull
request or merge without a separate command**. A defect found outside this scope is reported (cause and proposed fix)
before it is changed.

Goal: close the small technical backlog left after Phase 24 and finish the slider and icon passes of D039 §7 / §8;
behaviour otherwise, the project format and the export unchanged.

Gates for every step (QG): `dotnet build -warnaserror` 0 / 0; the affected test projects green, the full suite, the heavy
scenes and `--blame-hang` at the closeout; `project.json` v6 unchanged (byte comparison with `d3df0ba`, `ru` and `en`);
the Preview ↔ export parity suites and every expected value unchanged; every new user-visible text in `Strings` /
`Messages` with `.ru` (the D039 guards); `docs/PHASE25_MANUAL_TEST_PLAN.md` and `progress.md` updated.

Out of scope (D040 §7): FU-21.2 (clip-bound markers, no change of the marker model), the export of vertical video (a
separate future phase), a Speed slider, a general redesign, new dependencies, unrelated backlog.

### 25.0 — Sync and documentation *(done, `01dbb92`)*
- `main` fast-forwarded to `origin/main` = `d3df0ba`; the branch; D040; these steps; the manual plan skeleton; the
  documentation brought in line with the merges (Phases 19, 20, 22, 24 in the list above, ROADMAP, `progress.md`).

### 25.1 — Configuration (FU-24.2) *(done, `3fbdf5e`)*
- The host's content root is `AppContext.BaseDirectory`, so `appsettings.json` next to the exe is read whatever the
  process's current directory; the command-line `args` go to the builder.
- Tests: the host built with another current directory reads `App:Language` and `Ffmpeg:*` from the application's folder.

### 25.2 — Marker navigation status (FU-21.1) *(done, `4e491d1`; the cause and the fix in D040 "Refined in Step 25.2")*
- Reproduce, find the cause; a successful Previous / Next marker reports a localized status with the marker's timecode,
  a refusal keeps its message.
- Tests: frame boundaries (the playhead on, just before / after a marker's frame), the playhead past the sequence's end,
  the status replaced after a refusal.

### 25.3 — The hold markup mode left on its own (FU-22.1) *(done, `a7b1811` — not reproduced, no product code changed; D040 "Refined in Step 25.3")*
- Reproduce, find the cause, a regression test. If the mode is left by the playhead's own adjustment while the frame is
  prepared, D034 is refined: that internal adjustment is not a user seek; a user seek still leaves the mode. If the two
  cannot be told apart reliably, or the fix needs another change of agreed behaviour — stop and report before the code.

### 25.4 — Sliders (FU-24.1, in part) *(done, `4a471fa`)*
- Fade In / Fade Out (video and audio clips) and the hold line width get a slider under their field, bound to the same
  property; the field keeps exact typing; one finished drag is one undo step (D017's merge); the Fade maximum is the
  field's existing maximum and follows the clip's length.
- Tests: the binding and one undo step per drag for each new slider and for Volume (the Phase 24 gap); Volume checked in
  the real app on a clip with sound. No Speed slider.

### 25.5 — Icons on the timeline's tool bar *(done, `4c53633`; Unlink taken with Link)*
- Icons from the D039 §7 set (same style) for + Text, + Video / Audio track, Paste, Duplicate, Ripple Delete, Close Gap,
  Dissolve, Link and Fit; the localized labels and tooltips stay — no button becomes nameless for compactness.
- The layout checked at 1024 × 640 and 1440 × 900.

### 25.6 — Verification & closeout *(done by Claude 2026-10-19 — results in `progress.md` and the manual plan)*
- The Phase 24 manual gaps (sound, model errors, a damaged project, a failed export, the track and marker tooltips) run in
  the real app or marked NOT RUN — never PASS without a run.
- Release and Debug `--no-incremental -warnaserror`; the full suite, `AIVE_HEAVY_TESTS=1`, three `--blame-hang` runs;
  `git diff --check`; `project.json` v6 byte for byte with `d3df0ba` (`ru` and `en`); parity and undo-step counts; the
  manual plan at 1024 × 640 and 1440 × 900.

## Phase 26 — Vertical video and framing: steps (D041)

Assigned on 2026-10-20 by the product owner (the post-Phase 25 audit's variant A with the answers Q1–Q4); the normative
text is DECISIONS.md D041. Branch `feat/phase-26-vertical-video` from `origin/main` = `58c48cb` (the merge of PR #26, Phase
25). The permission covers the code, tests, documentation and local commits on this branch; **no push, pull request or
merge without a separate command**. A need to change the format, the architecture or agreed behaviour beyond D041 is
reported with variants before the change.

Goal: a phone video — portrait above all — exported without unwanted black bars in a few clicks; the composition, the
format and the export otherwise unchanged.

Gates for every step (QG): `dotnet build -warnaserror` 0 / 0; the affected test projects green, the full suite, the heavy
scenes and `--blame-hang` at the closeout; `project.json` v6 unchanged (byte comparison with `58c48cb`, `ru` and `en`); the
Preview ↔ export parity suites and every expected value unchanged; every new user-visible text in `Strings` / `Messages`
with `.ru` (the D039 guards); `docs/PHASE26_MANUAL_TEST_PLAN.md` and `progress.md` updated.

Out of scope (D041 §4): a blurred background, an automatic reframe or tracking, a persistent Fill mode, `project.json` v7,
a warning before the export, FU-21.2, AI features, the technical-debt refactoring, new dependencies, unrelated improvements.

### 26.0 — Sync and documentation *(done, `3b3d0b6`)*
- `main` fast-forwarded to `origin/main` = `58c48cb`; the branch; D041 (and D040's merge status); these steps; the manual
  plan's skeleton; Phase 25 recorded as merged (ROADMAP, `progress.md`, the list above).

### 26.1 — Core rules *(done, `c01f93d`)*
- `ProjectSettingsRules.SuggestedCanvas` (D041 §1: the short side 1080, the long side by the source's aspect rounded to
  even, the 4096 limit, `CanvasError`); `CompositionMath.FillScale` / `FitScale` (D041 §2: crop, rotation, exact for
  multiples of 90° and rounded outward, the margin for other angles).
- Tests: portrait / landscape / square / 20 : 9 / 4 : 3 sources, odd sides, the 4096 limit, sizes `CanvasError` refuses;
  Fill / Fit with crop, 0 / 90 / 180 / 270 and an odd angle, `CoversCanvas` true after Fill and the picture inside the canvas
  after Fit, Fit = 1 without rotation, values outside the Scale limits.

### 26.2 — The canvas offer *(done with 26.3)*
- The timeline's view model: after a successful add of a video clip with
  its picture to an empty timeline, the conditions of D041 §1 → the dialog → `SetCanvasSize` (one Undo step); the session
  flag reset by New / Open / Recover; localized texts.
- Tests: shown / not shown (orientation null, size unknown, a non-empty timeline, a canvas not default, a canvas changed in
  the session, a second add, sound only, a landscape 16 : 9 source), accept / decline / close, Undo / Redo (canvas, then the
  add), Insert / Overwrite from the Source viewer.

### 26.3 — Fill / Fit commands *(done; the clip's context menu opened from the view's right click — the timeline had no context menu)*
- `ITimelineEditService.FrameClip(clipId, ClipFraming)` — the D041 §2 rule, one Undo step as a gesture of its own,
  NoChange, the refusals; the Inspector's two buttons; the clip's context menu on the timeline (right click); the export's
  editing lock; localized texts.
- Tests: the edit (values, one step, no merge with a typed Scale, refusals, locked track), the Inspector and the menu's
  commands, a rendering parity test of a filled portrait clip on a landscape canvas and a landscape clip on a portrait one.

### 26.4 — Verification & closeout *(done by Claude 2026-10-20 — results in `progress.md` and the manual plan)*
- Release and Debug `--no-incremental -warnaserror`; the full suite, `AIVE_HEAVY_TESTS=1`, three `--blame-hang` runs;
  `git diff --check`; `project.json` v6 byte for byte with `58c48cb` (`ru` and `en`); the manual plan in the real app at
  1024 × 640 and 1440 × 900 with the product owner's Vivo and Huawei clips (portrait and landscape, with and without rotation
  metadata), an export watched in a player; the documentation.

## Phase 27 — Text animation and readability: steps (D043)

Approved on 2026-10-20 by the product owner with the answers Q1–Q6; the normative text is DECISIONS.md D043. Branch
`feat/phase-27-text-animation` from `origin/main` = `41dadf2` (the merge of PR #27). The permission covers the code, tests,
documentation and local commits on this branch; **no push, pull request or merge without a separate command**. **Each step
after 27.0 starts only on the product owner's confirmation**; 27.1 is a gate — when its result is not satisfactory the
work stops with the results and the alternatives, and no lower-quality fallback is chosen automatically. A need to change
the format, the architecture or agreed behaviour beyond D043 is reported with variants before the change.

Goal: a text over a bouldering video that appears and disappears nicely and stays readable on a colourful wall, in a few
clicks, without a title editor.

Gates for every step (QG): `dotnet build -warnaserror` 0 / 0; the affected test projects green, the full suite, the heavy
scenes and `--blame-hang` at the closeout; the existing Preview ↔ export parity suites and every expected value unchanged
(v6 text clips look exactly as before — D043 acceptance criterion 3); a v6 project saved again differs only in
`formatVersion`; every
new user-visible text in `Strings` / `Messages` with `.ru` (the D039 guards); `docs/PHASE27_MANUAL_TEST_PLAN.md` and
`progress.md` updated.

Out of scope (D043 §10): a title editor, keyframes, motion paths, per-letter animation, a shadow, gradients, stickers, text
on a curve, wrapping, font files, animation of other clip kinds, new dependencies, D025's fades of pictures and sound.

### 27.0 — Sync and documentation *(done)*
- The branch; D043 accepted with the answers; Phase 26 recorded as merged; the manual plan's skeleton
  `docs/PHASE27_MANUAL_TEST_PLAN.md`; `docs/README.md`.

### 27.1 — Feasibility of the outline (a separate technical check; a gate) *(done 2026-10-20 — D043's candidate fails off the UI thread; variant B — the geometry prepared on the UI thread and cached, the outlined text filled from it — chosen by the product owner with conditions 1–7: D043 "Refined in Step 27.1")*
- `FormattedText.BuildGeometry` (or the equivalent) stroked with a pen in `CompositionPainter`, checked for: the quality of
  the glyph contours (small and large sizes, Cyrillic and Latin, bold, the default font and another installed one,
  thicknesses 2–20 %); the export's cost per frame at 1080 × 1920 and 1920 × 1080 with a cache per text and style; correct
  work off the UI thread, as the export runs the painter; Preview ↔ export parity of the same frame.
- The result recorded (D043 "Refined in Step 27.1"); no product behaviour changes in this step (experimental code only in
  tests or a scratch harness, removed or kept as a test).
- **Unsatisfactory → stop**: the measurements and the alternatives (with their quality and cost) go to the product owner;
  no fallback is chosen automatically (D043 §9, §12).

### 27.2 — Model, validation and `project.json` v7 *(done with 27.3, `03168e2`)*
- `TextClip` / `TextProperties`: `MotionIn`, `MotionOut`, `Bold`, `Style`; `ClipPropertyValidator`; the DTOs and the
  serializer (v7, the members optional; v6 read; a v6 build's refusal by the version check).
- Tests: the round trip, v6 → v7 differing only in `formatVersion`, damaged values refused.

### 27.3 — Core rules: motion and placement *(done, `03168e2`)*
- The motion on timeline frames (D043 §1: D025's ramp, the ease-out, Slide `d = 8 %` of the short side, Zoom 70 → 100 %,
  clamped, none on a dissolve edge) applied to the `TextLayer`'s transform in `PlaybackSnapshot.LayersAt`; the grid
  placement (D043 §3: nine cells at 8 % of the canvas's short side from the edges, the box from the painter's layout rule
  with the background's padding and the outline).
- Tests: every frame of the ramps, short clips, the frame-rate change, the dissolve edge, the nine cells on portrait /
  landscape canvases; a v6 text's fades unchanged.

### 27.4 — Rendering: outline, background, bold *(done, `13f48db`; one glyph cache per consumer — D043 implementation notes)*
- `TextDraw` carries the outline, the background and the weight; `CompositionPainter` draws the background, then the
  outline, then the fill; the Move tool's frame includes the background's padding.
- The outline by variant B (D043 "Refined in Step 27.1", conditions 1–7): a glyph-geometry cache keyed by the text, family,
  size, Bold, alignment and box, bounded, thread-safe; the geometry created and first drawn on the UI thread, requested by
  the export with its cancellation token; the closing application ends the wait (a failure or a cancellation, never a hang);
  the fill of an outlined text from the geometry, a text without an outline by `DrawText` as before.
- Tests: Preview ↔ export parity per kind, the outline, the background and both; a v6 text byte for byte as before; the
  cache (a key part changed → a new geometry, the bound, a shared entry drawn by several threads); a cancel while the UI
  thread is busy; a dispatcher that no longer runs; the guard tests of `TextOutlineFeasibilityTests` kept; 16 px quality,
  parallel exports, the cost per frame. A failed guard or an export whose end cannot be guaranteed → stop (condition 7).

### 27.5 — Edits and the Inspector *(done, `7fb0d3d`)*
- `SetClipProperties` for the new fields (the merges of D017, one step per choice); the «Анимация» block replacing FADES for
  text clips (D043 Q2), «Обводка» and «Подложка» rows (each on its own), the swatches, the size slider, the 3 × 3 grid,
  «Жирный»; the defaults of "+ Text" (D043 §4); the `EditingLock`; resources en + ru.
- Tests: the edits (steps, merges, refusals, split / trim / duplicate / paste, Undo / Redo), the view models and AXAML, the
  localization guards; the real app at 1024 × 640 and 1440 × 900.

### 27.6 — Verification & closeout *(done by Claude 2026-10-20 — results in `progress.md` and the manual plan; the product owner's acceptance pending)*
- Release and Debug `--no-incremental -warnaserror`; the full suite, `AIVE_HEAVY_TESTS=1`, three `--blame-hang` runs; `git
  diff --check`; a Phase 26 v6 project with text opened and saved (only `formatVersion` differs) and its export compared
  with the Phase 26 build's; the manual plan in the real app on the product owner's portrait gym clip in `ru` and `en`, an
  export watched in a player; D043's acceptance criteria 1–9 checked one by one; the documentation.

## Phase 28 — Transition library: steps (D044)

Approved on 2026-10-20 by the product owner (D044 §11 the answers, the corrections of the readiness check, branch variant
B); the normative text is DECISIONS.md D044. Branch `feat/phase-28-transitions` from Phase 27's accepted tip `3c45c9d`
(Phase 27 not yet published). The permission covers the code, tests, documentation and local commits on this branch; **no
push, pull request or merge without a separate command**; Phase 28's pull request only after Phase 27 is merged. **If
Phase 27 changes before its merge, Phase 28 is not rebased on its own** — the difference is reported to the product owner
with a safe synchronisation plan first. **Step 28.1 starts only after the product owner agrees its visual check on the
real gym clips**; it is a gate — an unsatisfactory Zoom stops the work with Z2 and Z3 proposed, never chosen alone. A need
to change the format, the architecture or agreed behaviour beyond D044 is reported with variants before the change.

Goal: five transitions a bouldering video actually uses — Наплыв, Затемнение, Вспышка, Наезд, Сдвиг — on D025's model, the
Preview equal to the export.

Gates for every step (QG): `dotnet build -warnaserror` 0 / 0; the affected test projects green, the full suite, the heavy
scenes and `--blame-hang` at the closeout; the existing Preview ↔ export parity suites and every expected value unchanged
(a project with dissolves renders bit for bit as with Phase 27 — D044 acceptance criterion 2); a v7 project saved again
differs only in `formatVersion`; every new user-visible text in `Strings` / `Messages` with `.ru` (the D039 guards);
`docs/PHASE28_MANUAL_TEST_PLAN.md` and `progress.md` updated.

Out of scope (D044 §1, §11): a Wipe, motion blur and every blur, glitch, light leaks, shapes / masks, Smooth Cut, repeated
flashes, user curves, keyframes, "apply to every cut", a change of the audio crossfade (D032) or of D043; new dependencies.

### 28.0 — Sync and documentation *(done)*
- The branch from `3c45c9d` with the preparatory documentation carried onto it; D044 approved (the corrections 1–4 of the
  readiness check, variant B); these steps; the manual plan's skeleton `docs/PHASE28_MANUAL_TEST_PLAN.md`;
  `docs/README.md`; ROADMAP and `progress.md`. No product code.

### 28.1 — Visual check of Zoom and Flash (a gate; tests and records only) *(done 2026-10-20 — accepted by the product owner: Zoom and Flash as specified, Z2 / Z3 not needed; D044 "Refined in Step 28.1")*
- Starts only after the product owner agrees the check. Zoom (D044 §2: `K = 1.25`, the change of clip at the peak, 0.3 s)
  and Flash (0.2 s, one white frame at the cut) rendered by the painter's composition rule on the product owner's gym
  clips — horizontal and vertical, 25 / 30 / 60 fps where available — as short MP4s and frame strips for the owner's eye;
  experimental code only in tests or a scratch harness, no product behaviour.
- The result recorded (D044 "Refined in Step 28.1"). **Unsatisfactory Zoom → stop**: Z2 (a one-frame blend of A and B at
  the peak; one frame of handle on each side) and Z3 (no Zoom in the first version) proposed with examples; the owner
  chooses.

### 28.2 — Model, `project.json` v8 and the Core rules *(done 2026-10-20 — D044 "Implementation notes (Step 28.2)"; the product owner's review pending)*
- `Transition.Direction`; the video types and `TransitionRules.RequiresHandles`; the type-dependent `MaxTransitionFrames`;
  the per-type ranges in frames (D044 §5: `ceil` minimum, `floor` maximum); the 1.0 s rule between fill peaks across the
  video tracks (D044 §4) in the edit service's reconciliation and on load; `AddTransition` with a type (the default, else the
  longest that fits ≥ the minimum, never another type); a type change keeping the duration in range, else clamped to the
  nearest allowed value, one step; a direction change one step; the DTOs and the serializer (v8; v7 read; v9 refused).
- Tests: every rule of D044 §3–§9 on all eight rates (the table of §5), the handles only for Dissolve and Push, the 1.0 s
  rule on add / type change / move / ripple / speed / re-grid / load, D025 §5 for every type, the round trip, v7 → v8
  differing only in `formatVersion`, damaged files refused.

### 28.3 — Snapshot, draw plan, painter *(done 2026-10-20 — D044 "Implementation notes (Step 28.3)")*
- The transition zone with the type and the direction; the extended shown range for Dissolve and Push only; in
  `PlaybackSnapshot.LayersAt` the per-type presentation (D044 §2): the fill layer for Dip to black / Flash, the canvas scale
  for Zoom, the canvas offset for Push (offset layers never occlude); `SolidLayer` → `SolidDraw`; the hold outline following
  its frame; a type or direction change keeping the shown range presentation only.
- Tests: the per-frame curves of every type (D044 §2's examples as references), occlusion, Preview ↔ export parity per type,
  direction and zone frame (with rotation, scale, crop, opacity, Fit / Fill, outlines, a vertical canvas, lower and upper
  tracks), dissolves bit for bit as with Phase 27, the end-to-end export with real ffmpeg.

### 28.4 — The UI *(done 2026-10-20 — D044 "Implementation notes (Step 28.4)")*
- «Переход ▾» in the timeline header (the five types; the crossfade on an audio track as before); the Inspector's TRANSITION
  section (the type, the direction for Push, the duration in seconds — field and slider on the 0.1 s grid, the stored
  seconds and frames, «the longest that fits here» — Remove; types that cannot fit disabled with a tooltip); the type's icon
  on the zone; the status messages (the longest that fits, a clamp on a type change, the 1.0 s rule, not enough handles
  naming Dip to black); the `EditingLock`; resources en + ru.
- Tests: the view models and AXAML, the edits through the UI (steps, merges, refusals, Undo / Redo), the localization guards;
  the real app at 1024 × 640 and 1440 × 900.

### 28.5 — Verification & closeout *(done by Claude 2026-10-20; the real-app run and its fixes D1–D8 and the export refusal of D025 §4 on 2026-10-21 — results in `progress.md` and the manual plan; accepted by the product owner 2026-10-21)*
- Release and Debug `--no-incremental -warnaserror`; the full suite, `AIVE_HEAVY_TESTS=1`, three `--blame-hang` runs; `git
  diff --check`; a Phase 27 v7 project with dissolves opened, saved (only `formatVersion` differs) and its export compared
  with the Phase 27 build's; the manual plan in the real app on the product owner's gym clips in `ru` and `en`, an export
  watched in a player; D044's acceptance criteria 1–8 checked one by one; the documentation.

## Phase 29 — Visual templates and the music scenario: steps (D045)

The concept agreed by the product owner on 2026-10-21; the normative text is DECISIONS.md D045 — **a draft until the
gate of Step 29.1 and the product owner's separate approval**. Branch `feat/phase-29-templates` from `origin/main`
`4f0e66a` (the merge of PR #29). Until the approval only documentation, research and prototypes outside the product code
are allowed (no final template system, no change of the product's behaviour); **no push, pull request or merge without a
separate command**. A need to change the format, the architecture or agreed behaviour beyond D045 is reported with
variants before the change.

Goal: five ready-made visual templates for bouldering videos and a verified music scenario, the Preview equal to the
export.

Gates for every step (QG): `dotnet build -warnaserror` 0 / 0; the affected test projects green, the full suite, the heavy
scenes and `--blame-hang` at the closeout; the existing Preview ↔ export parity suites and every expected value unchanged; a
v8 project saved again differs only in `formatVersion`; every new user-visible text in `Strings` / `Messages` with `.ru`
(the D039 guards); `docs/PHASE29_MANUAL_TEST_PLAN.md` and `progress.md` updated.

Out of scope (D045 §8): colour correction and grading, an effect library or stacks, keyframes or curves, free styling,
automatic detection of a result / highlight / fall, third-party assets, the TOP / ZONE hold callout, new audio functions,
new dependencies.

### 29.0 — Sync and documentation *(done 2026-10-21)*
- The branch from `origin/main` `4f0e66a`; D045 (draft) with the product owner's concept of 2026-10-21; C refined above;
  these steps; the manual plan's skeleton with the music audit `docs/PHASE29_MANUAL_TEST_PLAN.md`; `docs/README.md`;
  ROADMAP and `progress.md` (Phases 27 and 28 recorded as merged). No product code.

### 29.1 — The gate: research and prototypes *(done 2026-10-21: REC, VHS, «Боулдер», Highlight accepted; WASTED deferred — D045 §15)*
- Research of the Competition overlay on real World Climbing broadcasts (D045 §2). Prototypes of the five templates on the
  product owner's gym clips (horizontal 1920 × 1080 and vertical 1080 × 1920, 60 fps, with animated hold outlines from the
  real SAM model; WASTED with its sound on and off) by a scratch harness outside the repository
  (`VideosForTest/Phase29_Step29.1`, excluded from git): the frame and the outlines drawn by the app's `CompositionPainter`,
  the treatment as a pixel pass, the graphics on top — the architecture of D045 §3; the costs measured (D045 §4).
- Revision 2 after the product owner's review (D045 §14): REC, VHS and Highlight unchanged; the Competition overlay reduced
  to «Боулдер» (horizontal TOP / ПОПЫТКА, vertical TOP / no status); WASTED reconstructed after GTA V's story-mode screen
  (D045 §13: research, timing, what own means can and cannot reproduce, the open licence questions), horizontal and
  vertical, with the sound and muted, slow motion by the clip's own speed (0.3×).
- Revision 3 after the acceptance of revision 2 with corrections (D045 §13, §11): WASTED's word as plain red letters (no
  outline); the sting at 1× at the switch whatever the clip's speed, the clips' own sound attenuated under it by a fixed
  local envelope (no general ducking) — verified on the mix with the clip at 0.3× and at 1×.
- Deferral (2026-10-21): WASTED taken out of Phase 29 (D045 §15: the sound accent unsatisfactory, no suitable sound
  chosen; six requirements for a return). Its materials archived in
  `VideosForTest/Phase29_Step29.1/out/archive_wasted_deferred` (README there). Proposed scope without WASTED — four
  templates, no template sound — awaiting the product owner's approval before 29.2.
- **Stop**: the product owner reviews the prototypes, the research, the architecture, the performance estimate, the music
  audit's plan and the open questions (D045 §11); the implementation starts only after D045 is approved.

### 29.2 — The music audit *(done 2026-10-21 — no defect found; observation O1, the manual plan part B)*
- Part B of the manual plan on the product owner's music and generated reference signals; the report of defects and UX
  gaps; fixes only those the product owner agrees to, with tests.

### 29.3 — Model, `project.json` v9 and the Core rules *(done 2026-10-21 — D045 §16)*
- The template clip, its typed fields and defaults, the template's clock (Split / Duplicate / trim), validation, v9 (v8
  read unchanged), the edit service's commands with one Undo step each; tests.

### 29.4 — Rendering *(done 2026-10-21 — D045 §16; no template sound: WASTED deferred)*
- The template layer and its draw operations; the treatment pass in the export's rasterizer and the Preview; the
  graphics; the built-in sound span; parity tests at the canvas size for every template, horizontal and vertical, with
  outlines and texts below.

### 29.5 — The UI *(done 2026-10-21)*
- The «Шаблон ▾» menu, the Inspector's sections, Russian texts and the D039 guards.

### 29.6 — Verification & closeout *(done by Claude 2026-10-21; the corrections of D045 §17 on 2026-10-21/22 — results in `progress.md` and the manual plan; accepted by the product owner 2026-10-22)*
- Release and Debug `--no-incremental -warnaserror`; the full suite, `AIVE_HEAVY_TESTS=1`, three `--blame-hang` runs;
  `git diff --check`; a v8 project opened and saved (only `formatVersion` differs); the Preview's and the export's
  performance with a treatment measured in the app; the manual plan in the real app; D045's acceptance criteria one by one;
  the documentation.
- Merged into `main` as `e28a9d3` (PR #30, 2026-10-22; CI green on the pull request).

## Phase 30 — Stabilisation and the portable build S1: steps (D046)

The plan approved by the product owner on 2026-10-22 with the refinements of D046 §2–§9; the normative text is
DECISIONS.md D046. Branch `feat/phase-30-stabilization` from `origin/main` `e28a9d3` (the merge of PR #30). Each step
starts on the product owner's command after the previous one is reviewed; **no commit, push, pull request or merge
without a separate command**. A need to change the format, the architecture or agreed behaviour is reported with variants
before the change.

Goal: S1 — the main path of D042 §1 (without step 4) completed in a portable Windows build that starts outside the
development environment (D042 "Refined after the merge of Phase 26", D046 §10).

Gates for every step that changes code (QG): `dotnet build -warnaserror` 0 / 0; the affected test projects green; the
full suite, the heavy scenes and `--blame-hang` at the closeout; the Preview ↔ export parity suites and every expected
value unchanged; a v9 project saved again byte for byte (no format change); every new user-visible text in `Strings` /
`Messages` with `.ru` (the D039 guards); `docs/PHASE30_MANUAL_TEST_PLAN.md` and `progress.md` updated.

Out of scope (D046 §9): hold tracking, contour smoothing, camera motion compensation, occlusion, COROS / FIT, the
automatic rough cut, WASTED and the other deferred functions, an installer, code signing, automatic updates, shipping
`DirectML.dll`, a move to .NET 10, the Final UX Pass, new functionality, a format change.

Dependencies: 30.0 → 30.1 → 30.2 → 30.3 → 30.4 → 30.7; 30.5 and 30.6 after 30.2, alongside 30.3; a fix in 30.4 rebuilds
the portable folder and re-runs the rows it touches. **As it happened:** 30.3 was run on 2026-10-23 before 30.2; 30.2 was
completed on 2026-10-24 as a partial clean-environment check (A3–A6, A8 and A10 PASS; A7 and A9 BLOCKED by the environment;
no defect found).

### 30.0 — Documentation *(done; accepted by the product owner 2026-10-22)*
- The branch from `origin/main` `e28a9d3`; D046; these steps; the skeleton of `docs/PHASE30_MANUAL_TEST_PLAN.md`;
  `docs/README.md`; the outdated statuses of Phases 27–29 and of the current phase in this file, ROADMAP, `progress.md`,
  README and D045 aligned with the merges. No code, tests or build scripts.

### 30.1 — The portable build *(done 2026-10-22 without FFmpeg — D046 §13; awaiting the product owner's review and the choice of the FFmpeg distribution)*
- A publish script (self-contained `win-x64`, a folder, no single-file, no trimming) run from a clean checkout; a version
  in the build; the user's instruction in the build's folder (start, FFmpeg, the model per `docs/AI_MODEL.md`, where the
  logs are).
- The tools' configured paths resolved relative to the application folder (`ExecutableLocator`; absolute paths and the
  `PATH` fallback unchanged), with tests.
- The licence notices of every shipped component read from the restored packages (D046 §3).
- FFmpeg (D046 §2): the licence, the composition and the obligations of the candidate distribution checked and reported;
  **the binaries bundled only after the product owner's choice**.
- The folder's layout and size recorded.

### 30.2 — The clean-environment check *(done 2026-10-24 — a partial check on the development machine; A7 and A9 blocked by the environment)*
- Windows Sandbox or a clean VM if available, otherwise a partial check with its limits documented (D046 §4): the start
  (double click, another current directory, a path with spaces and Cyrillic), the Russian interface, the messages without
  FFmpeg and without the model, the hold markup with the model installed, the Visual C++ runtime, the fonts of the
  templates. Manual plan part A.
- **The check of 2026-10-24** (local, Claude) in the portable build `0.30.0+cf718bc7dd.dirty` — the product owner's decision:
  a full clean environment is not required now; the available scenarios are checked on this machine and the absence of a VM /
  Windows Sandbox is recorded as a limit of the check (it does not block the development). **PASS:** A3 (the start by a double
  click — the main window, the Russian interface, no error dialog, the log in `%LOCALAPPDATA%\AiVideoEditor\logs`), A4 (the
  start from another current directory — `appsettings.json` and the tools' relative paths read from the application folder), A5
  (a folder with spaces and Cyrillic — the start, the import, the save and the export), A6 (no FFmpeg — the existing messages
  at the import and at the export, no crash), A8 (the model installed — the check and the loading progress, DirectML, a
  candidate contour confirmed), A10 (the templates' fonts — installed, the templates drawn as in the development build, no
  font warning). **BLOCKED by the environment:** A7 (the model cannot be uninstalled here — the app's data root cannot be
  redirected and the user's model files are not touched) and A9 (the Visual C++ runtime cannot be removed;
  `onnxruntime.dll` imports MSVCP140 / MSVCP140_1 / VCRUNTIME140 / VCRUNTIME140_1 → the runtime **is** required on a machine
  without it). **No defect was found.** The evidence in each row: `docs/PHASE30_MANUAL_TEST_PLAN.md` part A.
- **Open:** a full clean environment (Windows Sandbox / a VM) stays open and is not blocking; the limits of the partial check
  are listed in the manual plan's part A.

### 30.3 — The main path in the portable build *(run 2026-10-23; the owner's manual checks of 2026-10-24 — the step's checks are complete)*
- D042 §1 without step 4 on the product owner's footage at 1024 × 640 and 1440 × 900 (manual plan part B): import; cut,
  split, arrange; holds chosen and outlined; animated text; transitions; music with its volume and a template; framing,
  the Preview and the export, watched in an external player.
- The defects D1…Dn recorded with their severity (D046 §5); the list given to the product owner.
- **The run of 2026-10-23** (local, Claude): the portable build `0.30.0+cf718bc7dd.dirty` with gyan.dev FFmpeg 9.0.1 from
  its `ffmpeg\` folder (not on `PATH`), the product owner's clips of 2026-09-27 and an MP3, at 1440 × 900 and 1024 × 640
  (two projects) — **B1–B5, B7 and B8 PASS** at both sizes; **B6 BLOCKED** for the mix by ear and **B9 BLOCKED** for the
  export in an external player (both need a person); the frames at the checkpoints equal to the Preview. What was checked
  in each row: `docs/PHASE30_MANUAL_TEST_PLAN.md` part B.
- **The owner's manual checks of 2026-10-24** (on the artefacts of that run — the same portable build and the same two
  exported files, no new export): **B6 PASS** (the mix by ear — both exports listened to: the music and the gym's own
  sound are correct, the fade-out is normal, no critical clicks, level jumps or desynchronisation noticed); **B9 PASS**
  (both exports in an external player — the captions, the hold outlines, the transitions, the templates and the framing
  look correct, no critical playback problems noticed); the check that was NOT RUN in the local run — the window dragged
  between the monitors with the real mouse at 125 % ↔ 100 %, the interface's interaction and the clicks — **PASS** (no
  problems found, the interface's elements work correctly). The observations are the owner's (by ear and by eye); no new
  measurements were made. **B1–B9 PASS** — no row of the main path waits for a person. The manual plan's parts B and E
  keep both runs apart.
- **The defects:** no blocker and no major defect; one minor / UX defect — **U1** («+ Текст» on a project with one video
  track refuses without saying to add a track) — **not fixed: the product owner's decision of 2026-10-24**; the remark stays
  documented, no code change (D046 §5); the list was given to the product owner in the manual plan's part E. The former D1
  (the window seemingly kept the 125 % layout on a 100 % monitor) is **withdrawn** — an artefact of the test driver
  (`pwsh.exe` is system aware), not a defect of the application.
- **Open, not defects:** **M1** — the music of the projects is the owner's MP3 transferred on Claude's request; its
  measured parameters (MP3, 44 100 Hz, stereo, 199.6 s) meet M1's definition in `docs/PHASE29_MANUAL_TEST_PLAN.md`
  part B, the optional M4A / AAC counterpart is not provided; the rows of Phase 29 that waited for M1 keep their recorded
  results. **B1's «3 / 4 files imported and analysed»** — the final project holds all four assets (confirmed by the owner
  and by `project.json`), but which file was not imported at that step is not established by the existing evidence; the
  ambiguity stays open and is not a defect.

### 30.4 — Fixes and the flakes *(done — the flakes 2026-10-22, `b4edb87`; the defect part closed 2026-10-24: U1 not fixed by the product owner's decision, no code change)*
- Blocker / major defects of the main path fixed, each with a test where it can be automated; minor / UX defects only
  those the product owner approves. **Done 2026-10-24 without a code change** — the defect list of 30.3 holds no blocker and
  no major defect, only the minor / UX defect U1; the product owner decided **not to fix it**, so the remark stays
  documented as a minor / UX defect. The reason is the decision, **not** a confirmation that the defect is absent (D046 §5).
  No other defect of Step 30.4 waits for a decision: the list of 30.3 has one entry, and the withdrawn observation D1 is not
  a defect of the application.
- The two known flakes (D046 §0) investigated: fixed if the cause is found, otherwise recorded. **Done** — both causes
  in the tests, reproduced: (1) `FfmpegProcess.LiveProcesses` is process-wide and `HdrDecodeTests`, `HdrThumbnailTests`,
  `WaveformIntegrationTests` ran ffmpeg in parallel with the tests comparing it with a baseline
  (`FfmpegExportEncoderFailureTests`; also `FfmpegAudioDecoderIntegrationTests.PlaybackLifecycle_…`) — 10 / 10 parallel
  runs failed, 5 / 5 serial runs passed; (2) `FfmpegExportEncoderFailureTests` and `StrictEndOfStreamTests` took every
  ffmpeg / PING on the machine started after the test for their own — every run failed with unrelated ffmpeg running.
  Fixed in the tests only: the three classes join `MediaCollection` (the rule at its definition); the two classes run
  ffmpeg and ping through `ProcessAliases` (hard links named for the test run) and look for those names only — a
  process they leave behind still fails the check (shown). No product code, delay, retry or looser check. After: 50 / 50
  runs green with the parallel classes and 14–15 unrelated ffmpeg running; the full Video.Tests 2 × 363 / 0; the
  solution build `-warnaserror` 0 / 0. The unnamed Video.Tests failure of Phase 29 was not captured and is not
  identified; the tests hit by the two mechanisms are its likely candidates (a hypothesis). These remaining gates were
  later run in Step 30.7 (2026-10-24) — see the 30.7 section below; they do not hold Step 30.4.
  Left as they are: `FfprobeTerminationTests` and `AnalysisConcurrencyIntegrationTests` look for every PING on the machine
  (an unrelated ping is rare).

### 30.5 — Performance *(measured 2026-10-24 — the targets agreed by the product owner on 2026-10-24; one open observation from C4 remains)*
- The typical project of D046 §6 measured in the portable build (manual plan part C); the results reported; the targets
  agreed with the product owner afterwards.
- **The measurement of 2026-10-24** (local, Claude) on the development machine (Windows 11 IoT LTSC 26100, Ryzen 7 7800X3D,
  31.2 GiB RAM, RTX 4070 SUPER; the load during the measurements 0.7 % of the cores) in the portable build
  `0.30.0+cf718bc7dd.dirty`, the window 1440 × 900 on the 100 % monitor, the profile `perf-1440` (4:31, 1920 × 1080 @ 60 fps,
  6 clips on 3 tracks — V1: three video clips with the hold markup, V2: a text clip with an appearance and a template clip,
  A1: the music at 50 % — and the transitions of D044): **C1** the project opens in 0.21 s (the picture settles in 0.52 s; the first open without the cache
  in 1.44 s); **C2** the move and the trim answer within 13–22 ms, the split is drawn within ~290 ms; **C3** the whole
  project plays in **98.6 % of the real time** (the Preview draws 58.7 /s of a 60 fps project); **C4** a jump by the ruler
  shows the new frame in 109–121 ms, a scrub in 309–499 ms after the release; **C5** the export takes **21:21.52 =
  4.72 × the real time**; **C6** the app's working set up to 1077 MiB during the export, with the ffmpeg processes
  ≈ 3.7 GiB. The method, the evidence and the **agreed** targets: `docs/PHASE30_MANUAL_TEST_PLAN.md` part C.
- **Open observation from C4:** during a continuous scrub by dragging the ruler the Preview does **not** update live; the
  new frame appears only after the mouse-up. This is recorded as an open observation, not as an agreed defect. No fix or
  optimisation of Preview/Export was authorised by the product owner.
- **Closed:** the targets are agreed (D046 §6) — the acceptance criterion 6 of S1 is met as far as the measurement and the
  agreement go. The observation above does not meet the threshold of §5 by itself.

### 30.6 — The open manual checks *(the inventory done 2026-10-22; the main-path rows run in 30.3 — the local run of 2026-10-23 and the owner's checks of 2026-10-24)*
- The NOT RUN rows of the earlier manual plans inventoried (manual plan part D): those on the main path run, the rest
  recorded with their reason; HDR / PQ (Phase 18) and R10 (Phase 17) not blocking (D046 §7).
- **The inventory:** `docs/PHASE30_MANUAL_TEST_INVENTORY.md` — 623 rows of the earlier plans (523 PASS, 0 FAIL, 27
  PARTIAL, 37 AUTO ONLY, 29 NOT RUN, 7 other) and Phase 30's own rows, the open ones merged into 28 items with their
  evidence, needs and relation to the main path. Its main-path items were run with the manual run of B1–B9 in 30.3
  (2026-10-23) and the owner's checks (2026-10-24): **all the main-path rows are PASS** — the mix by ear (B6) and the
  export in an external player (B9) by the owner. The rest is recorded with its reason; R10 and the HDR / PQ checks stay
  non-blocking. The inventory's rows are summarised in the manual plan's part D.

### 30.7 — Verification & closeout *(technical verification done 2026-10-24; formal S1 acceptance not declared)*
- Release and Debug `--no-incremental -warnaserror`; the full suite, `AIVE_HEAVY_TESTS=1`, three `--blame-hang` runs;
  `git diff --check`; a v9 project opened and saved byte for byte; the final portable folder rebuilt and checked again
  through 30.2's rows; D046's acceptance criteria one by one; the documentation; the product owner's acceptance of S1.
- **Technical verification (2026-10-24):** Release and Debug builds 0/0; the full suite 0 failed (4536 passed, 2 skipped);
  `AIVE_HEAVY_TESTS=1` 0 failed (4538 passed, 0 skipped); three `--blame-hang` runs 0 failed/hung; `git diff --check` clean;
  v9 byte-for-byte round-trip covered by the automated `Project.Tests` suite (`TemplatePersistence` and `ProjectService`
  tests); the portable folder rebuilt (119 MiB, 349 files, FFmpeg not included); the available Step 30.2 rows re-run on the
  current machine (A3, A4, A5, A6, A10 PASS; A7 and A9 remain BLOCKED by the environment; A8 relies on the still-installed
  model and the previous PASS of 2026-10-24); the Preview ↔ export parity suites and the D039 localization guards ran as
  part of the full suite (Phase 30 changed no rendering or localization code).
- **What is not closed:** the full acceptance of S1 — FFmpeg distribution and licence review, the notices review (A2), a
  clean-environment check without the current machine's limitations (A7/A9), the C4 live-preview observation, **CI green**
  (D046 §10.5: the Phase 30 branch has not been pushed/merged, so no CI run exists; the last green CI was PR #30
  `e28a9d3`), and the product owner's final sign-off. These are deferred to a separate release-preparation task by the product owner's decision
  of 2026-10-24.

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
