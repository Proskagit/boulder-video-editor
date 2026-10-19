# Phase 26 — manual test plan

Manual checks of the vertical video and framing (DECISIONS.md D041). Run in the real app on Windows with the default
`appsettings.json` (`App:Language` = `ru`) unless a row says otherwise, at **1024 × 640** (the window's minimum) and
**1440 × 900** (the default), in Avalonia's logical pixels. Media: the product owner's phone clips in `VideosForTest`
(Vivo X300 Pro, Huawei P60 Pro) — portrait and landscape, with and without rotation metadata — and generated fixtures where
a case needs a property the clips lack (a rotation tag, an odd size, an unknown orientation).

Status column — kinds of evidence, never mixed:
- **auto** — covered by automated tests only, not checked in the real app;
- **local (Claude)** / **PO** — the real-app run on Windows / the product owner's check;
- **NOT RUN** — not checked in the real app (the reason given).

Run: not yet (skeleton, Step 26.0).

| # | Scenario | Expected | Automated coverage | Status |
|---|---|---|---|---|
| 1 | New project; a portrait phone clip (coded portrait, rotation 0) added to the empty timeline | The dialog offers 1080 × 1920 instead of 1920 × 1080; «Сменить холст» → the canvas 1080 × 1920, the picture fills it | 26.1, 26.2 | NOT RUN (pending) |
| 2 | As 1 with a clip coded landscape with a 90° / 270° rotation tag | The same offer (the display size after the rotation) | 26.1, 26.2 | NOT RUN (pending) |
| 3 | A clip with an unknown orientation (odd angle or mirrored matrix) | No offer; the canvas unchanged | 26.2 | NOT RUN (pending) |
| 4 | A landscape 16 : 9 clip (3840 × 2160 or 1920 × 1080) first | No offer (the offered canvas equals the current one) | 26.1, 26.2 | NOT RUN (pending) |
| 5 | «Оставить» / closing the dialog; then a second clip added | Nothing changes; no second offer | 26.2 | NOT RUN (pending) |
| 6 | After «Сменить холст»: Undo, Undo, Redo, Redo | First Undo restores 1920 × 1080 with the clip in place, the second removes the clip; Redo the reverse | 26.2 | NOT RUN (pending) |
| 7 | A canvas changed in Project Settings first, then a portrait clip | No offer | 26.2 | NOT RUN (pending) |
| 8 | Insert from the Source viewer into an empty project; a sound-only Insert | The offer for the picture; none for the sound only | 26.2 | NOT RUN (pending) |
| 9 | «Заполнить кадр» in the Inspector: a landscape clip on a portrait canvas, a portrait clip on a landscape canvas | No bars in the Preview; Масштаб shows the computed value; one Undo restores it | 26.1, 26.3 | NOT RUN (pending) |
| 10 | «Вписать в кадр» after a Fill or a typed Масштаб | The whole picture visible (Масштаб 100 % without rotation); one Undo | 26.1, 26.3 | NOT RUN (pending) |
| 11 | Fill / Fit from the clip's context menu on the timeline (right click) | The same as 9–10 for the clicked clip | 26.3 | NOT RUN (pending) |
| 12 | Fill with a crop, with rotation 90°, with a position offset | Crop and rotation accounted; the offset kept (a strip may remain at the far edge — D041 §2) | 26.1, 26.3 | NOT RUN (pending) |
| 13 | Refusals: a text clip, an audio clip, a locked track, during an export | The commands unavailable or refused with a status; nothing changes | 26.3 | NOT RUN (pending) |
| 14 | Export of rows 1 and 9 watched in a video player | No black bars; the picture as in the Preview | 26.3 (parity) | NOT RUN (pending) |
| 15 | The dialog, the Inspector buttons and the context menu at 1024 × 640 and 1440 × 900 | Russian texts, nothing cut | 26.2, 26.3 (guards) | NOT RUN (pending) |
| 16 | A Phase 25 project (`58c48cb`) opened and saved without an edit, `ru` and `en` | Byte for byte the same file | 26.4 | NOT RUN (pending) |
