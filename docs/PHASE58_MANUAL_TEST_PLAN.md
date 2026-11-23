# Phase 58 — a faster export without the graphics card: the manual check (D077)

For the product owner. New: a clip drawn smaller than its source is decoded at the size it is drawn at (a 4K project exported
in Full HD / 2K, a vertical export of landscape footage, a picture in picture), and the decoded frame is copied in one block. The
export on the graphics card was researched and **not** made (`docs/EXPORT_PERFORMANCE.md` §9: at most ×1.7 at 4K — the card's
encoder itself is the limit).

The automated part: the decode size per clip (keyframes, crop, picture in picture, the exported range; full size for stills,
Follow and unknown sizes), the decoder asked for it, the real export against the decoder's own scaled frame and against the
full-size export (the same place, the black bars), the Preview ↔ export parity suites, the speed and the picture measured on
the 12×10 footage (`docs/EXPORT_PERFORMANCE.md` §10). What needs **your eyes**: the speed on your machine and the picture in your
player. Use **copies** of your projects.

Mark each row PASS / FAIL / note.

| # | What | How | Expected |
|---|---|---|---|
| 1 | «Быстрый» is faster | A copy of a 4K training project → «Экспорт» → «Быстрый» → note the time; the same project in 1.10.0 | About twice as fast (measured ×2.2 on 10 s of 4K60) |
| 2 | «Средний» is faster | The same with «Средний» (2K AV1) | About 1.5× faster (measured ×1.6) |
| 3 | A vertical export is faster | A 4K landscape clip in a 1080 × 1920 project (with and without the blurred background) → «Максимальный» | 2–3× faster than 1.10.0 |
| 4 | The picture of a smaller export | Open the files of rows 1–3 next to 1.10.0's at the same moments: the wall, the chalk, the holds, the text, the outlines | The same picture, the same places; the wall's texture rather cleaner (less shimmering grain) — nothing blurrier |
| 5 | Zoom and crop | A clip zoomed in by keyframes (e.g. 50 % → 150 %) and a cropped clip in a Full HD export | Sharp at the largest zoom, as in 1.10.0 |
| 6 | 4K «Максимальный» | A 4K project → «Максимальный» | A little faster (+5–7 %), the picture unchanged |
| 7 | The rest is as before | A project with a Follow camera, a picture in picture, transitions, templates, HDR footage → any preset | Exports as before; Preview and file look alike |
