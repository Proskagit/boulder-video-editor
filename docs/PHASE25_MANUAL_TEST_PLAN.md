# Phase 25 — manual test plan

Manual checks of the technical backlog and the small UX improvements (DECISIONS.md D040), and of the Phase 24 rows that were
"auto only" (D040 §6). Run in the real app on Windows with the default `appsettings.json` (`App:Language` = `ru`) unless a row
says otherwise, at **1024 × 640** (the window's minimum) and **1440 × 900** (the default), in Avalonia's logical pixels.

Status column — kinds of evidence, never mixed:
- **auto** — covered by automated tests only, not checked in the real app;
- **local (Claude)** / **PO** — the real-app run on Windows / the product owner's check;
- **NOT RUN** — not checked in the real app (the reason given).

Run on 2026-10-19 by Claude on Windows 11 (OS UI culture ru-RU, display at 125 %), the Debug build of the branch (Steps
25.1–25.5, `4c53633`), driven by UI Automation and system mouse input (the keyboard by `SendKeys`, which did not reach the
app for Ctrl+Z — Undo / Redo were pressed on the tool bar instead); results read from UI Automation (timecodes, the status
bar, the sliders' values and limits), screenshots and the saved files. Fixture: `tools/manual/New-Phase10DissolveFixture.ps1`
(25 fps, 640 × 360; V1 0–26 s, `pattern.mp4` with sound) in the session's scratch folder, extended in the app (markers at
00:00:02:02 and 00:00:30:02 — past the end —, the sound of the first clip unlinked to A1). Not isolated: logs and the
recent-projects list under the real `%LOCALAPPDATA%\AiVideoEditor`; no recovery file left.

| # | Scenario | Expected | Automated coverage | Status |
|---|---|---|---|---|
| 1 | Start the exe from another current directory (a console in another folder) with `App:Language` = `en` next to the exe | The interface in English: the file next to the exe is read | 25.1 | local (Claude): PASS — `appsettings.json` next to the exe set to `en` (restored afterwards), the exe started with the scratch folder as its current directory: the whole window English («New», «INSPECTOR», «Opened project …») |
| 2 | The same with `Ffmpeg:FfmpegPath` / `FfprobePath` set next to the exe, ffmpeg not on PATH | Media analysed, export possible | 25.1 | local (Claude): PASS — the same start with `Ffmpeg:FfmpegPath` / `FfprobePath` set and the ffmpeg folder removed from the process's PATH (no `ffmpeg.exe` left on it): the four media analysed («0:12 · 640×360 · 25 FPS»), the Preview decoded |
| 3 | Previous / Next marker (FU-21.1) | A jump shows «Переход к маркеру 00:…»; with no marker «Перед указателем нет маркеров.» / «После указателя нет маркеров.»; a later jump replaces the warning | 25.2 | local (Claude): PASS — Next from 00:00:09:17: «Переход к маркеру 00:00:30:02»; Next again: «После указателя нет маркеров.»; Previous: «Переход к маркеру 00:00:02:02» (the refusal replaced); Previous again: «Перед указателем нет маркеров.» |
| 4 | Markers with the playhead past the sequence's end, on a marker, next to a marker | The right marker each time, never a false «нет маркера» | 25.2 | local (Claude): PASS — the first click on the ruler past the end (00:00:30:02, the sequence 26 s) kept the playhead there (before the fix it was pulled back to 00:00:26:00); Next onto the marker past the end stayed on it; on a marker each direction reported its refusal. Frame boundaries: auto only (`MarkerNavigationTests`) |
| 5 | FU-22.1: a click on the ruler at 00:00:10:00 with a video clip selected, then «Разметить зацепки» | The mode stays on; a seek by the user afterwards leaves it | 25.3 | local (Claude): PASS (not reproduced) — the ruler click on 00:00:10:00, `short.mp4` (9–11 s) selected, «Разметить зацепки»: the mode on (Лупа / Подтвердить / Готово) and the playhead on 00:00:10:00 for 12 s; a Left arrow then left the mode (00:00:09:24) |
| 6 | Fade In / Fade Out sliders, video clip and audio clip | The slider and the field agree; its maximum follows the clip's length; one drag = one Ctrl+Z; typing still works | 25.4 | local (Claude): PASS — video clip: a drag of «Появление» 0 → 59 frames (the field and «00:00:02:09» followed, the clip drawn faded), one Undo → 0, Redo → 59; «Исчезание» 0 → 30, one Undo → 0; the audio clip on A1: «Исчезание» 0 → 49, one Undo → 0; the maximum 100 on the 4 s clips and 50 on the 2 s `short.mp4`. Typing after a drag: auto only |
| 7 | Hold line width slider | As row 6 | 25.4 | local (Claude): PASS — «Толщина» 4 → 22 by a drag, one Undo → 4. Typing: auto only |
| 8 | Volume slider on a clip with sound (Phase 24 gap) | As row 6; the sound follows | 25.4 | local (Claude): PASS for the slider — `pattern.mp4` (with sound): 100 → 32 % by a drag, one Undo → 100 %. Whether the sound follows: the rendered sound PASS later (row F9: exports at 50 / 100 / 200 % measured ±6,02 dB); by ear NOT RUN |
| 9 | Timeline tool bar icons at 1024 and 1440 px | Each command an icon and its Russian label, tooltips with shortcuts, at most two rows, nothing cut | 25.5 | local (Claude): PASS — at 1024 × 640 and 1440 × 900 two rows, every command an icon and its Russian label, nothing cut (also in English at 1024); the tooltip of «Удалить со сдвигом» seen |
| 10 | Track and marker button tooltips (Phase 24 gap) | Russian tooltips | AXAML guard | local (Claude): PASS — hovered: M («Выключить / включить звук дорожки …»), the eye, the lock, ▲, ✕, the four marker buttons («Перейти к предыдущему маркеру», «Добавить маркер в позиции указателя», …) — all Russian |
| 11 | The Inspector's Звук section (Phase 24 gap) | Labels whole, Russian units | `RussianTextsTests` | local (Claude): PASS for the labels («ЗВУК», «Громкость», «Без звука», «%») — but see «Found» below: the number «100» is cut to «10(» in the field (also Масштаб, Непрозрачность) |
| 12 | A hold model error (Phase 24 gap) | A Russian status, the technical detail in parentheses | `HoldMarkupUiTests` | NOT RUN — an error of the hold model needs the installed model under `%LOCALAPPDATA%\AiVideoEditor\models` damaged or moved; not done on the product owner's machine. Auto only (`HoldMarkupUiTests`) |
| 13 | Open a damaged project (Phase 24 gap) | A Russian dialog, the reason in parentheses | workflow tests | local (Claude): PASS in part — a project folder with a truncated `project.json` given at start: the status «Не удалось открыть проект. Файл проекта повреждён и не может быть открыт.» Through «Открыть» and the folder dialog: NOT RUN (the system dialog could not be driven to the folder) |
| 14 | A failed export and the warnings dialog (Phase 24 gap) | Russian dialogs, the FFmpeg detail in parentheses | `ExportWorkflowTests` | local (Claude): PASS for a failed export — the target `ro.mp4` held open by another process, «Заменить файл?» → «Заменить»: «Ошибка экспорта» — «Не удалось записать выходной файл. Проверьте папку и убедитесь, что файл не открыт в другой программе. (The export could not be written to … Access to the path is denied.) Файл не записан; существующий файл не изменён.», the same in the status bar. The warnings dialog: NOT RUN (no warning in the fixture) |
| 15 | A Phase 24 project (`d3df0ba`) opened and saved with `ru` and with `en` | Byte for byte the base build's save (v6) | format tests | local (Claude): PASS — a fresh fixture saved by the base build (`git archive d3df0ba`, Debug): v6; three copies opened and saved without an edit — by the base build, by this build with `ru` and with `en`: 11 678 bytes each, byte-identical (SHA-256 `6DD5EF8B…`, `cmp`). (They differ from the source copy only in `relativePath`, as the copies point at the source folder's media by their absolute paths — the same in both builds.) |

