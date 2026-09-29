# Phase 10 — manual test plan

Run in the real app (`dotnet run --project src/App/App.csproj`, ffmpeg / ffprobe on PATH). Written during the Phase 10
steps (D025) and run as a whole at Step 10.9; sections are filled in per step (the scenarios below are the skeleton
agreed at Step 10.2 — steps, expected results and automated coverage are completed by the step that implements them).
Logs: `%LOCALAPPDATA%\AiVideoEditor\logs`.

Status column: **planned** — written at 10.2, not yet runnable; **auto** — covered by automated tests only, manual run
pending (10.9); **app 10.x** — checked in the real app during that step (a development check, not the formal run).

## Step 10.3 — project format v3

| # | Scenario | Steps | Expected | Automated coverage | Status |
|---|---|---|---|---|---|
| 1 | Open a Phase 9 project | Open a project saved by the Phase 9 build (v2) | It opens unchanged (no fades, no dissolves); Save writes `"formatVersion": 3` | `FadeTransitionPersistenceTests`, `SpeedPersistenceTests` | auto |
| 2 | v3 in an older build | Open a v3 project with the Phase 9 build | Refused: "saved by a newer version of AI Video Editor" — nothing opened, nothing written | the "newer version" check (`FadeTransitionPersistenceTests`: v4 refused — the same code path in the Phase 9 build for v3) | planned |
| 3 | Damaged v3 | Edit a v3 `project.json` by hand: a negative fade, a dissolve between clips that don't touch | Refused as damaged with the reason; the current project stays open | `FadeTransitionPersistenceTests` (20 damaged cases) | auto |

## Steps 10.4–10.5 — fades

| # | Scenario | Steps | Expected | Automated coverage | Status |
|---|---|---|---|---|---|
| 4 | Fade in / out, one video | Set Fade In 1 s and Fade Out 1 s on a video on V1; play from 0; scrub the ends | Picture rises from black and sinks to black; the sound fades with it; no jump at the ramp edges | Core / contract / parity tests | planned |
| 5 | Fade over a lower track | V2 video fading in over a V1 video | V1 shows through while V2 fades in; no placeholder flash on V1 during playback | Parity, prefetch tests | planned |
| 6 | Image and text fades | Fade an image and a text clip | Both fade like a video | Parity tests | planned |
| 7 | Speed ≠ 1× | Fade a clip at 2× and one at 0.5× | The ramp keeps its timeline length (seconds on the timeline, not of the source) | Parity tests | planned |
| 8 | Short clip | Clip of 20 frames with Fade In and Fade Out of 15 frames each; then trim it shorter / longer | Ramps overlap (multiply); trimming shorter shortens the visible ramps, trimming back restores them; the Inspector values stay | Core, edit tests | planned |
| 9 | Split inside a ramp | Split a clip in its fade-in | Left part keeps the fade in (shortened to its length) and has no fade out; right part has no fade in and keeps the fade out; Undo restores one clip with both fades | Edit tests | planned |
| 10 | Undo / redo and save | Change fades several times, Undo / Redo, Save, reopen | Consecutive edits of one field merge into one undo step; values survive save / reopen exactly | Edit, serializer tests | planned |
| 11 | Export with fades | Export a project with scenarios 4–7 | The MP4 matches the Preview (fades at the same frames), sound fades with the picture | E2E parity, audio tests | planned |
| 12 | Editing lock | Start an export; try to change a fade | The fields are disabled until the export ends | UI tests | planned |

## Steps 10.6–10.8 — cross dissolve

| # | Scenario | Steps | Expected | Automated coverage | Status |
|---|---|---|---|---|---|
| 13 | Add a dissolve | Two trimmed videos touching on V1; select the cut; add a 1 s dissolve | The zone is drawn centred on the cut; B dissolves over A; the sound cuts hard at the cut | Edit, parity tests | planned |
| 14 | No handles | Two untrimmed (full-length) videos touching; add a dissolve | Nothing is created; the message says there is not enough media beyond the clips (and the longest that fits, if any) | Edit tests | planned |
| 15 | Image / text neighbours | Dissolve video → image and image → text | Works without handles | Edit, parity tests | planned |
| 16 | Move / trim / split / delete | Move A alone; move A and B together; trim the cut edge; trim a far edge; split inside and outside the zone; delete B | As D025 §5: removed (with a status message) / kept / clamped / rejected; each one undo step | Edit tests | planned |
| 17 | Speed | Change B's speed so its handle is too short; change A's speed | B: rejected with a message; A: the dissolve is removed (status message) | Edit tests | planned |
| 18 | Export with dissolves | Export a project with scenarios 13 and 15 | The MP4 matches the Preview in every zone | E2E parity tests | planned |
| 19 | Cancel inside a zone | Start an export, cancel while it renders a zone | Ends as any cancel: no MP4, no temporary file, no ffmpeg left | E2E cancel tests | planned |
| 20 | Save / reopen | Save a project with dissolves, reopen | Dissolves, their durations and anchors are unchanged | Serializer tests | planned |
