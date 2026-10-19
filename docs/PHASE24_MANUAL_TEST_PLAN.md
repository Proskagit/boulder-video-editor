# Phase 24 — manual test plan

Manual checks of the Russian localization and the interface polish (DECISIONS.md D039). Run in the real app on Windows
with the default `appsettings.json` (`App:Language` = `ru`), at two window sizes: **1024 × 640** (the window's minimum) and
**1440 × 900** (the default), both in Avalonia's logical pixels (the display runs at 125 %: 1280 × 800 and 1800 × 1125
physical).

Run on 2026-10-19 by Claude on Windows 11 (OS UI culture ru-RU), the Debug build of the branch (Steps 24.7 / 24.8,
`932027a` and the fixes of `1e43e26` found in this run), driven by system mouse / keyboard input (`SendInput`-style events,
the window's keyboard layout switched with `WM_INPUTLANGCHANGEREQUEST`) and UI Automation; results read from screenshots of
the window and of the screen around it, the status bar, the saved `project.json` and the exported files. Fixtures:
`tools/manual/New-Phase23TransformFixture.ps1` (25 fps, 1920 × 1080; 4 clips 0–8 s, a phone clip and a text 10–18 s, a
rotated clip and an image 20–28 s, a clip on the locked V5); for the offline / relink rows a fresh copy of that fixture with
`q1.mp4` and `q2.mp4` moved to a subfolder. Not isolated: the app writes its logs, recovery files and the recent-projects list
under the real `%LOCALAPPDATA%\AiVideoEditor` (the recovery folder was empty again at the end of the run).

Found in this run and fixed (`1e43e26`): (1) at 1024 px the hold markup mode's buttons (Лупа / Подтвердить / Готово)
overlapped Переместить / Кадрировать in the Preview's bar — while the markup mode is on the two tools show only their icons
(names in the tooltips; the tools are off in that mode); the progress bar of «Подготовка кадра» ignored its width (Fluent's
minimum width 200) and covered the buttons — its minimum width is 0 now; (2) «Найти файлы…» was cut in the Media panel's
offline strip — the heading is above the buttons and the buttons wrap; (3) the status bar's tooltip is on the bar itself
(the whole bar is a hover target); (4) wording: «Найдено 1 из 1 офлайн-медиафайла» (the number agrees), «Автосохранённые
изменения … оставлены» (was «сохранены»).

Status column — kinds of evidence, never mixed:
- **auto** — covered by automated tests only, not checked in the real app;
- **local (Claude)** / **PO** — the real-app run on Windows / the product owner's check.

| # | Scenario | Expected | Automated coverage | Status |
|---|---|---|---|---|
| 1 | Start the app | Window title «… — AI Video Editor», status «Готово.», every panel heading and button in Russian | guards (AXAML, resources, source) | local (Claude): PASS — «Новый проект — AI Video Editor», «Готово.», МЕДИА / СВОЙСТВА / ТАЙМЛАЙН, all buttons Russian |
| 2 | Main tool bar at 1024 and 1440 px | Every button readable, nothing cut off or outside the window; tooltips in Russian with shortcuts | AXAML guard, `IconViewTests`, `ProjectSettingsViewBindingTests` | local (Claude): PASS — one row at both sizes (Undo / Redo as icons, «Параметры…»); the tooltip «Новый проект (Ctrl+N)» seen. Before Step 24.7 the Russian bar was wider than 1024 px and pushed the whole layout (the Inspector cut off) — fixed in 24.7 |
| 3 | Recent projects flyout | Heading, empty text, the state of a missing project, the ✕ tooltip in Russian | `RecentProjects*` | local (Claude): PASS for the heading «НЕДАВНИЕ ПРОЕКТЫ», five entries with names and folders, the ✕ column whole. The empty text and the «Недоступен» state: auto only (not reached in the run) |
| 4 | Media: empty state, rows, offline strip | Russian texts, kind (ВИДЕО / ИЗОБРАЖЕНИЕ), sizes in КБ / МБ, «Перепривязать…», «Найти файлы…» fit in 260 px | Media tests, `RussianTextsTests` | local (Claude): PASS — «Медиафайлы не импортированы» and its hint; rows «ВИДЕО MP4 · 85 КБ», «0:10 · 640×360 · 25 к/с», «ИЗОБРАЖЕНИЕ PNG · 89 КБ», «Файл офлайн»; the offline strip after fix (2) — nothing cut |
| 5 | Preview bar and transport at 1024 px | Таймлайн / Источник, Переместить / Кадрировать with icons, transport icons; Play / Pause keeps its width; Source bar | `IconViewTests` (tool bar) | local (Claude): PASS — the bar and the transport with the timecode whole at 1024 (the loop as an icon, D039 §7); the Source mode: «Вход» / «Выход» / «Вставить» / «Заменить», the range «Вход: 00:00:00:00 · Выход: конец» |
| 6 | Hold markup at 1024 px | Bar texts and buttons in Russian, statuses during preparation, the candidate | `HoldMarkupUiTests` | local (Claude): PASS after fix (1) — «Подготовка кадра» with the progress bar, «Отмена», «Готово»; then «Лупа», «Подтвердить», «Готово» and the two tool icons in one row; a click gave a candidate outline, Esc discarded it, «Готово» left the mode. The mode's instruction text is trimmed to «…» at 1024 px (its full help is in the tooltip; at 1024 px it was also hidden in English). The model's error statuses and «Контур N из M»: auto only |
| 7 | Timeline tool bar at 1024 and 1440 px | Russian labels, at most two rows; icons on Разрезать / Удалить / Привязка; tooltips | `IconViewTests` (glyphs) | local (Claude): PASS — two rows at both sizes (at 1440 only «Вместить» on the second), the three icons drawn |
| 8 | Track headers and markers | Mute / hide / lock tooltips, hide and lock icons, marker buttons' tooltips | AXAML guard | local (Claude): PASS for the icons (M, the eye, the lock; the lock amber on V5) and the lock / unlock of V5. The tooltips of the track and marker buttons were not hovered: auto only (AXAML guard + resources) |
| 9 | Edit refusals | Russian messages in the status bar | `RussianMessagesTests` | local (Claude): PASS — «Дорожка V5 заблокирована.» (+ Текст on the locked top track), «Перед этим клипом нет промежутка.» (Закрыть промежуток). A move before 0 and a paste after a frame-rate change: auto only |
| 10 | Inspector, every section | Labels not cut off (wrapping where long), units in Russian, drop-downs without English | Inspector tests, `RussianTextsTests` | local (Claude): PASS at 1024 for Трансформация («Непрозрачность» whole after the label column became 116), Кадрирование, Зацепки with the animation («Пауза после последней» wraps, «Снизу вверх», «Авто»), Появление и исчезание (the time below the field), Скорость, Клип; the text section («Текст», «По центру»). The Звук section (no clip with sound in the fixture): auto only |
| 11 | Opacity and Volume sliders | The slider and the field agree; a drag is one undo step; typing still works | `SliderViewBindingTests` (Opacity) | local (Claude): PASS for Opacity — a drag 100 → 21 %, the field followed, the picture dimmed; one Ctrl+Z gave 100 % with nothing left to undo. Volume: not in the app (no audio clip), and not covered by its own test (the same markup and converter as Opacity) |
| 12 | Project settings dialog | Russian headings, labels and buttons, nothing cut off | `ProjectSettings*` | local (Claude): PASS after shortening the speed details in the run («medium — по умолчанию»; «Пресет H.264 medium — по…» was cut) |
| 13 | Export: preflight error / warnings, progress, success, failure, replace file | Russian dialogs; FFmpeg detail kept in parentheses where shown | `ExportWorkflowTests` | local (Claude): PASS for the file picker («Экспорт — выберите файл», «Видео MP4 (*.mp4)»), the progress window («Видео», «417 / 950 кадров (44 %)», «Отмена»), «Экспорт завершён», «Заменить файл?» → «Отмена» → «Экспорт отменён.», «Экспорт невозможен» with two offline files (««q1.mp4» — офлайн (1 клип, первый в 00:00:00.000)»), the status «Экспорт невозможен: 2 проблемы.»; an export into a folder with a Cyrillic name. The warnings dialog and a failed export: auto only |
| 14 | Unsaved changes, recovery, open a damaged project | Russian dialogs and statuses | workflow tests | local (Claude): PASS for «Несохранённые изменения» (Сохранить / Не сохранять / Отмена) and the recovery offer «Восстановить несохранённую работу» after a forced close, «Не сейчас» and «Отбросить» with their statuses. A damaged project: auto only |
| 15 | Relink one file and a batch | Russian dialogs and summary | relink tests | local (Claude): PASS — the picker «Перепривязать «q1.mp4» — выберите файл», the status «… перепривязан к …», «Найти другие отсутствующие файлы?» (Искать / Не сейчас), «Поиск отсутствующих файлов» with «Перепривязать 1 файл», the status «Перепривязан 1 медиафайл.» |
| 16 | A long status message at 1024 px | Trimmed with «…», the whole message in the tooltip | — | local (Claude): PASS after fix (3) — «Экспортировано в C:\…\очень длинная папка для проверки обрезания ста…»; the tooltip with the whole message appears under the bar |
| 17 | Shortcuts in the Russian keyboard layout | The shortcuts work as in the English layout | `ShortcutRoutingTests` | local (Claude): PASS with the window's layout set to Russian (0x04190419) for I (Source In), N (Привязка), Ctrl+A («Выбрано 8 клипов»), Esc, S (split), Ctrl+Z. Ctrl+S / Y / I / E, J / K / L, Space, Delete in that layout: auto only (routed by key, not by character) |
| 18 | Numbers | Decimal comma in the fields | `NumericInputTests` | local (Claude): PASS for display («0,5» с, «1,0» ×); typing a decimal value: auto only |
| 19 | New text clip | «Текст»; existing texts and track names unchanged | `RussianMessagesTests` | local (Claude): PASS — + Текст on V5 made the clip «Текст» («Текст добавлен»); the fixture's «TITLE» and the track names unchanged |
| 20 | A Phase 23 project | Opens and saves byte for byte (v6) | format tests | local (Claude): PASS — one v6 `project.json` (written by the base build `c652667`) saved by the base build, by this build with `ru` and with `en`: three byte-identical files, equal to the source (SHA-256 `f59a6d43…`); method in `progress.md` |
| 21 | `App:Language` = `en` | The interface in English as before | resource tests | local (Claude): PASS — the whole window English (New / Open / … / INSPECTOR / 25 FPS) with the icons, when `appsettings.json` next to the exe says `en` and the app starts in its own folder (see the observation below) |

Observation (not a Phase 24 change, not fixed): the host reads `appsettings.json` from the **current directory** of the
process (`Host.CreateApplicationBuilder`'s content root). Started from another folder (a shortcut with another «Start in», a
console elsewhere), the app does not see the file next to the exe and runs with the defaults — `ru` for the language, and
also no `Ffmpeg:*` paths. Started by a double click on the exe the current directory is the exe's folder and the file is
read. Recorded for the product owner.
