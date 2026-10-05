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
| 1 | Delete an empty track | Add a video track; delete it with ✕ in its header | The track is gone, no confirmation; Undo brings it back at its place; the project is dirty, clean again after Undo to the save point | `TrackEditTests` (empty, back at its place), `TimelineTrackUiTests` (no question) | 12.9 (Claude): PASS — no question, `*`, two Undo clean |
| 2 | Delete a track with clips | Delete a track that has clips (one with a fade, two with a dissolve) | A confirmation; Cancel changes nothing; OK removes the track, its clips and the dissolve in one step; Undo restores everything exactly | `TrackEditTests` (clips, fade, dissolve restored), `TimelineTrackUiTests` (Cancel / closed / Delete Track, selection) | 12.9 (Claude): PASS — "Track V1 has 9 clips…", Cancel / Delete Track, Undo exact |
| 3 | Reorder video tracks | Two overlapping video clips on V1 / V2; move V1 above V2 | The timeline shows the new order; the Preview draws the other clip on top; Undo restores the order | `TrackEditTests` (orders, snapshot layer order, undo / redo), `TimelineTrackUiTests` (rows) | 12.9 (Claude): PASS — the Preview at 6.6 s: V2 on top, then V1 (red) |
| 4 | Reorder and export | After 3, export | The export shows the same layer order as the Preview | `ExportTrackOrderEndToEndTests` | 12.9 (Claude): PASS — the export frame at 6.6 s red, 34 s, decode clean |
| 5 | Reorder audio tracks | Move an audio track | Order changes in the timeline; the sound is the same mix | `TrackEditTests` (audio among audio), `TimelineTrackUiTests` (audio ▲ / ▼) | 12.9 (Claude): PARTIAL — A2 moved above A1 and back by Undo; the sound not listened |
| 6 | Save and reopen | After 2–5, Save, reopen | Tracks, order and clips as left; `formatVersion` 3 | `TrackEditTests` (save / reopen) | 12.9 (Claude): PASS — reopened: V1, V2, A1; `formatVersion` 3 |
| 7 | During an export / locked track / last track | Try ▲ / ▼ / ✕ during an export; on a locked track (set in `project.json`) and next to one; delete every track but one | Disabled during the export; a locked track is neither deleted nor moved, nor moved past ("Track V2 is locked, so V1 can't move past it."); the last track stays ("The timeline needs at least one track."); no question for a refused deletion | `TrackEditTests` (locked, last track), `TimelineTrackUiTests` (lock, refused, last) | 12.9 (Claude): PASS — all off during an export (navigation on); locked V2: the three messages; the last track kept |

## Step 12.4 — removing media from the project (D027 §4)

| # | Scenario | Steps | Expected | Automated coverage | Status |
|---|---|---|---|---|---|
| 8 | Unused asset | Import a file, don't use it; select it and click ✕ on its row | Gone from the Media Browser at once, no question; the file on disk unchanged; Undo brings it back with its thumbnail and metadata | `MediaRemovalTests` (unused, back at its place), `MediaRemovalUiTests` (no question, thumbnail again without making it) | 12.9 (Claude): PASS — no question, the file stays, Undo brings the row back |
| 9 | Used asset | Use a file in three clips (one in a dissolve); remove it | "Remove Media" naming 3 clips and that the file stays on disk; Cancel changes nothing; Remove removes the asset, the clips and the dissolve in one step (the dissolve note in the status bar); the Preview and the waveforms follow | `MediaRemovalTests` (three clips on two tracks, dissolve, snapshot), `MediaRemovalUiTests` (Cancel / closed / Remove, text) | 12.9 (Claude): PASS — "used by 8 clips…", Cancel / Remove, the dissolve note |
| 10 | Undo / Redo | After 9, Undo, Redo | Undo restores the asset, clips, dissolve, thumbnail and waveform; Redo removes them again | `MediaRemovalTests` (Undo exact), `MediaRemovalUiTests` (rows, thumbnail) | 12.9 (Claude): PASS — Undo exact (timeline and ids), Redo removes again |
| 11 | Offline asset / locked track | Remove an offline asset used on the timeline; then try an asset whose clip is on a locked track (set in `project.json`) | The offline one is removed like an online one, nothing on disk touched; the other is refused without a question ("… is used on track A1, which is locked.") | `MediaRemovalTests` (offline, locked, file untouched), `MediaRemovalUiTests` (refused without a question) | 12.9 (Claude): PASS — offline still.png removed and back; locked: refused without a question |
| 12 | Removed during analysis | Import a long file and remove it while it is analysed; Undo | No error; after Undo the asset is analysed (not stuck "Analyzing") | `MediaRemovalUiTests` (analysis and thumbnail still running) | 12.9 (Claude): NOT REPRODUCED by hand — the analysis of the fixture ends in a fraction of a second; `MediaRemovalUiTests` (analysis still running) |
| 13 | Save and reopen | After 9, Save, reopen; then Undo is gone (new session) | The asset and clips stay removed; the source file still on disk; `formatVersion` 3 | `MediaRemovalTests` (save / reopen) | 12.9 (Claude): PASS — reopened without the media; the source file on disk |

