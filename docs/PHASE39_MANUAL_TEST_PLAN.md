# Phase 39 — Timeline UX & Multi-Clip Editing: manual test plan

Decision: DECISIONS.md **D058** (2026-11-10). Environment: the target build of Phase 39; a **copy** of a real project (the
originals are never saved); 1440 × 900 and a narrow window (1024 × 640). Legend: **PASS**, **FAIL**, **PARTIAL**,
**NOT RUN** (with a reason). «Automated» names the tests that cover the row.

## A. The timeline follows the playhead (D058 §1)

| # | Check | Expected result | Automated | Evidence |
|---|---|---|---|---|
| A1 | Play a long project from the start | when the playhead passes the right edge the view turns its page: the playhead at the left edge, playback goes on; no continuous scrolling | `TimelineFollowAndZoomTests` | |
| A2 | Pause | nothing scrolls by itself | `TimelineFollowAndZoomTests` | |
| A3 | While playing: scroll with the wheel / drag the scroll bar / Ctrl+wheel / drag a clip | the view stays where the user put it; the playhead may leave it | `TimelineFollowAndZoomTests` | |
| A4 | Pause, Play again | following again | `TimelineFollowAndZoomTests` | |
| A5 | While playing click the ruler | a seek; the following goes on | — | |
| A6 | «Настройки… → Таймлайн и разметка», turn «Таймлайн следует за плейхедом» off, play | no page turns; on again — page turns; the choice kept after a restart | `SettingsWindowTests` | |

## B. The initial zoom (D058 §2)

| # | Check | Expected result | Automated | Evidence |
|---|---|---|---|---|
| B1 | New project | about ten minutes across the timeline | `TimelineFollowAndZoomTests` | |
| B2 | Add one clip, then several (10–15 min) | the zoom does not change | `TimelineFollowAndZoomTests` | |
| B3 | Open an existing project never zoomed | ten minutes across | `TimelineFollowAndZoomTests` | |
| B4 | Zoom, save, reopen | the user's zoom comes back | `TimelineFollowAndZoomTests` | |
| B5 | The narrow window | still about ten minutes; zoom in / out, Ctrl+wheel, «Вписать» work | `TimelineFollowAndZoomTests` | |

## C. Several clips in the Inspector (D058 §3)

| # | Check | Expected result | Automated | Evidence |
|---|---|---|---|---|
| C1 | Select 10 video clips | «ВЫБРАНО КЛИПОВ: 10»; only the five groups that fit | `ClipGroupEditingTests`, `ClipGroupViewTests` | |
| C2 | «Фон: Размытый» | every clip blurred; one Ctrl+Z restores all; Ctrl+Y again | `ClipGroupEditingTests` | |
| C3 | «Заполнить» / «Вписать» | every picture filled / fitted; one Ctrl+Z | `ClipGroupEditingTests` | |
| C4 | Volume 50 %, «Без звука» (clips with linked sound) | every clip's sound changed once (a pair counted once) | `ClipGroupEditingTests` | |
| C5 | Opacity with differing values | the field shows «—»; a typed value sets all | `ClipGroupEditingTests` | |
| C6 | The hold outline: width, colour, keyline on clips with holds (and one without) | every marked route changes; the clip without holds untouched | `ClipGroupEditingTests` | |
| C7 | A video clip and a text clip together | only Opacity offered | `ClipGroupEditingTests` | |
| C8 | One clip again | its own Inspector as before (every section) | `ClipGroupEditingTests`, the Inspector tests | |

## D. Marker labels (D058 §4)

| # | Check | Expected result | Automated | Evidence |
|---|---|---|---|---|
| D1 | Add a marker (◆+), double-click its flag, type, Enter | the label beside the flag; one Ctrl+Z removes it | `MarkerLabelTests` | |
| D2 | Double-click, type, Esc | the label unchanged | `MarkerLabelTests` | |
| D3 | Save, reopen | the labels come back | `MarkerLabelTests` | |
| D4 | Several labelled markers close together; click on the ruler near them | readable; the ruler still seeks | — | |

## E. Numeric parameters and «Готово» (D058 §5, §6)

| # | Check | Expected result | Automated | Evidence |
|---|---|---|---|---|
| E1 | Drag the labels «Позиция X / Y», «Масштаб», «Поворот» sideways; with Shift; with Ctrl | the value follows (1 px, 0.5 %, 0.5° per pixel; ×10; ×0.1); one Ctrl+Z per drag; the fields still take typed values | `ScrubLabelTests` | |
| E2 | Drag the Opacity / Volume sliders | 5 % steps; typed values exact | `ScrubLabelTests` | |
| E3 | «Разметить зацепки», look at the Inspector's «Зацепки», finish on the Preview bar | no second «Готово»; «Идёт разметка — завершите на панели Preview»; «Готово» on the Preview bar works as before | `InspectorFieldStyleTests`, `HoldMarkupUiTests` | |

## F. Regression

| # | Check | Expected result | Evidence |
|---|---|---|---|
| F1 | The main path on a copy: import → timeline → FIT → holds → templates → export | as before | |
| F2 | Scrubbing, frame stepping, drag and drop, the export dialog | as in Phase 38 | |
