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
| 1 | Hide a video track | Two overlapping video clips on V1 / V2; hide V2 | The Preview shows V1's clip; V2's clips drawn dimmed; V2's sound still plays; the title gets `*`; Undo shows V2 again and is clean at the save point | | planned |
| 2 | Mute a track | Mute A1 (music), then V1 (video with sound) | A1 silent, then V1's sound silent, the picture unchanged; waveforms dimmed; Undo / Redo | | planned |
| 3 | Lock a track | Lock V1; try move, trim, split, delete, ripple delete, paste, speed, a property, a dissolve, Q / W, ripple drag, slip, delete / move the track | Each refused with "Track V1 is locked." (or the step's text), nothing changes, no undo step; copy (Ctrl+C) still works; unlock → edits work again | | planned |
| 4 | Toggles on a locked track | Lock V1, then hide / mute it | As decided by Q13 | | planned |
| 5 | Save and reopen | Hide V2, mute A1, lock V1; Save; reopen | The flags come back; the project clean; `project.json` `formatVersion` 3, only `isHidden` / `isMuted` / `isLocked` changed | | planned |
| 6 | Export | After 5, export | No V2 picture, no A1 sound, V2's video sound present; the length unchanged; the output matches the Preview | | planned |
| 7 | During an export | Start an export, try the toggles | Disabled; enabled again after the export | | planned |
| 8 | Header layout | Window at 1024 and 1440 px, video and audio tracks | All header controls visible and clickable, tooltips present, nothing cut | | planned |

## Step 15.4 — trim to the playhead (D030 §5)

| # | Scenario | Steps | Expected | Automated coverage | Status |
|---|---|---|---|---|---|
| 9 | `Q` plain | Select a clip, playhead inside it, `Q` | The clip starts at the playhead, the frame under the playhead unchanged in the Preview; a gap before it; one Undo restores it | | planned |
| 10 | `W` plain | Select a clip, playhead inside it, `W` | The clip ends at the playhead; a gap after it; Undo | | planned |
| 11 | Ripple start | Clip with later clips and a gap on its track; the ripple trim start (Q2's access) | The clip keeps its start and begins with the frame that was at the playhead; later clips move left by the trimmed length, the gap between them kept; other tracks and markers unchanged; the playhead as decided (Q5) | | planned |
| 12 | Ripple end | The ripple trim end on the same layout | Later clips move left; no gap at the trimmed end | | planned |
| 13 | Speed clip | A 0.5× and a 2× clip: `Q`, `W`, ripple variants | Content as expected at the cut (no jump of the frame under the playhead for a plain trim), the sound in sync | | planned |
| 14 | Target rules | Playhead outside every selected clip; at a clip's first frame; no selection; two selected clips on two tracks | Refused with a message / nothing to do / as decided by Q1; two tracks trimmed in one undo step | | planned |
| 15 | Fade | A clip with a 1 s fade in: `Q` 0.5 s into it; a clip with a 2 s fade out trimmed to 1 s with `W` | The fade in stays on the new start; the fade out cut to the clip's length; Undo restores both | | planned |

## Step 15.5 — ripple trim by dragging (D030 §6)

| # | Scenario | Steps | Expected | Automated coverage | Status |
|---|---|---|---|---|---|
| 16 | Drag inward with the modifier | Ripple modifier (Q3) + drag a clip's end left | During the drag the later clips follow; release → the same result as the ripple command at that frame | | planned |
| 17 | Drag outward | The same, outward, then up to the end of the source | Later clips move right; the edge stops at the end of the source | | planned |
| 18 | `Esc` | Start a ripple drag, press `Esc` | Everything back, no undo step | | planned |
| 19 | Without the modifier | A plain edge drag | Unchanged Phase 4–14 trim (clamped to the neighbour) | | planned |

## Step 15.6 — slip (D030 §7)

| # | Scenario | Steps | Expected | Automated coverage | Status |
|---|---|---|---|---|---|
| 20 | Slip a clip | Slip modifier (Q3) + drag the body of a video clip with sound | Start and length unchanged; the content and its sound shift; the source in / out shown during the drag (Q12); one undo step | | planned |
| 21 | Limits | Slip to the start and to the end of the source | Stops at the source's first / last usable frame | | planned |
| 22 | Speed | Slip a 2× clip | Content shifts at the clip's speed; length unchanged | | planned |
| 23 | Refused | Slip an image, a text, a clip on a locked track | Refused / no slip gesture; nothing changes | | planned |
| 24 | Export | Export after 20 | The output matches the Preview | | planned |

## Step 15.7 — In / Out range (D030 §8)

| # | Scenario | Steps | Expected | Automated coverage | Status |
|---|---|---|---|---|---|
| 25 | Set and show | `I` at 2 s, `O` at 5 s | A band on the ruler and a shade over the tracks; the title has no `*`; Undo does not touch the range | | planned |
| 26 | Rules | `I` after Out; `O` before In; only In; only Out; clear (Q11) | As decided by Q7 / Q8 / Q11 | | planned |
| 27 | Loop | Loop on, range set, Play from inside and from outside the range | Playback loops In → Out, starting at In from outside (Q9); Loop off ignores the range | | planned |
| 28 | Range export | Export with a range (Q10) | The file covers exactly the range; first / last frame and sound match the Preview at In / Out − 1 frame; duration Out − In | | planned |
| 29 | Not saved | Set a range, Save, close and reopen; set a range, wait for the autosave, kill the process, Recover | No range after reopening and after the recovery; `project.json` and the recovery file contain no range | | planned |
| 30 | Edits | Ripple delete before the range; change the frame rate | The range stays at its time (nearest frame after the rate change) | | planned |

## Step 15.8 — UX fixes (D030 §9)

| # | Scenario | Steps | Expected | Automated coverage | Status |
|---|---|---|---|---|---|
| 31 | Text size | `+ Text` on 1920 × 1080, 3840 × 2160, 1080 × 1920, 1280 × 720 | Inspector Size 48, 96, 85.33, 32; existing text clips unchanged | | planned |
| 32 | Audio status | Play, unplug / disable the audio device (status "Playing without sound…"), plug it back, Play | The stale status gone once the sound is back; a second loss reported again (Q15) | | planned |

## Regression (Step 15.9)

| # | Scenario | Expected | Status |
|---|---|---|---|
| R1 | A project saved by the Phase 14 build (`7200976`, built apart from `git archive`): non-default canvas, rate and export settings, fades, a dissolve, markers, a text clip, and track flags set by editing the file | Opens with everything, the flags shown in the headers; a plain Save writes it byte for byte; the default export byte-identical to Phase 14's; a file saved by Phase 15 with flags opens in Phase 14 and renders the same | planned |
| R2 | Real bouldering workflow: 6–10 portrait recordings on a 1080 × 1920 canvas; for each attempt `Q` / `W` / ripple trims to the send, a slip of one clip, 0.5× on the crux, a new text with the problem's name (85.33 px), music on A1 muted / unmuted, V2 overlay hidden / shown; an In / Out loop over the crux; a full export and a range export | Every step in a few actions; Preview and both files agree; save / reopen keeps the edit (not the range); no error in the log | planned |
| R3 | Track mute / hide / lock (scenarios 1–8) end to end with save, reopen, undo and export | As in 1–8 | planned |
| R4 | Trim / ripple trim (9–19) on 30 and 29.97 fps projects with gaps and several tracks | As in 9–19; nothing off the frame grid | planned |
| R5 | Slip (20–24) incl. a clip at 0.5× and a dissolve next to the slipped clip | As in 20–24; the dissolve's handle limits the slip | planned |
| R6 | In / Out loop and range export (25–30) | As in 25–30; the range never saved | planned |
| R7 | Dissolve / fade edges: plain `Q` / `W` on a dissolve's cut edge (dissolve removed, note); the ripple variant on it (Q4); a far-edge trim stopped by a zone (Q6); an inactive (PO-8) fade becoming active after its dissolve is removed; a fade cut by a trim | As decided; Preview and export agree; Undo restores everything | planned |
| R8 | Phase 10–14 regression: fade / dissolve add and remove, relink and Find Missing, Recent, track delete / reorder, media removal, ripple delete / close gap, copy / paste / duplicate, markers, Project Settings (canvas, rate, export settings), autosave / recovery; `docs/EXPORT_MANUAL_TEST_PLAN.md` default export | As before | planned |
| R9 | No ffmpeg (ffmpeg / ffprobe hidden from PATH): open a project, toggle tracks, trim, slip, set a range, export | Edits work on analysed metadata; the same messages as Phase 14; export refused as before; no crash | planned |
| R10 | Audio device: scenario 32 with a real device, plus playback with a muted / hidden track | As in 32; the mix as set | planned |

## Results log

*(filled by the steps' development-time runs and by Step 15.9)*
