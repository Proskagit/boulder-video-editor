# Phase 24 — manual test plan

Manual checks of the Russian localization and the interface polish (DECISIONS.md D039). Run in the real app on Windows
with the default `appsettings.json` (`App:Language` = `ru`), at two window sizes: **1024 × 640** (the window's minimum) and
**1440 × 900** (the default). Fixture: any project with a video with sound, an image, a text clip, a hold markup and an
offline file (a renamed media file).

Status column — kinds of evidence, never mixed:
- **auto** — covered by automated tests only, not checked in the real app;
- **local (Claude)** / **PO** — the real-app run on Windows / the product owner's check.

| # | Scenario | Expected | Automated coverage | Status |
|---|---|---|---|---|
| 1 | Start the app | Window title «… — AI Video Editor», status «Готово.», every panel heading and button in Russian | guards (AXAML, resources) | |
| 2 | Main tool bar at 1024 and 1440 px | Every button readable, nothing cut off or outside the window; tooltips in Russian with shortcuts | AXAML guard | |
| 3 | Recent projects flyout | Heading, empty text, the state of a missing project, the ✕ tooltip in Russian | `RecentProjects*` | |
| 4 | Media: empty state, import, item rows, offline strip | Russian texts, kind (ВИДЕО / АУДИО / ИЗОБРАЖЕНИЕ), sizes in КБ / МБ, «Перепривязать…», «Найти отсутствующие…» fit in 260 px | Media tests | |
| 5 | Preview bar and transport at 1024 px | Таймлайн / Источник, Переместить / Кадрировать with icons, transport icons; Play / Pause keeps its width | | |
| 6 | Hold markup | Bar texts and buttons in Russian, statuses during preparation, confirmation, errors | `HoldMarkupUiTests` | |
| 7 | Timeline tool bar at 1024 and 1440 px | Russian labels, at most two rows at 1024; icons on Разрезать / Удалить / Привязка; tooltips | | |
| 8 | Track headers and markers | Mute / hide / lock tooltips, hide and lock icons, marker buttons' tooltips | | |
| 9 | Edit refusals | A locked track, a move before 0, a paste after a frame-rate change — Russian messages in the status bar | Russian flow tests | |
| 10 | Inspector, every section | Labels not cut off (wrapping where long), units in Russian, drop-downs without English (alignment, colour mode, order) | Inspector tests | |
| 11 | Opacity and Volume sliders | The slider and the field agree; a drag is one undo step; typing an exact value still works | slider tests | |
| 12 | Project settings dialog | Russian headings, labels and buttons, nothing cut off | `ProjectSettings*` | |
| 13 | Export: preflight error / warnings, progress, success, failure, replace file | Russian dialogs; FFmpeg detail kept in parentheses where shown | `ExportWorkflowTests` | |
| 14 | Unsaved changes, recovery, open a damaged project | Russian dialogs and statuses | workflow tests | |
| 15 | Relink one file and a batch | Russian dialogs and summary | relink tests | |
| 16 | A long status message at 1024 px | Trimmed with «…», the whole message in the tooltip | | |
| 17 | Shortcuts in the Russian keyboard layout | Ctrl+S / Z / Y / I / E, S, N, I / O, J / K / L, Space, Delete, Ctrl+A work as in the English layout | `ShortcutRoutingTests` | |
| 18 | Numbers | Decimal comma in the fields (speed 1,25), typed values accepted | `NumericInputTests` | |
| 19 | New text clip | «Текст»; existing texts and track names unchanged | text tests | |
| 20 | A Phase 23 project | Opens and saves byte for byte (v6) | format tests | |
| 21 | `App:Language` = `en` | The interface in English as before | resource tests | |
