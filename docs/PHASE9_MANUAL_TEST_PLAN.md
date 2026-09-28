# Phase 9 — manual test plan

Run in the real app (`dotnet run --project src/App/App.csproj`, ffmpeg / ffprobe on PATH). Written during the Phase 9
steps (D024) and run as a whole at Step 9.10; sections are added per step. Logs: `%LOCALAPPDATA%\AiVideoEditor\logs`
(`app-*.log`, `ffmpeg-*.log`, `errors-*.log`).

Status column: **auto** — covered by automated tests only, manual run pending (9.10); **UIA 9.3** — driven through the
real app's UI by UI Automation during Step 9.3 (a development check, not the formal run); **manual-only, not
executed** — needs hardware or system changes that were not made; must be run by hand at 9.10; **app 9.4** — checked
in the real app during Step 9.4 (a development check, not the formal run).

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

## Result log

| Date | Tester | Scenarios | Result |
|---|---|---|---|
| — | — | — | Not run yet (formal run at Step 9.10) |
