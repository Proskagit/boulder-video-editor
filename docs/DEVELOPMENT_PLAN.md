# Development Plan

Iterative phases, in order. Do not start a phase before the previous one builds
and runs cleanly.

- [x] **Phase 0 — Architecture.** Solution, projects, DI, logging, base MVVM,
      basic window, Git-ready structure. *(this commit)*
- [x] **Phase 1 — Basic UI.** Toolbar, Media Browser, Preview, Timeline, Inspector
      panels laid out with mock data. Visual skeleton only. *(this commit)*
- [x] **Phase 2 — Project state and real media import.** In-memory project state
      (`IProjectService`), native file picker, extension-validated import with
      duplicate detection, Media Browser wired to real imported files, Inspector
      shows real file properties. *(this commit)*
- [x] **Phase 3 — Media analysis foundation.** FFprobe-backed `IMediaAnalysisService`
      (Video subsystem), configurable ffprobe location (`IFfprobeLocator`,
      Infrastructure), background analysis after import via
      `MediaAnalysisCoordinator` (never blocks the UI thread), real technical
      metadata (duration/resolution/fps/codecs/bitrate/sample rate/channels) in
      the Media Browser and Inspector. No playback, timeline editing, or
      thumbnails yet. *(this commit)*
- [ ] **Phase 4 — Timeline.** Tracks, clips, selection, move, trim, split,
      delete, playhead, zoom, snapping — all as undoable commands.
- [ ] **Phase 5 — Preview.** Wire timeline playhead to the preview player;
      synchronize play/pause.
- [ ] **Phase 6 — Project persistence.** Open/Save/Save As, project.json,
      autosave, missing-media detection.
- [ ] **Phase 7 — Basic editing.** Speed, volume, opacity, transform, crop, text.
- [ ] **Phase 8 — Export.** FFmpeg render pipeline: Timeline → MP4 (H.264/AAC).
- [ ] **Phase 9 — Quality.** Performance profiling, caching, error handling,
      polish, hotkeys, waveform, thumbnails.

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
