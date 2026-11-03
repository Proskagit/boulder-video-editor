# Phase 35 — Import of COROS trainings and the heart rate in the video: manual test plan

Decisions: DECISIONS.md D050 (the phase) and D049 (the training in the project, the time model, the format). Record of
the phase: `docs/PHASE_COROS.md`. Scope: importing a COROS FIT training, saving it with the project, aligning it with
the videos by their recording time, and showing the heart rate in the video (Preview = export).

Environment: the build of the Phase 35 branch; the real session of 27.09.2026 — `Болдеринг20260927100239.fit` and the 16
videos `video_20260927_HHMMSS.mp4`; Russian interface unless a row says otherwise; FFmpeg / FFprobe available unless a row
says otherwise.

Legend: **PASS** — the criterion is met; **FAIL** — a defect; **PARTIAL** — part met, the rest recorded; **NOT RUN** —
not run, with a reason.

Time bases used below (D049 §1): **W** — watch time (UTC by the watch), **C** — camera time (UTC by the phone), **S** —
source time of a video. `ClockCorrection = W − C`.

## A. Step 35.2 — the import into the session — **done 2026-10-29: A1–A11 PASS**

The rows and the result are recorded in `docs/PHASE_COROS.md` §9. A8's expectation («no trace in `project.json`») holds
for Step 35.2's version only; from Step 35.4 the training is saved (part C).

## B. Step 35.3 — recording time

| # | Check | Expected result | Evidence |
|---|---|---|---|
| B1 | Import the 16 videos of 27.09 into a new project | every online video is probed once per session; its metadata holds the raw `creation_time` tag and its origin (container format or video stream) | |
| B2 | Inspect the zero state of the 16 videos before Step 35.3's rule is approved | every zero not set by hand is «unknown»; no heart rate there; the Inspector names the reason and offers the manual override | |
| B3 | For each of the 13 filmed climbs C2–C14 (C1 is not filmed): the visible start of the climb (the feet leave the floor) against its split start | a table of C ↔ W differences; the measurement report proposes the rule, its tolerance `τ` and its device scope (B12) | |
| B4 | A video without `creation_time` (e.g. a re-encoded copy) | the probe records «no tag»; the zero stays «unknown»; no error | |
| B4a | A video whose tag has no zone designator or another layout (prepared copy) | the tag is kept verbatim; it is «unrecognised», never read as UTC; the zero stays «unknown» | |
| B5 | Open an older project (no `recordingTimeProbe`) with online videos; wait until the background probe ends | every online video probed (log); the title shows no unsaved changes; closing asks nothing; no Undo step | |
| B5a | In the same session: switch to another window and back (re-check), wait | no second probe of any video (log) | |
| B5b | Close without saving; open the project again | the probe runs again for the same videos (log) — the accepted compromise (D049 §4, M2) | |
| B5c | Save (with any other change), close, open again | no probe; `project.json` holds `recordingTimeProbe` for every video | |
| B6 | Make one video unreadable for FFprobe (e.g. a truncated copy), open the project | one failed attempt in the log, no marker, no retry on re-check or window activation, the project not dirty; attempted again after the next Open | |
| B6a | A video missing at Open; bring the file back; activate the window | exactly one probe of that video; bringing it back a second time in the session → no further probe | |
| B6b | FFprobe unavailable; open a project with unprobed videos | no attempt, no marker; nothing recorded as attempted | |

### B′. Step 35.3 — the research window and the measurement (D050 amendment B5–B9; sub-steps 35.3c–e)

The fixed set: the folder `Боулдер 27.09.2026` (16 videos, the FIT with 14 climbs), frozen before the evaluation; the
groups of `docs/PHASE_COROS.md` §10. `τ = 2 s` (working value). The study file is never committed.

