# Roadmap

The canonical phase list, with per-phase scope, is `docs/DEVELOPMENT_PLAN.md`.
This file summarizes status only.

## Completed

### Phase 0 — Architecture
Commit `0ccc9be`.

### Phase 1 — Basic UI skeleton
Commit `88a608b`. Five panels (Toolbar, Media Browser, Preview, Timeline,
Inspector) laid out with mock data.

### Phase 2 — Project state and real media import
Commit `e7a8ef0`.

Known verified behavior:
- media import works
- multiple files can be added
- New clears the current project and the undo/redo stack
- unsupported files are blocked by file filters and by extension validation
- duplicates (same path) are skipped

### Phase 3 — Media analysis foundation
Commit `a8e5bac`.

Known verified behavior:
- ffprobe is located (configured `Ffmpeg:FfprobePath` or PATH)
- media metadata is obtained in the background after import
- the previous "FFprobe could not be found" blocker was resolved

Only ffprobe is integrated. ffmpeg itself (thumbnails, decode, export) is not
used yet.

### Phase 4 — Timeline
Commit `33c5c02`, merged into `main`. Tracks, clips, selection, move, trim, split, delete,
playhead, zoom, snapping — all as undoable commands. Decisions: DECISIONS.md D006–D008.

### Phase 5 — Preview
Branch `feat/phase-5-playback`: video checkpoint `85ca216`, audio in the Phase 5 closeout
commit. Timeline playback in the Preview with video (exact source-frame selection, ffmpeg
decoder) and audio (ffmpeg decode, mixer, WASAPI output as master clock), UI transport and
playhead ↔ seek. Manually validated by the product owner (video and audio). Decisions:
DECISIONS.md D009–D013. Details and verification: `progress.md`.

### Phase 6 — Project persistence
Branch `feat/phase-6-project-persistence` (from `acc1a49`), Phase 6 commit. Project folder
with `project.json` (format v1, DTOs separate from entities, `MediaTime` as long ticks);
Open with full validation before the current project is replaced; atomic Save / Save As;
undo save point for dirty tracking; missing media opens as offline (not dirty, not probed);
saved ffprobe metadata reused; autosave every 2 min into a separate recovery file, startup
recovery offer (Recover / Discard / Not now); Save / Don't Save / Cancel before New, Open and
Close; window title with `*`; Ctrl+N / O / S / Shift+S. UI manually validated by the product
owner. Decisions: DECISIONS.md D014–D016. Details and verification: `progress.md`.

The open question about playhead / zoom / snapping was decided at the start of Phase 7:
session state (D015).

## Current

Phase 25 — technical backlog and small UX improvements (**in progress**; DECISIONS.md D040, 2026-10-19; branch
`feat/phase-25-backlog-ux` from `d3df0ba`, the merge of PR #25). `appsettings.json` read from the application's folder
(FU-24.2), the marker navigation status (FU-21.1), the hold markup mode left on its own (FU-22.1), sliders for Fade In / Out
and the hold line width (FU-24.1 in part), icons on the timeline's tool bar, the Phase 24 manual gaps; `project.json` stays
v6. Steps: `docs/DEVELOPMENT_PLAN.md`, "Phase 25 — Technical backlog and small UX improvements: steps"; manual plan
`docs/PHASE25_MANUAL_TEST_PLAN.md`.

## Previous

Phase 24 — Russian localization and interface polish (**merged**; PR #25 merged into `main` as `d3df0ba` on 2026-10-19, CI
green on the pull request and on `main`; DECISIONS.md D039, 2026-10-19; branch `feat/phase-24-localization` from `c652667`).
The whole interface in Russian — English the neutral language of the resources, Russian chosen by `App:Language` in
`appsettings.json`, no switch in the interface — including the service layer's messages; guard tests against untranslated
text; the layout checked at 1024 × 640 and 1440 × 900; vector icons for the frequent commands; sliders for Opacity and
Volume; `project.json` stays v6. Steps 24.0–24.8 done (verified by Claude 2026-10-19: Release / Debug `--no-incremental
-warnaserror` 0 / 0, the full suite green, `project.json` v6 byte for byte with `c652667`, the manual plan run in the real
app at 1024 and 1440 px with the parts not reached marked "auto only"); **accepted by the product owner on 2026-10-19**.

Phase 23 — direct Move, Scale and Crop in the Preview (**merged**; PR #24 merged into `main` as `c652667` on 2026-10-19;
DECISIONS.md D038, 2026-10-16; branch `feat/phase-23-preview-transform` from `1aaedc8`, the merge of PR #23). Two tools in
the Preview's bar: Move (drag the selected clip; its frame's corner handles scale it proportionally around its centre) and
Crop (eight handles; anchored — the visible picture stays in place, the scale and position compensate); a click selects the
topmost layer; one undo step per gesture, Esc cancels it without a trace; only while paused, never during an export; the
Inspector's crop unchanged; `project.json` stays v6. Steps 23.0–23.7 done (verified by Claude 2026-10-19: gates green, the
manual plan run in the real app — 23 / 24 PASS, row 23 NOT RUN); **accepted by the product owner on 2026-10-19 with the
status PASS WITH NOTES**.

