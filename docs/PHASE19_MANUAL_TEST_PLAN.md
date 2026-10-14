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

Step 19.8 real-app run (Claude, 2026-10-14): Debug build, isolated profile (`USERPROFILE` / `LOCALAPPDATA` / `APPDATA` in a
scratch folder, the model hard-linked into it), the fixture `tools/manual/New-Phase19MarkupFixture.ps1` with the E1 clip c6
(4K, 60 fps) on a 1920 × 1080 timeline, 1440 × 900 window, input through UI Automation and synthesized mouse / keys. Stopped
early: the product owner was using the desktop, so synthesized input could reach other windows. The rows not run are left to
the product owner.

## Model and inference (Steps 19.4, 19.7)

| # | Scenario | Expected | Automated coverage | Status |
|---|---|---|---|---|
| 1 | No model files | The AI hold markup unavailable with a message naming the file and the folder; the rest of the app works | (19.4) | auto (19.4); 19.8 (Claude): NOT RUN |
| 2 | A model file with a wrong SHA-256 | Not loaded; "damaged or another version"; nothing replaced or downloaded | (19.4) | auto (19.4); 19.8 (Claude): NOT RUN |
| 3 | The model in place, DirectML available | Markup works; no CPU message | (19.4) | 19.8 (Claude): PASS — model verified and frame prepared in 5.9 s (first use in the session), no CPU message |
| 4 | DirectML unavailable (forced) | The operation completes on the CPU; non-blocking status-bar message | (19.4) | auto (19.4); 19.8 (Claude): NOT RUN |
| 5 | Cancel the frame preparation | Cancelled within 0.5 s; no candidate; the project not dirty | (19.4 / 19.5) | auto (19.4 service; 19.5 view model: Esc while preparing — no candidate, not dirty, no step); 19.8 (Claude): NOT RUN |

## Markup mode (Step 19.5)

| # | Scenario | Expected | Automated coverage | Status |
|---|---|---|---|---|
| 6 | Enter the mode on a paused frame of a video clip | Frame prepared with progress; the Preview takes clicks | (19.5) | 19.8 (Claude): PASS — progress shown ("Checking the hold model…"), then the hint; clicks taken |
| 7 | Click a hold; cycle the alternatives (wheel / Tab) | A candidate with three alternatives; masks > 2 % of the frame hidden | (19.5) | 19.8 (Claude): PASS — candidate under the click; "Outline 1 of 3" → Tab → "Outline 2 of 3"; "No hold there …" shown for a click with no visible mask |
| 8 | Shift+click on a two-tone hold | The same candidate refined; no new hold; no alternatives while it has several points | (19.5) | auto (19.5); 19.8 (Claude): NOT RUN |
| 9 | Enter / Esc / Delete in the mode | Confirm / discard / remove the selected contour; never a timeline clip deleted | (19.5) | 19.8 (Claude): PARTIAL — Enter confirmed a hold (title dirty, Undo on, "1 marked", the outline drawn by the composition); of five quick click / Enter pairs only one hold was added. Likely cause (phase review): an Enter pressed while the model was still predicting was consumed without effect — fixed: it is applied when the candidate appears (UI test); a click during a running confirm no longer drops it (UI test); local `MarkupScenarioTests` with the real model: 6 of 6. Esc / Delete: auto only. **PO to re-check** |
| 10 | Zoom / loupe on a small hold | Precise click possible | (19.5) | 19.8 (Claude): PASS — the loupe shows the full-size frame under the pointer (compared with an enlarged crop of the Preview) |
| 11 | Seek or Play | The mode left; confirmed holds kept; the candidate discarded | (19.5) | auto (19.5: seek, Play, the Source mode, an export, another project); 19.8 (Claude): NOT RUN |
| 12 | An HDR (HLG) clip | The frame as in the Preview (rule S1); markup works | (19.5) | auto (19.5, the D033 decode path); 19.8 (Claude): NOT RUN |

## Route outline (Steps 19.2, 19.3, 19.6)

| # | Scenario | Expected | Automated coverage | Status |
|---|---|---|---|---|
| 13 | Stroke on 1080p, 720p, 4K and a 1080 × 1920 canvas | The width per the short side (default 4 at 1080); Preview and export consistent | (19.3) | auto (19.3) |
| 14 | Auto colour; add / remove holds, also from another frame | One colour for the route; recomputed only on add / remove; never per frame | (19.2) | auto (19.2); local (19.6, 24 E1 routes) |
| 15 | Manual colour; keyline Auto / On / Off | The keyline per D034 §5 for the automatic and the manual colour | (19.2) | auto (19.2, 19.6) |
| 16 | The threshold examples (with / without the keyline): `<E1>/results/calibration-20261014-224451/sheet_band*.png`, `sheet_auto_*.png`, `README.md` | T = 0.12 (calibrated, proposed) accepted by the product owner — or another value read off the sheets | (19.6) | PO |
| 17 | Crop, position, scale, rotation; fades, a dissolve, a hidden track | As D034 §6 | (19.3) | auto (19.3) |

## Edits and project (Steps 19.1, 19.2)

| # | Scenario | Expected | Automated coverage | Status |
|---|---|---|---|---|
| 18 | Undo / redo of a confirm, a removal, a style change | One step each; style changes merge | (19.2) | auto (19.2, 19.5) |
| 19 | Split, Duplicate, Paste a marked clip | Independent copies of the markup | (19.1) | auto (19.1) |
| 20 | Save, close, reopen a v5 project | The markup and every hold's colour samples unchanged; Auto gives the same colour after an add / remove | (19.1, the serialization test of D034 §7) | auto (19.1) |
| 21 | Open a Phase 18 (v4) project; save | Opens unchanged; saved as v5 | (19.1) | auto (19.1); 19.8 (Claude): PASS — the v4 fixture opened |
| 22 | Open the v5 project in the Phase 18 build | Refused with the "saved by a newer version" message | (19.1) | auto (19.1: the mechanism — a newer format version is refused); the Phase 18 build itself: 19.8 (Claude): NOT RUN |
| 23 | Edit a hold on a locked track; during an export | Refused | (19.2) | auto (19.2: a locked track; 19.5: during an export the style edit is refused and the mode left, `EditingLock`) |

## Acceptance on real data (Step 19.8)

| # | Scenario | Expected | Coverage | Status |
|---|---|---|---|---|
| 24 | Criterion 10 on the E1 set | ≥ 155/164 SDR clicks without marked problems (the protocol in DEVELOPMENT_PLAN, Phase 19) | local (19.8) + PO re-checks | local (19.8): PASS — 156/164 (О 3, К 5, RECHECK 0), report `<E1>/results/20261014-230816/` |
| 25 | The local real-weights suite | Green; report recorded (model, environment, IoU, DirectML vs CPU, performance) | local (19.8) | local (19.8): PASS — 9 / 9; C# = Python reference (median IoU 1.0000, order 188/188), DirectML = CPU, GPU frame 417 ms / CPU 8.0 s, click 49 ms |
| 26 | 2–3 of the product owner's clips: mark a route, export, view | Contours readable, equal to the Preview; the time to mark a route recorded | — | PO |

## Regression

| # | Scenario | Expected | Status |
|---|---|---|---|
| R1 | A Phase 18 project without markup in the Phase 19 build | Opens and exports as in the Phase 18 build (the default export byte for byte) | 19.8 (Claude): NOT RUN |
| R2 | The Phase 15–18 tools on a marked clip | As before; the markup follows D034 §6 | 19.8 (Claude): NOT RUN |
