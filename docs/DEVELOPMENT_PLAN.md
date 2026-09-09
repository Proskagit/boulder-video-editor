# Development Plan

Iterative phases, in order. Do not start a phase before the previous one builds
and runs cleanly.

- [x] **Phase 0 — Architecture.** Solution, projects, DI, logging, base MVVM,
      basic window, Git-ready structure. *(this commit)*
- [x] **Phase 1 — Basic UI.** Toolbar, Media Browser, Preview, Timeline, Inspector
      panels laid out with mock data. Visual skeleton only. *(this commit)*
- [ ] **Phase 2 — Media import.** File picker, drag & drop, FFmpeg metadata
      probing, thumbnails.
- [ ] **Phase 3 — Timeline.** Tracks, clips, selection, move, trim, split,
      delete, playhead, zoom, snapping — all as undoable commands.
- [ ] **Phase 4 — Preview.** Wire timeline playhead to the preview player;
      synchronize play/pause.
- [ ] **Phase 5 — Project system.** New/Open/Save/Save As, project.json,
      autosave, missing-media detection.
- [ ] **Phase 6 — Basic editing.** Speed, volume, opacity, transform, crop, text.
- [ ] **Phase 7 — Export.** FFmpeg render pipeline: Timeline → MP4 (H.264/AAC).
- [ ] **Phase 8 — Quality.** Performance profiling, caching, error handling,
      polish, hotkeys, waveform.

## Architectural rules that must hold at every phase

- UI (`src/UI`, `src/App`) contains no business logic — only view models that
  call into Core service interfaces.
- Moving/trimming a clip changes project state only; FFmpeg only runs at
  thumbnail-generation or export time.
- Every user-triggered project mutation is an `IUndoableCommand` executed
  through `IUndoRedoService`.
- Internal time is `MediaTime` (100ns ticks), never a raw frame integer;
  frame numbers are derived on demand for a given FPS.
- Heavy work is `async` and off the UI thread.
