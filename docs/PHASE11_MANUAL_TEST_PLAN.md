# Phase 11 — manual test plan

Run in the real app (`dotnet run --project src/App/App.csproj`, ffmpeg / ffprobe on PATH unless a scenario says
otherwise). Written at Step 11.2 (D026, product owner decisions PO-1…PO-9), completed by the step that implemented
each feature, and closed at Step 11.9: the scenarios not run in the real app before, R1, R2 and a regression were run
then; the others count with the results recorded at their step (the product owner's decision for 11.9, variant (b)).
Logs: `%LOCALAPPDATA%\AiVideoEditor\logs`.

Fixtures: written by the steps that need them (scripts under `tools/manual`, projects and media outside the
repository, as in Phase 10). Moving, renaming and removing media files is done in Explorer or PowerShell while the app
runs; a "temporarily unavailable" file is simulated by a removable or mapped drive, or by renaming its folder.

Status column — three kinds of evidence, never mixed:
- **auto** / "automated only" — covered by automated tests only, not checked in the real app;
- **app 11.x (Claude), passed** — checked in the real app by Claude while implementing / accepting step 11.x (a
  development-time check with its results log; at 11.9 these count without a re-run, variant (b));
- **11.9 (Claude): PASS / FAIL / BLOCKED / NOT RUN** — the formal Step 11.9 run (results log below);
- **PO** — run by the product owner; **n/a** — not part of Phase 11.
A scenario is marked passed only for the run that actually checked it; the "Automated coverage" column is separate.

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
| 7 | Moved file | A used video moved to another folder; Relink it to the new place | Online; clips unchanged (position, source range, speed, fades, dissolves); Preview and export use it; the project is dirty | `MediaRelinkServiceTests` (id, clips, path, size, metadata, dirty), `MediaRelinkUiTests` (Preview, thumbnail, waveform), `MediaRelinkIntegrationTests` (real ffprobe), `MediaRelinkWorkflowTests` (the UI flow) | app 11.6 (Claude), passed |
| 8 | Renamed file | A used file renamed; Relink to the new name | As 7 | `MediaRelinkServiceTests` (any name of the right kind), `MediaRelinkWorkflowTests` (the UI flow) | app 11.6 (Claude), passed |
| 9 | Save and reopen | After 7, Save, close, reopen | The new path is used; not missing | `MediaRelinkServiceTests` (save, reopen: absolute and relative path, metadata), `MediaRelinkWorkflowTests` (the UI flow) | app 11.6 (Claude), passed |
| 10 | Hard rejects | Relink to: a file of another media type; a shorter video than a clip's source range; a file already used by another asset; a path that doesn't exist | Each refused with its own message; nothing changes | `MediaRelinkServiceTests` (each reject, Apply re-validation), `MediaRelinkIntegrationTests` (shorter, sound-only mp4), `MediaRelinkWorkflowTests` (the UI flow) | app 11.6 (Claude), passed |
| 11 | Warnings | Relink to a file with a different resolution / frame rate / without audio | A warning lists the differences; Cancel changes nothing, confirming relinks | `MediaRelinkServiceTests` (each warning, none when equal), `MediaRelinkIntegrationTests` (resolution), `MediaRelinkWorkflowTests` (the UI flow) | app 11.6 (Claude), passed |
| 12 | Short dissolve handles | Relink a clip of a dissolve to a file with less media beyond the clip | A warning; after confirming, the zone renders with held frames | `MediaRelinkServiceTests` (dissolve handles warning), `MediaRelinkWorkflowTests` (the UI flow) | app 11.6 (Claude), passed |
| 13 | Without ffprobe | Make ffprobe unavailable (configuration / PATH); Relink | Allowed; told that compatibility couldn't be checked; the asset is pending; analysed once ffprobe is available | `MediaRelinkServiceTests` (allowed, `Pending`, later incompatibility), `MediaRelinkUiTests` (not analysed, status message, preflight), `MediaRelinkWorkflowTests` (the UI flow) | app 11.6 (Claude), passed |
| 14 | Undo / Redo | Relink, then Ctrl+Z, Ctrl+Y (and the toolbar buttons) | Undo: offline again with the old path and metadata; Redo: online; thumbnail / waveform / Preview follow; dirty follows the save point | `MediaRelinkServiceTests` (exact undo / redo, save point), `MediaRelinkUiTests` (thumbnail after undo, stale work dropped), `MediaRelinkWorkflowTests` (the UI flow) | app 11.6 (Claude), passed |
| 15 | Online media | Select an online asset; a project with no offline media | Relink… disabled for it; without offline media the OFFLINE row is hidden | `MediaRelinkWorkflowTests` (commands) | app 11.6 (Claude), passed |
| 16 | During an export | Start an export; try Relink / Find Missing; let it end or cancel it | Disabled while it runs (`EditingLock`), enabled again afterwards; an export started during a check stops the relink | `MediaRelinkWorkflowTests` (lock before / after awaits, buttons told) | app 11.6 (Claude), passed |
| 17 | Picker start folder | Relink an asset whose old folder still exists; then one whose folder is gone | The picker starts there (else wherever the system starts) and filters the asset's kind | `MediaRelinkWorkflowTests` (start folder, filter) | app 11.6 (Claude), passed |

## Step 11.5 — batch relink (D026 §4, PO-4)

| # | Scenario | Steps | Expected | Automated coverage | Status |
|---|---|---|---|---|---|
| 18 | Batch found | Several missing files moved together into one folder; Relink one of them there | A summary lists the other matches (exact name ignoring case, that folder only) with their warnings; confirming relinks them in one step | `MediaRelinkBatchTests` (exact names, real folder + case, partial batch + summary, one step), `MediaRelinkWorkflowTests` (summary, cancel, partial, offer after a relink) | app 11.6 (Claude), passed |
| 19 | Not found / subfolder | One file in a subfolder of the chosen folder, one renamed | Neither is matched; both stay offline; a second search later finds only what is still offline | `MediaRelinkBatchTests` (no recursion, no similar names, second search), `MediaRelinkWorkflowTests` (summary, cancel, partial, offer after a relink) | app 11.6 (Claude), passed |
| 20 | Unusable match / cancel | A match of the wrong type or too short, a file of another item, two offline items of one name; then Cancel the summary | Each listed as not applicable with its reason (the shared name given to neither); Cancel applies nothing | `MediaRelinkBatchTests` (wrong type, owned file, shared name, single-check parity, not confirmed / cancelled), `MediaRelinkWorkflowTests` (summary, cancel, partial, offer after a relink) | app 11.6 (Claude), passed |
| 21 | Batch undo | Confirm a batch; Undo; Redo | One Undo restores every item of the batch, one Redo relinks them all again (D026 "Refined in Step 11.5") | `MediaRelinkBatchTests` (undo / redo of the batch), `MediaRelinkUiTests` (batch thumbnails, undo), `MediaRelinkWorkflowTests` (summary, cancel, partial, offer after a relink) | app 11.6 (Claude), passed |

## Steps 11.7–11.8 — recent projects (D026 §6, PO-7, PO-8)

Step 11.7 is the core only (no UI): its rules are covered by `RecentProjectsStoreTests`, `RecentProjectsWorkflowTests`
and `RecentProjectsCompositionTests`; in the real app the list can only be seen as the file
`%LOCALAPPDATA%\AiVideoEditor\config\recent-projects.json`. The scenarios below are run in the real app at 11.8.

| # | Scenario | Steps | Expected | Automated coverage | Status |
|---|---|---|---|---|---|
| 22 | Added by Open / Save As / Recover | Open a project; Save As to a new folder; recover a project that has a folder | Each is at the top of `Recent ▾` (next to Open) | `RecentProjectsWorkflowTests` (Open, Save As, first Save, Recover with a folder / gone / never saved), `RecentProjectsUiTests` (next opening) | app 11.8 (Claude), passed |
| 23 | Failed Open | Open a folder without a project / a damaged project | The list is unchanged | `RecentProjectsWorkflowTests` (failed Open ×3, cancelled picker, Cancel, failed Save As / Recover) | app 11.8: only a failed open from the list (35); 11.9 (Claude): PASS through Open |
| 24 | Limit and duplicates | Open 11 projects; open one again with a differently cased path | 10 entries, the newest first; no duplicate | `RecentProjectsStoreTests` (limit, spellings, update), `RecentProjectsWorkflowTests` (Open again) | 11.8: not run; 11.9 (Claude): PASS (the other-case path through `--open-project`, see the log) |
| 25 | Unavailable project | Rename a listed project's folder; open `Recent ▾` | The entry stays, shown as unavailable, and can be removed; choosing it opens nothing and keeps the current project | `RecentProjectsStoreTests` (availability, remove), `RecentProjectsWorkflowTests` (unavailable stay listed), `RecentProjectsUiTests` | app 11.8 (Claude), passed |
| 26 | Unsaved changes | With unsaved changes choose a recent project | Save / Don't Save / Cancel as for Open | `RecentProjectsUiTests` (Cancel, Don't Save) | app 11.8 (Claude), passed |
| 27 | Two instances | Open different projects in two running instances | Both entries are kept | `RecentProjectsStoreTests` (two instances, eight in parallel, held lock) | 11.8: not run; 11.9 (Claude): PASS |
| 28 | Damaged list | Damage the list file; start the app | The app starts; the list is empty, the file kept as `recent-projects.<time>.damaged`; the next Open starts a new list (D026 "Refined in Step 11.7") | `RecentProjectsStoreTests` (damaged contents, bad entries, newer version, unreadable file) | 11.8: not run; 11.9 (Claude): PASS (isolated profile) |
| 29 | During an export | Start an export; after it, open `Recent ▾` | `Recent ▾` is disabled, a click opens nothing; enabled again afterwards | `RecentProjectsUiTests` (editing lock), `RecentProjectsViewBindingTests` | app 11.8 (Claude), passed |

