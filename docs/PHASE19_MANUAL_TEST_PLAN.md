# Phase 19 — manual test plan

Run in the real app (`dotnet run --project src/App/App.csproj`, ffmpeg / ffprobe on PATH unless the scenario says
otherwise). Written at Step 19.0 (DECISIONS.md D034, specification revision 5, accepted 2026-10-14) as a **skeleton**: the
steps that implement the features complete the scenarios and their automated coverage; Step 19.8 runs them in the real
app with the regression. Manual runs use an isolated profile (`USERPROFILE` / `LOCALAPPDATA` / `APPDATA` pointed to a
scratch folder), as in Phases 11–18, and UI Automation. Every new control is checked at 1024 and 1440 px window width.

Model: `sam2.1-large-v1` placed by hand in `%LOCALAPPDATA%\AiVideoEditor\models\sam2.1-large\` (D034 §2; in an isolated
profile — in that profile's `LOCALAPPDATA`). Footage and reference data: the product owner's clips and the E1 / E2
reference folder `%USERPROFILE%\Desktop\AiVideoEditor-E1-Reference\` (never committed). Builds: the Phase 19 build; for
the regression the Phase 18 build of `main` (`c29a767`, built from `git archive`).

Status column — kinds of evidence, never mixed:
- **auto (19.x)** — covered by automated tests only, not checked in the real app;
- **local (19.x)** — covered by the local real-weights suite (`tests/Vision.ModelTests`), not by the regular CI;
- **19.8 (Claude): PASS / FAIL / NOT RUN** — the formal Step 19.8 run in the real app;
- **PO** — run by the product owner.

Real-app run 2 (Claude for the product owner, 2026-10-15, with the owner's permission to drive the GUI): Debug build,
isolated profiles, the fixtures of `tools/manual/New-Phase19MarkupFixture.ps1` for the E1 clips c6 and c8 and the HLG clip
"боулдеринг нежные цвета контраст" on 1920 × 1080 timelines, 1440 × 900 window; synthesized mouse / keys only after checking
that the foreground window and the window under the point belong to the test app (the check stopped the run once, when another
window was in front — no input was sent). Exported files checked with ffmpeg / ffprobe against the stored contours. Results
below are this run's; "auto" alone is never counted as passed.

Step 19.8 real-app run (Claude, 2026-10-14): Debug build, isolated profile (`USERPROFILE` / `LOCALAPPDATA` / `APPDATA` in a
scratch folder, the model hard-linked into it), the fixture `tools/manual/New-Phase19MarkupFixture.ps1` with the E1 clip c6
(4K, 60 fps) on a 1920 × 1080 timeline, 1440 × 900 window, input through UI Automation and synthesized mouse / keys. Stopped
early: the product owner was using the desktop, so synthesized input could reach other windows. The rows not run are left to
the product owner.

## Model and inference (Steps 19.4, 19.7)

| # | Scenario | Expected | Automated coverage | Status |
|---|---|---|---|---|
| 1 | No model files | The AI hold markup unavailable with a message naming the file and the folder; the rest of the app works | (19.4) | **PASS** (real-app run 2) — no model files: Mark holds stays off, "The hold model sam2.1-large-v1 is not installed: sam2.1_large.encoder.onnx is missing in <full folder>"; editing stays available. Defect found and fixed: with an unset profile the folder was named relative (`8f2ff61`) |
| 2 | A model file with a wrong SHA-256 | Not loaded; "damaged or another version"; nothing replaced or downloaded | (19.4) | **PASS** — a fake encoder file: not loaded, "damaged or another version (expected sam2.1-large-v1, SHA-256 5ca0f505…)", the file left byte for byte, nothing downloaded |
| 3 | The model in place, DirectML available | Markup works; no CPU message | (19.4) | **PASS** — DirectML, no CPU message; first Mark holds in a session 5.5–6.0 s (model check + sessions + 4K decode + encode), later ones ≈ 1.1 s |
| 4 | DirectML unavailable (forced) | The operation completes on the CPU; non-blocking status-bar message | (19.4) | **BLOCKED** in the GUI — no way to force a DirectML failure without changing system files; auto (19.4) only |
| 5 | Cancel the frame preparation | Cancelled within 0.5 s; no candidate; the project not dirty | (19.4 / 19.5) | **PASS** — Esc and the Cancel button while preparing: mode off in ≈ 0.3 s, no candidate, title not dirty |

## Markup mode (Step 19.5)

| # | Scenario | Expected | Automated coverage | Status |
|---|---|---|---|---|
| 6 | Enter the mode on a paused frame of a video clip | Frame prepared with progress; the Preview takes clicks | (19.5) | **PASS** — progress in the Preview bar ("Checking the hold model…"), then "Hold markup — click a hold, Enter confirms"; the picture keeps its size (defect found and fixed: the hint below the picture shrank it, `9c145ca`) |
| 7 | Click a hold; cycle the alternatives (wheel / Tab) | A candidate with three alternatives; masks > 2 % of the frame hidden | (19.5) | **PASS** — "Outline 1 of 3" → Tab "2 of 3" → Shift+Tab "1 of 3" → wheel "2 of 3"; the full-frame masks never offered |
| 8 | Shift+click on a two-tone hold | The same candidate refined; no new hold; no alternatives while it has several points | (19.5) | **PASS** (mechanics) — Shift+click refined the same candidate to one mask, no alternatives; Enter added one hold. Note: my second point lay on another object and the refined mask took it in — input, not a defect |
| 9 | Enter / Esc / Delete in the mode | Confirm / discard / remove the selected contour; never a timeline clip deleted | (19.5) | **PASS** — c6: five quick click → Enter pairs (Enter 0.7 s after the click, next click 0.6 s later) and two early Enters (30 ms after the click): 7 of 7 holds, twice (before and after the bar change); Esc discards; a click on a hold selects it (yellow dashes), Delete removes only it, the clips stay |
| 10 | Zoom / loupe on a small hold | Precise click possible | (19.5) | **PASS** — the loupe shows the full-size frame under the pointer (checked against an enlarged Preview crop) |
| 11 | Seek or Play | The mode left; confirmed holds kept; the candidate discarded | (19.5) | **PASS** — a frame step (→) and Play (Space) leave the mode, the candidate discarded, holds kept; opening Source leaves it (Source shows the file without outlines); an export leaves it and keeps Mark holds off while it runs; locking the track leaves it |
| 12 | An HDR (HLG) clip | The frame as in the Preview (rule S1); markup works | (19.5) | **PASS at the markup frame** — HLG portrait clip: frame prepared in 5.8 s, four holds, export aligned at 0.5–1.0 s; from ≈ 2 s the camera reframes and the static outlines no longer sit on the holds (camera motion, D034 "not in the MVP"). One click inside a donut hold took its hole (known E1 case; Tab offers the whole hold) |

## Route outline (Steps 19.2, 19.3, 19.6)

| # | Scenario | Expected | Automated coverage | Status |
|---|---|---|---|---|
| 13 | Stroke on 1080p, 720p, 4K and a 1080 × 1920 canvas | The width per the short side (default 4 at 1080); Preview and export consistent | (19.3) | **PASS** (1080p, 1080 × 1920, 4K; 720p NOT RUN) — the exported outline on 100 % of the stored contour points; stroke measured 4 px (1080p, width 4), 4 px (1080 × 1920), 9 px (4K, expected 8; anti-aliasing), 6–7 px at width 6; the Preview on 90 % of the points at its 0.3 scale (±1 px rounding) |
| 14 | Auto colour; add / remove holds, also from another frame | One colour for the route; recomputed only on add / remove; never per frame | (19.2) | **PASS** — two colourful holds → black; plus two dark holds → magenta; undoing both → black again; a width edit keeps the colour |
| 15 | Manual colour; keyline Auto / On / Off | The keyline per D034 §5 for the automatic and the manual colour | (19.2) | **PASS** — Manual black + keyline On: black core, white keyline; Manual cyan: black keyline (light core); back to Auto / Auto |
| 16 | The threshold examples (with / without the keyline): `<E1>/results/calibration-20261014-224451/sheet_band*.png`, `sheet_auto_*.png`, `README.md` | T = 0.12 (calibrated, proposed) accepted by the product owner — or another value read off the sheets | (19.6) | NOT ACCEPTED — the product owner's decision. Report `T_REPORT.md` and `T_examples.png` beside the sheets (2026-10-15): below 0.12 the keyline is needed; in 0.12–0.20 both versions read (a trade-off: crisper edge vs half the coloured core); routes outlined in their own colour score 0.03–0.30 — keyline on for 5 of 24 at T = 0.12, 16 of 24 at 0.20. In the app Auto chose black and the Auto keyline never switched on: no check error (the app equals an independent recomputation; no ring pixel near black even on c8's dark panels) — Auto scores 0.33–0.62 on these walls |
| 17 | Crop, position, scale, rotation; fades, a dissolve, a hidden track | As D034 §6 | (19.3) | **PASS** — scale 80 %, rotation 10°: outlines rotate with the layer, a click on the rotated picture selects the right hold; fade-in dims picture and outline together; inside a dissolve both layers' outlines; a hidden track shows neither |

## Edits and project (Steps 19.1, 19.2)

| # | Scenario | Expected | Automated coverage | Status |
|---|---|---|---|---|
| 18 | Undo / redo of a confirm, a removal, a style change | One step each; style changes merge | (19.2) | **PASS** — confirm, Delete, a refined hold: one step each (Ctrl+Z / Ctrl+Y); two width edits in a row merged into one step |
| 19 | Split, Duplicate, Paste a marked clip | Independent copies of the markup | (19.1) | **PASS** — Split: both halves keep the markup, a hold added to one half leaves the other at 7; Duplicate of the last clip and Copy / Paste: copies with all holds (Duplicate of a clip with a neighbour is refused by the existing overlap rule) |
| 20 | Save, close, reopen a v5 project | The markup and every hold's colour samples unchanged; Auto gives the same colour after an add / remove | (19.1, the serialization test of D034 §7) | **PASS** — saved as v5 (samples and source time in the file), closed, reopened: 7 / 8 / 8 holds and the style as saved; the next save byte-identical |
| 21 | Open a Phase 18 (v4) project; save | Opens unchanged; saved as v5 | (19.1) | **PASS** — the v4 fixtures open; saved as v5 |
| 22 | Open the v5 project in the Phase 18 build | Refused with the "saved by a newer version" message | (19.1) | **PASS** — the Phase 18 build (`c29a767`, from `git archive`): "This project was saved by a newer version of AI Video Editor and can't be opened"; the file untouched |
| 23 | Edit a hold on a locked track; during an export | Refused | (19.2) | **PASS** — on a locked track the confirm is refused ("Track V1 is locked."); fixed so that Mark holds is off there and locking leaves the mode (`9c145ca`, re-checked in the app); during an export the mode is left and the section disabled |

## Acceptance on real data (Step 19.8)

| # | Scenario | Expected | Coverage | Status |
|---|---|---|---|---|
| 24 | Criterion 10 on the E1 set | ≥ 155/164 SDR clicks without marked problems (the protocol in DEVELOPMENT_PLAN, Phase 19) | local (19.8) + PO re-checks | local **PASS** — 156/164 (О 3, К 5, RECHECK 0) |
| 25 | The local real-weights suite | Green; report recorded (model, environment, IoU, DirectML vs CPU, performance) | local (19.8) | local **PASS** (2026-10-15, one test class at a time) — 10 / 10, report `<E1>/results/20261015-110340/`: C# = Python reference (median IoU 1.0000, order 188/188), criterion 10 156/164, DirectML = CPU (median IoU 1.0000), frame preparation (4K, sessions loaded) median 350 ms / max 472 ms, first frame 379 ms, CPU 7.4 s, click median 41 ms / max 122 ms; stage profile `<E1>/results/perf-20261015-110442/perf.md` |
| 26 | 2–3 of the product owner's clips: mark a route, export, view | Contours readable, equal to the Preview; the time to mark a route recorded | — | **PASS** on three clips (c6, c8, the HLG clip): mark, confirm, save, export, view — outlines on the holds in the exported files. Marking time: scripted input only (8 holds in 14 s with fixed pauses) — a human time is the product owner's to record |

## Regression

| # | Scenario | Expected | Status |
|---|---|---|---|
| R1 | A Phase 18 project without markup in the Phase 19 build | Opens and exports as in the Phase 18 build (the default export byte for byte) | **PASS** — the same v4 project without markup exported by the Phase 18 and the Phase 19 builds: byte-identical (SHA-256 8D35E243…AA2E) |
| R2 | The Phase 15–18 tools on a marked clip | As before; the markup follows D034 §6 | **PASS** for the tools run on marked clips: split, duplicate, copy / paste, trim end to the playhead (W), speed 2×, fade-in, a dissolve, hide / show the track, lock / unlock, undo / redo, scale / rotation / crop; NOT RUN: slip, ripple trim, J / L cuts, unlink audio, sound-only insert |
