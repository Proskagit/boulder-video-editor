# Phase 57 — codecs and export presets: the manual check (D076)

For the product owner. New: the codec (H.264 / HEVC / AV1) with NVENC or the processor, the file type (MP4 / MOV / MKV), four
built-in presets instead of the profiles («Быстрый», «Средний», «Высокий», «Максимальный»), your own saved presets, the
calibrated quality of the new encoders and the size estimate per codec. The automated part: every codec × encoder × container
encodes every frame in order with the sound sample-exact and the BT.709 tags (on this machine's GPU and on the new minimal
FFmpeg); an old project's settings read as exactly its old output; the presets' fields, ceiling and matching; saved presets in
the global settings. What needs **your eyes**: the window, the pictures and the files on your devices. Use **copies** of your
projects.

Mark each row PASS / FAIL / note.

## The window

| # | Steps | Expected |
|---|---|---|
| 1 | A **new** project with a 4K clip → «Экспорт». | «Пресет: Максимальный»; the line under the name says the size, «MP4 (H.264 / AAC)»; the expected size is shown. |
| 2 | Open «Подробные настройки». | Every field of the preset is shown: Формат файла, Кодек, Кодировщик, Разрешение, Управление битрейтом, Качество, Скорость кодирования, Звук; below them the parameters («NVENC p7 CQ 19»), the speed note and where the codec plays. The window scrolls; the buttons stay in view. |
| 3 | Choose «Быстрый», «Средний», «Высокий» in turn. | The fields change to each preset (Full HD H.264 / 2K AV1 / 4K H.264 …); the size line and the estimate follow. |
| 4 | With a preset chosen, change any field (e.g. «Звук» 256). | «Пресет» becomes «Пользовательский»; set it back — the preset is found again. |
| 5 | A **Full HD** project → «Высокий» (4K). | «Разрешение: Как в проекте 1920 × 1080» — a preset never enlarges the picture. |
| 6 | «Формат файла» MOV, then MKV. | The file name's extension follows (.mov / .mkv); «Обзор…» offers that type first. |
| 7 | «Кодек» AV1 with MOV chosen. | MOV disappears from «Формат файла», MP4 is chosen; the note says where AV1 plays. |
| 8 | «Кодировщик» on each codec. | «Видеокарта NVIDIA (NVENC)» and «Процессор (libx264 / libx265 / SVT-AV1)». |
| 9 | «＋ Сохранить пресет…». | A name field with a suggestion («H.264 · Как в проекте · MP4 · AAC 320»); type «Мой архив» → «Сохранить»; «Пресет» shows «Мой архив». |
| 10 | Another project → «Экспорт» → «Пресет». | «Мой архив» is in the list; choosing it sets every field (its file type too). ✕ next to it deletes it; the settings stay. |
| 11 | «Параметры проекта». | The same presets and fields (without the size estimate). |
| 12 | Open a project of 1.9 or older (copy) → «Экспорт». | «Пресет: Пользовательский» with exactly its old settings (e.g. H.264, libx264 medium CRF 18); its export is the same as before. Saved — a 1.9 build no longer opens it (format v29). |

## The files

| # | Steps | Expected |
|---|---|---|
| 13 | Export a route with «Максимальный» (H.264 NVENC). | As fast and as large as «Высокое качество» was (the same encoding, AAC 320); the picture as before. |
| 14 | The same with «Средний» (2K AV1 NVENC) and «Быстрый» (Full HD H.264). | 2560 × 1440 / 1920 × 1080; good picture; «Средний» clearly smaller than the same 2K export in H.264 (≈ 30 %). |
| 15 | HEVC NVENC (custom) and AV1 / HEVC on the processor («Кодировщик: Процессор»). | They export (the processor much slower — the note says so); the pictures as good as the NVENC ones. |
| 16 | A .mov and a .mkv export. | Both open in VLC / your editor with sound in sync. |
| 17 | Play the HEVC file in Windows' «Медиаплеер», on your phone, in Telegram; upload to YouTube. | Plays (Windows' player may ask for «Расширения для видео HEVC» — the note in the window says so). |
| 18 | Play the AV1 file the same way. | YouTube / browser / a recent phone play it; an old device may not (said in the window). |
| 19 | Compare the estimate with the real file size (rows 13–15). | The real size inside the estimated range for ordinary footage. |
| 20 | On a computer without an NVIDIA card (if you have one). | «NVENC — недоступна»; the processor encodes the same codec; the reason is shown. |
