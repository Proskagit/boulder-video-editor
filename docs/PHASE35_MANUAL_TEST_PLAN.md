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
| B2 | Inspect the zero state of the 16 videos before Step 35.3's rule is approved | every zero is «unknown»; no heart rate anywhere; the Inspector names the reason and offers the manual override | |
| B3 | For each of the 14 climbs on video: the visible start of the climb against its split start | a table of C ↔ W differences; the measurement report proposes the rule, its tolerance `τ` and its device scope | |
| B4 | A video without `creation_time` (e.g. a re-encoded copy) | the probe records «no tag»; the zero stays «unknown»; no error | |
| B4a | A video whose tag has no zone designator or another layout (prepared copy) | the tag is kept verbatim; it is «unrecognised», never read as UTC; the zero stays «unknown» | |
| B5 | Open an older project (no `recordingTimeProbe`) with online videos; wait until the background probe ends | every online video probed (log); the title shows no unsaved changes; closing asks nothing; no Undo step | |
| B5a | In the same session: switch to another window and back (re-check), wait | no second probe of any video (log) | |
| B5b | Close without saving; open the project again | the probe runs again for the same videos (log) — the accepted compromise (D049 §4, M2) | |
| B5c | Save (with any other change), close, open again | no probe; `project.json` holds `recordingTimeProbe` for every video | |
| B6 | Make one video unreadable for FFprobe (e.g. a truncated copy), open the project | one failed attempt in the log, no marker, no retry on re-check or window activation, the project not dirty; attempted again after the next Open | |
| B6a | A video missing at Open; bring the file back; activate the window | exactly one probe of that video; bringing it back a second time in the session → no further probe | |
| B6b | FFprobe unavailable; open a project with unprobed videos | no attempt, no marker; nothing recorded as attempted | |

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
| C7 | Change the clock correction by ±1 frame | the mapping of every aligned video moves by one frame (the overridden ones too); one Undo step; Redo | |
| C7a | Sign: set the correction to +2 s | a frame filmed at C 10:00:00 shows the watch's sample of W 10:00:02 | |
| C8 | Set the zero override of `video_20260927_103026.mp4` | that video aligned; one Undo step; Redo | |
| C9 | Split, trim, move, slip and speed 2× a clip; duplicate it | the watch time of a given picture frame is unchanged; the duplicate shows the same heart rate for the same picture | |
| C10 | Open a v10 project | opens; Save writes the new version; an older build refuses the saved file | |
| C11 | Remove the training | the training and its clock correction are gone; the videos' overrides are kept; no Undo step | |
| C12 | Change the Windows time zone, restart, open the project | identical mapping and identical heart-rate values | |
| C13 | Move an overridden video's file, Open, relink to it | the override is kept (size, duration and tag equal) without a question; Undo of the relink restores the previous state | |
| C14 | Relink an overridden video to a different video | the relink asks «Сохранить поправку начала записи для нового файла?», default «нет»; «нет» clears the override; Undo restores it | |

## D. Step 35.5 — the heart-rate overlay

| # | Check | Expected result | Evidence |
|---|---|---|---|
| D1 | HR overlay over an aligned clip; play | the BPM changes once per second; during the timer's pause nothing is shown | |
| D2 | Compare the Preview with the export at 5 chosen frames | equal | |
| D3 | FIT missing + HR overlay; Export | a warning naming the reason; the export starts only after confirmation; Cancel exports nothing | |
| D4 | HR overlay over a clip with an unknown zero; Export | nothing shown there in the Preview and the export; the warning names the clips | |
| D5 | A project without an HR overlay, FIT missing; Export | no warning about the heart rate | |

## E. Step 35.6 — the real session end to end

| # | Check | Expected result | Evidence |
|---|---|---|---|
| E1 | The 27.09 session: 16 videos, the training, the alignment, the HR overlay, export | the heart rate matches the climbs; the product owner's acceptance | |