## Step 12.5 — ripple delete and close gap (D027 §2)

| # | Scenario | Steps | Expected | Automated coverage | Status |
|---|---|---|---|---|---|
| 14 | Ripple delete one clip | V1: A B C (B in the middle); select B, Ripple Delete | C moves left by B's length; no gap; other tracks, playhead and markers stay | `RippleEditTests` (one clip, undo / redo) | 12.9 (Claude): PASS |
| 15 | Several clips, several tracks | Select clips on V1 and A1 (Ctrl+click); Ripple Delete | Each track closes by its own removed length; nothing overlaps; the selection is cleared | `RippleEditTests` (several clips, two tracks), `TimelineRippleUiTests` (two tracks) | 12.9 (Claude): PASS |
| 16 | Gaps kept | V1: A [gap] B C; ripple delete A | The gap and B C move left together; the gap stays | `RippleEditTests` (gaps kept) | 12.9 (Claude): PASS |
| 17 | Close gap | V1: A [gap] B; select B, Close Gap; then select a clip with no gap before it | B (and everything after it on V1) moves left by the gap; the second time "There is no gap before this clip." | `RippleEditTests` (close gap cases), `TimelineRippleUiTests` (close gap) | 12.9 (Claude): PASS — and "There is no gap before this clip." |
| 18 | Dissolve of the removed clip | A → B dissolve; ripple delete B | The dissolve is removed with D025's status note; C meets A without a dissolve | `RippleEditTests` (removed clip's dissolve) | 12.9 (Claude): PASS — the dissolve note; none created |
| 19 | Dissolve after the removed clip | X, then C → D dissolve right of it; ripple delete X | C and D move together; the dissolve keeps its length; the Preview and the export show it as before | `RippleEditTests` (dissolve after, snapshot zone) | 12.9 (Claude): PASS — P and Q moved together, 0.4 s kept |
| 20 | Undo / export | Undo after 18 / 19; export after 19 | Undo restores clips and dissolves exactly; the export matches the Preview | `RippleEditTests` (undo / redo exact), `ExportRippleEndToEndTests` (a real export after a ripple = its Preview) | 12.9 (Claude): PASS — Undo exact; the export after it 30 s, decode clean |
| 21 | Locked track | Ripple delete / close gap on a locked track | Rejected with a message; nothing changes | `RippleEditTests` (locked), `TimelineRippleUiTests` (refused) | 12.9 (Claude): PASS — Ripple Delete and Close Gap on the locked V2 refused |

## Step 12.6 — copy / paste / duplicate (D027 §5)

