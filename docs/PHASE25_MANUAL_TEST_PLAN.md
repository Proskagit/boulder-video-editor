# Phase 25 — manual test plan

Manual checks of the technical backlog and the small UX improvements (DECISIONS.md D040), and of the Phase 24 rows that were
"auto only" (D040 §6). Run in the real app on Windows with the default `appsettings.json` (`App:Language` = `ru`) unless a row
says otherwise, at **1024 × 640** (the window's minimum) and **1440 × 900** (the default), in Avalonia's logical pixels.

Status column — kinds of evidence, never mixed:
- **auto** — covered by automated tests only, not checked in the real app;
- **local (Claude)** / **PO** — the real-app run on Windows / the product owner's check;
- **NOT RUN** — not checked in the real app (the reason given).

| # | Scenario | Expected | Automated coverage | Status |
|---|---|---|---|---|
| 1 | Start the exe from another current directory (a console in another folder) with `App:Language` = `en` next to the exe | The interface in English: the file next to the exe is read | 25.1 | |
| 2 | The same with `Ffmpeg:FfmpegPath` / `FfprobePath` set next to the exe, ffmpeg not on PATH | Media analysed, export possible | 25.1 | |
| 3 | Previous / Next marker (FU-21.1) | A jump shows «Переход к маркеру 00:…»; with no marker «Перед курсором нет маркера» / «После курсора нет маркера»; a later jump replaces the warning | 25.2 | |
| 4 | Markers with the playhead past the sequence's end, on a marker, next to a marker | The right marker each time, never a false «нет маркера» | 25.2 | |
| 5 | FU-22.1: a click on the ruler at 00:00:10:00 with a video clip selected, then «Разметить зацепки» | The mode stays on; a seek by the user afterwards leaves it | 25.3 | |
| 6 | Fade In / Fade Out sliders, video clip and audio clip | The slider and the field agree; its maximum follows the clip's length; one drag = one Ctrl+Z; typing still works | 25.4 | |
| 7 | Hold line width slider | As row 6 | 25.4 | |
| 8 | Volume slider on a clip with sound (Phase 24 gap) | As row 6; the sound follows | 25.4 | |
| 9 | Timeline tool bar icons at 1024 and 1440 px | Each command an icon and its Russian label, tooltips with shortcuts, at most two rows, nothing cut | 25.5 | |
| 10 | Track and marker button tooltips (Phase 24 gap) | Russian tooltips | AXAML guard | |
| 11 | The Inspector's Звук section (Phase 24 gap) | Labels whole, Russian units | `RussianTextsTests` | |
| 12 | A hold model error (Phase 24 gap) | A Russian status, the technical detail in parentheses | `HoldMarkupUiTests` | |
| 13 | Open a damaged project (Phase 24 gap) | A Russian dialog, the reason in parentheses | workflow tests | |
| 14 | A failed export and the warnings dialog (Phase 24 gap) | Russian dialogs, the FFmpeg detail in parentheses | `ExportWorkflowTests` | |
| 15 | A Phase 24 project (`d3df0ba`) opened and saved with `ru` and with `en` | Byte for byte the base build's save (v6) | format tests | |
