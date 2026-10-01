# Phase 11 — manual test plan

Run in the real app (`dotnet run --project src/App/App.csproj`, ffmpeg / ffprobe on PATH unless a scenario says
otherwise). Written during the Phase 11 steps (D026, product owner decisions PO-1…PO-9) and run as a whole at Step 11.9;
the scenarios below are the skeleton agreed at Step 11.2 — exact steps, expected messages, fixtures and automated
coverage are completed by the step that implements them. Logs: `%LOCALAPPDATA%\AiVideoEditor\logs`.

Fixtures: written by the steps that need them (scripts under `tools/manual`, projects and media outside the
repository, as in Phase 10). Moving, renaming and removing media files is done in Explorer or PowerShell while the app
runs; a "temporarily unavailable" file is simulated by a removable or mapped drive, or by renaming its folder.

Status column: **planned** — written at 11.2, not yet runnable; **auto** — covered by automated tests only, manual run
pending (11.9); **manual pending** — runnable, not yet run in the real app; **app 11.x** — checked in the real app during
that step (a development check, not the formal run); **PO** — run by the product owner; **app 11.3 (Claude), passed** — run in the real app by Claude for the Step 11.3
acceptance (results log below).

## Step 11.3 — media availability re-check (D026 §2, PO-5)

| # | Scenario | Steps | Expected | Automated coverage | Status |
|---|---|---|---|---|---|
| 1 | File removed during the session | Open a project with online media on the timeline; move one file away; switch to another window and back | The asset becomes offline: "Media offline" in the Media Browser, the Preview's placeholder, the status bar says "… is missing now and is shown as offline."; the project stays clean (no `*`) | `MediaRecheckTests` (gone, never dirty), `MediaAvailabilityTests` (Preview, Media Browser), `MediaAvailabilityMonitorTests` (status bar) | app 11.3 (Claude), passed |
| 2 | File comes back | After 1, move the file back; activate the window | The asset is online again without reopening: picture in the Preview, thumbnail, waveform; "… is available again."; clean | `MediaRecheckTests`, `MediaAvailabilityTests` (Preview frame, thumbnail / waveform restarted, old work dropped) | app 11.3 (Claude), passed |
| 3 | Missing at Open, back later | Open a project with a file missing; restore the file; activate the window | Online; an asset without saved metadata is analysed; thumbnail / waveform appear | `MediaRecheckTests` (returned), `MediaAvailabilityTests` (analysis of `Pending` / `Failed`, display-size refresh, offline thumbnail / waveform made on return) | app 11.3 (Claude), passed |
| 4 | Export right after a removal | Remove a used file and start Export at once (within the throttle interval) | The preflight reports the media as offline; nothing is exported. And the other way: a file put back and Export at once — no offline error | `ExportWorkflowTests` (gone / back since the last check) | app 11.3 (Claude), passed |
| 5 | Slow or disconnected drive | Media on a removable / mapped drive; disconnect it; activate the window, play, edit | The UI stays responsive while the check waits; the asset becomes offline | `MediaRecheckTests` (a held file system: the caller returns at once, no check on its thread; a replaced project's result dropped; requests folded) | app 11.3 (Claude), passed |
| 6 | Throttle | Switch windows repeatedly; restore a file and switch back within 3 s of the last switch | At most one check per 3 s; the file is still seen (one trailing check at the end of the interval); no visible stall | `MediaAvailabilityMonitorTests` (interval, one trailing check, stop at close) | app 11.3 (Claude), passed |

## Steps 11.4–11.6 — relink (D026 §3–§5, PO-1…PO-3, PO-6, PO-9)

Runnable since Step 11.6: Media Browser → the OFFLINE row (shown while media is offline) → Relink… / Find Missing…. Expected results follow D026 "Refined in Step 11.4" (the compared characteristics; a failed probe is a reject, ffprobe unavailable is not), "Refined in Step 11.5" (batch) and "Refined in Step 11.6" (the dialogs; the search offered only after an applied relink while other media are offline).

| # | Scenario | Steps | Expected | Automated coverage | Status |
|---|---|---|---|---|---|
| 7 | Moved file | A used video moved to another folder; Relink it to the new place | Online; clips unchanged (position, source range, speed, fades, dissolves); Preview and export use it; the project is dirty | `MediaRelinkServiceTests` (id, clips, path, size, metadata, dirty), `MediaRelinkUiTests` (Preview, thumbnail, waveform), `MediaRelinkIntegrationTests` (real ffprobe), `MediaRelinkWorkflowTests` (the UI flow) | manual pending |
| 8 | Renamed file | A used file renamed; Relink to the new name | As 7 | `MediaRelinkServiceTests` (any name of the right kind), `MediaRelinkWorkflowTests` (the UI flow) | manual pending |
| 9 | Save and reopen | After 7, Save, close, reopen | The new path is used; not missing | `MediaRelinkServiceTests` (save, reopen: absolute and relative path, metadata), `MediaRelinkWorkflowTests` (the UI flow) | manual pending |
| 10 | Hard rejects | Relink to: a file of another media type; a shorter video than a clip's source range; a file already used by another asset; a path that doesn't exist | Each refused with its own message; nothing changes | `MediaRelinkServiceTests` (each reject, Apply re-validation), `MediaRelinkIntegrationTests` (shorter, sound-only mp4), `MediaRelinkWorkflowTests` (the UI flow) | manual pending |
| 11 | Warnings | Relink to a file with a different resolution / frame rate / without audio | A warning lists the differences; Cancel changes nothing, confirming relinks | `MediaRelinkServiceTests` (each warning, none when equal), `MediaRelinkIntegrationTests` (resolution), `MediaRelinkWorkflowTests` (the UI flow) | manual pending |
| 12 | Short dissolve handles | Relink a clip of a dissolve to a file with less media beyond the clip | A warning; after confirming, the zone renders with held frames | `MediaRelinkServiceTests` (dissolve handles warning), `MediaRelinkWorkflowTests` (the UI flow) | manual pending |
| 13 | Without ffprobe | Make ffprobe unavailable (configuration / PATH); Relink | Allowed; told that compatibility couldn't be checked; the asset is pending; analysed once ffprobe is available | `MediaRelinkServiceTests` (allowed, `Pending`, later incompatibility), `MediaRelinkUiTests` (not analysed, status message, preflight), `MediaRelinkWorkflowTests` (the UI flow) | manual pending |
| 14 | Undo / Redo | Relink, then Ctrl+Z, Ctrl+Y (and the toolbar buttons) | Undo: offline again with the old path and metadata; Redo: online; thumbnail / waveform / Preview follow; dirty follows the save point | `MediaRelinkServiceTests` (exact undo / redo, save point), `MediaRelinkUiTests` (thumbnail after undo, stale work dropped), `MediaRelinkWorkflowTests` (the UI flow) | manual pending |
| 15 | Online media | Select an online asset; a project with no offline media | Relink… disabled for it; without offline media the OFFLINE row is hidden | `MediaRelinkWorkflowTests` (commands) | manual pending |
| 16 | During an export | Start an export; try Relink / Find Missing; let it end or cancel it | Disabled while it runs (`EditingLock`), enabled again afterwards; an export started during a check stops the relink | `MediaRelinkWorkflowTests` (lock before / after awaits, buttons told) | manual pending |
| 17 | Picker start folder | Relink an asset whose old folder still exists; then one whose folder is gone | The picker starts there (else wherever the system starts) and filters the asset's kind | `MediaRelinkWorkflowTests` (start folder, filter) | manual pending |

## Step 11.5 — batch relink (D026 §4, PO-4)

| # | Scenario | Steps | Expected | Automated coverage | Status |
|---|---|---|---|---|---|
| 18 | Batch found | Several missing files moved together into one folder; Relink one of them there | A summary lists the other matches (exact name ignoring case, that folder only) with their warnings; confirming relinks them in one step | `MediaRelinkBatchTests` (exact names, real folder + case, partial batch + summary, one step), `MediaRelinkWorkflowTests` (summary, cancel, partial, offer after a relink) | manual pending |
| 19 | Not found / subfolder | One file in a subfolder of the chosen folder, one renamed | Neither is matched; both stay offline; a second search later finds only what is still offline | `MediaRelinkBatchTests` (no recursion, no similar names, second search), `MediaRelinkWorkflowTests` (summary, cancel, partial, offer after a relink) | manual pending |
| 20 | Unusable match / cancel | A match of the wrong type or too short, a file of another item, two offline items of one name; then Cancel the summary | Each listed as not applicable with its reason (the shared name given to neither); Cancel applies nothing | `MediaRelinkBatchTests` (wrong type, owned file, shared name, single-check parity, not confirmed / cancelled), `MediaRelinkWorkflowTests` (summary, cancel, partial, offer after a relink) | manual pending |
| 21 | Batch undo | Confirm a batch; Undo; Redo | One Undo restores every item of the batch, one Redo relinks them all again (D026 "Refined in Step 11.5") | `MediaRelinkBatchTests` (undo / redo of the batch), `MediaRelinkUiTests` (batch thumbnails, undo), `MediaRelinkWorkflowTests` (summary, cancel, partial, offer after a relink) | manual pending |

## Steps 11.7–11.8 — recent projects (D026 §6, PO-7, PO-8)

| # | Scenario | Steps | Expected | Automated coverage | Status |
|---|---|---|---|---|---|
| 22 | Added by Open / Save As / Recover | Open a project; Save As to a new folder; recover a project that has a folder | Each is at the top of `Recent ▾` (next to Open) | — | planned |
| 23 | Failed Open | Open a folder without a project / a damaged project | The list is unchanged | — | planned |
| 24 | Limit and duplicates | Open 11 projects; open one again with a differently cased path | 10 entries, the newest first; no duplicate | — | planned |
| 25 | Unavailable project | Rename a listed project's folder; open `Recent ▾` | The entry stays, shown as unavailable, and can be removed; choosing it opens nothing and keeps the current project | — | planned |
| 26 | Unsaved changes | With unsaved changes choose a recent project | Save / Don't Save / Cancel as for Open | — | planned |
| 27 | Two instances | Open different projects in two running instances | Both entries are kept | — | planned |
| 28 | Damaged list | Damage the list file; start the app | The app starts; the list is empty or recovered as decided at 11.7 | — | planned |
| 29 | During an export | Start an export | `Recent ▾` is disabled | — | planned |

## Regression (Step 11.9)

| # | Scenario | Steps | Expected | Automated coverage | Status |
|---|---|---|---|---|---|
| R1 | Phase 10 project | Open a project saved by the Phase 10 build | Opens unchanged; Save writes `formatVersion` 3 | — | planned |
| R2 | Export manual plan | `docs/EXPORT_MANUAL_TEST_PLAN.md` | As before | — | planned |

## Results log

Filled in per run (date, build / commit, who ran it, scenario results).

### 2026-10-01 — Step 11.3 acceptance run (Claude), `db0feba`, Debug

Build of `db0feba` from the session's artifacts folder (`src/App/bin` was locked by another running instance, which was
left alone); every input pinned to the test process (UI Automation, keyboard only when its window was in front); the
window deactivated by bringing a Notepad window to the front, files moved with PowerShell; evidence: screenshots, the
app log, the project's cache folder. Scratch fixtures (not in the repository): V1 `pattern.mp4` (video + sound) and V2
`bars.mp4` (video + sound, half size) at 0–4 s, A1 `tone.wav`; no saved metadata.