| # | Scenario | Steps | Expected | Automated coverage | Status |
|---|---|---|---|---|---|
| 22 | Copy / paste one clip | Copy a clip with speed, volume, opacity, transform, crop, fades (Ctrl+C — no Copy button); move the playhead; Paste (button or Ctrl+V) | A new clip at the playhead with every property, the same media; the original unchanged; the new clip selected | `ClipboardEditTests` (video clip, detached copy), `TimelineClipboardUiTests` (paste at the playhead) | 12.9 (Claude): PASS — a 2× clip: every property, the same media, a new id |
| 23 | Several clips keep distances | Copy two clips on two tracks with a gap; paste | Same tracks, same distances from the playhead | `ClipboardEditTests` (two tracks) | 12.9 (Claude): PASS — V1 36–40, V2 41–45 |
| 24 | Text clip | Copy / paste a text clip | Text, font, size, colour, alignment kept | `ClipboardEditTests` (text) | 12.9 (Claude): PASS |
| 25 | Dissolve not copied | Copy A and B joined by a dissolve; paste | Two clips, no dissolve | `ClipboardEditTests` (no dissolve) | 12.9 (Claude): PASS — still one dissolve |
| 26 | Rejected paste | Paste where a clip would overlap; paste onto a locked track; copy, remove the media, paste | Each rejected with a message; nothing pasted | `ClipboardEditTests` (overlap, locked, deleted track, removed media, frame rate) | 12.9 (Claude): PARTIAL — overlap, a locked track and removed media (then its Undo) checked; a deleted track automated only |
| 27 | Duplicate | Duplicate a selection (Duplicate or Ctrl+D); then duplicate a clip followed directly by another | The copies start where the selection ends, on the same tracks, selected; one Undo removes them; the second is refused (it would overlap) | `ClipboardEditTests` (duplicate), `TimelineClipboardUiTests` (duplicate) | 12.9 (Claude): PASS — refused for a following clip; V2 41–45 → 45–49 selected, Undo / Redo |
| 28 | Undo / save / typing | Undo / Redo a paste; save and reopen; press Ctrl+C / Ctrl+V while editing the Inspector's text | Exact; pasted clips persist with their properties; in the text box Ctrl+C / Ctrl+V copy and paste text, not clips | `ClipboardEditTests` (save / reopen), `ShortcutRoutingTests` (text input) | 12.9 (Claude): PASS — Undo / Redo, save / reopen; in the Inspector text box Ctrl+C / Ctrl+V edited the text, no clip pasted |

## Step 12.7 — markers (D027 §6)

| # | Scenario | Steps | Expected | Automated coverage | Status |
|---|---|---|---|---|---|
| 29 | Add / remove | Move the playhead to three places and click ◆+ each time; go to one (◀ / ▶) and click ◆−; click ◆+ twice at one place | Drawn on the ruler (flag and line); each change undoable; the project dirty; the second marker on one frame refused ("A marker is already there."); ◆− away from a marker: "There is no marker at the playhead." | `MarkerEditTests` (add, sorted, one per frame, remove), `TimelineMarkerUiTests` (drawn, refused, remove) | app 12.7 + 12.9 (Claude): PASS — `*`, Undo / Redo, the two refusals |
| 30 | Next / previous | Go to the next / previous marker (▶ / ◀) from several positions | The playhead jumps to the right marker and the Preview follows; at the ends a status message | `MarkerEditTests` (next / previous), `TimelineMarkerUiTests` (seeks, messages) | app 12.7 (Claude), passed (results log) |
| 31 | Save and reopen | Save, reopen | Markers kept; `formatVersion` 3 | `MarkerEditTests` (save / reopen) | 12.9 (Claude): PASS — markers kept and drawn; v3 |
| 32 | Ripple keeps markers / snapping | Ripple delete a clip before a marker; drag a clip near a marker | The marker stays where it was; the clip snaps to the marker | `RippleEditTests` (marker untouched), `MarkerEditTests` (snap target) | 12.9 (Claude): PASS — the ripple left the marker (14 / 15); a dragged clip snapped to it |

## Step 12.8 — New during a running import (D027 §7)

| # | Scenario | Steps | Expected | Automated coverage | Status |
|---|---|---|---|---|---|
| 33 | New during an import | Import many files (or files on a slow drive); click New while "Importing N files…" shows | The new project has no media; the status says "Import stopped: another project was opened, so nothing was added."; the next import works | `ImportProjectChangeTests` (New during the check, during the status, another project during the picker; the next import) | auto (12.8); app 12.8 (Claude): the race not reproducible — 9 attempts, files never reached the new project (results log) |
| 34 | Plain import | Import without a project change | Works as before (status, duplicates, analysis) | `ImportProjectChangeTests` (without a change), `ImportStatusTests` | app 12.8 (Claude): the plain import passed; Open / Recover can't race from the UI (results log) |

## Regression (Step 12.9)

