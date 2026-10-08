# Phase 16 — manual test plan

Run in the real app (`dotnet run --project src/App/App.csproj`, ffmpeg / ffprobe on PATH unless the scenario says
otherwise). Written at Step 16.2 (D031, product owner decisions of 2026-10-08) as a skeleton; the steps that implemented
the features completed the scenarios; Step 16.7 ran them in the real app with the regression (2026-10-08, results below).
Manual runs use an isolated profile (`USERPROFILE` / `LOCALAPPDATA` / `APPDATA` pointed to a scratch folder), as in Phases
11–15. Every new control is checked at 1024 and 1440 px window width.

Fixture (generated outside the repository, as in Phases 10–15): a `project.json` v3 at 25 fps, 640 × 360 —
`pattern.mp4` (ffmpeg `testsrc2`, 12 s, 440 Hz sound, the frame number drawn as "P n"), `other.mp4` (`testsrc`, 8 s, "B n"),
`tone.wav` (6 s), `still.png`; V1 `other.mp4` [0, 6 s), V2 empty, A1 `tone.wav` [0, 4 s), a marker at 4.8 s. Builds: the
Phase 16 Debug build of `22d2ef4` + `ac0e3ac`; for the regression the Phase 15 build of `main` (`a3793a4` tree). Window: scenarios 1–6 and 8–11 at 1440 px (the mode switch, the source bar,
In / Out / ✕, Insert / Overwrite all shown and used there), 7a at 1024 px.

Status column — kinds of evidence, never mixed:
- **auto (16.x)** — covered by automated tests only, not checked in the real app;
- **16.7 (Claude): PASS / FAIL / NOT RUN** — the formal Step 16.7 run in the real app (UI Automation, screenshots);
- **PO** — run by the product owner.

## Source viewer (Steps 16.4 / 16.5)

| # | Scenario | Expected | Automated coverage | Status |
|---|---|---|---|---|
| 1 | Double-click a video in the Media Browser | The Preview switches to Source, shows the asset's first frame and its duration; the timeline playhead stays | `SourceViewerTests.Source_plays_the_asset_…` | 16.7 (Claude): PASS — `pattern.mp4`: Source highlighted, "P 0", 00:00:00:00 / 00:00:12:00, the source bar shown, the playhead on the ruler at 0, the title clean. The first run found that the media list kept the focus and took the arrows / Home / End — fixed in `ac0e3ac` (the double click clears the focus), then PASS |
| 2 | Play / J / ← / → / Shift+→ / Home / End in Source | The source plays with sound and steps; the timeline playhead never moves | `…_keys_act_on_the_source_only` | 16.7 (Claude): PASS — Shift+→ 00:00:01:00, End 00:00:12:00, Home 0, J at 0 stays 0, → ×3 00:00:00:03 ("P 3"); Space played; the timeline playhead at 0 throughout |
| 3 | I / O in Source, ✕ | The source range is marked on the source bar and cleared by ✕; the project stays clean | `…_keys_act_on_the_source_only`, `Loop_in_Source_…` | 16.7 (Claude): PASS — "Source In set at 00:00:01:00.", "Source Out set at 00:00:03:00.", the band 1–3 s on the bar, "In 00:00:01:00 · Out 00:00:03:00", the title without `*` |
| 4 | Loop in Source with a range | Playback loops over [In, Out) | `Loop_in_Source_loops_the_source_range` | 16.7 (Claude): PASS — Ctrl+L, Space, 3.2 s of playback: 00:00:02:14, paused at 00:00:02:23 — inside [1 s, 3 s) |
| 5 | Back to Timeline | The timeline frame at the playhead is shown; I / O set the timeline range again | `Timeline_mode_shows_the_playhead_frame_again_…` | 16.7 (Claude): PASS — "B 0", 00:00:00:00 / 00:00:06:00; back in Source the source at its last position |
| 6 | An image, an asset without analysis | Not opened (an image is added to the timeline as before); a message | `Images_missing_and_unanalysed_assets_do_not_open` | 16.7 (Claude): PASS — `still.png` double click: "Added to timeline" (Undo); without ffprobe (R4) `pattern.mp4`: "pattern.mp4 can't be added: its duration is unknown (FFprobe could not be found …)", Source not opened |
| 7 | New / Open while in Source | Source is closed; the ranges are gone | `Another_project_or_the_asset_leaving_the_project_closes_Source` | 16.7 (Claude): PASS — New: Timeline mode, Source disabled, no source name |
| 7a | An audio file in Source; 1024 px | Plays without a picture; the source bar and its buttons fit | `An_audio_asset_plays_without_a_picture` | 16.7 (Claude): PASS — `tone.wav`: black picture, 00:00:06:00; at 1024 px the mode switch, the bar, In / Out / ✕ and Insert / Overwrite fit; the duration text of the transport row is cut at 1024 px exactly as in the Phase 15 build (not a regression) |

