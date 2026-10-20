# Phase 27 — manual test plan

Manual checks of the text animation and readability (DECISIONS.md D043). Run in the real app on Windows with the default
`appsettings.json` (`App:Language` = `ru`) unless a row says otherwise, at **1024 × 640** (the window's minimum) and
**1440 × 900** (the default), in Avalonia's logical pixels. Media: the product owner's portrait gym clip in `VideosForTest`
and a Phase 26 v6 project with text clips (with and without fades) saved by the Phase 26 build (`41dadf2`).

Status column — kinds of evidence, never mixed:
- **auto** — covered by automated tests only, not checked in the real app;
- **local (Claude)** / **PO** — the real-app run on Windows / the product owner's check;
- **NOT RUN** — not checked in the real app (the reason given).

Skeleton written at Step 27.0; rows are completed per step and run at the closeout (Step 27.6). Nothing below is run yet.

| # | Scenario | Expected | Automated coverage | Status |
|---|---|---|---|---|
| 1 | "+ Text" on a portrait gym clip | «Текст» white bold, bottom centre, a black 60 % background; appears «Выезд» снизу 0.4 s, disappears «Проявление» 0.3 s; readable on the wall without any setting (D043 §4, criterion 1) | 27.3–27.5 | — |
| 2 | Each «Появление» kind (Нет, Проявление, Выезд ×4 directions, Увеличение) and each «Исчезновение» kind, durations 0.1 and 3 s | The motion as described in D043 §1; each choice one Undo step; at most two clicks (criterion 2) | 27.3, 27.5 | — |
| 3 | «Обводка» on alone, «Подложка» on alone, both together; colours from the swatches; thickness 2 / 8 / 20 %, opacity 0 / 60 / 100 % | Background under the outline under the fill; each switched and set on its own; values kept when switched off and on (D043 §2) | 27.1, 27.4, 27.5 | — |
| 4 | The 3 × 3 grid, all nine cells, with and without the background; then a free move in the Preview | The box at 8 % of the canvas's short side from the edges; the alignment inside the box unchanged; the Preview's Move tool still moves the text (D043 §3) | 27.3, 27.5 | — |
| 5 | «Жирный», a colour swatch, the hex field, the size slider | One Undo step per choice; consecutive changes of one field merge (D043 §6) | 27.5 | — |
| 6 | Undo / Redo of every action of rows 1–5 | Every value restored exactly | 27.5 | — |
| 7 | Save, close, reopen | Every new value back; the file is `project.json` v7 | 27.2 | — |
| 8 | The Phase 26 v6 project with text opened (with and without fades, a fade longer than 3 s) | Its texts look exactly as in the Phase 26 build (Preview and export compared); fades shown as «Проявление» with their durations, a value outside 0.1–3 s as it is (criterion 3) | 27.2–27.4 | — |
| 9 | Export of rows 1–4 watched in a video player against the Preview | The same animation, outline and background as the Preview, frame for frame (criterion 4) | 27.4 (parity) | — |
| 10 | A v7 project opened by the Phase 26 build | Refused with the existing "saved by a newer version" message (criterion 6) | 27.2 | — |
| 11 | The Inspector's «Анимация», «Обводка», «Подложка», the grid at 1024 × 640 and 1440 × 900, `ru` and `en` | Nothing cut, no untranslated text (criteria 5, 7) | 27.5 (guards) | — |
| 12 | Locked track; a running export | The new controls refused / unavailable, nothing changes | 27.5 | — |
