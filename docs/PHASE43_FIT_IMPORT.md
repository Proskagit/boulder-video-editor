# Phase 43 — FIT import from any watch (COROS, Garmin, others)

*Decision: DECISIONS.md **D062** (2026-11-16). Branch `feat/fit-import-garmin`. No `project.json` change. The phase is complete
only when merged into `main` and accepted by the product owner after the manual check of §5.*

## 1. The task

The owner (2026-11-16): a friend's Garmin FIT of an indoor-climbing training, with the Garmin Connect screenshots of the same
day. Support COROS and Garmin; do not hard-wire a check for one maker; use what the FIT contains, and when it has nothing
usable, import nothing. The button becomes «Импорт FIT-тренировки…». The editor should read the routes correctly and put them
on the overlay and the video.

## 2. Research — the three real files

Read with an independent stdlib Python FIT decoder and the app's parser; the field meanings from the Garmin FIT SDK profile
21.221 (`garmin/fit-javascript-sdk`, `src/profile.js`).

| | COROS APEX 4 (2026-09-27) | COROS APEX 4 (2026-08-08) | Garmin Venu X1 (2026-11-14) |
|---|---|---|---|
| Header | protocol 2.0, profile 21.158 | the same | protocol 1.0, profile 21.213 |
| `file_id` | manufacturer 294, product 814, device name | the same | manufacturer 1, product 4603 (`venuX1`), **no name** |
| Messages | 10 types, ~5.8 k messages | 10 types | ~30 types, ~13.9 k (many undocumented: 233, 534 ×5 k, battery, settings, user profile) |
| Sport | 31 / 69 bouldering | 31 / 69 | 31 / **68 indoor climbing** |
| `session.timestamp` | the end | the end | **the start** (= `start_time`); the end only as start + elapsed |
| `activity` | timestamp = end, local = +3 h | the same | timestamp = start, local = +3 h |
| Records | 1 s, heart rate in field 3 | 1 s | **only on a change**: 1–19 s apart (2 615 × 1 s, 414 × 2 s, … one 19 s) |
| Timer events | start, stop, start, stop (one 28 s pause) | start, stop | start, stop_all |
| Splits 312 | 9 climb / 10 rest; 15 / 16 HR | the same | the same |
| Split 69 scale / 70 grade | absent | **9 Font**, 9 / 11 / 13 = 6B / 6C / 7A | **2 French**, 11 / 13 = 6b / 6c |
| 71 status / 72 falls / 73 send | 71 in 9 climbs (6 × 3, 3 × 2), 72 = 0 | 71 = 2 / 3, 72 = 0 | 71 = 2 / 3, 72 = 0–4, 73 = 1 in 9 climbs |

**Garmin against Garmin Connect** (the owner's screenshots): 16 routes — 16 climb splits; 2 attempted — two 71 = 2; 14
completed — fourteen 71 = 3; 9 clean sends — nine 73 = 1; 12 falls — Σ 72 = 12; grades 6b 1 attempted / 4 with falls / 9
sent and 6c 1 attempted / 1 with falls — exactly the 70 = 11 / 13 splits with those results; max 6c; total 1:26:00 = the
session's elapsed 5 160.292 s; climbing time 15:32 = Σ climb elapsed; average 114 / maximum 169 bpm = the session's 16 / 17.
Every number matches — the SDK's meanings hold.

**What the old parser did with the Garmin file**: refused it («записана не часами COROS»); with that check removed, the end
was the start (an empty recording, no heart rate anywhere, the training's identity wrong). With the 1 s parser's 2 s hold the
heart rate would blink off in every silence of more than 2 s (≈ 1 / 4 of the record steps).

## 3. What is implemented (D062)

1. `FitWorkoutParser` (formerly `CorosWorkoutParser`): one reader for every maker; `FitVendor` recognised from the
   manufacturer; the refusals of D062 §1 (`NotCoros` removed, `NoHeartRate` → `NoTrainingData` «ни пульса, ни подъёмов»).
2. The end by D062 §2; the recording without a session from the records and the timer events.
3. `ClimbRoute` on each climb (fields 69–73), its grade text (`ClimbGrades`) and its overlay status.
4. A green area made from the FIT gets the grade and the status (Топ / Попытка) from the file (D062 §3).
5. `ClimbingWorkout.HeartRateHold` — 2 s, 20 s for a smart recording (D062 §4).
6. Texts: «Импорт FIT-тренировки…», «FIT-тренировка» (D062 §5).

Tests: `FitWorkoutParserTests` (the maker recognised for 1 / 294 / other, Garmin's end from the elapsed time, a session
stamped with its end, no session, no heart rate with climbs, neither, smart recording, the route fields and values outside
their enums — on a synthetic `GarminFixture` shaped like the real file), `ClimbRouteTests` (grade texts, status mapping),
`FitAutoBindingTests` (the grade and the status on a new area, an area without a recorded route), and on the real files
`Telemetry.LocalTests/RealGarminTests` (outside the solution; the Garmin file is not committed — `AIVE_GARMIN_FIT`).

## 4. Limits (D062 §6)

- Falls and sends are read but not shown: the overlay's status has only «Топ» / «Попытка».
- The device: the summary says «Garmin» (the file has no name); no product-name table.
- The first session of a multi-session FIT only, as before.
- British, Brazilian, Saxon and YDS ± grades are not spelled — such a route gets no grade.
- COROS 2026-09-27: status in 9 of 14 climbs, no grade; its «completed» (6) differs from the app's «onsight» (5).

## 5. Manual check (the owner)

| # | Steps | Expected |
|---|---|---|
| G1 | Toolbar | The button reads «Импорт FIT-тренировки…»; its tooltip names COROS, Garmin and other watches |
| G2 | Import the Garmin FIT (`25076394986_ACTIVITY.fit`) into a new project | Accepted; the status bar: «Garmin, 14.11.2026 15:31–16:57 (UTC+03:00), 16 подъёмов» |
| G3 | Import the COROS FIT of 27.09.2026 into the D053 project | Same summary as before (APEX 4, 10:02–11:39, 14 подъёмов); the saved training still «available» on reopening |
| G4 | COROS 27.09: «Распознать прохождения из FIT» on a clip with a climb | Areas as before; climbs with a status in the file get «Топ» / «Попытка» and the lilac; no grade (none in the file) |
| G5 | COROS 08.08 (if videos exist): the same | Areas get Font grades «6B» / «6C» / «7A» |
| G6 | Garmin with a video of the training (if any): heart-rate overlay | The BPM shows continuously (no blinking between Garmin's sparse samples), nothing in pauses or outside the training |
| G7 | Change a FIT area's grade / status by hand, run «Распознать…» again | Nothing changes (areas made earlier are never touched) |
| G8 | A FIT of another sport without splits but with heart rate | Accepted, 0 подъёмов; a FIT with neither: «в тренировке нет ни пульса, ни подъёмов.» |
| G9 | Settings → «FIT-тренировки» | The section's title and the heart-rate offset work as before |