### Step 11.8 — the `Recent ▾` drop-down (D026 "Refined in Step 11.8")

| # | Scenario | Steps | Expected | Automated coverage | Status |
|---|---|---|---|---|---|
| 30 | Placement, empty list | Start without a list; open `Recent ▾` | The button right after Open; "No recent projects yet. …" | `RecentProjectsViewBindingTests`, `RecentProjectsUiTests` (empty) | app 11.8 (Claude), passed |
| 31 | First Save, Save As again | New; Save (→ Save As) to folder A; Save As to folder B | A first, then B first, A second | `RecentProjectsWorkflowTests`, `RecentProjectsUiTests` | app 11.8 (Claude), passed |
| 32 | Up to date at every opening | Open more projects; open `Recent ▾` again | The new entries on top in the store's order | `RecentProjectsUiTests` (every opening reads again, overtaken reading) | app 11.8 (Claude), passed |
| 33 | Open an available project | Choose an available entry | The drop-down closes, the project opens ("Opened project …"), it moves to the top | `RecentProjectsUiTests` (open through the workflow, no second open) | app 11.8 (Claude), passed |
| 34 | Checking / Unavailable | An entry on an unreachable network path; a renamed folder | "Checking…" (greyed, not openable) until the check answers, then "Unavailable"; the renamed one "Unavailable"; clicking either opens nothing; the UI stays responsive | `RecentProjectsUiTests` (states, failing / slow check, UI thread) | app 11.8 (Claude), passed |
| 35 | Open fails | Damage a listed project after it showed as available; choose it | "Couldn't open the project. …", the current project (with its unsaved changes) and the entry stay | `RecentProjectsUiTests` (open failure) | app 11.8 (Claude), passed |
| 36 | Remove | ✕ on an available and on an unavailable entry; then on every entry | Each disappears at once, the project folders are untouched; the empty state after the last | `RecentProjectsUiTests` (remove, failed removal, last entry) | app 11.8 (Claude), passed |
| 37 | Fast opening / closing | Open and close the drop-down repeatedly while a slow check runs | No freeze, no error; the entry ends in its right state | `RecentProjectsUiTests` (one shared check, closing during a check, replaced items) | app 11.8 (Claude), passed |
| 38 | Long and equal names | Projects of one name in two folders; a project with a very long name and folder | One line each, trimmed with "…" (the folder from the start), ✕ fully visible; equal names told apart by the folder | `RecentProjectsViewBindingTests` (trimming, width) | app 11.8 (Claude), passed after a fix (below) |
| 39 | Clear list | — | Not part of 11.8 (D026 "Refined in Step 11.8") | — | n/a |

