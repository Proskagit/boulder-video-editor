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
| B1 | Import the gym clips (horizontal and vertical) and the music | Analysed, thumbnails and waveforms; the canvas offered by the first video (D041) | Timeline / UI tests | NOT RUN (30.3) |
| B2 | Cut away the excess, split and arrange the clips (trim, split, move, ripple delete, snapping), Undo / Redo | One step per action; the Preview follows | Timeline.Tests | NOT RUN (30.3) |
| B3 | Recognise holds, choose the wanted ones, correct a contour, outline them with their animation | D034–D036 as specified; the outlines in the Preview | Vision / Rendering tests | NOT RUN (30.3) |
| B4 | Add text with an animated appearance and disappearance, an outline or background | D043 as specified | Rendering / UI tests | NOT RUN (30.3) |
| B5 | Add transitions between clips (Наплыв, Затемнение, Вспышка, Наезд, Сдвиг) | D044 as specified | Rendering / Timeline tests | NOT RUN (30.3) |
| B6 | Add music, set its volume and fades; apply a template (REC / VHS / «Боулдер» / «Хайлайт») | D045 as specified; the mix by ear | Export / Rendering tests | NOT RUN (30.3) |
| B7 | Set the framing (Fill / Fit, Move / Scale / Crop), check the Preview | D038, D041 as specified | UI / Rendering tests | NOT RUN (30.3) |
| B8 | Save, close, reopen the project | Everything as before; nothing offline | Project.Tests | NOT RUN (30.3) |
| B9 | Export the video; watch it in an external player | The file plays; the picture, the text, the outlines, the transitions, the template and the sound as in the Preview | ExportEndToEnd.Tests | NOT RUN (30.3) |

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
| — | *(filled from 30.2)* | | | | |
