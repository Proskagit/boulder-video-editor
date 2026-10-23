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
**partial**, with what the check cannot prove (D046 §4). The environment used: *(filled at 30.2)*.

| # | Scenario | Expected | Automated coverage | Status |
|---|---|---|---|---|
| A1 | The portable folder built by the documented command from a clean checkout | The folder holds the runtime, the application, `appsettings.json`, the notices, the user's instruction (and FFmpeg once D046 §2 is decided); its size recorded | — | local (Claude), 2026-10-22: `pwsh -File tools/portable/Publish-Portable.ps1 -Zip` in a fresh clone of `e28a9d3` with the step's uncommitted changes copied in (so the build is marked `.dirty`): 349 files, 124 818 036 bytes (119.0 MiB), zip 51.1 MiB; .NET 8.0.30, SDK 8.0.424; **without FFmpeg** (D046 §2). To repeat from a commit once the step is committed |
| A2 | The notices: every shipped component named with its licence; the model and `DirectML.dll` stated as not shipped | Complete and matching the restored packages (D046 §3) | — | generated (30.1): 57 components from the deps file with their package metadata, licence files of 39 copied into `licenses\`, 18 without one; **not reviewed** — e.g. Avalonia.Fonts.Inter's metadata says MIT while the embedded Inter 3.019 fonts are under the SIL OFL 1.1 (their name table). Inventory 2026-10-22: the authoritative copies of the 18 missing texts located (D046 §13). Review pending |
| A3 | The start by a double click in the clean environment | The main window, the Russian interface, no error dialog; logs in `%LOCALAPPDATA%\AiVideoEditor\logs` | — | NOT RUN (30.2) |
| A4 | The start from another current directory (a shortcut, a console in another folder) | As A3; `appsettings.json` and the tools' relative paths read from the application folder | the locator's relative-path tests (30.1) | NOT RUN (30.2) |
| A5 | The folder in a path with spaces and Cyrillic (e.g. `C:\Мои программы\Boulder Editor`) | As A3; import, save and export work | — | NOT RUN (30.2) |
| A6 | No FFmpeg available (not bundled / removed, not on `PATH`) | The existing messages at import and export; no crash; the rest usable | the locator's tests | NOT RUN (30.2) |
| A7 | No model installed; then «Разметить зацепки» | The existing "not installed" message naming the folder and `docs/AI_MODEL.md`; the editor usable | Vision.Tests | NOT RUN (30.2) |
| A8 | The model installed as `docs/AI_MODEL.md` describes; «Разметить зацепки» | The check and loading progress; the markup works (DirectML, or the CPU fallback with its message) | — | NOT RUN (30.2) |
| A9 | The Visual C++ runtime absent (clean environment) | ONNX Runtime and the other native libraries load, or the missing dependency named for D046's risk list | — | NOT RUN (30.2) |
| A10 | The templates' fonts (Bahnschrift, Consolas, Segoe UI) in the clean environment | REC, VHS, «Боулдер», «Хайлайт» drawn as in the development build | Rendering.Tests | NOT RUN (30.2) |

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
| B6 | Add music, set its volume and fades; apply a template (REC / VHS / «Боулдер» / «Хайлайт») | D045 as specified; the mix by ear | Export / Rendering tests | BLOCKED (both sizes) for «the mix by ear» (needs a listener). Done: volume 50 % / 70 %, fades 1 / 2 s and 0.5 / 1 s — one undo step each; «Боулдер» (route 3, 6B+, TOP) and REC templates drawn in the Preview and the export; the export's level per second follows the fades (music end −18.7 dBFS → silence) |
| B7 | Set the framing (Fill / Fit, Move / Scale / Crop), check the Preview | D038, D041 as specified | UI / Rendering tests | PASS (both sizes) — Fill 316.05 % / Fit 100 %, Move by a drag in the Preview, scale by the field, crop by a handle drag (12 % left / 11.6 % right, the picture kept in place), Undo / Redo |
| B8 | Save, close, reopen the project | Everything as before; nothing offline | Project.Tests | PASS (both sizes) — saved, the app closed and restarted, opened: 0 missing, everything as before (holds and animation, text, template, transitions zoom / push, music 50 %); a Save As right after the reopen wrote the same `project.json` except its name |
| B9 | Export the video; watch it in an external player | The file plays; the picture, the text, the outlines, the transitions, the template and the sound as in the Preview | ExportEndToEnd.Tests | BLOCKED (both sizes) for «watch it in an external player» (needs a person). Done: 4:31 at 1920 × 1080 60 fps (21 min) and 0:25 at 1080 × 1920 60 fps (1:31) exported, decoded in full without an error; frames at the checkpoints (holds, text, template, transitions, framing) equal to the Preview |

Run of part B: local (Claude), 2026-10-23, portable build `0.30.0+cf718bc7dd.dirty` with gyan.dev FFmpeg 9.0.1 from its `ffmpeg\` (not on `PATH`), the product owner's clips of 2026-09-27 and the MP3 of the same folder, at 1440 × 900 and 1024 × 640 (two projects). Driven through UI Automation and messages posted to the window (no real cursor or keyboard), checked on window captures and on the exported files. The window was on a 100 % monitor. The driver (`pwsh.exe`) was not per-monitor DPI aware (system aware at DPI 120), so the window sizes, the input coordinates and the captures it saw were scaled by Windows; its input offset of 1.25 compensated for that. The app itself laid out and drew correctly; see the withdrawn observation under §E. The sizes above are the app's layout sizes; the captures' drawn widths confirm them (≈ 1456 and ≈ 1042 physical pixels at 100 %).

## C. Performance of a typical project (Step 30.5)

The project: several minutes, a few tracks, holds, text, transitions, a template, music (D046 §6). Measured first; the
targets agreed with the product owner afterwards. Machine: *(filled at 30.5)*.

| # | Measure | Definition | Result | Target (agreed later) |
|---|---|---|---|---|
| C1 | Opening the project | From Open to the timeline usable | — | — |
| C2 | The timeline's response | Move / trim / split on a busy timeline | — | — |
| C3 | The Preview's playback | Dropped frames over the project at 1440 × 900 | — | — |
| C4 | Scrubbing | The Preview following the playhead | — | — |
| C5 | The export | Its time against the project's length | — | — |
| C6 | Memory | The process's working set during playback and export | — | — |

## D. Open manual checks of earlier phases (Step 30.6)

The NOT RUN rows of `PHASE9_MANUAL_TEST_PLAN.md` … `PHASE29_MANUAL_TEST_PLAN.md` inventoried: on the main path → run in
the portable build; otherwise recorded with the reason. Not blocking S1 (D046 §7): the HDR / PQ checks of Phase 18 (the
product owner's visual check of the S1 HDR export, PQ on real PQ footage) and R10 (a real audio device, Phase 17).

| # | Plan and row | On the main path | Decision | Status |
|---|---|---|---|---|
| — | *(filled at 30.6)* | | | |

## E. Defects found

Severity per D046 §5: blocker / major fixed in Phase 30; minor / UX brought to the product owner.

| Id | Found in | Description | Severity | Decision | Status |
|---|---|---|---|---|---|
| U1 | 30.3 B4 (both sizes) | «+ Текст» on a project with one video track refuses: «Нельзя добавить текст на V1: клип начнётся внутри другого клипа…» — by design (titles go on the topmost video track, never a new one), but the message does not say to add a track | minor / UX | the product owner | open |

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
