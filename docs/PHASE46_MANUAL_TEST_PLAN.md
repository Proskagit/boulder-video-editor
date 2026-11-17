# Phase 46 — manual test plan (D065: smooth hold outlines)

Build: Debug or Release of `feat/phase-46-contour-smoothing`. Media: a bouldering video (4K or 1080p) with round holds and
holds whose edges SAM recognises badly (dark holds on a dark wall, volumes, holds in shadow); a project from an earlier
build with marked holds (a **copy**). Each row: steps → expected result.

## A. New holds

| # | Steps | Expected |
|---|---|---|
| A1 | «Разметить зацепки», click a round hold whose edge was a «ёжик» before. | The candidate outline is smooth: a circle, or a smooth «amoeba» around an uneven hold; it still lies on the hold's edge (not visibly inside or outside it). |
| A2 | With the candidate shown, move «Сглаживание» from 50 to 0, then to 100. | At 0 the outline is the mask's own jagged edge (as before Phase 46); at 100 the roundest; the candidate changes at once while the slider moves. |
| A3 | Confirm the candidate at 50; mark 5–10 more holds of different shapes (round, long, angular). | Every outline smooth; an angular hold's corners slightly rounded at 50, more at 100. |
| A4 | Select a confirmed hold, «Править контур»: drag a vertex. | The handles stand on the outline; the outline follows the vertex — smooth where the contour bends gently, a sharp bend (over 30°) drawn as a corner; the edit is not smoothed again after the release. |
| A4a | On the same hold drag vertices so that it becomes a rough rectangle (4–5 vertices). | Its sides are drawn straight, its corners sharp — the outline never bulges out of the vertices. |
| A5 | Proposals (if enabled in the settings): accept a few. | The accepted holds' outlines are smooth like A1. |
| A6 | Restart the app. | «Сглаживание» shows the value set last. |

## B. Drawing (Preview = export)

| # | Steps | Expected |
|---|---|---|
| B1 | Leave the markup mode, play the clip with the hold animation on. | The outlines appear / disappear as before, smooth, in the chosen colour and width, the keyline intact. |
| B2 | A clip with hold tracking (the camera moves) and with Follow / Occlusion (a person in front of the holds). | The outlines move with the holds and are hidden behind the person as before; no gaps, no flicker at the curve's joins. |
| B3 | Export the clip (1080p and 4K). | The outlines in the exported file look like the Preview: smooth, no corners or steps. |
| B4 | Zoom the Preview (if available) / look at a large hold on 4K. | The curve has no visible straight pieces or corners. |

## C. Existing projects

| # | Steps | Expected |
|---|---|---|
| C1 | Open the copy of a project from an earlier build. | Opens without a message; the holds are where they were; their outlines look as before or barely softer (the jagged edge is kept — the owner's decision, D065 §1; only long straight sides are drawn as a curve). |
| C2 | Save it, reopen it. | Nothing changed in the holds; the file stays `project.json` v25. |
EOF
echo done