## Insert / Overwrite (Steps 16.3 / 16.6)

| # | Scenario | Expected | Automated coverage | Status |
|---|---|---|---|---|
| 8 | `,` with the playhead inside a clip on V1 | The clip is split, its right part and the later clips of V1 move right by the range; other tracks and markers stay; the playhead at the end of the new clip, which is selected | `InsertOverwriteTests` (split + move rule, tick for tick), `SourceViewerTests.Comma_inserts_…`, `ExportInsertOverwriteEndToEndTests` | 16.7 (Claude): PASS — source [1 s, 3 s) at 2 s: V1 `other` 0–2 s, `pattern` 2–4 s (selected), `other` 4–8 s; A1 and the marker unchanged; playhead 00:00:04:00 / 00:00:08:00 showing "B 50"; at 2 s "P 25"; "Inserted pattern.mp4"; title `*` |
| 9 | `.` over the later clips | The covered clips are trimmed / removed; Undo restores exactly | `InsertOverwriteTests` (every overlap shape, dissolves) | 16.7 (Claude): PASS — at 6 s: `other` 4–6 s, `pattern` 6–8 s, duration unchanged 8 s, the end shows "P 74" (the range's last frame); "Overwrote with pattern.mp4". Across a dissolve: auto only |
| 10 | Target track by the selection | With a clip on V2 selected the edit goes to V2; with nothing selected to V1 (audio: A1) | `The_target_is_the_track_of_the_latest_selected_clip_…` | 16.7 (Claude): PASS — a text added on V2 (selected), `.` at 0: `pattern` 0–2 s on V2, the text trimmed to 2–5 s |
| 11 | Locked target, during an export | Refused with a message; nothing changes | `Refused_on_a_locked_target_and_off_during_an_export` | 16.7 (Claude): PASS for the lock — V1 locked, a V1 clip selected, `,`: "Track V1 is locked.", the timeline unchanged. During an export: auto only. Eight Undo steps back to the opened project, the title clean |
| 12 | An audio asset | Goes onto the audio target as an audio clip | `…_latest_selected_clip_…`, `InsertOverwriteTests` | auto (16.6) |

## Regression (Step 16.7)

| # | Scenario | Expected | Status |
|---|---|---|---|
| R1 | A Phase 15 project | Opens and saves byte for byte; `formatVersion` 3 | 16.7 (Claude): PASS — the fixture saved by the Phase 15 build, then opened and saved by the Phase 16 build: `project.json` identical byte for byte (SHA-256), `formatVersion` 3 |
| R2 | Export | The default export identical to Phase 15's for the same project | 16.7 (Claude): PASS — the default export of the same project from both builds: identical MP4s (216 586 bytes, the same SHA-256) |
| R3 | Phase 15 tools | Q / W, ripple trim, slip, In / Out loop and range export as before | 16.7 (Claude): PASS for W ("Trimmed the end of the clip to the playhead"), I / O ("In set at 00:00:02:00.", "Out set after 00:00:03:00 (that frame is included).") and the loop over the range (paused at 00:00:02:22); the rest auto (the full suite) |
| R4 | No ffmpeg | No crash; the Source viewer reports why it can't open | 16.7 (Claude): PASS — ffmpeg / ffprobe removed from PATH: the app runs, Space does nothing harmful, the reason shown (scenario 6) |
