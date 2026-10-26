# Phase 30 — manual test plan

Manual checks of Phase 30 (DECISIONS.md D046): the portable Windows build S1, its check in a clean environment, the main
path of D042 §1 (without step 4) in that build, the performance of a typical project and the inventory of the open manual
checks of earlier phases. Run in the **portable build** (not the development build) on Windows with its default
`appsettings.json` (`App:Language` = `ru`) unless a row says otherwise, at **1024 × 640** (the window's minimum) and
**1440 × 900** (the default), in Avalonia's logical pixels. Media: the product owner's real bouldering gym clips (session of
2026-09-27: 4K HEVC ≈ 60 fps, horizontal and one vertical) and a music file of the product owner's choice.

Status column — kinds of evidence, never mixed:
- **auto** — covered by automated tests only, not checked in the real app;
- **local (Claude)** / **PO** — the run of the portable build on Windows / the product owner's check;
- **sandbox** / **VM** — the run in Windows Sandbox / a clean VM (D046 §4); **partial** — a check outside a clean
  environment, its limits named in the row;
- **NOT RUN** — not checked (the reason given).

Skeleton written at Step 30.0 (2026-10-22). The rows are completed at the steps named in each part; the build's version
(commit, date) is written in every result.

## A. The portable build in a clean environment (Steps 30.1–30.2)

Environment: Windows Sandbox or a clean VM if available (no .NET, no FFmpeg on `PATH`, no repository); otherwise
**partial**, with what the check cannot prove (D046 §4).

**The environment used (30.2, 2026-10-24): the development machine, Windows 11 x64 — a partial check.** The product
owner's decision of 2026-10-24: a full clean environment is not required now; the available scenarios are checked on
this machine, and the absence of a VM / Windows Sandbox is recorded as a limit of the check (it does not block the
development).

| Limit | What it means for this check |
|---|---|
| No Windows Sandbox and no VM | `vmcompute` is absent, the state of the Sandbox feature is not readable without elevation → **no scenario ran on a machine without the development tools**; the .NET SDK/runtime, the Visual C++ runtime, the AI model and FFmpeg were all present on this machine |
| The .NET runtime is installed | The portable build carries its own runtime (self-contained), so its presence proves nothing either way — not checked |
| The Visual C++ runtime is installed | A9 cannot be tested (the runtime cannot be removed here) → **BLOCKED** |
| The AI model is installed | The app's data root (`%LOCALAPPDATA%\AiVideoEditor`) cannot be redirected — `Environment.GetFolderPath(LocalApplicationData)` ignores the process's `LOCALAPPDATA` / `USERPROFILE` overrides (checked) — and the user's model files are not touched → A7 cannot be tested → **BLOCKED** |
| FFmpeg is installed | A6 was tested with a **separate copy** of the portable folder without the `ffmpeg\` folder and with `PATH` without ffmpeg; the original folder was not changed |
| The application's own data | The run used the real `%LOCALAPPDATA%\AiVideoEditor` (logs, config, the model) — the same place a user would have them; the project files were written into the test folders |

| # | Scenario | Expected | Automated coverage | Status |
|---|---|---|---|---|
| A1 | The portable folder built by the documented command from a clean checkout | The folder holds the runtime, the application, `appsettings.json`, the notices, the user's instruction (and FFmpeg once D046 §2 is decided); its size recorded | — | local (Claude), 2026-10-22: `pwsh -File tools/portable/Publish-Portable.ps1 -Zip` in a fresh clone of `e28a9d3` with the step's uncommitted changes copied in (so the build is marked `.dirty`): 349 files, 124 818 036 bytes (119.0 MiB), zip 51.1 MiB; .NET 8.0.30, SDK 8.0.424; **without FFmpeg** (D046 §2). To repeat from a commit once the step is committed |
| A2 | The notices: every shipped component named with its licence; the model and `DirectML.dll` stated as not shipped | Complete and matching the restored packages (D046 §3) | — | generated (30.1): 57 components from the deps file with their package metadata, licence files of 39 copied into `licenses\`, 18 without one; **not reviewed** — e.g. Avalonia.Fonts.Inter's metadata says MIT while the embedded Inter 3.019 fonts are under the SIL OFL 1.1 (their name table). Inventory 2026-10-22: the authoritative copies of the 18 missing texts located (D046 §13). Review pending |
| A3 | The start by a double click in the clean environment | The main window, the Russian interface, no error dialog; logs in `%LOCALAPPDATA%\AiVideoEditor\logs` | — | **PASS (partial — 30.2, local, Claude, 2026-10-24)** — the portable build `0.30.0+cf718bc7dd.dirty` started with the working directory = the application folder (as a double click does): the main window «Новый проект — AI Video Editor» (class `MainWindow`), **one** top-level window of the process (no error dialog), the Russian interface (Создать / Открыть / Импорт медиа / Экспорт / Параметры… / МЕДИА / ТАЙМЛАЙН / СВОЙСТВА), the frame rate «30 к/с (предварительно)» from `appsettings.json`, and the log written to `%LOCALAPPDATA%\AiVideoEditor\logs\app-20261024.log` («Application shell initialized», «Autosave started … recovery folder C:\Users\Vlad\AppData\Local\AiVideoEditor\recovery»). Limit: not a clean machine (the limits above) |
| A4 | The start from another current directory (a shortcut, a console in another folder) | As A3; `appsettings.json` and the tools' relative paths read from the application folder | the locator's relative-path tests (30.1) | **PASS (partial — 30.2, local, Claude, 2026-10-24)** — the same build started with the working directory `C:\Windows`: the main window and the Russian interface (without `App:Language` the neutral English resources would be used — `UiLanguage`), the frame rate from `appsettings.json`, no error dialog; the import worked and the log shows the **relative** configured paths resolved against the *application* folder: «Using configured ffprobe path: C:\AiVideoEditor\artifacts\portable\AiVideoEditor-0.30.0-win-x64\ffmpeg\ffprobe.exe» and the same for `ffmpeg.exe` — the current directory never mattered |
| A5 | The folder in a path with spaces and Cyrillic (e.g. `C:\Мои программы\Boulder Editor`) | As A3; import, save and export work | — | **PASS (partial — 30.2, local, Claude, 2026-10-24)** — the folder copied to `…\Мои программы\Boulder Editor\AiVideoEditor-0.30.0-win-x64` (353 files, 315.1 MiB): the start OK (the window, the Russian interface, no error dialog); the import OK («Media import batch: 1 imported»), the tools called with the quoted path with spaces; the project saved — «Saved project 'Проект S1' to …\Мои программы\Boulder Editor\Проект S1» (`project.json` + the thumbnail cache); the export OK — «Export finished in 00:00:05.44», the file `…\Проект S1\экспорт S1.mp4` (9.97 MB) probed: H.264 1920 × 1080 60 fps + AAC 48 kHz stereo, 9.000 s |
| A6 | No FFmpeg available (not bundled / removed, not on `PATH`) | The existing messages at import and export; no crash; the rest usable | the locator's tests | **PASS (partial — 30.2, local, Claude, 2026-10-24)** — a separate copy of the portable folder **without** the `ffmpeg\` folder, started with `PATH` without ffmpeg (the directory holding `ffmpeg.exe` removed from the child's `PATH`): the app starts (the window, the Russian interface, no error dialog, no crash); the log — «Configured ffprobe path … does not exist; falling back to PATH lookup» and «ffprobe could not be located (not configured, not found on PATH)»; the import shows the **existing** message «Не найден FFprobe. Установите FFmpeg (ffprobe должен быть в PATH) или укажите путь к нему в настройках.» and the file is imported with the basic analysis only («Metadata analysis failed … ProbeToolUnavailable»), and it cannot be placed on the timeline («Файл 1_rec_h.mp4 нельзя добавить: длительность неизвестна (…)»); the export is refused with the **existing** message «Проект нельзя экспортировать: Ошибки: • Таймлайн пуст. • Не найден ffmpeg, поэтому видео нельзя закодировать.» — the editor stays usable |
| A7 | No model installed; then «Разметить зацепки» | The existing "not installed" message naming the folder and `docs/AI_MODEL.md`; the editor usable | Vision.Tests | **BLOCKED (30.2, the environment)** — the model **is** installed here (`%LOCALAPPDATA%\AiVideoEditor\models\sam2.1-large`, both files present) and the state cannot be produced without changing the machine: the app's data root is `%LOCALAPPDATA%\AiVideoEditor` and `Environment.GetFolderPath(LocalApplicationData)` ignores the process's `LOCALAPPDATA` / `USERPROFILE` overrides (checked), and the user's model files are not touched. The message itself is covered by Vision.Tests (the plan's column) |
| A8 | The model installed as `docs/AI_MODEL.md` describes; «Разметить зацепки» | The check and loading progress; the markup works (DirectML, or the CPU fallback with its message) | — | **PASS (partial — 30.2, local, Claude, 2026-10-24)** — the model as `docs/AI_MODEL.md` describes it; the status bar showed the **check and loading progress** («Проверка модели зацепок…» → «Загрузка модели зацепок…») and **no** CPU-fallback message (DirectML used); the markup mode opened («Разметка зацепок — щёлкните зацепку, Enter подтверждает», the Inspector's ЗАЦЕПКИ section) and the model returned a candidate on the real frame («Контур 1 из 2»); Enter confirmed it («Отмечено: 1») |
| A9 | The Visual C++ runtime absent (clean environment) | ONNX Runtime and the other native libraries load, or the missing dependency named for D046's risk list | — | **BLOCKED (30.2, the environment)** — the Visual C++ runtime cannot be removed on this machine, so the scenario was not run. What the shipped binaries need was read from their PE import tables: `onnxruntime.dll` imports **MSVCP140.dll, MSVCP140_1.dll, VCRUNTIME140.dll, VCRUNTIME140_1.dll** and `onnxruntime_providers_shared.dll` imports VCRUNTIME140.dll → the runtime **is** required for the hold markup on a machine without it (D046 §12's risk); the other shipped native libraries (`libSkiaSharp.dll`, `libHarfBuzzSharp.dll`, `av_libglesv2.dll`) import only the Windows system DLLs |
| A10 | The templates' fonts (Bahnschrift, Consolas, Segoe UI) in the clean environment | REC, VHS, «Боулдер», «Хайлайт» drawn as in the development build | Rendering.Tests | **PASS (partial — 30.2, local, Claude, 2026-10-24)** — all three fonts are installed on this machine (the registry: Bahnschrift, Consolas, Segoe UI); the templates were drawn in the portable build on this machine at Step 30.3 (B6: «Боулдер» (route 3, 6B+, TOP) and REC in the Preview and in the export); the log of that run has **no** warning and no error at all, so the export preflight named no missing font. Limit: a machine **without** those fonts was not available (the font's absence is covered by Rendering.Tests) |

**Step 30.7 re-run (2026-10-24) on the rebuilt portable folder.** The available rows of part A were re-run after the
portable folder was rebuilt: **A3, A4, A5, A6 and A10 PASS**; **A7 and A9 remain BLOCKED** by the environment; **A8** relies
on the still-installed model and the previous PASS of 2026-10-24. A6's 30.7 re-run confirmed only the start-up path; the
`ProbeToolUnavailable` message itself was observed in the Step 30.2 run and the same code path is unchanged.

## B. The main path in the portable build (Step 30.3)

D042 §1 without step 4, run end to end on one project, at 1024 × 640 and at 1440 × 900. Each defect gets an id (§E) and a
severity (D046 §5).

| # | Scenario | Expected | Automated coverage | Status |
|---|---|---|---|---|
| B1 | Import the gym clips (horizontal and vertical) and the music | Analysed, thumbnails and waveforms; the canvas offered by the first video (D041) | Timeline / UI tests | PASS (both sizes) — 3 / 4 files imported and analysed, thumbnails, waveforms; 16 : 9 first → no offer (correct, D041 §1); vertical first → «Сменить холст проекта на 1080 × 1920?» → 1080 × 1920 |
| B2 | Cut away the excess, split and arrange the clips (trim, split, move, ripple delete, snapping), Undo / Redo | One step per action; the Preview follows | Timeline.Tests | PASS (both sizes) — split at the playhead, ripple delete, trim by an edge drag, move with snapping (snapped exactly to the neighbour), trim to the playhead (Q), close gap; each one Undo / Redo step; the Preview followed |
| B3 | Recognise holds, choose the wanted ones, correct a contour, outline them with their animation | D034–D036 as specified; the outlines in the Preview | Vision / Rendering tests | PASS (both sizes) — markup mode, 3 / 2 holds confirmed (a donut hole needed Tab; a large red hold refused as "wall / too large" — D034 limits), a vertex dragged and undone / redone, animation bottom to top: appear, stay, disappear checked frame by frame |
| B4 | Add text with an animated appearance and disappearance, an outline or background | D043 as specified | Rendering / UI tests | PASS (both sizes) — text on a new V2 (see U1), appear «Выезд» / disappear «Проявление», background on (outline on at 1440): mid-appear, shown, mid-disappear and gone checked |
| B5 | Add transitions between clips (Наплыв, Затемнение, Вспышка, Наезд, Сдвиг) | D044 as specified | Rendering / Timeline tests | PASS (both sizes) — all five types on a cut: dissolve blended, dip black and flash white at the cut frame, zoom magnified at its peak, push displaced; Dissolve / Push refused without handles (correct message) |
| B6 | Add music, set its volume and fades; apply a template (REC / VHS / «Боулдер» / «Хайлайт») | D045 as specified; the mix by ear | Export / Rendering tests | **PASS (PO, 2026-10-24)** — the mix by ear: both exports listened to; the music and the gym's own sound are correct, the fade-out is normal; **no critical clicks, level jumps or desynchronisation noticed** (the owner's own observation, by ear). The run of 2026-10-23 (local) had it BLOCKED for «the mix by ear» (a listener was needed); done then: volume 50 % / 70 %, fades 1 / 2 s and 0.5 / 1 s — one undo step each; «Боулдер» (route 3, 6B+, TOP) and REC templates drawn in the Preview and the export; the export's level per second follows the fades (music end −18.7 dBFS → silence) |
| B7 | Set the framing (Fill / Fit, Move / Scale / Crop), check the Preview | D038, D041 as specified | UI / Rendering tests | PASS (both sizes) — Fill 316.05 % / Fit 100 %, Move by a drag in the Preview, scale by the field, crop by a handle drag (12 % left / 11.6 % right, the picture kept in place), Undo / Redo |
| B8 | Save, close, reopen the project | Everything as before; nothing offline | Project.Tests | PASS (both sizes) — saved, the app closed and restarted, opened: 0 missing, everything as before (holds and animation, text, template, transitions zoom / push, music 50 %); a Save As right after the reopen wrote the same `project.json` except its name |
| B9 | Export the video; watch it in an external player | The file plays; the picture, the text, the outlines, the transitions, the template and the sound as in the Preview | ExportEndToEnd.Tests | **PASS (PO, 2026-10-24)** — both exports watched in an external player: the captions, the hold outlines, the transitions, the templates and the framing look correct; **no critical playback problems noticed** (the owner's own observation, by eye). The run of 2026-10-23 (local) had it BLOCKED for «watch it in an external player» (a person was needed); done then: 4:31 at 1920 × 1080 60 fps (21 min) and 0:25 at 1080 × 1920 60 fps (1:31) exported, decoded in full without an error; frames at the checkpoints (holds, text, template, transitions, framing) equal to the Preview |

Run of part B: local (Claude), 2026-10-23, portable build `0.30.0+cf718bc7dd.dirty` with gyan.dev FFmpeg 9.0.1 from its `ffmpeg\` (not on `PATH`), the product owner's clips of 2026-09-27 and the MP3 of the same folder, at 1440 × 900 and 1024 × 640 (two projects). Driven through UI Automation and messages posted to the window (no real cursor or keyboard), checked on window captures and on the exported files. The window was on a 100 % monitor. The driver (`pwsh.exe`) was not per-monitor DPI aware (system aware at DPI 120), so the window sizes, the input coordinates and the captures it saw were scaled by Windows; its input offset of 1.25 compensated for that. The app itself laid out and drew correctly; see the withdrawn observation under §E. The sizes above are the app's layout sizes; the captures' drawn widths confirm them (≈ 1456 and ≈ 1042 physical pixels at 100 %).

**Manual checks of the owner (PO, 2026-10-24).** The rows of part B that needed a person were run by the product owner on the artefacts of the run of 2026-10-23 — the same portable build and the same two exported files (4:31 at 1920 × 1080 60 fps and 0:25 at 1080 × 1920 60 fps), no new export:

| Check | Result |
|---|---|
| B6 — the mix by ear (both exports listened to) | **PASS** — the music and the gym's own sound are correct, the fade-out is normal; no critical clicks, level jumps or desynchronisation noticed |
| B9 — both exports in an external player | **PASS** — the captions, the hold outlines, the transitions, the templates and the framing look correct; no critical playback problems noticed |
| The window dragged between the monitors with the real mouse at 125 % ↔ 100 %, the interface's clicks and the resize (§E's NOT RUN check) | **PASS** — no problems found, the interface's elements work correctly |

The observations are the owner's, by ear and by eye; no new measurements were made and no new file was produced. The run of 2026-10-23 above stays as it was written.

**Open record (not a defect):** B1's row records "3 / 4 files imported and analysed". The final project holds all four
assets (confirmed by the owner and by `project.json`), but which file was not imported at that step is not established by
the existing evidence. This ambiguity is recorded, not a defect, and does not block the main path.

## C. Performance of a typical project (Step 30.5)

The project: several minutes, a few tracks, holds, text, transitions, a template, music (D046 §6). Measured first; the
targets agreed with the product owner afterwards.

**The environment (2026-10-24, local — Claude):** the development machine — Windows 11 IoT Enterprise LTSC 26100, AMD
Ryzen 7 7800X3D (8 cores / 16 threads), 31.2 GiB RAM, GeForce RTX 4070 SUPER; the load during the measurements 0.7 % of
the cores (the machine was otherwise idle). The portable build `0.30.0+cf718bc7dd.dirty`
(`artifacts/portable/AiVideoEditor-0.30.0-win-x64`, FFmpeg 9.0.1 gyan.dev in `ffmpeg\`), the window 1440 × 900 logical on
the 100 % monitor (DPI 96); the driver thread per-monitor aware v2.

**The profile:** `perf-1440` — the project of Step 30.3 (`project-1440`): 4:31 (271.47 s), 1920 × 1080 @ 60 fps, 4 media
(three 4K HEVC clips of the session of 2026-09-27 and the MP3), 6 clips on 3 tracks — V1: three video clips with the hold
markup, V2: a text clip with an appearance and a template clip, A1: the music at 50 % — plus the transitions of D044.

**The method:** the app is driven by UI Automation and posted mouse / key messages (no real cursor or keyboard); a
background sampler hashes the picture of a region of the window (PrintWindow, PW_RENDERFULLCONTENT) about every **13 ms**,
so the moments of the redraws are known; the app's own log gives the times of the operations it records. The resolution of
the latency measurements is therefore ≈ 13 ms (one sampling) — a value below it is reported as «already drawn».
C2 was measured on the same profile **after the test edits themselves** (the clips moved, trimmed and split), so the
timeline was in the same busy state; the app's own undo restored it.

| # | Measure | Definition | Result | Target (agreed later) |
|---|---|---|---|---|
| C1 | Opening the project | From Open to the timeline usable | **with the project's cache** (3 runs): the app's log «Opened project» 165–168 ms, the window and the status bar show the project after 207–211 ms, the picture settles after 523–534 ms. **First open without the cache** (the thumbnails and the waveforms are regenerated in the background): the log 227 ms, the interface 292 ms, the picture settles after 1437 ms, the cache files are written ~0.6 s after the open | **≤ 1 s** for the project to be usable; **≤ 2 s** for the first open without the cache (agreed by the product owner, 2026-10-24) |
| C2 | The timeline's response | Move / trim / split on a busy timeline | **move** (a 700 ms drag of 20 steps): 11–14 redraws (14–18 /s) — the clip follows the cursor live, the final position is drawn within ≤ 13 ms of the mouse-up. **trim** (a 545 ms drag): 4–7 redraws (5.5–11 /s), the response 14–22 ms, the finalization ≤ 22 ms. **split** (the key S at 1:00): the redraw 288–295 ms after the key-up; the undo (Ctrl+Z) 245–257 ms | **≤ 50 ms** for the move and the trim; **≤ 400 ms** for the split to be drawn (agreed by the product owner, 2026-10-24) |
| C3 | The Preview's playback | Dropped frames over the project at 1440 × 900 | the whole project (271.47 s) played in 275.2 s of wall clock = **98.6 % of real time**; the Preview drew **58.7 redraws /s** (the project is 60 fps → ~98 % of the frames), the first frame 1.7 s after the start; the app's log has no warning and no error. The application has **no dropped-frame counter** (the known issue: «late frames are flagged but not counted») — the effective playback rate is measured instead | **≥ 95 %** of the real time and the Preview **≥ 55 redraws /s** (agreed by the product owner, 2026-10-24) |
| C4 | Scrubbing | A jump by the ruler; a continuous scrub by dragging | **a jump by clicking the ruler**: the new frame appears **109–121 ms** after the click, the last change ~1.19 s. **A continuous scrub by dragging**: the new frame appears **309–499 ms** after the mouse-up, the last change ~1.19 s | **≤ 200 ms** for the jump; **≤ 600 ms** for the new frame after the scrub's release (agreed by the product owner, 2026-10-24) |
| C5 | The export | Its time against the project's length | **21:21.52** for the 4:31 project (271.47 s) = **4.72 × the real time** (the app's own log: «Export finished in "00:21:21.5184949"»); the settings: quality High (CRF 18), preset medium, AAC 192 kbps; the file 232 929 229 bytes — **byte for byte the same size** as the export of Step 30.3 (the same project in the same build, 21:00.76 then) | **≤ 5 ×** the length of the project (agreed by the product owner, 2026-10-24) |
| C6 | Memory | The process's working set during playback and export | **during the playback:** the app's working set 282–466.5 MiB (mean 299.9), the private bytes up to 406.2 MiB; the ffmpeg processes it starts: up to 5, their working set up to 1313.5 MiB, the private bytes up to 2278.9 MiB. **during the export:** the app's working set 315.4–1077.1 MiB (mean 842.7), the private bytes up to 1024.4 MiB; up to 6 ffmpeg processes, their working set up to 2705 MiB, the private bytes up to 3406.2 MiB — together with the app **≈ 3.7 GiB** at the peak | **≤ 1.2 GiB** for the app's working set during the export; **≤ 4.5 GiB** for the app together with the ffmpeg processes (agreed by the product owner, 2026-10-24) |

**The targets were agreed by the product owner on 2026-10-24** (D046 §6). The values below are the agreed targets; the
measured values are shown for reference:

| # | Agreed target | Measured |
|---|---|---|
| C1 | the project is open and usable in ≤ 1 s (the picture settled); the first open without the cache ≤ 2 s | 0.52 s / 1.44 s |
| C2 | the move and the trim answer within ≤ 50 ms; the split is drawn within ≤ 400 ms | ≤ 13–22 ms / 288–295 ms |
| C3 | the playback keeps ≥ 95 % of the real time and the Preview draws ≥ 55 frames /s | 98.6 % and 58.7 /s |
| C4 | a jump by the ruler shows the new frame within ≤ 200 ms; a scrub shows it within ≤ 600 ms after the release | 109–121 ms / 309–499 ms |
| C5 | the export takes ≤ 5 × the length of the project | 4.72 × |
| C6 | the app's working set ≤ 1.2 GiB during the export; with the ffmpeg processes ≤ 4.5 GiB | 1.05 GiB / ≈ 3.7 GiB |

**Open observation C4 — live Preview during a continuous scrub.** The measurement of C4 found that, during a continuous
scrub by dragging the ruler, the Preview does **not** update live (only 1 redraw during the ~860 ms gesture); the new frame
appears after the mouse-up. This is recorded as a separate open observation, not as an agreed defect and not as part of the
C4 target. No fix or optimisation of Preview/Export was authorised by the product owner.

**What the measurements do not cover:** the playback's dropped frames are not counted by the application (the known
issue), so the effective playback rate and the redraw rate stand in for them; C2 was measured on the same profile after
the test edits themselves; the machine was otherwise idle (0.7 % of the cores), the figures are from one machine and one
build; nothing was optimised and no behaviour was changed.

## D. Open manual checks of earlier phases (Step 30.6)

The NOT RUN rows of `PHASE9_MANUAL_TEST_PLAN.md` … `PHASE29_MANUAL_TEST_PLAN.md` inventoried: on the main path → run in
the portable build; otherwise recorded with the reason. Not blocking S1 (D046 §7): the HDR / PQ checks of Phase 18 (the
product owner's visual check of the S1 HDR export, PQ on real PQ footage) and R10 (a real audio device, Phase 17).

**Filled at 30.6 after the run of 30.3 (2026-10-23) and after the owner's manual checks (2026-10-24).** The skeleton was written at Step 30.0 (2026-10-22); the
full inventory — 623 rows of the earlier plans (523 PASS, 0 FAIL, 27 PARTIAL, 37 AUTO ONLY, 29 NOT RUN, 7 other) and Phase
30's own rows, the open ones merged into 28 items with their evidence and needs — is `docs/PHASE30_MANUAL_TEST_INVENTORY.md`;
the table below is its summary. The main-path items were run with the manual run of part B (2026-10-23) and the owner's
checks (2026-10-24): the mix by ear (B6) and the export in an external player (B9) are **PASS**; nothing of the main path
waits for a person any more. The status column below reads with the kinds of evidence of this plan's header: `PASS` — run
and passed; `BLOCKED` — the named part needs a person; `NOT RUN` — not checked, the reason given.

| # | Plan and row | On the main path | Decision | Status |
|---|---|---|---|---|
| O1 | The hold outline animation in the app (P21 1–9, 12, 13, 18, 20, 21) | yes → B3 | closed by 30.3: the markup mode, the holds confirmed, a vertex corrected and undone / redone, the animation appear / stay / disappear checked frame by frame | PASS (30.3, B3) |
| O2 | The animated export compared frame by frame with the Preview (P21 16) | yes → B9 | closed by 30.3 for the frames — the checkpoints equal to the Preview; the export watched in an external player by the owner on 2026-10-24 (B9) | PASS (the frames — 30.3 B9; the external player — PO 2026-10-24) |
| O3 | A v6 project in the Phase 20 build (P21 19) | no | recorded with its reason: a second, older build is not needed for S1 | NOT RUN (recorded) |
| O4 | The holds after a crop / a move in the app (P23 23) | yes → B3 / B7 | closed by 30.3: the holds confirmed and animated, the framing with a crop and a move — the picture kept in place | PASS (30.3, B3 and B7) |
| O5 | The outline stroke at 720p (P19 13) | yes (the export) | recorded with its reason: a 720p canvas was not part of the run (1440 × 900 and 1024 × 640) | NOT RUN (recorded) |
| O6 | The Phase 15–18 tools on a marked clip: slip, ripple trim, J / L cuts, unlink audio, sound-only insert (P19 R2) | yes → B2 | partly closed by 30.3 (the trim, the split, the move with snapping, the ripple delete, the trim to the playhead, the close gap); slip, J / L cuts, unlink audio and the sound-only insert recorded with their reason | PASS (the named part) / the rest recorded |
| O7 | The hold model's failure paths: DirectML forced off, a model error (P19 4; P25 12) | no (error paths) → A7 / A8 partly | recorded with its reason; in Step 30.2 **A7 is BLOCKED** by the environment (the model cannot be uninstalled here) and **A8 is PASS** (the model installed and a candidate contour confirmed) | NOT RUN / BLOCKED (recorded) |
| O8 | The hold correction during an export / on a locked track; the colour after the corrections (P20 13–15) | partly (the holds) | recorded with its reason: covered by the automated suites | AUTO ONLY (recorded) |
| O9 | The text: «+ Текст» on a portrait gym clip, the nine grid cells and a free move, the bold / colour / size changed in the app (P27 1, 4, 5) | yes → B4 | partly closed by 30.3 (the text on a new V2, the appear «Выезд» / the disappear «Проявление», the outline and the background); the refusal on a one-track project is the minor / UX defect **U1** — **not fixed by the product owner's decision of 2026-10-24** (D046 §5; the remark stays documented, no code change); the nine grid cells and the bold / colour / size changes recorded with their reason | PASS (the named part) / U1 — not fixed (the owner's decision, 2026-10-24) |
| O10 | The text export watched in a player; the vertical-framing export watched in a player (P27 9; P26 14) | yes → B9 | closed by the owner's check of 2026-10-24 (B9): both exports of 30.3 (4:31 at 1920 × 1080 60 fps and 0:25 at 1080 × 1920 60 fps) watched in an external player — the captions, the hold outlines, the transitions, the templates and the framing look correct, no critical playback problems noticed | PASS (PO, 2026-10-24 — B9) |
| O11 | A cancel during the UI-thread wait; a v7 project in the Phase 26 build; a locked track / a running export (P27 13, 10, 12) | no (edge cases) | recorded with their reason | NOT RUN (recorded) |
| O12 | The framing dialog and the context menu at 1024 × 640; Fill with a crop / a rotation / an offset (P26 15, 12) | yes → B7 | closed by 30.3 at both sizes: Fill 316.05 % / Fit 100 %, the move by a drag in the Preview, the scale, the crop by a handle drag (12 % left / 11.6 % right), Undo / Redo | PASS (30.3, B7, both sizes) |
| O13 | The transitions in the app: the duration at 24 / 25 / 30 fps; a type change, a move, a ripple, a speed near a zone; 25 / 30 fps sources; an edit during an export (P28 5, 6, 7, 8, 1, 14) | yes → B5 | partly closed by 30.3 (all five types on a cut: the dissolve blended, the dip black and the flash white at the cut frame, the zoom magnified at its peak, the push displaced; Dissolve / Push refused without handles — the correct message); the durations at the other frame rates, the 25 / 30 fps sources and the edit during an export recorded with their reason (the owner's footage has no 25 / 30 fps source) | PASS (the named part) / the rest recorded |
| O14 | A v7 project with dissolves exported from the app (P28 11) | partly | recorded with its reason | PARTIAL (recorded) |
| O15 | The music in the app: the import by the Media Browser and drag and drop, placing under the video, the volume by field and slider, the trim / split / delete, fitting the end, the export by ear, the Preview across a trim (P29 B1–B4, B6–B8) | yes → B6 | partly closed by 30.3 and the owner's check of 2026-10-24 (B6): the volume 50 % / 70 %, the fades 1 / 2 s and 0.5 / 1 s, one undo step each; the export's level per second follows the fades, the music's end −18.7 dBFS → silence; **the mix by ear — PASS** (both exports listened to: the music and the gym's own sound are correct, the fade-out is normal, no critical clicks, level jumps or desynchronisation noticed). The music file is the owner's MP3 `SawanoHiroyukinZkRei_-_TO_BE_HERO_X_-_Opening_FULL_INERTIA_79350751.mp3` (MP3, 44 100 Hz, stereo, 199.6 s — the parameters M1 requires of `docs/PHASE29_MANUAL_TEST_PLAN.md` part B; the optional M4A / AAC counterpart not provided). The import by the Media Browser and drag-and-drop, and the Preview across a trim, were not run by hand and are recorded with their reason | PASS (the named part) / the rest recorded |
| O16 | The templates in the app: the menu, the Inspector, Split, the playback, the export (P29 12) | yes → B6 | partly closed by 30.3 («Боулдер» (route 3, 6B+, TOP) and REC drawn in the Preview and in the export); the menu, the Inspector and Split recorded with their reason | PASS (the named part) / the rest recorded |
| O17 | The volume by ear (P25 8, F9) | yes → B6 | closed by the owner's check of 2026-10-24 (B6): both exports listened to — the music and the gym's own sound are correct, the fade-out is normal, no critical clicks, level jumps or desynchronisation noticed (the rendered levels measured ±6.02 dB) | PASS (PO, 2026-10-24) |
| O18 | The interruption of an audio device — R10 (P15 32; P17 R10) | no | recorded with its reason: a physical audio device to unplug; **not blocking S1** (D046 §7) | NOT RUN (recorded, not blocking) |
| O19 | HDR: the S1 export on a phone / an SDR monitor, real PQ footage, an SDR import in the app, an export while the colour is unknown, the Phase 15–17 tools on HDR clips, the HDR picture in the Phase 17 build (P18 3, 11, 12, R3, R5, PQ) | no | recorded with its reason: the owner's gym clips may be HDR — the exports of B9 were watched by the owner on 2026-10-24, but the HDR / PQ checks themselves stay open; **not blocking S1** (D046 §7) | NOT RUN / PARTIAL (recorded, not blocking) |
| O20 | The linked sound and the crossfades by hand (P17 2, 3, 5, 6, 11, 12, 14, 17, 19, 21, 22, 23) | partly → B2 | recorded with its reason: covered by the automated suites | AUTO ONLY (recorded) |
| O21 | The source viewer's K / L, an analysed asset in Source without ffmpeg, an audio asset (P16 2, R4, 12) | no | recorded with its reason | PARTIAL / AUTO ONLY (recorded) |
| O22 | The project / export settings by hand: save / recovery / an older file, the defaults, every quality level, the speed preset and the bitrate, a paste after a rate change (P13 1, 2, 3, 12a, 18, 19, 20) | partly → B9 | partly closed by 30.3 (B8: saved, the app closed and restarted, the project opened — everything as before, a Save As right after the reopen wrote the same `project.json` except its name); the export settings by hand recorded with their reason | PASS (the save / reopen, 30.3 B8) / the rest recorded |
| O23 | The slip export, the header during an export; the relink / Find Missing / the track delete / the media removal in the regression (P15 24, 7; P15 R8) | no | recorded with its reason | AUTO ONLY / PARTIAL (recorded) |
| O24 | The track reorder heard, a rejected paste's last case, a removal during the analysis, New during an import (P12 5, 26, 12, 33) | no | recorded with its reason: the races are not reproducible by hand | PARTIAL / NOT RUN / AUTO ONLY (recorded) |
| O25 | A damaged project through «Открыть», the export warnings dialog (P25 13, 14) | no (error paths) | recorded with its reason | PARTIAL (recorded) |
| O26 | The cancel of an export during the finalizing (Export 9) | no | recorded with its reason: optional, never hit in four runs | NOT RUN (optional, recorded) |
| O27 | A v3 project in an older build (P10 2) | no | recorded with its reason: optional | NOT RUN (optional, recorded) |
| O28 | The 4K export scenes (P9 4K) | no | covered by the heavy run of 30.7 | AUTO ONLY (recorded) |

## E. Defects found

Severity per D046 §5: blocker / major fixed in Phase 30; minor / UX brought to the product owner.

| Id | Found in | Description | Severity | Decision | Status |
|---|---|---|---|---|---|
| U1 | 30.3 B4 (both sizes) | «+ Текст» on a project with one video track refuses: «Нельзя добавить текст на V1: клип начнётся внутри другого клипа…» — by design (titles go on the topmost video track, never a new one), but the message does not say to add a track | minor / UX | **the product owner, 2026-10-24: not fixed in Step 30.4** | not fixed — the product owner's decision (2026-10-24); the remark stays documented as a minor / UX defect, **no code change**. The reason is the decision, **not** a confirmation that the defect is absent (D046 §5) |

**Withdrawn observation (former D1) — not a defect of the application.** During the run of part B, after the window
moved from the 125 % monitor to a 100 % one, the window looked laid out at the old scale (a black fifth at the right and
bottom of the captures) and posted input landed 1.25 × off. Investigated on 2026-10-23: an artefact of the test driver.
`pwsh.exe` is system aware (DPI 120) and its call to switch to PerMonitorV2 (`SetProcessDpiAwarenessContext`) returned
`False`, so Windows scaled the window sizes, the input coordinates and the captures it handled. The app's window is
PerMonitorV2 (as its manifest declares); inside its process the sizes and the input matched throughout.

- Checked with the driver's thread switched to PerMonitorV2: a bare Avalonia window (11.1.3; the same result with
  11.1.5, 11.2.8, 11.3.22 under the old driver) and the portable build `0.30.0+cf718bc7dd.dirty`.
- Covered: programmatic moves 125 % → 100 % → 125 % → 100 % (`SetWindowPos`), resizing the window on each monitor,
  clicks. The logical size was kept across the move (1440 × 900 → 1438 × 899), the controls reached the client area's
  edges at every step (no empty areas), a click at (600, 400) arrived at (600, 400), and clicks at the centre and 4 px
  inside both edges of «Параметры…» on the 100 % monitor opened its dialog while the old 1.25 × point missed it.
- **NOT RUN:** dragging the window between the monitors with the real mouse, then clicking near the right and bottom
  edges and resizing it — a person's check.
- **PO, 2026-10-24 — PASS:** the owner performed this check by hand — the window dragged between the monitors with the real
  mouse at 125 % ↔ 100 %, the interface's interaction and the clicks, the resize; **no problems were found, the interface's
  elements work correctly**. The observation is the owner's (by eye); no new measurement was made. The withdrawn
  observation above stays withdrawn — it was an artefact of the test driver, and this check covers only what the driver
  could not do.
