# Phase 10 — manual test plan

Run in the real app (`dotnet run --project src/App/App.csproj`, ffmpeg / ffprobe on PATH). Written during the Phase 10
steps (D025) and run as a whole at Step 10.9; sections are filled in per step (the scenarios below are the skeleton
agreed at Step 10.2 — steps, expected results and automated coverage are completed by the step that implements them).
Logs: `%LOCALAPPDATA%\AiVideoEditor\logs`.

Fixture for the fade scenarios (4–12b), opened without the folder picker (Debug build only — `--open-project` is a
development option compiled out of Release, `src/App/DevStartup.cs`):

```
pwsh tools/manual/New-Phase10FadeFixture.ps1 -Force
dotnet run --project src/App/App.csproj -- --open-project "%TEMP%\aive-phase10-fades"
```

The script (ffmpeg on PATH) writes the project outside the repository; its header lists what lies where on the
timeline (25 fps, 640 × 360). The media have no saved metadata, so the app analyses them first (a few seconds). If the
app offers a recovery at start, choose "Not now". `-Force` replaces only a folder the script created.

Status column: **planned** — written at 10.2, not yet runnable; **auto** — covered by automated tests only, manual run
pending (10.9); **manual pending** — not yet run in the real app: the fade scenarios are run by hand for the product
owner's acceptance of fades (end of 10.5) and again at 10.9; **app 10.x** — checked in the real app during that step
(a development check, not the formal run).

## Step 10.3 — project format v3

| # | Scenario | Steps | Expected | Automated coverage | Status |
|---|---|---|---|---|---|
| 1 | Open a Phase 9 project | Open a project saved by the Phase 9 build (v2) | It opens unchanged (no fades, no dissolves); Save writes `"formatVersion": 3` | `FadeTransitionPersistenceTests`, `SpeedPersistenceTests` | auto |
| 2 | v3 in an older build | Open a v3 project with the Phase 9 build | Refused: "saved by a newer version of AI Video Editor" — nothing opened, nothing written | the "newer version" check (`FadeTransitionPersistenceTests`: v4 refused — the same code path in the Phase 9 build for v3) | planned |
| 3 | Damaged v3 | Edit a v3 `project.json` by hand: a negative fade, a dissolve between clips that don't touch | Refused as damaged with the reason; the current project stays open | `FadeTransitionPersistenceTests` (20 damaged cases) | auto |

## Steps 10.4–10.5 — fades

| # | Scenario | Steps | Expected | Automated coverage | Status |
|---|---|---|---|---|---|
Fades are set in the Inspector's FADES section: Fade In / Fade Out in whole frames of the project rate, the length as
timecode next to each field; the timeline draws each effective ramp as a darkening band at the clip's edge.

| # | Scenario | Steps | Expected | Automated coverage | Status |
|---|---|---|---|---|---|
| 4 | Fade in / out, one video | Select a video on V1 (25 fps project); FADES: Fade In 25, Fade Out 25; play from 0; step through the first and last frames with ← / → | Picture rises from black and sinks to black; the sound fades with it; the first frame is dark but not black (1/26), no jump where the ramps end; the timeline shows a band at each end of the clip | `FadeRuleTests`, `ExportFadeEndToEndTests` (source × factor), `InspectorFadeTests` | auto; manual pending |
| 5 | Fade over a lower track | V2 video (covering the canvas) over a V1 video; Fade In 25 and Fade Out 25 on V2; play through both ramps | V1 shows through during both ramps; no placeholder flash on V1 when the fade out starts during playback | `FadePlaybackTests` (prefetch before the ramp), `ExportFadeEndToEndTests` | auto; manual pending |
| 6 | Image and text fades | Fade an image and a text clip (Fade In / Fade Out ≥ 10) | Both fade like a video | `ExportFadeEndToEndTests`, `FadeRuleTests` | auto; manual pending |
| 7 | Speed ≠ 1× | Fade In 25 on a clip at 2× and on one at 0.5× | The ramp lasts 25 timeline frames (1 s) in both | `ExportFadeEndToEndTests`, `FadeEditTests` | auto; manual pending |
| 8 | Short clip | Clip of 20 frames, Fade In 15 and Fade Out 15; then trim it to 10 frames and back to 20 | Ramps overlap (multiply); at 10 frames the bands cover the clip, the Inspector still shows 15 / 15; back at 20 the ramps are as before | `FadeRuleTests`, `FadeEditTests`, `InspectorFadeTests` | auto; manual pending |
| 9 | Split inside a ramp | Fade In 40, Fade Out 40 on a clip; split 10 frames after its start | Left part: Fade In 40 (drawn over its 10 frames), Fade Out 0; right part: Fade In 0, Fade Out 40; Undo gives one clip with 40 / 40 | `FadeEditTests` | auto; manual pending |
| 10 | Undo / redo and save | Change Fade In several times with the arrows, then Fade Out; Undo / Redo; Save, reopen | The Fade In changes are one undo step ("Change Fade In"), Fade Out another; values survive save / reopen exactly (`fadeInTicks` / `fadeOutTicks` in `project.json`) | `FadeEditTests`, `InspectorFadeTests`, `FadeTransitionPersistenceTests` | auto; manual pending |
| 11 | Export with fades | Export a project with scenarios 4–7 | The MP4 shows the fades at the same frames as the Preview; the sound fades with the picture | `ExportFadeEndToEndTests`, `ExportFrameSelectionContractTests`, `ExportAudioContractTests` | auto; manual pending |
| 12 | Editing lock | Start an export; try to change a fade | The FADES fields are disabled until the export ends | `InspectorFadeTests` | auto; manual pending |
| 12a | Invalid values | Type 2.5, or more frames than the clip has | The status bar explains; the field shows the clip's value again; nothing is edited | `InspectorFadeTests` | auto; manual pending |
| 12b | Cancel during a ramp | Export, cancel while the first second (a fade in) renders | Ends as any cancel: no MP4, no temporary file, no ffmpeg left | `ExportFadeEndToEndTests` | auto; manual pending |

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
