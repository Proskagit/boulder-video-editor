# Phase 12 — manual test plan

Run in the real app (`dotnet run --project src/App/App.csproj`, ffmpeg / ffprobe on PATH). Written at Step 12.2
(D027, product owner decisions of 2026-10-05) as a skeleton; each step that implements a feature completes its
scenarios (exact texts, automated coverage, fixtures) and records its development-time run; Step 12.9 runs the
scenarios not yet run in the real app, the regression and the export manual plan. Logs:
`%LOCALAPPDATA%\AiVideoEditor\logs`. Manual runs use an isolated profile (`USERPROFILE` / `LOCALAPPDATA` pointed to a
scratch folder), as at Step 11.9.

Fixtures: written by the steps that need them (scripts under `tools/manual`, projects and media outside the
repository, as in Phases 10–11): a project with two video tracks and one audio track, clips with fades and dissolves,
gaps, a text clip, an image, an offline asset.

Status column — kinds of evidence, never mixed:
- **planned** — written at 12.2, not implemented yet;
- **auto (12.x); manual pending** — implemented and covered by automated tests at step 12.x, not yet run in the real app;
- **auto** — covered by automated tests only, not checked in the real app;
- **app 12.x (Claude), passed** — checked in the real app by Claude while implementing / accepting step 12.x;
- **12.9 (Claude): PASS / FAIL / BLOCKED / NOT RUN** — the formal Step 12.9 run;
- **PO** — run by the product owner.
A scenario is marked passed only for the run that actually checked it; the "Automated coverage" column is separate.

## Step 12.3 — tracks: delete and reorder (D027 §3)

| # | Scenario | Steps | Expected | Automated coverage | Status |
|---|---|---|---|---|---|
| 1 | Delete an empty track | Add a video track; delete it with ✕ in its header | The track is gone, no confirmation; Undo brings it back at its place; the project is dirty, clean again after Undo to the save point | `TrackEditTests` (empty, back at its place), `TimelineTrackUiTests` (no question) | auto (12.3); manual pending |
| 2 | Delete a track with clips | Delete a track that has clips (one with a fade, two with a dissolve) | A confirmation; Cancel changes nothing; OK removes the track, its clips and the dissolve in one step; Undo restores everything exactly | `TrackEditTests` (clips, fade, dissolve restored), `TimelineTrackUiTests` (Cancel / closed / Delete Track, selection) | auto (12.3); manual pending |
| 3 | Reorder video tracks | Two overlapping video clips on V1 / V2; move V1 above V2 | The timeline shows the new order; the Preview draws the other clip on top; Undo restores the order | `TrackEditTests` (orders, snapshot layer order, undo / redo), `TimelineTrackUiTests` (rows) | auto (12.3); manual pending |
| 4 | Reorder and export | After 3, export | The export shows the same layer order as the Preview | `ExportTrackOrderEndToEndTests` | auto (12.3); manual pending |
| 5 | Reorder audio tracks | Move an audio track | Order changes in the timeline; the sound is the same mix | `TrackEditTests` (audio among audio), `TimelineTrackUiTests` (audio ▲ / ▼) | auto (12.3); manual pending |
| 6 | Save and reopen | After 2–5, Save, reopen | Tracks, order and clips as left; `formatVersion` 3 | `TrackEditTests` (save / reopen) | auto (12.3); manual pending |
| 7 | During an export / locked track / last track | Try ▲ / ▼ / ✕ during an export; on a locked track (set in `project.json`) and next to one; delete every track but one | Disabled during the export; a locked track is neither deleted nor moved, nor moved past ("Track V2 is locked, so V1 can't move past it."); the last track stays ("The timeline needs at least one track."); no question for a refused deletion | `TrackEditTests` (locked, last track), `TimelineTrackUiTests` (lock, refused, last) | auto (12.3); manual pending |

## Step 12.4 — removing media from the project (D027 §4)

| # | Scenario | Steps | Expected | Automated coverage | Status |
|---|---|---|---|---|---|
| 8 | Unused asset | Import a file, don't use it; select it and click ✕ on its row | Gone from the Media Browser at once, no question; the file on disk unchanged; Undo brings it back with its thumbnail and metadata | `MediaRemovalTests` (unused, back at its place), `MediaRemovalUiTests` (no question, thumbnail again without making it) | auto (12.4); manual pending |
| 9 | Used asset | Use a file in three clips (one in a dissolve); remove it | "Remove Media" naming 3 clips and that the file stays on disk; Cancel changes nothing; Remove removes the asset, the clips and the dissolve in one step (the dissolve note in the status bar); the Preview and the waveforms follow | `MediaRemovalTests` (three clips on two tracks, dissolve, snapshot), `MediaRemovalUiTests` (Cancel / closed / Remove, text) | auto (12.4); manual pending |
| 10 | Undo / Redo | After 9, Undo, Redo | Undo restores the asset, clips, dissolve, thumbnail and waveform; Redo removes them again | `MediaRemovalTests` (Undo exact), `MediaRemovalUiTests` (rows, thumbnail) | auto (12.4); manual pending |
| 11 | Offline asset / locked track | Remove an offline asset used on the timeline; then try an asset whose clip is on a locked track (set in `project.json`) | The offline one is removed like an online one, nothing on disk touched; the other is refused without a question ("… is used on track A1, which is locked.") | `MediaRemovalTests` (offline, locked, file untouched), `MediaRemovalUiTests` (refused without a question) | auto (12.4); manual pending |
| 12 | Removed during analysis | Import a long file and remove it while it is analysed; Undo | No error; after Undo the asset is analysed (not stuck "Analyzing") | `MediaRemovalUiTests` (analysis and thumbnail still running) | auto (12.4); manual pending |
| 13 | Save and reopen | After 9, Save, reopen; then Undo is gone (new session) | The asset and clips stay removed; the source file still on disk; `formatVersion` 3 | `MediaRemovalTests` (save / reopen) | auto (12.4); manual pending |

