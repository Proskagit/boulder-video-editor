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

## Current

### Phase 5 — Preview
Checkpoint committed on branch `feat/phase-5-playback` ("Phase 5: playback engine and
preview integration"): video playback of the timeline in the Preview — exact source-frame
selection, ffmpeg decoder, playback clock, snapshot, playback service, UI transport and
playhead ↔ seek. Manually validated by the product owner. Decisions: DECISIONS.md
D009–D012. Details and verification: `progress.md`.

Not yet done in Phase 5: audio playback (NAudio output, mixer, audio master clock — D010).
The phase is not complete until audio playback is implemented and verified.

## Future phases

6 Project persistence, 7 Basic editing, 8 Export, 9 Quality —
see `docs/DEVELOPMENT_PLAN.md`.

## Rule

Do not silently mark a phase complete.

A phase is complete only after its acceptance criteria are implemented and verified.
