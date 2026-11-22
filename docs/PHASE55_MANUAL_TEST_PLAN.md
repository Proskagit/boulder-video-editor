# Phase 55 — editing comfort: the manual check (D074)

For the product owner. The automated part ran on the build machine: the trim with and without a selection (video and its linked
sound, one undo step), «Предлагать трассу» off after New / Open, the first hold switching «Скрывать за людьми» on in the same step,
the plate carrying its clip to «Люди» / «Зацепки», the export window's scroll, «Высокое качество» = NVENC CQ 19 / libx264 CRF 14
and the new project's NVENC «Высокое качество». What needs **your eyes** is here. Use **copies** of your projects.

Mark each row PASS / FAIL / note.

| # | Steps | Expected |
|---|---|---|
| 1 | Timeline bar: hover the two new buttons after «Разрезать». | «Обрезать слева / справа» with their tips (Q / W). |
| 2 | Nothing selected, playhead inside a video clip → «Обрезать слева». | The clip starts at the playhead (its sound too); on the main track the next clips close up. Ctrl+Z — back in one step. |
| 3 | Same with «Обрезать справа». | The clip ends at the playhead. |
| 4 | Select a clip (a text, a plate) → the buttons. | Only the selected clip is trimmed, as Q / W before. |
| 5 | Turn «Предлагать трассу» on, create a new project. Then turn it on again and open a project. | Off in both. |
| 6 | Open a project saved with the magnet off. | It stays off; nothing moves. A new project — on. |
| 7 | Export window: open «Подробные настройки» and «Таймкоды». | A scroll bar appears; everything reachable; «Экспорт» / «Отмена» always visible. |
| 8 | A new project → the export window. | «Высокое качество», NVENC; the plan reads NVENC p7 CQ 19. The expected size is larger than before. |
| 9 | An old project with «Высокое качество» → export. | NVENC CQ 19 (libx264 CRF 14 without NVIDIA) — the export log names it. |
| 10 | A clip with people, mark a route's first hold (or the route from its first hold). | «Скрывать контуры за людьми» is on by itself; the analysis runs; the outlines hide behind the climber. Ctrl+Z removes the hold and the switch together. |
| 11 | Switch it off, add more holds. | It stays off. |
| 12 | Select a green / lilac «Боулдер» anchored to a clip. | The Inspector shows «Слежение и люди» and «Зацепки» of that clip (the plate's route) with the plate's own fields. Changes go to the clip. |
| 13 | Select a free «Боулдер» (not anchored). | No «Люди» / «Зацепки». |
| 14 | An anchored «Боулдер» with Follow on its clip: move the playhead into a followed part. | «Удалить часть» is there and works (the clip's part goes). |
| 15 | The timeline bar in a 1024 px and a 1440 px wide window. | Every button visible, the two new ones too; nothing overlaps. |
| 16 | (If you have a PC without NVIDIA) a new project → export. | The window notes NVENC is unavailable; the export runs with libx264 «Высокое качество» (CRF 14). |

**Amendment 1 (1.7.1):**

| # | Steps | Expected |
|---|---|---|
| 17 | A route with TOP: trim its clip from the right into the lilac («Обрезать справа» / W). | The lilac ends at the new end; its start — the TOP moment — stays where you put it. Trimmed to its last frame — the status goes. |
| 18 | Drag the orange's end to the right (and its body). | The blue route start mark moves with the orange's end. Ctrl+Z — both back. |
| 19 | Select «Итоги»: «Попытки по трассам». | Each route with its result; the field shows the attempts (or «по видео: N»). Type 6 — the board shows 6 for that route; empty — the count from the video; a FLASH row is not editable. |
| 20 | Magnet on, «Итоги» after the last clip: delete the first clip (or trim one before «Итоги»). | «Итоги» moves left with the clips, keeping its place after the last one. |
| 21 | New project → export → «Подробные настройки» → «Пользовательский». | «Максимальное» (CRF 14), «Медленно»; from «Сбалансированный» — «Высокое» (CRF 18), «Средне». |
