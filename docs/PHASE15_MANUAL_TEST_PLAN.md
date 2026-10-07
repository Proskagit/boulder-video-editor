# Phase 15 — manual test plan

Run in the real app (`dotnet run --project src/App/App.csproj`, ffmpeg / ffprobe on PATH unless the scenario says
otherwise). Written at Step 15.2 (D030, product owner decisions of 2026-10-07) as a skeleton; each step that implements a
feature completes its scenarios (exact texts, the answers to D030 Q1–Q16, automated coverage, fixtures) and records its
development-time run; Step 15.9 runs the scenarios not yet run in the real app and the regression R1–R10. Logs:
`%LOCALAPPDATA%\AiVideoEditor\logs`. Manual runs use an isolated profile (`USERPROFILE` / `LOCALAPPDATA` / `APPDATA`
pointed to a scratch folder), as in Phases 11–14. Every new control is checked at 1024 and 1440 px window width.

Fixtures: written by the steps that need them (scripts under `tools/manual`, projects and media outside the
repository, as in Phases 10–14): two video tracks and one audio track; clips at 1× and at 0.5× / 2×; clips with fades and
a dissolve; gaps between clips; a text clip; an image; an offline asset; for R2 real phone recordings of bouldering
attempts (portrait, several minutes each, 30 or 60 fps), or generated stand-ins of the same shape when none are at hand
(recorded as such).

Status column — kinds of evidence, never mixed:
- **planned** — written at 15.2, not implemented yet;
- **auto (15.x); manual pending** — implemented and covered by automated tests at step 15.x, not yet run in the real app;
- **auto** — covered by automated tests only, not checked in the real app;
- **app 15.x (Claude), passed** — checked in the real app by Claude while implementing / accepting step 15.x;
- **15.9 (Claude): PASS / FAIL / BLOCKED / NOT RUN** — the formal Step 15.9 run;
- **PO** — run by the product owner.
A scenario is marked passed only for the run that actually checked it; the "Automated coverage" column is separate.

## Step 15.3 — track controls (D030 §4)

