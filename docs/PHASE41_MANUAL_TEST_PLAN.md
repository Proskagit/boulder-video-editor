# Phase 41 — Export UX, Resolution Presets & Project Snapshot Isolation: manual test plan

Decision: DECISIONS.md **D060** (2026-11-16). Environment: the target build of Phase 41 on the target machine (Ryzen 7
7800X3D, RTX 4070 SUPER, Windows 11); a **copy** of a real project (the originals are never saved); a 16:9 4K project, a
vertical 1080 × 1920 project and, if possible, one with a canvas of no preset's size (e.g. 2560 × 1440). Legend: **PASS**,
**FAIL**, **PARTIAL**, **NOT RUN** (with a reason). «Automated» names the tests that cover the row. Phase 40's plan
(`docs/PHASE40_MANUAL_TEST_PLAN.md`) still applies to the speed, NVENC and the profiles; section F below repeats its key rows.

Build and start: `pwsh -File tools\manual\Start-Phase41Check.ps1 [-Tests] [-Project <project folder>]` — publishes the portable
build `artifacts\portable\AiVideoEditor-0.41.0-win-x64` (FFmpeg from PATH), copies the project (without its media, which stay
where they are) to `%TEMP%\aive-phase41-check`, opens this plan and the app; open the copy with «Открыть».

## A. Unsaved changes and the exported version (D060 §2)

