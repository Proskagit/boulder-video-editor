# Phase 17 — manual test plan

Run in the real app (`dotnet run --project src/App/App.csproj`, ffmpeg / ffprobe on PATH unless the scenario says
otherwise). Written at Step 17.2 (D032, product owner decisions of 2026-10-08) as a skeleton; the steps that implemented
the features completed the scenarios; Step 17.9 ran them in the real app with the regression (2026-10-12, results below).
Manual runs use an isolated profile (`USERPROFILE` / `LOCALAPPDATA` / `APPDATA` pointed to a scratch folder), as in Phases
11–16, and UI Automation. Every new control is checked at 1024 and 1440 px window width (logical; the screen is at 125 %).

Fixture (generated outside the repository, as in Phases 10–16): the Phase 16 fixture — a `project.json` v3 at 25 fps,
640 × 360: `pattern.mp4` (ffmpeg `testsrc2`, 12 s, 440 Hz sound, the frame number drawn), `other.mp4` (`testsrc`, 8 s, no
sound), `tone.wav` (6 s), `still.png`; V1 `other.mp4` [0, 6 s), V2 empty, A1 `tone.wav` [0, 4 s), a marker at 4.8 s.
Builds: the Phase 17 Debug build of `453f0d6` + the toolbar wrap of the closeout; for the regression the Phase 16 build of
`main` (`79e4e78`, built from `git archive`). The edits were checked by saving and reading `project.json` (clip frames,
links, `isAudioDetached`, transitions).

Status column — kinds of evidence, never mixed:
- **auto (17.x)** — covered by automated tests only, not checked in the real app;
- **17.9 (Claude): PASS / FAIL / NOT RUN** — the formal Step 17.9 run in the real app;
- **PO** — run by the product owner.

## Unlink Audio, Link, Unlink (Steps 17.4 / 17.8)

