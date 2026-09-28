# Phase 9 — manual test plan

Run in the real app (`dotnet run --project src/App/App.csproj`, ffmpeg / ffprobe on PATH). Written during the Phase 9
steps (D024) and run as a whole at Step 9.10; sections are added per step. Logs: `%LOCALAPPDATA%\AiVideoEditor\logs`
(`app-*.log`, `ffmpeg-*.log`, `errors-*.log`).

Status column: **auto** — covered by automated tests only, manual run pending (9.10); **UIA 9.3** — driven through the
real app's UI by UI Automation during Step 9.3 (a development check, not the formal run); **manual-only, not
executed** — needs hardware or system changes that were not made; must be run by hand at 9.10; **app 9.4** — checked
in the real app during Step 9.4 (a development check, not the formal run); **app 9.5** — checked in the real app
during Step 9.5 (a development check, not the formal run); **app 9.6** — checked in the real app during Step 9.6
(keys posted to / typed into the window, a development check, not the formal run).

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
| 13 | Damaged project | Close the app, replace a project's `project.json` with garbage, Open that folder | The status bar says the project file is damaged and can't be opened (with the reason); no promise of a backup; the current project stays open | `DamagedProjectMessageTests`, `ErrorTranslatorTests` | auto |
| 14 | Default audio device changed while paused | Play briefly, Pause; in Windows switch the default output device (e.g. speakers → headphones); Play | Sound comes from the new default device; `app-*.log`: "Audio output moved to the current default device …" | `AudioDeviceChangeTests` (fake devices) | **manual-only, not executed** |
| 15 | Default audio device changed while playing | Play; switch the Windows default output device during playback; then seek | Sound stays on the old device until the seek (no switch during playback); after the seek it comes from the new default | `AudioDeviceChangeTests` (fake devices) | **manual-only, not executed** |
| 16 | Audio device removed while playing | Play through USB / Bluetooth headphones; unplug / disconnect them | No crash, no hang; the picture keeps playing without a jump; "Playing without sound" in the status bar; after Pause and Play the sound comes from the current default device | `AudioDeviceChangeTests` (fake devices), `AudioPlaybackServiceTests` | **manual-only, not executed** |
| 17 | Audio device removed while paused | Pause; unplug the playing device; Play | Sound comes from the current default device at once | `AudioDeviceChangeTests` (fake devices) | **manual-only, not executed** |

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
| 26 | Recovery and startup cleanup | New project with a video (unsaved), wait for an autosave (2 min), kill the app (Task Manager); start it → Recover. Then leave an unsaved project's folder under `cache\unsaved` without a recovery file and start again | Recover shows the thumbnail from its unsaved folder without decoding; at the next start the orphan folder is removed, the recoverable one kept | `ThumbnailCacheLocationTests`, `StartupThumbnailCleanupTests` | auto |
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
| 41 | Unsaved project, Save, Save As, recovery | New project; add an audio clip; check `…\cache\unsaved\<id>\waveforms`; Save; Save As; crash-recover an unsaved project (as scenario 26) | The `.peaks` is in the unsaved folder, then moved to `<project>/cache/waveforms` (the unsaved folder gone once the thumbnails are out too), copied on Save As; a recovered project finds its waveforms; orphaned unsaved waveform folders are removed at the next start | `WaveformCacheLocationTests`, `StartupThumbnailCleanupTests` | auto |
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
| 52 | During an export | Start an export; press S, Delete, Ctrl+Z, Ctrl+I, Ctrl+E, Ctrl+N; press ←, →, N, L, K, Ctrl+L | Editing shortcuts do nothing; viewing and playback ones work | `ShortcutRoutingTests` | auto |

## Step 9.7 — performance

Measured numbers (baseline, before / after) are in `progress.md`; this section only checks the shipped behaviour by
hand. Media: a 1920 × 1080, 30 fps video of 10 s or longer; a project with 4 video tracks of it, all above the bottom
at opacity 0.8 (nothing hidden).

| # | Scenario | Steps | Expected | Automated coverage | Status |
|---|---|---|---|---|---|
| 53 | Multi-layer export | Export the 4-layer project; watch Task Manager during it | Finishes; the MP4 plays and shows the four layers as the Preview does; during the export about 4 `ffmpeg.exe` decoders + 1 encoder and about two cores busy; no `ffmpeg.exe` left after "Export finished" | `ExportServiceTests`, `ExportFrameSourceTests`, parity suite (`ExportEndToEnd.Tests`) | auto; measured with the scratch tool (9.7) |
| 54 | Cancel a multi-layer export | Start the 4-layer export; Cancel early in the video stage, then again near its middle | The dialog closes within a fraction of a second; no output file and no temporary files in the output folder; no `ffmpeg.exe` left | `ExportServiceTests` (cancellation, the fetch ahead cancelled and awaited) | auto; cancel latency measured (9.7) |
| 55 | Repeated exports | Export the same project 5 times in a row | Every export succeeds; memory in Task Manager rises during an export and falls back afterwards without growing from run to run; no `ffmpeg.exe` left | — | measured with the scratch tool (10 runs, 9.7) |
| 56 | Preview with 8 layers | An 8-layer version of the project; play 10 s | Smooth playback at the content's rate, the layers in order; the app stays responsive | — | measured in the app (C, 9.7) |

## Result log

| Date | Tester | Scenarios | Result |
|---|---|---|---|
| — | — | — | Not run yet (formal run at Step 9.10) |
