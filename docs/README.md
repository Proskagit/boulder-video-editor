# Project Documentation

This directory contains:

- `DEVELOPMENT_PLAN.md` — **the canonical roadmap**: the phase list, per-phase steps and acceptance criteria, the one table
  of the phases' state and of what comes next ("Phase and step numbering" → "Official phases"), standing architectural rules
- `EXPORT_MANUAL_TEST_PLAN.md` — manual export checks (Phase 8), re-run as a regression at later closeouts
- `PHASE9_MANUAL_TEST_PLAN.md` — manual checks of Phase 9 (quality), run at its closeout
- `PHASE10_MANUAL_TEST_PLAN.md` — manual checks of Phase 10 (fades, cross dissolve), run at its closeout
- `PHASE11_MANUAL_TEST_PLAN.md` — manual checks of Phase 11 (media re-check, relink, recent projects), completed per step
  and run at its closeout (Step 11.9)
- `PHASE12_MANUAL_TEST_PLAN.md` — manual checks of Phase 12 (tracks, media removal, ripple delete, copy / paste /
  duplicate, markers, import during New), completed per step and run at its closeout (Step 12.9)
- `PHASE13_MANUAL_TEST_PLAN.md` — manual checks of Phase 13 (canvas size, frame rate, project and export settings),
  completed per step and run at its closeout (Step 13.10)
- `PHASE14_MANUAL_TEST_PLAN.md` — Phase 14 (stabilization, no new user functionality): the regression run of its
  closeout (Step 14.8)
- `PHASE15_MANUAL_TEST_PLAN.md` — manual checks of Phase 15 (track controls, trim to the playhead, ripple trim, slip,
  In / Out range, text size and audio status), completed per step and run at its closeout (Step 15.9)
- `PHASE16_MANUAL_TEST_PLAN.md` — manual checks of Phase 16 (Source viewer, source In / Out, Insert / Overwrite),
  completed per step and run at its closeout (Step 16.7)
- `PHASE17_MANUAL_TEST_PLAN.md` — manual checks of Phase 17 (audio editing: unlink / link, J / L cuts, sound-only
  Insert / Overwrite, audio fades and crossfades, `project.json` v4, the real audio device check R10), completed per step
  and run at its closeout
- `PHASE18_MANUAL_TEST_PLAN.md` — manual checks of Phase 18 (HDR → SDR of phone footage: colour metadata, the picture in
  the Preview, the thumbnails and the export, the regression against Phase 17), completed per step and run at its closeout

- `PHASE19_MANUAL_TEST_PLAN.md` — manual checks of Phase 19 (AI hold markup: the markup mode, the route outline in the
  Preview and the export, the model's absence and the CPU fallback, the threshold examples), run at its closeout
- `PHASE20_MANUAL_TEST_PLAN.md` — manual checks of Phase 20 (the manual correction of hold contours: handles, drag,
  insert, removal, crossing, undo / redo, save / reopen, the Preview and the export)
- `PHASE21_MANUAL_TEST_PLAN.md` … `PHASE25_MANUAL_TEST_PLAN.md` — manual checks of Phases 21–25 (the outline animation,
  timeline selection and making room, Move / Scale / Crop in the Preview, the Russian localization, the backlog and small
  UX improvements), run at their closeouts
- `PHASE26_MANUAL_TEST_PLAN.md` — manual checks of Phase 26 (the canvas offered by the first video, Fill / Fit, the export
  of portrait and landscape phone clips), completed per step and run at its closeout
- `PHASE27_MANUAL_TEST_PLAN.md` — manual checks of Phase 27 (text animation, outline and background, the placement grid,
  bold, the defaults of a new text, v6 texts unchanged), completed per step and run at its closeout
- `PHASE28_MANUAL_TEST_PLAN.md` — manual checks of Phase 28 (the transition library: Наплыв, Затемнение, Вспышка, Наезд,
  Сдвиг; the visual gate of Step 28.1 on real gym clips; durations, source handles, the 1.0 s rule, v7 dissolves
  unchanged), completed per step and run at its closeout
- `PHASE29_MANUAL_TEST_PLAN.md` — manual checks of Phase 29 (the visual templates REC, VHS, «Боулдер», Highlight — the gate of
  Step 29.1 on real gym clips, WASTED deferred; the audit of the existing music scenario with its inputs and objective defect criteria), completed per step
- `PHASE30_MANUAL_TEST_PLAN.md` — manual checks of Phase 30 (the portable build S1 in a clean environment, the main path
  in that build, the performance of a typical project, the inventory of the open manual checks, the defects found),
  completed per step
- `PHASE30_MANUAL_TEST_INVENTORY.md` — Step 30.6: the inventory of the open manual checks of the earlier phases
- `PHASE31_MANUAL_TEST_PLAN.md` — manual checks of Phase 31 (automatic hold proposals; the A / B / E checks open)
- `PHASE_HOLD_TRACKING.md` — R2 → E: the research and the plan of the outlines following the camera (in Russian; its
  camera-motion part merged in PR #31, unnumbered)
- `TARGET_WORKFLOW.md` — the target user scenario agreed with the product owner (in Russian; the basis of D048)
- `HOLD_AUTO_DETECTION_RESEARCH.md` — D048 stage 1 (Phase 32, paused): the results of the automatic route detection
  research (in Russian)
- `D048_STAGE1_ACCEPTANCE.md` — D048 stage 1: the acceptance procedure on the owner's clips (not run; the stage paused)
- `PHASE_COROS.md` — the record of Phase 35 (COROS trainings and the heart rate): the real export, the specification, the
  steps and their state, the research of Step 35.3 and the result of its evaluation 35.3d
- `PHASE35_MANUAL_TEST_PLAN.md` — manual checks of Phase 35 (the import, the time model, the zero override, the heart-rate
  overlay, the closeout)
- `PHASE37.md` — the record of Phase 37 (preliminary route starts from a COROS FIT and the global start calibration,
  D051): the steps as implemented, the decisions of 2026-11-03, the verification
- `PHASE35_5_MANUAL_ACCEPTANCE.md` — the owner's step-by-step acceptance of Step 35.5 (the heart-rate overlay) in the
  running app, in Russian; run 2026-11-03 — PASS
- `PHASE37_MANUAL_TEST_PLAN.md` — manual checks of Phase 37 (not run yet)
- `drafts/ffmpeg-minimal-9.0.2-1/` — drafts of the minimal FFmpeg build's release plan and notices (S1 release
  preparation, not published)
- `AI_MODEL.md` — the AI hold model: files and checksums, installing, messages, licences and notices, the export recipe

Agent-oriented project context lives in the repository root:

- `CLAUDE.md` — instructions for AI coding agents
- `ARCHITECTURE.md` — verified architecture and domain structure
- `ROADMAP.md` — the product concept, a summary of the phases' state and the release stopping points (S0–S4)
- `DECISIONS.md` — durable architectural decisions
- `progress.md` — the current state in brief and the dated working log

The repository source code remains the ultimate implementation source of truth.