## Regression (Step 11.9)

| # | Scenario | Steps | Expected | Automated coverage | Status |
|---|---|---|---|---|---|
| R1 | Phase 10 project | Open a project saved by the Phase 10 build | Opens unchanged; Save writes `formatVersion` 3 | `ProjectSerializerRoundTripTests`, `FadeTransitionPersistenceTests` | 11.9 (Claude): PASS |
| R2 | Export manual plan | `docs/EXPORT_MANUAL_TEST_PLAN.md` | As before | `ExportEndToEnd.Tests`, `ExportWorkflowTests` | 11.9 (Claude): PASS (1–8, 10–14; 9 optional, not hit) |

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

### 2026-10-01 / 02 — Step 11.6 manual run (Claude), `145d484`, then the D1–D5 fixes

Debug builds from the session's artifacts folder; separate scratch fixtures under `%TEMP%\aive116` (single relinks,
a dissolve, a long 1080p export, a batch of 21 offline items); every input pinned to the test process (UI Automation;
list rows selected by a real click — the UIA selection of an Avalonia list item doesn't stick; shortcuts sent by
`SendKeys` don't reach the app, so the buttons were used). Without ffprobe: the WinGet Links folder removed from the
instance's PATH and ffmpeg given by `Ffmpeg__FfmpegPath`. Scenario 16b: a test-only ffprobe stand-in delaying each probe
(`Ffmpeg__FfprobePath`, outside the repository) so that the relink's check was still running when the export started.

| # | Result | Observed |
|---|---|---|
| 7 | pass | Picker in the old folder (`single\media`); relinked — new thumbnail, waveform, Inspector path, `*`; the offer (another item offline) — Not Now. |
| 8 | pass | Renamed file: Preview shows both files, clip labels and thumbnail follow; no offer (nothing else offline); the OFFLINE row hides; selection kept. |
| 9 | pass | Save → `project.json` new absolute / relative paths and metadata; close, reopen: online. |
| 10 | pass | Wrong extension, an mp4 without video, a 2 s file for 4 s of clip, a path of another item: each "Can't relink", nothing applied, no `*`, no offer. File removed while the warning was open → "Relink not applied: … doesn't exist any more." |
| 11 | pass | "Picture size: 640×360 before, 1280×720 now"; Cancel → "Relink cancelled.", unchanged; Relink Anyway → applied. |
| 12 | pass | The dissolve-handles warning; Cancel unchanged; Relink Anyway applied, the zone renders with a held frame. |
| 13 | pass after D1 / D4 | Without ffprobe: allowed with the not-checked warning, `Pending`, no metadata saved, export and split refused until analysed; with ffprobe at the next Open analysed. The Inspector said "Analyzing…" (D1) and the dialog "differs from" (D4) — fixed, re-checked below. |
| 14 | pass after D2 | Undo / Redo (also fast ×2) consistent for path, offline state, Preview, `*` and selection; after Undo an offline item showed the relinked file's thumbnail (D2) — fixed, re-checked below. |
| 15 | pass | Relink… disabled for online media; the OFFLINE row hidden without offline media. |
| 16 | pass | Both buttons disabled during an export, enabled after it finished or was cancelled; an export started while the relink's check ran → "An export is running — nothing was relinked.", item still offline; the UI answered in 15–17 ms meanwhile. |
| 17 | pass | Picker in the old folder when it exists, the system default otherwise; the kind filter. |
| 18 | pass | "Found 16 of 21", "Relink 16 Files", one step, "Relinked 16 media files." |
| 19 | pass | `d.wav` in a subfolder and `e.mp4` under "Not in the folder (stay offline)", left offline. |
| 20 | pass | The too-short `c` and both `twin` items under "Can't be used" with the reason; Cancel → "Nothing was relinked."; the long summary scrolls inside a 633 px window. |
| 21 | pass after D2 | One Undo takes all 16 back, Redo relinks them; thumbnails of undone files shown on offline items (D2) — fixed, re-checked below. |

Also: the Open message; the offer → Search (the other `twin` refused as a path already in the project); a second workflow
impossible while a picker is open; no freeze. Defects D1 (Inspector "Analyzing…" for `Pending` / offline), D2 (an
undone relink's thumbnail / waveform on an offline item), D3–D5 (texts) — fixed (D026 "Refined after the Step 11.6
manual run").

Re-check after the fixes (2026-10-02, real app): D1 — offline before relink: row and Inspector "Media offline"; relinked
without ffprobe: both "Not analysed yet"; after Undo both "Media offline". D4 — "Relink without a compatibility check?",
"The technical compatibility of … was not checked", no "differs". D2 — a cache-less project, `pattern.mp4` (video with
sound) and `tone.wav` relinked: thumbnail and waveforms made; Undo, Undo: no thumbnail, no waveform on V1 and A1 (as at
open); Redo, Redo: all back. Batch of 16: after Undo every item shows the placeholder it showed at open, after Redo the
thumbnails again. D5 — a folder with only unusable matches: "Files with matching names were found in the folder, but none
of them can be used."; a folder without matches: "No offline media file was found in the folder." All passed.

### 2026-10-02 — Step 11.8 manual run (Claude), the Step 11.8 code before its commit, on `55606fd` (committed unchanged as `7bf4ed8`), Debug

Debug build of the final Step 11.8 code from the session's artifacts folder; fixtures under `%TEMP%\aive118` (Trip,
`x\Film` and `y\Film`, a project with a 100-character name; each with three media and clips); UI Automation pinned to the
test process; the app's real list `%LOCALAPPDATA%\AiVideoEditor\config\recent-projects.json` (the folder didn't exist
before the run). An unreachable network entry (`\\10.255.255.1\share\Remote`) was put into the list file by hand.

