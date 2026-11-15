# Phase 40 — Export Performance: manual test plan

Decision: DECISIONS.md **D059** (2026-11-10). Environment: the target build of Phase 40 on the target machine (Ryzen 7
7800X3D, RTX 4070 SUPER, Windows 11) and, for section F, a computer (or a session) without NVENC; a **copy** of a real
project (the originals are never saved); the 12×10 videos and a vivo HLG clip. Legend: **PASS**, **FAIL**, **PARTIAL**,
**NOT RUN** (with a reason). «Automated» names the tests that cover the row. The measured numbers of the phase:
`docs/EXPORT_PERFORMANCE.md`.

## A. The export dialog (D059 §4)

| # | Check | Expected result | Automated | Evidence |
|---|---|---|---|---|
| A1 | «Экспорт» on an existing project (never exported with Phase 40) | «Профиль: Сбалансированный», «Кодировщик: libx264»; the line about the encoder; «Ожидаемый размер: примерно … – … (оценка …)» | `ExportEncodingEditorTests`, `ExportWorkflowTests` | |
| A2 | Open the «Кодировщик» list | «NVIDIA NVENC» and «libx264», each with its short description; «Оба кодировщика работают через FFmpeg…» | `ExportEncodingEditorTests` | |
| A3 | Choose «Быстрый» | the encoder switches to NVENC; «Параметры: NVENC p… CQ …»; the size estimate changes | `ExportEncodingEditorTests` | |
| A4 | Switch the encoder NVENC ↔ libx264 | the parameters line and the estimate change at once | `ExportEncodingEditorTests` | |
| A5 | «Пользовательский» | quality, speed, the rate control appear; «Средний битрейт» shows the bitrate list; the estimate becomes «около … (оценка, ±7 %)» | `ExportEncodingEditorTests` | |
| A6 | Change only the AAC bitrate | the profile stays | `ExportEncodingEditorTests` | |
| A7 | Export with a non-default profile, close, reopen the dialog; Undo | the dialog shows that profile (the project's); Undo restores the previous export settings | `ExportWorkflowTests`, `ExportEncodingPersistenceTests` | |
| A8 | «Параметры проекта» | the same editor (without the estimate); Apply changes it in one undo step | `ProjectSettingsUiTests`, `ProjectSettingsViewBindingTests` | |
| A9 | Compare the estimate with the real file (Balanced libx264, Balanced NVENC, Custom 25 Mbit/s) on a 1–2 min project | the real size inside the range (constant quality) / within ±7 % (bitrate); note the values | — | |

## B. Speed and the progress window (D059 §1–§3)

| # | Check | Expected result | Automated | Evidence |
|---|---|---|---|---|
| B1 | A 4K60 project (12×10), «Сбалансированный» libx264 | the progress window: «… кадр/с · прошло … · осталось ≈ …», «Кодировщик: libx264 medium CRF 18»; markedly faster than before (≈ 12 fps before) | benchmark `4k` | |
| B2 | The same with «Быстрый» NVENC | «Кодировщик: NVENC p… CQ …»; faster than B1 | benchmark `4k` NVENC | |
| B3 | A vertical project with the blurred background; a 4K → 1080p project | faster than before (≈ 13 fps before) | benchmark `vert-blur`, `4k-1080` | |
| B4 | A 4K120 source in a 60 fps project | faster than before (≈ 6 fps before) | benchmark `4k120` | |
| B5 | Task Manager during B1 / B2 | the CPU busy (B1 near all cores), the GPU's «Video Encode» busy in B2; the app's memory bounded (no steady growth) | benchmark (peak WS, VRAM) | |

## C. The picture and the sound (D059 §2, §5)

| # | Check | Expected result | Automated | Evidence |
|---|---|---|---|---|
| C1 | A real project with text (outline, background), «Боулдер», «Хайлайт», the heart rate, animated hold outlines, VHS, a Dissolve and a Push, a picture in picture — export Balanced libx264 | the file looks like the Preview at the same frames (outlines, texts, templates, transitions in place; nothing flickers, no frame out of order) | `ExportPipelineParityEndToEndTests`, the parity suite | |
| C2 | The same with «Быстрый» NVENC and «Высокое качество» NVENC; play side by side with C1 | the same picture; look at the wall texture, the chalk, fine detail, fast motion and dark parts — no visible blocks or smearing beyond libx264's | calibration (VMAF) | |
| C3 | Sound in C1 / C2 | in sync with the picture over the whole length (claps / hold contact); the length of the file = the project's | `ExportAudioSync…`, `NvencExportEncoderTests` | |
| C4 | A long export (≥ 10 min of 4K60) | finishes; the length, the sound to the end; the memory flat | benchmark `long` | |

## D. Cancel and failures (D059 §2, §3)

| # | Check | Expected result | Automated | Evidence |
|---|---|---|---|---|
| D1 | Cancel during the video stage (libx264, then NVENC) | the window closes within about a second; no `ffmpeg.exe` left (Task Manager); no `.…partial.mp4` / `.…audio.m4a` left in the folder; an existing file of the same name untouched | `ExportServiceTests`, `FramePipeTransportTests` | |
| D2 | Start an export with NVENC while another program uses many NVENC sessions (or, on a test machine, a driver issue) | the export completes with libx264; the message says NVENC failed and libx264 encoded | `ExportServiceTests` (fallback) | |
| D3 | A source removed during the export | the export stops with the decoding error, nothing written | the strict-end tests | |

## E. Compatibility (D059 §4)

| # | Check | Expected result | Automated | Evidence |
|---|---|---|---|---|
| E1 | Open a project saved before Phase 40 with non-default export settings (e.g. Compact / Slow / 320) | «Пользовательский» with exactly those values; the export as before | `ExportEncodingPersistenceTests` | |
| E2 | Save it without a change | `project.json` unchanged (compare) | `ExportEncodingPersistenceTests` | |
| E3 | The Preview of a 4K project (play, scrub) | as before (the Preview keeps its transport) | `FramePipeTransportTests` | |

## F. Without NVENC

| # | Check | Expected result | Automated | Evidence |
|---|---|---|---|---|
| F1 | The export dialog | only «libx264»; «Быстрый» = libx264 veryfast | `ExportEncodingEditorTests` | |
| F2 | A project saved with NVENC on the target machine | the dialog says NVENC can't be used and libx264 will encode; the export succeeds with libx264; the project's choice kept | `ExportEncodingEditorTests`, `NvencExportEncoderTests` | |
