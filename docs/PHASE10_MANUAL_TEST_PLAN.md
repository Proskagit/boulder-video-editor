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
(a development check, not the formal run); **app 10.5 (PO)** — run in the real app by the product owner on
2026-09-29 (fixture `tools/manual/New-Phase10FadeFixture.ps1`), passed; the formal re-run is at 10.9; **app 10.8 (PO)** —
the dissolve scenarios 13–20 run in the real app by the product owner on 2026-09-29 (fixture
`tools/manual/New-Phase10DissolveFixture.ps1`, three rounds; the fixes of 16.2–16.4 and 17 re-checked), passed.

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
| 4 | Fade in / out, one video | Select a video on V1 (25 fps project); FADES: Fade In 25, Fade Out 25; play from 0; step through the first and last frames with ← / → | Picture rises from black and sinks to black; the sound fades with it; the first frame is dark but not black (1/26), no jump where the ramps end; the timeline shows a band at each end of the clip | `FadeRuleTests`, `ExportFadeEndToEndTests` (source × factor), `InspectorFadeTests` | app 10.5 (PO) |
| 5 | Fade over a lower track | V2 video (covering the canvas) over a V1 video; Fade In 25 and Fade Out 25 on V2; play through both ramps | V1 shows through during both ramps; no placeholder flash on V1 when the fade out starts during playback | `FadePlaybackTests` (prefetch before the ramp), `ExportFadeEndToEndTests` | app 10.5 (PO) |
| 6 | Image and text fades | Fade an image and a text clip (Fade In / Fade Out ≥ 10) | Both fade like a video | `ExportFadeEndToEndTests`, `FadeRuleTests` | app 10.5 (PO) |
| 7 | Speed ≠ 1× | Fade In 25 on a clip at 2× and on one at 0.5× | The ramp lasts 25 timeline frames (1 s) in both | `ExportFadeEndToEndTests`, `FadeEditTests` | app 10.5 (PO) |
| 8 | Short clip | Clip of 20 frames, Fade In 15 and Fade Out 15; then trim it to 10 frames and back to 20 | Ramps overlap (multiply); at 10 frames the bands cover the clip, the Inspector still shows 15 / 15; back at 20 the ramps are as before | `FadeRuleTests`, `FadeEditTests`, `InspectorFadeTests` | app 10.5 (PO) |
| 9 | Split inside a ramp | Fade In 40, Fade Out 40 on a clip; split 10 frames after its start | Left part: Fade In 40 (drawn over its 10 frames), Fade Out 0; right part: Fade In 0, Fade Out 40; Undo gives one clip with 40 / 40 | `FadeEditTests` | app 10.5 (PO) |
| 10 | Undo / redo and save | Change Fade In several times with the arrows, then Fade Out; Undo / Redo; Save, reopen | The Fade In changes are one undo step ("Change Fade In"), Fade Out another; values survive save / reopen exactly (`fadeInTicks` / `fadeOutTicks` in `project.json`) | `FadeEditTests`, `InspectorFadeTests`, `FadeTransitionPersistenceTests` | app 10.5 (PO) |
| 11 | Export with fades | Export a project with scenarios 4–7 | The MP4 shows the fades at the same frames as the Preview; the sound fades with the picture | `ExportFadeEndToEndTests`, `ExportFrameSelectionContractTests`, `ExportAudioContractTests` | app 10.5 (PO) |
| 12 | Editing lock | Start an export; try to change a fade | The export window is modal: until the export ends nothing in the main window can be selected or changed — media, clips, the Inspector, fades included; the `EditingLock` disabling the FADES fields is a second line | `InspectorFadeTests` (the lock) | app 10.5 (PO) |
| 12a | Invalid values | In Fade In / Fade Out type a fraction (`2,5` / `2.5`), text, or more frames than the clip has (e.g. `500` for a 100-frame clip); leave the field | Not applied: the field shows the previous value again once it loses focus, the clip is unchanged, no undo step — like every numeric field (NumericInput); the control rejects such input itself, so no status message | `InspectorFadeTests` (view-model rejections) | app 10.5 (PO) |
| 12b | Cancel during a ramp | Export, cancel while the first second (a fade in) renders | Ends as any cancel: no MP4, no temporary file, no ffmpeg left | `ExportFadeEndToEndTests` | app 10.5 (PO) |
| 12c | PO-8 in the fixture | Select P, then Q (30–34 s, a dissolve on their cut) | P: Fade Out 20 with "Not applied: a dissolve is on this edge.", no band on the right, its Fade In 10 applies; Q: the same note under Fade In 15, no band on the left, its Fade Out 10 applies | `InspectorFadeTests`, `FadeViewBindingTests`, `FadeRuleTests` | app 10.5 (PO) |