| # | Result | Observed |
|---|---|---|
| 30 | pass | `Recent ▾` after Open; the empty state text; Esc closes it. |
| 22 / 31 | pass | Open Trip → listed; New, Save → Save As "Saved one" → first; Save As "Saved two" → first, then Saved one, Trip; Recover (below) → first. |
| 32 | pass | After opening `x\Film`, `y\Film` and the long one through Open, the next opening showed all six, newest first. |
| 33 | pass | Trip chosen: the drop-down closed, "Opened project "Trip".", Trip first. |
| 34 / 25 | pass | The network entry "Checking…" (greyed, open disabled, ✕ enabled) for 22 s, then "Unavailable"; "Saved one" renamed on disk → "Unavailable"; a click on it opened nothing (status "Ready.", the drop-down stayed open); the window answered meanwhile. |
| 37 | pass | Eight openings / closings in 17 s during the network check: no freeze or error; it ended "Unavailable". |
| 36 | pass | ✕ on `x\Film` (available) and "Saved one" (unavailable): gone at once, gone from the file, both folders untouched. At the end ✕ on every entry → the empty state, `"projects": []`. |
| 26 | pass | Trip edited (`*`), `Saved two` chosen: the question; Cancel → still `Trip*`, the list file byte-identical; Don't Save → `Saved two` opened (Trip's edit not saved); edited, Trip chosen, Save → `Saved two` saved with the edit, Trip opened. |
| 35 | pass | `Trip*`; `y\Film` shown available, its `project.json` damaged, chosen, Don't Save → "Couldn't open the project. The project file is damaged and can't be opened.", still `Trip*`, the entry kept (file restored afterwards). |
| 22 (Recover) | pass | `y\Film` opened from the list, edited, its entry removed with ✕, autosave written, the test process killed; restart → the offer, Recover → `Film*`, `y\Film` first in the list again. |
| 29 | pass | Export of the recovered project: `Recent ▾` disabled 0.25 s after the start, a click opened nothing; after "Export finished" enabled, the drop-down worked. |
| 38 | pass after a fix | Equal names "Film" told apart by their folders; the long name and folder trimmed with "…". First run: the content (460 wide) was wider than the Fluent flyout's maximum (456 with padding), the ✕ column was cut off and a horizontal scroll bar showed — fixed (400, ✕ centred), re-checked. |
| 23, 24, 27, 28 | not run | Automated only (store and workflow tests); 23 checked from the list as 35. |

