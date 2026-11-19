# Phase 53 — the route from its first hold: the manual check (D072)

For the product owner. The automated part ran on the build machine with a stand-in model on synthetic walls: the first click
opens the analysis, only Esc acts meanwhile, the route's holds are proposed on the markup frame, the review (remove, add by a
click + Enter, the tolerance without the model), «Принять» = one Undo step, «Готово» keeps what is shown, «Отменить» / «Отмена»
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
| A1 | Select a clip, pause on a frame where the route's wall is visible, «Разметить зацепки». | In the Inspector the check box «Предлагать трассу» is ticked (the route has no holds yet). |
| A2 | Click one hold of the route. | A window «Подождите, идёт анализ» with a bar and «Отмена» covers the editor; nothing else reacts (Space, Ctrl+Z, clicks). It closes after a few seconds (aim: ≤ 20 s). |
| A3 | Look at the frame. | The route's holds found are dashed proposals, the clicked one among them; the list «Предложения» in the Inspector; «Допуск цвета», «Принять», «Отменить»; the status «Предложено зацепок трассы: N». The playhead has not moved. |
| A4 | Click a wrong proposal, press Delete (or «Отклонить» in the list). | It disappears and does not come back when the tolerance changes. |
| A5 | Click a missed hold, check its outline (wheel / Tab for another), Enter. | It joins the proposals (not yet the clip). |
| A6 | Change «Допуск цвета» up and down. | More / fewer proposals at once (no waiting); the rejected ones stay out. |
| A7 | «Принять». | All proposals become holds in the route's colour; the status «Добавлено зацепок: N»; the markup goes on (the next click marks one hold by hand). One Ctrl+Z removes them all, Ctrl+Y returns them. |
| A8 | «Готово». | The intro and the final are laid out as before. |
| A9 | Again on a new route: first click → review → «Готово» directly. | The proposals shown are kept as holds, then the intro and final. |
| A10 | Again: first click → review → «Отменить». | The proposals vanish, nothing added; the next click marks one hold by hand. |
| A11 | Again: first click → «Отмена» (or Esc) in the window. | The window closes, nothing changed; the next click marks one hold by hand. |
| A12 | First click on the bare wall (beside any hold). | The status says no hold was found there; nothing proposed; the next click marks one hold by hand. |
| A13 | Esc during the review. | A candidate / selection is discarded first; otherwise the review stays as it is. Play or a seek leave the markup and drop the proposals (the project unchanged). |
| A14 | Re-enter «Разметить зацепки» on the route that now has holds. | The check box is hidden; the first click marks one hold by hand (no analysis). |
| A15 | Untick «Предлагать трассу» (Inspector or «Настройки…»), restart the editor. | Still unticked; the first click on a new route marks one hold by hand. |
| A16 | A wall with large volumes / panels of the route's colour; a dull (grey / black / white) route; a route next to another of a similar colour. | Note found / wrong per route. Expected limits (D072 §4): about 60 % found, a few wrong per frame; a click on a hold bolted onto a volume may take the volume's colour (research error B). |
| A17 | A 4K clip (the markup frame is full size). | The analysis may take longer; the holds lie exactly on the holds after «Принять» and follow the camera as manual ones do. |

## B. What is gone

| # | Steps | Expected |
|---|---|---|
| B1 | The Inspector's «Зацепки» outside the markup. | No hidden «Авторазметка» button, no «Показать N» list, no «Принять все» — only «Отклонить все» while a route is reviewed. |
| B2 | «Настройки…» → the hold settings. | «Предлагать трассу» with its note instead of «Предлагать зацепки автоматически». |
