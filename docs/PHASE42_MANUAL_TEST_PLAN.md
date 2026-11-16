# Phase 42 — Follow and Occlusion: manual test plan (D061)

For the product owner's check on the target machine (Ryzen 7 7800X3D, RTX 4070 SUPER, Windows 11). Automated coverage is in
the PR; this plan checks what only eyes can judge. Material: the four vertical test videos
`D:\Времянка для монтажа\Куча сырья\VID_20251217_191039.mp4`, `VID_20251217_194717.mp4`, `VID_20251223_195009.mp4`,
`VID_20251223_195901.mp4` — **use them as they are; never edit, move or overwrite them**. Work in a new test project.

**Before:** the people models installed in `%LOCALAPPDATA%\AiVideoEditor\models\people-v1\` (docs/AI_MODEL.md, "People models");
a project with a 1080 × 1920 canvas.

Mark each row: ✅ passed / ❌ failed (what happened, the time code) / ⏭ not checked.

## A. Follow — the frame follows the climber

| # | Steps | Expected |
|---|---|---|
| A1 | Add `VID_20251217_194717.mp4`, select the clip, Inspector → «Слежение и люди» → «Следить за скалолазом» | A progress bar «Поиск людей… NN %» appears; the editor stays usable (play, scrub, edit other clips) during the analysis |
| A2 | Wait for the end | «Кадру некуда двигаться: увеличьте масштаб больше «Заполнить кадр»…» — the vertical video fills the vertical canvas, there is no room |
| A3 | Set Scale to 150 % | «Слежение: скалолаз виден в NN % клипа»; play: the climber stays near the centre, the camera moves smoothly, never a black border at any edge |
| A4 | Change «Плавность» to Мягкая, then Быстрая | Мягкая: calmer, the climber may drift from the centre on fast moves; Быстрая: follows closer; each change one Undo step |
| A5 | Undo / Redo the Follow switch | Off: the clip's own framing at once; on again: following without a new analysis (cached) |
| A6 | Close and reopen the project | Follow on, its smoothness kept; no new analysis (the progress does not come back) |
| A7 | Split the clip in the middle, Trim its start | Both halves follow; the cut has no jump of the camera; no new analysis |

## B. Follow — hard material

| # | Steps | Expected |
|---|---|---|
| B1 | `VID_20251223_195009.mp4`, Scale 150 %, Follow on | Several people (background) but one climber: followed without a pick, or «В клипе несколько человек: выберите скалолаза» |
| B2 | If asked: move the playhead to a frame where the climber is seen, «Выбрать скалолаза», click on the climber | The people's boxes with numbers appear; after the click «Скалолаз выбран…», the frame follows that person |
| B3 | Play 2.3–3.7 s (the climber partly out of the left / right edge) | The frame goes to the edge and stops there (no black border); the climber is not lost or not for long; no jump when the climber comes back |
| B4 | `VID_20251223_195901.mp4`, Scale 150 %, Follow on, pick the climber at ~0:10 | Following; the Inspector lists the lost stretches |
| B5 | Play 0:35–0:50 (close-up, less than half of the body) | The frame stays on the climber's visible part; smooth |
| B6 | Play 1:00–1:08 (a man crosses in front of the camera) | The camera **does not switch** to the man; while the climber is hidden «Скалолаз здесь не виден — кадр удерживается на месте»; then it continues smoothly |
| B7 | Play 0:12–0:16 and 1:11–1:14 (the operator's hand in the frame) | No switch to the hand |
| B8 | Pick a wrong person on purpose (a spectator), then pick the climber again | Follows the spectator, then the climber; each pick one Undo step |
| B9 | `VID_20251217_191039.mp4`, Scale 150 % | Follows the whole climb; at the end (the climber walks to the camera, wider than the frame) the frame holds a sensible position, no jitter |

## C. Occlusion — outlines hidden behind people

| # | Steps | Expected |
|---|---|---|
| C1 | On a clip with marked holds (mark a few holds the climber touches), «Скрывать контуры за людьми» on | «Сегментация людей… NN %», then «Люди сегментированы: контуры скрываются…»; the editor stays usable |
| C2 | Play where the climber's hand / foot / body is on a marked hold | The outline disappears exactly where the body is in front of the hold and stays where the hold is visible; the edge is soft, no halo, no flicker |
| C3 | «Показывать маски людей в Preview» on | Each person in its own colour; two people never one colour; the masks follow the people when the camera moves |
| C4 | `VID_20251223_195901.mp4` 1:00–1:08 with holds marked behind the passing man | The outlines are hidden behind the man too (every person, not only the climber) |
| C5 | Switch «Скрывать…» off | The outlines over everything again (exactly as before Phase 42) |
| C6 | On a computer / session without DirectML (if available) | «Для скрытия контуров за людьми нужна видеокарта с DirectML…»; the outlines drawn as before; Follow still works |

## D. Analysis control and the export

| # | Steps | Expected |
|---|---|---|
| D1 | Start Follow on a long clip, «Остановить анализ» | The analysis stops at once; «Анализ остановлен…»; «Повторить анализ» starts it again |
| D2 | Export while the analysis runs | The export does not start: «…люди в видео ещё анализируются — экспорт ждёт…» |
| D3 | Export after the analysis | The exported video follows the climber and hides the outlines exactly as the Preview (compare 3–4 moments side by side) |
| D4 | Export a clip with Follow but several people and no pick | A warning «слежение за скалолазом не работает (скалолаз не выбран)…»; «Экспортировать» works, the clip with its own framing |
| D5 | Rename the models folder, restart, switch Follow on | «Модели людей people-v1 не установлены в …» — nothing else breaks; restore the folder, «Повторить анализ» works |
| D6 | Replace a video file on disk by another (a copy, in a test folder only) and reopen the project | The video is analysed again (a new file) |
| D7 | An export of a project without Follow / Occlusion (Phase 40–41 check) | Exactly as before: speed, look, the encoder choice |

## F. After the manual check (D061 amendment 1)

The cache of the people analyses is analysed again once (masks v3). Use copies of the D053 project.

| # | Do | Expect |
|---|---|---|
| F1 | `video_20260927_101034` with «Скрывать контуры за людьми»; Settings → Таймлайн → «Показывать маски людей в Preview»; play 8–75 s | From about 10 s the mask covers the head, arms and legs during the whole climb (not the T-shirt only); no mask on the black volume at 57–74 s |
| F2 | The same on `video_20260927_112606` (Occlusion only) | As before: the whole climber; the outlines hidden behind him |
| F3 | A clip without Follow / Occlusion: switch Follow on | «Скрывать контуры за людьми» switches on too; one Ctrl+Z switches both off; Occlusion can be switched off with Follow on |
| F4 | Switch Occlusion on first, wait for «Контуры скрываются…», then switch Follow on | No new «Поиск людей» / «Сегментация людей»: Follow is ready at once |
| F5 | Switch Follow on and, while «Поиск людей … %» runs, switch Occlusion on | The search continues (not restarted), then the segmentation runs once |
| F6 | Put the playhead in the middle of a clip, «Следить за скалолазом отсюда» | Playback starts; from that frame the camera glides to the climber and follows; before it the clip's own framing; a cyan strip from that frame to the clip's end |
| F7 | A few seconds later «Остановить слежение здесь» | Playback pauses; the strip ends at that frame; after it the camera glides back to the clip's own framing — no jump at either edge |
| F8 | Drag the strip's start / end edge left / right on the timeline; Ctrl+Z / Ctrl+Y | The part follows; one undo step per drag; dragged to the clip's edge, the side is open again; «На весь клип» in the Inspector restores the whole clip |
| F9 | Split the clip inside the part; trim the clip | Each half follows only its piece of the part; a half outside the part shows «Участок слежения лежит вне этого клипа» and no warning in the export |
| F10 | Save, close, reopen the project; export | The part, Follow and Occlusion are as before; the export looks like the Preview |
| F11 | Open a project saved by the previous build (v20) | Opens unchanged (Follow on the whole clip); saved as v22 |

## G. Second check (D061 amendment 1, second round)

| # | Do | Expect |
|---|---|---|
| G1 | A long clip (`video_20260927_101034`, 98 s): playhead at 10 s, «Следить за скалолазом отсюда» | Playback starts; within about 2 s the camera follows the climber and keeps following while it plays — no wait for the whole file |
| G2 | «Остановить слежение здесь» at about 24 s; watch the Inspector's / Preview's progress | The tracks stop at the part (no analysis of the rest of the file); the masks run only where the outlines are shown and finish in well under a minute |
| G3 | The same clip: playhead at 50 s (after the first part), «Следить отсюда», stop at 60 s | A second cyan strip; the first part unchanged; each part follows its own climber (pick another one in the second part with «Выбрать скалолаза» on a frame of it) |
| G4 | Playhead inside a part, «Следить отсюда» | «Указатель уже на участке слежения…»; nothing changes |
| G5 | Drag the first part's end right onto the second part | It stops at the second part's start; where the two meet the camera passes from one climber to the other without going back |
| G6 | Playhead in a part, «Удалить участок»; Ctrl+Z | The part goes / comes back; the other stays |
| G7 | Save, reopen (also a project saved by 0.42.1) | The parts and their climbers as before; a 0.42.1 project opens with its one part |

## H. Third round (D061 amendment 1 §10–§12)

| # | Do | Expect |
|---|---|---|
| H1 | A clip with Occlusion, a new part (or after clearing the cache): note the masks' time against the previous build | About 15 % faster; the masks look the same |
| H2 | Pause on a clip (no tool on), click its picture in the Preview and drag | The frame shows, the clip moves with the pointer; the «Переместить» button is on; Esc turns it off |
| H3 | Ctrl + the wheel over the picture; Ctrl+Z | The clip scales around its centre; one Ctrl+Z undoes the whole turn of the wheel |
| H4 | Pause on a frame of a followed part (the camera follows), click and drag the picture right | The bar says «Слежение — тяните кадр…»; the climber moves right in the frame and stays there while it plays; the Inspector's «Скалолаз в кадре» X grows; one Ctrl+Z undoes the drag |
| H5 | Pause between two parts (or on a clip without Follow) and drag | The clip's Position changes, not the climber's place |
| H6 | Type an exact X / Y in «Скалолаз в кадре»; save, reopen | The climber stands there; kept after reopening (`project.json` v23) |
| H7 | During the hold markup or «Выбрать скалолаза», click the picture | The markup / the pick get the click (Move does not turn on) |

## E. Notes for the owner

- Follow without zoom needs room: the vertical videos on the vertical canvas need Scale above «Заполнить кадр» (D061 §7).
- The masks take about 0.3–0.6 of the clip's duration with one or two people (longer with more); once per file, cached.
- While Follow moves the picture, Position X / Y and the Move tool do not move it on those axes (D061 §7).