| # | Check | Expected result | Automated | Evidence |
|---|---|---|---|---|
| A1 | Edit something (the title «*» shows), press «Экспорт» | «Несохранённые изменения»: «…будет сначала сохранён», buttons «Сохранить и экспортировать» / «Отмена» | `ExportWorkflowTests` (Unsaved_*) | |
| A2 | «Отмена» | nothing exported, nothing saved, the «*» stays | `ExportWorkflowTests` | |
| A3 | «Сохранить и экспортировать» | the project is saved (no «*»), the export dialog opens; after the start the progress window says «Экспортируется «…» в состоянии на ЧЧ:ММ:СС (сохранён) · W × H» | `ExportWorkflowTests` | |
| A4 | A project never saved (new) with changes, «Сохранить и экспортировать» | Save As asks for a folder; cancelling it exports nothing | `ExportWorkflowTests` (A_save_that_fails…) | |
| A5 | A saved project, «Экспорт» | no question; straight to the dialog | `ExportWorkflowTests` | |
| A6 | Change the profile in the export dialog and export | the window says «сохранён; изменены параметры экспорта»; the project shows «*» afterwards (the export settings are the project's, one Undo step) | — | |

## B. Working during the export (D060 §2, §5)

| # | Check | Expected result | Automated | Evidence |
|---|---|---|---|---|
| B1 | Start a 1–2 min export | the progress window is not modal: the main window can be clicked, scrolled, played | `ExportWorkflowTests` (Editing_stays_available…) | |
| B2 | During the export: move and trim clips, change a text, opacity, Scale / Position, a template's fields, add a track, delete a clip | all edits work; Undo / Redo work | `ExportWorkflowTests` | |
| B3 | During the export: change the canvas (project settings), the frame rate | allowed; the running export is not affected | `ExportSnapshotIsolationTests` | |
| B4 | During the export: «Сохранить» | saves the edited project | — | |
| B5 | Play the exported file | it shows the project **as it was at the start** (A3's time) — none of B2–B4's changes | `ExportSnapshotIsolationTests`, end to end | |
| B6 | Import a **copy** of a video (never an original) into the test project and export; during the export try to delete or rename that copy in Explorer | Windows refuses («файл открыт в другой программе»); after the export the file can be deleted / renamed again | `ExportMediaLeaseTests`, `ExportResolutionEndToEndTests` | |
| B7 | During the export: relink a media file to another file, or remove a media from the project | allowed; the exported file still uses the original files | `ExportSnapshotIsolationTests` | |
| B9 | A project with outlined text: during its export run «Сохранить как…» and leave the folder picker open ~1 min | the export goes on (it may pause on an outlined text while the picker is open) and finishes; no «Не удалось нарисовать кадр» | — | |
| B8 | During the export: open another project (or New) | allowed (the usual unsaved question); the export finishes with the first project | `ExportSnapshotIsolationTests` | |

## C. The detailed window and the compact indicator (D060 §5)

| # | Check | Expected result | Automated | Evidence |
|---|---|---|---|---|
| C1 | The progress window | the version line, the output path, the stage, one bar for the whole export (no restart at the video stage), frames, speed / time left, the encoder, the note «Можно продолжать работу…», «Свернуть», «Отменить экспорт» | `ExportResolutionDialogTests` (overall %) | |
| C2 | «Свернуть» | the window hides; the status bar shows a small bar, «Экспорт NN %» and ✕; the percentage grows | `ExportBackgroundCloseTests` | |
| C3 | Click the indicator | the window comes back with the current state | `ExportBackgroundCloseTests` | |
| C4 | The window's ✕ (title bar) and Alt+F4 | the window hides, the export goes on (not cancelled) | — | |
| C5 | «Экспорт» / Ctrl+E during the export | the running export's window opens — no second export, no second window or indicator | `ExportWorkflowTests` | |
| C6 | The indicator's ✕ | «Отменить экспорт?» with the percentage; «Продолжить экспорт» changes nothing; «Отменить экспорт» cancels within ~1 s, the indicator disappears, no partial file, an existing file unchanged | `ExportBackgroundCloseTests` | |
| C7 | «Отменить экспорт» in the window | cancels at once (as in Phase 40) | `ExportWorkflowTests` | |
| C8 | Let an export finish while working | «Экспорт завершён» appears; the indicator disappears | `ExportWorkflowTests` | |

## D. Closing the app during an export (D060 §5)

| # | Check | Expected result | Automated | Evidence |
|---|---|---|---|---|
| D1 | Close the main window during an export | «Идёт экспорт … (NN %)» — «Продолжить экспорт» / «Отменить экспорт и закрыть» | `ExportBackgroundCloseTests` | |
| D2 | «Продолжить экспорт» | the app stays open, the export goes on | `ExportBackgroundCloseTests` | |
| D3 | «Отменить экспорт и закрыть» on a saved project | the status says «Отмена экспорта перед закрытием…»; the app closes after the export stopped; Task Manager: no `ffmpeg.exe`; the output folder: no partial file, no temporary file; an existing file of that name unchanged | `ExportBackgroundCloseTests` | |
| D4 | «Отменить экспорт и закрыть» with unsaved edits, then «Отмена» at the unsaved question | the app stays open **and the export goes on** | `ExportBackgroundCloseTests` | |
| D5 | During an export end the app in Task Manager («Снять задачу») | every `ffmpeg.exe` of the app ends with it within a second or two | `ChildProcessJobTests` | |

## E. Resolution (D060 §3)

| # | Check | Expected result | Automated | Evidence |
|---|---|---|---|---|
| E1 | 16:9 project 3840 × 2160, open the export dialog | «Разрешение: 4K UHD 3840 × 2160 · как в проекте» selected; the list: 4K UHD 3840 × 2160, Full HD 1920 × 1080, HD 1280 × 720, SD 854 × 480 | `ExportResolutionDialogTests` | |
| E2 | Choose Full HD | the line below says «1920 × 1080, …»; the expected size drops | `ExportResolutionDialogTests` | |
| E3 | Export Full HD (libx264 and NVENC «Быстрый») | the file is 1920 × 1080 (file properties / MediaInfo), the same length, sound in sync; texts, «Боулдер», outlines, VHS, transitions where they are in the Preview, not stretched; faster than the 4K export | `ExportResolutionEndToEndTests`, `ExportResolutionRenderingTests` | |
| E4 | Export HD and SD | 1280 × 720 / 854 × 480, nothing cropped, no black border | `ExportResolutionRenderingTests` | |
| E5 | A 1920 × 1080 project, export 4K UHD | the file is 3840 × 2160; texts and outlines sharp | `ExportResolutionEndToEndTests` (upscale HD) | |
| E6 | A vertical 1080 × 1920 project | Full HD 1080 × 1920 selected; HD 720 × 1280, SD 480 × 854; exported file vertical, not distorted | `ExportResolutionDialogTests` | |
| E7 | A 2560 × 1440 project | «Как в проекте 2560 × 1440» first and selected; Full HD gives 1920 × 1080 | `ExportResolutionDialogTests` | |
| E8 | After exporting at Full HD from a 4K project: the canvas in «Параметры проекта» | still 3840 × 2160; the composition unchanged; reopening the export dialog shows Full HD (the project's choice) | `ExportWorkflowTests`, `ExportEncodingPersistenceTests` | |
| E9 | Save, close, reopen; open an **old** project (before Phase 41) | the choice is kept; the old project opens and exports at its canvas size, as before | `ExportEncodingPersistenceTests` | |

## F. Phase 40 still holds (D060 §6)

| # | Check | Expected result | Automated | Evidence |
|---|---|---|---|---|
| F1 | 4K60 export at the canvas size, «Сбалансированный» libx264 / «Быстрый» NVENC | ≈ 30 / ≈ 45 fps as in Phase 40 (± a few) | benchmark (`tools/export-bench`) | |
| F2 | NVENC listed and «Быстрый» switches to it; NVENC failure / absence → libx264 | as in Phase 40 | `ExportEncodingEditorTests`, `NvencExportEncoderTests` | |
| F3 | A real project at the canvas size | the same picture as before Phase 41 (Preview parity), sound in sync | `ExportPipelineParityEndToEndTests`, `ExportResolutionEndToEndTests` | |
