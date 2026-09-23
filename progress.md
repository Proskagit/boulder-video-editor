# Project Progress

## Current phase

Phase 4 — Timeline (not started; design in progress).

## Last known state

Phases 0–3 are complete and committed (see `ROADMAP.md` for commit ids).
Phase 3 was committed on branch `feat/phase-3-media-analysis`.

ffprobe is configured; the previous "FFprobe could not be found" error was resolved.

Not implemented yet:
- Timeline: `TimelineViewModel` builds mock tracks/clips; no `Clip` is ever
  created from a `MediaAsset`. The `Timeline` project contains no code.
- Preview: transport only toggles a flag; current time/duration are mock values.
- Open/Save/Export: Toolbar reports "not implemented yet".

## Completed

- Phase 0
- Phase 1
- Phase 2
- Phase 3

## Current task

Phase 4 — design/plan stage. No Phase 4 code yet.

## Known issues

- `ffmpeg-*.log` is never written: nothing tags log events with `Area=Ffmpeg`.
- Media analysis has no concurrency limit and no cancellation on New Project.
- `MediaAnalysisCoordinator` relies on the captured UI SynchronizationContext
  to apply results on the UI thread.
- Toolbar tooltips mention Ctrl+Z / Ctrl+Y, but no hotkeys are bound.
- Test coverage: only `MediaTime` (tests/Core.Tests).

## Verification

Last documented successful state:
- local Debug build artifacts (2026-09-17) are newer than all sources at the
  Phase 3 commit; build/tests were not re-run for the documentation sync.

## Instructions for Claude

Update this file after substantial milestones.

Do not fabricate progress.

When uncertain, inspect the repository and Git history first.