## Steps 10.6–10.8 — cross dissolve

Fixture (Debug build; the timeline layout is in the script's header):

```
pwsh tools/manual/New-Phase10DissolveFixture.ps1 -Force
dotnet run --project src/App/App.csproj -- --open-project "%TEMP%\aive-phase10-dissolves"
```

UI: select two clips that meet (click the first, Ctrl+click the second), then **Dissolve** in the timeline header —
1 s, or the longest that fits when less fits (the status bar says which). The zone is drawn over the cut; click it (on a clip
body — the clips' edge handles inside the zone still trim the clips) to select the dissolve: the Inspector's DISSOLVE section shows its length in frames with the time, the longest that fits
and **Remove Dissolve**; Delete removes it too. Everything is one undo step.

| # | Scenario | Steps | Expected | Automated coverage | Status |
|---|---|---|---|---|---|
| 13 | Add a dissolve | Select the clips at 0–4 s and 4–8 s; Dissolve; play 3–5 s; step through the zone with ← / → | Status "Dissolve added: 25 frames."; a band over 3.52–4.52 s, selected; the bars fade in over the pattern frame by frame (the pattern keeps moving past 4 s — its handle); the sound switches from 440 Hz to 880 Hz exactly at 4 s (a hard cut); Inspector: 25 frames, "Longest that fits here: 51 frames" | `TimelineDissolveUiTests`, `DissolveEditTests`, `DissolveCompositionTests`, `ExportDissolveEndToEndTests` | app 10.8 (PO) |
| 13a | Length and selection | With the dissolve selected: Inspector Duration 25 → 30 with the arrows, then 7; Undo; zoom in / out; click a clip, click the band again | The band widens / narrows around the cut and follows the zoom; the arrow changes are one undo step ("Change Dissolve Duration"); a value above the longest that fits, a fraction or text is not applied (the field shows the length again); selecting a clip leaves the band unselected | `TimelineDissolveUiTests`, `FadeViewBindingTests` | app 10.8 (PO) |
| 14 | No handles | Select the two "short" clips at 9–13 s; Dissolve | Nothing is created; status "There is not enough media beyond the clips for the dissolve." | `TimelineDissolveUiTests`, `DissolveEditTests` | app 10.8 (PO) |
| 14a | No suitable cut | Select the "bars" clip (4–8 s) and the first "short" clip (9–11 s; a gap between them); Dissolve. Then select one clip, or three: the button | Status "Select two clips that meet on a video track …"; with one or three clips selected the button is disabled | `TimelineDissolveUiTests` | app 10.8 (PO) |
| 15 | Image / text neighbours | Dissolve 14–18 s / 18–22 s (video → image) and 18–22 s / 22–26 s (image → text); play 17–23 s | Both added with 25 frames (no handle limit for the image and the text); the image fades in over the video, the text over the image | `DissolveEditTests`, `ExportDissolveEndToEndTests` | app 10.8 (PO) |
| 16 | Move / trim / split / delete | On the 13 dissolve: move the bars clip alone (status "A dissolve was removed …", Undo); move both clips together; trim the pattern clip's end (removed, Undo); drag its left edge toward 4 s (it stops at 3.52 s, keeping the dissolve's 12 frames); split at 3.8 s (rejected "Can't split inside a dissolve.") and at 2 s (kept); delete the bars clip (removed, Undo); select the band, Delete (removed, Undo) | As listed; every change is one undo step and Undo restores clips and the dissolve exactly | `DissolveEditTests`, `TimelineDissolveUiTests` | app 10.8 (PO) |
| 17 | Speed | With the 13 dissolve: "bars" clip Speed 4× in the Inspector, then 2×; Undo; "pattern" clip Speed 2× | 4×: rejected — not enough media before "bars" at that speed (the field shows 1× again); 2×: allowed, the dissolve stays; 2× on "pattern": its end moves to 2 s, the dissolve is removed with the status message; Undo brings it back | `DissolveEditTests`, `InspectorFadeTests` | app 10.8 (PO) |
| 17a | Locked track, export | Start an export with a dissolve selected. (A locked track has no control in the UI: not reachable by hand) | During the export the main window is blocked (the export window is modal), the DISSOLVE fields are disabled. Locked track: Dissolve / Duration / Remove report "Track V1 is locked." (tests only) | `TimelineDissolveUiTests` | app 10.8 (PO) (export part) |
| 18 | Export with dissolves | Export the project with the dissolves of 13 and 15 | The MP4 shows the dissolves at the same frames as the Preview; the sound cuts hard at 4 s | `ExportDissolveEndToEndTests`, `ExportFrameSelectionContractTests` | app 10.8 (PO) |
| 19 | Cancel inside a zone | Export, cancel while it renders 3.5–4.5 s (the export is short: be quick, or repeat) | Ends as any cancel: no MP4, no temporary file, no ffmpeg left | `ExportDissolveEndToEndTests` | app 10.8 (PO) |
| 20 | Save / reopen | Save (Ctrl+S), close, open the fixture again with the same command | The dissolves, their lengths and the clips they join are unchanged (`transitions` with `leftClipId` / `rightClipId` in `project.json`) | `FadeTransitionPersistenceTests` | app 10.8 (PO) |

## Formal run (Step 10.9)

What remains for the real app at the closeout, and why only this: the dissolve scenarios 13–20 were run by the product
owner on the final dissolve code (10.8, the fixes re-checked) and are not repeated; the fade scenarios 4–12c were
passed at 10.5, and since then 10.6–10.8 changed code they pass through — the layer set in the Preview / export
(10.7), the Inspector's numeric fields after a rejected value (10.8, every numeric field, the fade fields included) and
the undo of speed steps — so those fades are re-checked; the format scenarios were never run by hand.

| # | Run | Fixture / build | Why |
|---|---|---|---|
| R1 | 1 | a project saved by the Phase 9 build (any — e.g. from `main` before Phase 10) opened in this build | v2 → v3 never run by hand |
| R2 | 3 | the fade fixture's `project.json`, edited by hand (one damaged case is enough: `"fadeInTicks": -1`), then restored with `-Force` | the damaged-file refusal never run by hand |
| R3 | 4, 5, 12c | fade fixture | the layer set in the Preview after 10.7 (fades alone and PO-8 next to a rendered dissolve) |
| R4 | 9, 10 | fade fixture | split / undo / save of fades after the 10.6 edit coupling |
| R5 | 12a | fade fixture | the numeric fields' reset after a rejection changed in 10.8 |
| R6 | 11 | fade fixture | the export after 10.7 (the fades at the Preview's frames) |
| R7 | `docs/EXPORT_MANUAL_TEST_PLAN.md` 1–8, 10–14 (9 optional) | any project without fades and dissolves | the export regression the plan requires |

Optional: 2 (a v3 project opened with the Phase 9 build) — it needs a second build of `main`; the refusal is the same
code path as the automated v4 check. Not repeated: 6, 7, 8, 12, 12b (their code is unchanged since 10.5 and covered by
the same automated tests, which are green), 13–20 (above).

Results: to be filled after the run (date, tester, pass / fail per row).
