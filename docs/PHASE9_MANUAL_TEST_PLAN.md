# Phase 9 — manual test plan

Run in the real app (`dotnet run --project src/App/App.csproj`, ffmpeg / ffprobe on PATH). Written during the Phase 9
steps (D024) and run as a whole at Step 9.10; sections are added per step. Logs: `%LOCALAPPDATA%\AiVideoEditor\logs`
(`app-*.log`, `ffmpeg-*.log`, `errors-*.log`).

Status column: **auto** — covered by automated tests only, manual run pending (9.10); **UIA 9.3** — driven through the
real app's UI by UI Automation during Step 9.3 (a development check, not the formal run); **manual-only, not
executed** — needs hardware or system changes that were not made; must be run by hand at 9.10; **app 9.4** — checked
in the real app during Step 9.4 (a development check, not the formal run); **app 9.5** — checked in the real app
during Step 9.5 (a development check, not the formal run); **app 9.6** — checked in the real app during Step 9.6
(keys posted to / typed into the window, a development check, not the formal run); **measured with the scratch tool
(9.7)** — measured by Step 9.7's scratch tool driving the shipped services (not the app's UI), numbers in `progress.md`;
**measured in the app (C, 9.7)** — measured in the running app during Step 9.7 (C); **app 9.8** — checked in the real
app during Step 9.8 (a development check, not the formal run). The formal run of every scenario (Step 9.10) is in
"Formal run (Step 9.10)" below; the Status column keeps the development checks.

## Step 9.3 — stability & error handling

