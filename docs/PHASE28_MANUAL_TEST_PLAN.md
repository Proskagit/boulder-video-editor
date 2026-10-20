# Phase 28 — manual test plan

Manual checks of the transition library (DECISIONS.md D044). Run in the real app on Windows with the default
`appsettings.json` (`App:Language` = `ru`) unless a row says otherwise, at **1024 × 640** (the window's minimum) and
**1440 × 900** (the default), in Avalonia's logical pixels. Media: the product owner's real bouldering gym clips
(horizontal and vertical; 25 / 30 / 60 fps where available; some cut at their source's ends, for the source-handle rows)
and a Phase 27 v7 project with dissolves saved by the Phase 27 build (`3c45c9d`).

Status column — kinds of evidence, never mixed:
- **auto** — covered by automated tests only, not checked in the real app;
- **local (Claude)** / **PO** — the real-app run on Windows / the product owner's check;
- **harness** — renders outside the app (the painter's composition rule, a scratch tool or tests);
- **NOT RUN** — not checked in the real app (the reason given).

Skeleton written at Step 28.0; rows are completed per step and run at the closeout (Step 28.5). Nothing below is run yet.

| # | Scenario | Expected | Automated coverage | Status |
|---|---|---|---|---|
| 1 | The gate of Step 28.1: Zoom (`K = 1.25`, 0.3 s) and Flash (0.2 s) rendered on the product owner's gym clips, horizontal and vertical, 25 / 30 / 60 fps where available | The product owner judges the look; an unsatisfactory Zoom stops the work with Z2 / Z3 proposed (D044 §11) | 28.1 (harness) | harness + PO: PASS (2026-10-20) — four real cuts (two attempts of one route on a vertical canvas, the same wall and angle, a change of episode, preparation → a dynamic move) at 60 fps, MP4s and frame strips in `VideosForTest/Phase28_Step28.1` (local); Zoom and Flash accepted by the product owner as specified, Z2 / Z3 not needed. 25 / 30 fps: no such source in the session — not run; a 30 fps project check judged redundant by the product owner (rounding covered by 28.2's tests). A second camera on one attempt: none in the session (a change of episode used) |
| 2 | «Переход ▾» on two adjacent clips of a video track: each of the five types | Each added with its default (1.0 / 0.6 / 0.2 / 0.3 / 0.3 s), or the longest that fits with a status; one Undo step each (D044 §6, §8) | 28.2, 28.4 | — |
| 3 | A cut without source handles (clips cut at the source's ends) | Dip to black, Flash and Zoom added; Dissolve and Push not created / disabled, the message names Dip to black; no type replaced automatically (criterion 4) | 28.2, 28.4 | — |
| 4 | The Inspector: type change between all types (e.g. Dissolve 1.0 s → Flash, Flash 0.2 s → Zoom), Push's four directions | The duration kept when in range, else clamped to the nearest allowed value with a status; each change one Undo step (D044 §6) | 28.2, 28.4 | — |
| 5 | The duration field and slider at 24, 25, 30 and 60 fps: the defaults, the minimum, the maximum, typed values, the arrows | The stored seconds and frames as D044 §5's table; the 0.1 s step never sticks; nothing shown below the minimum or above the limit (criterion 3) | 28.2, 28.4 | — |
| 6 | Two Flash / Dip to black cuts closer than 1.0 s: add, type change, move, ripple trim, speed change, frame-rate change | Refused with a clear message; nothing changes (D044 §4, criterion 5) | 28.2 | — |
| 7 | Clips of different lengths, trim, move, split and speed near a zone; a locked track; linked clips (D032) | D025 §5 for every type; the locked track refuses; the linked sound stays a hard cut (criterion 6) | 28.2 | — |
| 8 | Preview playback and scrubbing through each type; a text above the zone; hold outlines in a Push and a Zoom; a vertical canvas | Smooth playback; the title above stays readable through Dip / Flash; the outline follows its frame (D044 §2) | 28.3 | — |
| 9 | Export of a cut-to-music sequence using the five types, watched in a player against the Preview | The same transitions as the Preview, frame for frame (criterion 1, 7) | 28.3 (parity), end to end | — |
| 10 | Undo / Redo of every action of rows 2–6; save, close, reopen | Every value restored; the file is `project.json` v8 with `direction` only for Push (D044 §9) | 28.2 | — |
| 11 | A Phase 27 v7 project with dissolves opened, saved and exported | Its dissolves look exactly as with the Phase 27 build; saved again only `formatVersion` differs (criterion 2) | 28.2, 28.3 | — |
| 12 | A v8 project opened by the Phase 27 build | Refused with the existing "saved by a newer version" message | 28.2 | — |
| 13 | The menu, the TRANSITION section and the zone icons at 1024 × 640 and 1440 × 900, `ru` and `en` | Nothing cut, no untranslated text | 28.4 (guards) | — |
| 14 | A running export | Every transition edit refused (`EditingLock`) | 28.4 | — |
