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

## E. Notes for the owner

- Follow without zoom needs room: the vertical videos on the vertical canvas need Scale above «Заполнить кадр» (D061 §7).
- The masks take about 0.3–0.6 of the clip's duration with one or two people (longer with more); once per file, cached.
- While Follow moves the picture, Position X / Y and the Move tool do not move it on those axes (D061 §7).
