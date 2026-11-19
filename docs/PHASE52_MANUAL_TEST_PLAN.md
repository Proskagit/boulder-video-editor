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

## D. Amendment 2 (the owner's check of 1.4.0) — the build 1.4.1

| # | Steps | Expected |
|---|---|---|
| D1 | The Preview's bar, right of the time. | A small button: an eye and «1080p»; a click opens the four qualities. |
| D2 | With proxies ready, switch «Минимум» → «Высокое» → «Оригинал» → «Среднее». | Each change at once; the status bar makes **no** proxy again. |
| D3 | New project with 4K / HEVC clips. | One proxy per clip («Прокси: …»), once; at «Среднее» / «Минимум» scrubbing and seeking clearly faster than at «Оригинал». |
| D4 | Add each kind of transition. | 1 s by default (shorter only where the clips are shorter). |
| D5 | Drag a transition's edge near the playhead / a marker / another clip's edge. | It snaps (the snap line shows). |
| D6 | Drag a transition's left edge to the right over the cut (and the right edge to the left). | It stops at the shortest length; never turns over to the other clip. |
| D7 | Drag a video clip onto a track where another clip's «Боулдер» stands. | The place is red; on release nothing moves; the plate stays. |
| D8 | Drag a clip from one track to another (free place). | A ghost of its future place on the target track (yellow if it would push clips, red if refused). |
| D9 | Move a clip with its «Боулдер» from a high track down (as in the owner's screenshot). | Its plate goes down to the lowest free track above it (beside the other plates); the tracks left empty by this move disappear; Ctrl+Z puts everything back. |

## E. Amendment 3 (the owner's check of 1.4.1) — the build 1.4.2

Sections A, B (proxies, «Создавать / Удалить прокси») and D2–D3 are withdrawn: no proxies any more.

| # | Steps | Expected |
|---|---|---|
| E1 | Import 4K / HEVC clips. | No «Прокси: …» in the status bar; nothing is made in the background. The old `cache\proxies` folder is gone after the start. |
| E2 | The quality button: «Минимум» / «Среднее» / «Высокое» / «Оригинал». | Applied at once; lower is lighter (smoother scrubbing on a weak machine). |
| E3 | Two tracks: V1 the clips, V2 the plates. Drag a clip from V1 into V2's free part. | It goes there; its «Боулдер» goes to a new V3 above it; Ctrl+Z undoes it in one step. |
| E4 | Two tracks: drag a clip from V1 up over the ruler (above the top track). | The bar «+ Новая дорожка» while dragging; on release a new track on top with the clip, its plate above it. |
| E5 | E4 where the clip's place is blocked (e.g. a locked track would be needed). | The bar red; nothing changes on release. |