| # | Scenario | Expected | Status |
|---|---|---|---|
| R1 | A project saved by the Phase 11 build | Opens unchanged; Save writes `formatVersion` 3 | 12.9 (Claude): PASS — the Phase 11 state (`47ed2fa`) built apart saved a project; Phase 12 opened it (13 clips) and saved v3 with everything else byte-identical; Phase 11 opened that again |
| R2 | `docs/EXPORT_MANUAL_TEST_PLAN.md` | Passes as before | 12.9 (Claude): PASS — 1 / 14, 7, 10, 11, 12, 13; 2, 3, 5, 6, 8 not re-run (no export / render code changed; parity and 4K green); 9 optional |
| R3 | Relink, recent projects, fades and dissolves (short run) | As accepted in Phases 10–11 | 12.9 (Claude): PASS — relink undoable; Recent ▾ (10, newest first, unsaved-changes question); fade dark at its start; a dissolve removed and added, undone |

## Results log

### 2026-10-05 — Step 12.6 header layout check (Claude), Debug, isolated profile

The timeline header and the panels touched by Phase 12 at the window's default size and at its minimum width (the
Debug build from the scratch artifacts, `USERPROFILE` / `LOCALAPPDATA` in a scratch folder, the Phase 10 fade fixture
project, 125 % display scaling; the window narrowed by dragging its border).
- 1440 × 900: the whole timeline header fits with room to spare; track headers (84 px) with ▲ ▼ ✕ whole.
- 1024 px wide, with a Copy button: the header overflowed — Fit cut at the right edge, "+ Video Track" right against
  "25 FPS". The Copy button was removed (Ctrl+C stays; the product owner's rule for this case).
- 1024 px wide, after the change: the header fits — Fit whole with its margin, a gap after "25 FPS"; −, + and Fit work
  (the ruler at 5 s steps, then 2 s, then the whole 0:00–0:34); ▲ ▼ ✕ whole; the Media Browser's ✕ on the selected row
  whole (260 px panel).
- Not checked: the minimum height (640 px) — a scripted resize left the window taller than requested and dragging the
  bottom border to the minimum did not take on this mixed-DPI multi-monitor desktop; the header doesn't depend on it.
- Scenarios 22–28 themselves (copy / paste / duplicate in the app) are not run yet: manual pending.

### 2026-10-05 — Step 12.7 marker buttons (Claude), Debug, isolated profile

The compact marker block in the corner left of the ruler, at 1440 px (the default) and 1024 px wide (the window
narrowed by dragging its border); the same build setup and project as for 12.6.
- First run: the four buttons worked, but the block was wider than the 84 px column at both widths — ▶ partly under
  the ruler, ◀ against the left edge.
- After the product owner's variant A (the block's buttons: Padding 3,0 → 1,0, Spacing 2 → 1; nothing else):
  - 1440 px and 1024 px: all four buttons whole; ▶ clear of the ruler; nothing over the ruler, TIMELINE or the track
    headers.
  - ◆+ added markers (a flag and a line on the ruler, "Marker added at 00:00:05:18"); ▶ went to the next marker and,
    at the last one, "There is no marker after the playhead."; ◀ went back and, at the first, "There is no marker before
    the playhead."; ◆− removed the marker on the playhead ("Marker removed").
  - Zoom + and Fit: the markers stayed at their time against the ruler; the block did not move.
  - The test edits were undone (title without `*`) before closing.

### 2026-10-05 — Step 12.8 New during an import (Claude), Debug, isolated profile

A new empty project; the four fixture media files picked in the Windows file dialog by a script (click into the name
box, paste, Enter) and New clicked right after the Enter, 0 to 250 ms later; nine attempts.
- The race was never hit. The check of the picked files runs synchronously on the UI thread, so a New can only come in
  during the one-frame yield between the picker closing and "Importing N files…".
- 0 and 40 ms: the click was lost while the dialog closed; the import went into the current project.
- 20 to 250 ms: the click came after the import — the files were in the project the import started in (title with
  `*`, "Added 4 media asset(s)" in the log) and New asked "Unsaved changes" (answered Don't Save; the next project
  empty).
- In no attempt did the files reach the new project; "Import stopped: another project was opened, so nothing was
  added." never appeared — the guarded path is covered by `ImportProjectChangeTests` only.
- Scenario 34: the plain import worked (4 files added and analysed). Open needs its folder picker first and Recover is
  offered at startup, so neither can race an import from the UI; they replace the project object like New.