| # | Check | Expected result | Evidence |
|---|---|---|---|
| B7 | Debug build: open «Проверка синхронизации COROS»; Release build: look for it | Debug — the window opens on the W axis with the whole training (14 numbered climb intervals, the rests, the pause) and one lane per video; Release — no such command | |
| B8 | Use the window (place, mark, match, export), then close it and look at the title bar, Undo and `project.json` | no unsaved changes, no Undo step, `project.json` unchanged; the marks and matches are in the study file only | |
| B9 | A video with an unknown zero; a video placed by the evaluated zero | the first listed apart, not on the axis; the second placed by `w = Z + s + ClockCorrection`, never moved by hand | |
| B10 | Check flow on one climb: pick it, open its video at the predicted frame, step to the frame where the feet leave the floor, mark it, confirm | Δ shown in ms and frames; the match «confirmed»; a rejection leaves the case «rejected»; a case without a climb stays «not matched», unshifted, in «Несопоставленные попытки» | |
| B11 | Step 35.3d with the tool of 35.3b2d (no Debug window panel): `AIVE_COROS_EVALUATE_STUDY` = the frozen study, run `EvaluateReferenceStudy` (`tests/Telemetry.LocalTests`); look at the output, the study and `<study>.eval-<seq>.json` / `.csv` | the whole set hashed first; a new evaluation record (algorithm version, parameters, inputs hash, revision hash, report hash); the estimate (median, MAD, count), each climb's leave-one-out residual in ms, the drift and the outliers shown separately; all 13 climbs C2–C14 with their status; only confirmed matches computed; the tag check on the 7 videos per file, outside the verdict; an existing export never overwritten; a run with a problem writes nothing | |
| B12 | The measurement report on the fixed set, then the owner's explanations with `ExplainReferenceStudy` (`AIVE_COROS_EXPLAIN_STUDY`, `AIVE_COROS_EXPLANATIONS`) | (1) ≥ 11 of the 13 climbs C2–C14 within `τ`; (2) every one of the 13 explained by its family (a misfit refused at recording, «unexplained» never counted); (3) no drift beyond `τ / 2` («not established» is not met); the verdict as computed — a negative result recorded as it is; `103026` and `104651` analysed; the product owner's decision recorded | |
| B13 | After the rule's approval (35.3e): the zero state of the 16 videos | «automatic» exactly where the rule's conditions hold, else «override» / «unknown»; the window and the Inspector give the same zero for each video (one estimator) | |

### B″. Step 35.3R1 — the reference-marking tool (D050 amendment of 2026-11-02)

A Debug build; the project of the set of 27.09 and a study file of it outside the repository (never committed). The
marking rule of `docs/PHASE_COROS.md` §10. The rows check the tool, not the marking itself (that is 35.3R2).

| # | Check | Expected result | Evidence |
|---|---|---|---|
| R1 | Start the Debug build with `--open-project <set folder> --coros-reference <study file>`; then once without a path, once with a missing file, once with a damaged file; start a Release build with the same arguments | the window «Проверка синхронизации COROS» opens beside the main window with the study; without a path or with a bad file a clear refusal, no study created; Release — no such window | |
| R2 | Look at the window | the 16 videos with their coverage and marks; all climbs C1–C14 with no hint from file names or times; C1 marked «нет на видео, вне B6»; the marking rule (start and end) shown | |
| R3 | A study without a marking rule | marking blocked until the rule is set; setting it is journalled | |
| R4 | Open a video from the window | it opens in the ordinary Source viewer (after its SHA-256 check the first time, with progress); a video whose path or size does not match the project, or that fails the check, is blocked with the reason | |
| R5 | Step through the video with the Source viewer's own keys; press «Начало подъёма здесь», later «Конец подъёма здесь» | the mark keeps the Source position of each press; an end earlier than the start or without a mark is refused | |
| R6 | Move the start, then the end, separately; «Убрать конец» | each boundary changes alone; the journal holds every move | |
| R7 | «Перейти к началу» / «Перейти к концу» of a stored mark | the Source viewer shows that position | |
| R8 | Confirm a climb for the mark (manual); then try to confirm the same climb for another mark | the first is confirmed and highlighted in the list; the second is refused, naming the mark that holds the climb | |
| R9 | Move the start of a confirmed mark | the confirmation stays; the move is journalled; the mark's history shows the old and the new time and the climb | |
| R10 | Change the video file on disk (e.g. touch it), then try to mark | marking blocked until the video is checked again | |
| R11 | Mark, save, close, reopen with the same arguments | marks, decisions and coverage are there; the project shows no unsaved changes; In / Out and the timeline unchanged | |
| R12 | Freeze | the whole set is checked first; the frozen revision holds the marking rule | |

## C. Step 35.4 — the training in the project and the alignment

