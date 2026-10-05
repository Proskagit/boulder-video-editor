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

Phase 12 — Editing essentials, branch `feat/phase-12-editing-essentials` (from `47ed2fa`, `main` after the merge of
PR #11). Scope from the product owner decisions of 2026-10-05, recorded in DECISIONS.md D027: track delete / reorder,
removing media from the project, ripple delete and close gap (dissolves of removed clips removed, the others kept,
none created), copy / paste / duplicate of clips, markers on the timeline, and the fix of New during a running import;
`project.json` stays v3; L1-c stays open. Steps 12.3 tracks · 12.4 media removal · 12.5 ripple · 12.6 copy / paste /
duplicate · 12.7 markers · 12.8 import / New · 12.9 closeout — scope and acceptance criteria in
`docs/DEVELOPMENT_PLAN.md`. Step 12.1 (audit) accepted (2026-10-05); Step 12.2 (sync after the merge, scope
formalization, documentation only) accepted (2026-10-05); Step 12.3 (tracks: delete and reorder, undoable; the last
track and locked tracks kept) accepted (2026-10-05; build 0 / 0, 2237 passed, 2 skipped, three `--blame-hang` runs
clean, `5bcdcbb`); Step 12.4 (removing media from the project, undoable, with its clips after a confirmation)
accepted (2026-10-05; build 0 / 0, 2255 passed, 2 skipped, three `--blame-hang` runs clean, `097e555`); Step 12.5
(ripple delete of the selected clips and close gap, undoable) accepted (2026-10-05; build 0 / 0, 2279 passed, 2
skipped, three `--blame-hang` runs clean; a real export after a ripple matches its Preview). Details: `progress.md`.

## Previous

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

None planned after Phase 12 — see `docs/DEVELOPMENT_PLAN.md`. Left out of Phase 12 by the product owner (2026-10-05):
AI features, export settings, HDR / colour management, an installer, timeline virtualization, an undoable import.

## Rule

Do not silently mark a phase complete.

A phase is complete only after its acceptance criteria are implemented and verified.