Found in this run, outside the Phase 25 scope, not changed then (reported to the product owner):
- **The Inspector's numeric fields cut a three-digit value** — «100» showed as «10(» in Громкость, Масштаб and
  Непрозрачность. Fixed afterwards at the product owner's request (FU-25.1, below).

The product owner's «Insert» among the timeline's commands is the bar's «Вставить» (Paste): the timeline's tool bar has no
Insert (Insert / Overwrite from the Source viewer are in the Preview's bar).

## FU-25.1 — the Inspector's numeric fields (2026-10-19, after the Phase 25 acceptance with notes)

Fix: the Inspector's numeric-field style narrows Fluent's spinner buttons (≈ 35 → 22 px) and the text padding, so a 104 px
field shows ≈ 49 px of text instead of ≈ 19 px (UI Automation: the text box 35 → 59 px, the buttons 35 → 22 px, the height
the same). Run by Claude on the Debug build with the fix, a fresh Phase 10 fixture, UI Automation, system mouse input and
`SendKeys` (Tab / focus loss commits a value; Enter from `SendKeys` did not always reach the field).

| # | Scenario | Expected | Automated coverage | Status |
|---|---|---|---|---|
| F1 | Масштаб at 1440 × 900: type 0, 1, 10, 99, 100, 999,5, 1000 | Each value whole in the field, applied | `InspectorFieldStyleTests` (the style only) | local (Claude): PASS — every value whole (screenshots); 0 → 1 (the minimum); the picture follows |
| F2 | Непрозрачность: 0, 10, 99, 100 | Whole, applied | — | local (Claude): PASS — whole; the picture dims, the slider follows |
| F3 | Громкость (with sound): 0, 10, 99, 100, 200 | Whole, applied | — | local (Claude): PASS — whole; the slider follows |
| F4 | Out of range: Масштаб 1001 / −5, Непрозрачность 101 / −3, Громкость 201 | Not applied | — | local (Claude): PASS — the field returns to the previous value, nothing changes, no message (the same in the base build `d3df0ba`: Масштаб 1001 → previous) |
| F5 | The spinner buttons | One step per click; the maximum holds | — | local (Claude): PASS — Масштаб 100 → ▲ 101 → ▼▼ 99; Непрозрачность 100 → ▲ 100 → ▼ 99; Громкость 150 → ▲ 151 → ▼▼ 149 |
| F6 | Undo / Redo of these edits | Back and forth | slider tests (undo steps) | local (Claude): PASS — Undo / Redo on the tool bar return and reapply the values (consecutive changes of one field merge into one step, D017) |
| F7 | Save, close, reopen | The values kept and shown whole | format tests | local (Claude): PASS — Масштаб 1000, Непрозрачность 10, Громкость 200 saved (`project.json` v6: scale 10, opacity 0.1, volume 2), reopened as 1000 / 10 / 200, whole |
| F8 | 1024 × 640: every Inspector section | Nothing cut, the panel as compact as before | `InspectorFieldStyleTests` (one width 104 for all 21 fields) | local (Claude): PASS — Трансформация, Кадрирование, Зацепки (Толщина), Появление и исчезание, Скорость, Звук: values whole, labels as before, the panel width unchanged |
| F9 | Volume heard (row 8's open part) | The sound follows the volume | the parity suites | local (Claude): PASS for the rendered sound — three exports of the same clip (mono source, −21,08 dB RMS) at 50 / 100 / 200 %: −30,11 / −24,09 / −18,07 dB RMS per stereo channel, exactly −6,02 / 0 / +6,02 dB around 100 % (the mono → stereo split −3,01 dB). Listening by ear: NOT RUN — no capture device on this machine and no human listener; for the product owner |

Found in the FU-25.1 run, not changed (reported): **FU-25.2** — a field with a slider loses a typed fraction (Непрозрачность
50,5 → 50, Громкость 150,5 → 150, Толщина 4,5 → 4; fields without a slider keep fractions); the slider's whole-step
snapping writes the rounded value back. Present since Phase 24 (the base `d3df0ba` the same); recorded in ROADMAP.
