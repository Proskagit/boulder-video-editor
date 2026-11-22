# Phase 56 — the holds' contour: the manual check (D075)

For the product owner. Only the smoothing changes (the refinement on a close-up was measured and withdrawn, D075 §2). The
automated part: a jagged triangle keeps its three tips while its teeth are smoothed (without the rule a tip was cut by 8.7 px),
a round hold has no corners, a square keeps its corners at every strength, a saved contour never changes. What needs **your
eyes**: the outlines on your footage. Use **copies** of your projects.

Mark each row PASS / FAIL / note.

| # | Steps | Expected |
|---|---|---|
| 1 | Open a saved project (copy). | Its holds look exactly as before. |
| 2 | «Разметить зацепки», click a round / uneven hold. | No teeth along the edge; as quick as before. |
| 3 | Click a triangle hold (as on your screenshot). | Its tips stay in the outline, not rounded off; the sides are smooth. |
| 4 | Move «Сглаживание» 0 → 50 → 100 on a candidate. | 0 — the mask's own edge; higher — smoother, the tips of a pointed hold kept at every strength. |
| 5 | A proposed route («Предлагать трассу») → «Принять». | Its holds smoothed the same way (teeth gone, tips kept). |
| 6 | A very small hold (a foot chip). | Smoothed as before (no stuck pixel corners). |
| 7 | The blue hold with the notch. | If the notch is still there, it is in the model's mask (out of scope, D075): Shift+click or edit by hand. |
