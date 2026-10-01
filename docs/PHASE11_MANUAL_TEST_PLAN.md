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
that step (a development check, not the formal run); **PO** — run by the product owner.

## Step 11.3 — media availability re-check (D026 §2, PO-5)

| # | Scenario | Steps | Expected | Automated coverage | Status |
|---|---|---|---|---|---|
| 1 | File removed during the session | Open a project with online media on the timeline; move one file away; switch to another window and back | The asset becomes offline: "Media offline" in the Media Browser, the Preview's placeholder; the project stays clean (no `*`) | — | planned |
| 2 | File comes back | After 1, move the file back; activate the window | The asset is online again without reopening: picture in the Preview, thumbnail, waveform; clean | — | planned |
| 3 | Missing at Open, back later | Open a project with a file missing; restore the file; activate the window | Online; an asset without saved metadata is analysed; thumbnail / waveform appear | — | planned |
| 4 | Export right after a removal | Remove a used file and start Export at once (within the throttle interval) | The preflight reports the media as offline; nothing is exported | — | planned |
| 5 | Slow or disconnected drive | Media on a removable / mapped drive; disconnect it; activate the window, play, edit | The UI stays responsive while the check waits; the asset becomes offline | — | planned |
| 6 | Throttle | Switch windows repeatedly | At most one check per interval (log); no visible stall | — | planned |

## Steps 11.4–11.6 — relink (D026 §3–§5, PO-1…PO-3, PO-6, PO-9)

| # | Scenario | Steps | Expected | Automated coverage | Status |
|---|---|---|---|---|---|
| 7 | Moved file | A used video moved to another folder; Relink it to the new place | Online; clips unchanged (position, source range, speed, fades, dissolves); Preview and export use it; the project is dirty | — | planned |
| 8 | Renamed file | A used file renamed; Relink to the new name | As 7 | — | planned |
| 9 | Save and reopen | After 7, Save, close, reopen | The new path is used; not missing | — | planned |
| 10 | Hard rejects | Relink to: a file of another media type; a shorter video than a clip's source range; a file already used by another asset; a path that doesn't exist | Each refused with its own message; nothing changes | — | planned |
| 11 | Warnings | Relink to a file with a different resolution / frame rate / without audio | A warning lists the differences; Cancel changes nothing, confirming relinks | — | planned |
| 12 | Short dissolve handles | Relink a clip of a dissolve to a file with less media beyond the clip | A warning; after confirming, the zone renders with held frames | — | planned |
| 13 | Without ffprobe | Make ffprobe unavailable (configuration / PATH); Relink | Allowed; told that compatibility couldn't be checked; the asset is pending; analysed once ffprobe is available | — | planned |
| 14 | Undo / Redo | Relink, then Ctrl+Z, Ctrl+Y (and the toolbar buttons) | Undo: offline again with the old path and metadata; Redo: online; thumbnail / waveform / Preview follow; dirty follows the save point | — | planned |
| 15 | Online media | Select an online asset | Relink is not offered | — | planned |
| 16 | During an export | Start an export; try Relink | Disabled (`EditingLock`) | — | planned |
| 17 | Picker start folder | Relink an asset whose old folder still exists | The picker starts there and filters the asset's kind | — | planned |

## Step 11.5 — batch relink (D026 §4, PO-4)

| # | Scenario | Steps | Expected | Automated coverage | Status |
|---|---|---|---|---|---|
| 18 | Batch found | Several missing files moved together into one folder; Relink one of them there | A summary lists the other matches (exact name, that folder only); confirming relinks them | — | planned |
| 19 | Not found / subfolder | One file in a subfolder of the chosen folder, one renamed | Neither is matched; both stay offline | — | planned |
| 20 | Unusable match / cancel | A match of the wrong type or too short; then Cancel the summary | The match is listed as not applicable with the reason; Cancel applies nothing from the batch | — | planned |
| 21 | Batch undo | Confirm a batch; Undo | Restored as decided at the start of 11.5 (undo granularity) | — | planned |

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
