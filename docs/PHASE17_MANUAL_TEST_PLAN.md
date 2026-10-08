# Phase 17 — manual test plan

Run in the real app (`dotnet run --project src/App/App.csproj`, ffmpeg / ffprobe on PATH unless the scenario says
otherwise). Written at Step 17.2 (D032, product owner decisions of 2026-10-08) as a skeleton: the steps that implement the
features complete the scenarios (expected results refined by the answers to AQ1–AQ10, automated coverage named);
Step 17.9 runs them in the real app with the regression. Manual runs use an isolated profile (`USERPROFILE` /
`LOCALAPPDATA` / `APPDATA` pointed to a scratch folder), as in Phases 11–16. Every new control is checked at 1024 and
1440 px window width.

Fixture (generated outside the repository, as in Phases 10–16; to be fixed at Step 17.3): a `project.json` **v3** at
25 fps, 640 × 360 saved by the Phase 16 build — `pattern.mp4` (ffmpeg `testsrc2`, 12 s, 440 Hz sound, the frame number
drawn), `other.mp4` (`testsrc`, 8 s, 880 Hz sound), `silent.mp4` (no audio stream), `tone.wav` (6 s), `still.png`; V1 and
V2 video tracks, A1 and A2 audio tracks.

Status column — kinds of evidence, never mixed:
- **planned** — written, not yet covered;
- **auto (17.x)** — covered by automated tests only, not checked in the real app;
- **17.9 (Claude): PASS / FAIL / NOT RUN** — the formal Step 17.9 run in the real app;
- **PO** — run by the product owner.

## Unlink Audio, Link, Unlink (Steps 17.4 / 17.8)

| # | Scenario | Expected | Automated coverage | Status |
|---|---|---|---|---|
| 1 | Unlink Audio on a video clip with sound | Its sound becomes an audio clip of the same file on the first free audio track (AQ8); the video clip has no sound and no audio group in the Inspector; playback sounds the same; one Undo restores | — | planned |
| 2 | Unlink Audio with every audio track occupied over the clip | A new audio track is added in the same undo step (AQ8) | — | planned |
| 3 | Unlink Audio on a clip without sound (`silent.mp4`), on a locked track, on an already unlinked clip | Refused with a message; nothing changes, no undo step | — | planned |
| 4 | Link a video clip and an audio clip (AQ9); Unlink | The pair is marked as linked; Unlink removes the mark; timing never changes | — | planned |
| 5 | Export before and after Unlink Audio | The two exports identical (byte for byte) | — | planned |

## Linked editing (Step 17.5, AQ1–AQ7)

| # | Scenario | Expected | Automated coverage | Status |
|---|---|---|---|---|
| 6 | Select a linked clip | The partner is selected too (AQ1) | — | planned |
| 7 | Move a linked clip in time; drag it to another track | The partner moves by the same frames; only the dragged clip changes track (AQ7) | — | planned |
| 8 | Trim an edge of a linked clip | The partner's same edge follows by the same frames (AQ3) | — | planned |
| 9 | Alt + edge drag on a linked clip: J-cut (sound starts earlier) and L-cut (sound ends later) | Only that clip changes; the link stays | — | planned |
| 10 | Split at the playhead through a linked pair | Both are split; the right parts form a new link (AQ5) | — | planned |
| 11 | Ripple Delete / Close Gap / ripple trim / Insert on the video track of a linked pair | The partner on the audio track keeps its offset; other clips of the audio track stay (AQ4) | — | planned |
| 12 | The same edit when the partner would overlap a clip of its track | Refused as a whole with a message; nothing changes | — | planned |
| 13 | Any edit of a linked clip whose partner's track is locked | Refused as a whole: "Track … is locked."; nothing changes, no undo step | — | planned |
| 14 | Delete a linked clip (AQ2); speed change (AQ6); slip (AQ7) | As the answers to AQ2 / AQ6 / AQ7 | — | planned |
| 15 | Copy / Paste / Duplicate of a linked pair and of one side | Both: a new linked pair; one side: pasted unlinked | — | planned |

## Sound-only Insert / Overwrite (Step 17.7, AQ10)

| # | Scenario | Expected | Automated coverage | Status |
|---|---|---|---|---|
| 16 | Source `pattern.mp4`, "Sound only", In / Out, `,` | An audio clip of the range on the target audio track; only that track (and linked partners) ripple; the playhead at its end | — | planned |
| 17 | The same with `.` | Overwrites on the audio track by the D031 SQ9 rules | — | planned |
| 18 | "Sound only" with `silent.mp4` or an audio file | Not offered for a video without sound; an audio file inserts as before | — | planned |

## Audio fades and crossfades (Step 17.6)

| # | Scenario | Expected | Automated coverage | Status |
|---|---|---|---|---|
| 19 | Fade in / out on an unlinked audio clip | Heard as on a video clip before unlinking; the export matches the Preview | — | planned |
| 20 | Two audio clips meeting on A1: Dissolve / Crossfade | A 1 s crossfade, or the longest that fits with a message; the sound fades from A to B linearly | — | planned |
| 21 | A crossfade without enough handles | Refused / shortened with the D025 messages | — | planned |
| 22 | Edits that separate or keep the cut (move, trim, split inside the zone, delete) | As the D025 §5 dissolve rules | — | planned |
| 23 | Export of a timeline with a crossfade | The sound of the export equals the Preview's | — | planned |

## Regression (Step 17.9)

| # | Check | Expected | Status |
|---|---|---|---|
| R1 | A Phase 16 project (v3) | Opens, plays and exports exactly as in the Phase 16 build; saved as v4 differing only in `formatVersion` | planned |
| R2 | Export | The default export of the fixture identical to Phase 16's | planned |
| R3 | Phase 15–16 tools | Q / W, ripple trim, slip, In / Out, the Source viewer and Insert / Overwrite of picture and sound as before | planned |
| R4 | No ffmpeg | No crash; the reasons shown as before | planned |
| R5 | A v4 project in the Phase 16 build | Refused with the "saved by a newer version" message | planned |
| R10 | Audio device (D030 R10, carried from Phase 15) | With a real device unplugged during playback and plugged back: the status as D030 Q15 says; playback with a muted / hidden track mixes as set | planned (PO with a physical device) |