| # | Result | Observed |
|---|---|---|
| 3 | pass | `bars.mp4` and `tone.wav` renamed away before Open: "Media offline" rows, status "2 media files are missing…". Put back while the window was inactive, window activated: "Media file is available again" ×2, "Analysing 2 media file(s) that are available again"; both rows online with metadata (0:08 · 640×360 · 25 FPS / 48 kHz · Stereo), the bars thumbnail made, waveforms of bars and tone drawn, cache files written at the moment of the return; the Preview shows bars over pattern; status "2 media files are available again." |
| 1 | pass | `pattern.mp4` renamed away during the session (playhead in a gap): "is missing now" at the next activation; row "Media offline" (its earlier thumbnail kept, D024); status message; title without `*`; on the clip the Preview shows the "Media offline" placeholder under bars. Note: a file that the Preview is decoding (playhead on its clip) can't be renamed on Windows at all — the OS lock; such a file disappears in practice only with its drive. |
| 2 | pass | Put back, window activated: "available again", the Preview shows the pattern frame again, row online — without reopening. Same size and time → the cached thumbnail was reused (no decode). Extra: a different file put under the same name → a new thumbnail was made (cache key changed) and shown; the asset's size and metadata stay from the import (online files are not re-validated, PO-6). |
| 6 | pass | Activations at 19:15:25.077 and 19:15:25.728 (`tone.wav` renamed away between them): no log line right after the second; one trailing check at 19:15:28.095 ("tone.wav is missing now") without another switch. |
| 4 | pass | Window kept active (no activation), `pattern.mp4` renamed away, Export: "missing now" logged by the export's own check (after the click), dialog "Export not possible — 'pattern.mp4' is offline (1 clip, first at 00:00:00.000)". Then put back (no activation for > 3 s), Export: "available again" logged by the export's check, no preflight error, the output-file picker opened (cancelled). Note: Export was triggered through the toolbar button (UI Automation); Ctrl+E sent with `SendKeys` did nothing in this run — not investigated (not part of 11.3; the shortcut was accepted in Phase 9). |
| 5 | pass | A fixture with an extra asset on an unreachable share (`\\10.255.255.1\share\unc.mp4`, `File.Exists` ≈ 11 s): activation at 21:32:23.921 with `tone.wav` renamed away — the change applied at 21:32:44.909 (the check waited on the share); meanwhile the slowest UI Automation answer was 14 ms; during an earlier check playback ran smoothly (00:09:01 → 00:12:11 in 3 s). Note: opening that project took ≈ 47 s (Open resolves the paths and marks missing media file by file, before the project is shown; the window stayed responsive) — Open's behaviour, unchanged since Phase 6. |
