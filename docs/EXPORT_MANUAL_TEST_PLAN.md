# Export — manual test plan (Phase 8 Step 7)

Run in the real app (`dotnet run --project src/App/App.csproj`) with ffmpeg on PATH. Automated coverage: UI flow in
`tests/UI.Tests/ExportWorkflowTests.cs` (fakes), the pipeline in `tests/ExportEndToEnd.Tests` (real ffmpeg). The
known app-close hang is out of scope; don't close the app while an export runs.

For every successful export: the progress window shows Preparing → Audio → Video → Finalizing with "done / total"
and a percentage; the window closes on its own; an "Export finished" message names the file; the title bar gets no
`*` from exporting; Undo doesn't offer an export step; the timeline and Media Browser are unchanged (the MP4 is not
imported).

| # | Scenario | Steps | Expected |
|---|---|---|---|
| 1 | Normal project | Video clip + audio, Export, pick a new `.mp4` | MP4 plays in a normal player (e.g. VLC / Windows Media Player); size = project canvas, same rate, picture and sound as in the Preview |
| 2 | Text | Add "+ Text", change font/size/colour/position | Text appears as in the Preview (same place, size, colour); a missing font gives a warning with Continue / Cancel |
| 3 | Crop / scale / rotation / opacity | Set each on a clip over another clip | Output matches the Preview |
| 4 | Hidden track with audio | Hide the video track of a clip with sound | Output: no picture of that clip (black / lower layer), its sound present |
| 5 | Muted audio | Mute a clip and a track | Those are silent in the output |
| 6 | No audio | Only video / text / images | Output has a silent AAC track (player shows an audio stream) |
| 7 | Cancel during audio | Long project; Cancel while the stage is "Audio" | "Cancelling…", window closes when done, status "Export cancelled.", no error message, no new/partial file, no `.partial` / `.audio.m4a` left in the folder |
| 8 | Cancel during video | Cancel while "Video" | As 7 |
| 9 | Cancel during finalizing | Only if you manage to hit it | As 7 (or the export finishes if it was already complete) |
| 10 | Missing / offline source | Rename a used media file, then Export | "Export not possible" lists the offline file under **Errors**; the file picker doesn't open; nothing written. Deleting a source while the export runs → "A source file could not be read", no output |
| 11 | Existing destination | Pick an existing `.mp4` | "Replace file?" appears; Cancel → nothing happens; Replace → file replaced only when the export succeeds; cancelled/failed export leaves the old file byte-identical |
| 12 | File picker cancelled | Export, close the picker | Nothing happens, no message |
| 13 | Editing locked | During an export try: keyboard shortcuts (S, Delete, Ctrl+Z, Ctrl+N/O/S), clicking the main window | The main window doesn't react (modal progress window); after the export every command works again |
| 14 | Output in a media player | Open the results of 1–6 in a normal player | Plays from start to end, A/V in sync, correct duration |

Also: exporting twice suggests the last output folder and file name; choosing a `.mov` name reports that the output must
be `.mp4` (no silent renaming); without ffmpeg the preflight says ffmpeg could not be found.

## Result log

| Date | Tester | Scenarios | Result |
|---|---|---|---|
| 2026-09-25 | Claude (UI Automation on the real app) | 1–14 | 14 PASS, 0 FAIL, 0 BLOCKED — details in `progress.md` (Step 7 manual test) |