## Step 12.5 — ripple delete and close gap (D027 §2)

| # | Scenario | Steps | Expected | Automated coverage | Status |
|---|---|---|---|---|---|
| 14 | Ripple delete one clip | V1: A B C (B in the middle); select B, Ripple Delete | C moves left by B's length; no gap; other tracks, playhead and markers stay | `RippleEditTests` (one clip, undo / redo) | auto (12.5); manual pending |
| 15 | Several clips, several tracks | Select clips on V1 and A1 (Ctrl+click); Ripple Delete | Each track closes by its own removed length; nothing overlaps; the selection is cleared | `RippleEditTests` (several clips, two tracks), `TimelineRippleUiTests` (two tracks) | auto (12.5); manual pending |
| 16 | Gaps kept | V1: A [gap] B C; ripple delete A | The gap and B C move left together; the gap stays | `RippleEditTests` (gaps kept) | auto (12.5); manual pending |
| 17 | Close gap | V1: A [gap] B; select B, Close Gap; then select a clip with no gap before it | B (and everything after it on V1) moves left by the gap; the second time "There is no gap before this clip." | `RippleEditTests` (close gap cases), `TimelineRippleUiTests` (close gap) | auto (12.5); manual pending |
| 18 | Dissolve of the removed clip | A → B dissolve; ripple delete B | The dissolve is removed with D025's status note; C meets A without a dissolve | `RippleEditTests` (removed clip's dissolve) | auto (12.5); manual pending |
| 19 | Dissolve after the removed clip | X, then C → D dissolve right of it; ripple delete X | C and D move together; the dissolve keeps its length; the Preview and the export show it as before | `RippleEditTests` (dissolve after, snapshot zone) | auto (12.5); manual pending |
| 20 | Undo / export | Undo after 18 / 19; export after 19 | Undo restores clips and dissolves exactly; the export matches the Preview | `RippleEditTests` (undo / redo exact), `ExportRippleEndToEndTests` (a real export after a ripple = its Preview) | auto (12.5); manual pending |
| 21 | Locked track | Ripple delete / close gap on a locked track | Rejected with a message; nothing changes | `RippleEditTests` (locked), `TimelineRippleUiTests` (refused) | auto (12.5); manual pending |

## Step 12.6 — copy / paste / duplicate (D027 §5)

| # | Scenario | Steps | Expected | Automated coverage | Status |
|---|---|---|---|---|---|
| 22 | Copy / paste one clip | Copy a clip with speed, volume, opacity, transform, crop, fades; move the playhead; paste | A new clip at the playhead with every property; the original unchanged | — | planned |
| 23 | Several clips keep distances | Copy two clips on two tracks with a gap; paste | Same tracks, same distances from the playhead | — | planned |
| 24 | Text clip | Copy / paste a text clip | Text, font, size, colour, alignment kept | — | planned |
| 25 | Dissolve not copied | Copy A and B joined by a dissolve; paste | Two clips, no dissolve | — | planned |
| 26 | Rejected paste | Paste where a clip would overlap | Rejected with a message; nothing pasted | — | planned |
| 27 | Duplicate | Duplicate a selection | Copies placed as decided at 12.6; one Undo removes them | — | planned |
| 28 | Undo / save | Undo / Redo a paste; save and reopen | Exact; pasted clips persist with their properties | — | planned |

## Step 12.7 — markers (D027 §6)

| # | Scenario | Steps | Expected | Automated coverage | Status |
|---|---|---|---|---|---|
| 29 | Add / remove | Add markers at three playhead positions; remove one | Drawn on the timeline; each change undoable; the project dirty | — | planned |
| 30 | Next / previous | Go to the next / previous marker from several positions | The playhead jumps to the right marker; nothing at the ends | — | planned |
| 31 | Save and reopen | Save, reopen | Markers kept; `formatVersion` 3 | — | planned |
| 32 | Ripple keeps markers | Ripple delete a clip before a marker | The marker stays where it was | — | planned |

## Step 12.8 — New during a running import (D027 §7)

| # | Scenario | Steps | Expected | Automated coverage | Status |
|---|---|---|---|---|---|
| 33 | New during an import | Import many files (or files on a slow drive); click New while "Importing N files…" shows | The new project has no media; the status says the import was dropped | — | planned |
| 34 | Plain import | Import without a project change | Works as before (status, duplicates, analysis) | — | planned |

## Regression (Step 12.9)

| # | Scenario | Expected | Status |
|---|---|---|---|
| R1 | A project saved by the Phase 11 build | Opens unchanged; Save writes `formatVersion` 3 | planned |
| R2 | `docs/EXPORT_MANUAL_TEST_PLAN.md` | Passes as before | planned |
| R3 | Relink, recent projects, fades and dissolves (short run) | As accepted in Phases 10–11 | planned |

## Results log

None yet.
