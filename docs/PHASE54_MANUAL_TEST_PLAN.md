# Phase 54 — the «Итоги» template: the manual check (D073)

For the product owner. The automated part ran on the build machine: the rows' data (which routes, the climb time `S − Ae` in
source time, the heart rate of the window, attempts grouped by number + grade, stated attempts, FLASH = 1, the totals), the
format v27 (save / open, an older file), the Preview and the export drawing the board byte for byte (landscape, portrait, scrolled,
English, own background), the Inspector's fields with Undo. What needs **your eyes and your footage** is here: the look, the
speed of the animation, and whether the numbers match what you see on the video. Use **copies** of your projects.

Mark each row PASS / FAIL / note.

## 0. Step 0

| # | Steps | Expected |
|---|---|---|
| 01 | Open «Справка» (F1) → «Шаблоны». | «Итоги» and FLASH are described. |

## A. Adding «Итоги»

| # | Steps | Expected |
|---|---|---|
| A1 | A project with a FIT and several routes (green / orange / lilac). Put the playhead after the last route, «Шаблон ▾ → Итоги». | A 10 s template at the playhead on the top video track; the timeline label «Итоги · Итоги дня». |
| A2 | Look at the Inspector. | «Заголовок» = «Итоги дня», «Дата» = the training's date, «Свой фон» off, «Надписи на кадре: Русский» (with a Russian interface), the hint about which routes are listed. |
| A3 | A project without a FIT → add «Итоги». | The date is empty; the heart-rate columns and cards are not drawn. |

## B. The board on the frame

| # | Steps | Expected |
|---|---|---|
| B1 | Play «Итоги» from its start. | The header slides in, the table panel unrolls under it, the rows come in one by one, the cards at the bottom after the last row; it fades out at the end. **Is the speed right?** (Note what feels too fast / slow.) |
| B2 | Compare the rows with the routes. | One row per route, in the order they started; number, grade, the route's colour on the left edge; TOP yellow, ПОПЫТКА blue, a route without status «—». |
| B3 | Check one route's time: on the timeline, the orange's end and the lilac's start. | «ВРЕМЯ» = lilac start − orange end (`m:ss`). A route without orange uses its start `T`; without a lilac — «—». |
| B4 | Check one route's heart rate: scrub the «Боулдер» plate's heart rate over the climb. | «ПУЛЬС СР» / «ПУЛЬС МАКС» match what the plate shows between the orange's end and the lilac's start. |
| B5 | The same route climbed in two clips (the same number and grade; one ПОПЫТКА, one TOP). | One row: TOP, «ПОПЫТКИ» 2, the time and the heart rate of the TOP attempt. |
| B6 | A route without a number. | Its own row with «—» in «ТРАССА». |
| B7 | More than 10 routes (or a copy with many). | At most 10 rows visible; each next one moves the list up, the first goes under the header. If not every row comes in before the end — lengthen the template, they come in. |
| B8 | The totals cards. | «ТОПЫ N / M» (N yellow), «ФЛЕШИ», «ПУЛЬС СР / МАКС», «НА ТРАССАХ» (the sum of every attempt's time, yellow outline). |
| B9 | Put a second «Итоги» later, after more routes. | The second lists only the routes after the first one. |
| B10 | ~~A vertical (9:16) project.~~ | Dropped (D073 amendment 1: no «Итоги» on vertical videos). |
| B11 | Split a route's video clip (S) after its orange, before its lilac. | «Итоги» still shows the route once, with the same time and heart rate as before the split. A copy (Ctrl+C / Ctrl+V) of a route's clip is another attempt. |
| B12 | The Inspector of «Итоги» and of a «Боулдер» in a 1024 px and a 1440 px wide window. | «Попыток», «Надписи на кадре», «Заголовок», «Дата», «Свой фон» readable, nothing cut off. |

## C. Own background, language

| # | Steps | Expected |
|---|---|---|
| C1 | Tick «Свой фон». | A full-frame navy card (a slanted band, three yellow stripes) — the video is not visible under it. Ctrl+Z returns the board over the video. |
| C2 | «Надписи на кадре: English». | The captions and cards in English («ROUTE, RESULT …», «bpm»); the header becomes «DAY SUMMARY» (if it was still the default). A header you typed yourself stays. |
| C3 | On a «Боулдер»: «Надписи на кадре: English». | The plate reads «ROUTE 7 · 6B», the status «TOP» / «ATTEMPT». |

## D. FLASH and attempts

| # | Steps | Expected |
|---|---|---|
| D1 | On a «Боулдер», status «Флеш». | In the lilac a **white** block «FLASH» with a yellow bolt, a yellow outline and a soft glow; a light sweep crosses it when it appears. **Clearly different from TOP?** «FLASH» stays English in a Russian project. |
| D2 | The same route in «Итоги». | The FLASH chip in «РЕЗУЛЬТАТ», «ПОПЫТКИ» 1; it counts in «ТОПЫ» and in «ФЛЕШИ». |
| D3 | On a «Боулдер», «Попыток» = 5 (the attempts not in the edit). | «Итоги»: «ПОПЫТКИ» 5 for that route. Empty the field — the count from the video again. 0 or a letter is not taken. |
| D4 | Export the project with «Итоги» and a FLASH. | The exported video shows the same board and the same FLASH block as the Preview. YouTube timestamps: the FLASH route ends with « ⚡»; «Итоги» is a line of its own at its start with its header («00:00 …» style, e.g. «12:40 Итоги дня»). |

## E. Files

| # | Steps | Expected |
|---|---|---|
| E1 | Save, close, open the project. | «Итоги», FLASH, the language, the attempts and «Свой фон» are kept. |
| E2 | Open a project saved by 1.5.x (copy). | It opens; its «Боулдер» plates are Russian as before; saving writes the new version (an older build then refuses the file — as with every format change). |
