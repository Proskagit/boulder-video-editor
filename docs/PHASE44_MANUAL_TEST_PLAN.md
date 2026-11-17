# Phase 44 — Transform keyframes & canvas snapping: manual test plan (D063)

For the product owner's check on the target machine. Automated coverage is in the PR (Core, Timeline, Project, Rendering, UI);
this plan checks what only eyes and hands can judge. Material: the vertical test videos of Phase 42
(`D:\Времянка для монтажа\Куча сырья\VID_20251217_194717.mp4` and the others) and a landscape video — **use them as they are;
never edit, move or overwrite them**. Work in a new test project (a copy of a real project for §F).

Mark each row: ✅ passed / ❌ failed (what happened, the time code) / ⏭ not checked.

## A. Keyframes of a clip

| # | Steps | Expected |
|---|---|---|
| A1 | Add a video, select it, playhead at 0:00:01, press «Ключ. кадр» on the timeline's tool bar | An amber diamond appears on the clip at the playhead; the picture does not change; the Inspector's «Ключевые кадры» says «трансформация клипа · 1» and «◇ Удалить кадр» |
| A2 | Move the playhead 5 s later, change Position X to 500 in the Inspector | A second diamond appears there; play from 0:00:01: the picture moves smoothly to X 500 over exactly these 5 s, then stays |
| A3 | Drag the second diamond 2 s to the left | It follows the pointer frame by frame; release: the move now lasts 3 s; one Undo puts it back |
| A4 | Click the first diamond | It turns white (selected), the playhead jumps onto it; «К следующему» shows its easing |
| A5 | Choose «Плавный старт», then «Плавная остановка», «Плавно с обеих сторон» | Play each: the motion starts slowly / ends slowly / both; the end positions do not change |
| A6 | With the first diamond selected press Delete | The keyframe is removed — the clip stays; Undo brings the keyframe back |
| A7 | Snap on, drag the playhead near a diamond | The playhead jumps onto the diamond's frame |
| A8 | ◀ / ▶ in the Inspector's «Ключевые кадры» | The playhead goes to the previous / next diamond |
| A9 | In the Preview drag the picture (Move) between two keyframes, release | A new diamond appears at the playhead; one Undo removes the drag |
| A10 | Ctrl + wheel on the picture on a diamond's frame | The scale of that keyframe changes (the diamond's tooltip shows it); other frames follow the curve |
| A11 | Remove every diamond with «◇ Удалить кадр» | The picture keeps the last keyframe's position and scale |

## B. Keyframes of a Follow part

| # | Steps | Expected |
|---|---|---|
| B1 | A vertical video at Scale 150 %, Follow on a part in the middle (the cyan strip), the playhead inside the part, «Ключ. кадр» | A cyan diamond on the cyan strip; the Inspector says «кадрирование слежения, участок 1 · 1» |
| B2 | 4 s later inside the part: drag the picture in the Preview (the climber's place), Ctrl + wheel to 200 % | A second cyan diamond; play: the frame keeps following the climber while the climber's place and the zoom change smoothly between the two diamonds |
| B3 | Play across the part's start and end | No jump of position or scale: the frame glides from the clip's own framing into Follow and back (about 0.75 s) |
| B4 | Give the clip itself keyframes outside the part (A1–A2) and play across the part | Before / after the part the clip's own animation; inside the part Follow's framing; neither changes the other |
| B5 | During the zoom in B2 look at every edge of the frame | Never a black border |

## C. Clip edits

| # | Steps | Expected |
|---|---|---|
| C1 | Split a keyframed clip between two diamonds | Both parts keep their diamonds; play across the cut: no jump |
| C2 | Trim the start past the first diamond, then trim it back | The diamond disappears with the trimmed part and comes back with it |
| C3 | Move the clip on the timeline, change its Speed to 2× | The diamonds stay on the same pictures (with 2× twice as close together) |
| C4 | Duplicate / Copy-Paste the clip | The copy has the same diamonds; editing one does not change the other |
| C5 | Drag a Follow part's edge | Its cyan diamonds stay on their frames (those outside the part are hidden, not lost) |
| C6 | Save, close, reopen the project | Every diamond, value and easing is back; Undo / Redo before the save work as before |
| C7 | Open an older project (made before Phase 44) | It opens unchanged; saved again it opens in this build only (v24) |

## D. Canvas snapping

| # | Steps | Expected |
|---|---|---|
| D1 | A clip at Scale 110 %, Move: drag the picture so its left edge comes near the canvas's left edge | Within about 8 screen pixels it jumps onto the edge, a magenta line marks the edge, the bar says «прилипло к краю» |
| D2 | Keep dragging slowly past it | The picture stays stuck while within the threshold, then leaves it smoothly — no jitter |
| D3 | The same at the right, top and bottom edges | The same |
| D4 | Corner-scale the picture down to just under the canvas size and back | Near the canvas size it snaps so the edges coincide exactly |
| D4a | (amendment 1) A small picture moved into the bottom-left corner (it snaps); drag its top-right corner handle up and right | It grows towards the top right; the bottom-left corner stays exactly in the canvas corner; at the top-right canvas corner it snaps and fills the canvas — no black line |
| D4b | The same from each other corner (bottom right → top left, top left → bottom right, top right → bottom left) | It always grows towards the dragged corner, the opposite one held |
| D5 | Zoom the Preview window large and small, repeat D1 | The snap distance feels the same on the screen |
| D6 | «Настройки» → «Таймлайн и разметка» → «Прилипание к краям кадра»: 20, then 0 | 20: from farther away; 0: no snapping |
| D7 | Export a clip snapped to the edges (D1–D4) and look at the edges of the file in a player at 100 % | No thin black line at any edge |

## E. The route plate and the heart rate

| # | Steps | Expected |
|---|---|---|
| E1 | Landscape canvas: a «Боулдер» area with a route and TOP, the heart rate shown | The plate as before Phase 44; the heart rate about 1.5 times smaller than before (amendment 1) |
| E2 | Inspector → «Угол плашки»: each of the four corners | The plate (with its TOP / ПОПЫТКА block) stands in that corner inside the canvas, the text and the plate never past the edges; in a right corner the route block is at the edge and the status block on the centre's side |
| E3 | Put the heart rate in the plate's corner, for each of the four corners | The heart rate stands beside the plate on the centre's side (above it in a bottom corner, below in a top one), never over the plate or its text |
| E4 | Heart rate and plate in different corners | The plate stays in its place; the heart rate stands on the plate's line (plate bottom left, heart rate bottom right: one line) |
| E5 | Vertical canvas (1080 × 1920): E1–E4 again | The plate is clearly larger than before, the heart rate a little smaller; the plates stand about one plate's height lower than in 0.44.0 (amendment 1); they look like one composition |
| E6 | Export a short piece with E3 and E5 | The file shows exactly what the Preview showed |

## F. A real project

| # | Steps | Expected |
|---|---|---|
| F1 | A copy of a real D053 project: add keyframes to a climb with Follow, export it | Preview and export agree; the climb looks as intended |