### 2026-10-05 — Step 12.9 formal run (Claude), `d467a84`, Debug, isolated profile

The Debug build from the scratch artifacts, `USERPROFILE` / `LOCALAPPDATA` in a scratch folder, 125 % display scaling.
Fixture projects copied from the Phase 10 fade fixture (V2: pattern 5–9, text FADE 10–14; V1: nine clips 0–34 s with
fades and a 0.4 s dissolve P | Q at 32 s; A1: tone 5–9), with variants: V2 locked (`isLocked` in `project.json`), an
offline image (both its paths gone), an extra unused media file. Every action through the UI — buttons and dialogs by UI
Automation, clips, rows and the Windows file dialogs by real clicks and keys — and every result checked on the saved
`project.json`, the title (`*`), the status bar, the dialogs and screenshots. The checks of Steps 12.6–12.8 in the real
app count where the code is unchanged since (header layout, marker buttons, the import race attempts).
- Tracks (1–7): passed. Deleting an empty track asks nothing; one with clips asks "Track V1 has 9 clips. … You can
  undo this with Undo." (Cancel keeps it, Delete Track removes it, Undo brings V1 back with its clips, fades and the
  dissolve). ▲ on V1 puts it above V2 — the Preview at 6.6 s showed V2's pattern before and V1's red after, and the
  export's frame at 6.6 s was red. A2 moved above A1 (5: the sound itself not listened). After reopening: V1, V2, A1.
  During an export ▲ / ✕ / ◆+ / ◆− / Ripple Delete / Duplicate / + Video Track were off, ▶ on; afterwards all on. On the
  locked V2: ✕ → "Track V2 is locked." (no question), ▼ → the same, ▲ on V1 → "Track V2 is locked, so V1 can't move
  past it."; deleting A1 and V2 left V1, whose ✕ said "The timeline needs at least one track.".
- Media removal (8–13): passed except 12. The unused copy went without a question ("Removed spare.mp4 from the
  project."), its file stayed; pattern.mp4 asked "used by 8 clips … The file on disk is not deleted." — Cancel kept it,
  Remove removed it with its 8 clips and the dissolve ("… A dissolve was removed: its clips no longer meet."); Undo
  restored the timeline exactly, Redo removed it again; after reopening it stayed removed and pattern.mp4 was still on
  disk. The offline still.png was removed with its clip and came back by Undo; media of a clip on the locked V2: "…
  is used on track V2, which is locked." without a question. 12 not reproducible by hand (the analysis ends at once).
- Ripple / close gap (14–21): passed. Ripple of V1 red moved the later V1 clips 4 s left, V2, A1, the playhead and a
  marker stayed; two tracks closed each by its own length; a 1 s gap kept; Close Gap moved red to 4 s ("Gap closed") and
  said "There is no gap before this clip." for the first clip; ripple of P removed the dissolve with the D025 note and
  created none; ripple of the clip before P moved P | Q together with the dissolve (0.4 s) unchanged; Undo restored
  the timeline exactly; the export after it was 30 s, decode clean; Ripple Delete / Close Gap on V2 (locked) refused.
- Copy / paste / duplicate (22–28): passed except 26 partly. Ctrl+C / Ctrl+V of the 2× clip: a new clip at the end with
  every property, the same media, no new asset; V1 red + V2 FADE kept tracks and distances (36–40, 41–45), FADE's
  text and style the same; P + Q pasted without a dissolve; refused: over clips ("Can't paste: Clips would overlap on
  track V1."), onto the locked V2, after the media was removed ("… no longer in the project.") — and pasted after its
  Undo; Duplicate refused where a clip follows, and FADE' 41–45 → 45–49, the copy selected, Undo / Redo; save / reopen
  kept the pasted clips; Ctrl+C / Ctrl+V in the Inspector's text box edited the text ("FADEFADE"), no clip pasted.
- Markers (29, 31, 32): passed. ◆+ made the project dirty, a second ◆+ said "A marker is already there.", Undo / Redo,
  ◆− away from a marker "There is no marker at the playhead."; reopened: the marker kept, drawn, v3; a clip dragged near
  the marker snapped to it (20.08 s); ripple left markers in place.
- R1, R2, R3: see the Regression table.
