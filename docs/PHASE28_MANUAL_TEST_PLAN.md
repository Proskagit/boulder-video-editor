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

Skeleton written at Step 28.0; completed at the closeout (Step 28.5, 2026-10-20). The first real-app run was stopped
before any input (the product owner was using the computer). The real-app run was made on 2026-10-20 after the product
owner freed the computer: the Debug build of `57364d8`, Windows at 125 % (120 DPI), the fixtures of
`tools/manual/New-Phase28TransitionFixture.ps1` (the product owner's session of 2026-09-27, 60 fps: two attempts on one
wall with handles, the same without handles, two attempts of one route on a vertical canvas) and the v7 project of the
Step 28.5 harness (synthetic media, offline in the app). Input was sent only while the app was the foreground window.
The defects found are listed under the table with their reproduction steps; they were fixed afterwards with the
product owner's permission (see "Fixes" at the end; the rows keep what the run found).

| # | Scenario | Expected | Automated coverage | Status |
|---|---|---|---|---|
| 1 | The gate of Step 28.1: Zoom (`K = 1.25`, 0.3 s) and Flash (0.2 s) rendered on the product owner's gym clips, horizontal and vertical, 25 / 30 / 60 fps where available | The product owner judges the look; an unsatisfactory Zoom stops the work with Z2 / Z3 proposed (D044 §11) | 28.1 (harness) | harness + PO: PASS (2026-10-20) — four real cuts (two attempts of one route on a vertical canvas, the same wall and angle, a change of episode, preparation → a dynamic move) at 60 fps, MP4s and frame strips in `VideosForTest/Phase28_Step28.1` (local); Zoom and Flash accepted by the product owner as specified, Z2 / Z3 not needed. 25 / 30 fps: no such source in the session — not run; a 30 fps project check judged redundant by the product owner (rounding covered by 28.2's tests). A second camera on one attempt: none in the session (a change of episode used) |
| 2 | «Переход ▾» on two adjacent clips of a video track: each of the five types | Each added with its default (1.0 / 0.6 / 0.2 / 0.3 / 0.3 s), or the longest that fits with a status; one Undo step each (D044 §6, §8) | 28.2, 28.4 | auto: PASS. local (Claude): PASS — the menu lists the five types; Затемнение 0,6 с · 36 кадров, Вспышка 0,2 с · 12, Наезд 0,3 с · 18, Сдвиг 0,3 с · 18 with «Переход «…» добавлен»; Наплыв on a 0.57 s clip — 0,95 с · 57 кадров «— самый длинный, что здесь помещается»; the zone marks ◐ ✦ ⊕ ⇢ ⨯; Undo × 4 / Redo step by step. Defect D4 (short zones hard to select) |
| 3 | A cut without source handles (clips cut at the source's ends) | Dip to black, Flash and Zoom added; Dissolve and Push not created / disabled, the message names Dip to black; no type replaced automatically (criterion 4) | 28.2, 28.4 | auto: PASS. local (Claude): PASS — Сдвиг from the menu refused with the message naming «Затемнение» and «Вспышка», nothing added; Вспышка added 0,2 с · 12 кадров; in the Inspector Наплыв and Сдвиг disabled. Defect D3: a disabled type button shows no reason on hover |
| 4 | The Inspector: type change between all types (e.g. Dissolve 1.0 s → Flash, Flash 0.2 s → Zoom), Push's four directions | The duration kept when in range, else clamped to the nearest allowed value with a status; each change one Undo step (D044 §6) | 28.2, 28.4 | auto: PASS. local (Claude): PASS — Затемнение 0,6 с → Вспышка clamped to 0,5 с · 30 кадров with the status, white at the cut; Вспышка → Наезд kept; Сдвиг Справа / Слева / Сверху / Снизу on the horizontal and the vertical canvas (Preview); each change one Undo step. Defect D1 (the section cut at 1440 × 900 and 1024 × 640) |
| 5 | The duration field and slider at 24, 25, 30 and 60 fps: the defaults, the minimum, the maximum, typed values, the arrows | The stored seconds and frames as D044 §5's table; the 0.1 s step never sticks; nothing shown below the minimum or above the limit (criterion 3) | 28.2, 28.4 | auto: PASS. local (Claude), 60 fps only: typed 0,75 → 45 кадров PASS; the arrow up from 0,75 → 0,9 (not 0,8) — observation O1; a value out of range not applied, the field shows the stored value again. 24 / 25 / 30 fps in the app: NOT RUN (no such source; covered by the tests) |
| 6 | Two Flash / Dip to black cuts closer than 1.0 s: add, type change, move, ripple trim, speed change, frame-rate change | Refused with a clear message; nothing changes (D044 §4, criterion 5) | 28.2 | auto: PASS. local (Claude): add — PASS: a Flash at 15.0 s, a cut at 15.57 s, Вспышка and Затемнение there refused, nothing added; the message's prefix names the wrong type — defect D5. Type change, move, ripple trim, speed and frame-rate changes in the app: NOT RUN (covered by the tests) |
| 7 | Clips of different lengths, trim, move, split and speed near a zone; a locked track; linked clips (D032) | D025 §5 for every type; the locked track refuses; the linked sound stays a hard cut (criterion 6) | 28.2 | auto: PASS. local (Claude): the locked track — PASS («Дорожка V1 заблокирована.», nothing added); Delete of a clip next to a Сдвиг removes the transition — the status names «Наплыв» — defect D2; an audio crossfade on detached sound added at once (60 кадров, the frames field, D032 unchanged) — PASS. Trim, move, split and speed next to a zone in the app: NOT RUN (covered by the tests) |
| 8 | Preview playback and scrubbing through each type; a text above the zone; hold outlines in a Push and a Zoom; a vertical canvas | Smooth playback; the title above stays readable through Dip / Flash; the outline follows its frame (D044 §2) | 28.3 | auto: PASS for the picture. local (Claude): playback through a Сдвиг zone and the playhead parked in the zones — PASS (black at the Dip's cut, white at the Flash's, the two halves of a Push); playback through the other types in the app: not watched separately; a vertical canvas, Push from the right, top and bottom — PASS. A text above the zone and hold outlines in the app: NOT RUN (covered by `ExportTransitionLibraryEndToEndTests`) |
| 9 | Export of a cut-to-music sequence using the five types, watched in a player against the Preview | The same transitions as the Preview, frame for frame (criterion 1, 7) | 28.3 (parity), end to end | auto: PASS. local (Claude): PASS — the five types on one track (no-handles fixture, 30 s, 60 fps) exported from the app: 1800 frames, h264 + aac, 30.000 s; the frames at the peaks — Zoom enlarged, Flash white, Dip black, Push two halves, Dissolve — as in the Preview; the vertical project with Push from the bottom: 1080 × 1920, 1440 frames, the zone frame as in the Preview; both MP4s played in PotPlayer (the system's player) to the end. No music track used; the product owner's own viewing remains |
| 10 | Undo / Redo of every action of rows 2–6; save, close, reopen | Every value restored; the file is `project.json` v8 with `direction` only for Push (D044 §9) | 28.2 | auto: PASS. local (Claude): PASS — saved: `formatVersion` 8, `push` with `fromRight` / `fromBottom`, the other types without `direction`, durations in ticks (e.g. 9000000 for 0,9 с); reopened in `ru` and `en`: every type, duration and mark restored |
| 11 | A Phase 27 v7 project with dissolves opened, saved and exported | Its dissolves look exactly as with the Phase 27 build; saved again only `formatVersion` differs (criterion 2) | 28.2, 28.3 | harness: PASS — a v7 project with dissolves (a 10-frame one, a legacy 2-frame one, two texts dissolving over a rotated, scaled, translucent clip with a hold outline) written by the Phase 27 code (`git archive 3c45c9d`): 17 zone frames drawn by the export's rasterizer of the Phase 27 code and of this branch byte-identical; saved again by this branch: only `formatVersion` 7 → 8 differs. local (Claude): opened in the app — PASS (three dissolves shown as «Наплыв», the legacy one 0,08 с · 2 кадра); its media are the harness's synthetic files (offline in the app), so the export from the app: NOT RUN; observation O2 |
| 12 | A v8 project opened by the Phase 27 build | Refused with the existing "saved by a newer version" message | 28.2 | harness: PASS — the project saved as v8 by this branch, opened by the Phase 27 code: "This project was saved by a newer version of AI Video Editor and can't be opened." |
| 13 | The menu, the TRANSITION section and the zone icons at 1024 × 640 and 1440 × 900, `ru` and `en` | Nothing cut, no untranslated text | 28.4 (guards) | auto: the localization guards PASS. local (Claude): **FAIL** — defect D1: the TRANSITION section cut at 1440 × 900 and 1024 × 640. The menu and the zone marks — PASS at both sizes; `en` — the menu (Dissolve, Dip to black, Flash, Zoom, Push), the toolbar and the statuses in English, PASS; a library type named «dissolve» in a refusal — defect D6 |
| 14 | A running export | Every transition edit refused (`EditingLock`) | 28.4 | auto: PASS. local (Claude): the export's progress window shown during both exports; an edit attempt during an export: NOT RUN (the exports ran 10–20 s; covered by `TransitionLibraryUiTests`) |

## Defects found in the real-app run (2026-10-20) — not fixed

- **D1 — the TRANSITION section is cut in the Inspector.** Open `fixture28/handles`, select both clips, «Переход» →
  «Сдвиг», keep the zone selected. At 1440 × 900 the type button «Сдвиг» and the direction button «Сверху» are cut at
  the Inspector's right edge and the note «Самое длинное, что здесь помещается: 4,02 с · 241 кадр» wraps with its end
  cut; at 1024 × 640 (and 1152 × 720) also, the Inspector scrolls and «Удалить переход» is below the fold. The audio
  crossfade's «Удалить перекрёстное затухание» is cut the same way at 1440 × 900.
- **D2 — the status of an automatic removal names «Наплыв» for every type.** `fixture28/handles`, add «Сдвиг» between
  the clips, select the left clip (click its body away from the zone), Delete: the status reads «Наплыв удалён: его клипы
  больше не соседние.» Source: `TimelineEditService.TransitionNote` (`Edit_DissolveRemoved`, `Edit_DissolvesRemoved`).
- **D3 — a disabled type button gives no reason.** `fixture28/nohandles`, add «Вспышка», select the zone, hover over the
  disabled «Наплыв» or «Сдвиг» for 2 s: no tooltip (D044 §6 / Step 28.4: disabled «with the reason»). Avalonia shows no
  tooltip on a disabled control unless `ToolTip.ShowOnDisabled` is set.
- **D4 — short zones are hard to select at the default zoom.** At 50 px per second a 0.2 s zone is 10 px wide and the
  trim handles of both clips take most of it: in `fixture28/nohandles` the Flash zone at 15 s was selectable at one
  pixel column only, a 0.9 s zone at its outer ~4 px. Zooming the timeline in works around it.
- **D5 — the 1.0 s refusal's prefix names «наплыв».** `fixture28/nohandles` with a Flash at 15 s; split the right clip
  at ~15.5 s, select both parts, «Переход» → «Вспышка» (or «Затемнение»): «Нельзя добавить наплыв: Вспышки и затемнения
  должны быть не ближе 1 секунды друг к другу.» Source: `Transition_CantAddDissolve` used for every video type
  (`TimelineEditService`, the add path).
- **D6 — «This cut already has a dissolve» for any type.** With `App:Language` = `en`, select the two clips of a cut that
  has a Zoom, «Transition» → «Flash»: "This cut already has a dissolve." (ru: «На этой склейке уже есть наплыв.»).
  Source: `Transition_CutHasDissolve`.

Observations (not defects by D044's text, for the product owner):
- **O1 — the arrows from a typed value.** The arrow step goes along the 0.1 s grid from the shown value rounded to
  0.1 s: from 0,75 the up arrow gives 0,9 (0,75 → 0,8 + 0,1) and the down arrow 0,7; a value typed exactly 0.1 s from the
  shown one (0,75 → 0,85) is taken for an arrow step and becomes 0,9.
- **O2 — an offline source.** For a dissolve whose media are offline the Inspector shows «Самое длинное, что здесь
  помещается: 0 с · 0 кадров» beside the stored 0,08 с · 2 кадра.

## Fixes (2026-10-21) — status

Allowed by the product owner after the run (D1–D6, O1, O2); D044 "Corrections after the real-app run". Automated: the full
suite 4374 / 2 / 0, heavy 4376 / 0 / 0, three blame-hang runs clean, Release / Debug `-warnaserror` 0 / 0. The repeated
real-app checks were made on 2026-10-21 when the product owner freed the computer — the Debug build of `c722a20` (D7
included), fresh fixtures of `tools/manual/New-Phase28TransitionFixture.ps1`, Windows at 125 %; the column "Real app" below.

| Defect | Fix | Automated | Real app |
|---|---|---|---|
| D1 | the type / direction rows wrap in the column after the labels; the Remove label wraps | `TransitionLibraryUiTests` (the section's layout in the XAML), `FadeViewBindingTests` | local (Claude): PASS — at 1440 × 900 and 1024 × 640 the buttons wrap in two or three rows, «…4,02 с · 241 кадр» whole; no element of the Inspector passes its right edge (UI Automation bounds); at 1024 × 640 the section scrolls and «Удалить переход» is reachable and enabled |
| D2 | the automatic removal names the type («Переход «Сдвиг» удалён…», «Удалено переходов: N…») | `TransitionMessageTests` (en + ru, each type) | local (Claude): PASS — Delete of A next to a Сдвиг: «Переход «Сдвиг» удалён: его клипы больше не соседние.»; a plain trim of the cut edge: the same; en: "The transition "Zoom" was removed: its clips no longer meet."; Undo brings the clip and the transition back |
| D3 | `ToolTip.ShowOnDisabled` on the type buttons | `TransitionLibraryUiTests` | local (Claude): PASS — hovering the disabled «Наплыв» (no handles) shows «За краями клипов недостаточно материала для этого перехода. Затемнению и Вспышке он не нужен.» |
| D4 | the zone's mark (14 × 14 px at its centre) selects the transition over the trim handles | `TransitionLibraryUiTests` (the mark in the XAML, its name) | local (Claude): PASS — the 0.2 s Flash at 50 px/s: a press on the mark (x 857–872, the clip row's middle) selects it every time; above and below the mark the handles act as before — a press selects the clip, a plain or Shift drag of A's end above the mark of a Сдвиг trims (ripple: B follows, the transition stays) |
| D5 | «Нельзя добавить переход «Вспышка»: …» | `TransitionMessageTests` | local (Claude): PASS — a Flash at 15 s, a cut at 15.6 s: «Нельзя добавить переход «Вспышка»: Вспышки и затемнения должны быть не ближе 1 секунды друг к другу.»; «…переход «Затемнение»: …» for a Dip |
| D6 | «На этой склейке уже есть переход «Наезд».» (en: "This cut already has a transition: "Zoom".") | `TransitionMessageTests` | local (Claude): PASS — ru: «На этой склейке уже есть переход «Сдвиг».»; en: "This cut already has a transition: "Zoom"." |
| O1 | typed = the nearest frame; only the spinner steps to the next 0.1 s mark that changes the frames | `TransitionLibraryUiTests` (60 / 25 / 24 fps, the limits, the spinner through the command) | local (Claude): PASS — 60 fps: typed 0,75 → 45 кадров; the spinner up → 0,8 (48), from 0,75 down → 0,7 (42), again → 0,6 (36); typed 0,85 after 0,75 → 0,85 (51); the Up key from 0,85 → 0,9 (54). A Flash at 24 fps: 0,21 (5) → 0,29 (7) → 0,42 (10) → 0,5 (12, the maximum) and back 0,42 → 0,29; at 25 fps 0,28 (7) → 0,32 (8) → 0,4 (10), down 0,32; at 30 fps 0,33 (10) → 0,4 (12) → 0,5 (15), down 0,4 — never stuck |
| O2 | «Длиннее, чем сейчас, здесь не поместится.» instead of a limit below the value | `TransitionLibraryUiTests` (video transition and crossfade) | local (Claude): PASS — the v7 harness project (media offline, no metadata): the legacy dissolve shows «0,08 с · 2 кадра» and «Длиннее, чем сейчас, здесь не поместится.» |

Found while fixing (code reading, confirmed by a scratch test); **fixed** on 2026-10-21 with the product owner's
permission — the ripple trim and the slip keep handles only for Dissolve, Push and the audio crossfade
(`TransitionHandleReservationTests`, 17: slip both ways, ripple outward on both cut edges, plain and ripple trims inward for
every type, a dissolve and a flash on one clip, the audio crossfade, Undo / Redo; 7 of them fail without the fix); the
real-app check (2026-10-21): PASS — the no-handles fixture (A's source ends 1 s after the cut) with a Flash at the cut: an
Alt-drag slip of A stops at the end of the source («Сдвиг остановлен в конце исходного файла.»), the Flash stays; a Shift drag
of A's end to the right reaches the source's end (16 s 2 frames — the file's 16.033 s), B and the Flash follow, no stop note.
Before the fix both stopped 6 frames earlier:
- **D7 — the trim and the slip keep source handles for every type.** `TimelineEditService` (ripple trim outward, slip)
  reserves the handle of the zone for any transition on the edge, though D044 §3 needs handles only for Dissolve and Push:
  a 20 s video at 25 fps split at 100 / 300 with a Flash at 100 — slipping B by −1000 frames stops at Source In 2 frames
  («Сдвиг остановлен там, где материал клипа нужен переходу «Вспышка».») instead of 0; a ripple trim of A's end to 1000
  stops at 497 instead of 500. Dip to black and Zoom the same.

## Re-run of the remaining rows (2026-10-21)

The rows of the table above that were partly NOT RUN, checked in the app where no specification change is needed:
- Row 5 — the field at 24, 25 and 30 fps (Project Settings → frame rate): PASS (see O1 above).
- Row 6 — a Zoom 0.6 s after a Flash, type change to Вспышка / Затемнение: refused, «Нельзя изменить переход «Наезд»: Вспышки
  и затемнения должны быть не ближе 1 секунды друг к другу.», the type stays; two Flashes 2 s apart, a Shift drag of the clip
  between them 1.2 s shorter: refused, «Нельзя обрезать: Вспышки и затемнения должны быть не ближе 1 секунды друг к другу.» —
  PASS. Moving a pair, a speed change and a frame-rate change that would bring peaks closer: NOT RUN (automated).
- Row 7 — a split strictly inside the Flash's zone: «Нельзя разрезать внутри перехода «Вспышка».» PASS; a split at the zone's
  edge (15:06 at 60 fps): made, as D025 §5 allows. Trim and slip beside a transition without handles: PASS (D7 above). Move
  and speed next to a zone: NOT RUN (automated).
- Row 8 — a title on V2 over a Dip (0,4 s at 30 fps): the picture darkens, the title stays at full brightness — PASS. Hold
  outlines in a Push / Zoom: NOT RUN (the fixtures have no hold markup; automated).
- Row 11 — exporting the v7 project from the app: NOT RUN (its media are the harness's synthetic files, offline in the app).
- Row 13 — after D1: PASS at both sizes; en: the menu, the statuses and the messages in English, PASS.
- Row 14 — an export running (900 frames): the TRANSITION section is disabled; toggling «Наезд» through UI Automation is
  refused; after the export the type is unchanged — PASS.

New defect found in the re-run — **fixed** on 2026-10-21 (two-way `IsChecked`, the section re-synced from the model after a
refused or empty change; `TransitionLibraryUiTests`); real app: PASS — the same steps (a Flash at 12 s, a Zoom at 12.6 s in
`fixture28c/relink`), «Вспышка» clicked on the Zoom: refused, only «Наезд» stays On (UI Automation), a click on «Наезд» itself
keeps it On:
- **D8 — a refused type change leaves the clicked type button checked.** `fixture28c/nohandles`: a Flash at the cut at 15 s,
  split B at 15.6 s, add «Наезд» on that cut, then click «Вспышка» (or «Затемнение») in the Inspector: the change is refused
  (the 1.0 s rule) and the status says so, but the clicked button stays highlighted (UI Automation: ToggleState On) next to
  «Наезд»; it stays so for the transitions selected later (the Flash at 15 s then showed «Затемнение» and «Вспышка» both On).
  Cause: a `ToggleButton` checks itself on click; `IsChecked="{Binding IsSelected, Mode=OneWay}"` is replaced by that local
  value, and the option's `IsSelected` did not change, so nothing resets it.

Observation: the frame-rate hint of Project Settings says «…появление, исчезание, наплывы и маркеры сохранят своё время.» —
"dissolves" for every transition type (a Phase 13 string; not changed).

## The export refusal for missing source frames (2026-10-21)

The product owner's policy (D025 §4 changed, D044): a Dissolve or a Push whose zone needs source frames the media do not
have refuses the export. Automated: Export.Tests (before / while rendering, the exact handle and one frame too few, Dip to
black / Flash / Zoom without handles, a range export, a clip's own hold-last), ExportEndToEnd.Tests with real ffmpeg (a
file replaced by a shorter one with the old metadata — refused before rendering, the destination byte for byte; a decoder
running out while rendering — the unfinished output deleted, the destination kept, no ffmpeg left; a v7 project with
dissolves exporting exactly as the same project built in memory; the Preview still holding the frames), the relink warning
(Timeline), the message (UI). Real app (local, Claude): PASS —
- `fixture28c/relink` (the handles excerpts), a Наплыв 1 с at 12 s saved; attempt A's file made unavailable, the project
  reopened (offline), «Перепривязать…» to `attemptA-14s.mp4` (the same excerpt cut at A's end): the dialog says «В новом
  файле не хватает исходных кадров, нужных переходу «Наплыв» за краем клипа; экспорт с ним будет отклонён, пока переход не
  укоротят или не удалят.»; relinked anyway.
- Export to an existing file («previous export», 15 bytes) → «Заменить»: «Ошибка экспорта — Экспорт не начат: переходу
  «Наплыв» между «attemptA-14s.mp4» и «attemptB.mp4» на дорожке V1 в 00:00:12:00 нужны исходные кадры за краями клипов,
  которых в файле нет. Укоротите или удалите переход либо перепривяжите полный файл. Файл не записан; существующий файл не
  изменён.»; the file still 15 bytes «previous export», nothing else in the folder.
- The type changed to «Вспышка» (0,5 с): the export succeeds, the file replaced (h264 1440 frames + aac, 24.0 s).
- NOT RUN in the app: the failure while rendering (a decoder running out after the check — covered end to end with real
  ffmpeg only; it needs a file that changes during the export).