| # | Scenario | Steps | Expected | Automated coverage | Status |
|---|---|---|---|---|---|
| 1 | Hide a video track | Two overlapping video clips on V1 / V2; hide V2 | The Preview shows V1's clip; V2's clips drawn dimmed; V2's sound still plays; the title gets `*`; Undo shows V2 again and is clean at the save point | `TrackStateEditTests` (hide: layers, sound kept, length), `TimelineTrackStateUiTests`, `ExportTrackStateEndToEndTests` | app 15.3 (Claude), passed — 👁 on V1 of the Phase 14 R5 fixture: title `*`, "Track V1 hidden", V1's clips dimmed, the Preview at 0:00 black; Undo → title clean, Redo → `*` |
| 2 | Mute a track | Mute A1 (music), then V1 (video with sound) | A1 silent, then V1's sound silent, the picture unchanged; waveforms dimmed; Undo / Redo | `TrackStateEditTests` (video / audio track mute), `ExportTrackStateEndToEndTests` (PCM) | app 15.3 (Claude), passed — M on V1 and A1: "Track V1 muted", "Track A1 muted"; the exported file silent (scenario 6). The Preview's sound not judged by ear |
| 3 | Lock a track | Lock V1; try move, trim, split, delete, ripple delete, paste, speed, a property, a dissolve, Q / W, ripple drag, slip, delete / move the track | Each refused with "Track V1 is locked." (or the step's text), nothing changes, no undo step; copy (Ctrl+C) still works; unlock → edits work again | `TrackStateEditTests.A_locked_track_refuses_every_ordinary_edit_and_nothing_changes` (18 edits), `TimelineTrackStateUiTests` | app 15.3 (Claude), passed — 🔒 on V1: "Track V1 locked"; ✕ and ▲ on V1 → "Track V1 is locked.", nothing changed; 🔒 on A1, ✕ on A1 → "Track A1 is locked.". Clip edits by pointer not driven (UI Automation) — covered by the service test; Q / W, ripple drag and slip come with 15.4–15.6 |
| 4 | Toggles on a locked track | Lock V1, then hide / mute it | As decided by Q13 | `TrackStateEditTests.Mute_and_hide_change_on_a_locked_track_as_undoable_steps`, `TimelineTrackStateUiTests` | app 15.3 (Claude), passed — Q13 (allowed): on the locked V1 M → "Track V1 muted", 👁 → "Track V1 shown", 👁 → "Track V1 hidden" |
| 5 | Save and reopen | Hide V2, mute A1, lock V1; Save; reopen | The flags come back; the project clean; `project.json` `formatVersion` 3, only `isHidden` / `isMuted` / `isLocked` changed | `TrackStateEditTests` (save / reopen / save byte for byte), `TimelineTrackStateUiTests` (headers after Open) | app 15.3 (Claude), passed — saved: `formatVersion` 3, V1 muted / hidden / locked, A1 muted / locked; against the Phase 14 save of the same fixture only the six flag values and `modifiedAt` differ; reopened clean; M / 👁 on V1 and 🔒 on A1 then said "unmuted" / "shown" / "unlocked" (the state was loaded); three Undo → clean, nothing left to undo |
| 6 | Export | After 5, export | No V2 picture, no A1 sound, V2's video sound present; the length unchanged; the output matches the Preview | `ExportTrackStateEndToEndTests` (Preview = export canvas byte for byte, PCM, length) | app 15.3 (Claude), passed — Export through the UI with V1 hidden + muted + locked, A1 muted + locked: 27.000 s, 675 frames (as the export of the same fixture without the flags); frames at 1 s / 15 s black (0, 0, 0), at 6 s only V2's text "R148"; audio max −91 dB (silence); the unflagged export: red at 6 s, max −19.4 dB |
| 7 | During an export | Start an export, try the toggles | Disabled; enabled again after the export | `TimelineTrackStateUiTests.The_toggles_are_disabled_while_an_export_runs` | auto — the fixture's export takes about 2 s, too short to probe the header by hand |
| 8 | Header layout | Window at 1024 and 1440 px, video and audio tracks | All header controls visible and clickable, tooltips present, nothing cut | — | app 15.3 (Claude), passed — screenshots at 1024 × 768 and 1440 × 820 (125 % scaling): V2 / V1 with M 👁 🔒, A1 / A2 with M 🔒, ▲ ▼ ✕ below, nothing cut; off dim, on red / blue / amber; A2's mute from the Phase 14 file shown on |

## Step 15.4 — trim to the playhead (D030 §5)

| # | Scenario | Steps | Expected | Automated coverage | Status |
|---|---|---|---|---|---|
| 9 | `Q` plain | Select a clip, playhead inside it, `Q` | The clip starts at the playhead, the frame under the playhead unchanged in the Preview; a gap before it; one Undo restores it | `TrimToPlayheadTests` (plain = the edge trim at 5 rates × 5 speeds), `TimelineTrimToPlayheadUiTests` | app 15.4 (Claude), passed — R5 fixture, red.mp4 (5–9 s) selected by a click, playhead 6 s (Home, Shift+→ ×6), Q: red starts at 6 s (screenshot), "Trimmed the start of the clip to the playhead", title `*`, the playhead stays 00:00:06:00; Ctrl+Z → clean |
| 10 | `W` plain | Select a clip, playhead inside it, `W` | The clip ends at the playhead; a gap after it; Undo | `TrimToPlayheadTests` | app 15.4 (Claude), passed — W at 7 s: "Trimmed the end of the clip to the playhead", the next clip (10 s) not moved; Ctrl+Z |
| 11 | Ripple start | Clip with later clips and a gap on its track; the ripple trim start (Q2's access) | The clip keeps its start and begins with the frame that was at the playhead; later clips move left by the trimmed length, the gap between them kept; other tracks and markers unchanged; the playhead as decided (Q5) | `TrimToPlayheadTests` (ripple = trim + move at 5 rates × 5 speeds, Q5), `TimelineTrimToPlayheadUiTests` (seek) | app 15.4 (Claude), passed — Shift+Q at 6 s: red keeps 5 s, the next clip 50 px (1 s) to the left, the playhead 00:00:05:00 (Q5), "Ripple trimmed the start…"; Ctrl+Z restores, title clean |
| 12 | Ripple end | The ripple trim end on the same layout | Later clips move left; no gap at the trimmed end | `TrimToPlayheadTests` | app 15.4 (Claude), passed — Shift+W at 7 s: the next clip 100 px (2 s) to the left, the playhead stays 00:00:07:00; Ctrl+Z restores |
| 13 | Speed clip | A 0.5× and a 2× clip: `Q`, `W`, ripple variants | Content as expected at the cut (no jump of the frame under the playhead for a plain trim), the sound in sync | `TrimToPlayheadTests` (0.25× / 0.5× / 2× / 4×, the D022 invariant) | app 15.4 (Claude), passed — the 2× clip (10–12 s): Shift+Q at 11 s, playhead → 10 s; the 0.5× clip (13–17 s): W at 15 s; both done and undone. Sound sync not judged by ear |
| 14 | Target rules | Playhead outside every selected clip; at a clip's first frame; no selection; two selected clips on two tracks | Refused with a message / nothing to do / as decided by Q1; two tracks trimmed in one undo step | `TrimToPlayheadTests` (boundaries, one frame inside, several clips, some eligible), `TimelineTrimToPlayheadUiTests` | app 15.4 (Claude), passed — red selected, playhead 3 s: Q → "The playhead is not inside the selected clip(s).", title clean; at 5 s (its first frame) Shift+W → the same. Several clips by hand not driven (Ctrl+click) — automated |
| 15 | Fade | A clip with a 1 s fade in: `Q` 0.5 s into it; a clip with a 2 s fade out trimmed to 1 s with `W` | The fade in stays on the new start; the fade out cut to the clip's length; Undo restores both | `TrimToPlayheadTests.Fades_stay_on_their_edges_and_are_cut_to_a_shorter_clip_undo_restores_them` | auto |

## Step 15.5 — ripple trim by dragging (D030 §6)

| # | Scenario | Steps | Expected | Automated coverage | Status |
|---|---|---|---|---|---|
| 16 | Drag inward with the modifier | Ripple modifier (Q3) + drag a clip's end left | During the drag the later clips follow; release → the same result as the ripple command at that frame | `RippleTrimDragTests` (equivalence with Shift+Q / Shift+W at 5 rates × 5 speeds, the preview = the release), `TimelineRippleDragUiTests` | app 15.5 (Claude), passed — real pointer, Shift held at the press: red's end 9 → 7 s → red 5–7 s, the later clips 2 s to the left (10 → 8, 13 → 11, 18 → 16 s); red's start 5 → 6 s → red still at 5 s, 3 s long, the later clips 1 s to the left; the playhead stays 00:00:00:00; each Ctrl+Z restores, title clean. At 1024 px: red's end 9 → 8 s the same way; Ctrl+Z / Ctrl+Y |
| 17 | Drag outward | The same, outward, then up to the end of the source | Later clips move right; the edge stops at the end of the source | `RippleTrimDragTests` (outward end / start at 0.5× / 1× / 2× / 0.25×, the source limits, a clip at 0, a dissolve's handle) | app 15.5 (Claude), passed — the 0.5× clip's end 17 → 18 s: 13–18 s, still.png 18 → 19 s; undone. The source-end stop automated |
| 18 | `Esc` | Start a ripple drag, press `Esc` | Everything back, no undo step | `TimelineRippleDragUiTests.Esc_after_moving_restores_everything_and_changes_nothing` | app 15.5 (Claude), passed — Shift+drag red's end 9 → 7 s, Esc before the release, then the release: every clip where it was, title clean, Undo disabled, the playhead unchanged |
| 19 | Without the modifier | A plain edge drag | Unchanged Phase 4–14 trim (clamped to the neighbour) | `TimelineRippleDragUiTests` (ordinary trim clamped, nothing else moved), `RippleTrimDragTests` (the ordinary trim still removes a dissolve whose cut it opens), every Phase 4–14 trim test unchanged | app 15.5 (Claude), passed — red's end 9 → 11 s without Shift: stops at 10 s (the next clip), nothing else moves; the dissolve's cut edge 25 → 24 s without Shift: the dissolve removed with "A dissolve was removed: its clips no longer meet." (D025 §5, unchanged), with Shift: kept at 24 s, B 24–26 s |

## Step 15.6 — slip (D030 §7)

| # | Scenario | Steps | Expected | Automated coverage | Status |
|---|---|---|---|---|---|
| 20 | Slip a clip | Slip modifier (Q3) + drag the body of a video clip with sound | Start and length unchanged; the content and its sound shift; the source in / out shown during the drag (Q12); one undo step | `SlipTests` (5 rates × 5 speeds: only the source range moves, by D022's amount), `TimelineSlipUiTests`, `ExportRippleEndToEndTests` (slip parity, picture and sound) | app 15.6 (Claude), passed — R5 fixture, the first pattern clip (0–4 s, 1×, source 0–4 s), playhead 2 s; Alt pressed and released at the press (the mode stays slip), the body dragged 1 s left: during the drag the clip shows "In 00:00:01:00  Out 00:00:05:00", its place, the Preview, the title and Undo unchanged (screenshot); on release the Preview shows the source 1 s later at the same playhead, title `*`, playhead 00:00:02:00; Ctrl+Z / Ctrl+Y / Ctrl+Z; the same at 1024 px |
| 21 | Limits | Slip to the start and to the end of the source | Stops at the source's first / last usable frame | `SlipTests` (source start / end at 0.25× / 1× / 2× / 4×, no room → no step) | app 15.6 (Claude), passed — the same clip dragged right (earlier content than its source start): "In 00:00:00:00 … (limit)" during the drag, "The slip stopped at the start of the source.", nothing changed, no undo step. The source-end stop automated |
| 22 | Speed | Slip a 2× clip | Content shifts at the clip's speed; length unchanged | `SlipTests` (a timeline frame = 0.25 / 0.5 / 1 / 2 / 4 source frames) | app 15.6 (Claude), passed — the 0.5× clip (13–17 s, source 0–2 s) dragged 1 s (25 frames) left: "In 00:00:00:12  Out 00:00:02:12" (0.5 s of source, shown at 25 fps) — the speed counted; undone. The 2× clip automated |
| 23 | Refused | Slip an image, a text, a clip on a locked track | Refused / no slip gesture; nothing changes | `SlipTests`, `TimelineSlipUiTests` (image / text, locked) | app 15.6 (Claude), passed — Alt + drag on still.png and on the text "R148": "Only video and audio clips can be slipped.", nothing moved; V1 locked: "Track V1 is locked.", nothing changed; unlocked. A dissolve: B (25–27 s, source 2.4–4.4 s) dragged 3 s right: "In 00:00:00:05 … (limit)", "The slip stopped where a dissolve needs the clip's source.", the dissolve kept; undone. Esc during a slip: the text gone, nothing changed, Undo disabled, playhead 2 s |
| 24 | Export | Export after 20 | The output matches the Preview | `ExportRippleEndToEndTests.An_export_after_a_slip_shows_the_slipped_source_in_the_same_place_and_matches_its_preview` | auto — the exported picture and sound after a slip, and the Preview equal to the export canvas |

## Step 15.7 — In / Out range (D030 §8)

| # | Scenario | Steps | Expected | Automated coverage | Status |
|---|---|---|---|---|---|
| 25 | Set and show | `I` at 2 s, `O` at 5 s | A band on the ruler and a shade over the tracks; the title has no `*`; Undo does not touch the range | `InOutRangeTests`, `InOutRangeUiTests` (points, bar, In / Out lines, playhead-in-range) | app 15.7 (Claude), passed — R5 fixture copy: Home, Shift+→ ×5, I → "In set at 00:00:05:00."; Shift+→ ×3, O → "Out set after 00:00:08:00 (that frame is included)."; the bar on the ruler, the shade over the tracks, the In / Out lines (Out right after the playhead's frame) and the ✕ shown (screenshot); a mouse click on the ruler at 6 s then I → In 6 s; the title never `*`, Undo disabled |
| 26 | Rules | `I` after Out; `O` before In; only In; only Out; clear (Q11) | As decided by Q7 / Q8 / Q11 | `InOutRangeTests` (Q7 / Q8, only In / only Out, clamp), `InOutRangeUiTests` | app 15.7 (Claude), passed — the ✕ on the bar: "In / Out cleared.", the bar and the ✕ gone, the title clean; the ordering rules automated |
| 27 | Loop | Loop on, range set, Play from inside and from outside the range | Playback loops In → Out, starting at In from outside (Q9); Loop off ignores the range | `LoopPlaybackTests` (Loop off ignores the range; Loop on loops [In, Out), from before In and after Out starts at In; clearing frees it), `AudioPlaybackServiceTests` (silence from Out) | app 15.7 (Claude), passed — range [5 s, 8 s]: Loop off, Play from 6 s: timecodes 06:11 … 08:08 … 11:15 (through Out); Loop on (Ctrl+L), Play from 2 s: 05:11, 05:23 … 07:20, 05:05 … 08:00, 05:10 … (24 samples, all in 5:00–8:00); from 15 s: 05:20 … — starts at In. The sound at Out judged only by the automated test |
| 28 | Range export | Export with a range (Q10) | The file covers exactly the range; first / last frame and sound match the Preview at In / Out − 1 frame; duration Out − In | `ExportRangeEndToEndTests` (frames and samples equal the whole export's), `ExportRangeTests`, `ExportWorkflowTests` | app 15.7 (Claude), passed — Export: the question "An In / Out range is set: 00:00:05:00 to 00:00:08:01 (76 frames). Export only the range, or the entire sequence?" with Range / Entire sequence / Cancel; Range → 76 frames, 3.040 s, red (red.mp4 at 5–9 s); with gone-still.png offline (used only at 18–22 s) the range exported, Entire sequence → "Export not possible: 'gone-still.png' is offline (1 clip, first at 00:00:18.000)."; on an intact copy with the range set, Entire sequence → 675 frames, 27.000 s |
| 29 | Not saved | Set a range, Save, close and reopen; set a range, wait for the autosave, kill the process, Recover | No range after reopening and after the recovery; `project.json` and the recovery file contain no range | `InOutRangeUiTests.The_range_is_not_saved_and_New_Open_and_Recover_start_without_it` | app 15.7 (Claude), passed (partly) — every range change left the title clean and Undo disabled; the copy's `project.json` unchanged by the session (hash), `formatVersion` 3, no range field. New / Open / Recover clearing automated |
| 30 | Edits | Ripple delete before the range; change the frame rate | The range stays at its time (nearest frame after the rate change) | `InOutRangeUiTests` (edits, undo / redo, zoom, frame-rate change) | auto |

## Step 15.8 — UX fixes (D030 §9)

| # | Scenario | Steps | Expected | Automated coverage | Status |
|---|---|---|---|---|---|
| 31 | Text size | `+ Text` on 1920 × 1080, 3840 × 2160, 1080 × 1920, 1280 × 720 | Inspector Size 48, 96, 85.33, 32; existing text clips unchanged | `NewTextFontSizeTests` (720 / 1080 / 1440 / 2160 / 360 / portrait; D028 resize with Scale kept; copies; save / open) | app 15.8 (Claude), passed — a new project (1920 × 1080): + Text → Inspector Size 48; Project Settings → Portrait 1080 × 1920: that text 27 (D028), + Text → 85,33; → 2560 × 1440: + Text → 64 (the portrait text 85.33 × 0.75 = 64); → 3840 × 2160: + Text → 96 (the 1440 text 64 × 1.5 = 96). The 15.7 build on 3840 × 2160: + Text → 48 (before the change) |
| 32 | Audio status | Play, unplug / disable the audio device (status "Playing without sound…"), plug it back, Play | The stale status gone once the sound is back; a second loss reported again (Q15) | `PreviewSoundStatusTests` | auto (15.7 — Q15 taken into 15.7); manual pending: needs a real audio device unplugged and plugged back (PO) |

## Regression (Step 15.9)

| # | Scenario | Expected | Status |
|---|---|---|---|
| R1 | A project saved by the Phase 14 build (`7200976`, built apart from `git archive`): non-default canvas, rate and export settings, fades, a dissolve, markers, a text clip, and track flags set by editing the file | Opens with everything, the flags shown in the headers; a plain Save writes it byte for byte; the default export byte-identical to Phase 14's; a file saved by Phase 15 with flags opens in Phase 14 and renders the same | partly app 15.3 (Claude), passed — the Phase 14 R5 fixture (`r148/r5/p14`, saved by the Phase 14 build; A2 muted) opened in place: A2's M shown on; a plain Save wrote it byte for byte (SHA-256 `d3d13909…`); a copy saved by Phase 15 and by Phase 14 identical; the Phase 15 file with V1 hidden / muted / locked and A1 muted / locked opened in the Phase 14 build ("V1 🔒", "A1 🔒", V1 not drawn) and was saved by it byte for byte. The default-export comparison at 15.9 |
| R2 | Real bouldering workflow: 6–10 portrait recordings on a 1080 × 1920 canvas; for each attempt `Q` / `W` / ripple trims to the send, a slip of one clip, 0.5× on the crux, a new text with the problem's name (85.33 px), music on A1 muted / unmuted, V2 overlay hidden / shown; an In / Out loop over the crux; a full export and a range export | Every step in a few actions; Preview and both files agree; save / reopen keeps the edit (not the range); no error in the log | planned |
| R3 | Track mute / hide / lock (scenarios 1–8) end to end with save, reopen, undo and export | As in 1–8 | planned |
| R4 | Trim / ripple trim (9–19) on 30 and 29.97 fps projects with gaps and several tracks | As in 9–19; nothing off the frame grid | planned |
| R5 | Slip (20–24) incl. a clip at 0.5× and a dissolve next to the slipped clip | As in 20–24; the dissolve's handle limits the slip | planned |
| R6 | In / Out loop and range export (25–30) | As in 25–30; the range never saved | planned |
| R7 | Dissolve / fade edges: plain `Q` / `W` on a dissolve's cut edge (dissolve removed, note); the ripple variant on it (Q4); a far-edge trim stopped by a zone (Q6); an inactive (PO-8) fade becoming active after its dissolve is removed; a fade cut by a trim | As decided; Preview and export agree; Undo restores everything | partly app 15.4 (Claude), passed — the fixture's dissolve 23–25 | 25–27 s (0.4 s): B selected, playhead 26 s, Q → "Not trimmed where a dissolve is on the clip's start: Shift+Q trims it and keeps the dissolve.", nothing changed; Shift+Q → B 25–26 s, the dissolve kept at 25 s, the Preview at 25 s shows B's source 3.4 s (was at 26 s), the playhead 25 s; A selected, playhead 24 s 24 f, Q → "The trim stopped where a dissolve needs the clip's frames.", the dissolve kept (PO-8 inactive fades of the fixture unchanged). Fades and the rest at 15.9 |
| R8 | Phase 10–14 regression: fade / dissolve add and remove, relink and Find Missing, Recent, track delete / reorder, media removal, ripple delete / close gap, copy / paste / duplicate, markers, Project Settings (canvas, rate, export settings), autosave / recovery; `docs/EXPORT_MANUAL_TEST_PLAN.md` default export | As before | planned |
| R9 | No ffmpeg (ffmpeg / ffprobe hidden from PATH): open a project, toggle tracks, trim, slip, set a range, export | Edits work on analysed metadata; the same messages as Phase 14; export refused as before; no crash | planned |
| R10 | Audio device: scenario 32 with a real device, plus playback with a muted / hidden track | As in 32; the mix as set | planned |

## Results log

*(filled by the steps' development-time runs and by Step 15.9)*

### 2026-10-07 — Step 15.3 development-time run (Claude), Debug build of the 15.3 tree

- Builds: the Phase 15 Debug build copied to a scratch folder; the Phase 14 build from the Phase 14 closeout run
  (`p14bin`) for the compatibility checks. Isolated profiles (`LOCALAPPDATA` / `APPDATA` / `USERPROFILE`),
  `--open-project`, UI Automation (buttons by name: M, 👁, 🔒, ✕, ▲, Save, Undo, Redo, Export; the native save dialog
  through `WM_SETTEXT` / `BM_CLICK`); output files checked with ffprobe / ffmpeg (`volumedetect`, 1 × 1 frame averages).
- Fixture: the Phase 14 R5 fixture (`r148/r5/p14`: 640 × 360, 25 fps, 7 clips on V1, a text on V2, a tone on A1, A2
  muted). R1 in place (a plain Save, byte for byte); everything else on a copy. A copied project resolves its media by
  the stored absolute path, so its first Save rewrites the four `relativePath`s — the Phase 14 build does exactly the
  same with the same copy (identical files): D014's existing rule, not a Phase 15 change.
- An automation slip, not an app defect: a Phase 15 instance left open on the original fixture received one Export
  (default name, into the fixture folder, with the fixture's own Phase 14 state); the file was moved out to the scratch
  folder at once and used as the unflagged baseline of scenario 6; the fixture's `project.json` is unchanged (hash
  checked).
- Scenarios 1–6, 8 and R1 (partly): see the tables. Scenario 7 automated only.

### 2026-10-07 — Step 15.4 development-time run (Claude), Debug build of the 15.4 tree

- The same setup as 15.3, on a fresh copy of the Phase 14 R5 fixture (the original's `project.json` unchanged, hash
  checked). Clips selected by a mouse click on their timeline label (UI Automation finds it, `SetCursorPos` /
  `mouse_event` click it); the playhead set with Home and Shift+→ / → (keys sent as extended keys — without that flag
  Windows drops Shift for the arrow keys); Q / W / Shift+Q / Shift+W / Ctrl+Z by `keybd_event` to the foreground app.
  Results read from the status bar, the timecode, the window title, the clip labels' positions and screenshots.
- Scenarios 9–14 and R7 (partly): see the tables; lock: V1 locked by its 🔒, then Q and Shift+Q on red → "Track V1 is
  locked.", unlocked again. At the end every change undone: title clean, Undo disabled. Scenario 15 automated only.

### 2026-10-07 — Step 15.5 development-time run (Claude), Debug build of the 15.5 tree

- A fresh copy of the Phase 14 R5 fixture (the original unchanged, hash checked), the window at 1440 × 820, then 1024 ×
  768. Real pointer drags: `SetCursorPos` / `mouse_event` from 3 logical px inside the clip's edge handle to the
  target time, in eight steps; the time axis read from the ruler labels and the clip labels (each clip's start and end
  read back from its label to ±0.02 s). Shift pressed before the button and released right after it — so the ripple
  mode is the press's (D030 "Decided at the start of Step 15.5"). Esc by `keybd_event` while the button was held.
- Scenarios 16–19: see the table. Lock: V1 locked by its 🔒, Shift+drag red's end 9 → 7 s → "Track V1 is locked.",
  nothing moved; unlocked. At the end all undone back to the save point (title clean, Undo disabled).

### 2026-10-07 — Step 15.6 development-time run (Claude), Debug build of the 15.6 tree

- A fresh copy of the Phase 14 R5 fixture (the original unchanged, hash checked), 1440 × 820 then 1024 × 768. Real
  pointer drags of clip bodies: Alt (`keybd_event` VK_MENU) pressed before the button and released right after it — the
  gesture stays a slip; the clip's text read through UI Automation while the button was still held, and screenshots
  before / during / after.
- Scenarios 20–23: see the table; 24 automated. At the end every change undone: title clean, Undo disabled, the playhead
  where it was set (2 s) throughout.

### 2026-10-07 — Step 15.7 development-time run (Claude), Debug build of the 15.7 tree

- Fresh copies of the Phase 14 R5 fixture (the original unchanged, hash checked): one with `still.png` pointed to a missing
  file (offline, used only at 18–22 s), one intact. Keys by `keybd_event` (I, O, Space, Ctrl+L, navigation keys as
  extended keys), the ruler by a mouse click, the range's ✕ and the export choice by UI Automation (the choice dialog
  is a child window of the main window), the native save dialog as before; timecodes sampled every 300–400 ms while
  playing; the outputs checked with ffprobe / ffmpeg.
- A first ruler click landed below the ruler (on V2) and selected the text clip — the click position, not the app: a
  ruler click moves the playhead in the 15.7 and the 15.6 builds alike (checked side by side). Two exports took the save
  dialog's default name inside the scratch copies' folders; the files were moved to the scratch output folder.
- Scenarios 25–29: see the table; 30 automated; 32 (the audio status) automated only — it needs a real device
  removed and restored.

### 2026-10-07 — Step 15.8 development-time run (Claude), Debug build of the 15.8 tree

- A new project in the app; canvases changed through Project Settings (the preset box, Apply) by UI Automation; the
  Inspector's Size read from its field. Scenario 31: see the table.
- An observation, not a 15.8 change: after + Text with the playhead beyond the sequence's previous end (e.g. a new
  project, the canvas changed, the playhead at 6 s), the Preview stays black until the next seek — the same in the 15.7
  build (checked side by side); with the playhead inside the sequence the text shows at once. Recorded in
  `progress.md` Known issues.