| # | Check | Expected result | Evidence |
|---|---|---|---|
| C1 | Import the training, Save, close, Open | the training is there (tooltip / Inspector); the heart rate is available for aligned clips | |
| C2 | Import, Save, end the app from Task Manager, start it, Recover | the training, the clock correction and the overrides are there | |
| C3 | Rename the FIT, Open | «файл тренировки не найден»; editing and an export without an HR overlay work as usual | |
| C4 | Put the file back, activate the window | the training is available again without reopening | |
| C5 | Replace the FIT with a new export of the same activity from the COROS app | «файл изменён»; the data is used; the new size / hash saved on the next save | |
| C6 | Import another training while one is attached | a confirmation; after it the new training is attached and the clock correction is 0; overrides unchanged; no Undo step | |
| C6a | Point the training to another activity's FIT (relink) | the relink asks for confirmation (the correction may not fit) | |
| C7 | Change the training's clock correction (COROS training drop-down) by ±1 frame | the readout «Тренировка в позиции плейхеда» of every aligned video moves by one frame (the overridden ones too); one Undo step; Redo | |
| C7a | Sign: set the correction to +2 s | a frame filmed at C 10:00:00 shows the watch's sample of W 10:00:02 | |
| C8 | In the Inspector of `video_20260927_103026.mp4`: «Точка привязки» shows «Конец подъёма» (the default); playhead on the frame of the last touch of the wall at the end of climb 5, climb 5 picked, «Совместить кадр с концом подъёма» (D050 amendment of 2026-11-02 to B2) | that video aligned: the readout at that frame shows the climb's end in the FIT less 4.6 s (10:31:53.5 − 4.6 s → «10:31:48» on the watch); the climb list shows «Подъём 5 · 10:31:21–10:31:53»; one Undo step; Redo; the training's clock correction unchanged | |
| C8b | The same video: «Точка привязки» → «Начало подъёма»; playhead on the frame where the last foot leaves the floor at the start of climb 5; «Совместить кадр с началом подъёма» | the earlier behaviour: the readout at that frame shows the climb's start (10:31:21); the zero replaced; one Undo step | |
| C8c | After C8: switch «Точка привязки» back and forth without pressing the button; then match by the end again on the same frame as in C8 | switching changes nothing (readout, «Начало записи», Undo history unchanged); the new match gives exactly the readout of C8 — the 4.6 s never accumulate; Undo steps back through the matches one by one | |
| C8a | Move the playhead over an aligned and an unaligned clip | the readout follows the playhead: watch time · BPM, or the reason (no training / FIT not available / recording start unknown / outside the recording / no sample) | |
| C9 | Split, trim, move, slip and speed 2× a clip; duplicate it | the watch time of a given picture frame is unchanged; the duplicate shows the same heart rate for the same picture | |
| C10 | Open a v10 project | opens; Save writes the new version; an older build refuses the saved file | |
| C11 | Remove the training | the training and its clock correction are gone; the videos' overrides are kept; no Undo step | |
| C12 | Change the Windows time zone, restart, open the project | identical mapping and identical heart-rate values | |
| C13 | Move an overridden video's file, Open, relink to it | the override is kept (size, duration and tag equal) without a question; Undo of the relink restores the previous state | |
| C14 | Relink an overridden video to a different video | the relink asks «Сохранить поправку начала записи для нового файла?», default «нет»; «нет» clears the override; Undo restores it | |

## D. Step 35.5 — the heart-rate overlay

| # | Check | Expected result | Evidence |
|---|---|---|---|
| D1 | HR overlay over an aligned clip; play | the BPM changes once per second; during the timer's pause nothing is shown | PASS — owner, 2026-11-03 (`593b6a2`; acceptance О3, О6) |
| D2 | Compare the Preview with the export at 5 chosen frames | equal | PASS — owner, 2026-11-03 (acceptance О13: the shown values, corner, look and colour; byte equality by `ExportHeartRateEndToEndTests`) |
| D3 | FIT missing + HR overlay; Export | a warning naming the reason; the export starts only after confirmation; Cancel exports nothing | PASS — owner, 2026-11-03 (acceptance О14, О15): the warning naming the reason, Cancel exports nothing, the overlay back once the file is back; «Продолжить» (an export after the confirmation) not pressed manually |
| D4 | HR overlay over a clip with an unknown zero; Export | nothing shown there in the Preview and the export; the warning names the clips | PARTIAL — owner, 2026-11-03: nothing in the Preview after «Сбросить», restored by Undo (acceptance О11); the export warning naming the clips not run manually (optional Д1) — covered by `HeartRatePreflightTests` |
| D5 | A project without an HR overlay, FIT missing; Export | no warning about the heart rate | NOT RUN (optional Д5) — covered by `HeartRatePreflightTests` |
| D6 | «Шаблон ▾ → Пульс» with the playhead on an aligned clip on V1 (the overlay goes on V2) | the overlay covers the rest of that clip; the Inspector shows «Вид», «Положение», «Цвет сердца» and the hint; each click is one Undo step and shows in the Preview at once | PASS — owner, 2026-11-03 (acceptance О1, О2, О7–О10) |
| D7 | Change the video's zero (Inspector «Совместить кадр…»), then Undo; remove the training, then import it again | the BPM in the Preview follows each change at the same frame; without the training nothing is drawn | PARTIAL — owner, 2026-11-03: «Сбросить» of the zero and its Undo followed at once with the overlay present (О11); a new zero by «Совместить кадр…» with the overlay present, and removing and importing the training again, not run (optional Д1, Д4) |

## E. Step 35.6 — the real session end to end

| # | Check | Expected result | Evidence |
|---|---|---|---|
| E1 | The 27.09 session: 16 videos, the training, the alignment, the HR overlay, export | the heart rate matches the climbs; the product owner's acceptance | |
