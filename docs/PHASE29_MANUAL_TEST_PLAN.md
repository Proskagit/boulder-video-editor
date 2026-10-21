# Phase 29 — manual test plan

Manual checks of Phase 29 (DECISIONS.md D045): five ready-made visual templates for bouldering videos and the audit of the
existing music scenario. Run in the real app on Windows with the default `appsettings.json` (`App:Language` = `ru`) unless
a row says otherwise, at **1024 × 640** (the window's minimum) and **1440 × 900** (the default), in Avalonia's logical
pixels. Media: the product owner's real bouldering gym clips (session of 2026-09-27: 4K HEVC ≈ 60 fps, horizontal and one
vertical) and a music file of the product owner's choice (the audit's inputs below).

**Result (2026-10-22):** the product owner ran the manual check of the templates and accepted Phase 29 — the templates work
correctly, the corrected centring (row 13) and the moment of the status (row 15) are satisfactory. The rows marked NOT RUN
below were not run by Claude in the real app; the product owner's check covers the templates in the app. WASTED deferred
(D045 §15): its rows stay as recorded, not part of the phase.

Status column — kinds of evidence, never mixed:
- **auto** — covered by automated tests only, not checked in the real app;
- **local (Claude)** / **PO** — the real-app run on Windows / the product owner's check;
- **harness** — renders outside the app (the painter's drawing, a scratch tool or tests);
- **NOT RUN** — not checked in the real app (the reason given).

Skeleton written at Step 29.0 (2026-10-21). The template rows are completed after the gate 29.1 and the product owner's
decision on D045; the music audit (part B) is specified now and run at Step 29.2 (before any change of the audio path).

## A. Templates (the gate 29.1 and later steps)

| # | Scenario | Expected | Automated coverage | Status |
|---|---|---|---|---|
| 1 | The gate of Step 29.1, revision 1: the five template prototypes on the product owner's gym clips, horizontal and vertical, with animated hold outlines; WASTED with its sound on and off | The product owner judges the look, the restraint (holds and the climber readable) and the fields; changes or a stop decided by the owner (D045 §11) | 29.1 (harness) | harness: rendered 2026-10-21. PO (2026-10-21): REC, VHS, Highlight accepted in principle; Competition → «Боулдер»; WASTED → a reconstruction (revision 2). Renders of the replaced ones in `out/archive_rev1` |
| 2 | The gate, revision 2: «Боулдер» (horizontal TOP and ПОПЫТКА, vertical TOP and without a status) and the WASTED reconstruction (horizontal and vertical, with the sound and muted; slow motion by the clip's 0.3× speed) | The plate compact, never over the climbing; WASTED recognisably close to GTA V's screen in composition, typography, transition, timing and sound role (D045 §13); the licence questions answered by the owner | 29.1 (harness) | harness: rendered 2026-10-21. PO (2026-10-21): accepted with two corrections of WASTED (no outline; the sound at 1× over the attenuated clip sound) → revision 3. Revision 2's WASTED renders in `out/archive_rev2` |
| 3 | *(WASTED — deferred, D045 §15; kept as a record)* The gate, revision 3: WASTED with plain red letters; the sting at 1× at the switch with the clip's own sound attenuated under it and restored after; the sound off; the same clip at 0.3× and at 1× | The word plain (no outline, shadow or effect) at the agreed size, place, colour and moment; the sting identical and at the same template time at 0.3× and 1×; the clip's sound clearly under the sting, back after it; nothing eerie | 29.1 (harness) | harness: PASS on the 48 kHz mix — lag 0 samples, correlation 0.998 at 0.3× and 1×; the clip's sound −22 dB under the sting, its own level after it (−34.2 / −29.2 dBFS). PO (2026-10-21): **WASTED deferred** — the sound accent unsatisfactory; materials in `out/archive_wasted_deferred` |

The implementation (D045 approved 2026-10-21, §16; WASTED deferred, §15). Fixture:
`tools/manual/New-Phase29TemplateFixture.ps1 -Source "<the session's folder>"` — the product owner's climb on V1 and the four
templates on V2 (REC 0–4 s, VHS 4–8 s, «Боулдер» 8–12 s, «Хайлайт» 12–15 s), horizontal and vertical, the playhead at 5 s.

| # | Scenario | Expected | Automated coverage | Status |
|---|---|---|---|---|
| 4 | «Шаблон» in the timeline header: each of the four at the playhead | On the top video track at the playhead, selected, its length and look; «Шаблон добавлен»; one Undo step; a locked top track refuses | `TemplateEditTests`, `TemplateUiTests` | auto: PASS |
| 5 | The Inspector of each kind: only its fields (REC / VHS: Дата; Боулдер: Трасса, Категория, Статус, Цвет трассы; Хайлайт: Заголовок, Подзаголовок); no fades, transform or text sections | Typing applies live and merges into one step; a status and a colour are one step each; a too long or two-line value is refused and the field shows the model again | `TemplateUiTests`, `TemplateViewTests` | auto: PASS |
| 6 | Split inside a REC / VHS template; Duplicate; copy / paste; a transition next to a template | The left part keeps the appearance, the right the disappearance; the counter goes on from the cut; copies keep every field; a transition is refused with «Рядом с шаблоном нельзя поставить переход.» | `TemplateEditTests` | auto: PASS |
| 7 | Save, reopen; a v8 project; the Phase 28 build opening a v9 file | Every field exact (saved again byte for byte); a v8 project saved again differs only in `formatVersion`; the older build refuses the newer version | `TemplatePersistenceTests` (the older build's check: the version rule shared since v2) | auto: PASS |
| 8 | Preview = export: each template, horizontal and vertical, over a picture and a text, in the appearance and in the middle; VHS treating what is below it only | The same bytes at the canvas size; VHS changes the text under it, not the text above it | `TemplateRenderingTests`, `ExportTemplateEndToEndTests` | auto: PASS |
| 9 | Templates and the sound | The audio of a project with templates is sample-identical to the same project without them | `ExportTemplateEndToEndTests` | auto: PASS |
| 10 | The look in the app on the product owner's footage (the fixture) | As the accepted prototypes (`VideosForTest/Phase29_Step29.1/out/app` — the product's own drawing on the gate's scenes) | harness | harness: PASS (2026-10-21, Claude: side by side with the prototypes; the PLAY triangle corrected) |
| 12 | The real app (Debug build of the branch, an isolated profile): the fixture's two v9 projects opened with `--open-project`, the playhead at 5 s (VHS) and at 9.5 s («Боулдер», vertical) | The projects open; the Preview shows the template over the climb as the export would; the timeline names the four templates; «Шаблон» in the header | — | local (Claude, 2026-10-21): PASS — VHS with its treatment, PLAY ▶ and the counter 0:00:01 at 5 s; on the vertical canvas «ТРАССА 3 · 6B+ · TOP» above the phone zone; «REC · 27.09.2026», «VHS», «Боулдер · 3 · 6B+», «Хайлайт · ЛУЧШАЯ…» on V2. Window shots only — no input was sent to the app (the product owner's computer): the menu, the Inspector, Split, playback and the export in the app NOT RUN (covered by rows 4–9) |
| 13 | Fix 1 of the product owner's check (2026-10-21): the text of «Боулдер» (ТРАССА · КАТЕГОРИЯ, TOP, ПОПЫТКА) and «Хайлайт» (title, subtitle) centred on its block — short and long, Russian and English, horizontal and vertical, Preview and export | The ink of the text centred on its block within 1 px vertically and horizontally (the band of the capitals — descenders and diacritics of Д, Й, Ц, Щ, Ё reach out of it by design) | `TemplateTextAlignmentTests` (9, pixels at 1080p), `TemplateRenderingTests` (Preview = export) | auto: PASS — every case within 0.5 px (one vertical title 1.0 px). Before / after: `VideosForTest/Phase29_Step29.1/review/fix` (local). PO: PASS (2026-10-22) |
| 14 | Fix 2: the route block and the status as two blocks — ТРАССА navy with white text; TOP its own yellow block; ПОПЫТКА its own muted blue block with white text; the status appears separately with a short restrained animation | Two blocks with a gap; the status fades in with a small slide (0.25 s, no overshoot) after the route block | `TemplateTextAlignmentTests` (the gap, the colours), `TemplateRenderingTests` | auto: PASS; the appearance shown in `review/fix/attempt_appear_sequence.png`. PO: PASS (2026-10-22) |
| 15 | The moment of the status (D045 §17.4): add «Боулдер», choose TOP or ПОПЫТКА, put the playhead on a frame inside the clip, press «На указатель»; play over the moment; Undo / Redo; Split before and after the moment; Duplicate; trim the end before the moment; save, close, open; export | Before the moment only the route block; from it the status appears in 0.25 s and stays to the end; the Inspector shows the moment in seconds of the template (and «после конца шаблона» when beyond it); a line on the clip marks it; each «На указатель» one undo step; after a Split each part shows the status only where it is due; the copy has the same moment; a trim moves nothing (beyond the end: no status); the moment survives save / open; the export starts the appearance on the same frame as the Preview | `TemplateEditTests` (5 new), `TemplateCompositionTests` (2), `TemplatePersistenceTests` (+3 cases), `TemplateRenderingTests` (3), `ExportTemplateEndToEndTests` (3 new: real export, Preview = export around the moment, split = whole), `TemplateUiTests` (2), `TemplateViewTests` (1) | auto: PASS. Renders: `VideosForTest/Phase29_Step29.1/out/app/status_at`, frame sheets `review/status_at` (local). PO: PASS (2026-10-22) |
| 11 | The Preview's and the export's speed with VHS on screen (1080p) | The Preview plays smoothly; the export's slowdown recorded | — | harness: the export's rasterizer 18–20 ms / 1080p frame with VHS (9.5 without), REC ≈ 10.5, «Боулдер» / «Хайлайт» ≈ 10 ms |

## B. Music scenario audit (D045 §7)

Goal: check the **existing** path end to end and record its defects; nothing new is added (no mixer, ducking, equaliser,
reverb, looping, automatic fitting). A defect is fixed only when the product owner agrees it is a defect or a material UX gap
(D045 §7). Each row is run once in the app and checked by ear and, where a measurable criterion exists, on the exported file
(`ffprobe` / `ffmpeg` — `astats`, `silencedetect`, a sample comparison).

Inputs:
- **M1** — the product owner's real music file (an MP3, ideally also the same piece as M4A / AAC), ≥ 3 min, 44.1 kHz.
- **M2** — a generated reference (made by a local script, not committed): 48 kHz and 44.1 kHz WAV / MP3 / M4A, a
  1 kHz sine at −12 dBFS with a 10 ms click every whole second, 3 min — a gap, a doubled block, an offset or a level error
  is measurable on it.
- **V** — two or three of the product owner's gym clips with their own sound (60 fps), a horizontal and a vertical canvas.

Defect criteria (objective where possible):
- **Gap / doubled audio**: in the export of M2 the clicks stay exactly 1.000 s apart (± 1 sample at 48 kHz) from the
  music clip's start to its end; `silencedetect` (−60 dB, 5 ms) finds no silence inside the clip except the fades.
- **Offset**: the music's first click at the clip's timeline start ± 1 ms (an MP3's encoder delay compensated); after a
  trim of the start by N s, the first click at N s of the source.
- **Level**: volume 100 % → the exported sine's RMS within ± 0.1 dB of the source's; 50 % → −6.02 ± 0.1 dB; 0 % → digital
  silence; 200 % → +6.02 dB, the overload behaviour recorded (clipping or a limiter — whatever exists is recorded, not
  changed in this audit).
- **Fades**: the envelope linear over the set number of frames (D025 PO-3), starting / ending at the frame boundaries; no
  click at a fade's start or end (no sample step above the sine's own).
- **Clicks at edits**: no discontinuity at a cut, a trim point, a split point or the end of the export beyond the
  signal's own (sample-to-sample difference checked around each boundary).
- **End of the export**: the audio stream ends with the video (± one audio frame); the fade-out reaches silence at the
  last frame; nothing cut early.
- **Mix with the video's own sound**: both audible at their volumes; muting the music track mutes only it.
- **Preview**: by ear — no stutter, the same position as the picture after a seek and during scrubbing; the Preview's
  level matches the export's (by ear; the R10 audio-device note of Phase 17 stays as it is).
- **UX gap**: a step of the scenario that needs more than the obvious action (e.g. no way to set a fade without opening an
  unrelated panel), or a message that does not tell the user what happened — recorded with the steps, not fixed on the
  spot.

| # | Scenario | Expected | Status |
|---|---|---|---|
| B1 | Import M1 (MP3) and M1 (M4A) through the Media Browser and by drag and drop; M2 in every format | Imported, analysed (duration, sample rate shown), waveform drawn; an unsupported file refused with a reason | auto: PASS (`MusicScenarioAuditTests`, M2): MP3 / M4A / FLAC at 44.1 kHz and WAV at 48 kHz analysed and placed — every burst within ±0.2 ms on the encoder's PCM and in the MP4. The Media Browser dialog and drag and drop in the app: NOT RUN (input to the app not sent; M1, the product owner's music, not provided) |
| B2 | Place the music under the video: drag to the audio track A1 at 0, at the playhead, onto an occupied place; a project with no audio track yet | The clip lands where dropped (snapping as D037); no overlap; the video's own sound stays on its track; one Undo step | NOT RUN in the app (drop and snapping covered by the D037 suites); the export of music under the video: auto: PASS (`MusicScenarioAuditTests`, M2): |
| B3 | Volume: 0 / 50 / 100 / 200 % by the field and the slider; Undo / Redo | Levels as the criteria above; one Undo step per change (D017) | auto: PASS (`MusicScenarioAuditTests`, M2): 100 % 0.1768 RMS (0.25/√2), 50 % −6.021 dB, 200 % +6.021 dB, 0 % digital silence. Observation O1: 200 % of a loud source is hard-clipped at full scale (no limiter — by design, D042 §3); recorded, not a defect. One step per change: the D017 suites. The field and the slider in the app: NOT RUN |
| B4 | Trim the music's start and end (drag, `Q` / `W`), split it and delete a part; ripple and non-ripple | Offsets as the criteria; no gap or doubled block at the edit; one Undo step each | auto: PASS (`MusicScenarioAuditTests`, M2): a 0.32 s trim of the start on time (±0.04 ms); a split at 2.48 s sample-identical to the unsplit music; a removed middle part exact silence, the rest on time. The gestures in the app: NOT RUN (the trim / split / ripple suites) |
| B5 | Fade-in 2 s and fade-out 3 s on the music; a fade on a clip shorter than both fades | Linear envelopes; clamped as D025; no click | auto: PASS (`MusicScenarioAuditTests`, M2): a 1 s fade-in and a 2 s fade-out within 0.001 of full scale of the line; the largest sample step equal to the sine's own (no click). The clamp: the D025 suites |
| B6 | Fit the music's end to the video's end by hand (trim to the last video clip's end) and a fade-out there | Possible with the existing tools in a few actions; the exported audio ends with the picture, faded to silence | auto: PASS (`MusicScenarioAuditTests`, M2): the export as long as the picture, the last 20 ms 0.001 RMS, the last sample 0. The actions in the app: NOT RUN (trim and the fade field exist; no automatic fitting — by the product owner's decision) |
| B7 | Export (H.264 / AAC, each audio bitrate offered) of a 3-min project: music + the clips' own sound, a vertical and a horizontal canvas | The criteria above on M2; by ear on M1: no gap, no click, no drift between picture and sound at the end | auto: PASS (`MusicScenarioAuditTests`, M2): B1–B6 through the real encoder (AAC 48 kHz, the MP4 decoded). By ear on M1: NOT RUN (not provided) |
| B8 | Preview playback from the start, after a seek into the music, across a trim point and a split; scrubbing; the In / Out loop | By ear: no stutter, sound at the picture's position, the fades heard | auto: the Preview's mix equals the export's sample for sample (`ExportAudioIntegrationTests`); by ear: NOT RUN |
| B9 | Save, reopen, export again | The same audio (sample-identical export of M2 before and after the reopen) | auto: PASS (`MusicScenarioAuditTests`, M2): the export after a save and reopen sample-identical (placement, trim, 60 % volume, fades) |
| B10 | A 44.1 kHz MP3 with a variable bitrate (VBR) and a long file (≥ 10 min) | Seeks land at the right place (clicks at whole seconds); no drift over the length | auto (heavy): PASS — a 10-minute VBR MP3 used from 5:00 for 60 s: 60 bursts, the worst 0.05 ms |

The audit's result (Step 29.2, 2026-10-21): **no defect found** in the existing music path on the measurable criteria;
one observation (O1: 200 % of a loud source clips — no limiter, by design). Nothing in the audio path was changed. Not
covered: the app's own gestures (import dialog, drop, slider, trims by hand) and listening by ear, and the product owner's
real music (M1, not provided) — the remaining manual check for the product owner.