### 2026-10-05 — Step 11.9 formal run (Claude), `7bf4ed8`, Debug, isolated profile

Debug build of `7bf4ed8` (rebuilt `--no-incremental`); UI Automation pinned to the test processes; the test build always
started with `USERPROFILE` / `LOCALAPPDATA` pointing to a scratch profile (its configuration, recovery files, caches and
logs stayed there; the user's `%LOCALAPPDATA%\AiVideoEditor` was compared with a snapshot afterwards: unchanged).
Fixtures under `%TEMP%\aive119`: projects without media (`MiniProject`), projects with generated media (testsrc2 /
smptebars / sine, a 120 s 1080p source), the scenario projects of the export plan written as files (as at 10.9).

| # | Preconditions and actions | Expected | Actual | Result |
|---|---|---|---|---|
| 23 | Alpha open (listed). Open → a folder without `project.json`; Open → a folder whose `project.json` is cut off | The list unchanged, the current project kept, a message | "Couldn't open the project. project.json was not found in …" / "… The project file is damaged and can't be opened."; still Alpha; the list file byte-identical (hash) both times | PASS |
| 24 | Opened P01…P11 through Open after Alpha (12 projects); then P05 again with the path typed in capitals; then P06 through `--open-project` in capitals | 10 entries, newest first; no duplicate | 10 entries P11…P02 (Alpha, P01 dropped); P05 moved first, still 10, one P05 — the picker returned the canonical spelling (log: "from …\aive119\P05"), so the case part was repeated through `--open-project C:\USERS\…\P06`: the app got the capitals (log), P06 moved first with the new spelling, one P06, 10 entries | PASS (case through `--open-project`, see the note) |
| 27 | Two instances A and B on the same profile. A opens Q1, B opens Q2; both drop-downs opened; A removes P04, then B opens Q3 | Both entries kept; nothing of the other instance lost | After Q1 and Q2: both in the file and in both drop-downs (read again); after A's removal and B's Q3: Q3, Q2, Q1 listed, P04 gone, 10 entries | PASS |
| 28 | All instances closed; `recent-projects.json` replaced by a list cut off mid-entry; app started; `Recent ▾` opened; then Open Alpha | The app starts; empty list; the file kept as `*.damaged`; the next Open starts a new list | Started normally ("Ready."); "No recent projects yet."; the file renamed to `recent-projects.<time>.damaged` with the same content; log "… is damaged (not JSON); the list starts empty" / "… kept as …"; Open Alpha → a new list with Alpha | PASS |
| R1 | A project with media, fades and a scaled clip opened in the Phase 10 build (`2e758f1`, built separately from `git archive`) and saved there with Save As; opened in the current build through Open; Save | Opens unchanged; Save writes `formatVersion` 3 | Opened clean (no `*`), 3 media online, V1 / V2 / A1 clips; the file not touched by Open; Save → `formatVersion` 3, byte-identical to the Phase 10 file | PASS |
| R2 | `docs/EXPORT_MANUAL_TEST_PLAN.md` | As in the plan | 1–8, 10–14 as in the plan (details in its result log); 9 not hit; 14 by decoding and stream timing only; the "without ffmpeg" extra not run | PASS (9 NOT RUN — optional) |
| Reg-relink | A project whose three media were moved to `moved\`: Relink… `pattern.mp4` → its moved file, Not Now; Undo; Redo. Find Missing… → `moved\` → Relink 2 Files; one Undo; Redo | As at 11.6 (with the D1–D5 fixes) | Relinked (the picker started on the desktop: the old folder is gone); the offer "2 other media files are offline …" (Search / Not Now); Undo → offline with the placeholder (no thumbnail of the relinked file, D2), Redo → thumbnail; "Found 2 of 2 …", "Relinked 2 media files.", one Undo → both offline, Redo → online | PASS |
| Reg-recheck | `tone.wav` renamed while the window was inactive, window activated; renamed back, activated again | Offline, then online, without reopening | ""tone.wav" is missing now and is shown as offline." → "Media offline"; ""tone.wav" is available again." → online | PASS |
| Reg-recent | Listed project folder renamed; `Recent ▾`: its entry, ✕; with unsaved changes choose E1 → Cancel; again → Don't Save | Unavailable entry removable; Cancel keeps project and list; Don't Save opens | "Unavailable", open disabled, ✕ → gone (folder untouched); Cancel → still `RL*`, list file byte-identical; Don't Save → E1 opened, first in the list | PASS |
| Reg-export-lock | During an export (export plan 13) | `Recent ▾` disabled | Main window disabled, `Recent ▾` disabled, a real click on it opened nothing; enabled afterwards | PASS |

Note on 24: through Open a folder always reaches the app in its canonical spelling (the Windows picker normalises it);
spellings that differ only in case come from recovery files, the Debug `--open-project` argument or a list written on
another machine — D026 "Refined in Step 11.9".

CI: not run at 11.9 — the branch was not published then (the product owner's decision for 11.9); afterwards green on
PR #11, merged into `main` as `47ed2fa` (2026-10-05; recorded at Step 13.2).
