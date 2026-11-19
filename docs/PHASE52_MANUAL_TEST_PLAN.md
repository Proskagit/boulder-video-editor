# Phase 52 — proxies for 4K / HEVC and the Preview's quality: the manual check (D071)

For the product owner. The automated part ran on the build machine: the proxy keeps every frame and its exact timestamp
(CFR, NTSC, HEVC, VFR, MPEG-TS, after seeks, HDR → SDR), the cache, the queue, the Preview's decoding, the drop of a sound.
What needs **real 4K footage and eyes** is here. Use **copies** of your clips (e.g. a 4K HEVC from the phone, a 4K from the
camera, an HDR one if you have it, one ordinary 1080p) in a new test project.

Mark each row PASS / FAIL / note.

## A. Proxies and the quality

| # | Steps | Expected |
|---|---|---|
| A1 | New project, import the 4K / HEVC clips and the 1080p one. | For each 4K / HEVC clip the status bar shows «Прокси: <файл> — N %» with a bar and ✕, one file after another; nothing for the 1080p H.264. The editor stays usable meanwhile. |
| A2 | The Preview's bar, right of the time. | A list «Среднее (1080p)» (the default); the other choices «Оригинал», «Высокое (1440p)», «Минимум (720p)». |
| A3 | Put a 4K clip on the timeline before its proxy is ready, play, scrub. | It plays from the original as in 1.3 (heavier). When its proxy is ready the picture stays the same frame and playback / scrubbing become noticeably smoother. |
| A4 | With the proxy: play, scrub fast, step frame by frame (← / →), stop on a hold. | Smooth; the frames are the same as in the export (compare one paused frame with the exported video at the same time code). |
| A5 | Pick «Минимум (720p)». | The picture a bit softer; the status bar makes the 720p proxies (once per file); then as smooth as A4 or smoother. |
| A6 | Pick «Оригинал». | The original at full size — the sharpest and the heaviest (full 4K). No proxies made. |
| A7 | Back to «Среднее». | The 1080p proxies of A1 are used again at once (not made again). |
| A8 | A portrait (vertical) 4K clip at «Минимум». | The Preview sharper than 1.3 at the same choice (720 × 1280 now, not 405 × 720). |
| A9 | An HDR clip (HLG from the phone). | Its proxy's colours as the original's in the Preview (no washed-out or too-saturated picture). |
| A10 | Export the project at «Среднее» with proxies in use. | The export is made from the originals at full quality (look at the file in a player); its speed as before. |
| A11 | Holds, Follow / Occlusion, auto enhance on a 4K clip with a proxy. | Work as before (they use the original); the outlines sit on the holds in the Preview. |
| A12 | Close the editor while a proxy is being made; start it again, open the project. | It closes without a question and quickly; the proxy is made again from the start (no broken file); the ready ones are used at once. |
| A13 | The same clip in a second project. | Its proxy is used at once (one cache for every project). |
| A14 | ✕ in the status bar while a proxy is made. | That proxy stops; the clip stays on the original; the next file's proxy starts. |

## B. Settings

| # | Steps | Expected |
|---|---|---|
| B1 | «Настройки…» → «Таймлайн и разметка». | «Создавать прокси для видео 4K и HEVC (плавный Preview)» ticked; a button «Удалить прокси (N ГБ)» with the size; the note. |
| B2 | Untick, import another 4K clip. | No proxy made; the clip plays from the original. Clips with a proxy keep using it. |
| B3 | «Удалить прокси». | «Удалено, освобождено N ГБ»; with the box ticked the current project's proxies are made again in the background. |

## C. The owner's remarks (amendment 1)

| # | Steps | Expected |
|---|---|---|
| C1 | Drag a music file from the Media Browser onto an empty audio track, at 0:20. | It lands at 0:20 (snapped to a nearby edge or the playhead if close), not at the start. |
| C2 | The same onto an audio track that has clips. | Under the pointer, snapped to the clips' edges as before. |
| C3 | Drag a video onto V1 with the magnet on, and onto an empty V2. | V1 closes up as before (D069); on V2 it lands where it is dropped. |
| C4 | The Inspector: select different clips (video, text, template, a clip with people), scroll, open / close sections. | No empty space between sections. If it appears: note what was done just before and whether scrolling or opening a section removes it. |