Phase 22 — timeline selection, snapping and making room (**merged**; PR #23 merged into `main` as `1aaedc8` on 2026-10-16; DECISIONS.md D037, 2026-10-15; branch
`feat/phase-22-timeline-ux` from `7318ba8`, the merge of PR #22). Ctrl+A, Esc, a marquee and "1 of N selected"; a magnet
that never makes an overlap, also for a drop from the Media Browser (a new clip on an empty track at 0); the placement
edits (move, nudge, drop, Paste, Duplicate, + Text) push the clips on the right by the overlap instead of being refused —
on one track, a partner follows or the whole edit is refused, a dissolve of untouched clips never opened, one undo step; a
collision on the left still refused; Alt+arrow nudges; auto-scroll while dragging; `project.json` stays v6. Steps
22.0–22.6 done (closeout by Claude 2026-10-16: gates green, the manual plan run in the real app, all rows PASS); **accepted
by the product owner on 2026-10-16**. Steps:
`docs/DEVELOPMENT_PLAN.md`, "Phase 22 — Timeline selection, snapping and making room: steps"; manual plan
`docs/PHASE22_MANUAL_TEST_PLAN.md`.

Phase 21 — animation of the hold outlines (**merged**; PR #22 merged into `main` as `7318ba8` on 2026-10-15; accepted 2026-10-15 — main functionality checked by the product owner in the
real app; DECISIONS.md D036, 2026-10-15; branch `feat/phase-21-hold-animation` from
`a657bff`). With "Animate" on, a clip's hold
contours are hidden before a start bound to the source time, appear one by one (Bottom to top by default, or the marking
order) every Interval with an appear fade, stay, and disappear together with a fade after a set time — or stay to the
clip's end; computed on timeline frames in `PlaybackSnapshot.LayersAt`, so the Preview and the export agree on every
frame; one undo step per change (one setting merges); `project.json` v6 (v5 read without animation); in the markup mode
every contour is outlined dashed so hidden holds stay editable (D035 unchanged). Steps: `docs/DEVELOPMENT_PLAN.md`,
"Phase 21 — Animation of the hold outlines: steps"; manual plan `docs/PHASE21_MANUAL_TEST_PLAN.md`.

Phase 20 — manual correction of hold contours (**merged**; PR #21 merged into `main` as `a657bff` on 2026-10-15; DECISIONS.md
D035): in the markup mode a selected hold shows handles — drag, Alt+click to add, Delete / double-click to remove (at least
three), no self-crossing; one undo step per gesture; `project.json` stayed v5.

Phase 19 — AI hold markup (**merged**; PR #20 merged into `main` as `2d0a10b` on 2026-10-15, CI green; DECISIONS.md D034,
specification revision 5). On a paused frame of a video clip the user clicks holds; SAM 2.1 Large (pinned model
`sam2.1-large-v1`, ONNX, local only, DirectML with a CPU fallback) returns their real contours; confirmed holds are drawn
over the clip for its whole duration as one route outline, identical in the Preview and the export; `project.json` v5.
Criterion 10 on the E1 set: 156/164. Steps: `docs/DEVELOPMENT_PLAN.md`, "Phase 19 — AI hold markup: steps"; manual plan
`docs/PHASE19_MANUAL_TEST_PLAN.md`.

Phase 18 — HDR → SDR of phone footage (**complete**; PR #19 merged into `main` as `c29a767` on 2026-10-14, CI green;
still open: the product owner's visual check of the S1 export and PQ on real PQ footage; DECISIONS.md D033 with Revision R1, product owner decisions of 2026-10-13 / 14). HDR video —
HLG and PQ, BT.2020, 10 bit; Dolby Vision 8.4 and HDR Vivid by their HLG base layer — converted to the editor's SDR
picture by one exact rule — S1 since Revision R1: scene light (PQ through the inverse BT.2100 OOTF), BT.2020 → BT.709,
gamut compression, exposure, mobius on max RGB, the BT.709 OETF — as an application-generated 65³ LUT, the same for the Preview, the thumbnails and the export; SDR media decoded
byte for byte as before; colour metadata read and kept as optional properties (`project.json` stays v4). Step 18.1
(pre-flight: the product owner's 13 HDR files measured, tone-mapping variants compared, the zimg OOTF finding) and Step
18.2 (sync after the merge, D033, the steps, the manual plan's skeleton) done on 2026-10-13; the implementation
(18.3–18.8) approved by the product owner on 2026-10-13 and done: closeout Step 18.8 on 2026-10-13 — the gates green
(at `ff57787`: passed 3150, skipped 2, failed 0; heavy 3152 / 0 / 0; three blame-hang runs clean; on the final
`9c8785e`: build -warnaserror Release / Debug 0 / 0, passed 3151, skipped 2, failed 0), the real-app run with the product
owner's footage, the regression against the Phase 17 build (an SDR project's default export byte for byte); the product
owner's visual check of the L2 export **failed** (too bright, washed out) → D033 Revision R1 (2026-10-13 / 14): the rule
S1 chosen on a phone-made SDR reference, Steps 18.9–18.12 done (S1 in Core, the HLG criteria refined after the LUT
accuracy study, thumbnails and LUTs keyed by the colour rule, the real footage, closeout: build -warnaserror Release / Debug 0 / 0; passed 3196, skipped 2, failed 0; three blame-hang runs the same; heavy 3198 / 0 / 0; git diff --check clean). HLG
measured on the product owner's 13 clips; PQ not verified on real PQ footage; the product owner's visual check of the S1
export open. Steps and acceptance criteria: `docs/DEVELOPMENT_PLAN.md`, "Phase 18 — HDR →
SDR of phone footage: steps"; manual plan `docs/PHASE18_MANUAL_TEST_PLAN.md`.

Phase 17 — Audio editing, branch `feat/phase-17-audio-editing` (from `79e4e78`, `main` after the merge of PR #17). Scope
(product owner, 2026-10-08, DECISIONS.md D032 — variant A of the Phase 17 pre-flight, the D side of D031 §3): Unlink Audio
(a video clip's sound becomes an independent `AudioClip` of the same asset), Link (an explicit link of a video clip and an
audio clip, edited as one where an edit touches both), J / L cuts, sound-only Insert / Overwrite from the Source viewer,
audio split / trim / fades, audio crossfades on audio tracks (1 s by default, linear, limited by the handles), the ripple of
linked clips; every edit one undo step, refused as a whole when a locked partner would change; Preview ↔ export parity;
`project.json` **v4** (v3 opens unchanged in meaning and is saved as v4; no v4 → v3). The base model stays D008's
`VideoClip` with its own sound. Out: ducking, keyframes, speed ramps, waveform work, a mixer, volume automation, a separate
stabilization phase. Step 17.1 (pre-flight) and Step 17.2 (sync after the merge, D032, the Phase 17 steps, the manual
plan's skeleton) done; AQ1–AQ13 answered as proposed; Steps 17.3 (model and `project.json` v4), 17.4 (Unlink Audio, Link,
Unlink), 17.5 (linked editing), 17.6 (audio crossfades), 17.7 (sound-only Insert / Overwrite) and 17.8 (UI) done. **Phase 17
complete** on its branch: Step 17.9 closeout on 2026-10-12 — the real-app run (one layout defect found and fixed: the
toolbar at 1024 px), the regression against the Phase 16 build (a Phase 16 save saved again differs only in
`formatVersion`, the default export byte for byte, a v4 file refused by Phase 16), the gates green (passed 3030, skipped
2, failed 0; heavy 3031 / 0 / 0; after the independent review's test fixes and the product owner's Ripple Delete change of
2026-10-12: a linked pair closes both tracks by its target clip's length). R10 (a real audio device): an open manual check,
not blocking the pull request. Accepted; PR #18 merged into `main` as `fa96468` (2026-10-13, its tree
identical to `1564c02`), CI green (checked by the product owner). Steps and
acceptance criteria: `docs/DEVELOPMENT_PLAN.md`, "Phase 17 — Audio editing: steps"; manual plan
`docs/PHASE17_MANUAL_TEST_PLAN.md`.

Phase 16 — Source viewer & three-point editing, branch `feat/phase-16-source-viewer` (from `1bdeb95`, `main` after the
merge of PR #16). Scope (product owner, 2026-10-08, DECISIONS.md D031): a Source mode of the Preview panel playing one
video or audio asset, a source In / Out as unsaved session state, Insert (`,`) and Overwrite (`.`) of the source range at
the timeline playhead on the target track (ripple of the target track only), one undo step each; picture and sound of a
video file stay one clip — unlinking, J / L cuts and audio crossfades stay out (the A ↔ D boundary). `project.json` stays
v3. **Phase 16 complete** on its branch: Steps 16.1–16.6 done (`392db82`, `cb50435`, `22d2ef4`), Step 16.7 closeout on
2026-10-08 — the real-app run (one focus defect found and fixed, `ac0e3ac`), the regression against the Phase 15 build
(a Phase 15 save opened and saved byte for byte, the default export byte for byte), an independent review and its
corrections (`2216865`), the gates green (passed 2894, skipped 2, failed 0 after the second review's test). Accepted; PR
#17 merged into `main` as `79e4e78` (2026-10-08), CI green on the pull request (run 37776542826) and on `main` (run
37778354822).
Steps and acceptance criteria: `docs/DEVELOPMENT_PLAN.md`, "Phase 16 — Source viewer & three-point editing: steps"; manual
plan `docs/PHASE16_MANUAL_TEST_PLAN.md`.

Phase 15 — Editing tools, branch `feat/phase-15-editing-tools` (from `7200976`, `main` after the merge of PR #14). Scope
(product owner decisions of 2026-10-07, DECISIONS.md D030): track controls — mute, hide, lock (15.3); trim to the
playhead, plain and ripple, `Q` / `W` (15.4); ripple trim by dragging an edge (15.5); slip (15.6); a timeline In / Out
range — `I` / `O`, loop over the range, range export — as **unsaved session state** (15.7); a new text clip's
`FontSize = 48 × canvasHeight / 1080` and the audio status reset after the sound returns (15.8); closeout (15.9).
`project.json` stays v3 with no new property. Steps 15.1 (pre-analysis) and 15.2 (sync after the merge, scope
formalization, `d1296be`) accepted (2026-10-07); Step 15.3 (track controls: mute / hide / lock in the track header, one
undo step each, mute / hide allowed on a locked track — Q13, `1d26165`) accepted; Step 15.4 (trim to the playhead: Q / W,
Shift+Q / Shift+W ripple; dissolves kept — Q1, Q2, Q4, Q5, Q6, `f122c9a`) accepted; Step 15.5 (ripple trim by Shift +
edge drag, the same planner as Shift+Q / Shift+W — Q3, `efecd24`) accepted; Step 15.6 (slip: Alt + body drag, only the
source mapping changes — Q12, `aa94da0`) accepted; Step 15.7 (In / Out range: I / O, the ✕, loop over the range, range
export with its own preflight — Q7–Q11, Q16 — and the stale audio status fix, Q15, `46eae30`) accepted; Step 15.8 (a new
text's font size `48 × canvasHeight / 1080`, no clamp — Q14, `f9a2253`, review corrections `9de14c6`) accepted.
**Phase 15 complete** — Step 15.9 (final verification & closeout) done on 2026-10-08: build `--no-incremental
-warnaserror` Release and Debug 0 / 0; the full suite, `AIVE_HEAVY_TESTS=1` and three `--blame-hang-timeout 5m` runs
green (counts in `progress.md`); `git diff --check` clean; R1–R9 of the manual plan run in the real app (R10's audio
device part not run: it needs a physical audio-output interruption); the R2 run found a still-image decode defect (a long
image clip entered far into it: range export failed, Preview placeholder) — fixed in the closeout (D030). Accepted; PR
#15 merged into `main` as `a3793a4` (2026-10-08), CI green on the first attempt on the pull request (run 37743022102) and
on `main` (run 37743623800). The follow-up Preview fix (a playhead parked past the end) merged as PR #16 (`1bdeb95`).
Scope and acceptance criteria: `docs/DEVELOPMENT_PLAN.md`, "Phase 15 — Editing tools: steps"; manual plan
`docs/PHASE15_MANUAL_TEST_PLAN.md`.

Phase 14 — Stabilization / technical debt, branch `feat/phase-14-stabilization` (from `ed40b74`, `main` after the merge
of PR #13). **Complete**: accepted by the product owner; PR #14 merged into `main` as `7200976` (2026-10-07), CI green on
the first attempt on the pull request (run 37590085671) and on `main` (run 37590661647) — the D029 §5 measure met, the
D028 §8 flaky-test policy closed. Steps 14.1–14.7 accepted by the product owner; Step 14.8 (final verification & closeout) done on
2026-10-07 — build `--no-incremental -warnaserror` Release and Debug 0 / 0; the full suite 2600 passed, 2 skipped (only
the two 4K scenes), 0 failed; three `--blame-hang` runs 2600 / 2 / 0 each, no hang, no dump; with `AIVE_HEAVY_TESTS=1`
2602 passed, 0 skipped, 0 failed; R1–R5 of `docs/PHASE14_MANUAL_TEST_PLAN.md` passed against the Phase 13 build (a Phase 13
project opens and saves byte for byte; the default export, the saved files and an every-property export identical to
Phase 13's; autosave / recovery; ffmpeg found / missing). Goal (product owner, D029): a more deterministic CI and less
technical debt **without any change of the user functionality**. Delivered: 14.2 sync & D029 (`0a50fe5`) · 14.3 the
autosave timer — a production fix of a tick that a callback queued before `Stop` could start, `TimeProvider`, deterministic
tests (`ed35f1f`) · 14.4 the ffprobe / PATH-probe tests independent of the app's limits, which stay 5 s / 20 s / 20 s
(`0a4e6fe`) · 14.5 on-grid test helpers with a guard, the NUL escape (`b8e6aac`) · 14.7 the empty `src/Effects` project
removed (`90caae9`) · 14.8 closeout. 14.6 (a canvas-relative font size of a new text clip, the status after an audio
device returns) **not taken — out of scope** (both taken into Phase 15, D030 §9).

Phase 13 — Project & export settings, branch `feat/phase-13-project-export-settings` (from `c0cb600`, `main` after the
merge of PR #12). **Complete**: accepted by the product owner on the Step 13.10 verification (closeout `7d4f6d8`); PR #13
merged into `main` as `ed40b74` (2026-10-06), CI green on the pull request. The first CI run on `main` after the merge
(run 37470605596) failed on one known flaky test —
`ExecutableLocatorTests.RealLocator_FirstCallCancelledMidProbe_SecondCallStillFindsFfmpeg` (`Assert.NotNull() Failure:
Value is null` after 7 s: the 5 s PATH probe, D028 §8); recorded in `progress.md`, its fix is Phase 14 Step 14.4. Steps 13.1–13.9 accepted by the product owner (`226c7f2` … `5c01aed`); Step 13.10
(final verification & closeout) done on 2026-10-06 — build `--no-incremental -warnaserror` 0 / 0; the full suite
2585 passed, 2 skipped (only the two 4K scenes), 0 failed; three `--blame-hang` runs 2585 / 0 / 2 each, no hang, no
dump; the full suite with `AIVE_HEAVY_TESTS=1` 2587 passed, 0 skipped, 0 failed; R1 (a Phase 12 project opens with the default export settings and saves byte for byte as Phase 12 wrote it),
R2 (export settings applied, undone, redone and used by a real export: `crf=28.0`, `subme=8`, `-b:a 320000`) and R3
(Phase 11–12 features at 1080 × 1080 / 30 fps) passed in the real app. What it delivers (DECISIONS.md D028): the canvas size
and the frame rate as project settings (undoable; positions and font sizes scale with the canvas; D007's re-grid on a
rate change, FR-1); export settings — quality (CRF 14 / 18 / 23 / 28), libx264 preset (fast / medium / slow), AAC 128–320
kbps — in the Project Settings dialog (a change from the Step 13.2 plan, which had them in the export flow), saved as the
optional `settings.export` of `project.json` v3 (a project without it: the default = the Phase 12 output); L1-c decided
(per-level bounds on the codec's own error, the level order, flat colour; the measurement kept in the suite); the
Phase 8–12 export / parity criteria unchanged. Steps 13.3 model & format · 13.4 canvas · 13.5 frame rate · 13.6 project
settings UI · 13.7 export settings core · 13.8 L1-c · 13.9 export settings UI · 13.10 closeout — details in
`docs/DEVELOPMENT_PLAN.md` and `progress.md`.

Phase 12 — Editing essentials, branch `feat/phase-12-editing-essentials` (from `47ed2fa`, `main` after the merge of
PR #11). **Complete**: accepted by the product owner on the Step 12.9 verification (closeout `6761c1d`); PR #12 merged
into `main` as `c0cb600` (2026-10-05), CI green (recorded at Step 13.2, 2026-10-06). Step 12.9 (final verification &
closeout) done on 2026-10-05 — build `-warnaserror` 0 / 0; the full suite 2324 passed, 2 skipped (only the two 4K scenes), 0 failed; three `--blame-hang` runs 2324 / 0 / 2, no
hang, no dump; the 4K scenes with `AIVE_HEAVY_TESTS=1`: ExportEndToEnd 90 / 90; the Phase 12
manual plan in the real app (30 scenarios passed, 2 partly, 2 not reproducible by hand and covered by automated tests),
R1 (a project saved by the Phase 11 build, opened and saved by Phase 12: v3, unchanged), R2 (the export manual plan's UI
and file scenarios) and R3 (relink, recent projects, fade, dissolve) passed; CI green on PR #12. Scope from the
product owner decisions of 2026-10-05, recorded in DECISIONS.md D027: track delete / reorder, removing media from the project, ripple delete and close gap (dissolves of removed clips removed, the others kept,
none created), copy / paste / duplicate of clips, markers on the timeline, and the fix of New during a running import;
`project.json` stays v3; L1-c stays open. Steps 12.3 tracks · 12.4 media removal · 12.5 ripple · 12.6 copy / paste /
duplicate · 12.7 markers · 12.8 import / New · 12.9 closeout — scope and acceptance criteria in
`docs/DEVELOPMENT_PLAN.md`. Step 12.1 (audit) accepted (2026-10-05); Step 12.2 (sync after the merge, scope
formalization, documentation only) accepted (2026-10-05); Step 12.3 (tracks: delete and reorder, undoable; the last
track and locked tracks kept) accepted (2026-10-05; build 0 / 0, 2237 passed, 2 skipped, three `--blame-hang` runs
clean, `5bcdcbb`); Step 12.4 (removing media from the project, undoable, with its clips after a confirmation)
accepted (2026-10-05; build 0 / 0, 2255 passed, 2 skipped, three `--blame-hang` runs clean, `097e555`); Step 12.5
(ripple delete of the selected clips and close gap, undoable) accepted (2026-10-05; build 0 / 0, 2279 passed, 2
skipped, three `--blame-hang` runs clean; a real export after a ripple matches its Preview; `a2f8c7c`); Step 12.6 (copy /
paste / duplicate of clips, undoable, Ctrl+C / Ctrl+V / Ctrl+D) accepted (2026-10-05; build 0 / 0, 2304 passed, 2
skipped, three `--blame-hang` runs clean; the header checked at 1024 and 1440 px, the Copy button removed for 1024 px;
`518730c`); Step 12.7 (markers: add / remove at the playhead, drawn on the ruler, previous / next, snapping) accepted
(2026-10-05; build 0 / 0, 2319 passed, 2 skipped, three `--blame-hang` runs clean; the marker buttons checked in the
real app at 1024 and 1440 px; `5a7edae`); Step 12.8 (an import belongs to the project it started in: New / Open /
Recover meanwhile adds nothing) accepted (2026-10-05; build 0 / 0, 2324 passed, 2 skipped, three `--blame-hang` runs
clean; the real race not reproducible by hand; `d467a84`). Step 12.9 closeout. Details: `progress.md`.

Phase 11 — Media relink & recent projects, branch `feat/phase-11-relink-recent-projects` (from `2e758f1`, `main` after
the merge of PR #10). **Complete**: accepted by the product owner on 2026-10-05 on the Step 11.9 local verification
(closeout `ca20352`); PR #11 merged into `main` as `47ed2fa` (2026-10-05), CI green. Not run: optional export scenario 9
and the "without ffmpeg" check; export scenario 14 checked by decoding the 8 outputs, not watched in a player; L1-c
stays open, outside Phase 11. Scope from the product owner decisions PO-1…PO-9 (2026-10-01), recorded in DECISIONS.md D026:
re-checking media availability during the session (window activation, throttled; before the export and the relink),
relink of missing media only (undoable; hard rejects and warnings; allowed without ffprobe), a batch relink of files with
the same name in the chosen folder, and `Recent ▾` (10 entries) next to Open. Steps 11.3 re-check · 11.4 relink core ·
11.5 batch search · 11.6 relink UI · 11.7 recent projects core · 11.8 recent projects UI · 11.9 closeout — scope and
acceptance criteria in `docs/DEVELOPMENT_PLAN.md`.

Step 11.1 (audit) and Step 11.2 (scope formalization, documentation only, `ba6762e`) accepted (2026-10-01); Step 11.3
(media availability re-check: on window activation, throttled to 3 s with a trailing check, and before the export; files
gone or back during the session become offline / online with their analysis, thumbnail and waveform restarted, `db0feba`)
accepted (2026-10-01; automated, and the manual scenarios 1–6 passed in the real app). Step 11.4 (relink core: check →
confirmation → apply as one undoable step, hard rejects and warnings, ffprobe unavailable allowed / probe failure
rejected, the old file's thumbnail and waveform dropped) accepted (2026-10-01, `0e002dc`); its UI follows in 11.6.
Step 11.5 (batch search: one folder, its own files, exact names ignoring case, a shared name given to none, every match
through the 11.4 check, a summary, the confirmed items applied as one undoable step, re-validated) accepted (2026-10-01,
`4180b4a`). Step 11.6 (relink UI: Relink… and Find Missing… in the Media Browser for offline media, the confirmations,
the offer to search after an applied relink, the `EditingLock`) accepted (2026-10-02, `145d484`; the manual run of
scenarios 7–21 found D1–D5 — Inspector state line, an undone relink's thumbnail / waveform, three texts — fixed in
`5c4d2d9` and re-checked in the real app). Step 11.7 (recent projects core: `IRecentProjectsStore`, the list in the
configuration folder, added after a successful Open, Save As and Recover) accepted (2026-10-02, `55606fd`). Step 11.8
(`Recent ▾` next to Open: the list read at every opening, Checking / Available / Unavailable, open through the workflow,
remove per entry, no "Clear list", disabled during an export) accepted (2026-10-04, `7bf4ed8`). Step 11.9 (final
verification & closeout): local verification done (2026-10-05) — build `-warnaserror` 0 / 0, the full suite once and
three times with `--blame-hang` (2213 passed, 2 skipped each), heavy scenes 88 / 88, the remaining manual scenarios
(23 through Open, 24, 27, 28), R1 (a Phase 10 project), R2 (the export manual plan) and a relink / recent projects
regression passed in the real app; accepted 2026-10-05. CI green on PR #11 (merged as `47ed2fa`).
Details: `progress.md`.

Phase 10 — Transitions & basic effects, branch `feat/phase-10-transitions-effects` (from `409240b`). **Complete**:
accepted by the product owner on 2026-10-01 (last verified commit `ddf45df`, closeout `ee0527d`; R7 export regression
13 / 13; build `-warnaserror` 0 / 0; 1988 passed, 2 skipped; 4K 88 / 88). PR #10 merged into `main` as `2e758f1`
(2026-10-01).

Step 10.1 (audit) accepted; Step 10.2 (scope formalization: D025, the Phase 10 steps, the manual plan skeleton) done;
Step 10.3 (model, `project.json` v3) done; Step 10.4 (fades: Core rule, edits, Preview / export composition and mix) done; Step 10.5 (fades: Inspector and
timeline UI, end-to-end parity) done; Steps 10.4–10.5 accepted by the product owner on 2026-09-29 (real-app fade
scenarios passed). Step 10.6 (dissolve: edits and validation, the coupling with every timeline edit) accepted on
2026-09-29 (automated). Step 10.7 (dissolve: composition in the Preview and the export) done — awaiting acceptance;
the real-app dissolve scenarios follow 10.8. Step 10.7 accepted (automated). Step 10.8 (dissolve UI) accepted by the
product owner on 2026-09-29 (real-app dissolve scenarios 13–20 passed). Step 10.9 (final verification & closeout)
accepted on 2026-10-01: its manual run found and fixed the timeline's reuse of clip views after the same project was
opened again, fades longer than a shortened clip (now cut, D025 §2 changed by the product owner) and numeric fields
applying each typed digit; the one-frame clip minimum (D008) confirmed.
MVP (PO-1…PO-7): fade in / fade out of a clip (picture and its own sound, linear ramp `(i+1)/(F+1)`) in 10.3–10.5,
then a cross dissolve centred on a cut of one video track with source handles (B over A, the sound a hard cut) in
10.6–10.8; `project.json` v3 (v1 / v2 read without fades and dissolves). Steps 10.3 model & format v3 · 10.4 fades core ·
10.5 fades UI & parity · 10.6 dissolve edits · 10.7 dissolve composition · 10.8 dissolve UI · 10.9 closeout — scope and
acceptance criteria in `docs/DEVELOPMENT_PLAN.md`, decision DECISIONS.md D025. Details: `progress.md`.

Phase 9 — Quality, branch `feat/phase-9-quality` (from `ab248e5`, `main` after PR #6). **Complete**: accepted by the
product owner on 2026-09-29 (closeout `dcb86cb`, last Step 9.10 commit `f27a4ab`; PRs #7 and #8, merged as `409240b`). Step 9.1
(audit) and Step 9.2 (scope formalization) done; Step 9.3 (stability & error handling: close hang, analysis
cancellation and concurrency, FFmpeg diagnostics, audio default device, damaged-project message) accepted
(`3006785`); Step 9.4 (thumbnails + cache: real Media Browser thumbnails, deterministic frame, project-scoped cache
with invalidation, unsaved-project cache, offline media from the cache only) accepted (`e666b48`); Step 9.5
(waveform: timeline waveforms of audio clips and video with sound, only for media on the timeline, volume / mute /
speed shown, a cache next to the thumbnails', offline media from the cache only) accepted (`568a47b`); Step 9.6
(hotkeys: routing tests, J / K / L, loop, Ctrl+I / Ctrl+E / \, the text-input guard checked in the running app)
accepted (`e8c3c93`); Step 9.7 (performance): baseline and C (Preview at 8 layers) accepted, A (export decodes ahead / in parallel) accepted (`4bdf738`), the step accepted (`1124138`); Step 9.8 (polish & cleanup: dead code removed incl.
`PlaybackFrame.Picture`, naming, documentation, shortcut tooltips, an empty-timeline hint, an importing status)
accepted (`da4838c`); Step 9.9 (CI: GitHub Actions on windows-2025, .NET SDK 8.0.424, FFmpeg 9.0.1 essentials pinned
by SHA256, build with `-warnaserror`, the full suite and a gate on skips) accepted (`e8f2410`); Step 9.10 (final
verification: the Phase 9 manual test plan run in the real app, 59 scenarios incl. real audio devices with the owner; the
export manual plan as a regression; quality gates) done. Steps 9.3 stability & error handling · 9.4 thumbnails + cache ·
9.5 waveform · 9.6 hotkeys · 9.7 performance baseline & optimization · 9.8 polish & cleanup · 9.9 CI / quality gates ·
9.10 closeout — scope and acceptance criteria in `docs/DEVELOPMENT_PLAN.md`, decision DECISIONS.md D024. L1-c stays
open. Details: `progress.md`.

Phase 8 — Export, branch `feat/phase-8-export` (from `2f0e26f`). **Complete**: accepted by the product owner on
2026-09-25 (commit `8786491`, PR #6). Decisions: DECISIONS.md D023 (export as an
offline rendering of the Preview: C# compositor + FFmpeg encoder, fixed MP4 H.264 CRF 18 / AAC 48 kHz,
canvas size and exact project rate, preflight that blocks on offline/unanalysed/unsupported media).
Step 1 (contract, preflight, `ExportSettings` cleanup, documentation), Step 2 (offline source-frame
selection), Step 3 (shared composition plan, Avalonia offscreen rasterizer), Step 4 (offline audio PCM with the
shared placement and mix), Step 5 (ffmpeg encoder), Step 6 (`ExportService` orchestration + end-to-end exports) and
Step 7 (export UI: command, preflight dialogs, progress window, cancel, editing lock; manual test 14/14 PASS) and
Step 8 (end-to-end Preview ↔ Export parity 8.1–8.5, codec-error measurement 8.6, closeout 8.7 — the planned Step 9)
done. Open: no numeric tolerance for the codec leg
MP4 → export canvas (D023 Step 8, decision L1-c). Details:
`progress.md`.

Phase 7 — Basic editing, branch `feat/phase-7-basic-editing`. **Complete**: Steps 1–9 (last
checkpoint `48a3f54`), Step 10 closeout (audit, full test runs, integration smoke in the running app);
manually accepted by the product owner on 2026-09-24. Speed, volume/
mute, opacity, transform, crop and text clips per clip (no keyframes), multi-layer Preview,
`project.json` v2 (reads v1). Decisions: DECISIONS.md D017–D022. Carried into Phase 8: the export
must reproduce D018 exactly; text fonts (D021) — resolved by D023. Known issue (pre-Phase 7, separate
task): closing the app after a project with media was open hangs the process — fixed in Phase 9 Step 9.3a. Details: `progress.md`.

## Future phases

Phase 19 (AI hold markup, D034) and Phase 20 (contour correction, D035) are merged; Phase 21 (the outline animation, D036) is merged (PR #22, `7318ba8`); Phase 22 (timeline selection, snapping and making room, D037) is merged (PR #23, `1aaedc8`); Phase 23 (direct Move, Scale and Crop in the Preview, D038) is merged (PR #24, `c652667`); Phase 24 (Russian localization and interface polish, D039) is merged (PR #25, `d3df0ba`); Phase 25 (technical backlog and small UX improvements, D040) is current. A magnetic main track that closes gaps automatically (CapCut-like) is a candidate, not decided (D037 §5). Candidates named in the
Phase 16–18 pre-flights, not decided: ducking (after keyframes); a
stabilization pass (splitting `TimelineEditService` / `TimelineViewModel`); text styling and dragging objects in the
Preview; keyframes; timeline polish (marker labels, marquee, filmstrips); freeze frame / reverse / speed ramps. ("The
In / Out update order" and "deep-seek decode coverage", listed here before, were defined nowhere and are not tasks —
D032 "Context"; removed at Step 18.2.) Left out by the product owner: colour management beyond the HDR → SDR
conversion of D033 (HDR output, grading, ICC), an installer, timeline virtualization, an undoable import. AI features,
listed here before as left out, are the product's main direction since 2026-10-14: the AI hold markup MVP is decided —
DECISIONS.md D034 (accepted 2026-10-14, after experiments outside the repository); implemented in Phase 19, merged 2026-10-15.

Correction of a hold's contour by editing its vertices (proposed 2026-10-15) is Phase 20 — DECISIONS.md D035.

Taken up as Phase 21 (DECISIONS.md D036, 2026-10-15) — recorded 2026-10-14 as a future idea: a route animation after the AI hold markup — the marked holds appear
over the video one by one at a configurable interval, stay visible, and disappear together after a set time. Not part
of the AI hold markup MVP; keeping the contours on the holds while the camera moves is a separate motion compensation /
tracking task. Sketch: `docs/DEVELOPMENT_PLAN.md`, "Future ideas (not phases)".

## Follow-ups and backlog

Separate tasks recorded at the Phase 21 acceptance (2026-10-15) and later; none is part of Phase 21 or 22 (D037 §5). Phase 25
(D040) takes FU-21.1, FU-22.1, FU-24.2 and FU-24.1 in part; FU-21.2 and the vertical-video export stay out.

- **FU-21.1 — "Previous marker" status message (marker issue)** — *Phase 25 (D040 §2).* Going to a marker outside the clip sometimes first shows
  "There is no marker before the playhead", although a second press moves the playhead; after returning, the warning
  stays in the status bar. Cause to be found (Previous / Next compare with the playhead's nearest frame; a successful jump
  reports nothing, so an earlier message is not replaced). Details: `docs/PHASE21_MANUAL_TEST_PLAN.md`, "Follow-ups".
- **FU-21.2 — Markers stay in place when a clip is moved (marker semantics).** Markers are sequence (timeline) markers and
  D027 says no edit moves them, so this is the current decided behaviour. Open product question: whether markers bound
  to a clip (moving with it) are wanted. Not to be treated as a bug before that is decided.
- **FU-22.1 — The hold markup mode left on its own after entering it (observed at Step 22.6)** — *Phase 25 (D040 §3).* With the
  playhead put on 00:00:10:00 by a click on the ruler and a video clip selected, "Mark holds" entered the mode ("Preparing
  the frame…"); a few seconds later the playhead stood on 00:00:09:23 and the mode was off (a seek leaves it, D034). Entering
  again at 00:00:09:23 kept it. Phase 19 code (D034), untouched by Phase 22; to be reproduced and analysed separately.
- **Export of vertical video without unwanted black bars (backlog; a separate future phase, D040 §7).** A vertical (portrait) clip exported on a landscape
  canvas comes out with black bars at the sides (the D018 fit of a picture to the canvas). The expected framing must be
  decided first — e.g. a portrait canvas from the clip, a fill / crop-to-canvas mode, or a choice at export — before any
  change of the export or the composition code. No code changed for it in Phase 21.
- **FU-24.1 — Sliders for other numeric fields (recorded at Phase 24, D039 §8)** — *Fade In / Out and the hold line width in
  Phase 25 (D040 §4); a Speed slider not wanted; dragging on labels stays backlog.* Phase 24 adds sliders only to Opacity
  and Volume. Candidates: Fade In / Out (a slider up to the clip's length), Speed (detents at 0.5 / 1 / 2 ×), dragging on
  the label of Scale / Rotation / Position, the hold line width. Not decided; the exact numeric fields stay in every case.
- **FU-24.2 — `appsettings.json` read from the current directory (observed at Phase 24, D039 implementation notes)** —
  *Phase 25 (D040 §1).* The host's content root was the process's current directory: started from another folder the app
  ran with the defaults and without the `Ffmpeg:*` paths.

## Rule

Do not silently mark a phase complete.

A phase is complete only after its acceptance criteria are implemented and verified.
