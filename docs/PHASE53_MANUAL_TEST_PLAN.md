# Phase 53 — the route from its first hold: the manual check (D072)

For the product owner. The automated part ran on the build machine with a stand-in model on synthetic walls: the first click
opens the analysis, only Esc acts meanwhile, the route's holds are proposed on the markup frame, the review (remove, add by a
click + Enter), «Принять» = one Undo step, «Готово» keeps what is shown, «Отменить» / «Отмена»
/ a click beside any hold / the check box off change nothing; the split colour map and the chroma / hue gates. What needs **the
real model, real walls and eyes** is here. Use **copies** of your clips in a new test project; walls of different colours, at
least one with large volumes or panels of the route's colour.

Mark each row PASS / FAIL / note. Where you can, note for each route: how many holds the route has, how many were found, how
many wrong ones were proposed, and how long the analysis took.

## 0. Step 0

| # | Steps | Expected |
|---|---|---|
| 01 | Start the build, open a project with a 4K clip. | The Preview's quality button shows «Ориг.» — «Оригинал» by default (a choice made earlier stays). |

## A. The route from its first hold

| # | Steps | Expected |
|---|---|---|
| A1 | Select a clip, pause on a frame where the route's wall is visible, «Разметить зацепки». | In the Inspector the check box «Предлагать трассу» is shown (the route has no holds yet), **unticked** by default (amendment 2) — tick it. |
| A2 | Click one hold of the route. | A window «Подождите, идёт анализ» with a bar and «Отмена» covers the editor; nothing else reacts (Space, Ctrl+Z, clicks). It closes after a few seconds (aim: ≤ 20 s). |
| A3 | Look at the frame. | The route's holds found are proposals drawn in the route's colour (amendment 1, C3), the clicked one among them; in the Inspector «Предложено: N», «Отклонить все», «Принять», «Отменить»; the status «Предложено зацепок трассы: N». The playhead has not moved. |
| A4 | Click a wrong proposal, press Delete. | It disappears; nothing else changes. |
| A5 | Click a missed hold, check its outline (wheel / Tab for another), Enter. | It joins the proposals (not yet the clip). |
| A7 | «Принять». | All proposals become holds in the route's colour; the status «Добавлено зацепок: N»; the markup goes on (the next click marks one hold by hand). One Ctrl+Z removes them all, Ctrl+Y returns them. |
| A8 | «Готово». | The intro and the final are laid out; the orange starts on the frame you marked on (amendment 2, D2). |
| A9 | Again on a new route: first click → review → «Готово» directly. | The proposals shown are kept as holds, then the intro and final. |
| A10 | Again: first click → review → «Отменить». | The proposals vanish, nothing added; the next click marks one hold by hand. |
| A11 | Again: first click → «Отмена» (or Esc) in the window. | The window closes, nothing changed; the next click marks one hold by hand. |
| A12 | First click on the bare wall (beside any hold). | The status says no hold was found there; nothing proposed; the next click marks one hold by hand. |
| A13 | Esc during the review. | A candidate / selection is discarded first; otherwise the review stays as it is. Play or a seek leave the markup and drop the proposals (the project unchanged). |
| A14 | Re-enter «Разметить зацепки» on the route that now has holds. | The check box is hidden; the first click marks one hold by hand (no analysis). |
| A15 | Untick «Предлагать трассу» (Inspector or «Настройки…»), restart the editor; then tick it, restart. | The choice is kept each time; unticked, the first click on a new route marks one hold by hand. |
| A16 | A wall with large volumes / panels of the route's colour; a dull (grey / black / white) route; a route next to another of a similar colour. | Note found / wrong per route. Expected limits (D072 §4): about 60 % found, a few wrong per frame; a click on a hold bolted onto a volume may take the volume's colour (research error B). |
| A17 | A 4K clip (the markup frame is full size). | The analysis may take longer; the holds lie exactly on the holds after «Принять» and follow the camera as manual ones do. |

## B. What is gone

| # | Steps | Expected |
|---|---|---|
| B1 | The Inspector's «Зацепки» outside the markup. | No hidden «Авторазметка» button, no «Показать N» list, no «Принять все» — only «Отклонить все» while a route is reviewed. |
| B2 | «Настройки…» → the hold settings. | «Предлагать трассу» with its note instead of «Предлагать зацепки автоматически». |

## C. Amendment 1 (the owner's check of 1.5.0)

| # | Steps | Expected |
|---|---|---|
| C1 | The two frames of the owner's check (the green route on the light wall; the turquoise one beside the blue holds). | No proposal far from the route (the other end of the frame); no turquoise on the green route; no blue, white or black on the turquoise one. Fewer proposals rather than more. |
| C2 | Any route. | At most 15 proposals. |
| C3 | Look at the proposals. | Drawn solid in the colour the route will get (as after «Принять»), well visible; the selected one with yellow dashes over it. |
| C4 | The Inspector during the review. | No list; «Предложено: N», «Отклонить все», «Принять», «Отменить» (amendment 2: no «Допуск цвета»). |
| C5 | Delete a proposal in the middle of a long route. | Only it disappears; the holds beyond it stay. |

## D. Amendment 2 (the owner's check of 1.5.1)

| # | Steps | Expected |
|---|---|---|
| D1 | First start of 1.5.2 (your settings of 1.5.0 / 1.5.1 kept). | «Предлагать трассу» unticked; the first click marks one hold by hand. |
| D2 | Tick it, mark a route, «Принять», «Готово». | The orange (the route's animation) starts at the frame you marked on and lasts 5 s; the green («Боулдер») starts 2 s before it. |
| D3 | The review in the Inspector. | No «Допуск цвета»; «Предложено: N», «Отклонить все», «Принять», «Отменить». |
| D4 | A route whose first hold has another hold right beside it (touching). | The route's other holds are proposed too — not only the two. |