| # | Scenario | Expected | Automated coverage | Status |
|---|---|---|---|---|
| 1 | Unlink Audio on a video clip with sound | Its sound becomes an audio clip of the same file on the first free audio track (AQ8); the video clip has no sound and no audio group in the Inspector; playback sounds the same; one Undo restores | `LinkEditTests`, `ExportUnlinkAudioEndToEndTests`, `TimelineLinkUiTests` | 17.9 (Claude): PASS — `pattern.mp4` added to V1 at 6 s, "Unlink Audio": "Sound unlinked to track A1.", an audio clip `pattern.mp4` on A1 [6 s, 18 s), both selected, no waveform on the video clip; saved: `isAudioDetached` true on the video clip |
| 2 | Unlink Audio with every audio track occupied over the clip | A new audio track is added in the same undo step (AQ8) | `LinkEditTests.Unlink_audio_takes_the_first_free_…` | auto (17.4) |
| 3 | Unlink Audio on a clip without sound, on a locked track, on an already unlinked clip | Refused with a message; nothing changes, no undo step | `LinkEditTests.Unlink_audio_is_refused_…`, `TimelineLinkUiTests` (the button's state) | auto (17.4 / 17.8) |
| 4 | Link a video clip and an audio clip (AQ9); Unlink | The pair is marked as linked; Unlink removes the mark; timing never changes | `LinkEditTests`, `TimelineLinkUiTests` | 17.9 (Claude): PASS — "Link": "Clips linked.", both labels "🔗 pattern.mp4", the button reads "Unlink"; Unlink / Undo: auto |
| 5 | Export before and after Unlink Audio | The two exports identical (byte for byte) | `ExportUnlinkAudioEndToEndTests` (every canvas and sample) | auto (17.4) |

## Linked editing (Step 17.5, AQ1–AQ7)

| # | Scenario | Expected | Automated coverage | Status |
|---|---|---|---|---|
| 6 | Select a linked clip | The partner is selected too (AQ1) | `TimelineLinkUiTests.Selecting_a_linked_clip_…` | auto (17.8); in the app the pair is selected by the click before Ctrl+C in 15 (both copied) |
| 7 | Move a linked clip in time; drag it to another track | The partner moves by the same frames; only the dragged clip changes track (AQ7) | `LinkedEditTests.Moving_…`, `Dragging_a_linked_pair_…` | 17.9 (Claude): PASS for the move — the video dragged +1 s: video [7 s, 17 s), sound [7 s, 18 s) (saved file). To another track: auto |
| 8 | Trim an edge of a linked clip | The partner's same edge follows by the same frames (AQ3) | `LinkedEditTests.Trimming_…`, `…the_smaller_trim` | 17.9 (Claude): PASS — the video's end dragged 18 s → 16 s: both ends at 16 s |
| 9 | Alt + edge drag on a linked clip: J-cut and L-cut | Only that clip changes; the link stays | `LinkedEditTests.Alt_edge_…`, `TimelineLinkUiTests.Alt_edge_…` | 17.9 (Claude): PASS for the L cut — Alt + the sound's end 16 s → 17 s: the sound ends at 17 s, the video at 16 s, the link kept. J cut: auto |
| 10 | Split at the playhead through a linked pair | Both are split; the right parts form a new link (AQ5) | `LinkedEditTests.Splitting_…` | 17.9 (Claude): PASS — S at 12 s with the pair selected: four clips, the right parts share a new link (saved: `377d71ac` / `4e0daf8c`) |
| 11 | Ripple Delete / Close Gap / ripple trim / Insert on the video track of a linked pair | The partner keeps its offset; other clips of the audio track stay (AQ4) | `LinkedEditTests.A_ripple_…`, `An_insert_before_…`, `A_ripple_trim_…` | auto (17.5) |
| 12 | The same edit when the partner would overlap a clip of its track | Refused as a whole with a message; nothing changes | `LinkedEditTests.A_partner_that_would_overlap_…` | auto (17.5) |
| 13 | Any edit of a linked clip whose partner's track is locked | Refused as a whole: "Track … is locked."; nothing changes, no undo step | `LinkedEditTests.Every_edit_…` (14 edit kinds) | 17.9 (Claude): PASS — A1 locked, the linked video dragged +2 s: "Track A1 is locked.", nothing moved |
| 14 | Delete a linked clip (AQ2); speed change (AQ6); slip (AQ7) | The partner goes / takes the speed / slips too | `LinkedEditTests` | auto (17.5) |
| 15 | Copy / Paste / Duplicate of a linked pair and of one side | Both: a new linked pair; one side: pasted unlinked | `LinkedEditTests.A_copied_pair_…` | 17.9 (Claude): PASS for the pair — the video clicked (the pair selected), Ctrl+C, playhead 24 s, Ctrl+V: "2 clips pasted", a new pair at 24 s with its own link `86e1bcee`. One side: auto |

## Sound-only Insert / Overwrite (Step 17.7, AQ10)

| # | Scenario | Expected | Automated coverage | Status |
|---|---|---|---|---|
| 16 | Source `pattern.mp4`, "Sound only", `,` | An audio clip of the range on the target audio track; only that track (and linked partners) ripple; the playhead at its end | `SoundOnlyInsertTests`, `SourceViewerTests.Sound_only_…`, `ExportInsertOverwriteEndToEndTests` | 17.9 (Claude): PASS — "Sound only" on, `,` at 30 s: "Inserted the sound of pattern.mp4", A1 [30 s, 42 s) an audio clip of `pattern.mp4`, no video clip added. (The first attempt pressed the toggle with an invoke the toggle does not support, so the whole file went in; undone — a test-driver issue, not the app's.) |
| 17 | The same with `.` | Overwrites on the audio track by the D031 SQ9 rules | `SoundOnlyInsertTests` (overwrite shapes) | auto (17.7) |
| 18 | "Sound only" with a video without sound or an audio file | Not offered for a video without sound; an audio file inserts as before | `SourceViewerTests.Sound_only_…`, `SoundOnlyInsertTests` | 17.9 (Claude): PASS — `other.mp4` (no sound) in Source: no "Sound only" |

## Audio fades and crossfades (Step 17.6)

| # | Scenario | Expected | Automated coverage | Status |
|---|---|---|---|---|
| 19 | Fade in / out on an unlinked audio clip | Heard as on a video clip before unlinking; the export matches the Preview | `ExportUnlinkAudioEndToEndTests`, `ExportAudioContractTests` | auto (17.4 / 17.6) |
| 20 | Two audio clips meeting on A1: Dissolve | A 1 s crossfade, or the longest that fits with a message; the sound fades from A to B linearly | `CrossfadeEditTests`, `ExportAudioContractTests`, `TimelineLinkUiTests` | 17.9 (Claude): PASS — the two sound halves of the split pair selected (left, Ctrl + right), "Dissolve": "Crossfade added: 25 frames.", the zone drawn on A1, the Inspector reads CROSSFADE, "Remove Crossfade", "Longest that fits here: 251 frames"; saved as `crossfade`, 25 frames. Heard: not judged by ear (the mix is automated sample for sample) |
| 21 | A crossfade without enough handles | Refused / shortened with the D025 messages | `CrossfadeEditTests` | auto (17.6) |
| 22 | Edits that separate or keep the cut (move, trim, split inside the zone, delete) | As the D025 §5 dissolve rules | `CrossfadeEditTests` | auto (17.6) |
| 23 | Export of a timeline with a crossfade | The sound of the export equals the Preview's | `ExportAudioContractTests` (6 crossfade tests), `ExportCrossfadeEndToEndTests` | auto (17.6) |

## Layout (Step 17.8)

| # | Check | Expected | Status |
|---|---|---|---|
| L1 | 1440 px | Unlink Audio / Unlink, Link in the timeline tools, one row; Sound only on the source bar | 17.9 (Claude): PASS — one row (every tool at the same y) |
| L2 | 1024 px | Every tool reachable | 17.9 (Claude): FAIL, then fixed and PASS — the two new buttons pushed −, + and Fit past the window's edge (the tools ended about 90 logical px beyond it); the tools now wrap onto a second row when too narrow (`TimelineView.axaml`, a `WrapPanel`); re-checked: every tool visible, −, + and Fit on the second row; the source bar's Sound only / Insert / Overwrite fit |

## Regression (Step 17.9)

| # | Check | Expected | Status |
|---|---|---|---|
| R1 | A Phase 16 project (v3) | Opens, plays and exports exactly as in the Phase 16 build; saved as v4 differing only in `formatVersion` | 17.9 (Claude): PASS — the fixture opened and saved by the Phase 16 build, then opened and saved by the Phase 17 build: 236 lines each, the only difference `"formatVersion": 3` → `4` |
| R2 | Export | The default export of the fixture identical to Phase 16's | 17.9 (Claude): PASS — both builds' default exports of the same project identical (216 586 bytes, the same SHA-256) |
| R3 | Phase 15–16 tools | Q / W, ripple trim, slip, In / Out, the Source viewer and Insert / Overwrite of picture and sound as before | 17.9 (Claude): PASS for the Source viewer, the playhead and Insert of the whole file (the first attempt of 16); the rest auto (the full suite, Phase 15–16 tests unchanged) |
| R4 | No ffmpeg | No crash; the reasons shown as before | 17.9 (Claude): PASS — ffmpeg / ffprobe off PATH: the v4 project with links opens, Space: "Playback is unavailable: FFmpeg could not be found. …", the app runs |
| R5 | A v4 project in the Phase 16 build | Refused with the "saved by a newer version" message | 17.9 (Claude): PASS — "Couldn't open the project. This project was saved by a newer version of AI Video Editor and can't be opened." |
| R10 | Audio device (D030 R10, carried from Phase 15) | With a real device unplugged during playback and plugged back: the status as D030 Q15 says; playback with a muted / hidden track mixes as set | NOT RUN — needs a physical audio-output interruption (PO) |
