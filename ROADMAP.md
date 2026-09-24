# Roadmap

The canonical phase list, with per-phase scope, is `docs/DEVELOPMENT_PLAN.md`.
This file summarizes status only.

## Completed

### Phase 0 — Architecture
Commit `0ccc9be`.

### Phase 1 — Basic UI skeleton
Commit `88a608b`. Five panels (Toolbar, Media Browser, Preview, Timeline,
Inspector) laid out with mock data.

### Phase 2 — Project state and real media import
Commit `e7a8ef0`.

Known verified behavior:
- media import works
- multiple files can be added
- New clears the current project and the undo/redo stack
- unsupported files are blocked by file filters and by extension validation
- duplicates (same path) are skipped

### Phase 3 — Media analysis foundation
Commit `a8e5bac`.

Known verified behavior:
- ffprobe is located (configured `Ffmpeg:FfprobePath` or PATH)
- media metadata is obtained in the background after import
- the previous "FFprobe could not be found" blocker was resolved

Only ffprobe is integrated. ffmpeg itself (thumbnails, decode, export) is not
used yet.

### Phase 4 — Timeline
Commit `33c5c02`, merged into `main`. Tracks, clips, selection, move, trim, split, delete,
playhead, zoom, snapping — all as undoable commands. Decisions: DECISIONS.md D006–D008.

### Phase 5 — Preview
Branch `feat/phase-5-playback`: video checkpoint `85ca216`, audio in the Phase 5 closeout
commit. Timeline playback in the Preview with video (exact source-frame selection, ffmpeg
decoder) and audio (ffmpeg decode, mixer, WASAPI output as master clock), UI transport and
playhead ↔ seek. Manually validated by the product owner (video and audio). Decisions:
DECISIONS.md D009–D013. Details and verification: `progress.md`.

### Phase 6 — Project persistence
Branch `feat/phase-6-project-persistence` (from `acc1a49`), Phase 6 commit. Project folder
with `project.json` (format v1, DTOs separate from entities, `MediaTime` as long ticks);
Open with full validation before the current project is replaced; atomic Save / Save As;
undo save point for dirty tracking; missing media opens as offline (not dirty, not probed);
saved ffprobe metadata reused; autosave every 2 min into a separate recovery file, startup
recovery offer (Recover / Discard / Not now); Save / Don't Save / Cancel before New, Open and
Close; window title with `*`; Ctrl+N / O / S / Shift+S. UI manually validated by the product
owner. Decisions: DECISIONS.md D014–D016. Details and verification: `progress.md`.

The open question about playhead / zoom / snapping was decided at the start of Phase 7:
session state (D015).

## Current

Phase 7 — Basic editing, branch `feat/phase-7-basic-editing`. **Complete**: Steps 1–9 (last
checkpoint `48a3f54`), Step 10 closeout (audit, full test runs, integration smoke in the running app);
manually accepted by the product owner on 2026-09-24. Speed, volume/
mute, opacity, transform, crop and text clips per clip (no keyframes), multi-layer Preview,
`project.json` v2 (reads v1). Decisions: DECISIONS.md D017–D022. Carried into Phase 8: the export
must reproduce D018 exactly and needs font files for text (D021). Known issue (pre-Phase 7, separate
task): closing the app after a project with media was open hangs the process. Details: `progress.md`.

## Future phases

7 Basic editing, 8 Export, 9 Quality — see `docs/DEVELOPMENT_PLAN.md`.

## Rule

Do not silently mark a phase complete.

A phase is complete only after its acceptance criteria are implemented and verified.
