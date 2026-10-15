# Phase 20 — manual test plan

Manual checks of the manual correction of hold contours (DECISIONS.md D035). Run in the real app with the hold model
installed (`docs/AI_MODEL.md`) and an isolated profile (`USERPROFILE` pointed to a scratch folder, `LOCALAPPDATA` /
`APPDATA` to its `AppData\Local` / `AppData\Roaming` — the known folders resolve from the profile — with the model
hard-linked into it), as in Phases 11–19. Fixture: `tools/manual/New-Phase19MarkupFixture.ps1` with the E1 clip c6
(`1014(6).mp4`, 3840 × 2160, 60 fps) on a 1920 × 1080 timeline.

Status column — kinds of evidence, never mixed:
- **auto** — covered by automated tests only, not checked in the real app;
- **20.4 (Claude): PASS / FAIL / NOT RUN** — the real-app run of Step 20.4;
- **PO** — run by the product owner.

Real-app run (Claude, 2026-10-15): a Debug build of `feat/phase-20-hold-contour-editing` built into a scratch folder (the
product owner's own app instance was running from `src/App/bin/Debug` and was left alone), the isolated profile, a 1440 ×
900 window and the app's minimum width; input synthesized only after checking that the test app's window was in the
foreground and under the point; screenshots through `PrintWindow`; the export checked with ffmpeg against the saved
contour. Footage and screenshots stay outside the repository.

| # | Scenario | Expected | Automated coverage | Status |
|---|---|---|---|---|
| 1 | Select a confirmed hold in the markup mode | Handles at its vertices (white squares; the one under the pointer yellow); the mode text names the gestures | `HoldMarkupUiTests` | **PASS** — first run: a small hold (≈ 45 px in the Preview) showed overlapping handles → **fixed** (handles at least 14 screen px apart, D035 §2); after the fix 5 separate handles, 3 at the minimum window width |
| 2 | Drag a handle | The contour follows live (dashed), the composition's outline moves on release; one undo step | `HoldMarkupUiTests`, `HoldMarkupEditTests` | **PASS** — the top handle dragged 26 px up; the hidden vertices followed with the falloff |
| 3 | Undo / Redo of a drag | The original contour back, then the corrected one; the hold stays selected | `HoldMarkupUiTests`, `HoldMarkupEditTests` | **PASS** — Ctrl+Z / Ctrl+Y; also two Ctrl+Z through two later steps, one step each |
| 4 | Alt+click on the outline | A new handle there (one step, or a hidden vertex shown as a handle) | `ContourEditingTests`, `HoldMarkupUiTests` | **PASS** after two fixes found in this run: on a dense contour the click fell on an edge shorter than a pixel and was silently ignored → the vertex there becomes a handle; a click within the reach of a neighbouring handle was taken as a press on that handle → with Alt the outline wins. Alt+click away from the outline shows "Alt+click on the selected outline adds a point there." |
| 5 | Drag the new handle | As 2 | — | **PASS** |
| 6 | Double-click a handle | The vertex removed (the stretch between its neighbours straight); the hold stays | `ContourEditingTests`, `HoldMarkupUiTests` | **PASS** |
| 7 | Delete with the pointer over a handle | The vertex removed, not the hold; Delete elsewhere removes the hold (D034) | `HoldMarkupUiTests` | **PASS** (vertex); the hold removal by Delete: auto |
| 8 | Remove down to three, then once more | Refused: "An outline keeps at least three points." | `ContourEditingTests`, `HoldMarkupUiTests` | **PASS** |
| 9 | Drag a handle across the contour | Drawn red while it crosses; on release refused: "The outline can't cross itself — the change is not applied."; the contour as before | `ContourEditingTests`, `HoldMarkupUiTests`, `HoldMarkupEditTests` | **PASS** (a triangle's vertex moved to the other side is no crossing and was accepted — correct) |
| 10 | Esc during a drag | The drag abandoned, nothing changed, the hold still selected | `HoldMarkupUiTests` | **PASS** |
| 11 | Save, close, reopen | `project.json` v5 with the same members (`id`, `points`, `surfaceLab`, `wallLab`, `sourceTimeTicks`); the corrected contour drawn after reopening, no handles outside the mode; the hold selectable and editable again | `HoldMarkupPersistenceTests`, `HoldMarkupUiTests` | **PASS** — 14 vertices saved; the reopened outline identical to the one before closing |
| 12 | Export | The corrected contour in the exported video exactly as in the Preview; no handles in the export | `HoldOutlineRenderTests` | **PASS** — 1920 × 1080, 60 fps, 5 s; at 1 s all 381 samples along the 14 saved edges lie on the outline colour (100 %); no handle squares; the shape equals the Preview's |
| 13 | An export running | The mode leaves, no correction possible | `HoldMarkupUiTests` | auto (the export dialog is modal in the real app) |
| 14 | A locked track | The mode leaves on locking; a correction is refused by the edit service | `HoldMarkupUiTests`, `HoldMarkupEditTests` | auto |
| 15 | The route colour and the colour samples after corrections | Unchanged (the samples never re-taken) | `HoldMarkupEditTests`, `HoldMarkupUiTests` | auto; in the run the Auto colour stayed #FF00FF through every correction |
| 16 | The product owner's close-ups with wrong large masks (floor, wall panel) | A wrong boundary corrected in a few gestures | — | **PO** |