| # | Scenario | Steps | Expected | Automated coverage | Status |
|---|---|---|---|---|---|
| 1 | Close with media, idle | Open a project with a video (with sound) on the timeline, wait until the Preview shows it, close the window | The process ends within a few seconds; "Shutting down." is the last line of `app-*.log`; no `ffmpeg.exe` left (Task Manager) | `CloseReleaseTests`, `PlaybackReleaseTests` | UIA 9.3 (0.1 s) |
| 2 | Close while playing | As 1, press Play, close during playback | As 1 | as 1 | UIA 9.3 |
| 3 | Close after Pause | Play, Pause, close | As 1 | as 1 | UIA 9.3 |
| 4 | Close after Stop | Play, Stop, close | As 1 | as 1 | UIA 9.3 |
| 5 | Close after an export | Export the project to an `.mp4`, confirm "Export finished", close | As 1; the MP4 is complete | as 1 | UIA 9.3 |
| 6 | Close with unsaved changes | Edit something, close → Cancel; play / seek; close → Don't Save | Cancel keeps the window and playback working; Don't Save closes as in 1 | `CloseReleaseTests` | auto |
| 7 | New while analysis runs | Import several large videos, press New (Don't Save) before they finish "Analyzing" | The new project is empty and stays unchanged; no `ffprobe.exe` keeps running; media imported into the new project are analysed normally | `AnalysisGenerationTests` | auto |
| 8 | Open while analysis runs | As 7, but Open another saved project | The opened project's media are analysed (none stays "Analyzing" / pending); nothing of the first project appears | `AnalysisGenerationTests` | auto |
| 9 | Recover while analysis runs | — | Not reachable in the current UI: the recovery offer comes at startup, before anything can be imported. Covered by tests only | `AnalysisGenerationTests` | auto (no manual form) |
| 10 | Reopen the same project while its analysis runs | Import a large video, Save, immediately Open the same project folder | The video ends "Completed" with its metadata and can be added to the timeline (before 9.3b it stayed pending for good) | `AnalysisGenerationTests` | auto |
| 11 | Many imports at once | Import ~30 media files | Task Manager never shows more than 4 `ffprobe.exe` at a time; every file ends Completed or Failed; the UI stays responsive | `AnalysisConcurrencyTests`, `AnalysisConcurrencyIntegrationTests` | auto |
| 12 | FFmpeg diagnostics | Play, seek, export; then import a damaged / non-media file renamed to `.mp4` | `ffmpeg-*.log`: the ffmpeg command lines (Debug), normal exits, "ended by the app" for decoders stopped by seeking or closing, and for the damaged file a Warning with ffprobe's exit code and error text; none of these lines in `app-*.log` | `FfmpegDiagnosticsTests` | auto; the log content of a play / export / close run checked in 9.3d |
| 13 | Damaged project | Close the app, replace a project's `project.json` with garbage, Open that folder | The status bar says the project file is damaged and can't be opened (with the reason); no promise of a backup; the current project stays open | `DamagedProjectMessageTests` | auto |
| 14 | Default audio device changed while paused | Play briefly, Pause; in Windows switch the default output device (e.g. speakers → headphones); Play | Sound comes from the new default device; `app-*.log`: "Audio output moved to the current default device …" | `AudioDeviceChangeTests` (fake devices) | manual-only; run at 9.10 with the owner |
| 15 | Default audio device changed while playing | Play; switch the Windows default output device during playback; then seek | Sound stays on the old device until the seek (no switch during playback); after the seek it comes from the new default | `AudioDeviceChangeTests` (fake devices) | manual-only; run at 9.10 with the owner |
| 16 | Audio device removed while playing | Play through USB / Bluetooth headphones; unplug / disconnect them | No crash, no hang; the picture keeps playing without a jump; "Playing without sound" in the status bar; after Pause and Play the sound comes from the current default device | `AudioDeviceChangeTests` (fake devices), `AudioPlaybackServiceTests` | manual-only; run at 9.10 with the owner |
| 17 | Audio device removed while paused | Pause; unplug the playing device; Play | Sound comes from the current default device at once | `AudioDeviceChangeTests` (fake devices) | manual-only; run at 9.10 with the owner |

## Step 9.4 — thumbnails + cache

A thumbnail decode shows in `ffmpeg-*.log` as an ffmpeg command line scaled to fit 160 × 90; playback decodes are
not. Cache files: `<assetId>-<size>-<lastWriteTicks>-v1.thumb`.

| # | Scenario | Steps | Expected | Automated coverage | Status |
|---|---|---|---|---|---|
| 18 | Thumbnails appear | New project; import a video, an image, an audio file and a file that fails analysis | While "Analyzing" every row shows its colour tile; then the video and the image show a picture of their content (aspect kept, centred in the 56 × 32 tile), audio and the failed file keep the tile; the UI stays responsive meanwhile | `MediaBrowserThumbnailTests`, `ThumbnailCoordinatorTests`, `ThumbnailServiceTests` | auto |
| 19 | Deterministic frame | Import the same video into two new projects (or reopen one) | The same picture every time: the frame at `min(Duration / 10, 5 s)` (D009) — for a clip with a scene change at 1 s and 20 s duration, the frame after it | `ThumbnailServiceTests`, `ThumbnailIntegrationTests` | auto |
| 20 | Reopen uses the cache | Save a project with a video, close the app, start it, Open the project | The thumbnail is shown at once; `ffmpeg-*.log` of this session has no thumbnail decode; `<project>/cache/thumbnails` holds one `.thumb` per video / image | `ThumbnailServiceTests`, `ThumbnailCoordinatorTests` | app 9.4 (one decode on first open, none on reopen) |
| 21 | Changed source | Close the app; replace the video file with another video of the same name (or change it so size / last-write time differ); Open the project | The new content's thumbnail (after analysis); one thumbnail decode; the old `.thumb` of that asset is gone | `ThumbnailServiceTests` | auto |
| 22 | Damaged cache | Close the app; overwrite a `.thumb` file with garbage (or truncate it); Open the project | The thumbnail is made again silently: no message, no Warning in `app-*.log`; the file is valid again | `ThumbnailServiceTests` | auto |
| 23 | Offline media | Save a project with two videos, close; rename one source file and delete the other's `.thumb`, also rename its source; Open | Both are offline; the one with a cache shows its cached thumbnail, the other its colour tile; `ffmpeg-*.log` has no decode of either | `ThumbnailServiceTests`, `ThumbnailCoordinatorTests`, `MediaBrowserThumbnailTests` | auto |
| 24 | Unsaved project, first Save | New project; import a video; check `%LOCALAPPDATA%\AiVideoEditor\cache\unsaved\<projectId>\thumbnails`; Save to a new folder | Before saving the `.thumb` is in the unsaved folder; after it, in `<project>/cache/thumbnails`, and the unsaved folder is gone; `project.json` names no thumbnail (`thumbnailPath` absent or null, `formatVersion` 2); nothing is decoded again | `ThumbnailCacheLocationTests` | auto |
| 25 | Save As | Save As the saved project of 24 to another folder | The new folder has its own `cache/thumbnails` copy; the old project folder keeps its cache; the thumbnail stays visible | `ThumbnailCacheLocationTests` | auto |
| 26 | Recovery and startup cleanup | New project with a video (unsaved), wait for an autosave (2 min), kill the app (Task Manager); start it → Recover. Then leave an unsaved project's folder under `cache\unsaved` without a recovery file and start again | Recover shows the thumbnail from its unsaved folder without decoding; at the next start the orphan folder is removed, the recoverable one kept | `ThumbnailCacheLocationTests`, `StartupCacheCleanupTests` | auto |
| 27 | New / Open while thumbnails are made | Import ~10 videos; while thumbnails are still appearing press New (Don't Save) or Open another project | Task Manager never shows more than 2 thumbnail `ffmpeg.exe` at once; after the switch none of the old project keeps running; nothing of the old project appears; the new project's media get their own thumbnails | `ThumbnailCoordinatorTests`, `MediaBrowserThumbnailTests` | auto |
| 28 | Close while thumbnails are made | As 27, close the window instead (Don't Save) | The process ends within a few seconds; "Shutting down." is the last line of `app-*.log`; no `ffmpeg.exe` left | `ThumbnailCoordinatorTests` | auto; clean close after thumbnails checked in app 9.4 |
| 29 | Portrait / rotated video | Import a phone video recorded in portrait (display matrix −90°) | A portrait thumbnail, upright, centred in the tile with the colour visible on both sides | `ThumbnailIntegrationTests` | auto |
| 30 | Playback and export untouched | With thumbnails shown, play the timeline and export it | Preview and export are as before; neither reads `.thumb` files (no change in `ffmpeg-*.log` besides their usual commands) | parity suite (`ExportEndToEnd.Tests`) unchanged | auto |

## Step 9.5 — waveform

A waveform decode shows in `ffmpeg-*.log` as an audio command line (`-map 0:a:0 … -f f32le`) without `-ss` and
without `atempo`; the Preview also starts audio decodes at the playhead (with `atempo` for a clip at another speed), so
count per file. Cache files: `<project>/cache/waveforms/<assetId>-<size>-<lastWriteTicks>-v1.peaks`. Media with a
known shape help: e.g. 2 s silence, 4 s of a quiet tone, 4 s of a loud tone.

| # | Scenario | Steps | Expected | Automated coverage | Status |
|---|---|---|---|---|---|
| 31 | Only timeline media | Import an audio file and a video with sound; wait for analysis; then add only the video to the timeline | Nothing is decoded for a waveform at import; after Add to Timeline one waveform decode of the video; the audio file gets none until it is on the timeline | `WaveformCoordinatorTests` | auto |
| 32 | Audio clip and video with sound | A timeline with an audio clip and a video clip with sound | The audio clip shows its waveform over its whole height, the video clip in its lower half, both following the loudness of the media (silence flat) | `TimelineWaveformTests`, `WaveformViewTests`, `WaveformIntegrationTests` | app 9.5 |
| 33 | Video without sound, images, text | Add a video without an audio stream, an image and a text clip | None of them shows a waveform; `ffmpeg-*.log` has no audio decode of the silent video | `TimelineWaveformTests`, `WaveformCoordinatorTests`, `WaveformIntegrationTests` | app 9.5 (silent video) |
| 34 | Volume | Set a clip's volume to 50 %, 100 %, 200 % (Inspector) | The height changes linearly; a full-scale peak reaches the clip's edge at 200 % and half of it at 100 %; nothing is decoded again | `WaveformLayoutTests`, `WaveformViewTests` | auto |
| 35 | Mute | Mute a clip (Inspector); unmute. (A muted track has no UI yet — covered by tests) | Muted: the same shape, dimmed; unmuted: as before; nothing is decoded again | `TimelineWaveformTests`, `WaveformViewTests` | app 9.5 (a muted clip) |
| 36 | Trim, move, split, undo | Trim a clip's start and end, move it, split it, undo each | The waveform always shows exactly the clip's source range (a split continues seamlessly); no decode; while a start trim is dragged the waveform follows only after the release | `WaveformLayoutTests`, `TimelineWaveformTests` | auto |
| 37 | Speed | Set an audio clip to 2× and to 0.5× | The source range is compressed / stretched onto the clip's length (a loudness step in the source at 6 s shows at 3 s / 12 s after the clip's start); no new waveform decode | `WaveformLayoutTests`, `TimelineWaveformTests` | app 9.5 (2×) |
| 38 | Zoom and scroll | Zoom in to the maximum on a long clip and scroll through it; zoom out to the minimum | The waveform is drawn across the visible part at every zoom; scrolling stays smooth; zoomed out, the loud parts stay visible (largest peak per column) | `WaveformLayoutTests` (viewport columns) | app 9.5 |
| 39 | Reopen from the cache | Save, close, open the project again | The waveforms appear at once; `ffmpeg-*.log` has no waveform decode (only the Preview's audio at the playhead); the `.peaks` files keep their last-write time | `WaveformServiceTests`, `WaveformCoordinatorTests` | app 9.5 |
| 40 | Offline with and without a cache | Close; rename the media folder; open. Then also delete one asset's `.peaks` and open again | Offline media with a cache shows its waveform, without one none; `ffmpeg-*.log` has no decode at all | `WaveformServiceTests`, `WaveformCoordinatorTests`, `TimelineWaveformTests` | app 9.5 |
| 41 | Unsaved project, Save, Save As, recovery | New project; add an audio clip; check `…\cache\unsaved\<id>\waveforms`; Save; Save As; crash-recover an unsaved project (as scenario 26) | The `.peaks` is in the unsaved folder, then moved to `<project>/cache/waveforms` (the unsaved folder gone once the thumbnails are out too), copied on Save As; a recovered project finds its waveforms; orphaned unsaved waveform folders are removed at the next start | `WaveformCacheLocationTests`, `StartupCacheCleanupTests` | auto |
| 42 | New / Open / close while waveforms are made | Put several long audio files on the timeline; while their waveforms are still being made press New (or Open), or close the window | At most 2 waveform decodes at once, independent of the thumbnails'; after the switch none of the old project keeps running and nothing of it appears; closing ends within seconds with no `ffmpeg.exe` left | `WaveformCoordinatorTests` | auto; clean close checked in app 9.5 |
| 43 | Playback and export untouched | With waveforms shown, play (also a muted clip and a clip at 2×) and export | Sound and export are as before; neither reads `.peaks` files | parity suite (`ExportEndToEnd.Tests`) unchanged | auto |

## Step 9.6 — hotkeys

| # | Scenario | Steps | Expected | Automated coverage | Status |
|---|---|---|---|---|---|
| 44 | Existing shortcuts unchanged | Try Ctrl+N / O / S / Shift+S, Ctrl+Z / Y / Shift+Z, Delete / Backspace, S, N, ← / →, Shift+← / →, Space, Home / End, Ctrl+= / − | Each does what it did before 9.6 | `ShortcutRoutingTests` | auto; N checked in app 9.6 |
| 45 | L and K | With a clip on the timeline press L, L, K, K | L plays and pressed again keeps playing; K pauses and pressed again stays paused | `PlaybackShortcutTests` | app 9.6 |
| 46 | J | Play, press J; pause, press J; press J near the start | Playing: one second back and playing on; paused: one second back, still paused; near the start: stops at 0 | `PlaybackShortcutTests` | app 9.6 |
| 47 | L at the end | Press End, then L | Playback starts from 0 (D011) | `PlaybackShortcutTests` | auto |
| 48 | Loop | Turn Loop on (button or Ctrl+L), play over the end; turn it off, play over the end; pause near the end with Loop on | On: continues from the start without stopping, again every time; off: stops at the end, paused; a pause is not undone. The button shows its state | `LoopPlaybackTests` | app 9.6 (on / off, button) |
| 49 | Loop is session state | Turn Loop on; check the title (no `*`), Undo, save and look into `project.json`; New project | Not dirty, nothing to undo, nothing about it in `project.json`; still on in the new project; off after restarting the app | `LoopPlaybackTests` | auto |
| 50 | Ctrl+I, Ctrl+E, \ | Press Ctrl+I, Ctrl+E; zoom in, then press \ (on an ISO keyboard also the key next to the left Shift) | The import picker, the export flow; the whole sequence fits the view | `ShortcutRoutingTests`, `PlaybackShortcutTests` | app 9.6 (\, US layout); Ctrl+I / Ctrl+E auto |
| 51 | No shortcut while typing | Select a text clip; in the Inspector's text box type J, K, L, space, S, N, Backspace, press Ctrl+L, Ctrl+E, Ctrl+I, Ctrl+N, \, Ctrl+A and type; in a number field (font size, position …) type letters and digits and press Ctrl+L | The characters go into the field, Backspace / Ctrl+A / typing edit the text; playback, loop, snapping, the playhead, the clips and the zoom are unchanged, no dialog opens; after Tab / focus elsewhere N, Ctrl+L, L, K, \ work again | `ShortcutRoutingTests` (every shortcut with a text box focused) | app 9.6 |
| 52 | During an export | Start an export; while it runs click the main window (e.g. Snap, + Video Track) and press S, Delete, Ctrl+Z, Ctrl+I, Ctrl+E, Ctrl+N, ←, →, N, L, K, Ctrl+L | The modal progress window (D023) disables the main window: no click or key reaches it — nothing changes, no dialog opens; after the export every command and shortcut works again. (Routing tests: an editing shortcut is inert under `EditingLock`, viewing ones are not) | `ShortcutRoutingTests` | auto |

## Step 9.7 — performance

Measured numbers (baseline, before / after) are in `progress.md`; this section only checks the shipped behaviour by
hand. Media: a 1920 × 1080, 30 fps video of 10 s or longer; a project with 4 video tracks of it, all above the bottom
at opacity 0.8 (nothing hidden).

| # | Scenario | Steps | Expected | Automated coverage | Status |
|---|---|---|---|---|---|
| 53 | Multi-layer export | Export the 4-layer project; watch Task Manager during it | Finishes; the MP4 plays and shows the four layers as the Preview does; during the export 4 `ffmpeg.exe` decoders + 1 encoder of the export (on top of the Preview's decoders of the open project) and about two cores for the app; after "Export finished" none of the export's `ffmpeg.exe` is left — the Preview's processes of the open project stay (not a leak) and are gone after closing it | `ExportServiceTests`, `ExportFrameSourceTests`, parity suite (`ExportEndToEnd.Tests`) | auto; measured with the scratch tool (9.7) |
| 54 | Cancel a multi-layer export | Start the 4-layer export; Cancel early in the video stage, then again near its middle | The dialog closes within a fraction of a second; no output file and no temporary files in the output folder; no `ffmpeg.exe` left | `ExportServiceTests` (cancellation, the fetch ahead cancelled and awaited) | auto; cancel latency measured (9.7) |
| 55 | Repeated exports | Export the same project 5 times in a row | Every export succeeds; memory may grow after the first export and then stays on a plateau — it does not keep growing from run to run (the bounded footprint accepted in Step 9.7; it is not returned after an export); handles without a trend; no export `ffmpeg.exe` left | — | measured with the scratch tool (10 runs, 9.7) |
| 56 | Preview with 8 layers | An 8-layer version of the project; play 10 s | Smooth playback at the content's rate, the layers in order; the app stays responsive | — | measured in the app (C, 9.7) |

## Step 9.8 — polish

| # | Scenario | Steps | Expected | Automated coverage | Status |
|---|---|---|---|---|---|
| 57 | Shortcut tooltips | Hover Import Media, Export (toolbar), Fit, + Video Track, + Audio Track (timeline), ⏮, Play, ⏭ (Preview) | The tooltips name the shortcut: Ctrl+I, Ctrl+E, \, ←, → and Space / L / K / J; the track buttons say what they add | — | app 9.8 |
| 58 | Empty timeline | New project; add a text clip (+ Text); Undo; drag a media file from the Media Browser onto a track | Empty: "The timeline is empty" with how to add a clip, over the tracks; it disappears with the first clip and comes back after Undo; the drop onto a track still works through the hint | `TimelineEmptyStateTests` | app 9.8 |
| 59 | Importing status | Import Media, pick several files | The status bar says "Importing N files…" while the picked files are checked, then the usual result ("Imported N media files" …); cancelling the picker leaves the status as it was | `ImportStatusTests` | app 9.8 |

## Formal run (Step 9.10)

2026-09-28 / 29, Claude with the product owner (14–17: the owner switched and unplugged the devices). The app: `dotnet run` of
`feat/phase-9-quality` at `e8f2410` (CI green), driven through its UI — the Windows file dialogs, UI Automation, real
cursor moves and key presses (`keybd_event`), screenshots; logs and processes read alongside; sound checked by a loopback
capture of the output device (level and frequency only). Test media and projects generated with ffmpeg.

| # | Result | Evidence |
|---|---|---|
| 1 | pass | closed in 807 ms, 0 ffmpeg/ffprobe left, last app line "Shutting down."; 2 decoders while idle |
| 2 | pass | closed while playing (label Pause, 2 ffmpeg) in 813 ms, 0 left, "Shutting down." |
| 3 | pass | paused (position 00:00:02:11 stable), closed in 815 ms, 0 left, "Shutting down." |
| 4 | pass | after Stop, closed in 812 ms, 0 left, "Shutting down." |
| 5 | pass | UI export of AV (1080p30, 12 s) in ~10 s: "Export finished" dialog, status "Exported to …"; MP4 h264 1920x1080 30/1 + aac, 12.000 s; closed in 814 ms, 0 left |
| 6 | pass | edit (+ Video Track) → title "AV*"; close → Cancel: window stays, playback 1.9 s, Home → 0, → ×3 → frame 3; close → Don't Save: 1352 ms, 0 left, "Shutting down." (note: + Text on the occupied V1 says "Can't add text on V1: Clips would overlap" — pre-Phase-9 behaviour) |
| 7 | pass | 30 clips imported, New (Ctrl+N) while 1 ffprobe ran → "Don't Save" prompt → empty project (0 rows, "No media imported"); 0 ffprobe 2 s later; av.mp4 + tone.mp4 imported afterwards: 2/2 with metadata in the saved project.json |
| 8 | pass | 30 clips importing, Open (picker first, then the save prompt → Don't Save) of "Wave" (5 media without saved metadata): all 5 analysed (durations / sizes shown), none of the 30 clips shown, 0 ffprobe left |
| 9 | n/a | no manual form (plan) — covered by `AnalysisGenerationTests` |
| 10 | pass | import av.mp4, Save, immediately Open the same folder: "0:12 · 1920×1080 · 30 FPS"; Add to Timeline works (duration 00:00:12:00) |
| 11 | pass | 30 files at once: max 4 ffprobe processes; all 30 with metadata in the saved project.json; UIA read of the UI 30 ms right after |
| 12 | pass | ffmpeg-*.log: 13 "ffmpeg started" command lines (Debug), "exited normally" and "was ended by the app (exit code -1)" endings, damaged.mp4: `[WRN] ffprobe exited with code 1: <command>` + "Invalid data found when processing input"; app-*.log: none of these lines (only the analysis coordinator's own "Metadata analysis failed … ProbeProcessFailed") |
| 13 | pass | garbage project.json: status "Couldn't open the project. The project file is damaged and can't be opened."; no backup mentioned; "AV" stays open |
| 14 | pass | paused; the user switched the Windows default output from "Динамики (Sound BlasterX G6)" to "Mi TV (NVIDIA High Definition Audio)"; Play: loopback of Mi TV ~448 Hz (the 440 Hz tone), peak 0.09, G6 silent; app-*.log "Audio output moved to the current default device {…5a766486…} (was {…333d7dae…})" |
| 15 | pass | "Long" project (one 1 h audio clip, 300 Hz sine, loop off) playing on G6; the user switched the default to Mi TV during playback: still G6 (300 Hz, peak 0.089), Mi TV silent; after a seek while playing (Shift+→): Mi TV 299 Hz, G6 silent. A first attempt with Loop on over the 12 s AV clip moved the sound at a loop wrap (23:11:00.8 = the wrap time) — by design, a wrap is a new start (9.3e checks at every start) — so it was repeated without loop |
| 16 | pass | the user connected wired headphones "Наушники (Realtek USB Audio)" (they became the default), a seek moved playback there (300 Hz in the headphones, Mi TV silent); unplugged while playing: no crash, app responding, position kept advancing evenly (2:37:14 → 2:40:06 in 2.7 s), status "Playing without sound: no audio output is available.", log "[WRN] Audio output failed; continuing playback without sound."; Pause → Play: "Audio output moved to the current default device {…333d7dae…}" and the tone on G6 (the new default). Observation: the status bar keeps "Playing without sound…" after the sound is back (the status shows the last message until another one) |
| 17 | pass | headphones plugged back in (default again), a seek moved playback there (300 Hz in the headphones, G6 silent), paused; unplugged while paused; Play: at once "Audio output moved to the current default device {…333d7dae…}" and the tone on G6 (peak 0.088), no silent phase |
| 18 | pass | av.mp4 and image.png show their picture, tone.wav and damaged.mp4 keep the colour tile (shots/s18-done.png); 2 thumbnail decodes (av, image). The "Analyzing" tile phase was too short to capture (analysis ≈ 0.1 s) |
| 19 | pass | scene.mp4 (red 0–1 s, blue after) in two new projects: blue both times, decoded at the same seek (1.92 s preroll for T = 2 s) |
| 20 | pass | reopen: thumbnail at once, 0 thumbnail decodes this session, one .thumb in <project>/cache/thumbnails |
| 21 | pass | source replaced by other content with the same name: new .thumb name (size/last-write key), the old one gone, 1 decode, new picture (blue) |
| 22 | pass | .thumb overwritten with garbage: made again silently (1 decode, same size as before), no WRN/ERR in app-*.log, no message |
| 23 | pass | both sources renamed, one .thumb deleted: both "Media offline"; the one with a cache shows its picture, the other the tile; 0 ffmpeg processes this session |
| 24 | pass | unsaved: .thumb under cache\unsaved\<id>\thumbnails; after Save: in <project>/cache/thumbnails, unsaved folder gone, "1 thumbnail(s) moved", formatVersion 2, thumbnailPath null; one decode in total (a second one counted by the script came from buffered log lines — the log shows only the decode before saving) |
| 25 | pass | Save As: the new folder has its own copy, the old folder keeps its .thumb, 0 decodes, thumbnail visible |
| 26 | pass | unsaved project (av.mp4 + tone.wav on the timeline), autosave after ~110 s, killed; an orphan cache folder added. Restart → "Recover unsaved work" → Recover: thumbnail shown, 0 thumbnail decodes; the two audio decodes at 0 are the Preview's readers (one per audible clip, same command as a waveform decode — plan: "count per file"); the orphan removed at that start, the recoverable folder kept; after closing (Don't Save) the recovery file and the unsaved folder are gone |
| 27 | pass | 30 slow 4K files: max 2 thumbnail ffmpeg at once; New while 2 ran → 0 old-project ffmpeg 0.7 s later, only 8 of 30 had started; the new project's tone.mp4 got its thumbnail |
| 28 | pass | close while thumbnails were made (1 running): exited in 1401 ms, 0 ffmpeg left, last line "Shutting down." |
| 29 | pass | portrait.mp4 (display matrix 90°): upright portrait thumbnail, centred, colour on both sides (shots/s29-portrait.png) |
| 30 | pass | with thumbnails shown: Add to Timeline, play 2 s, UI export: MP4 1920×1080 h264 + aac, 12.000 s; no log line names a .thumb, no thumbnail decode during play / export |
| 31 | pass | import tone.wav + tone.mp4: 0 audio decodes, 0 .peaks; after Add to Timeline of tone.mp4 only: tone.mp4 ×2 (waveform + the Preview at the playhead), 1 .peaks; tone.wav none |
| 32 | pass | Wave project: tone.wav on A1 over the clip height (flat 0–2 s, quiet 2–6 s, loud 6–10 s), tone.mp4 in the lower half of its clip, step.wav quiet → loud at 6 s (shots/s32-wave.png) |
| 33 | pass | silent.mp4, image.png and a text clip show no waveform; no audio decode of silent.mp4 / image (shots/s33.png) |
| 34 | pass | loud-part height 50 % = 5 px, 100 % = 13 px, 200 % = 25 px (of 36; linear, 200 % reaches the edge region); 0 audio decodes while changing (shots/s34-*.png) |
| 35 | pass | muted: same shape, dimmed (shots/s35-muted.png); unmuted as before; 0 decodes |
| 36 | pass | trim start by 2 s, end back to 8 s, move by 1 s: the waveform always shows the clip's source range (the step stays at source 6 s: timeline 6 s, then 7 s after the move); split: the two parts continue seamlessly (first run); 4 × Undo restores the clip; 0 audio decodes (shots/s36*-montage.png). Not checked: the start-trim drag preview mid-drag (a D024 residual) |
| 37 | pass | step.wav at 2×: the step at 3 s, clip 6 s; at 0.5×: the step at 12 s, clip 24 s; no new waveform decode |
| 38 | pass | max zoom: drawn, the step exactly at 0:06.00 after scrolling (shots/s38-max-loud.png); min zoom: loud parts visible; Fit fits the 15 s sequence; 0 decodes while zooming |
| 39 | pass | first open made 3 .peaks; reopen: waveforms at once, .peaks last-write unchanged 3/3; audio decodes only the Preview's (tone.mp4 ×1, tone.wav ×2, step.wav ×2 — the second reader of each WAV ended by the app within 10–25 ms: the Preview's audio pipeline restarting while the project loads, not a waveform decode) |
| 40 | pass | media folder renamed: 5 media offline, waveforms from the cache, 0 ffmpeg / ffprobe started; step.wav's .peaks deleted: that clip shows none, still 0 decodes (shots/s40-*.png) |
| 41 | pass | unsaved: .peaks under cache\unsaved\<id>\waveforms; Save: "1 waveform(s) moved", unsaved folder gone; Save As: "1 waveform(s) copied", both folders have it; recovery: see 26 |
| 42 | pass | 6 × 3 h audio clips: max 2 waveform decodes at once (+ the Preview's reader of the first clip); New while 2 ran: all 3 ended within 6 ms, the other 4 never started; close while making: 1360 ms, 0 ffmpeg left, "Shutting down." |
| 43 | pass | tone.wav muted, step.wav at 2× (playback used atempo=2); play 4 s and UI export: MP4 h264 + aac 15.000 s; no log line names a .peaks file |
| 44 | pass | real key presses (keybd_event): Ctrl+O / Ctrl+Shift+S open their pickers; + Video Track, Ctrl+Z / Ctrl+Y / Ctrl+Shift+Z undo / redo; Ctrl+S "Saved project"; S splits at the playhead (2 clips); Delete / Backspace delete the selected clip, Ctrl+Z restores; N toggles Snap; ← / → one frame, Shift+← / → one second (1:01 → 2:01 → 1:01), Home / End; Space play / pause; Ctrl+= / Ctrl+− zoom; Ctrl+N new project |
| 45 | pass | L plays, L again keeps playing; K pauses, K again stays paused at the same position |
| 46 | pass | playing: J one second back and playing on; paused: 1:27 → 0:27, still paused; near the start: 0:10 → 0:00 |
| 47 | pass | End (12:00), L → playback from 0 |
| 48 | pass | Ctrl+L on: 11:29 → 00:04 → 01:07 without stopping; button off: stops at 12:00, paused; on and paused 0.4 s after playing from 11:00: stays paused at 11:28; the button shows its state |
| 49 | pass | loop on: title without *, Undo disabled, saved project.json has nothing about loop; still on after New; off after restarting the app |
| 50 | pass | Ctrl+I → "Import Media" picker, Ctrl+E → "Export — choose the output file"; \ (VK_OEM_5) and the ISO key (VK_OEM_102) zoom to fit: 226 → 278 px/s and 142 → 278 px/s, the same as the Fit button |
| 51 | pass | text box of a text clip: J K L Space S N Backspace, Ctrl+L / E / I / N, \, Ctrl+A + typing edit only the text ("Textолд ы\" → "чн"; keyboard layout Russian); playback, loop, snap, playhead, clips, zoom unchanged, no dialog; font-size field: digits and letters go into the field ("487", "48йц7"), no shortcut fires; after a click on an empty track / Tab (focus to the font combo box): N, Ctrl+L, Ctrl+=, \, L, K work again. Note: letters can be typed into a number field — by design (NumericInput, Phase 7: invalid text keeps the last value) |
| 52 | pass (plan corrected) | Layers8 export running: the main window is disabled (Win32 IsWindowEnabled = false) by the modal progress window; real clicks on Snap and + Video Track and real N / → presses change nothing; after the export a click on Snap works again. (A first check forced the disabled main window to the foreground and injected keys — viewing keys then "worked", editing ones stayed inert; not what a user can do, so the plan now describes the modal window, D023) |
| 53 | pass | UI export of Layers4 (4 × 1080p30, opacity 0.8, 12 s) in ~8.6 s; export frame at 5 s = frame 150; during it 4 decoders + 1 encoder of the export on top of the Preview's 8 (4 video + 4 audio) of the open project; the app itself ≈ 2 cores (1.90–2.02 over 10 exports); after "Export finished" the export's ffmpeg are gone, the Preview's 8 stay while the project is open; 0 after closing |
| 54 | pass | Cancel at 13 % and at 46 % of the video stage: the progress window closed 249 / 258 ms after Cancel, "Export cancelled.", no output and no temporary file in the folder, only the Preview's ffmpeg left |
| 55 | pass | 10 exports in a row, each succeeded; private memory 401 MB idle → 1350 MB after the first, then 1352–1365 MB flat over 10 (plateau, no growth; still 1361 MB after 30 s idle — not returned, as accepted in 9.7); handles 1484–1572 without a trend (1488 after 30 s idle); 0 ffmpeg after closing. Same pattern before A (e8c3c93): 400 → 858–920 MB |
| 56 | pass | 8 layers: 9:25 of content in 10.3 s of wall time (the start includes the key press), steady progress every sample, UIA reads ≤ 65 ms (responsive); 16 decoders (8 video + 8 audio) |
| 57 | pass | real cursor hover: Import Media "(Ctrl+I)", Export "(Ctrl+E)", Fit "Zoom to fit (\)", "Add a video track", "Add an audio track", ⏭ "Next frame (→)", Play "Play / Pause (Space; L plays, K pauses, J one second back)"; ⏮ "(←)" (its shot was overwritten; seen in 9.8) |
| 58 | pass | new project: hint shown; + Text → gone; Undo → back; drag tone.mp4 onto V1 → clip added, hint gone; with 8 tracks the hint lies over a track row and a drop onto the hint text adds the clip to that track (not hit-testable); with 2 tracks the hint sits below the rows, where a drop adds nothing (no track there) |
| 59 | pass | 600 files: "Importing 600 files…" from ~0.13 s to ~0.57 s, then "Imported 600 media files"; picker cancelled: the status unchanged |
| real audio device | pass | loopback capture of the default device "Динамики (Sound BlasterX G6)" while AV plays: ~436 Hz tone (the 440 Hz source), peak 0.09; silent while paused; app log "Audio output: device {…333d7dae…}, 100 ms latency" |
| 4K | pass (automated) | the manual plan has no 4K scenario; the two opt-in 4K parity scenes with AIVE_HEAVY_TESTS=1: 2 / 2 passed |

## Result log

| Date | Tester | Scenarios | Result |
|---|---|---|---|
| 2026-09-28 / 29 | Claude, with the product owner for 14–17 | 1–59 | 58 pass, 0 fail; 9 has no manual form; plan wording corrected for 52 (modal window), 53 (export processes) and 55 (memory plateau) — details above |
