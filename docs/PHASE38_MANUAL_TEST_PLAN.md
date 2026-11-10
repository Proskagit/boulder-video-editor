# Phase 38 — UX Improvements before the MVP: manual test plan

Decision: DECISIONS.md **D057** (approved 2026-11-09). Plan of the phase: `docs/DEVELOPMENT_PLAN.md`, "Phase 38".

**Result (2026-11-10): Phase 38 accepted by the product owner.** The owner checked the build 0.38.1 (H1–H7 and the
regression rows of the checklist: passed, three remarks) and 0.38.2 (H8–H10 after the fixes, the blurred background by the
owner's reference: passed) by hand; the remarks are fixed in PR #63 / PR #64 (D057 §7). The rows below were the plan of
that check; per-row evidence was not recorded separately.
Environment: the target build of Phase 38; a **copy** of a real project (e.g. the training of 27.09.2026 with its FIT and
videos — the originals are never saved); Russian interface unless a row says otherwise, an English pass for the new texts;
1024 × 640 and 1440 × 900. Legend: **PASS**, **FAIL**, **PARTIAL**, **NOT RUN** (with a reason). The column «Automated»
names the tests that cover the row; «Evidence» is filled at the run.

## A. Settings files, FU-PF.1, the timeline's scroll bar and wheel, hold proposals (block A)

| # | Check | Expected result | Automated | Evidence |
|---|---|---|---|---|
| A1 | Start the app the first time (no `settings.json`, no `ui-state.json`) | starts with the defaults; nothing asked | `UserSettingsStoreTests` | |
| A2 | Damage `settings.json` by hand, start | starts with the defaults; the file set aside as `*.damaged`; the log says so | `UserSettingsStoreTests` | |
| A3 | Open a project saved by a newer build (a file with `formatVersion` 99 and an unknown value) | «Этот проект сохранён более новой версией…», not «повреждён» | `ProjectVersionMessageTests` | |
| A4 | Open a really damaged project | «повреждён» as before | `ProjectVersionMessageTests` | |
| A5 | Look at the timeline's horizontal scroll bar without hovering | always shown, wide enough to grab; dragging it scrolls | — | |
| A6 | Wheel over the tracks; Shift+wheel; Ctrl+wheel; a touchpad's horizontal swipe | horizontal scroll; vertical scroll of the tracks; zoom at the pointer (as before); horizontal scroll | `TimelineWheelTests` | |
| A7 | Wheel over the track headers | vertical scroll | `TimelineWheelTests` | |
| A8 | Select a clip without holds, «Разметить зацепки» on a fresh user | no proposals are generated; «Предлагать автоматически» off | `HoldProposalDefaultTests` | |
| A9 | Turn «Предлагать автоматически» on, restart, mark again | the choice kept; proposals generated (D047 unchanged) | `HoldProposalDefaultTests` | |

## B. The Inspector's sections and the window layout (block B)

| # | Check | Expected result | Automated | Evidence |
|---|---|---|---|---|
| B1 | Select a video clip | the Inspector in collapsible sections, **all collapsed at first** (D057 §7.2); «FIT» (marks and link) one section; no tabs | `InspectorSectionsTests` | |
| B2 | Collapse «Трансформация», select another video clip, then a text clip, then the first again | the state is the Inspector's: still collapsed for every clip; never reset by a selection | `InspectorSectionsTests` | |
| B3 | Collapsed «Зацепки», «Скорость», «Звук» on a clip with 12 holds | the header shows a summary («Зацепки · 12», «Скорость · 1×», «Звук · 100 %») | `InspectorSectionsTests` | |
| B4 | Scroll to «Звук», select another video clip | the scroll position stays | `InspectorSectionsTests` | |
| B5 | Restart | the sections' state kept | `InspectorSectionsTests` | |
| B6 | Resize the window, move it, widen the Inspector, raise the timeline; restart; then maximise and restart | the layout comes back; the maximised state comes back | `WindowLayoutTests` | |
| B7 | Put the window on a second screen, close, disconnect the screen, start | the window opens on the default place | `WindowLayoutTests` | |
| B8 | Every section of every kind of item (media, video, audio, text, template, transition) in `ru` and `en`, both sizes | nothing cut, every former field still there and working, Undo unchanged | UI tests of the Inspector | |

## C. The Media Browser, Shift+click, the clip's context menu (block C)

| # | Check | Expected result | Automated | Evidence |
|---|---|---|---|---|
| C1 | Import the videos of 27.09 in a mixed order; Ctrl+A in the Media Browser; «Добавить на таймлайн» | every file on the timeline in the order of recording, one after another; one Ctrl+Z removes them all; the list's order unchanged | `MediaBrowserMultiAddTests` | |
| C2 | Ctrl+A on the timeline; Ctrl+A in the hold markup mode with proposals | every clip selected; all proposals accepted — both as before | `ShortcutRoutingTests`, `MediaBrowserMultiAddTests` | |
| C3 | Ctrl+A while typing in a text field | selects the text | `ShortcutRoutingTests` | |
| C4 | Select one clip, Shift+click a clip three clips later on the same track | the four clips selected | `ShiftRangeSelectionTests` | |
| C5 | Shift+click a clip on another track | the clips of the tracks between in that time interval selected; partners included | `ShiftRangeSelectionTests` | |
| C6 | Shift+drag a clip's edge | a ripple trim, as before | `ShiftRangeSelectionTests` | |
| C7 | Right-click a video clip, then an audio clip, then a text clip | the menu with the clip's operations; Split only with the playhead inside; «Разметить зацепки» / «Распознать прохождения из FIT» only on a video clip (the latter with a training); each item does what its button / key does, one Undo step | `ClipContextMenuTests` | |

## D. A drop on an occupied place, frame stepping (block D)

| # | Check | Expected result | Automated | Evidence |
|---|---|---|---|---|
| D1 | Drag a file from the Media Browser onto the middle of a clip X | while dragging, the preview shows the new clip after X and the clips that move; after the release it is right after X, the later clips pushed as far as needed | `DropOnOccupiedTests` | |
| D2 | Drag an existing clip so that it starts inside X | it goes to X's start; X and the later clips move right only as far as needed; X not split; one Undo step | `DropOnOccupiedTests` | |
| D3 | The same with a linked clip (video + audio), with an anchored «Боулдер», with a dissolve at X's left edge, onto a locked track | the partner moves with it; the template with its clip; the dissolve case refused, shown before the release with the reason; the locked track refused | `DropOnOccupiedTests` | |
| D4 | Nudge (Alt+→), Paste and Duplicate onto an occupied place | refused as before (Q11) | `DropOnOccupiedTests` | |
| D5 | A project of 30 fps with clips of 30, 60 and 240 fps: Left / Right on each | steps of 1/30, 1/60, 1/240 s; the Preview shows each source frame | `FrameSteppingTests`, `FrameNavigationTests`, `NavigationGridPlaybackTests` | |
| D6 | A 240 fps clip at speed 0.25 | each step a new source frame (1/60 s on the timeline) | `FrameSteppingTests` | |
| D7 | Between two such steps: Split, Q, W, I, O, a marker, Insert | each at the nearest project frame | `FrameSteppingTests` | |
| D8 | A gap; Shift+arrows; J; the Source mode | a project frame; one second; one second back; the source's own steps — as before | `FrameSteppingTests` | |
| D9 | «Совместить кадр с концом подъёма» on a 240 fps frame between project frames | `Z` from that exact source frame | `FrameSteppingTests` | |

## E. Live scrubbing (block E)

| # | Check | Expected result | Automated | Evidence |
|---|---|---|---|---|
| E1 | Drag the playhead quickly over a 4K / HEVC clip and back | the picture follows while the mouse moves (forward smoothly, back a few pictures a second — D057 §7.4); no growing lag | `ScrubbingTests`, `PreviewLayersTests`, local `ScrubBenchmarkTests` | |
| E2 | Stop the mouse and release | the picture is exactly the frame at the playhead | `ScrubbingTests` | |
| E3 | Scrub, then play | playback and its sound start from the playhead as before | `PlaybackServiceTests` | |
| E4 | Scrub over an HDR clip, over a transition, over a text | correct pictures, no stale frame left | — | |

## F. Settings, the heart-rate offset, the blurred background, YouTube timestamps (block F)

| # | Check | Expected result | Automated | Evidence |
|---|---|---|---|---|
| F1 | «Настройки…» | sections «Общие», «FFmpeg», «Таймлайн и разметка», «COROS / FIT»; the FIT calibration there; «Настройки FIT…» of the COROS menu opens that section | `SettingsWindowTests` | |
| F2 | The heart-rate offset on a fresh user | −70 с (D057 §7.7); a saved value stays | `UserSettingsStoreTests`, `SettingsWindowTests` | |
| F3 | In a green area: the BPM at a frame with −60 and with 0 | with −60 the value of 60 s later in the FIT; the area, its bounds, `T`, `Z`, `CC`, the calibration and the Inspector's readout unchanged | `HeartRateOverlayTests` (D057 §3.8) | |
| F4 | Change the offset, restart | kept | `HeartRateOverlayTests` (D057 §3.8) | |
| F5 | Export a part with the heart rate | the export as the Preview | `ExportHeartRateEndToEndTests` | |
| F6 | A landscape clip in a vertical project, «Фон: Размытый» | the bars carry the picture's own edges, softly blurred and darker — no enlarged copy of the video (D057 §7.5); the export the same (5 frames compared) | `BlurredBackgroundRenderTests` (Preview = export) | |
| F7 | Undo; Save; open in this build; open the v19 file in a build of `main` before Phase 38 | the choice back to black; kept after a reopen; the older build refuses with «новее» | `BackgroundPersistenceTests` | |
| F8 | Open a v18 project | opens, every background black; Save writes v19 | `BackgroundPersistenceTests` | |
| F9 | Scrub and play a blurred 4K clip | no noticeable slowdown | — | |
| F10 | «Экспорт» (the dialog's timestamps, D057 §7.9) on a project with routes (some Top, some Attempt, one free «Боулдер», one split area) | `00:00 Intro` first unless a route starts at 0; one line per route at its green's left edge; `Трасса 7 · 7A 🟢`, `🔴`, nothing for no status; «Копировать» puts the text on the clipboard | `YouTubeTimestampsTests` | |
| F11 | With an In / Out range | the times from In; routes outside left out | `YouTubeTimestampsTests` | |

## H. After the practical check (D057 §7, 2026-11-10)

| # | Check | Expected result | Automated | Evidence |
|---|---|---|---|---|
| H1 | Select several files in the Media Browser, drag one of them onto a track | all of them land in the order of recording from the drop point; one Ctrl+Z removes them all; dragging one unselected file as before | `MediaBrowserMultiAddTests`, `AddClipsTests` | |
| H2 | Drag a file over an empty place, then over a clip | a translucent area as wide as the clip shows where it lands; amber over a clip it would push; the drop is never refused by it | `AddClipsTests`, `MediaBrowserMultiAddTests` | |
| H3 | The Inspector of a video clip | every section collapsed at first; «Появление, исчезание и скорость» and «Трансформация и кадрирование» one section each; a section opened stays open for the next clip | `InspectorSectionsTests` | |
| H4 | «Настройки…» | the sections on the left, the selected one on the right | `SettingsWindowTests` | |
| H5 | Mark the holds of a route, «Готово» | the intro and the final laid out with the animation; «Готово» again or reopen + «Готово»: nothing added; one Ctrl+Z removes the layout | `HoldMarkupUiTests` | |
| H6 | «Экспорт» | one dialog: folder, «Обзор…», name, the output's line, the YouTube timestamps with «Копировать»; no «Таймкоды YouTube…» on the tool bar | `ExportWorkflowTests` | |
| H7 | Step → through a 4K / 240 fps clip | each step at once; ← about a third of a second | `ScrubbingTests`, local `ScrubBenchmarkTests` | |
| H8 | The Inspector of a template; of a file selected in the Media Browser | the template section open; «Технические данные» open; the rest collapsed (D057 §7.11) | `InspectorSectionsTests` | |
| H9 | A blurred background (a vertical clip in a horizontal project, and the reverse) | the scene itself enlarged behind the picture, softly blurred and recognisable, slightly darker — as the owner's reference (D057 §7.12); the export the same | `BlurredBackgroundRenderTests` | |
| H10 | Right-click a file in the Media Browser; then a file of a group selection; then an offline file | «Добавить на таймлайн», «Показать в проводнике», «Удалить из проекта» (+ «Перепривязать…» when offline); on a group file «Добавить» adds the group, «Удалить» removes only that file | `MediaRemovalUiTests` | |
| H11 | The tool bar | no «Импорт медиа»; the other buttons in place and working; «+ Импорт» in the Media Browser and Ctrl+I import; «На таймлайн» and the file menu add (D057 §7.13) | `ProjectSettingsViewBindingTests`, `ShortcutRoutingTests` | |

## G. Regression and the practical check

| # | Check | Expected result | Evidence |
|---|---|---|---|
| G1 | The main path on the copy: import → «Добавить на таймлайн» → FIT → «Разметить зацепки» → templates → export → timestamps | no step harder than before; every former function works | |
| G2 | The owner's practical session | the remarks recorded; each fixed, moved to D057 §4 or declined | |
