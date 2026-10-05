# Project Progress

## Current phase

Phase 12 — Editing essentials: **complete locally** — Step 12.9 (final verification & closeout) done on 2026-10-05 on
branch `feat/phase-12-editing-essentials` (from `47ed2fa`, `main` after the merge of PR #11). Open: CI not run yet — the
branch is not published (push / pull request only with the product owner's permission); L1-c stays open; a separate
test cleanup (`F(end − start)` helpers of 12.3 / 12.5). Scope, steps and acceptance criteria: `docs/DEVELOPMENT_PLAN.md`
"Phase 12 — Editing essentials: steps"; decision D027 (product owner decisions of 2026-10-05). Steps 12.1–12.9 done.

### Phase 12 — Editing essentials (complete locally)

Steps (D027; each accepted by the product owner before the next, never started automatically): 12.1 audit · 12.2 sync
after the merge & scope formalization · 12.3 tracks · 12.4 media removal · 12.5 ripple delete / close gap · 12.6 copy /
paste / duplicate · 12.7 markers · 12.8 New during an import · 12.9 closeout. Working rules from the product owner (kept
from Phase 11): no build or test run without a separate command; no push, pull request or merge without direct
permission; no next step without the product owner's go.

- Step 12.1 done and accepted (2026-10-05) — audit, no change (no `git fetch`).
  - Git: the local `origin/main` = `47ed2fa` "Merge pull request #11 …", its tree identical to `f30885d` (Phase 11
    accepted); the local `main` was at `2e758f1`, 12 commits behind, none ahead; working tree clean. CI of PR #11 green
    (the product owner; not checked from here).
  - Build and tests (with `--artifacts-path` in the session's scratch folder, the product owner's audit request):
    `dotnet build AiVideoEditor.sln --no-incremental -warnaserror` 0 errors / 0 warnings; `dotnet test` (once) 2213
    passed, 2 skipped (the 4K heavy scenes), 0 failed — Core 448, Timeline 361, Project 371, UI 496, Export 99,
    Rendering 58, Video 294, ExportEndToEnd 86 + 2.
  - Code: no TODO / FIXME / HACK in `src/` or `tests/`; `src/Effects` is an empty placeholder (`Clip.Effects` only
    persisted); `Sequence.Markers` is persisted in v3 (`MarkerDto`) but never created or shown; no track removal or
    reordering, no media removal, no ripple, no clipboard (`ITimelineEditService`, `IProjectService`).
  - Documentation outdated by the merge: "CI pending — the branch is not published" (ROADMAP, README,
    DEVELOPMENT_PLAN, this file), this file's "Last known state" (2026-10-01) and "Completed" (no Phase 11), the
    ARCHITECTURE verification note (no 11.7–11.9).
  - Candidates reported: editing essentials (chosen), export settings, colour / HDR, AI features, distribution,
    technical debt.
- Product owner decisions (2026-10-05), recorded in D027: Phase 12 = 12.2–12.8 as above; ripple delete only (no ripple
  trim), clips right of the removed span on the same track move left, no overlap, fully undoable, dissolves kept valid
  by D025's rules with the exact rule fixed in D027 before the code; media removal with a confirmation for used
  assets, atomic and undoable, caches consistent, files on disk untouched; track delete (confirmation with clips) and
  reorder, undoable, the layer order the same in the Preview and the export; copy / paste / duplicate with all clip
  properties and fades, no dissolve, paste at the playhead with the distances kept, on the source tracks when
  possible, rejected rather than overlapping, hotkeys optional; markers: add, remove, show, next / previous, snapping
  optional, no new `formatVersion` unless needed; the import bound to its project, the solution recorded in D027; L1-c
  stays open; AI, export settings, HDR / colour, installer, timeline virtualization and an undoable import out.
- Step 12.2 done (2026-10-05) — sync after the merge and scope formalization, documentation only (no production code,
  no test changed, no build or test run). `git checkout main` + `git merge --ff-only origin/main` (`2e758f1` →
  `47ed2fa`, no fetch), branch `feat/phase-12-editing-essentials` created from it.
  - DECISIONS: D027 (context from the audit; §1 scope and constraints, §2 ripple delete with the dissolve rule, §3
    tracks, §4 media removal, §5 copy / paste / duplicate, §6 markers, §7 New during an import; the sub-decisions left
    to each step's start).
  - The dissolve rule for ripple (D027 §2) follows from D025 without a new choice: a dissolve of a removed clip is
    removed (D025 §5 "Delete"); every other dissolve has both clips moved by the same distance (they meet, so nothing
    removed lies between them) and keeps its length, zone and handles (D025 §5 "Move"); no dissolve is created where
    clips meet only because of the ripple. Two alternatives were considered and excluded because they contradict D025
    (an automatic dissolve across the removed clip; refusing the ripple of a clip with a dissolve).
  - The import fix (D027 §7): the workflow keeps the project it started in and adds nothing when another project is
    current after an await (the identity pattern of D026); a cancellation token and blocking New / Open were considered
    and not taken.
  - `docs/DEVELOPMENT_PLAN.md`: the Phase 11 line (PR #11 / `47ed2fa`, CI green), the Phase 12 line and the section
    "Phase 12 — Editing essentials: steps" (gates, constraints, steps 12.1–12.9 with PR / QG / Impl items).
  - ROADMAP (Current: Phase 12; Phase 11 merged; future phases), README (status), ARCHITECTURE (verification note),
    `docs/README.md`, this file (this section, the Phase 11 heading, "Last known state", "Completed", "Known issues").
  - `docs/PHASE12_MANUAL_TEST_PLAN.md` — skeleton: scenarios per step, status "planned".
  - Interpretation to confirm: "ripple delete of a clip / range" is read as the selected clips (one or several, on one
    or several tracks) plus close gap (an empty span of one track); a time range cutting through clips would need an
    in / out selection, which does not exist and is out of scope.
- Step 12.2 accepted by the product owner (2026-10-05), with the ripple interpretation, the dissolve rule (D027 §2) and
  the import fix (D027 §7) confirmed — recorded in D027 "Confirmed at the Step 12.2 acceptance". Committed as
  `8e2b109`.
- Step 12.3 done and accepted (2026-10-05) — tracks: delete and reorder (D027 §3; the product owner's rules at its
  start and the implementation in D027 "Refined at the start of Step 12.3"; the four open points of the report — at
  least one track of either kind, no move past a locked neighbour, equal orders numbered anew, the 84 px header — and
  the manual check of the header bindings / the E2E scene through `SetTrackOrderCommand` confirmed by the product
  owner).
  - Core: `ITimelineEditService.DeleteTrack`, `GetDeleteTrackBlockReason`, `MoveTrack(trackId, ±1)`.
  - Timeline: `RemoveTrackCommand` (the same track object back at its index on Undo), `SetTrackOrderCommand` /
    `TrackOrderChange` (absolute orders); `TimelineEditService` — refuses a locked track, the last track of the
    timeline, a move past a locked neighbour; no neighbour = no change; equal orders numbered anew.
  - UI: `TimelineTrackViewModel.HasTrackAbove` / `HasTrackBelow`; `TimelineViewModel` — `MoveTrackUp/Down` (video ▲ =
    +1, audio ▲ = −1), `DeleteTrack` (asks through `IDialogService` when the track has clips; skips the question when
    the service would refuse; ignores the answer when an export started meanwhile), the video rows listed by the
    snapshot's rule for equal orders; `TimelineView.axaml` — ▲ / ▼ / ✕ in the track header, the column 84 px. DI: the
    optional `IDialogService` of `TimelineViewModel` comes from the container.
  - Tests (new): `Timeline.Tests/TrackEditTests` (13: empty track deleted and back at its place; a track
    with clips, a fade and a dissolve deleted and restored exactly; locked not deleted; the last track kept; no audio
    track → audio refused, a new one accepted; unknown track; a move swaps only the orders and the snapshot composites by
    them, undo / redo; audio among audio only; no neighbour = no change; locked / past locked refused; equal orders
    numbered anew, undo; a dissolve stays with its moved track; delete + move survive save / reopen in v3);
    `UI.Tests/TimelineTrackUiTests` (10: arrows only towards the same kind; up / down for video and audio and the rows
    after Undo; a refused move's message; an empty track without a question; a track with clips after Delete Track only
    (Cancel, closed); a selected clip on the deleted track leaves the selection; no question for a refused deletion; the
    last track kept; an export started during the question; disabled during an export);
    `ExportEndToEnd.Tests/ExportTrackOrderEndToEndTests` (1: two solids, the order swapped — the export shows the other
    colour on top and the Preview draws the same bytes).
  - Not automated: the header buttons' bindings in the real view (the UI test project has no headless platform to
    realize item templates) — manual scenarios 1–7 (run in the real app at 12.9 at the latest).
  - Verification (on the product owner's command; built with `--artifacts-path` in the session's scratch folder): the
    first build failed (CS0535) — two other implementations of `ITimelineEditService` lacked the new members, the
    design-time stub `MainWindow.DesignTimeTimelineEditService` and the test stub
    `MediaOrientationRefreshTests.TimelineEditServiceStub`; both completed (design time: "Design time."; the stub:
    `No` / null), nothing else changed. Then `dotnet build AiVideoEditor.sln --no-incremental -warnaserror` 0 errors /
    0 warnings; `dotnet test` (whole solution, once) 2237 passed, 2 skipped (the 4K heavy scenes), 0 failed — Core 448,
    Timeline 374 (+13), Project 371, UI 506 (+10), Export 99, Rendering 58, Video 294, ExportEndToEnd 87 (+1) + 2; then
    three runs with `--blame-hang --blame-hang-timeout 5m`, each 2237 passed / 2 skipped / 0 failed, no hang, no dump.
    Parity suites unchanged. Not run: the heavy 4K scenes, CI (not pushed), the manual scenarios 1–7.
  - Committed as `5bcdcbb` (the product owner's permission, 2026-10-05; not pushed).
- Step 12.4 done and accepted (2026-10-05) — removing media from the project (D027 §4; the implementation and the
  points made precise in D027 "Refined in Step 12.4", all confirmed by the product owner — the analysis rule written
  into §4 before the checks, no code change for it). Started on the product owner's instruction right after the 12.3
  commit.
  - Audit before the code: assets are referenced by clips through `MediaBackedClip.MediaAssetId` only; the media list is
    `Project.MediaAssets` (import adds through `IProjectService.AddMediaAssets`, the only duplicate check, not undoable);
    `MediaCacheCoordinator` keeps results per asset id and generation (requests on `MediaAssetsChanged`, once per
    generation); `MediaAnalysisCoordinator` writes its result into the asset object it started for; confirmations go
    through `IDialogService` (the track deletion of 12.3, the relink workflow); a plain clip delete removes dissolves by
    `EditPlan.ReconcileTransitions`.
  - Core: `ITimelineEditService.CountClipsUsing`, `GetRemoveMediaBlockReason`, `RemoveMedia`.
  - Timeline: `RemoveMediaAssetCommand` (the same object back at its index on Undo, `NotifyMediaAssetsChanged` both
    ways); `TimelineEditService.RemoveMedia` — the clips of every track through an `EditPlan` with their dissolves, then
    the asset, one `CompositeCommand` (`NotifyingCommand` for `TimelineChanged` only when clips go); refused when the asset
    is not in the project or a clip of it is on a locked track.
  - UI: `MediaBrowserViewModel.RemoveCommand` (optional `ITimelineEditService`, `IDialogService`, `StatusService` from the
    container) — the service's block reason without a question, "Remove Media" / "Remove" / "Cancel" when clips use the
    asset, the answer ignored when an export or a relink started meanwhile, status messages; `MediaBrowserView.axaml` —
    ✕ on the selected row. The coordinators needed no change (their results are kept per asset id). The design-time
    stub and the `MediaOrientationRefreshTests` stub of `ITimelineEditService` completed.
  - Tests (new, 18): `Timeline.Tests/MediaRemovalTests` (9: an unused asset in one step, back at its place,
    no timeline change, clean after Undo; a used asset with three clips on two tracks, a dissolve and a fade, another
    asset's clip kept, the snapshot without it, Undo exact; metadata / analysis state / size back; offline asset; the
    file on disk untouched; a locked track blocks, its count still made; a locked track without its clips doesn't; an
    asset no longer in the project; save / reopen in v3 without it); `UI.Tests/MediaRemovalUiTests` (9: unused without a
    question, the selection and the Inspector cleared, Undo brings the row back; used only after Remove (Cancel, closed),
    the question's text, Undo; refused without a question; an export started during the question; selection needed and
    disabled during an export; no Remove without the edit service; the thumbnail shown again after Undo and made once;
    a thumbnail still being made ends and is there after Undo; an analysis still running completes into the removed
    asset, which comes back analysed).
  - Not automated: the ✕ button's bindings in the real view (no headless platform, as in 12.3) — manual scenarios 8–13.
  - Verification (on the product owner's command; `--artifacts-path` in the session's scratch folder): `dotnet build
    AiVideoEditor.sln --no-incremental -warnaserror` 0 errors / 0 warnings at the first attempt (both other
    implementations of `ITimelineEditService` were completed with the change); `dotnet test` (whole solution, once)
    2255 passed, 2 skipped (the 4K heavy scenes), 0 failed — Core 448, Timeline 383 (+9), Project 371, UI 515 (+9),
    Export 99, Rendering 58, Video 294, ExportEndToEnd 87 + 2; three runs with `--blame-hang --blame-hang-timeout 5m`,
    each 2255 passed / 2 skipped / 0 failed, no hang, no dump; `git diff --check` clean. The Preview ↔ Export parity
    suites unchanged and green. No fix needed. Not run: the heavy 4K scenes, CI (not pushed), the manual scenarios 8–13.
  - Committed as `097e555` (the product owner's permission, 2026-10-05; not pushed); D027 §4 and this plan's 12.4 text
    made to say what the implementation does for running work (before the checks).
- Step 12.5 done and accepted (2026-10-05) — ripple delete and close gap (D027 §2; the product owner's rules at its
  start and the implementation in D027 "Refined in Step 12.5", confirmed: Close Gap through the selected clip, a ripple
  refused when a moved dissolve's handles no longer fit, no hotkeys). Started on the product owner's instruction right
  after the 12.4 commit.
  - Core: `ITimelineEditService.RippleDeleteClips`, `CloseGap(trackId, at)`, `CloseGapBefore(clipId)`.
  - Timeline: `TimelineEditService` — the move's per-clip timing rule extracted into `PlanShift` (Move unchanged in
    behaviour, now its user); ripple: per track, the removed clips into the `EditPlan` and every other clip shifted
    left by the removed frames before it; close gap: the span containing the point, every later clip of the track
    shifted by its length; both through `Validate` (`ReconcileTransitions` removes a removed clip's dissolves, keeps
    the others with their two clips, validates zones / handles) and one `Commit`. No new command class: the plan's
    `RemoveClipCommand` / `UpdateClipsCommand` / transition commands in one `CompositeCommand`.
  - UI: `TimelineViewModel.RippleDeleteCommand` / `CloseGapCommand` (refreshed with the selection like Dissolve);
    `TimelineView.axaml` — "Ripple Delete" and "Close Gap" next to Delete. The design-time stub and the
    `MediaOrientationRefreshTests` stub completed.
  - Tests (new, 24): `Timeline.Tests/RippleEditTests` (17: one clip, undo / redo exact; gaps kept; several
    clips of one track; two tracks with the third, the playhead and a marker untouched; a removed clip's dissolve
    removed with the note, none created at the new cut; a dissolve after the removed clip moved with anchors, length
    and fades kept and drawn at the new cut by the snapshot; a dissolve before it untouched; a 2× clip keeps speed and
    source range; 29.97 fps stays on the grid; locked track refused; nothing selected / a missing clip; close gap
    between clips (a later gap kept, undo / redo), before the first clip, only an existing gap (in a clip, after the
    last, an empty track, an unknown track), locked refused, a dissolve moved with its clips and another track
    untouched, the gap right before a clip); `UI.Tests/TimelineRippleUiTests` (6: Ripple Delete needs a selection and
    is off during an export; two tracks closed, the selection cleared, one Undo; a refused one's message, the selection
    kept; Close Gap needs exactly one clip and is off during an export; it closes the gap before the clip, the
    selection kept; no gap → the service's message).
  - The QG "an export after a ripple matching its Preview" (the product owner asked for it before the checks):
    `ExportEndToEnd.Tests/ExportRippleEndToEndTests` (1) — red [0, 10), green [10, 20), blue [20, 30) on V1, the green
    one removed by the real `TimelineEditService.RippleDeleteClips` (through a minimal `IProjectService` stand-in over
    the scene's project, as in `Video.Tests`); the real export has 20 frames, red then blue without a gap, and the
    Preview from the export's snapshot draws the same bytes at frames 9 and 10 (D023's canvas rule; no new criterion).
    It ran (not skipped) and passed. The existing parity scenes unchanged.
  - Not automated: the two header buttons' bindings in the real view — manual scenarios 14–21.
  - Verification (on the product owner's command; `--artifacts-path` in the session's scratch folder): `dotnet build
    AiVideoEditor.sln --no-incremental -warnaserror` 0 errors / 0 warnings at the first attempt; `dotnet test` (whole
    solution, once) 2279 passed, 2 skipped (only the two 4K heavy scenes), 0 failed — Core 448, Timeline 400 (+17),
    Project 371, UI 521 (+6), Export 99, Rendering 58, Video 294, ExportEndToEnd 88 (+1) + 2; three runs with
    `--blame-hang --blame-hang-timeout 5m`, each 2279 passed / 2 skipped / 0 failed over all 8 test assemblies, no hang,
    no dump; `git diff --check` clean. The Preview ↔ Export parity suites unchanged and green. No fix needed. Not run:
    the heavy 4K scenes, CI (not pushed), the manual scenarios 14–21.
  - Committed as `a2f8c7c` (the product owner's permission, 2026-10-05; not pushed).
- Step 12.6 done and accepted (2026-10-05) — copy / paste / duplicate (D027 §5; the rules chosen and the edge cases in
  D027 "Refined in Step 12.6", all confirmed by the product owner: copy from a locked track, offline media pasted, media
  removed after the copy blocks the paste until its Undo, another frame rate needs a new copy, one problem rejects the
  whole paste / duplicate, the clipboard only for the current project, no system clipboard, nothing saved). Started on
  the product owner's instruction right after the 12.5 commit.
  - Audit before the code: clips refer to media only through `MediaBackedClip.MediaAssetId`; `TimelineEditService`
    already clones a clip with every property for Split (`CloneClip`: a new id, the same asset); new clips go into an
    `EditPlan` (`Insert`) and are validated (no overlap) and committed as one `IUndoableCommand`, as Add does; the
    selection lives in `TimelineViewModel` (`SelectAdded` selects new clips); `ShortcutRouter` is a key → command table
    with the text-input guard.
  - Core: `ITimelineEditService.CopyClips` (→ `TimelineClipboard` / `TimelineClipboardEntry`), `PasteClips`,
    `DuplicateClips`.
  - Timeline: `TimelineEditService` — `ShiftedState` (the move's timing rule, out of `PlanShift`), `CopyClips`
    (`CloneClip` per clip, its track, the rate), `PasteInto` (rate, track, lock and media checks, `CloneClip` + the
    shifted state, `Insert`, `Validate`, `Commit`) for Paste and Duplicate. No new command class.
  - UI: `TimelineViewModel.Clipboard` (emptied on another project), `CopyCommand` / `PasteCommand` /
    `DuplicateCommand` (status messages, the new clips selected); `TimelineView.axaml` — Paste and Duplicate in the
    header (Copy by Ctrl+C only, after the 1024 px check below); `ShortcutRouter` — Ctrl+C / Ctrl+V / Ctrl+D. The design-time stub and the `MediaOrientationRefreshTests`
    stub completed.
  - Tests (new, 25): `Timeline.Tests/ClipboardEditTests` (16: copy is no project change, nothing / a missing
    clip copies nothing; a video clip at 2× with transform, crop, volume, fades pasted with a new id, the same asset
    and source range; a text clip's text and style; clips of two tracks keep tracks and distances, one Undo; a copy is
    detached from later edits; no dissolve copied; an overlap rejects the whole paste; a locked target rejects, copying
    from it is allowed; a deleted track rejects; media removed after the copy rejects, its Undo lets the paste work;
    offline media pasted, in the snapshot; another frame rate rejects; the paste snapped to the grid and in the
    snapshot; pasted clips survive save / reopen in v3; duplicate after the selection on two tracks, one Undo;
    duplicate rejected for an overlap, a locked track, a missing clip, nothing); `UI.Tests/TimelineClipboardUiTests`
    (5: what each command needs, Copy allowed during an export; Paste at the playhead selecting the copies, a second
    one at the same place refused; Duplicate after the selection, selected, Undo; a refused Duplicate's message and the
    selection kept; another project empties the clipboard). Changed: `UI.Tests/ShortcutRoutingTests` (the table +3
    rows — also checked against a focused text box — and C / V / D without Ctrl, Ctrl+Shift+C not shortcuts).
  - Not automated: Ctrl+C / Ctrl+V / Ctrl+D and the copy / paste flows in the running app — manual scenarios 22–28.
  - Verification (on the product owner's command; `--artifacts-path` in the session's scratch folder): `dotnet build
    AiVideoEditor.sln --no-incremental -warnaserror` 0 errors / 0 warnings. The first full run had 1 failure —
    `ClipboardEditTests.Clips_of_several_tracks_keep_their_tracks_and_distances_from_the_earliest`, "Clip edges must lie
    on the project frame grid": the test helper gave clips the length `F(end − start)`, at 30 fps a tick off
    `F(end) − F(start)`, so a clip's end was off the grid and the validator rightly refused the paste onto its track.
    Fixed in the two 12.6 test helpers (`ClipboardEditTests`, `TimelineClipboardUiTests`: `F(end) − F(start)`), no
    production change (the same helper in the committed 12.3 / 12.5 tests left alone — a separate cleanup, product
    owner). Then: `dotnet test` 2304 passed, 2 skipped (only the two 4K heavy scenes), 0 failed — Core 448, Timeline
    416 (+16), Project 371, UI 530 (+9: 5 + 4 new `ShortcutRoutingTests` cases; the 3 new table rows checked by the
    existing facts), Export 99, Rendering 58, Video 294, ExportEndToEnd 88 + 2; three runs with `--blame-hang
    --blame-hang-timeout 5m`, each 2304 / 0 / 2 over all 8 test assemblies, no hang, no dump; `git diff --check` clean.
    The Preview ↔ Export parity suites unchanged and green; the pasted clips are asserted in the playback snapshot.
  - Real-app UI check (the product owner's permission; the Debug build from the scratch artifacts, an isolated profile
    — `USERPROFILE` / `LOCALAPPDATA` in the scratch folder —, the Phase 10 fade fixture project with V2 / V1 / A1 and
    media, 125 % display scaling): at 1440 × 900 everything fits with room to spare. At the minimum width (1024 px,
    reached by dragging the window border) the timeline header overflowed: Fit cut at the right edge and "+ Video
    Track" right against "25 FPS". As the product owner had decided for that case, only the Copy button was removed
    (Ctrl+C stays). Rebuilt (0 / 0) and checked again at 1024 px: the whole header fits — Fit complete with its margin,
    a gap after "25 FPS"; −, + and Fit work (the ruler went to 5 s, then 2 s steps, Fit showed 0:00–0:34); the 84 px
    track headers show ▲ ▼ ✕ whole; the Media Browser's ✕ on the selected row (260 px panel) is whole. Full suite again
    after the change: 2304 / 0 / 2. Not checked: the minimum height (640) — setting the window size from outside
    (SetWindowPos) left the window taller than requested (an interplay of Avalonia's size limits with the scripted
    resize on this multi-monitor, mixed-DPI desktop; dragging works), and dragging the bottom border to its minimum
    did not take; the header row does not depend on the height.
  - Committed as `518730c` (the product owner's permission, 2026-10-05; not pushed).
- Step 12.7 done and accepted (2026-10-05) — markers (D027 §6; the rules in D027 "Refined in Step 12.7", confirmed by
  the product owner: one marker per frame, remove only on the playhead's frame, markers keep their time when the rate
  changes, no edit moves them, no label / colour editing, drag, click or hotkeys, the buttons a compact block in the
  corner left of the ruler). Started on the product owner's instruction right after the 12.6 commit.
  - Audit before the code: `Sequence.Markers` (`Marker`: id, position, label, colour) is read and written by
    `project.json` v3 and used nowhere else; the snap targets are one list in `TimelineEditService.Snap`; the ruler is
    an ItemsControl on a Canvas laid out in `TimelineViewModel.Relayout` (also after every timeline change and zoom).
  - Core: `ITimelineEditService.AddMarker`, `RemoveMarkerAt`, `NextMarker`, `PreviousMarker`;
    `TimelineEditResult.MarkerId`; the markers added to the snap targets.
  - Timeline: `AddMarkerCommand` (sorted insert), `RemoveMarkerCommand` (index kept), through `NotifyingCommand` so the
    panel refreshes; `TimelineEditService` — one marker per frame of the current grid, the playhead's frame for remove,
    strictly after / before for the queries.
  - UI: `TimelineMarkerViewModel` (left, colour); `TimelineViewModel.Markers` (rebuilt in `Relayout`),
    `AddMarkerCommand` / `RemoveMarkerCommand` (disabled during an export) and `PreviousMarkerCommand` /
    `NextMarkerCommand` (through `SetPlayhead`, so the Preview seeks); `TimelineView.axaml` — the marker buttons in the
    corner left of the ruler, the markers on the ruler. The design-time stub and the `MediaOrientationRefreshTests`
    stub completed.
  - Tests (new, 15): `Timeline.Tests/MarkerEditTests` (9: added on the grid in one step with the default look,
    undo / redo the same marker, clean after Undo; sorted, one per frame; before zero → zero; removed and back in its
    place; nothing to remove refused; next / previous strictly after / before, none at the ends; markers are snap
    targets; markers change no clip and don't lengthen the timeline; saved and reopened in v3);
    `UI.Tests/TimelineMarkerUiTests` (6: drawn at the zoom, laid out again after a zoom, Undo; the same frame refused;
    remove at the playhead or the message; previous / next with seeks and the messages at the ends; add / remove off
    during an export, going to a marker not; a project's markers shown, another project has its own).
  - Not automated: the corner buttons and the drawing in the real view — manual scenarios 29–32.
  - Verification (on the product owner's command; `--artifacts-path` in the session's scratch folder): `dotnet build
    AiVideoEditor.sln --no-incremental -warnaserror` 0 / 0 at the first attempt; `dotnet test` 2319 passed, 2 skipped
    (only the two 4K heavy scenes), 0 failed — Core 448, Timeline 425 (+9), Project 371, UI 536 (+6), Export 99,
    Rendering 58, Video 294, ExportEndToEnd 88 + 2; three runs with `--blame-hang --blame-hang-timeout 5m`, each 2319 /
    0 / 2 over all 8 test assemblies, no hang, no dump; `git diff --check` clean.
  - Real-app UI check (the product owner's permission; the Debug build from the scratch artifacts, an isolated profile,
    the Phase 10 fade fixture project, 125 % display scaling; the window narrowed by dragging its border): the four
    buttons worked (two markers added and drawn, ▶ / ◀ to them with the end messages, ◆− removed one; zoom and Fit kept
    the markers at their time), but the block was wider than the 84 px column at 1440 and 1024 px alike — ▶ partly
    under the ruler, ◀ against the left edge. The product owner chose variant A: in the block's button style Padding
    3,0 → 1,0 and Spacing 2 → 1, nothing else. Then again: build 0 / 0, `dotnet test` 2319 / 0 / 2, three
    `--blame-hang` runs 2319 / 0 / 2 (no hang, no dump), `git diff --check` clean; in the real app at 1440 and 1024 px
    all four buttons whole, ▶ clear of the ruler, nothing over the ruler, TIMELINE or the track headers; all four
    clicked (◆+ added, ▶ / ◀ moved the playhead with the end messages, ◆− removed the marker on the playhead); zoom + and
    Fit moved neither the block nor a marker off its time. The test edits undone (title without `*`) before closing.
  - Committed as `5a7edae` (the product owner's permission, 2026-10-05; not pushed).
- Step 12.8 done and accepted (2026-10-05) — New during a running import (D027 §7, confirmed again by the product owner
  at the step; D027 "Refined in Step 12.8"). Started on the product owner's instruction right after the 12.7 commit.
  - Audit before the code: `MediaImportWorkflow.RunAsync` awaits the picker, then a dispatcher yield (`showStatus`,
    Step 9.8) and `IMediaImportService.ImportManyAsync` (synchronous in the app), then calls
    `IProjectService.AddMediaAssets` on whatever project is current and queues the analysis — a New / Open / Recover
    during one of the awaits got the picked files (the known issue since Step 9.3). The relink workflow and service
    already guard with the identity of the project they started in (D026).
  - UI: `MediaImportWorkflow` — the project taken before the picker, `ProjectChanged(project)` after each of the three
    awaits (status message `ProjectChangedMessage`, a log line, return before anything is added or analysed). No other
    file changed.
  - Tests (new, 5): `UI.Tests/ImportProjectChangeTests` (5: New while the files are checked — nothing in the
    new project nor the old, still clean, no analysis, the message; New while "Importing N files…" is shown; another
    project while the picker is open — the files not even checked; without a change as before — both added and
    analysed; the next import into the new project works). The existing `ImportStatusTests` (status order, cancelled
    picker, failing check) unchanged.
  - Verification (on the product owner's command; `--artifacts-path` in the session's scratch folder): `dotnet build
    AiVideoEditor.sln --no-incremental -warnaserror` 0 / 0 at the first attempt; `dotnet test` 2324 passed, 2 skipped
    (only the two 4K heavy scenes), 0 failed — Core 448, Timeline 425, Project 371, UI 541 (+5), Export 99, Rendering 58,
    Video 294, ExportEndToEnd 88 + 2; three runs with `--blame-hang --blame-hang-timeout 5m`, each 2324 / 0 / 2 over all
    8 test assemblies, no hang, no dump; `git diff --check` clean.
  - Real-app attempt at the race (manual scenarios 33–34; the Debug build, an isolated profile, a new empty project,
    the four fixture media files picked in the Windows file dialog by a script — click into the name box, paste,
    Enter — and New clicked right after the Enter, with 0 to 250 ms between them; 9 attempts): the race was never hit.
    In the running app the check of the picked files is synchronous on the UI thread, so a New can only come in during
    the one-frame yield between the picker closing and "Importing N files…"; the click was either lost while the dialog
    closed (0 and 40 ms) or came after the import — the files went into the project the import started in (title with
    `*`, "Added 4 media asset(s)") and New asked "Unsaved changes" (answered Don't Save, the next project empty). In no
    attempt did the files reach the new project; "Import stopped: …" never appeared (0 lines in the log). The plain
    import (scenario 34's baseline) worked: 4 files added and analysed. Open / Recover can't race at all from the UI —
    Open needs its folder picker first and Recover is offered at startup; their replacement is the same project-object
    change the tests cover with New. The guarded path itself is covered by the automated tests only.
  - Committed as `d467a84` (the product owner's permission, 2026-10-05; not pushed).
- Step 12.9 done (2026-10-05) — final verification & closeout (the product owner's decisions: the manual plan run by
  Claude, the step-time real-app checks counted, R1 from the Phase 11 state, R2 / R3, the full local QG, the test-helper
  cleanup left for later, no push / pull request / change of `main`).
  - Audit before the checks: 7 commits on the branch (12.2–12.8), clean tree, not published; the manual plan had 1–28,
    31, 32 and part of 29 run only as automated tests; ARCHITECTURE still said "Phase 12 changes nothing here yet".
  - Automated QG (`--artifacts-path` in the session's scratch folder): `dotnet build AiVideoEditor.sln --no-incremental
    -warnaserror` 0 errors / 0 warnings; `dotnet test` once: 2324 passed, 2 skipped (only the two 4K scenes), 0 failed —
    Core 448, Timeline 425, Project 371, UI 541, Export 99, Rendering 58, Video 294, ExportEndToEnd 88 + 2; three runs
    with `--blame-hang --blame-hang-timeout 5m`: each 2324 / 0 / 2 over all 8 test assemblies, no hang, no dump; the
    4K scenes once with `AIVE_HEAVY_TESTS=1` (`tests/ExportEndToEnd.Tests`): 90 passed, 0 skipped, 0 failed;
    `git diff --check` clean. No failure, nothing fixed.
  - Manual plan (`docs/PHASE12_MANUAL_TEST_PLAN.md`, results log "Step 12.9 formal run"): the Debug build, an isolated
    profile, small fixture projects copied from the Phase 10 fade fixture (V2 / V1 / A1, fades, a dissolve; variants
    with a locked V2, an offline image, an extra unused media), every action through the UI (UI Automation for the
    buttons and dialogs, real clicks for clips, rows and the Windows file dialogs), every result checked on the saved
    `project.json`, the window title, the status bar, the dialogs and screenshots. Passed: 1–4, 6–11, 13–25, 27–32, 34
    (30 scenarios; 29 / 30 / 34 partly from the Step 12.7 / 12.8 runs); partly: 5 (the order, the sound not listened)
    and 26 (overlap, locked track and removed media — the deleted-track case automated only); not reproducible by hand:
    12 (removing media during its analysis) and 33 (New during an import) — automated tests only.
  - R1: the Phase 11 state extracted with `git archive 47ed2fa` into the scratch folder and built there (no worktree,
    branch or commit); the Phase 11 app opened a copy of the fixture, added a text clip and saved; Phase 12 opened it
    (4 media, 0 missing, 13 clips), a plain Save wrote the same file, and after a marker the saved file was
    `formatVersion` 3 with clips, media, tracks and the dissolve identical to the Phase 11 file; the Phase 11 app opened
    that file again (13 clips).
  - R2 (`docs/EXPORT_MANUAL_TEST_PLAN.md`): 1 / 14 — two exports (34 s after a track move, 30 s after a ripple) H.264
    640 × 360 25 fps + AAC 48 kHz stereo, video = audio duration, decode clean, the 6.6 s frame red as the Preview after
    the move; 7 — Cancel in Audio (74 %): "Export cancelled.", no file, no leftovers; 10 — an offline image: "Export not
    possible" naming it, no picker, nothing written; 11 — "Replace file?": Cancel leaves the file byte-identical,
    Replace replaces it after the export; 12 — the picker closed: nothing; 13 — the main window disabled during the
    export (and every Phase 12 command off, navigation on), everything back afterwards, no Undo step from exporting.
    Not re-run: 2, 3, 5, 6, 8 (rendering — no export / render / playback / audio code changed in Phase 12; parity suites
    and 4K green); 9 optional.
  - R3: relink of an offline image through Relink… (linked, the OFFLINE row gone, undoable); `Recent ▾` (10 entries,
    newest first; opening one asks about unsaved changes and moves it to the top); a fade in the Preview (the fade-in
    start dark — brightness 18 against 125 in the clip); a dissolve selected on its zone (after a zoom in — see the
    observation below), removed, added with Dissolve ("Dissolve added: 25 frames."), both undone.
  - Observation (not a Phase 12 change, D025 Step 10.8): at the default zoom a 0.4 s dissolve zone is covered by the trim
    handles of its two clips, so a press there acts on a clip; the zone is selectable after zooming in.
  - Documentation: ARCHITECTURE (the Timeline section's Phase 12 paragraphs, the verification note), README, ROADMAP,
    this file, D027 (closeout), `docs/DEVELOPMENT_PLAN.md` (the Phase 12 checkbox — complete locally), the manual plan
    (statuses and the results log).

### Phase 11 — Media relink & recent projects (complete; PR #11 merged as `47ed2fa`, CI green)

Steps (D026; each accepted by the product owner before the next): 11.1 audit · 11.2 scope formalization · 11.3 media
availability re-check · 11.4 relink core · 11.5 batch search · 11.6 relink UI · 11.7 recent projects core · 11.8 recent
projects UI · 11.9 closeout. Working rule from the product owner for this phase: no build or test run without a separate
command; no push, merge, pull request or branch deletion without direct permission.

- Step 11.1 done and accepted (2026-10-01) — audit, no change (only `git fetch origin`).
  - Git: `origin/main` = `2e758f1` "Merge pull request #10 from Proskagit/feat/phase-10-transitions-effects"; `ee0527d`
    is in it and the trees of `ee0527d` and `2e758f1` are identical. The local `main` was at `409240b` (PR #8), 23
    commits behind and none ahead; working tree clean, no stash.
  - Persistence (D014): `MediaAsset.FilePath` absolute, `IsMissing` runtime only; `project.json` stores `FilePath` and
    `RelativePath` (none on another volume, `ProjectFileDto`); `ProjectSerializer.ResolveMediaPath` — absolute, else
    relative, else the absolute path kept and the asset missing; a project moved with its media opens without a relink;
    missing media keeps its saved references on Save.
  - Missing detection: `ProjectService.MarkMissingMedia` (`File.Exists`) on Open and Recover, before the project
    replaces the current one; nothing re-checks later. `IProjectService.DetectMissingMedia()` has no production caller
    (only the design-time stub in `MainWindow.axaml.cs`, a fake in `Video.Tests` and one `Project.Tests` test) and raises
    no event — its XML comment promises more than it does.
  - Offline handling: `MediaAnalysisCoordinator` never probes missing media (`QueueWhereNeeded`, `QueueAnalysis`);
    `MediaCacheCoordinator.CanMake` / `SourceFileCache` — cached thumbnails / waveforms only; `PlaybackSnapshotBuilder`
    placeholder "The media file is missing."; `MediaBrowserItemViewModel` "Media offline"; `ExportPreflight` error
    `MediaOffline` (also its own `File.Exists`); `TimelineEditService` refuses to add missing media; the Open / Recover
    status message counts the missing files (`ProjectFileWorkflow.MissingSuffix`).
  - Relevant mechanics: the playback snapshot rebuilds when an asset's `FilePath` or `IsMissing` changes
    (`AssetState`; `DiffersOnlyInPresentation` compares the assets); `MediaCacheCoordinator` handles an asset once per
    generation by id and a generation starts only on `ProjectChanged` (a relinked asset would keep its old thumbnail /
    waveform); the cache key (asset id, size, last-write time, rule) already tells a new file apart; the analysis
    coordinator skips `Completed` assets and holds `_inFlight` per asset; `TimelineValidator` rejects `SourceOut` beyond
    `Metadata.Duration` (a relink to a shorter file would block every later edit of the clip); `AddMediaAssets`
    deduplicates by path (a relink could create two assets with one path).
  - Open path and window: `ProjectFileWorkflow` (New / Open with the folder picker / Save / Save As / Close, the
    unsaved-changes prompt, `StartSessionAsync` — unsaved caches cleaned, recovery offer, autosave start) has a public
    non-interactive `OpenAsync(folder)`; the toolbar is a row of buttons, no menu bar and no context menus;
    `Program.Main` hands its arguments to Avalonia, and only the Debug-only `--open-project <folder>` of `DevStartup`
    reads them (a development option, not a product feature — the audit report said no argument was handled; corrected
    here).
  - Reusable: `AppPaths.ConfigFolder` (unused), `ProjectFileStore.WriteAtomicAsync`, `RecoveryStore` (an app-wide store
    with damaged-file handling and other-process awareness), `IFilePickerService.PickFilesAsync` (single file, filters),
    the import's extension rules (`MediaImportService`).
  - Already out of scope before: D014 ("Relink is out of scope"), the Phase 6 deferred list (relink, recent projects,
    re-checking missing media), D024 Steps 9.4 / 9.5 (media back online not re-checked), README "Not planned yet".
    Nothing of Phase 11 exists yet.
  - Documentation findings: ROADMAP / DEVELOPMENT_PLAN without the PR #10 merge; this file's "Current phase", the Step
    10.9 pull-request sentence, "Last known state" (2026-09-29) and "Completed" (no Phase 10) outdated; README "Not
    planned yet"; ARCHITECTURE "Verification note" without the Phase 10 closeout; `docs/README.md` without the manual
    test plans; `CLAUDE.md` "11 projects" (the solution has 19 projects: 11 under `src/`, 8 under `tests/`); the comments
    of `DetectMissingMedia` and `MediaAsset.IsMissing` ("on project load"), the unused `AppPaths.ConfigFolder` /
    `ProjectMediaFolder` (code — not changed at 11.2).
- Product owner decisions PO-1…PO-9 (2026-10-01), recorded in D026 and summarised in `docs/DEVELOPMENT_PLAN.md`: PO-1
  relink undoable, dirty, consistent undo of path + metadata + analysis state; PO-2 hard rejects (missing file, wrong
  type, too short for the used source range, path of another asset) and warnings with confirmation (resolution, frame
  rate, no audio, other characteristics, short dissolve handles), no adaptation of clips; PO-3 relink allowed without
  ffprobe (`Pending`); PO-4 batch search in the chosen folder only, no recursion, exact name, summary, confirmation;
  PO-5 re-check on window activation (throttled), no `FileSystemWatcher`, always before export / relink, both
  directions, never on the UI thread; PO-6 online media out of scope; PO-7 `Recent ▾` next to Open, 10 entries, no start
  screen / auto-open / menu bar / redesign; PO-8 added after successful Open, Save As, Recover with a folder, unavailable
  entries kept and removable; PO-9 a path of another asset rejected, no merge. Step order 11.3–11.9 confirmed (relink
  before recent projects). The documentation keeps its current split into documents (product owner).
- Step 11.2 done (2026-10-01) — scope formalization, documentation only (no production code, no test changed, no build
  or test run). `git checkout main` + `git merge --ff-only origin/main` (`409240b` → `2e758f1`), branch
  `feat/phase-11-relink-recent-projects` created from it.
  - DECISIONS: D026 (context from the audit, §1 scope and constraints, §2 re-check, §3 relink, §4 batch, §5 relink UI,
    §6 recent projects, consequences); D014's "once per Open … Relink is out of scope" and the D024 9.4 / 9.5 "not
    re-checked" notes marked superseded by D026 (not rewritten).
  - `docs/DEVELOPMENT_PLAN.md`: the Phase 10 line (closeout `ee0527d`, PR #10 / `2e758f1`), the Phase 11 line and the
    section "Phase 11 — Media relink & recent projects: steps" (the scope stated as based on PO-1…PO-9, gates,
    constraints, steps 11.1–11.9 with PR / QG / Impl items and the sub-decisions left to a step's start).
  - ROADMAP (Current: Phase 11; Phase 10 merged; future phases), README (status), ARCHITECTURE (the missing-media
    behaviour as it is now, the planned Phase 11 changes, the verification note), `docs/README.md` (every document in
    `docs/`), `CLAUDE.md` (project count), this file (this section, the Phase 10 heading, "Last known state",
    "Completed").
  - `docs/PHASE11_MANUAL_TEST_PLAN.md` — skeleton: scenarios per step, status "planned".
  - Left to the step that changes the code: the comments of `IProjectService.DetectMissingMedia` and
    `MediaAsset.IsMissing` (11.3); `AppPaths.ConfigFolder` gets its user at 11.7; `AppPaths.ProjectMediaFolder` stays
    unused (copying media into the project is out of scope).
  - Sub-decisions left to a step's start (D026): 11.3 the throttle interval; 11.4 the compared characteristics, a later
    incompatible analysis, a failed probe of an existing file; 11.5 the batch's undo granularity and duplicate names;
    11.6 the placement of Relink; 11.8 a "Clear list" item.
- Step 11.2 accepted by the product owner (2026-10-01) and committed as `ba6762e` (the Step 10.9 pull-request sentence
  marked as a historical record).
- Step 11.3 done (2026-10-01) — media availability re-check (D026 §2; choices recorded as D026 "Refined in Step 11.3").
  - Core: `IProjectService.RecheckMediaAsync(ct)` and `MediaAvailabilityChanged` (`MediaAvailabilityChangedEventArgs`:
    `Returned`, `Gone`); `DetectMissingMedia` (no production caller, no event) removed — the design-time stub, the
    `Video.Tests` fake and one `Project.Tests` call follow. `MediaAsset.IsMissing`'s comment says what it is now.
  - Project: `ProjectService` — an injectable file check (`Func<string, bool>`, `File.Exists` in the app; also used by
    Open / Recover), `RecheckMediaAsync`: project and paths captured on the caller's thread, `File.Exists` in one
    `Task.Run`, applied on the caller's context only to the same project and to assets with the same path, an error =
    missing; a loop under a lock folds requests during a running check into one more check; the event, then
    `MediaAssetsChanged`; no dirty flag, no history, no `SaveStateChanged`.
  - UI: `MediaAvailabilityMonitor` (new, singleton) — `OnWindowActivated` from `MainWindow.Activated` through
    `MainWindowViewModel.OnWindowActivated`; interval 3 s from the last check's start, one trailing check for activations
    inside it; `Stop()` from `MainWindowViewModel.PrepareToCloseAsync`; status-bar messages for the changes.
    `ExportWorkflow` awaits `RecheckMediaAsync` before its preflight. `MediaAnalysisCoordinator` — returned `Pending` /
    `Failed` assets analysed, the display-size refresh as after Open; an analysis / refresh whose asset is missing or has
    another path when the probe ends writes nothing (missing: the status and error from before the queueing restored).
    `MediaCacheCoordinator` — `Restart(ids)` on `MediaAvailabilityChanged.Returned` (forget as handled, request again);
    each handling has an identity checked before publishing, so work from before a restart publishes nothing; the
    result shown while offline stays until the new one is ready. Preview / Media Browser / export preflight: no change
    needed (snapshot `AssetState.IsMissing`, rows rebuilt on `MediaAssetsChanged`, `IsMissing` + `File.Exists`).
  - Interval choice: 3 s — activation is the moment a user returns from Explorer; the trailing check makes a file
    restored within 3 s of the last check visible without another switch; on a slow share a check may take seconds, so
    a check per activation would pile up (they are folded anyway); no timer while the window stays active (PO-5: no
    watcher, activation only).
  - Tests (new): `Project.Tests/MediaRecheckTests` (12: gone, returned, nothing changed, never dirty / no history / file
    unchanged, off the caller's thread and not blocking it, replaced project dropped, path changed meanwhile left alone,
    requests folded into one more check, consecutive checks, an error = missing, Open uses the same check, no media);
    `UI.Tests/MediaAvailabilityTests` (13: analysis of returned `Pending` / `Failed`, saved metadata not probed again,
    display-size refresh, analysis dropped when the file went meanwhile and run again on return, dropped for another
    path; offline thumbnail made on return, cached thumbnail kept until replaced, work from before a restart
    publishes nothing, nothing made again for files that stayed; offline waveform made on return; the Preview shows
    the placeholder and the frame again; the Media Browser row); `UI.Tests/MediaAvailabilityMonitorTests` (6: first
    activation checks, one trailing check for activations inside the interval — it sees a change made meanwhile, after
    the interval at once, nothing after Stop, status messages, no message without a change). Changed:
    `ExportWorkflowTests` (+2: a file gone since the last check blocks with "offline" and is marked; a file back is
    online for the preflight), `OpenMissingMediaTests` (`DetectMissingMedia` → `RecheckMediaAsync`, assertion unchanged).
  - Mutations (each reverted): no handling identity check → 1 failure; no cache restart → 5; analysis ignoring a changed
    asset → 2; no analysis on return → 4; no replaced-project drop → 1; no path check in the re-check → 1; export without
    the re-check → 2; no trailing check → 1; no request flag (no fold) → 7.
  - Build: the running `AiVideoEditor.exe` (PID 38056 — not started in this step, left running) locks
    `src/App/bin/Debug`, so builds and tests ran with `--artifacts-path` in the session's scratch folder (same sources,
    same configuration). Verification: `dotnet build AiVideoEditor.sln --no-incremental -warnaserror` 0 errors / 0
    warnings; `dotnet test` (whole solution, once) 2021 passed, 2 skipped (the 4K heavy scenes), 0 failed — Core 448,
    Timeline 314, Project 331 (+12), UI 394 (+21), Export 99, Rendering 58, Video 291, ExportEndToEnd 86 + 2; parity
    suites unchanged. Not run in this step: `--blame-hang` repeats, the 4K scenes, CI (the branch is not pushed), the
    manual scenarios in the real app.
  - Manual plan: scenarios 1–6 filled in (automated coverage named), status "manual pending".
- Step 11.3 preliminarily accepted on the automated results (product owner, 2026-10-01), then the real-app run of the
  manual scenarios 1–6 by Claude (2026-10-01, `db0feba`, Debug from the artifacts folder; another running instance —
  PID 38056, `src/App/bin` — left alone): all passed — details in `docs/PHASE11_MANUAL_TEST_PLAN.md` "Results log". With
  that, Step 11.3 is accepted (the product owner's condition). No code changed. Notes (not 11.3 defects, nothing done):
  a file the Preview is decoding can't be renamed on Windows (OS lock — in practice such a file goes only with its
  drive); opening a project with media on an unreachable share took ≈ 47 s (Open's file-by-file checks before the
  project is shown, unchanged since Phase 6; the window stayed responsive); Ctrl+E sent by `SendKeys` did not start the
  export in this run (the toolbar button did) — not investigated; an offline video without metadata is drawn as a
  full-canvas placeholder over the lower layers (its size is unknown; existing behaviour).
- Step 11.3 accepted in full by the product owner (2026-10-01); the run committed as `69ce4b7`.
- Step 11.4 done (2026-10-01) — relink core (D026 §3; the sub-decisions confirmed by the product owner at its start and
  the implementation in D026 "Refined in Step 11.4").
  - Technical analysis first (no code), plan approved: reuse the re-check, `IUndoRedoService`, `IMediaAnalysisService`,
    the import's extension table, the `TimelineValidator` length rule, `DissolveHandles`, `AssetState`, the 11.3 guards.
    Confirmed: the compared characteristics; a failed probe (ffprobe available) is a reject, ffprobe unavailable is not;
    a later incompatibility is a message, never undone automatically; no old metadata kept without ffprobe (`Pending`,
    the validator / preflight limits stand until the analysis).
  - Core: `MediaFileTypes` (the extension → kind table, now also the import's); `IMediaRelinkService` (`CheckAsync`,
    `ApplyAsync`, `RelinkedMediaFoundIncompatible`), `RelinkCheck`, `RelinkResult`, `RelinkRejection`
    (AssetNotFound, NotOffline, FileNotFound, WrongMediaType, UnreadableMedia, TooShort, PathInUse, Stale),
    `RelinkWarningKind` (NotChecked, DisplaySize, FrameRate, NoAudio, Rotation, StartTime, VideoCodec, AudioCodec,
    SampleRate, Channels, DissolveHandles); `IProjectService.MediaRelinked` + `NotifyMediaRelinked` (implemented in
    `ProjectService`, the design-time stub and the `Video.Tests` fake).
  - Timeline: `MediaRelinkService` (the checks, the probe, the warnings, `ApplyAsync`'s re-validation, the watcher of
    later analyses) and `Commands/RelinkMediaCommand` (`MediaFileState` / `MediaRelink`, a list per command for 11.5).
    Placed in Timeline, not Project as the plan's scope line said: the rules it applies are the timeline's; the plan's
    line was adjusted.
  - UI: `MediaCacheCoordinator.Restart(…, dropResults)` on `MediaRelinked` (thumbnail / waveform of the old file dropped,
    the new requested; work for a replaced file publishes nothing); `TimelineViewModel` refreshes clip waveforms on
    `MediaAssetsChanged`; `MainWindowViewModel` shows `RelinkedMediaFoundIncompatible` in the status bar. DI:
    `IMediaRelinkService` → `MediaRelinkService` (singleton). No relink UI yet (11.6).
  - Tests (new): `Timeline.Tests/MediaRelinkServiceTests` (32: success keeps id / clips / speed and updates path, size,
    metadata, dirty, one step; exact undo / redo incl. the save point; save → reopen with absolute and relative path;
    online asset, missing file, wrong type ×3, no video stream, no audio stream, failed probe ×4, too short with the exact
    boundary, the longest range over tracks, a 2× clip, no clips / images, path in use (case); the warnings (video set,
    audio set, none when equal / longer / no earlier metadata), dissolve handles; ffprobe unavailable (`Pending`, no
    metadata, validator still refusing, undo); later incompatibility reported once, nothing undone; fitting media not
    reported; Apply re-validation: path taken, file changed / gone, old file back, clip lengthened, another project;
    unknown asset); `UI.Tests/MediaRelinkUiTests` (7: old thumbnail dropped at once and the new made, undo; work before an
    undo not published; the timeline waveform gone for a file without sound; the Preview decodes the new file and is
    offline again after undo; the export preflight passes; unprobed relink not analysed and reported as not analysed;
    the status message of a later incompatibility); `Video.Tests/MediaRelinkIntegrationTests` (3, real ffprobe: shorter
    rejected, sound-only mp4 rejected, another resolution warned, the same kind applied with its probed metadata — a
    small project-service stand-in: Video.Tests can't reference Project, whose namespace hides the `Project` type there).
  - Mutations (each reverted): old thumbnail kept on relink → 3 failures; no timeline waveform refresh → 1; Apply without
    the length check → 1; a failed probe accepted → 4; no duplicate-path check → 2; online media relinked → 2; no later
    report → 1; ffprobe unavailable rejected → 2; no re-check in Check → 13; too short accepted → 5.
  - Verification (built with `--artifacts-path` in the session's scratch folder — `src/App/bin` is still locked by the
    other running instance, PID 38056, left alone): `dotnet build AiVideoEditor.sln --no-incremental -warnaserror` 0 errors /
    0 warnings; `dotnet test` (whole solution, once) 2063 passed, 2 skipped (the 4K heavy scenes), 0 failed — Core 448,
    Timeline 346 (+32), Project 331, UI 401 (+7), Export 99, Rendering 58, Video 294 (+3), ExportEndToEnd 86 + 2; parity
    suites unchanged. Not run: `--blame-hang` repeats, the 4K scenes, CI, a real-app run (no relink UI before 11.6).
  - Manual plan: relink scenarios 7–14 name their automated coverage, status "auto (core, 11.4); manual with the UI
    (11.6)"; the real-app relink run belongs to 11.6 (no relink UI exists before it).
- Step 11.4 accepted by the product owner (2026-10-01), `0e002dc` its final commit; the manual relink scenarios 7–14 are
  run once the UI exists (11.6).
- Step 11.5 done (2026-10-01) — batch relink search (D026 §4, PO-4 as restated at the start of 11.5; D026 "Refined in
  Step 11.5").
  - Decisions within PO-4 (no new product decision): a file name shared by two or more offline items is given to none of
    them (listed as ambiguous, "relink them one by one"); the confirmed batch is one undoable step; items that fail the
    re-validation at Apply are reported and stay offline while the still-valid ones are applied together. The folder is a
    parameter: how the UI chooses it is 11.6's.
  - Flow (one implementation of the rules — no second relink path): `SearchFolderAsync(folder)` → re-check once (PO-5) →
    list the folder's own files off the UI thread → per offline item: exact name (case-insensitive) → `NotFound` /
    `Ambiguous` / the 11.4 check `CheckCoreAsync` (the code `CheckAsync` runs after its re-check) → `Found` or
    `Rejected` → `RelinkSearch` (`Entries`, `Applicable`, `Summary()`); the user confirms → `ApplyAllAsync(Applicable)` →
    re-check once, every file's size in one `Task.Run`, per item the 11.4 re-validation plus the batch's own (one file per
    item, an item once) → one `RelinkMediaCommand` ("Relink N Media Files") for the valid items → per-item
    `RelinkResult` (now with `AssetId`) in a `RelinkBatchResult`. `ApplyAsync(check)` became `ApplyAllAsync` with one item.
  - Tests (new): `Timeline.Tests/MediaRelinkBatchTests` (15: exact names directly in the folder — subfolder, `.bak`,
    "(1)" not matched, only the match probed, nothing changed; the real listing without subfolders and names ignoring case;
    nothing offline / a folder that isn't there; a shared name given to neither; a file of another (online) item; another
    media type by the probe; every candidate's check equal to a single `CheckAsync`; a partial batch (found, warned, too
    short, absent) with the summary, one step, clips untouched; undo / redo of the whole batch; not confirmed / cancelled;
    a second search after a partial one; Apply races — file gone, file changed, path taken, the rest applied; one file
    never for two items, one item never twice; an item whose old file came back; the folder listed off a UI-thread
    stand-in (a single-thread synchronization context) while that thread stays responsive); `UI.Tests/MediaRelinkUiTests`
    (+1: a batch refreshes both thumbnails, one Undo drops both).
  - Mutations (each reverted): a shared name assigned → 1 failure; recursive listing → 1; case-sensitive names → 1; one
    file for two items → 1; one item twice → 1; one undo step per item → 2; matches not checked → 5; no size re-validation
    → 2; listing on the calling thread → 1 (survived the first test, which had no synchronization context — the test now
    runs the search on a UI-thread stand-in); ambiguity counting online items → 1.
  - Verification (artifacts folder, as in 11.3–11.4; the other instance PID 38056 left alone): `dotnet build
    AiVideoEditor.sln --no-incremental -warnaserror` 0 errors / 0 warnings; `dotnet test` (whole solution, once) 2079
    passed, 2 skipped (the 4K heavy scenes), 0 failed — Core 448, Timeline 361 (+15), Project 331, UI 402 (+1), Export 99,
    Rendering 58, Video 294, ExportEndToEnd 86 + 2; parity suites unchanged. A doc comment changed after that run; an
    incremental `-warnaserror` build afterwards: 0 / 0. Not run: `--blame-hang` repeats, the 4K scenes, CI, a real-app run
    (no relink UI before 11.6).
  - Manual plan: batch scenarios 18–21 name their coverage, status "auto (core, 11.5); manual with the UI (11.6)".
- Step 11.5 accepted in full by the product owner (2026-10-01), `4180b4a` its final commit.
- Step 11.6 implemented (2026-10-01) — relink UI (D026 §5; the audit and plan approved by the product owner, all four UI
  extensions, both batch entries, the Open hint; D026 "Refined in Step 11.6"). The manual acceptance of scenarios 7–21
  follows; 11.7 does not start before it.
  - UI flow: `UI/Services/MediaRelinkWorkflow` (singleton, DI) — Relink: picker (kind filter, start in the old folder when
    it exists — checked off the UI thread) → `CheckAsync` → rejection dialog / warning confirmation → `ApplyAsync` →
    refusal dialog or status; only after an applied relink and while others are offline: "Find other missing media?" →
    the batch in the chosen file's folder. Find Missing: folder picker → `SearchFolderAsync` → `Summary()` (Relink N
    Files / Cancel, or OK) → `ApplyAllAsync` → "Relinked X of Y" with reasons for refused items. The `EditingLock` is checked
    before starting, after every await and right before applying; `IsRunning` keeps it to one workflow. No relink rule
    in the UI; errors are the service's messages; the UI changes no asset itself.
  - Media Browser: an "OFFLINE" header row (only while media is offline) with Relink… (`RelinkCommand`, a selected offline
    item) and Find Missing… (`FindMissingCommand`), told on lock, selection, media and workflow changes; `HasOfflineMedia`.
  - The four extensions: `FilePickerRequest.StartFolder` → `SuggestedStartLocation` in `AvaloniaFilePickerService`; the
    dialog's message in a `ScrollViewer` (max 420 px) in `AvaloniaDialogService`; the Media Browser selection kept by asset
    id (was the path, which a relink changes); "Not analysed yet" for a `Pending` row. Open / Recover message: "… shown as
    offline — use Relink or Find Missing in the Media Browser." (two `ProjectOpenWorkflowTests` assertions follow the
    approved wording). `ScriptedPicker` answers file pickers from a queue and records the requests.
  - Tests (new): `UI.Tests/MediaRelinkWorkflowTests` (22: relink without dialogs, no offer when nothing else is offline;
    the picker's kind filter and start folder (and none when the folder is gone); a cancelled picker; a rejection
    explained, nothing applied, no offer; warnings confirmed / cancelled (no offer); without ffprobe → "Not analysed yet";
    races while the warning is open — file gone, file changed, asset online again, path taken, project replaced — each
    explained, nothing changed, no offer; the offer after an applied relink (Search → summary → one batch step; Not Now);
    Find Missing summary with every group and one step; a cancelled summary; nothing to apply; items refused at Apply
    listed; the commands with selection, offline media and the lock (and told about it); an export started during the
    check or while asked; one workflow at a time; the Media Browser through relink, batch, undo ×2, redo ×2 — rows,
    thumbnails, selection, the OFFLINE row).
  - Mutations (each reverted; 16): no lock check before Apply → 1 failure; none after the check → 1; none before the
    batch's Apply → 1; the offer with nothing else offline → 2; a cancelled warning applied → 1; no one-at-a-time guard →
    first a hang (the test awaited the second workflow, which waited for the open picker — found with `--blame-hang`; the
    test now asserts that the second call is refused at once) → 1; no start folder → 1; every kind in the filter → 1; a
    cancelled summary applied → 1; refused items not listed → 1; a rejection not shown → 1; Relink for online media → 1;
    the lock change not told to the buttons → 1; the offer after a refused Apply → 5; Pending without text → 1. Survived,
    equivalent: restoring the selection by path instead of id — the asset object's path is changed in place before the
    list is rebuilt, so the path read at rebuild time already is the new one; the old code never lost the selection on a
    relink (the 11.6 audit's reason for the change was wrong). Kept by id (approved, robust, no behaviour change).
  - Verification (artifacts folder; the other instance PID 38056 left alone): `dotnet build AiVideoEditor.sln
    --no-incremental -warnaserror` 0 errors / 0 warnings (a first run caught `.Result` in a test — xUnit1031 — fixed);
    `dotnet test` (whole solution) 2101 passed, 2 skipped (the 4K heavy scenes), 0 failed — Core 448, Timeline 361, Project
    331, UI 424 (+22), Export 99, Rendering 58, Video 294, ExportEndToEnd 86 + 2; parity suites unchanged. Not run:
    `--blame-hang` repeats of the full suite, the 4K scenes, CI, the manual scenarios (next).
  - Manual plan: scenarios 7–21 runnable, status "manual pending", the UI coverage named.
- Step 11.6 accepted on the code preliminarily (product owner, 2026-10-01); the manual run of scenarios 7–21 by Claude in
  the real app (results in `docs/PHASE11_MANUAL_TEST_PLAN.md`): all functional, defects D1–D5 found; fixing them ordered by
  the product owner (2026-10-02).
- Step 11.6 acceptance fixes (2026-10-02, D026 "Refined after the Step 11.6 manual run"):
  - D1 cause: `InspectorViewModel.ShowMedia` set "Analyzing…" for `Pending` and `Analyzing` and ignored `IsMissing`
    (Phase 3 logic; visible once a relink without ffprobe leaves an item `Pending`). Fix: `AnalysisStatusText` /
    `HasAnalysisStatusText` (the view binds them) — "Media offline", "Analyzing…" only while analysing, "Not analysed yet"
    for `Pending`; `IsAnalyzing` true only while an analysis runs; the error only for an online failed analysis.
  - D2 cause: the cache keeps one file per asset (`SourceFileCache.Write` deletes the older) and offline media takes the
    last one (D024); after a relink that is the relinked file's, so an Undo showed it on the offline item (thumbnails and
    waveforms alike — the waveform part reproduced in the new tests). Fix: `MediaRelinkedEventArgs.Replacements`
    (`MediaFileReplacement`: asset + previous path; `NotifyMediaRelinked` takes them; `RelinkMediaCommand` passes the
    before / after path) and `MediaCacheCoordinator.OnRelinked`: what was shown for the file an asset leaves (a result or
    none, if its handling had settled — `Generation.Settled`) is kept in `Generation.Shown` by (asset, path) and shown
    again when an Undo / Redo returns the asset to that path (handled at once, no cache read, `Ready` raised for a result);
    otherwise requested as before. Work for a replaced file still publishes nothing (the handling identity). D024 for
    media offline for other reasons unchanged. Limitation: memory only — after Undo, Save and a reopen an offline item
    gets the cache's last file again.
  - D3: "is an audio file" (`MediaRelinkService` names both kinds as files). D4: `MediaRelinkWorkflow` — a `NotChecked`
    warning gives "Relink without a compatibility check?" / "The technical compatibility of … was not checked:". D5:
    `MediaRelinkWorkflow` — only unusable matches → "Files with matching names were found in the folder, but none of them
    can be used."
  - Tests (new): `UI.Tests/InspectorMediaStatusTests` (9: every status online / offline, the same words as the Media
    Browser, clearing); `UI.Tests/RelinkUndoCacheTests` (6, a fake cache that behaves like the real one: none → relink →
    undo none / redo new, three rounds, no re-make; batch with an item that had its own thumbnail and one that had none;
    consecutive relinks and undos back to each file; a make still running at Undo never published; D024 for an item
    offline when the project opens; the waveform of an offline audio clip). Changed: `MediaRelinkUiTests` (2) and
    `MediaRelinkWorkflowTests` (1) expected "none" after Undo where the item had shown its own thumbnail before — now the
    same thumbnail as before; `MediaRelinkWorkflowTests` +2 (D3 text, D5 status) and the D4 / "no match" status asserts.
  - Mutations (each reverted; 9): nothing remembered → 7 failures; nothing restored → 7; settled never marked → 7; Pending
    says Analyzing → 1; offline ignored in the Inspector → 4; ffprobe-unavailable says "differs" → 1; unusable reported as
    nothing → 1; "is an audio." → 1. Survived, equivalent: the restored result not raised by `Ready` — the Media Browser
    and the timeline read it again on the `MediaAssetsChanged` that follows `MediaRelinked`.
  - Verification: `dotnet build AiVideoEditor.sln --no-incremental -warnaserror` 0 / 0; `dotnet test` (whole solution)
    2118 passed, 2 skipped (4K), 0 failed — Core 448, Timeline 361, Project 331, UI 441 (+17), Export 99, Rendering 58,
    Video 294, ExportEndToEnd 86 + 2. Re-check in the real app: D1, D2 (single and batch, thumbnail and waveform), D4, D5 —
    passed (the plan's results log). `TestResults` folders of the `--blame-hang` mutation runs remain under the git-ignored
    `tests/*/TestResults` (the sandbox refused removing them).
- Step 11.6 accepted by the product owner (2026-10-02): `5c4d2d9`, manual acceptance PASS.
- Step 11.7 implemented (2026-10-02, D026 "Refined in Step 11.7"; the audit and the decisions on the slow availability
  check, the silent write errors and the damaged file agreed with the product owner) — awaiting review.
  - Core `IRecentProjectsStore` + `RecentProject(FolderPath, Name, LastUsedAt)`; `RecentProjectsStore`
    (`src/Project/Persistence`): JSON `{ format "AiVideoEditor.RecentProjects", formatVersion 1, projects[] }` in
    `AppPaths.RecentProjectsFile` (`%LOCALAPPDATA%\AiVideoEditor\config\recent-projects.json`; new
    `AppPaths.ConfigFolderPath` / `RecentProjectsFile` that don't create the folder); key = full path without a trailing
    separator, ignoring case; 10 entries; re-read + atomic write (`ProjectFileStore.WriteAtomicAsync`) under
    `recent-projects.lock` (`FileShare.None`, retried for about 2 s) and a `SemaphoreSlim`; damaged → `*.<time>.damaged`;
    newer version / unreadable → not overwritten; bad entries dropped; errors logged, `false` returned. `IsAvailableAsync`
    off the calling thread, no time limit, no lock. Registered in `ServiceCollectionExtensions`.
  - `ProjectFileWorkflow` (new last optional parameter `recentProjects`): `RememberRecentAsync` after a successful
    `OpenAsync(folder)` (the opened project's folder and name), `SaveAsAsync` (the written folder, its name) and
    `OnRecoverAsync` (if the project has a folder); a store failure or exception is logged and changes no result or
    status message.
  - Tests (new): `Project.Tests/RecentProjectsStoreTests` (40 with theory cases: empty, order, update, four spellings of
    one folder — stored once, limit, remove / remove of an unlisted folder without a write, reload, eight damaged
    contents set aside, a change on a damaged file, bad entries, unnamed entry, duplicates + 13 entries, newer version
    untouched, unreadable file never overwritten, failed write keeps the file and no `*.tmp`, configuration folder that
    can't be created, invalid paths, two instances interleaved, eight in parallel, a held lock → false after the timeout
    and the file kept, availability true / five unavailable cases / an error, a hanging check holds up neither the caller
    nor a change, reads and changes off the calling thread); `UI.Tests/RecentProjectsWorkflowTests` (22: Open by its full
    path and name, the interactive Open, Open again moves first, three failed Opens, cancelled picker, Cancel at the
    unsaved-changes question, Save As (named after the folder), Save As into its own folder, the first Save, cancelled /
    refused Replace / failed Save As, Save + New + Close untouched, Recover with a folder, with its folder gone, never
    saved, failed; a throwing store and an unwritable list never fail Open / Save As / Recover; unavailable projects stay
    listed); `UI.Tests/RecentProjectsCompositionTests` (1: the app's composition gives the workflow the store at
    `AppPaths.RecentProjectsFile`).
  - Mutations (each reverted; 12): no Add after Open → 5 failures; after Save As → 4; after Recover → 3; no key
    normalisation when adding → 0 at first (every read normalised and de-duplicated again, so only the file kept a
    duplicate) — the spelling test now also checks the file: → 3; no normalisation anywhere → 6; no limit → 2; no
    re-read before a write (a cached list) → 5 + 2; a non-atomic write → 1; an unreadable / newer file overwritten → 1;
    a damaged file not set aside → 9; the availability check under the list lock → 1; the store not registered → 1.
  - Verification: `dotnet build AiVideoEditor.sln --no-incremental -warnaserror` 0 / 0; `dotnet test` (whole solution)
    2181 passed, 2 skipped (4K), 0 failed — Core 448, Timeline 361, Project 371 (+40), UI 464 (+23), Export 99, Rendering
    58, Video 294, ExportEndToEnd 86 + 2. No real-app run: 11.7 has no UI (the plan's scenarios 22–29 are run at 11.8).
- Step 11.7 accepted by the product owner (2026-10-02): `55606fd`.
- Step 11.8 implemented (2026-10-02, D026 "Refined in Step 11.8"); accepted by the product owner on 2026-10-04 (`7bf4ed8`).
  - "Clear list": not added (minimal safe option, reasons in D026) — one ✕ per entry.
  - `ProjectFileWorkflow.OpenFolderAsync(folder)`: the part of Open after the picker (unsaved-changes question, `OpenAsync`,
    discarding the recovery file after Don't Save), shared by `OpenProjectAsync` and the list.
  - `RecentProjectsViewModel` + `RecentProjectItemViewModel` + `RecentProjectAvailability` (`src/UI/ViewModels/Panels`);
    `ToolbarViewModel.Recent` (new last optional parameter); `ToolbarView`: `DropDownButton` "Recent" after Open with a
    400-wide flyout (header, empty state, rows: name / folder trimmed at the start / state, ✕); the code-behind forwards
    the flyout's `Opened` / `Closed` and hides it on `CloseRequested`. DI: `RecentProjectsViewModel` transient.
  - Tests (new): `UI.Tests/RecentProjectsUiTests` (29, on a UI-thread stand-in with the real ProjectService, workflow and
    store; the store wrapped to hold / fail checks and removals and to answer a reading late: empty state, order, names /
    folders, every opening reads again, Open / Save As at the next opening, overtaken readings, Checking → Available /
    Unavailable, failing check, slow check vs the UI and the list, state changes on the UI thread only, closing during a
    check, one shared check for repeated openings, replaced items, removed entry not brought back by a late check or
    reading, open through the workflow, Checking / Unavailable never open, no second open, open failure keeps project and
    entry, Cancel / Don't Save, an open ending while the drop-down is open again, remove, remove the last → empty state,
    failed / throwing removal ×2, editing lock); `UI.Tests/RecentProjectsViewBindingTests` (3: the button after Open and
    the lock, hidden without a view model, the row template — trimming, commands, state, tooltip, fixed width).
  - Mutations (each reverted; 16): list read once → 8 failures; Checking / Unavailable openable → 3; a new check at every
    opening → 1; late reading not dropped → 2; removal not dropping older readings → 1; replaced items still able to act → 0
    at first (no test used a replaced item) — test added → 1; no busy guard → 1; lock ignored → 2; failed removal shown as
    removed → 2; open without the unsaved-changes question → 1; check started on the UI thread → 1; failing check
    available → 1; no reading after an open while open again → 1; `OpenFolderAsync` opening another folder → 5; folder not
    trimmed in the view → 1; button not bound to the lock in the view → 1. An `IsDetached` early return in the
    availability tracking survived as equivalent (a replaced item is not shown and can't act) and was removed.
  - Real-app run (Claude, Debug build of the final code, UI Automation, the real `%LOCALAPPDATA%` list — the config folder
    didn't exist before; the test entries were removed through the UI at the end, leaving an empty `recent-projects.json`
    and `recent-projects.lock`): scenarios 22, 25, 26, 29, 30–38 passed, 23 only from the list (scenario 35), 24, 27, 28
    not run (automated only), 39 n/a; a defect found and fixed during the run: the 460-wide content was wider than the
    Fluent flyout's maximum, cutting the ✕ column off — now 400 (and the ✕ centred). Details in the plan's results log.
  - Verification: `dotnet build AiVideoEditor.sln --no-incremental -warnaserror` 0 / 0; `dotnet test` (whole solution)
    2213 passed, 2 skipped (4K), 0 failed — Core 448, Timeline 361, Project 371, UI 496 (+32), Export 99, Rendering 58,
    Video 294, ExportEndToEnd 86 + 2.
- Step 11.9 audit (2026-10-04, no change): 11.1–11.8 present (`55606fd`, `7bf4ed8` in the branch), the code matches the
  11.8 report; documentation inaccuracies listed (11.8 "awaiting acceptance", "skeleton", the plan's status legend). The
  product owner decided: no push / PR, CI after the local closeout; manual run variant (b).
- Step 11.9 local verification (2026-10-05, D026 "Refined in Step 11.9"; HEAD `7bf4ed8`, no code change) — awaiting the
  product owner's acceptance; CI pending (the branch is not published).
  - Automated (artifacts in a fresh scratch folder, nothing reused):
    `dotnet build AiVideoEditor.sln --no-incremental -warnaserror` → exit 0, 0 warnings, 0 errors, 19 projects;
    `dotnet test AiVideoEditor.sln --no-build` → exit 0, 2213 passed, 2 skipped (4K), 0 failed (Core 448, Timeline 361,
    Project 371, UI 496, Export 99, Rendering 58, Video 294, ExportEndToEnd 86 + 2 skipped), 64 s;
    the same three times with `--blame-hang --blame-hang-timeout 5m` → each exit 0, 2213 / 2 / 0, 63–64 s, no hang;
    `AIVE_HEAVY_TESTS=1 dotnet test tests/ExportEndToEnd.Tests --no-build` → exit 0, 88 passed, 0 skipped (the two 4K
    scenes included), 63 s.
  - Manual (Debug build of `7bf4ed8`, UI Automation, an isolated profile: `USERPROFILE` / `LOCALAPPDATA` of the test
    process pointed to a scratch folder; fixtures under `%TEMP%\aive119`; details in the plan's results log):
    23 through Open (a folder without `project.json`, a damaged project) PASS; 24 PASS (12 projects opened → 10
    entries; the second part — a differently cased path — through the Debug `--open-project` argument, because the folder
    picker returns the canonical spelling); 27 PASS (two instances on one profile: both instances' entries kept, a
    removal in one not undone by an add in the other); 28 PASS (a truncated list → the app starts, the list is empty,
    the file kept as `recent-projects.<time>.damaged` byte for byte, the next Open starts a new list); R1 PASS (a project
    saved by the Phase 10 build of `2e758f1` opens clean in the current build; Save writes `formatVersion` 3, byte-identical
    to the Phase 10 file); R2 PASS — `docs/EXPORT_MANUAL_TEST_PLAN.md` 1–8 and 10–14 (9, optional, not hit; 14 checked by
    full decoding and stream timing, not watched in a player; "without ffmpeg" not run); regression of relink (single with
    Undo / Redo — the D2 thumbnail fix holds —, batch with one Undo, gone / back during the session) and recent projects
    (open an available entry, remove an unavailable one, Cancel / Don't Save at the unsaved-changes question, disabled
    during an export) PASS.
  - Findings: no Phase 11 defect. Test-environment notes (not product issues): with an isolated profile the shell
    dialogs need `Desktop` / `Documents` folders in it; UI Automation in one PowerShell call doesn't see a dialog that
    appeared during that call (the next call does); the sandbox refused `Remove-Item` while a source was to be removed
    during an export, so that file was moved away instead (the same "file gone" for the app).
  - The user's profile (`%LOCALAPPDATA%\AiVideoEditor` config, recovery, cache) compared with a snapshot taken before the
    run: unchanged; no log written there.
- Phase 11 accepted by the product owner (2026-10-05) on the Step 11.9 local verification (closeout `ca20352`): build
  0 / 0, the full suite 2213 passed / 2 skipped / 0 failed once and three times with `--blame-hang`, heavy scenes
  88 / 88, the manual scenarios 23, 24, 27, 28, R1, R2 and the relink / recent projects / editing-lock regression passed,
  no Phase 11 defect, the user's profile unchanged. Still open, not hidden by the acceptance: CI (not run, the branch is
  not published — publication and CI are the product owner's next decision); export scenario 9 (optional) and the
  "without ffmpeg" check NOT RUN; export scenario 14 checked by decoding all 8 outputs, not watched in a player; L1-c
  open, outside Phase 11.

## Phase 10 (complete)

Phase 10 — Transitions & basic effects: **complete** (accepted by the product owner on 2026-10-01, last verified commit
`ddf45df`, closeout `ee0527d`; PR #10 merged into `main` as `2e758f1` on 2026-10-01), branch
`feat/phase-10-transitions-effects` (from `409240b`, `main` after the merge of PR #8; the docs commit of PR #9 merged
in). Scope, steps and acceptance criteria: `docs/DEVELOPMENT_PLAN.md` "Phase 10 — Transitions & basic effects: steps";
decision D025.

### Phase 10 — Transitions & basic effects (complete)

- Step 10.1 done and accepted (2026-09-29) — audit, no change. Findings: `Clip.Effects` (generic, untyped parameters)
  and `Track.Transitions` (`Id`, `TransitionTypeId`, `Duration` — no anchor) only persisted, never rendered; format v2
  ignores unknown properties (an older build would silently drop new typed fields — hence v3); the Preview and the
  export share `PlaybackSnapshot.LayersAt` → `CompositionDrawPlan` → `CompositionPainter` and the audio
  `AudioPlacement` / `AudioMix` rule; `LayersAt` returns one clip per track and `NextPictureChange` knows only clip
  edges; `VideoPipeline` keys readers by clip (two clips of one track can decode at once); `SourceFrameSelector` is
  exact for frames outside a clip; the mix gain is one constant per span; the trim limits (`MaxWholeFrames`,
  `SpeedTiming.FramesFor`, `CeilingFrame`) are exactly the handle rules; `EditPlan` carries clip changes only.
- Step 10.2 done (2026-09-29) — product owner decisions PO-1…PO-7 formalized (documentation only): D025 (model, format
  v3, fade ramps in frames and samples, dissolve zone `[c − ⌊F/2⌋, c + ⌈F/2⌉)`, handles, the edit coupling), the Phase 10
  steps in `DEVELOPMENT_PLAN.md`, ROADMAP, `docs/PHASE10_MANUAL_TEST_PLAN.md` (skeleton). Open for the product owner
  (before 10.6, not blocking 10.3–10.5): a fade on an edge that has a dissolve — proposed: not applied while the
  dissolve exists.
- Step 10.3 done (2026-09-29) — model and `project.json` v3.
  - Model: `Clip.FadeIn` / `FadeOut` (`MediaTime`, every clip kind); `Transition.LeftClipId` / `RightClipId`;
    `Core/Entities/TransitionRules` — `CrossDissolve`, `MinFrames` 2, `Frames` (`ToNearestFrame`), `Zone`
    (`⌊F/2⌋` before / `⌈F/2⌉` after the cut), `ClipFrames`, `ValidateTrack` (type, `F ≥ 2`, two different clips of
    the track that touch exactly, one transition per cut, zone parts fit each clip incl. both edges, video tracks only).
  - Format: `CurrentFormatVersion` 3; clips write `fadeInTicks` / `fadeOutTicks`, transitions `leftClipId` /
    `rightClipId`. v1 / v2: fades read as 0, transitions dropped after their old checks (id, type, duration ≥ 0 — a v2
    file rejected before is still rejected). v3: negative fades and invalid transitions are damaged; duplicate
    transition ids too; transitions are validated after every track is read (a clip problem is reported first).
  - Tests: `Project.Tests/FadeTransitionPersistenceTests` (round trip byte-identical, the v3 fields, v2 read without
    fades / transitions and saved as v3, an unanchored v2 transition dropped, v4 refused, 20 damaged cases, zones that
    exactly fill a clip); `Core.Tests/TransitionRulesTests`. Existing tests: `ProjectTestData`'s unanchored "fade"
    transition became an anchored dissolve (A|image, adjacent) plus fades on the video clip, the round-trip test asserts
    them; five assertions of the version a save writes changed 2 → 3. Mutations: transition validation off → 14
    failures; fades read for v1 / v2 too → 1 failure.
  - Verification: `dotnet build` 0 errors / 0 warnings; `dotnet test` (whole solution) 1827 passed, 2 skipped (4K
    heavy) — Core 415, Timeline 261, Project 319, UI 334, Export 82, Rendering 58, Video 291, ExportEndToEnd 67 (+2).
  - Interim (until the steps named): nothing renders or edits fades / dissolves yet. Timeline edits do not know
    transitions (10.6): moving, trimming, splitting or deleting a clip that a transition (only possible in a hand-made
    v3 file) is anchored to can leave a transition that makes the saved file fail to load. A split copies no fade to
    the right part yet (10.4 sets the rule).
- PO-8 decided (product owner, 2026-09-29): a fade on an edge that has a dissolve is not applied (picture and sound)
  while the dissolve exists; stored values kept, shown as inactive; applies again once the dissolve is gone; fades and
  the dissolve's opacity never multiply. Recorded in D025.
- Step 10.4 done (2026-09-29) — fades in Core, edits, Preview / export composition and mix.
  - Core: `Playback/FadeRule` (`Ramp`, `EffectiveFrames` with the PO-8 switches, `PictureFactor`, `RampEdges`) and
    `AudioFadeEnvelope` (`FirstSample`, `FadeInEnd`, `FadeOutStart`, `EndSample` by the `AudioPlacement` ceiling rule,
    `Gain`, `Affects`); `AudioMix.Add(samples, mix, gain, envelope, firstSample)` — `(float)(gain · g(k))`, the plain mix
    outside the ramps (bit for bit as before). Spans carry `FadeInFrames` / `FadeOutFrames` (picture, text, audio);
    `DiffersOnlyInPresentation` ignores them. `PictureLayer` / `TextLayer` take a `FadeFactor` (opacity = the clip's ×
    the factor; exactly the clip's without a fade), so `OccludesBelow` is false during a ramp. `NextPictureChange`
    includes `s + Fin` and `e − Fout`. The builder suppresses the fade on an edge with a dissolve (PO-8).
  - Mix paths: `MixEntry.Fade`, `AudioSpanReader.MixInto(…, fade)` with the timeline sample of each ring piece;
    `ExportAudioReader.MixIntoAsync(…, fade, ct)`; `ExportAudioSource` and `AudioPipeline` build the envelope from the
    snapshot's frame rate.
  - Edits: `FadeProperties` group (`ClipPropertyChange.Fade`, `ClipPropertyValues.Fade`, fields `FadeIn` / `FadeOut`,
    descriptions "Change Fade In" / "Change Fade Out" / "Change Fades"); the service stores whole frames
    (`FromFrame(ToNearestFrame)`), rejects negative and longer than the clip, keeps an unchanged fade as stored. Split:
    the right part keeps the fade out, the left the fade in — the left's fade out set to 0 in the same command
    (`EditPlan.SetProperties` → `SetClipPropertiesCommand` inside the split's composite). `CloneClip` copies fades.
  - Tests: `Core.Tests/FadeRuleTests` (19: ramps, product, clamp, PO-8, ramp edges, envelope boundaries at 25 / 30 /
    29.97 / 23.976, mix bit-identity and gains, layer opacity, occlusion, text, prefetch edges, presentation-only,
    audio clips, PO-8 in the snapshot and after the dissolve's removal); `Timeline.Tests/FadeEditTests` (10: whole frames,
    limits, merge / undo / redo, every clip kind, locked track, split inside a ramp with undo / redo, trim / move / speed
    / re-grid keep the stored fades); `Timeline.Tests/Playback/FadePlaybackTests` (3: prefetch before a fade out, both
    layers in the ramp with the fade opacity, a fade change keeps the decoders); `Export.Tests` — frame contract with
    fades at 29.97 / 23.976 / 25 and speeds 1× / 2× / 0.25× (the frame description now carries the layer opacity, so
    every existing frame contract also compares opacities), audio contract (fades at 1× / 0.25× / 2×, 200 % volume, a
    split inside the ramp, overlapping ramps, a muted clip) and a constant tone following the envelope sample by sample.
    `TimelineFixture.Snapshot` includes the fades, so every existing undo / redo exactness test covers them.
  - Mutations (each caught): no ramp edges in `NextPictureChange` → 1 (prefetch); the Preview's mixer ignoring the fade
    → 4 (audio contracts); split keeping the left fade out → 1; no PO-8 suppression → 1.
  - Verification: `dotnet build` 0 errors / 0 warnings; `dotnet test` 1866 passed, 2 skipped (4K heavy) — Core 434,
    Timeline 274, Project 319, UI 334, Export 89, Rendering 58, Video 291, ExportEndToEnd 67 (+2). The parity suite is
    unchanged and green: a project without fades renders exactly as before.
- Step 10.5 done (2026-09-29) — fades: UI and end-to-end parity. Accepted together with 10.4 (see below) after the
  product owner's real-app run of the fade scenarios 4–12b of `docs/PHASE10_MANUAL_TEST_PLAN.md`.
  - Inspector: a FADES section for every clip kind — `FadeInFrames` / `FadeOutFrames` (whole frames of the project
    rate; fractions and more than the clip rejected with a status message, the field shows the model again), the length
    as timecode, `MaxFadeInFrames` / `MaxFadeOutFrames` = max(clip frames, stored frames) so a stored fade longer than a
    trimmed clip is shown and never coerced into an edit; each field one `SetClipProperties` edit (merged per field,
    "Change Fade In" / "Change Fade Out"); inside the section the `EditingLock` disables them like the other fields.
    PO-8: `TimelineClipSelection` carries `DissolveAtStart` / `DissolveAtEnd` (from the track's transitions), the
    Inspector shows "Not applied: a dissolve is on this edge." under that field, the value stays.
  - Timeline: `TimelineClipViewModel.FadeInWidth` / `FadeOutWidth` (the effective ramp in pixels, clamped, 0 on an edge
    with a dissolve), refreshed with every relayout (zoom, edits, undo); the clip template draws them as black-to-clear
    gradient bands at the clip's edges.
  - Tests: `UI.Tests/InspectorFadeTests` (7: shown for every kind with the time, typing edits and merges, undo updates
    the fields, rejections, a stored fade longer than the trimmed clip shown without an edit, the editing lock, PO-8
    inactive notes and ramps in the timeline — gone with the dissolve —, ramp widths following fades and trim);
    `UI.Tests/FadeViewBindingTests` (the real `InspectorView` XAML: both NumericUpDowns show the frames, the PO-8 note
    visible only on the edge with the dissolve, a value typed in the control edits the clip); `ExportEndToEnd.Tests/
    ExportFadeEndToEndTests` (11, real ffmpeg: byte-equal Preview = export canvas on ramp frames for one layer, a video
    fading over another, 8 layers, 0.25× and 2×, image and text, a clip shorter than its fades, 23.976 and 29.97 fps;
    independently of the app a faded frame over black is ffmpeg's source frame × the ramp factor — max |Δ| 1; the PCM is
    the decoded tone × g(k) sample by sample and the AAC rises / holds / falls; cancelling inside a ramp leaves no file
    and no ffmpeg).
  - Mutations (caught): the picture fade off on both sides (Preview and export equal, so only the independent check can
    see it) → 2 failures.
  - Not checked in the real app in this step: the `TimelineView` ramp bands (a headless `TimelineView` needs Avalonia's
    platform — cursors — which the UI tests don't initialise; the widths are covered at the view-model level) and the
    fade scenarios as a whole — opening a generated project needs the native folder picker (unreliable through UI
    Automation in Phase 9) or a recovery file in the user's app-data folder; left to the manual run ("manual pending").
  - Verification: `dotnet build` 0 errors / 0 warnings; `dotnet test` 1885 passed, 2 skipped (4K heavy) — Core 434,
    Timeline 274, Project 319, UI 342, Export 89, Rendering 58, Video 291, ExportEndToEnd 78 (+2). Existing parity scenes
    unchanged and green.
  - Product owner (2026-09-29): the automated part of 10.4–10.5 accepted; the final acceptance waits for the real-app
    fade scenarios. For them (no production logic changed): `src/App/DevStartup.cs` — Debug builds only (`#if DEBUG`,
    absent from Release): `--open-project <folder>` opens that project when the main window is shown through the same
    `ProjectFileWorkflow.OpenAsync` the Open command uses after its picker; `tools/manual/New-Phase10FadeFixture.ps1` —
    ffmpeg media and a v3 `project.json` in `%TEMP%\aive-phase10-fades` (outside the repository; `-Force` replaces only
    a folder carrying its `.aive-fixture` marker). Checked: Debug and Release build 0 / 0; the app started with the
    option opened the fixture ("Opened project 'Phase 10 fades' … (4 media, 0 missing, 12 clips)", title "Phase 10
    fades — AI Video Editor"), analysed the media, closed clean without a save prompt.
- Steps 10.4 and 10.5 accepted by the product owner (2026-09-29). Real-app run of the fade scenarios 4–12b and the PO-8
  check (12c) with the fixture: all passed, no remarks on the fade implementation. Expected results corrected (no
  behaviour change, product owner): 12 — the export window is modal, so nothing in the main window can be changed
  during an export (the `EditingLock` on the FADES fields is a second line); 12a — a fraction (`2,5` / `2.5`), text or
  more frames than the clip has is not applied and the field shows the previous value when it loses focus, without a
  status message, like every numeric field (the control rejects such input before it reaches the view model).
- Step 10.6 done (2026-09-29) — dissolve: edits and validation. Awaiting the product owner's acceptance.
  - Core: `TransitionRules.Validate` over `TransitionSpec`s and clip edges (the planned state; `ValidateTrack` uses it),
    `TransitionRules.MaxFrames` (the longest F for given room and handles).
  - Timeline: `DissolveHandles` (`After`: `MaxWholeFrames` / `SpeedTiming.FramesFor` of the source after SourceIn minus
    the clip's frames; `Before`: the 1× trim-start limit / `FramesFor(SourceIn)`; images and text unlimited, unknown
    media duration none); `EditPlan` transition adds / removes / updates, `StateOf`, `EffectiveTransitions`,
    `IsTouched`, `ReconcileTransitions`, `TransitionTracks`, `BuildTransitionCommands`; commands
    `AddTransitionCommand`, `RemoveTransitionCommand`, `UpdateTransitionCommand` (track move, re-anchor, length; merges
    length-only changes); `TimelineEditService`: `AddTransition` (touching clips of one unlocked video track, one per
    cut, ≥ 2 frames, whole frames stored, longest-that-fits message), `RemoveTransition`, `SetTransitionDuration`
    (merged), `MaxTransitionFrames`; `Validate` reconciles then checks zones and the handles of touched dissolves;
    Move / Trim / Split / Delete / Speed / Add (re-grid) return the removal note; Split rejects inside a zone and
    re-anchors a dissolve at the clip's end to the right part; a far-edge trim is clamped so the other edge's zone part
    stays; a speed change removing a dissolve is one composite step; `TimelineValidator.ValidateSequence` also checks
    the transitions. `TimelineEditResult.TransitionId`. Inspector: a successful speed change reports its note.
  - Tests: `Timeline.Tests/DissolveEditTests` (21: add with exact undo / redo, every rejection, no handles, the longest
    that fits at 1× and 2× and at 29.97 with an odd F, images / text unlimited, remove, resize merged and limited, locked
    track, dissolves on both edges, move one / both / to another track, cut-edge trim, far-edge clamp, split inside /
    outside the zone with re-anchoring and undo, delete, speed of A (removed) and of B (rejected / allowed), re-grid
    keeps, an unrelated edit not rejected by a dissolve that lost its handles); `Core.Tests/TransitionRulesTests`
    (`MaxFrames`, 5 cases incl. maximality); `UI.Tests/InspectorFadeTests` (the speed change's note in the status bar).
    `TimelineFixture.Snapshot` includes the transitions, so every existing undo / redo exactness test covers them.
  - Mutations (each caught): reconciliation keeping a broken dissolve → 4; no re-anchoring on split → 1; no far-edge
    clamp → 1; no handle check → 1; handles checked for untouched dissolves → 1; split inside a zone allowed → 1.
  - Verification: `dotnet build` 0 errors / 0 warnings; `dotnet test` 1912 passed, 2 skipped (4K heavy) — Core 439,
    Timeline 295, Project 319, UI 343, Export 89, Rendering 58, Video 291, ExportEndToEnd 78 (+2).
  - The interim limitation of 10.3 (edits not knowing transitions) is resolved. Still open by plan: dissolves are not
    rendered (10.7) and have no UI (10.8).
- Step 10.6 accepted by the product owner (2026-09-29) on the automated checks; its real-app scenarios (14, 16, 17)
  follow 10.8.
- Step 10.7 done (2026-09-29) — dissolve composition in the Preview and the export. Awaiting acceptance (automated; the
  real-app dissolve scenarios after 10.8).
  - Core: `DissolveZone` (start, cut, end, F) on `VideoLayer.Dissolves`; `PictureSpan.ExtendedStart` / `ExtendedEnd` /
    `ShownStart` / `ShownEnd` (timing anchor unchanged); the builder derives both from the track's transitions (hidden
    tracks none; a transition not on a cut skipped). `LayersAt`: in a zone A (at the tick before the cut) below B (at
    the cut), B × `Ramp(j, F)` × its own fade, A with its own fade (PO-8 already off at the cut); A may occlude the
    tracks below, B never. `NextPictureChange` adds the zone edges; `DiffersOnlyInPresentation` compares the zones.
  - Export: `ExportPictureReader` serves the shown range. The Preview's `SpanReader` needed nothing (it never limited
    frames to the clip); readers are keyed by clip, so A and B of one track decode at once.
  - Sound: unchanged (PO-5) — a dissolve changes no audio span.
  - Tests: `Core.Tests/DissolveCompositionTests` (9: zone on the grid with odd F and the shown ranges, A below B with
    B's ramp per frame and B's opacity, occlusion by an opaque / transparent A, a zone under a partly covering upper
    clip, text as B, PO-8 with free-edge fades, zone edges as picture changes and a dissolve change not presentation-only,
    the audio spans unchanged, hidden tracks); `Export.Tests` — frame contract (23.976 / 29.97 × 0.25× / 1× / 4×: every
    output frame = the Preview playing and seeking, opacities included, and in the zone A and B of one split source
    show the same source frame — the handles; dissolves on both edges of a clip and on two tracks at once: 4 layers),
    audio contract (the export's samples with a dissolve = without, = the Preview's); `Timeline.Tests/Playback/
    DissolvePlaybackTests` (B's reader opened ahead of a zone longer than the prefetch window; in the zone A past its
    end and B before its start show the same source frame, B at its ramp); `ExportEndToEnd.Tests/
    ExportDissolveEndToEndTests` (8, real ffmpeg: byte-equal Preview = export canvas in every zone for video → video,
    2×, image and text neighbours, odd F at 29.97 with both edges, two tracks with a partly covering upper clip and an
    alpha image; independently a zone frame = ffmpeg's A frame (from the handle, 2n at 2×) and B frame blended B over A
    at p — max |Δ| 2; handles missing at render: the first frame held in both, the blend confirmed; PCM identical with
    and without the dissolve; cancel inside a zone).
  - Mutations (caught): the export reader not extended → 7 contract + 8 E2E failures; B without its ramp → 3 Core + 3
    E2E; the zone start not a picture change → the prefetch test (after the zone was made longer than the prefetch
    window — with a short zone the cut itself triggered the prefetch, so the first version didn't see it).
  - Verification: `dotnet build --no-incremental` 0 errors / 0 warnings; `dotnet test` 1939 passed, 2 skipped (4K
    heavy) — Core 448, Timeline 297, Project 319, UI 343, Export 97, Rendering 58, Video 291, ExportEndToEnd 86 (+2).
    The existing parity suite unchanged and green.
  - Limitations: no UI to create or edit dissolves yet (10.8) — until then they come only from a project file; the
    real-app dissolve scenarios 13–20 wait for 10.8.
- Step 10.7 accepted by the product owner (2026-09-29) on the automated checks.
- Step 10.8 done (2026-09-29) — dissolve UI. Awaiting the product owner's acceptance with the real-app scenarios
  13–20 (fixture `tools/manual/New-Phase10DissolveFixture.ps1`).
  - Timeline: `AddDissolveCommand` ("Dissolve" in the header; enabled for exactly two selected clips and no export;
    orders them by start, asks `MaxTransitionFrames` — null: "Select two clips that meet on a video track …" —, adds
    1 s or the longest that fits when shorter, selects the new dissolve, reports "Dissolve added: N frames[, the
    longest that fits here]." or the service's refusal); `TimelineTrackViewModel.Transitions` of
    `TimelineTransitionViewModel` (zone `[c − ⌊F/2⌋, c + ⌈F/2⌉)` in pixels, relaid out on every zoom / edit / undo);
    a dissolve selection exclusive with the clip selection (`OnTransitionPressed`, `HasTransitionSelection`,
    `TransitionSelectionChanged` with the longest that fits from the service); a selection whose dissolve is gone
    (undo, an edit that removed it) is dropped; `DeleteSelected` removes a selected dissolve ("Dissolve removed").
    View: the zones as an overlay of the track row (class `dissolve`, gold when selected), hit-tested before the clips.
  - Inspector: selection kind `Transition`; DISSOLVE section (the clips "A → B (V1)", Duration in whole frames with
    the timecode, range 2 … the longest that fits — never below the current length —, "Longest that fits here: N
    frames", Remove Dissolve); fractions reported, the service's refusals (locked track, too long) reported and the
    field shows the length again; disabled by the `EditingLock`. Shell: `TransitionSelectionChanged` → Inspector.
  - Tests: `UI.Tests/TimelineDissolveUiTests` (11: the command's availability with 0 / 1 / 2 / 3 clips and during an
    export; add selects and shows it, undo / redo without a stale selection; the longest that fits; no handles, no cut,
    locked track messages; the zone follows the cut, the zoom and the length; zone vs clip selection; Delete in one
    undo step; the Inspector's length merged, fractions, undo, Remove; its range and a locked refusal; the export lock;
    an edit removing the selected dissolve drops the selection); `UI.Tests/FadeViewBindingTests` (the real
    `InspectorView` XAML binds the DISSOLVE section). Mutations (caught): no re-raise of the dissolve selection on
    refresh → 4; Delete ignoring a selected dissolve → 1; no clamp to the longest → 1.
  - Checked: the dissolve fixture opens with `--open-project` ("Opened project 'Phase 10 dissolves' … (4 media, 0
    missing, 7 clips)"), closes clean. The timeline overlay itself is not checked headlessly (a `TimelineView` needs
    Avalonia's platform); it is part of the manual scenarios.
  - Verification: `dotnet build --no-incremental` 0 errors / 0 warnings; `dotnet test` 1951 passed, 2 skipped (4K
    heavy) — Core 448, Timeline 297, Project 319, UI 355, Export 97, Rendering 58, Video 291, ExportEndToEnd 86 (+2).
  - For the product owner: the Dissolve command's length when 1 s doesn't fit (the longest that fits is added and said)
    — confirm or choose "refuse" at the acceptance.
  - Fix after the product owner's manual run (scenario 13 stopped): with two clips selected the Dissolve button stayed
    disabled. Cause: the command's availability was announced only when `HasSelection` changed, and 1 → 2 selected clips
    keeps it true, so the button never re-queried `CanExecute`; the tests queried `CanExecute` directly and missed it.
    Fix: `UpdateSelectionVisuals` announces `AddDissolveCommand.NotifyCanExecuteChanged()` on every selection change.
    New test `The_button_is_told_whenever_the_command_becomes_available_or_not` follows `CanExecuteChanged` like a
    button (0 → 1 → 2 → 1 clips, export lock, clear); it fails on the old code. UI tests 356 passed.
  - Second manual round (product owner, 2026-09-29): 13 (A–F), 14 (A1–A3), 15, 16.1, 16.5–16.7, 17a, 18, 19, 20 passed.
    Five defects found and fixed (Core rules of 10.6 and the rendering of 10.7 unchanged):
    1. 16.2 — dragging both clips: the zone stayed at its place until the release. Cause: the move preview laid out
       only the clips. Fix: `UpdateMove` lays out every zone whose two clips are dragged with the drag's frame delta, and
       hides one whose cut the drag would open (`TimelineTransitionViewModel.IsVisible`, `Layout(…, frameDelta)`); a
       trim preview of a cut edge hides the zone (a far edge keeps it); the gesture's end or cancel shows them again.
    2. 16.3 / 16.4 — the clip's handle under a zone (A's end at the cut; A's start after it was trimmed to the zone's
       start) could not be dragged: the zones were an overlay that took the press. Fix: the zone overlay is not
       hit-testable; the view first hit-tests the clip and its trim handles, and only a press on a clip body (no handle,
       no Ctrl) inside a zone selects the dissolve (`TimelineViewModel.TransitionAt`).
    3. 17 — 4× on "bars" looked applied. The edit service refused it (checked on the saved fixture: "Can't change the
       speed: There is not enough media beyond the clips for the dissolve.", the clip unchanged), but the field kept
       showing 4: the Inspector reset the value while the control was still sending it, so the control ignored it, and
       losing the focus didn't help either — a binding doesn't pass on a value equal to the one it last sent. Fix: after a
       rejection the Inspector makes the fields show the model again right after the control's update
       (`Dispatcher.UIThread.Post`) and on every focus loss, by taking each numeric field through "no value" and back
       (`AnnounceFields`, under the sync guard). This applies to every numeric field of the Inspector.
    4. 17 — 2× on "pattern", then Undo: the dissolve didn't come back. Cause: the speed change that removed it was a
       separate, non-merging composite step; the next speed changes of the clip (the arrows, or retyping) were new steps,
       so one Undo went back only to the intermediate speed. Fix: `SetClipSpeedCommand` carries the removed dissolves
       (executed after the clip changes, undone before it goes back) and still merges with the next speed changes of the
       clip, keeping all of them; a chain that returns to the starting speed but removed a dissolve stays a step. (Ctrl+Z
       pressed while the Speed field has the focus is the field's own text undo — the 9.6 rule that no shortcut fires
       while typing —, not the app's Undo.)
    Tests: `TimelineDissolveUiTests` (+5: the zone follows a drag of both clips and hides for one, a trim preview hides it
    only when it opens the cut, the zone found by position, a refused speed reported with the field back at the clip's
    speed, one Undo after three Inspector speed changes brings the dissolve back), `FadeViewBindingTests` (+1: the real
    `InspectorView` Speed field — reproduced the stuck 4 before the fix — shows 1 again), `DissolveEditTests` (+2: a chain
    of speed changes is one undo step; back to the first speed keeps the step). Mutations (caught): the merged step
    dropped when back at 1× → 1; the removal not in the speed step → 3 + 2; the zone not moved in the drag preview → 1.
    Not testable headlessly: the handle-before-zone order of the press (view code-behind) — manual 16.3 / 16.4.
    Verification: `dotnet build --no-incremental` 0 / 0; `dotnet test` 1960 passed, 2 skipped (4K heavy) — Core 448,
    Timeline 299, Project 319, UI 362, Export 97, Rendering 58, Video 291, ExportEndToEnd 86 (+2).
  - Third round (product owner): fixes 1–4 confirmed. 5 worked with the app's Undo (after leaving the Speed field), but
    Ctrl+Z right after the speed change, with the focus still in the field, restored the speed without the dissolve.
    Cause: inside a text field Ctrl+Z is the field's own text undo (the 9.6 rule: no app shortcut while typing); it
    puts 1.00 back, i.e. a new speed change, and the step that removed the dissolve was kept. Fix (the 9.6 rule
    unchanged): a speed change that returns the clip to the speed its current speed step started from, when that step
    removed dissolves, undoes the step (`IUndoRedoService.NextUndo`, new, names the step the next Undo would undo) — the
    timing and the dissolves come back, status "The dissolve is back: its clips meet again."; any other speed keeps
    the dissolve removed. Tests: `DissolveEditTests` (the previous "back to the first speed keeps the step" became
    "… undoes the step and brings the dissolve back"; + another speed keeps it removed), `TimelineDissolveUiTests`
    (+1: 2× then 1.00 put back in the field). Mutation (the path off) → 1 + 1 failures. `dotnet test` 1962 passed,
    2 skipped (UI 363, Timeline 300).
- Step 10.8 accepted by the product owner (2026-09-29): the real-app dissolve scenarios 13–20 all passed, the fixes of
  16.2–16.4 and 17 re-checked. The Dissolve command's length confirmed: 1 s, or the longest that fits when 1 s doesn't
  (at least 2 frames; below that nothing is created). With it Steps 10.6 and 10.7 (their real-app scenarios are part
  of 13–20) are accepted too. Last 10.8 commit `f27c5c4`.
- Step 10.9 — final verification & closeout (2026-09-29), in progress.
  - Quality gates (local, at `10ba46b` + the 10.9 documentation): `dotnet build --no-incremental` (also with
    `-warnaserror`, as CI) 0 errors / 0 warnings; the full suite once and three times with `--blame-hang`: 1962 passed,
    2 skipped (4K), 0 failed every time (Core 448, Timeline 300, Project 319, UI 363, Export 97, Rendering 58, Video 291,
    ExportEndToEnd 86 + 2); the 4K scenes with `AIVE_HEAVY_TESTS=1`: ExportEndToEnd 88 / 88. No hang.
  - CI: not run — the branch is local (pushing waits for the product owner's command; PR #9 untouched).
  - Manual: the reduced formal run R1–R7 in `docs/PHASE10_MANUAL_TEST_PLAN.md` "Formal run (Step 10.9)" (the format
    scenarios 1 and 3, a fade re-check where 10.6–10.8 changed code — 4, 5, 9, 10, 11, 12a, 12c —, the export
    regression 1–8, 10–14); 13–20 not repeated (run by the product owner on the final code at 10.8). Pending.
  - Documentation: ARCHITECTURE (Phase 10 no longer "in progress"; the Effects project stays empty — fades and the
    dissolve live in Core / Timeline / UI; the zone hit-testing and the speed step of 10.8), D025 (the 10.6 speed-step
    bullet marked superseded by 10.8; the 10.8 acceptance), ROADMAP, this file. The Phase 10 checkbox of
    `docs/DEVELOPMENT_PLAN.md` waits for the product owner's acceptance.
  - Product owner's manual run (2026-09-30), a project made with the Phase 9 build and saved again in this build: R1,
    R2 passed; speed, split, fades (a typed value jumped back to 0) and the dissolve looked broken. Cause: the project
    had been opened a second time in the session (the log: opened 10:27:56 and again 10:28:42); `TimelineViewModel`
    kept its clip view models by clip id (since Phase 4) and reused those of the first load for the new clip objects
    with the same ids, so the timeline drew and handed the Inspector the old clips while the edits changed the new ones.
    Not the migration or format v3, not Phase 10 code: any project opened twice (or Save As → Open) had it. Fix:
    `GetClipViewModel` reuses a view model only for the same clip object; `OnProjectReplaced` drops the clip and dissolve
    view models and the dissolve selection. Test `UI.Tests/ReopenedProjectTimelineTests` (5: the timeline's clips are the
    open project's; speed resizes with undo / redo; split side by side with undo; a typed fade stays and is drawn; a
    dissolve added on the cut) — 4 fail without the fix (the dissolve one passes either way: the service works by id).
    Checked alongside: the fades that reached the model are in the product owner's MP4 (brightness follows both ramps);
    the project's only meeting clips use their whole media, so its dissolve refusal is the intended "not enough media".
    Verification: `dotnet build --no-incremental -warnaserror` 0 / 0; `dotnet test` 1967 passed, 2 skipped (UI 368);
    4K with `AIVE_HEAVY_TESTS=1` 88 / 88. The second manual run (S1–S6 in the manual plan) is pending.
  - Second manual run (product owner, 2026-09-30): S1 passed; S2 / S3 — after a split or a faster speed a fade longer
    than the clip stayed stored (500 frames on a shorter part) and the arrows could not lower it (one frame less was
    still longer than the clip, so the edit was refused). That was the accepted D025 §2 rule (scenario 8); the product
    owner changed it: an edit that leaves a clip shorter than a fade cuts the fade to the clip in the same step
    (`EditPlan.ClampFades` from `Validate` — trim, split, re-grid through `BuildCommand`; `SetClipSpeedCommand` carries
    the cuts with its dissolve removals as `Changes`, merged with the next speed changes; typing the first speed back
    restores them, "The dissolve is back" only when a dissolve came back). Files keep loading tick for tick (the
    round-trip gate): a longer fade saved before renders clamped and is cut by the clip's next length edit. Tests:
    `FadeEditTests` (split parts cut, trim cut + undo, move / slower speed unchanged, faster speed cut in one merged step
    with undo / redo, back to the first speed, lowering a cut fade by one frame), `InspectorFadeTests` (the trimmed clip
    shows the cut fade and the arrow lowers it; a longer fade from a file shown without an edit). Mutation: no
    `ClampFades` → 5 failures. The product owner stopped the manual run and asked for the testing to be done by Claude.
  - Real-app run by Claude (2026-10-01, Debug build, both fixtures; driven with UI Automation, real mouse / keys, the
    Windows file dialogs, screenshots, the app log; every input guarded to go only to the editor's window — a first
    attempt sent a few clicks and keys to the browser in front before the guard existed, reported to the product owner).
    Each fixture opened twice through Open (the product owner's situation). Passed: speed 2× / back / Undo / Redo
    (length on the timeline); Fade In typed and kept after reselecting, the band drawn; split inside a ramp (scenario 9:
    left 10 / 0, right 0 / 40, parts side by side, Undo → 40 / 40); speed 4× cuts 40 / 40 to 25 / 25, the down arrow
    24, 23, Undo steps back; 13 (25 frames, zone 3.52–4.52, "Longest that fits here: 51 frames"); 14; 14a; 15; 16 (bars
    moved alone removes, Undo; both moved together carry the zone; far-edge trim stops at 3.52; split inside rejected,
    outside kept; Delete of a clip and of the selected dissolve, Undo); 17 (4× refused with the field back at 1, 2×
    kept; 2× on A removes, Undo brings it back); 20 (Save, reopen: three dissolves of 25, Fade In 12, speed 2×, v3);
    18 export checked independently with ffmpeg — the fade in follows `(k+1)/(F+1)` (luma 24.2 / 66.2 / 117.0 / 125.4
    for frames 0 / 5 / 11 / 12, expected 24.4 / 66.3 / 116.6 / full), the zone blends bars over pattern with pattern
    continuing from its handle, the sound cuts 440 → 880 Hz exactly at 4 s; the Preview's frame at 4.0 s matches the
    MP4's frame 100; playback through the zones without warnings.
    Found and fixed: 13a — typing "99" in Duration (longest 51) left the dissolve at 9 frames, "2,5" at 2: Avalonia's
    NumericUpDown parses the text after every key, so the first digit was already an edit. `Controls/
    CommitNumericUpDown` (every Inspector field) parses only when the input is committed (Enter, leaving the field);
    the arrows still apply at once. Test `FadeViewBindingTests.Text_typed_in_a_field_is_applied_only_when_committed`
    (the real XAML; fails with the old control: 9 frames). In the app afterwards: 99, 2,5 and "ab" leave 30, the
    arrows give 32, Undo 25. Not defects: a click 5 px from a clip's end inside a zone takes the clip's trim handle
    (6 px, by the 10.8 rule); Ctrl+S right after typing in a field is the field's (9.6 rule) — Save works.
    Verification after all fixes: `dotnet build --no-incremental -warnaserror` 0 / 0; `dotnet test --blame-hang` 1973
    passed, 2 skipped (UI 370, Timeline 304); 4K 88 / 88.
  - Product owner (2026-10-01): scenario 8 (changed rule) and the Preview's sound by ear passed.
  - R7 — `docs/EXPORT_MANUAL_TEST_PLAN.md` 1–8, 10–14 run by Claude in the real app (2026-10-01): 13 pass, 0 fail, 9
    not run (optional); results per scenario in that plan's log. Scenario projects written as files (scratch script;
    the hidden track of 4 and the muted track of 5 are model-only, no UI control), everything else through the UI. The
    product owner's own app instance was open meanwhile; the automation was pinned to its own process id. No defect.
    Observed, as before: Cancel in "Replace file?" sets the status "Export cancelled."; "+ Text" on a project whose only
    video track is occupied at the playhead is refused (it needs a free spot on the top track).
  - Minimum clip length (product owner's question: an edge dragged down to "about one pixel"). Defined since Phase 4
    (D008: trims clamp to neighbours, the source and a one-frame minimum): `PlanTrim` clamps to `max(1, the dissolve
    zone part on the other edge)` frames, the target rounded to the nearest frame; `TimelineValidator` rejects a clip
    under one frame or off the grid; the view draws a clip at least `MinWidthPixels` = 2 px wide (one frame at 25 fps and
    50 px/s is 2 px, hence "about a pixel"). Not zoom dependent (the trim works in frames). Checked, no change needed:
    `Timeline.Tests/OneFrameClipTests` (10: both edges at 25 / 23.976 / 29.97 and 2× / 0.25× → exactly one frame on the
    grid, exact undo / redo; fades cut to 1 / 1 — factor 0.25 on that frame; a far-edge trim stops at the dissolve's zone
    part, the cut edge removes it; split refused; save / read back identical; the snapshot shows it on exactly its
    frame), `Export.Tests` (one-frame video at 1× and 2× and a one-frame text with cut fades: every export frame = the
    Preview's), `UI.Tests/TimelineTrimLayoutTests` (dragging past the other edge at 2, 50 and the maximum px/s → one
    frame, width max(2 px, one frame), undo). In the real app: Inspector Duration 00:00:00:01, undo / redo, the MP4 shows
    the clip on frame 0 only.
  - Verification at `ddf45df` (a clean worktree): `dotnet build --no-incremental -warnaserror` 0 / 0; `dotnet test
    --blame-hang` 1988 passed, 2 skipped (Core 448, Timeline 314, Project 319, UI 373, Export 99, Rendering 58, Video 291,
    ExportEndToEnd 86 + 2); 4K with `AIVE_HEAVY_TESTS=1` 88 / 88.
- Step 10.9 and Phase 10 accepted by the product owner (2026-10-01): R7 13 / 13, the one-frame minimum by D008, the
  results above. The temporary Phase 9 build (a worktree of `main` used for R1 / scenario 2) removed. The branch is
  pushed for CI; PR #9 is left as it is (not changed, not closed) and no new pull request is opened without the product
  owner's permission.
  (Historical record of 2026-10-01, no longer a current rule: later the same day the product owner merged PR #9 and
  then PR #10 into `main` — `2e758f1`; see Phase 11.)

## Phase 9 (complete)

Phase 9 — Quality: **complete** (accepted by the product owner on 2026-09-29; closeout `dcb86cb`, last Step 9.10 commit
`f27a4ab`; PRs #7 and #8, merged as `409240b`), branch `feat/phase-9-quality` (from `ab248e5`, `main` after the merge of PR #6).
Scope, steps and acceptance criteria: `docs/DEVELOPMENT_PLAN.md` "Phase 9 — Quality: steps"; decision D024.

### Phase 9 — Quality (complete)

Steps (D024; each accepted by the product owner before the next): 9.1 audit · 9.2 scope formalization ·
9.3 stability & error handling · 9.4 thumbnails + cache · 9.5 waveform · 9.6 hotkeys · 9.7 performance baseline &
optimization · 9.8 polish & cleanup · 9.9 CI / quality gates · 9.10 final verification & closeout.
Constraints: D023 unchanged; L1-c stays open (no MP4 → export canvas tolerance, Step 8.6 unchanged); out of scope
HDR / 10-bit, colour management, export quality presets, bitrate policy, hardware encoding, hardware decoding as a
required optimization, a full audio editor, configurable hotkeys, a large UI redesign.

- Step 9.1 done (2026-09-25) — audit, no change. Baseline on `ab248e5`: `dotnet build --no-incremental` 0 errors /
  0 warnings; `dotnet test` 1494 passed, 2 skipped (4K heavy), 0 failed (Core 380, Timeline 257, Project 259, UI 216,
  Export 78, Rendering 52, Video 185, ExportEndToEnd 67 + 2 skipped); ffmpeg 9.0.1, .NET SDK 8.0.424. Findings:
  - Thumbnails / waveform / cache: not implemented — `IThumbnailService` (DI registration commented out) and
    `IVideoEngine` (thumbnail / audio extraction) declared only; `AppPaths.ProjectCacheFolder` /
    `ProjectThumbnailsFolder` and `MediaAsset.ThumbnailPath` (serialized) exist; the Media Browser shows a colour swatch.
  - Error handling: `ErrorTranslator`; `LoggingBootstrapper` has `Area=Ffmpeg` → `ffmpeg-.log` and `Area=Export`
    sinks, nothing tags events with either; the damaged-project message mentions a backup in the cache folder, but
    `ProjectFileStore` calls `File.Replace` without a backup file.
  - Hotkeys: Ctrl+N/O/S/Shift+S, Ctrl+Z/Y/Shift+Z, Delete/Backspace, S, N, ←/→, Shift+←/→, Space, Home/End,
    Ctrl+= / Ctrl+− (`MainWindow.ShortcutFor`); the playback model plays forward at 1× only (D010/D011).
  - `PlaybackFrame.Picture` / `IsPictureCurrent`: filled by `PlaybackService`, read by no product code; 48 uses in
    8 test files.
  - Tests / CI: 8 xUnit test projects; no coverage tool, no CI, no `Directory.Build.props` / `.editorconfig`;
    ffmpeg tests skip silently without ffmpeg. Gates so far by practice: 0 warnings, full suite, `--blame-hang`,
    mutations, manual plans.
  - Carried from Phase 8: L1-c open; export throughput / memory / handles / Cancel latency not measured; the close
    hang; the single `Project.Tests` hang; the deferred `PlaybackFrame.Picture` cleanup.
- Step 9.2 done (2026-09-25) — scope formalized (documentation only): `DEVELOPMENT_PLAN.md` Phase 9 steps with
  scope, acceptance criteria (PR / QG / M / Impl), out of scope and dependencies; D024; ROADMAP. Awaiting the product
  owner's review.
- Step 9.2 accepted (2026-09-25).
- Step 9.3 — stability & error handling. Audit accepted (2026-09-25); product owner decisions: audio device B (check
  the default device's id at every Play and at the audio restart after a seek, recreate the output when it changed;
  no switch during playback), device failure during playback stays D013 (Stopwatch, sound may stop, the new device
  at the next Play/restart; no automatic recovery); backup A (correct the unused `ErrorTranslator` text, no backup,
  `ErrorTranslator` / `CorruptProjectFileException` kept — possible 9.8 cleanup); New during `ImportManyAsync` out of
  scope (known issue); analysis concurrency limit an implementation detail. Sub-steps, each accepted separately:
  9.3a close hang · 9.3b analysis cancellation + ffprobe termination + same-project reopen · 9.3c analysis concurrency ·
  9.3d FFmpeg diagnostics · 9.3e audio device · 9.3f backup message + final verification.
  - 9.3a done — close hang. Root cause (reproduced with a scratch copy of `Program.Main` and in the real app): the
    host was disposed synchronously (`using var host`) after the Avalonia lifetime had ended; `Host.Dispose` blocks on
    the services' async disposal, and `PlaybackService.DisposeAsync` posted its continuations (pipelines → readers →
    ffmpeg processes) to the `AvaloniaSynchronizationContext` of the stopped dispatcher — a deadlock whenever
    decoders were open. Fix: `MainWindowViewModel.PrepareToCloseAsync`, once closing is agreed, awaits
    `PreviewViewModel.ReleasePlaybackAsync` (stops polling, `IPlaybackService.DisposeAsync`) on the UI thread before
    the window closes; `PlaybackService` releases once (later calls return the same task) and is inert from the first
    line of the release (snapshots, Play, Pause, Seek ignored — no decoder or ffmpeg process can open again, also while
    the release is in progress); `Program.Main` clears the dead synchronization context before the host's disposal
    (defensive only). Tests: `Timeline.Tests/Playback/PlaybackReleaseTests` (3), `UI.Tests/CloseReleaseTests` (2).
    Mutations: no guard in `UpdateSnapshot` → 2 failures, in `SeekAsync` → 2, release not once → 1, shell not
    releasing → 2, preview polling after release → 1; the `Pause` guard is redundant (the release sets Paused first),
    not caught. Real app (UI Automation, a saved project with one video with sound): closing idle, playing, paused,
    stopped and after an export exits in 0.1 s, "Shutting down." logged, no ffmpeg left; with the fix alone (no
    safeguard) the same; with neither change the app hangs (control). Full suite with `--blame-hang`: 1499 passed,
    2 skipped (4K), 0 failed.
  - 9.3a accepted (2026-09-25).
  - 9.3b done — analysis generations, ffprobe termination, same-project reopen. `MediaAnalysisCoordinator`: a
    cancellation generation per project, replaced on `IProjectService.ProjectChanged` (New / Open / Recover all go
    through `Replace`, on the UI thread, before the new project's media are queued); the generation's token goes to
    `IMediaAnalysisService.AnalyzeAsync`; after the probe a cancelled generation's result is dropped whatever it is —
    no asset change, no `MediaAssetsChanged` (results are applied on the UI thread, so a result either belongs to the
    current project or is dropped). `RefreshDisplaySize` the same. The in-flight guard is per asset object
    (reference equality) instead of per id: the reopened project's assets (same ids, new objects) are analysed again
    — before, they were skipped as duplicates and stayed Pending for good (reproduced with a scratch program: now the
    reopened asset completes, the old result is dropped). `FfprobeMediaAnalysisService`: on cancellation, timeout or any
    other exit before ffprobe ended, the process tree is killed and awaited (up to 5 s) — before, only the `Process`
    object was disposed and ffprobe kept running; the timeout is an internal property (tests). Normal results and the
    JSON parsing are unchanged; no concurrency limit (9.3c). Tests: `UI.Tests/AnalysisGenerationTests` (11: New / Open /
    Recover / same-project reopen / orientation refresh of a replaced project — each with a probe that honours the
    cancellation and one whose result arrives after the switch — plus a refresh of the current project), 
    `Video.Tests/FfprobeTerminationTests` (2: cancellation and timeout end a hanging fake ffprobe and its child, in the
    media collection like the other ping-counting tests). Mutations (all caught): guard by id → 2 failures, no
    generation check on apply → 6, generation never cancelled → 10, refresh without the check → 1, token not passed to
    the probe → 6, ffprobe not ended → 2. `UI.csproj` gained `InternalsVisibleTo UI.Tests` (the coordinator's
    `IdleAsync` test hook). Full suite with `--blame-hang`: 1512 passed, 2 skipped (4K), 0 failed; build 0 warnings.
    Real app: open, play, close as in 9.3a — clean exit, nothing left running.
  - 9.3b accepted (2026-09-25).
  - 9.3c done — bounded analysis concurrency. `MediaAnalysisCoordinator.MaxConcurrentAnalyses = 4` (implementation
    detail, not configurable): a `SemaphoreSlim` slot is taken before the probe and held for the whole analysis, whose
    ffprobe runs (stream probe, then the orientation probe) are sequential — so at most 4 ffprobe processes run;
    orientation refreshes after Open share the slots. Waiting assets stay `Analyzing` (no new status), in queue order.
    The wait takes the generation token, so a replaced project's queued analyses end at once; after getting a slot an
    analysis checks its generation again before probing (found by a test: a slot freed by a cancelled analysis could
    reach a still-registered old waiter during `Cancel()` and start a probe for the replaced project). The slot is
    released in `finally`, only if taken. ffprobe's timeout starts inside the probe, i.e. after the slot. Choice of 4
    (scratch measurement, 24 generated files in the OS cache, 16 cores): 1 → 788 ms, 2 → 463, 4 → 312, 8 → 232, 24 →
    187 ms — beyond 4 the gain is small, while more processes compete with playback decoding and, on slow disks or
    shares, with each other for the 20 s timeout. Tests: `UI.Tests/AnalysisConcurrencyTests` (5: 25 queued → at most 4
    at once, all Completed with their own result; slots refilled over three rounds; cancel with 16 waiting — both
    probe behaviours — the waits end at once, no old probe starts, the next project completes, no slot lost;
    orientation refreshes share the limit), `Video.Tests/AnalysisConcurrencyIntegrationTests` (1, real
    `FfprobeMediaAnalysisService` with a scripted ffprobe whose orientation probe hangs: 6 analyses → exactly 4
    hanging ffprobe at once, the first 4 end after one timeout, the last 2 after two — the timeout counts from the
    slot —, all Completed with unchanged metadata, nothing left running; `Video.Tests` now references UI). Mutations
    (all caught): no limit → 5 UI failures and the real-ffprobe test (6 at once), wait not cancellable → 1, no check
    after the slot → 1, slot not released on a cancelled analysis → 2. Full suite with `--blame-hang`: 1518 passed,
    2 skipped (4K), 0 failed; build 0 warnings; analysis tests 10 × and the ffprobe tests 3 × in a row green.
  - 9.3c accepted (2026-09-25).
  - 9.3d done — FFmpeg / ffprobe diagnostics. Routing: one rule, `LogArea.ForSource` (Infrastructure), applied by an
    `AreaEnricher` that replaced the fixed `Area=App` property: sources `AiVideoEditor.Video.*` and the ffmpeg / ffprobe
    locators are `Area=Ffmpeg`, everything else `App`; an explicit `Area` (e.g. a scope) is kept. `ffmpeg-*.log` takes
    `Area=Ffmpeg`, the application log now excludes it (no duplicates; format unchanged); `errors-*.log` still collects
    errors of any area; `export-*.log` untouched (nothing writes to it). `LoggingBootstrapper.CreateLogger(folder)`
    overload for tests. `FfmpegProcess` (decoders and the export encoder): the command line at Debug when it starts (a
    start failure as a Warning), and one entry when stderr closes — Debug for a normal exit or when the app ended it
    (flag set before the kill in `DisposeAsync`: seek, end of playback, cancelled export, closing), Warning with the
    command line, exit code and the unconsumed stderr lines when it failed on its own. The decoder's and encoder's own
    Debug lines no longer repeat the command line. `FfprobeMediaAnalysisService`: `-v quiet` → `-v error` (stdout stays
    the JSON; stderr collected as it arrives, bounded to 4000 chars); Debug command line; a non-zero exit is a Warning
    with command line, exit code and stderr (then an internal `FfprobeFailedException`, so `AnalyzeAsync` doesn't log
    it twice); the timeout is its own Warning (with stderr so far); cancellation only Debug. Analysis outcomes and
    messages unchanged. `ExecutableLocator` messages unchanged, routed by their category. Tests:
    `Video.Tests/FfmpegDiagnosticsTests` (12: routing into the real sinks of a temporary folder — Video, locator and
    explicitly tagged events only in `ffmpeg-*.log`, others only in `app-*.log`, errors log unchanged, no export log;
    locator found / not found; a real failed ffmpeg in the ffmpeg log file with quoted command line, exit code and
    stderr; `FfmpegProcess` normal run → Debug only, own failure → one Warning with the data, ended by the app → Debug
    only; ffprobe failure (exit 3 + stderr), timeout, cancellation told apart; the real ffprobe's reason for a damaged
    file reaches the log; the real ffprobe with `-v error` gives the generated file's exact metadata). Mutations (all
    caught): no source routing → 4, ffmpeg events also in the app log → 4, app kill not recognised → 1, `-v quiet`
    again → 3, cancellation as a Warning → 1, timeout not a Warning → 1, failed exit not reported → 2. Real app
    (open → export → close): `ffmpeg-20260925.log` got the locator line, 6 ffmpeg command lines (decoders, both
    encoder passes), 4 normal exits, 2 "ended by the app", no warning; none of these in the application log. Full
    suite: 1 plain run with 1 failure in `Project.Tests` (not captured; the project references only Core and Project,
    which Step 9.3 doesn't touch), then 40 isolated `Project.Tests` runs and 8 full runs (1532 tests each) all green —
    recorded under the watched `Project.Tests` concern; build 0 warnings.
  - 9.3d accepted (2026-09-25).
  - 9.3e done — audio device (option B). `IAudioEndpoints` (Audio, internal; only `DefaultRenderDeviceId()` and
    `Open(deviceId, latency)` → an `IWavePlayer` that is also an `IWavePosition`), production `WasapiEndpoints`
    (`MMDeviceEnumerator` default render / multimedia endpoint id, `WasapiOut` on `GetDevice(id)`). `WasapiAudioOutput`
    asks for the default device at every `TryStart` — Play and the audio restart after a seek (the service's existing
    paths, `PlaybackService` unchanged) — and compares endpoint ids (ordinal, case-insensitive; names never used): the
    same id keeps the open output, another id (default changed, or the old device removed) closes the old output and
    opens one on the new default; no default device or one that can't be opened fails the start as before (the service
    plays on the Stopwatch). No switch during playback, no device notifications; a device lost while playing keeps D013
    (`HasFailed` → Stopwatch without a jump, silent until the next Play, which opens the default of that moment). The
    clock stays cumulative across a change of device. Logging names the device by id (the name is no longer looked up).
    Tests: `Video.Tests/AudioDeviceChangeTests` (12, fake endpoints and outputs — no real device is added, removed or
    switched: reuse on the same default; recreate after a change, old output closed once and never replayed; id
    compared by id (other letter case = same, another id = different); clock across a change; no default / unopenable
    default → start fails, nothing leaked; each output disposed exactly once; with `PlaybackService`: seek while playing
    after a change → new device, position from the seek; seek without a change → same output; device removed while
    playing → Stopwatch without a jump, nothing switches; next Play after a loss opens the current default (another or
    the same device), the failed output closed and never reused; unopenable new default → Stopwatch as before).
    `WasapiAudioOutputDeviceTests` (real device, production endpoints) and `AudioPlaybackServiceTests.
    DeviceFailure_FallsBackToTheStopwatch_WithoutAJump` still green. Mutations (all caught): default not compared → 4,
    always recreate → 3, old output not closed → 5, case-sensitive id → 1. Full suite with `--blame-hang`: 1542 passed,
    2 skipped (4K), 0 failed; build 0 warnings. Real app (real device): Play → Pause → Play opened the device once, by
    its endpoint id. Not verified on hardware: an actual default-device change or unplugging during playback (would need
    changing the system's devices) — covered only by the fake-device tests; a manual check belongs to the Phase 9
    manual test plan.
  - 9.3e accepted (2026-09-25).
  - 9.3f done — damaged-project message and closeout of 9.3. `ErrorTranslator`'s text for `CorruptProjectFileException`
    was "This project file appears to be damaged and couldn't be opened. A backup may be available in the project's
    cache folder." — a backup the app never makes (the translator is unused; what users see on Open is the
    `ProjectFileException` of `ProjectSerializer`, "The project file is damaged and can't be opened (reason).", which
    never promised one). Now: "This project file is damaged or can't be read, so the project couldn't be opened." No
    backup mechanism; `ErrorTranslator` / `CorruptProjectFileException` kept (possible 9.8 cleanup). Tests:
    `Video.Tests/ErrorTranslatorTests` (1: exact text, no "backup / cache / recover / restore / copy", the detail kept
    for the log; restoring the old text fails it), `Project.Tests/DamagedProjectMessageTests` (2: opening an unreadable
    and an incomplete `project.json` gives the damaged message without any backup promise). Documentation: D024
    "Refined in Step 9.3" (the decisions of 9.3a–f); `docs/PHASE9_MANUAL_TEST_PLAN.md` created with the 9.3 scenarios
    (close cases, analysis during New / Open / reopen, many imports, diagnostics, damaged project, audio device change
    and removal — the last four hardware ones manual-only, not executed); `docs/EXPORT_MANUAL_TEST_PLAN.md` no longer
    calls the close hang a known issue; DEVELOPMENT_PLAN / ROADMAP step status. Verification: see "Step 9.3 closeout".
  - Step 9.3 closeout (2026-09-25): 9.3a–f done, each sub-step accepted except 9.3f (awaiting, with the whole step).
    Residual, not fixed by 9.3: New while `ImportManyAsync` checks the picked files adds them to the new project (known
    issue, out of scope); the `Project.Tests` hang (Phase 8) / single unidentified failure (9.3d) — watched, no
    workaround; a real default-device change and a real device removal were not tried on hardware (manual-only
    scenarios 14–17 of the Phase 9 manual plan); `MediaAnalysisCoordinator` still relies on the captured UI
    synchronization context (its results are applied there — also what makes the 9.3b generation check race-free).
- Step 9.3 accepted and closed (2026-09-25), committed as `3006785`.
- Step 9.4 — thumbnails + cache. Audit accepted (2026-09-25); product owner decisions: PO-1 saved project cache
  `<project>/cache/thumbnails/`; PO-2 unsaved project `%LOCALAPPDATA%\AiVideoEditor\cache\unsaved\<projectId>\thumbnails\`;
  PO-3 source time `T = min(⌊Duration / 10⌋, 5 s)` in ticks, D009 decides the frame; PO-4 at most 2 thumbnail
  generations at once (fixed); PO-5 `MediaAsset.ThumbnailPath` kept for compatibility, never used or filled
  (`project.json` unchanged); PO-6 `<project>/cache/thumbnails` is the one cache path — the competing
  `AppPaths.ProjectThumbnailsFolder` (`<project>/thumbnails`) is fixed or removed within 9.4. Constraints: D009 selects
  the frame (no `-ss` / `select` / `thumbnail` filters), `IVideoDecoder` + `SourceFrameSelector` reused, playback and
  export never read the cache, cache hits take no slot and start no ffmpeg, a damaged cache file is a silent miss,
  internal binary format (magic / version / size + BGRA, no PNG), 160 × 90 bound. Sub-steps, each accepted separately:
  9.4a service and cache core · 9.4b cache location · 9.4c thumbnail queue · 9.4d Media Browser UI · 9.4e closeout.
  - 9.4a done — thumbnail service and cache core (no UI, no cache location, no queue). Core `IThumbnailService`
    replaced (the unused `GetOrCreateThumbnailAsync → path` contract): `TryGetCached(asset, cacheFolder)` (no decoding;
    online media only a thumbnail matching the file as it is now, offline media — marked missing or file gone — the
    last one cached without looking at the source) and `GetOrCreateAsync(asset, cacheFolder, ct)` (hit, or decode +
    atomic write; null for audio, unanalysed or failed analysis, offline, or an undecodable source; cancellation
    throws); `Thumbnail` (width, height, packed BGRA). Media `ThumbnailService`: `SourceTime(metadata)` =
    `min(⌊Duration.Ticks / 10⌋, 5 s)`; decode request = the sample point T (ticks from `StartTime`), nominal rate,
    160 × 90, software, `StrictEnd`; the first frame, then forward while the next frame `IsAtOrBefore` T — D009's
    last-at-or-before with hold-first / hold-last; frames packed. Cache file `{assetId:N}-{size:x}-{lastWriteUtcTicks:x}
    -v{rule version}.thumb` = `AIVT` + format version + width + height (little-endian) + BGRA; `TryRead` rejects
    anything else (miss); write to a temporary name + move, older files of the asset removed afterwards; a failed
    write only costs the cache. No ffmpeg specifics in the service. `Video.Tests` references Media (and Media gives it
    `InternalsVisibleTo`). Tests: `Video.Tests/ThumbnailServiceTests` (35 with theory rows, fake decoder: miss →
    decode + cache with the request checked, later hits and `TryGetCached` never decode; size, last-write time and rule
    version changes are misses that replace the old file; nine kinds of damaged cache files are misses and are
    repaired; atomic write, unwritable cache; offline with a cache → the cached one, never decoded, also for a file
    gone during the session; offline without a cache, audio, pending and failed analysis → none, not decoded;
    undecodable source; cancellation; the T rule incl. floor and cap; frame choice at exact boundaries, one tick
    before, a 3-frame clip, T after the last frame, a start time of 1.4 s; an image), `Video.Tests/
    ThumbnailIntegrationTests` (10, real ffmpeg, the expected frame from the generation recipe + D009: CFR 25 and
    29.97, VFR, jittered timestamps, MPEG-TS with a container start time, H.265 — all 160 × 90; a hit starts no ffmpeg;
    a 3-frame clip → frame 0, video ending at 0.2 s in a 10 s file → its last frame; a 320 × 240 image → 120 × 90; a
    video with a −90° display matrix comes out portrait with its left half on top). Mutations (all caught): no D009
    advance → 6, T = Duration/5 → 21, start time ignored → 2, size or time missing from the key → 1 each, missing flag
    ignored → 1, audio decoded → 1, older files kept → 3, hardware decoding → 1, no length check → 3. Full suite with
    `--blame-hang`: 1590 passed, 2 skipped (4K), 0 failed; build 0 warnings. `AppPaths` untouched (9.4b).
  - 9.4a accepted (2026-09-25), committed as `119adcd`.
  - 9.4b done — cache location in the project's life. Core `IThumbnailCacheLocation` (`CurrentFolder`, `Changed`,
    `CleanUpUnsavedAsync`); Project `ThumbnailCacheLocation`: saved → `<project>/cache/thumbnails` (`SavedFolder`),
    unsaved → `<unsaved root>/<projectId:N>/thumbnails`; the folder follows `IProjectService.Current` at every read.
    Hooks into the existing flow only: `ProjectChanged` (New / Open / Recover — a recovered project keeps its id, so an
    unsaved one finds its thumbnails, a saved one uses its folder) and `ProjectSaved` (first Save / Save As to another
    folder: the `*.thumb` files are carried over on the thread pool — moved out of the unsaved folder, which is then
    removed if empty; copied from another project folder, which keeps its cache; any failure is logged and only means
    regeneration — never affects the save). Carry-over into a folder that already has thumbnails (product owner option
    C, after a review of Save As over an earlier copy of the same project — same asset ids, so an offline asset could
    have shown an older variant), refined after a second review (several source variants of one asset used to be
    carried in file-name order, the last one winning — an outdated one when the current name sorts first, e.g. a size
    of 9 → 16 bytes, `-9-` / `-10-` — and a move then deleted both sources): the source files are grouped by asset
    (file name prefix before the first '-'); per group one current variant is chosen — the latest `LastWriteTimeUtc`
    (the order the service's offline lookup uses; format validity isn't checked here, Project doesn't reference
    Media), on a tie the ordinally last name; if any variant's time can't be read the group is skipped and logged. The
    chosen file is copied with overwrite; only after that copy succeeded are the asset's other thumbnails in the
    target removed (prefix `<assetId>-`) and, for a move, all of the asset's source variants deleted; a failed copy
    removes nothing of that asset anywhere; files of other assets and foreign files stay; the target folder is never
    cleared. Startup cleanup: `ProjectFileWorkflow.
    StartSessionAsync` (optional constructor dependency) runs `CleanUpUnsavedAsync` before the recovery offer: removes
    unsaved folders named by a project id without a recovery file (`RecoveryStore.PathFor`), keeps the current
    project's and anything not named by an id; best effort. `AppPaths`: `ProjectThumbnailsFolder` (`<project>/thumbnails`)
    removed, `UnsavedThumbnailCacheRoot` (`%LOCALAPPDATA%\AiVideoEditor\cache\unsaved`, not created) added;
    `ProjectCacheFolder` documented as the parent of the thumbnails folder. DI (App): `IThumbnailService` →
    `ThumbnailService`, `IThumbnailCacheLocation` → `ThumbnailCacheLocation(projects, RecoveryStore,
    AppPaths.UnsavedThumbnailCacheRoot)`. Comments of `Project.ProjectFolderPath` and `MediaAsset.ThumbnailPath` (unused,
    PO-5) corrected; no model or format change. A Save As over a folder that held an unrelated project leaves that
    project's thumbnail files there; they are never read (named by other asset ids). Tests:
    `Project.Tests/ThumbnailCacheLocationTests` (19: unsaved folder, nothing created by asking; unsaved projects never
    share; Open → `cache/thumbnails`, no `<project>/thumbnails`; Recover unsaved / saved; first save switches and moves,
    nothing of it in `project.json`; saving again changes nothing; Save As copies and the old project keeps its cache;
    a failing carry-over doesn't break the save and loses nothing; a carried file replaces a same-name target file;
    after the carry-over each asset has only its current thumbnail, other assets and foreign files untouched; a file
    that can't be carried (locked target) stays in the source and the target isn't cleaned for it; two source variants
    of one asset with different times — the current one sorting first and last by name — : a move carries only the
    latest and removes both sources, a copy from a saved cache carries only the latest and leaves the source as it was;
    a failed copy keeps every source variant and doesn't clean the target for that asset; an unreadable variant time
    keeps the whole asset where it is while other assets are carried; cleanup keeps
    recoverable, open and foreign folders and removes orphans; cleanup without a cache),
    `UI.Tests/StartupThumbnailCleanupTests` (1: startup removes the
    orphan, the recovered project uses and keeps its folder), `UI.Tests/ThumbnailCompositionTests` (2: the app's
    `AddAiVideoEditor` resolves both services, one singleton, the unsaved folder under `AppPaths`; `AppPaths` has no
    `ProjectThumbnailsFolder`). `UI.Tests` references App for the DI test (`Video.Tests` can't: App brings the Project
    namespace, which clashes with its `Project` type names). Mutations (all caught): saved cache at `<project>/thumbnails`
    → 5, one unsaved folder for all → 3, no carry-over → 2, cleanup ignoring recovery files → 1, Save As moving the old
    cache → 1, cleanup removing the open project's cache → 1, no cleanup at startup → 1; carry-over (option C): same
    name skipped → 1, older variants kept → 1, other assets removed too → 2, source removed before the copy → 3, target
    cleaned after a failed copy → 1; per-asset choice: oldest variant → 3, first by name → 1, last by name (the former
    order) → 3, a variant picked despite an unreadable time → 1, a move deleting only the carried variant → 1, sources
    removed before the copy → 6. Full suite with `--blame-hang`: 1612 passed, 2 skipped (4K), 0 failed; build 0 warnings. Real app: start, open, close clean, no
    warnings; nothing created in the project folder (no thumbnails are made yet — 9.4c/d).
  - 9.4b accepted (2026-09-28), committed as `79e6359`.
  - 9.4c done — `ThumbnailCoordinator` (UI/Services): makes and keeps the current project's thumbnails between the
    media, `IThumbnailService` and the UI (no bitmaps — 9.4d). Requests on `MediaAssetsChanged` (and at start) for
    video / images with completed analysis (made) and for offline media (a cached one only, never decoded); audio,
    pending and failed analysis get nothing — analysis stays `MediaAnalysisCoordinator`'s. Generations: one per project,
    replaced on `ProjectChanged` (New / Open / Recover), the old one cancelled; a cancelled generation's result is never
    stored and raises no event, also when its work ended after the switch (applied on the caller's context — the UI
    thread — like the analysis). Once per asset id and generation (in work, done or without a thumbnail — a failure
    isn't retried until the next project). Cache read (`TryGetCached`) first, on the thread pool, without a slot; a
    miss takes one of `MaxConcurrentGenerations = 2` slots (PO-4) — the wait ends with the generation — and runs
    `GetOrCreateAsync` on the thread pool. The cache folder is read from `IThumbnailCacheLocation` per request: Save /
    Save As change it for later requests and start no new generation. `ThumbnailReady` (asset id) + `Get(assetId)`
    for 9.4d. `ShutdownAsync` cancels everything and waits (≤ 5 s); `MainWindowViewModel.PrepareToCloseAsync` calls it
    after releasing playback (optional constructor dependency). DI: singleton. Tests: `UI.Tests/ThumbnailCoordinatorTests`
    (13, fake service counting concurrent makes, real project service: at most 2 at once and every asset its own
    thumbnail with one event each; one asset handled once over repeated media changes, also after it is ready; a cache
    hit takes no slot and is never made; eligibility incl. offline with / without a cache and an analysis completing
    later; a failing and an undecodable asset don't stop the others and aren't retried; a new project cancels the old
    work — service noticing the cancellation or finishing anyway — and publishes nothing of it; waits of the old project
    end at once, the new project is handled, no slot lost; a slot freed while the old project is cancelled starts no
    work for it; reopening a project handles its assets again (cache hit); shutdown cancels, waits and publishes
    nothing, nothing starts afterwards; closing the main window shuts the work down; a new cache folder is used for
    later requests without starting over); 10 × in a row green. Mutations: no limit → 6, no dedup → 3, cache hit
    taking a slot → 1, stale result published → 2, old generation not cancelled → 3, slot wait not cancellable → 1,
    shutdown not cancelling → 2, folder not read per request → 1, offline media decoded → 1, window close not shutting
    down → 1; the generation check after getting a slot is not caught (0 of 5): unlike 9.3c the work runs behind
    `Task.Run` and SemaphoreSlim hands a freed slot over asynchronously, so the waits are always cancelled before a slot
    of the cancelled project reaches them — a defensive check. Full suite with `--blame-hang`: 1625 passed, 2 skipped
    (4K), 0 failed; build 0 warnings. Real app (a saved project with one video): first open made one thumbnail
    (`cache/thumbnails/<id>-…-v1.thumb`, 57 616 B = 16 + 160 × 90 × 4; one 160-bound decode in `ffmpeg-*.log`), the
    reopen decoded nothing (cache hit); close clean, no ffmpeg left.
  - 9.4c accepted (2026-09-28), committed as `b6ca02a`.
  - 9.4d done — Media Browser shows thumbnails. `MediaBrowserItemViewModel.Thumbnail` (Core `Thumbnail`, null = the
    kind's colour tile) + `HasThumbnail`; `MediaBrowserViewModel` (optional `ThumbnailCoordinator`, injected by DI) gives
    rebuilt rows `coordinator.Get(asset.Id)` and updates a row on `ThumbnailReady` (the coordinator only reports the
    current project). View: the 56 × 32 colour tile stays as background/placeholder, an `Image` (`Stretch=Uniform`) over it
    bound through `UI/Rendering/ThumbnailBitmapConverter` — BGRA → `WriteableBitmap` with the Preview's `FrameBitmap`,
    one bitmap per `Thumbnail` instance (`ConditionalWeakTable`), so the rows rebuilt on every media change reuse it. No
    layout redesign. Tests: `UI.Tests/MediaBrowserThumbnailTests` (3: a row gets its thumbnail when ready and keeps the
    same instance after a rebuild; audio and offline without a cache keep the tile, offline with a cache shows it;
    another project starts without the previous thumbnails), `Rendering.Tests/ThumbnailBitmapConverterTests` (2: size
    and exact BGRA pixels incl. alpha; same thumbnail → same bitmap, null → none). Mutations (all caught): ready events
    ignored → 3, rebuilt rows losing the thumbnail → 1, a new bitmap per rebuild → 1. Full suite with `--blame-hang`:
    1630 passed, 2 skipped (4K), 0 failed; build 0 warnings. Real app: the opened project's video shows its thumbnail in
    the Media Browser (screenshot checked).
  - 9.4d accepted (2026-09-28), committed as `197e99e`.
  - 9.4e done — closeout of 9.4 (documentation only, plus one comment). D024 "Refined in Step 9.4": PO-1–PO-6 and
    option C of the carry-over, the interface (Core `IThumbnailService`, Media implementation over `IVideoDecoder` +
    `SourceFrameSelector`, `IVideoEngine` not used), cache key and format, location, queue, Media Browser, and what is
    left as it is. `docs/PHASE9_MANUAL_TEST_PLAN.md`: section "Step 9.4" (scenarios 18–30: thumbnails appear,
    deterministic frame, reopen from the cache, changed source, damaged cache, offline with / without a cache, unsaved
    project and first Save, Save As, recovery and startup cleanup, New / Open and close while thumbnails are made, a
    portrait video, playback / export untouched) and the status "app 9.4". `docs/DEVELOPMENT_PLAN.md`: 9.3 marked
    accepted, 9.4 done with the chosen rule, key, location and interface noted per item (offline media refined: its
    cached thumbnail, if any). `ROADMAP.md` "Current". `ARCHITECTURE.md`: media pipeline no longer calls `IVideoEngine`
    the thumbnail interface; new section "Thumbnails (Phase 9 Step 9.4)"; verification note. `README.md`: the lines
    that called thumbnails future work (`Video/`, `Media/`, "FFmpeg behind Core interfaces"). `IVideoEngine`'s comment no
    longer reserves it for thumbnails. Not touched: other stale README lines (Export "Empty" / "not implemented yet") —
    not 9.4's; a 9.8 / 9.10 item.
  - Step 9.4 closeout (2026-09-28): 9.4a–e done, 9.4a–d accepted, 9.4e awaiting with the whole step. Verification:
    `dotnet build --no-incremental` 0 errors / 0 warnings; full suite with `--blame-hang`: 1630 passed, 2 skipped (4K),
    0 failed (Core 380, Timeline 260, Project 280, UI 253, Export 78, Rendering 54, Video 258, ExportEndToEnd 67 + 2);
    the 85 thumbnail tests (`~Thumbnail` in UI 19, Video 45 incl. 10 with real ffmpeg 9.0.1, Project 19, Rendering 2)
    11 × in a row green; parity suite unchanged. Real app: start → close window clean (67 ms, no ffmpeg left, no
    errors); with media, the 9.4c / 9.4d checks (one decode on first open, none on reopen, thumbnail shown). The formal
    manual run of scenarios 18–30 is Step 9.10's. Residual (D024 "Left as they are"): no cache limit / eviction; a
    failed thumbnail retried only with the next project; media coming back online not re-checked; an unrelated
    project's thumbnail files left in a Save As target; the defensive post-slot generation check unreachable by
    mutation.
- Step 9.4 accepted and closed (2026-09-28): 9.4d committed as `197e99e`, 9.4e as `e666b48`.
- Step 9.5 — waveform. Audit (2026-09-28): no waveform code (only a mention in `Audio/ModuleInfo.cs`). Building
  blocks: Core `IAudioDecoder` / `FfmpegAudioDecoder` (48 kHz stereo float, exact `FirstSampleIndex`; from source
  position 0 one ffmpeg run from the file's start), `AudioPlacement` / `AudioTiming` (the exact timeline ↔ source
  sample mapping at any speed, D022), `PlaybackSnapshotBuilder`'s audible sources (`AudioClip`; `VideoClip` whose media
  has an `AudioCodec`; clips on muted tracks left out, muted clips kept at zero gain), clip volume 0–200 % linear and
  mute (Inspector), track mute (model only, no UI); timeline clips are 36 px `Border`s with a label, zoom 2 px/s –
  40 px/frame (2400 px/s at 60 fps) — a long clip is millions of pixels wide at the top zoom, so the display must draw
  only what is visible; the 9.4 cache location (`ThumbnailCacheLocation`) handles `*.thumb` in `cache/thumbnails` only.
  Product owner decisions (2026-09-28, all as proposed): PO-W1 video clips with sound show a waveform too, in the lower
  part of the clip; PO-W2 height linear in the clip's volume (200 % reaches the clip's edge), a muted clip or track
  dimmed with the same shape, envelope `max(|L|, |R|)` on a linear scale; PO-W3 waveforms are made only for media used
  by a clip on the timeline (not at import); PO-W4 `<project>/cache/waveforms` and
  `%LOCALAPPDATA%\AiVideoEditor\cache\unsaved\<projectId>\waveforms`, the thumbnails' life cycle (first Save moves,
  Save As copies, startup cleanup) and key, at most 2 made at once, separate from thumbnails; PO-W5 (2026-09-28, after
  9.5a) offline media: the timeline may show a waveform cached earlier in `cache/waveforms`, never decoding or making
  one; without a cached one it shows none (refines the plan's "offline shows no waveform"). Sub-steps, each accepted separately: 9.5a peak service and
  cache core · 9.5b cache location · 9.5c waveform queue · 9.5d timeline display · 9.5e closeout.
  - 9.5a done — waveform service and cache core (no UI, no cache location, no queue, no DI registration yet — as
    9.4a). Core `IWaveformService` (`TryGetCached(asset, cacheFolder)` never decodes; `GetOrCreateAsync(asset,
    cacheFolder, ct)`) and `Waveform` (samples per peak, sample count, one byte peak per started group of source
    samples; `ToPeak` = `⌈|a|·255⌉` capped at 255 — rounded up so any sound is visible; `MaxPeak(from, to)` over the
    groups a source range touches, 0 outside `[0, SampleCount)` — for the display). Media `WaveformService`: the file's
    audio decoded once by `IAudioDecoder` from source position 0, 1×, `StrictEnd`, the file's start time as origin;
    samples placed by the stream's `FirstSampleIndex` (before 0 dropped, a later start leaves silent groups); peak =
    `max(|L|, |R|)` per 256 samples (187.5 per second); the waveform ends where the audio ends. Media: audio files and
    video with an `AudioCodec`, analysis completed; images, silent video, pending / failed analysis → none, not
    decoded; offline (marked missing or file gone) → the last cached one, never decoded. A decode failure (also midway)
    → none, nothing cached; cancellation throws. Cache file `{assetId:N}-{size:x}-{lastWriteUtcTicks:x}-v1.peaks`:
    `AIVW` + format version + samples per peak + sample count (little-endian) + peaks; anything else is a miss
    (repaired). The cache key, the atomic write and the offline lookup moved from `ThumbnailService` into
    `Media/Caching/SourceFileCache` and are used by both services (thumbnail behaviour unchanged — its 45 tests green).
    Test fake: `FakeAudioSource` got an optional `Shape` (sample index → L, R). Tests: `Core.Tests/WaveformTests` (15:
    peak count, validation, the peak scale incl. rounding up, capping and NaN, `MaxPeak` over groups / edges / empty
    ranges), `Video.Tests/WaveformServiceTests` (29 with theory rows, fake decoder: peaks of both channels per group
    incl. a partial last group; the decode request; samples before the start dropped; a later stream start; empty
    audio; which media; hit / miss / size / time / rule version; thumbnails and other assets left alone; nine kinds of
    damaged files repaired; offline with / without a cache, a file gone during the session; open and midway failures;
    cancellation; an unwritable cache), `Video.Tests/WaveformIntegrationTests` (4, real ffmpeg 9.0.1: PCM tones —
    silence, 0.5 left / 0.2 right, 0.8 right only — exact per group; AAC in MPEG-TS starting at 10 s — the onset
    within 8 groups of 1 s; a video with sound; a silent video never decoded; a hit starts no ffmpeg). Mutations (all
    caught): no start time → 2, stream start ignored → 2, left channel only → 4, peak rounded down → 7, not strict → 1,
    no cache read → 2, offline decoded → 3, silent video decoded → 2, older files kept → 4, no length check → 2,
    `MaxPeak` missing the last group → 1. `dotnet build --no-incremental` 0 errors / 0 warnings; full suite with
    `--blame-hang`: 1678 passed, 2 skipped (4K), 0 failed (Core 395, Timeline 260, Project 280, UI 253, Export 78,
    Rendering 54, Video 291, ExportEndToEnd 67 + 2). No real-app check: nothing user-visible yet (9.5d).
  - 9.5a accepted (2026-09-28), committed as `732f0a7`.
  - 9.5b done — waveform cache location and life cycle (PO-W4). Core: `IMediaCacheLocation` (`CurrentFolder`,
    `Changed`, `CleanUpUnsavedAsync` — the former members of `IThumbnailCacheLocation`), `IThumbnailCacheLocation` and
    the new `IWaveformCacheLocation` derive from it (consumers and fakes unchanged). Project: the logic of 9.4b's
    `ThumbnailCacheLocation` moved unchanged into the abstract `MediaCacheLocation` (kind folder, file pattern, log
    name); `ThumbnailCacheLocation` (`thumbnails`, `*.thumb`) and `WaveformCacheLocation` (`waveforms`, `*.peaks`) are
    its two kinds — saved `<project>/cache/<kind>`, unsaved `<unsaved root>/<id>/<kind>`, carry-over on the first Save
    (move) / Save As (copy, one current variant per asset), each kind touching only its own folder and files. Two
    changes of the shared code, so the kinds don't disturb each other: a move removes the unsaved `<id>` folder only
    once nothing else is left in it (was: once the thumbnails folder was empty — same result with one kind), and the
    startup cleanup removes an orphan's kind folder and then the `<id>` folder if empty (was: the whole `<id>` folder).
    `ProjectFileWorkflow.StartSessionAsync` also cleans up the waveform cache (optional constructor dependency, after
    the thumbnails', before the recovery offer). DI (App): `IWaveformService` → `WaveformService`,
    `IWaveformCacheLocation` → `WaveformCacheLocation(projects, RecoveryStore, AppPaths.UnsavedThumbnailCacheRoot)`
    (the unsaved root of both kinds; name kept, comments of `AppPaths` updated). Tests: `Project.Tests/
    WaveformCacheLocationTests` (12, both locations side by side: unsaved folder next to the thumbnails', nothing created;
    Open; Recover unsaved / saved; the first save moves each kind into its own folder and removes the `<id>` folder;
    each kind carries only its own files; the `<id>` folder stays while the other kind still has something; Save As
    copies and the old project keeps its own; Save As over an earlier copy leaves one current waveform per asset and
    no thumbnail touched; a failing carry-over doesn't break the save; cleanup removes an orphan's waveforms, keeps its
    thumbnails until their own cleanup, keeps recoverable / open / foreign folders; cleanup without a cache),
    `UI.Tests/WaveformCompositionTests` (1: the app's composition resolves the service and the location, singletons, the
    unsaved folder next to the thumbnails'), `UI.Tests/StartupThumbnailCleanupTests` (+1: startup removes both caches of
    an orphan, the recovered project keeps its waveforms). The 19 `ThumbnailCacheLocationTests` and the other thumbnail
    tests unchanged and green. Mutations (all caught): waveforms in the thumbnails folder → 13, every file carried → 2,
    cleanup removing the whole `<id>` folder → 1, a move removing the whole `<id>` folder → 1 (the first version of that
    test used a locked file, which also stopped the mutated delete — replaced by a file the other kind doesn't carry),
    first save copying instead of moving → 7, no waveform cleanup at startup → 1, location not registered → 1.
    `dotnet build --no-incremental` 0 errors / 0 warnings; full suite with `--blame-hang`: 1692 passed, 2 skipped (4K),
    0 failed (Core 395, Timeline 260, Project 292, UI 255, Export 78, Rendering 54, Video 291, ExportEndToEnd 67 + 2).
    Real app: start (startup cleanup of both kinds) → close window clean (75 ms, no ffmpeg left, no errors); nothing
    user-visible yet (9.5d).
  - 9.5b accepted (2026-09-28), committed as `babfbd5`; `AppPaths.UnsavedThumbnailCacheRoot` keeps its name
    (technical debt, 9.5e / 9.8).
  - 9.5c done — `WaveformCoordinator` (UI/Services), the waveform queue. The orchestration of 9.4c's
    `ThumbnailCoordinator` moved unchanged into the abstract `MediaCacheCoordinator<T>` (generations cancelled on
    `ProjectChanged` with their results dropped, once per asset id and generation, cache read on the thread pool without
    a slot, making on the thread pool with one of the coordinator's own slots — a `SemaphoreSlim` per instance, so the
    two kinds never share or hold up each other's limit —, results on the caller's context, `Ready` + `Get`,
    `ShutdownAsync` ≤ 5 s); `ThumbnailCoordinator` is its thumbnail kind (API unchanged: `ThumbnailReady`,
    `MaxConcurrentGenerations`; its 13 tests and the Media Browser's green). `WaveformCoordinator` (PO-W3 / W4 / W5):
    candidates are only the media a clip on the timeline uses (any track, also hidden or muted — a muted clip is still
    drawn, dimmed), requested on `TimelineChanged`, `MediaAssetsChanged` (an analysis completing) and at start; made for
    audio and for video with an `AudioCodec` whose analysis completed; offline media only reads the cache (never made) —
    unless its saved metadata says it has no sound; at most 2 made at once. A clip removed from the timeline keeps its
    asset's waveform and work (an undo may bring it back). `WaveformReady` (asset id) + `Get(assetId)` for 9.5d.
    `MainWindowViewModel.PrepareToCloseAsync` shuts it down after the thumbnails (optional constructor dependency). DI:
    singleton; resolved with the main window, so it runs from the start (nothing is drawn until 9.5d). Tests:
    `UI.Tests/WaveformCoordinatorTests` (15, fake service counting concurrent makes and holding them, real project
    service: only media on the timeline — nothing at import —, an opened project's timeline handled (cache hit), a
    removed clip keeps its waveform; at most 2 at once and one event per asset; busy thumbnail slots never hold up the
    waveforms and free none of theirs; once per asset over repeated timeline / media changes and a second clip; a cache
    hit takes no slot; silent video, images, pending and failed analysis get none, an analysis completing later is
    picked up; offline: cached shown, none without, never made, known-silent not even read; a failure isn't retried and
    doesn't stop the others; a new project cancels and publishes nothing of the old one (service noticing the
    cancellation or not) and the new one is handled; shutdown; closing the main window; a new cache folder for later
    requests), `UI.Tests/WaveformCompositionTests` (+1: one coordinator, a singleton). The limit in the tests is the
    product decision (2), not the constant. Mutations (all caught): every imported media → 1, timeline changes ignored →
    13, limit 1 → 7 (first survived: the tests read the constant — now the literal 2), one pool for both kinds → 8,
    offline decoded → 1, offline known-silent read → 1, silent video made → 1, no dedup → 3, cache hit taking a slot → 1,
    old generation not cancelled → 2, stale result published → 1, window close not shutting down → 1, coordinator not
    registered → 1. `dotnet build --no-incremental` 0 errors / 0 warnings; full suite with `--blame-hang`: 1708
    passed, 2 skipped (4K), 0 failed (Core 395, Timeline 260, Project 292, UI 271, Export 78, Rendering 54, Video 291,
    ExportEndToEnd 67 + 2). Real app: start (the coordinator now runs from the start) → close window clean (67 ms, no
    ffmpeg left, no errors). Not checked in the real app: a waveform actually made for a project's timeline — driving
    the native folder picker (Open) through UI Automation from this session failed (the modal dialog blocked it);
    nothing is drawn yet, so that check comes with 9.5d.
  - 9.5c accepted (2026-09-28), committed as `a67b900` (a removed clip keeping its waveform confirmed as intended).
  - 9.5d done — waveforms on the timeline (PO-W1–W5). `UI/Common/WaveformLayout` (the display rule, no drawing):
    `ClipWaveform` — an immutable value per clip: the asset's `Waveform`, the clip's timeline start / end, source in,
    speed, volume, muted (clip or track), lower half (video) and the zoom; `Fraction(peak, volume)` =
    `peak / 255 · volume / 2`, capped at 1 — linear in the volume, a full-scale peak takes the full height at 200 % and
    half of it at 100 %; `Column(clip, c)`: clip-local pixel column c covers timeline samples
    `[⌊c·48000/pps⌋, ⌊(c+1)·48000/pps⌋)` from the clip's first sample, mapped to the source by the clip's
    `AudioPlacement` (the rule of playback and export — trim, position, speed ≠ 1×), and shows the largest peak of
    that source range (`Waveform.MaxPeak`); 0 outside the clip or after the sound. `TimelineClipViewModel.Waveform`
    (`ClipWaveform?`) + `HasWaveform`. `TimelineViewModel` (optional `WaveformCoordinator`, injected by DI) gives every
    clip its value on each refresh / relayout (edits, undo, zoom) and on `WaveformReady`: audio clips over the whole
    clip, video clips in the lower half, from `coordinator.Get(assetId)` — none for text, images, silent video (never
    made), offline media without a cached waveform (PO-W5); dimmed when the clip or its track is muted (PO-W2); equal
    values are not raised again. `UI/Rendering/WaveformView` (Control, not hit-testable): a filled polygon symmetric
    around the centre line of its area, white at 50 % alpha, muted at 19 %; computes and draws only the columns inside
    the timeline `ScrollViewer`'s viewport (redrawn on `ScrollChanged`) — a clip is up to millions of pixels wide at the
    top zoom. `TimelineView.axaml`: the view inside each clip's grid, under the label, over all three columns (the trim
    handles stay on top). Its column 0 is the clip border's inner edge — 1–2 px (the border) right of the clip's
    timeline x. Tests: `UI.Tests/WaveformLayoutTests` (17: the volume rule incl. 100 % / 200 % / 0; 1× columns; a
    trimmed clip anywhere on the timeline; 2× and 0.5×; zoomed out; nothing outside the clip or after the sound; the
    volume scaling; only viewport columns, six positions), `UI.Tests/TimelineWaveformTests` (8, real project, edit
    service and coordinator, fake service: audio clip with timing and volume; video with sound lower half, silent
    video / image / text none and only one made; made later appears on both clips of the asset; clip and track mute
    dimmed with the same data; trim / move / speed / zoom followed; unchanged values not raised; offline cached shown,
    none without, nothing made; no coordinator, no waveform), `Rendering.Tests/WaveformViewTests` (4, rendered with
    Avalonia: audio whole height where there is sound and nothing where silent; video lower half only; half height at
    100 %; muted same shape, dimmer). Mutations (all caught): 100 % as the full height → 9, speed ignored → 1, source
    in ignored → 1, one peak per column → 2, track mute ignored → 1, video over the whole clip → 1, clip volume ignored
    → 1, ready ignored → 4, muted not dimmed → 1, lower half ignored in drawing → 1, viewport not clamped → 4.
    Real app (a generated project: V1 `tone.mp4` 10 s with AAC — 0–2 s silence, 2–6 s a tone at 0.25, 6–10 s at 0.9 —
    and `silent.mp4` without sound; A1 `tone.wav` (the same sound) at 2× and a muted `tone.wav` clip from source 2 s;
    no saved metadata, so Open analysed first; driven through UI Automation, window screenshots checked):
    1) first Open: `cache/waveforms` got one `.peaks` for `tone.wav` and one for `tone.mp4` (1 895 B = 20 + 1 875 peaks
    for 10 s), none for `silent.mp4`; `ffmpeg-*.log`: one 1× full-file audio decode per file with sound (the two other
    audio starts at the same moment are the Preview's — one with `atempo=2` for the 2× clip), no audio decode of
    `silent.mp4`; 2) shown: `tone.mp4` in the lower half — nothing to 2 s, thin to 6 s, thicker to 10 s; `silent.mp4`
    none; the 2× clip silent to 1 s, thin to 3 s, thick to 5 s; the muted clip dimmed, thick from 10 s (source 6 s);
    3) reopen: only the Preview's two audio starts, no waveform (or thumbnail) decode, the `.peaks` files unchanged
    (same last-write times), the same picture; 4) offline (media folder renamed): the cached waveforms shown, no ffmpeg
    at all; offline with the WAV's `.peaks` removed: its clips show none, the video its cached one; zoomed in 12 steps
    and scrolled to 5.5–8.4 s: the waveform drawn across the viewport, the loudness step exactly at 6.00 s. Every run
    closed cleanly, no ffmpeg left. Seen, as decided (PO-W2): at 100 % a loud tone (0.9) takes under half of the
    height and a quiet one (0.25) about an eighth — with the 19 % alpha a muted quiet clip is barely visible.
    `dotnet build --no-incremental` 0 errors / 0 warnings; full suite with `--blame-hang`: 1737 passed, 2 skipped (4K),
    0 failed (Core 395, Timeline 260, Project 292, UI 296, Export 78, Rendering 58, Video 291, ExportEndToEnd 67 + 2).
  - 9.5d accepted (2026-09-28), committed as `80d3748`; the height at 100 % (PO-W2), the 1–2 px border offset and the
    waveform during a start-trim drag stay as they are.
  - 9.5e done — closeout of 9.5. Documentation: D024 "Refined in Step 9.5" (PO-W1–W5, the data and service, the shared
    location and queue, the display rule, what is left as it is); `docs/PHASE9_MANUAL_TEST_PLAN.md` section "Step 9.5"
    (scenarios 31–43: only timeline media, audio clip and video with sound, silent video / images / text, volume, mute,
    trim / move / split / undo, speed, zoom and scroll, reopen from the cache, offline with and without a cache,
    unsaved project / Save / Save As / recovery, New / Open / close while waveforms are made, playback and export
    untouched) and the status "app 9.5"; `ARCHITECTURE.md` new section "Waveforms (Phase 9 Step 9.5)", the Thumbnails
    section points to the shared location / coordinator, verification note; `docs/DEVELOPMENT_PLAN.md` 9.5 done;
    `ROADMAP.md` "Current"; `README.md` (waveforms next to thumbnails); stale comments of `Audio/ModuleInfo`
    ("waveform generation" there) and `IVideoEngine` corrected. One code fix found by the final verification: the
    waveform and thumbnail tests repeated 10 × failed once (`ThumbnailCoordinatorTests.Shutdown_cancels_everything…`,
    `RunningCount` 1 after `ShutdownAsync`; reproduced once in 22 runs) — a race of `MediaCacheCoordinator` (from
    9.4c): a piece of work leaves the bookkeeping in a continuation that runs after the work has completed, possibly
    after `Task.WhenAll` noticed it, so `IdleAsync` (and with it `ShutdownAsync`'s wait) could end one step early.
    `IdleAsync` now waits until the bookkeeping is empty. After the fix: 60 runs in a row green. Not reachable by a
    deterministic test (a scheduling race); the shutdown tests of both coordinators cover it statistically.
  - Step 9.5 closeout (2026-09-28): 9.5a–e done, 9.5a–d accepted, 9.5e awaiting with the whole step. Verification:
    `dotnet build --no-incremental` 0 errors / 0 warnings; full suite with `--blame-hang` (after the fix): 1737 passed,
    2 skipped (4K), 0 failed (Core 395, Timeline 260, Project 292, UI 296, Export 78, Rendering 58, Video 291,
    ExportEndToEnd 67 + 2); the waveform, thumbnail and cache-location tests (UI 62, Video 78 incl. 14 with real ffmpeg
    9.0.1, Project 31, Rendering 6, Core 15) 10 × in a row — one failure, the race above — and the UI part 60 × green
    after the fix; parity suite unchanged. Real app (the 9.5d project, waveform cache removed first): first open made
    one `.peaks` per file with sound and none for the silent video, the same picture as in 9.5d; closed 1 s after
    opening, while waveforms were being made — clean (140 ms, no ffmpeg left); reopen: only the Preview's two audio
    starts, the `.peaks` unchanged; no process left. The formal manual run of scenarios 31–43 is Step 9.10's. Residual
    (D024 "Left as they are"): no cache limit / eviction; a failed waveform retried only with the next project; media
    coming back online not re-checked; the start-trim drag preview; the 1–2 px border offset; the faint muted quiet clip
    at 100 %; `AppPaths.UnsavedThumbnailCacheRoot` naming both caches (9.8).
- Step 9.5 accepted and closed (2026-09-28): 9.5d committed as `80d3748`, 9.5e as `568a47b`.
- Step 9.6 — hotkeys. Audit (2026-09-28): every shortcut lives in `MainWindow.ShortcutFor` (Ctrl+N / O / S /
  Shift+S, Ctrl+Z / Y / Shift+Z, Delete / Backspace, S, N, ← / →, Shift+← / →, Space, Home / End, Ctrl+= / −), handled
  on the window's bubbling KeyDown only when no focused control consumed the key; the text-input guard (`TextBox`,
  also inside `NumericUpDown`) was never exercised in the running app (known issue since Phase 4); a known shortcut
  whose command can't run (editing during an export, `EditingLock`) is consumed and does nothing; no routing tests at
  all. Playback: forward at 1× only; at the end Paused at Duration, Play there restarts from 0, Stop = Pause + Seek(0)
  (D011); no loop. Existing commands without a shortcut: Import Media, Export, Zoom to Fit, Stop, Add to Timeline,
  + Text, + Video / Audio Track. Product owner decisions (2026-09-28, all as proposed): PO-H1 J = back 1 s, the
  playback state kept (playing continues, paused stays paused); PO-H2 K = pause, L = play (nothing when already
  playing; at the end from 0), Space unchanged; PO-H3 loop: a toggle button in the Preview transport + Ctrl+L, session
  state only (not in `project.json`, not dirty, not undoable), while on the end of the sequence continues from 0;
  PO-H4 extra shortcuts Ctrl+I Import Media, Ctrl+E Export, \ Zoom to Fit; the optional shortcut list (F1) not chosen.
  Sub-steps, each accepted separately: 9.6a routing and its tests (no behaviour change) · 9.6b J / K / L and the extra
  shortcuts · 9.6c loop · 9.6d the text-input guard in the running app and closeout.
  - 9.6a done — routing and its tests, no behaviour change. The shortcut table and the key handling moved unchanged
    from `MainWindow` into `UI/Common/ShortcutRouter`: `CommandFor(vm, key, modifiers)` (exact modifiers),
    `IsTextInput(element)` (`TextBox`), `Handle(vm, key, modifiers, source, focused)` — nothing while the source or the
    focused element is a text input, a known shortcut is consumed and runs only if its command can execute.
    `MainWindow.OnKeyDown` only calls it (unused `System.Windows.Input` using removed). Tests:
    `UI.Tests/ShortcutRoutingTests` (14 with theory rows, real view models / project / edit service: every existing
    shortcut keeps its key and command (22 key + modifier pairs); nine other keys / modifier combinations are no
    shortcut and not consumed; a shortcut runs and is consumed; nothing fires while a `TextBox` sent the key or has
    focus, any other control doesn't block; only text inputs block; during an export split / New / Undo are consumed
    without running, viewing (→) still works, after it split works). Mutations (all caught): no guard → 1, focus not
    checked → 1, unavailable not consumed → 1, unavailable run anyway → 1, modifiers not exact → 3, Shift ignored → 2, a
    mapping swapped → 1, a key lost → 1. `dotnet build --no-incremental` 0 errors / 0 warnings; full suite with
    `--blame-hang`: 1751 passed, 2 skipped (4K), 0 failed (Core 395, Timeline 260, Project 292, UI 310, Export 78,
    Rendering 58, Video 291, ExportEndToEnd 67 + 2). Real app (the 9.5d project opened through UI Automation): N sent to
    the window (WM_KEYDOWN / WM_KEYUP posted to it — `SendKeys` from this session doesn't reach the app) toggled Snap
    On → Off through the router; closed clean.
  - 9.6a accepted (2026-09-28), committed as `b90247a`.
  - 9.6b done — J / K / L and the extra shortcuts (PO-H1 / PO-H2 / PO-H4). `PreviewViewModel`: `PlayCommand` (plays;
    nothing when already playing; at the end the service starts from 0, D011) and `PauseCommand` (pauses; nothing when
    paused), next to `PlayPauseCommand` (Space, unchanged). `ShortcutRouter`: J → the timeline's
    `StepBackwardSecondCommand` (the same as Shift+←: one second of nominal frames back; a seek never changes the
    playback state, so playing continues from there and paused stays paused; at the start it stops at 0), K → Pause,
    L → Play, Ctrl+I → Import Media, Ctrl+E → Export (both inert during an export, like the other editing shortcuts),
    `\` → Zoom to Fit (`OemPipe` on US layouts, `OemBackslash` — the key next to the left Shift — on ISO ones); all
    without other modifiers. No reverse playback or faster speeds (D010 / D011 unchanged). Tests:
    `UI.Tests/PlaybackShortcutTests` (6, the real shell and playback service with the fake decoder and a manual clock,
    keys through the router: L plays and pressed again keeps playing; K pauses and pressed again stays paused, the clock
    moving doesn't move the playhead; J while playing goes back 25 frames at 25 fps and plays on; J while paused goes
    back and stays paused, and stops at 0; L at the end starts from 0; both `\` keys fit the sequence),
    `UI.Tests/ShortcutRoutingTests` (+7 table rows, +5 non-shortcut rows: Shift+J, Ctrl+K, I and E without Ctrl,
    Ctrl+\; Ctrl+I / Ctrl+E consumed without running during an export); the shortcut tests 5 × in a row green.
    Mutations (all caught): L toggling → 2, K toggling → 2, J one frame → 3, J stopping playback → 3, Pause playing
    when paused → 1, the ISO backslash missing → 2, Import without Ctrl → 3, Export missing → 2 (a first "J pauses"
    mutation changed nothing — replaced by "J stopping playback"). `dotnet build --no-incremental` 0 errors /
    0 warnings; full suite with `--blame-hang`: 1762 passed, 2 skipped (4K), 0 failed (Core 395, Timeline 260,
    Project 292, UI 321, Export 78, Rendering 58, Video 291, ExportEndToEnd 67 + 2). Real app (the 9.5d project, keys
    posted to the window): L → the transport shows Pause (playing), L again → still Pause, K → Play (paused), K again
    → still Play; closed clean. Second round (keys posted as VK codes, the timecode read through UI Automation before
    and ~760 ms after each key; no test suite running): J while playing 04:17 → 04:09 and 06:10 → 06:02 — one second
    back plus the time until the second reading (expected ≈ 04:11 / 06:04; the 2-frame difference is the latency of the
    UI Automation readings), the transport kept showing Pause and the time kept running; J while paused 04:10 → 03:10,
    exactly 25 frames, still paused; `\` sent as VK 0xDC (`OemPipe`): a view zoomed in 8 steps showing 0–7.5 s changed
    to the whole sequence 0–15 s (screenshots compared; `OemBackslash` is covered by the tests only). Processes checked
    apart from the suite: before the runs no ffmpeg / ffprobe and no `testhost`; after each close none left (the
    `ffmpeg left: 2` of the first round was the test suite running in parallel).
  - 9.6b accepted (2026-09-28), committed as `990c33b` on top of `b90247a` (9.6a; history not rewritten).
  - 9.6c done — loop (PO-H3). `PreviewViewModel.IsLooping` (off by default) + `ToggleLoopCommand`; a `Loop` toggle
    button in the Preview transport after ⏭ (tooltip "Loop playback (Ctrl+L)"), Ctrl+L in `ShortcutRouter` (a viewing
    shortcut: also available during an export). Where: in the Preview's tick, not in Core — when an update of the
    playback service has just reached the end and paused there (playing before the update, paused after it, at
    `Duration`, D011) and loop is on, the tick calls `Play()`, which at the end starts again from 0 (the existing D011
    rule), and shows that update. So the D011 end rule itself, `IPlaybackService` and its fakes are unchanged, and with
    loop off nothing changes; a pause the user made (between ticks) is never undone, and turning loop on while paused
    at the end starts nothing. The whole sequence loops (no in / out range). Session state only: not in the project or
    `project.json` (format v2 unchanged), not dirty, not undoable, kept across New / Open. Tests:
    `UI.Tests/LoopPlaybackTests` (6, the real shell and playback service with the fake decoder and a manual clock: loop
    off by default and the end pauses there as before; loop on — past the end playback continues from the start,
    plays on and loops again; a pause before the end is not undone; turning loop on while paused at the end starts
    nothing; Ctrl+L toggles; session state — not dirty, nothing added to undo, nothing about it in the saved
    `project.json`, kept after New), `UI.Tests/ShortcutRoutingTests` (+1 row, Ctrl+L); the loop, shortcut and playback
    UI tests 5 × in a row green. Mutations: loop ignored → 1, loop always on → 4, looping without having been playing →
    1, no restart after the end → 1, loop reset by another project → 1, Ctrl+L missing → 2, toggle doing nothing → 3;
    not caught: the end check (`Position ≥ Duration`) — defensive: an update of the service only ever pauses at the end,
    so "playing before, paused after" already means the end; kept so that a future pause for another reason never
    loops. Real app (the 9.5d project, 15 s; the Loop button toggled through UI Automation, keys posted to the window,
    no test suite running): loop on — End, J (14:00), L, ~4.6 s later the timecode read 03:16 (14.00 + 4.64 − 15.00 =
    3.64 s): it went past the end and continued from 0 without stopping, K then paused; loop off — the same keys stop
    at 15:00, paused (D011); the button shows the on state highlighted (screenshot); no ffmpeg / `testhost` before, none
    left after each close. `dotnet build --no-incremental` 0 errors / 0 warnings (two xUnit1031 warnings of a first
    version of the session-state test — blocking waits — fixed by making it async); full suite with `--blame-hang`:
    1768 passed, 2 skipped (4K), 0 failed (Core 395, Timeline 260, Project 292, UI 327, Export 78, Rendering 58, Video
    291, ExportEndToEnd 67 + 2).
  - 9.6c accepted (2026-09-28; the defensive end check kept), committed as `8f46e25` on top of `990c33b`.
  - 9.6d done — the text-input guard in the running app, and closeout of 9.6. Real app (a copy of the 9.5d project
    with a text clip "Hello" on a second video track; the clip selected by a mouse click posted to the window, fields
    focused through UI Automation, keys as real keyboard input (`keybd_event`, so Ctrl is really held), state read
    through UI Automation; no test suite running): 1) the Inspector's text box: End, then J, K, L, Space, S, N typed
    "Hellojkl sn", Backspace removed the last character, Ctrl+L, Ctrl+E, Ctrl+I, Ctrl+N and \ changed nothing —
    snapping, loop, the transport (Play), the timecode (00:00:00:00), the clips, the windows (no picker, no New prompt,
    no export) and the zoom as before; Ctrl+A and typing replaced the text ("hi"); Ctrl+Z in the box undid the last
    character ("h" — the text box's own undo or the app's, UI Automation can't tell them apart; the app's Undo is
    blocked like every shortcut while the box has focus, as the tests show); the clip's label followed the text;
    2) the font size (a `NumericUpDown`'s inner text box): Ctrl+A, "60", J, K, L, S, N and Ctrl+L went into the field
    ("60jklsn"), no shortcut fired; "60" and Tab applied the size; 3) focus on the Snap button: N switched snapping
    off, Ctrl+L turned loop on, L played, K paused, \ changed the zoom — the same keys reach the app and work again
    once no text input has focus. Closed through Don't Save (the edits made it dirty); no ffmpeg / `testhost` before,
    none left after. No problem found — no code change. Tests: `UI.Tests/ShortcutRoutingTests` — the table of every
    shortcut moved into one method; a new test presses every row (also J / K / L, Ctrl+L, Ctrl+I / Ctrl+E, \) with a
    text box focused and as its source: nothing fires (the earlier guard test covers N and Ctrl+S only). Mutations
    (both caught): the guard only for keys without modifiers → 2 (the old and the new test), J / K / L let through → 1
    (only the new one). Documentation (closeout): D024 "Refined in Step 9.6" (PO-H1–H4, routing, J / K / L, loop, the
    guard, what is left as it is); `docs/PHASE9_MANUAL_TEST_PLAN.md` section "Step 9.6" (scenarios 44–52) and the status
    "app 9.6"; `ARCHITECTURE.md` (shortcut routing, loop); `docs/DEVELOPMENT_PLAN.md` 9.6 done; `ROADMAP.md` "Current";
    the Phase 4 known issue about the guard struck through. Verification: `dotnet build --no-incremental` 0 errors /
    0 warnings; full suite with `--blame-hang`: 1769 passed, 2 skipped (4K), 0 failed (Core 395, Timeline 260,
    Project 292, UI 328, Export 78, Rendering 58, Video 291, ExportEndToEnd 67 + 2); a final open / play (L) / pause
    (K) / close of the app apart from the suite: closed in 142 ms, no ffmpeg / ffprobe, app or host process left.
  - Step 9.6 closeout (2026-09-28): 9.6a–d done, 9.6a–c accepted (`b90247a`, `990c33b`, `8f46e25`), 9.6d awaiting with
    the whole step. Residual (D024 "Left as they are"): fixed shortcuts; J one second only; loop over the whole
    sequence; the defensive loop end check; `OemBackslash` (ISO \) and Ctrl+I / Ctrl+E checked by tests only, not
    pressed in the running app.
- Step 9.6 accepted and closed (2026-09-28): 9.6d committed as `e8c3c93`.
- Step 9.7 — performance baseline (2026-09-28), measurement only: no code of the repository changed, no optimization,
  no targets (D024: the product owner chooses what to optimize after reviewing the baseline).
  - Method. Tool: a scratch console program outside the repository (as Step 8.6's `CodecMeasure`; the plan's
    alternative, opt-in tests, would change the repository) — `Perf.csproj` referencing the app's projects (Core,
    Infrastructure, UI, Video, Timeline, Media, Export, Project), built Release; Avalonia initialised as the app does
    (`UsePlatformDetect().WithInterFont()`, own UI thread with a running dispatcher). It drives the shipped code as is:
    `PlaybackService` + `CompositionView`, `ExportService` with the app's decoders / encoder / rasterizer, the
    thumbnail and waveform services; the export breakdown wraps the service's own interfaces (`IVideoDecoder`,
    `ICompositionRasterizer`, `IExportEncoder`) in timing decorators. Scenarios generated with ffmpeg (as Step 8.1):
    `testsrc2` at 1280 × 720 / 1920 × 1080 / 3840 × 2160, 30 fps, H.264 veryfast CRF 18, no audio; a stereo tone (PCM
    WAV, AAC 60 / 600 s). A project of N video tracks, each a full-length clip of the same source, all above the bottom
    at opacity 0.8 (nothing culled: every layer decoded and composited) and, for the export, the tone on A1. Nothing
    else running (no test suite, no app; no ffmpeg before a run). Hardware / software: AMD Ryzen 7 7800X3D (8 cores /
    16 threads), 31 GB RAM, NVIDIA RTX 4070 SUPER, Samsung 990 PRO NVMe, Windows 11 IoT Enterprise LTSC 10.0.26100,
    power plan High Performance; .NET SDK 8.0.424; FFmpeg 9.0.1 essentials (gyan.dev); repository at `e8c3c93`.
  - Preview (1920 × 1080 canvas, 1080p sources decoded at ≤ 1280 × 720 as the app's playback settings, a 960 × 540
    view, ticks at 60 Hz for 10 s after the first pictures, Stopwatch clock, video only — no audio device, so nothing
    is played aloud; per tick: `Update()`, setting the view's layers = copying new frames into its bitmaps, rendering
    the view offscreen with `RenderTargetBitmap` — Skia on the CPU, an upper bound for the app's own compositor):
    | layers | update p50 / p95 ms | copy p50 / p95 ms | render p50 / p95 ms | ticks | frames shown / 300 | late ticks | peak WS MB | ffmpeg |
    |---|---|---|---|---|---|---|---|---|
    | 1 | 0.018 / – | 0.018 / 0.36 | 3.2 / 4.0 | 600 | 300 | 0 | 243 | 1 |
    | 2 | 0.021 / – | 0.009 / 0.68 | 6.4 / 7.6 | 600 | 300 | 0 | 437 | 2 |
    | 4 | 0.029 / – | 0.016 / 1.65 | 12.7 / 14.7 | 600 | 300 | 0 | 470 | 4 |
    | 8 | 0.053 / – | 7.2 / 8.6 | 25.4 / 31.0 | 301 | 293 (7 skipped) | 0 | 869 | 8 |
    Copy p50 is small at 60 Hz because every second tick has no new frame; at 8 layers a tick (copy + render ≈ 33 ms)
    no longer fits 16.7 ms, the loop runs at 30 Hz and 7 of 300 frames are skipped; the decoders are never late.
  - Export (10 s at 30 fps + the 10 s PCM tone, sources at the canvas size, D023 format; real-time factor = duration /
    wall time; peaks of this process; ffmpeg = the decoders + the encoder):
    | canvas × layers | wall s | RTF | fps | audio / video / finalize ms | peak WS / private MB | ffmpeg peak / after |
    |---|---|---|---|---|---|---|
    | 720p × 1 | 1.98 | 5.04 | 151 | 296 / 1579 / 100 | 162 / 136 | 2 / 0 |
    | 720p × 2 | 3.12 | 3.20 | 96 | 290 / 2729 / 98 | 189 / 164 | 3 / 0 |
    | 720p × 4 | 5.73 | 1.74 | 52 | 290 / 5336 / 104 | 284 / 259 | 5 / 0 |
    | 720p × 8 | 11.18 | 0.89 | 27 | 282 / 10774 / 122 | 389 / 365 | 9 / 0 |
    | 1080p × 1 | 4.31 | 2.32 | 70 | 284 / 3837 / 181 | 276 / 251 | 2 / 0 |
    | 1080p × 2 | 7.73 | 1.29 | 39 | 290 / 7250 / 180 | 295 / 269 | 3 / 0 |
    | 1080p × 4 | 14.99 | 0.67 | 20 | 283 / 14489 / 207 | 498 / 474 | 5 / 0 |
    | 1080p × 8 | 29.23 | 0.34 | 10 | 288 / 28720 / 212 | 626 / 602 | 9 / 0 |
    | 2160p × 1 (5 s, opt-in) | 16.79 | 0.30 | 9 | 160 / 16019 / 604 | 736 / 713 | 2 / 0 |
  - Export breakdown (the same scenarios, timing decorators; time of the export loop, which handles one frame at a
    time: get every layer's decoded frame → rasterize → hand the canvas to the encoder):
    | canvas × layers | wall ms | waiting for decoded frames | rasterize (per frame) | write frames | write audio | this process CPU (cores) |
    |---|---|---|---|---|---|---|
    | 1080p × 1 | 4631 | 1976 | 1868 (6.2 ms) | 184 | 247 | 1.20 |
    | 1080p × 2 | 8521 | 3744 | 3921 (13.1 ms) | 190 | 254 | 1.21 |
    | 1080p × 4 | 15694 | 7394 | 7292 (24.3 ms) | 206 | 243 | 1.28 |
    | 1080p × 8 | 29700 | 14829 | 13579 (45.3 ms) | 202 | 238 | 1.29 |
    | 720p × 8 | 11259 | 4399 | 5929 (19.8 ms) | 114 | 241 | 1.26 |
  - Cancel latency (1080p × 2 + tone; cancel requested from the progress callback inside each stage; 3 runs each;
    latency = request → `ExportAsync` returned): Preparing 13–25 ms, Audio 8–9 ms, Video at 5 % 50–54 ms, Video at 50 %
    71–79 ms, Finalizing 38–40 ms; every time: nothing in the output folder and no ffmpeg process at the return, none
    after 1 s.
  - Repeated runs (after each: full GC, then this process's handles / private memory / managed heap / threads and the
    ffmpeg processes): 10 exports (720p × 1, 5 s) — handles 620 → 626 (flat from run 4), private 91–106 MB without a
    trend, managed 1.6–2.6 MB, threads 45–46, ffmpeg 0 after every run; 10 playback sessions (1080p × 2: play 2 s, seek,
    play 1 s, dispose) — handles 633–641 (no growth), private 252–368 MB fluctuating without a trend, managed 58–59 MB
    constant from the first session (retained after every session is disposed — most likely pooled frame buffers;
    not growing), threads 45–48, ffmpeg 0.
  - Thumbnails / waveforms (cold = decoded and written to the cache, warm = cache hit): thumbnail 720p 79 ms, 1080p
    91 ms, 2160p 191 ms, warm ≤ 0.2 ms; waveform PCM 10 s 57 ms, AAC 60 s 108 ms, AAC 600 s 685 ms (≈ 1.1 ms per second
    of sound), warm ≤ 0.2 ms.
  - The app itself (the 9.5d project: 320 × 180 video with sound, a silent video, two WAV clips; opened, played
    10.8 s, closed; the process sampled every 100 ms): peak working set 239 MB, private 250 MB, peak handles 1 487,
    ffmpeg ≤ 3; closed clean, none left.
  - Bottlenecks (findings, not decisions): 1) export — one frame at a time on about one core (1.2–1.3 of 16): about
    half of the video stage waits for the layers' decoded frames and a little less rasterizes (≈ 6 ms per 1080p layer
    on the CPU); handing canvases to the encoder and the audio are 2–3 %; the time grows linearly with layers (1080p:
    RTF 2.32 → 0.34 from 1 to 8 layers); the decoders don't seem to run ahead of the loop (the wait grows with every
    layer although decoding a 1080p frame takes ffmpeg far less than the wait) — a hypothesis, not measured inside
    ffmpeg; 2) Preview — 1–4 layers fit a 60 Hz tick with a wide margin (render ≤ 14.7 ms p95 on the CPU); 8 layers
    don't (≈ 33 ms per tick in this measurement, 7 skipped frames; the app's own GPU compositor was not measured for
    8 layers); 3) memory grows with layers (8 × 1080p: Preview 869 MB, export 626 MB peak working set); 4) no leak
    found: no process left behind, handles and memory without a trend over 10 repetitions; cancel ≤ 80 ms and clean.
  - Proposed for the product owner (D024: decided at the start of 9.7): the tool — this scratch tool, outside the
    repository, re-run for every before / after; leak criteria — a leak is 1) any ffmpeg / ffprobe process still running
    1 s after the operation that started it ended (export done or cancelled, playback released, project replaced,
    window closed) or 2) handles or private memory (after a full GC) growing at every one of 10 repetitions of the same
    operation without levelling off; growth once (warm-up, pools) is not a defect. Candidates, all within D023 (same
    frames, same encoder, same canvas bytes — the parity suite unchanged): A) export: decode the layers ahead of the
    loop and in parallel (overlap decoding with rasterizing); B) export: overlap rasterizing frame n + 1 with encoding
    frame n; C) Preview with many layers: measure the app's own compositor at 8 layers first, then decide; D) none.
    The scratch tool: `…\scratchpad\perf` (this session), its raw results `perfwork\results-*.json`.
  - Baseline accepted (2026-09-28). Product owner decisions: the scratch tool and these scenarios stay for every
    before / after; the leak criteria as proposed; A not blindly; B not now (encoding is 2–3 %); first C — the Preview
    with 8 layers in the running app; D (no optimization) not chosen yet — decided after C whether A is worth it.
  - C — the Preview with 8 layers in the running app (2026-09-28), measurement only, no code changed. Method: the app
    started as the product owner runs it (`dotnet run`, Debug build of `e8c3c93`), window maximized on the 3440 × 1440
    monitor (59 Hz; the other one 1920 × 1080, 60 Hz), projects opened through the folder picker (UI Automation),
    L / K posted to the window; the Preview area 1531 × 862 on screen (a 1920 × 1080 canvas at 0.80). Scenario as the
    baseline's Preview: 8 video tracks of the same 1920 × 1080, 30 fps, 12 s H.264 source (decoded at ≤ 1280 × 720 by the
    app's playback), all above the bottom at opacity 0.8, no audio; a 1-layer project as the control. The source carries
    its frame number n in 9 white boxes in a black band at its top (bit k at x = 40 + 200 k, plus an always-white
    reference box). Frame delivery: while playing 10 s, ffmpeg `gdigrab` captured only that band of the screen at 60 fps
    (lossless), and a small reader decoded n from every capture — which timeline frames reached the screen, the gaps,
    the order, how long each stayed. Load: separate runs without the capture — the app process's CPU time
    (`TotalProcessorTime`), `GPU Engine(pid_<app>…)\Utilization Percentage` per engine type every second, peak working
    set / private bytes / handles every 200 ms, the ffmpeg decoders. No test suite, no other app; no ffmpeg before; none
    left after any run. The window's content is composited by Avalonia on the GPU (the baseline's CPU `RenderTargetBitmap`
    time does not apply here).
    | run | timeline frames shown / in range | skipped (numbers) | timeline frames / s | picture changes / s | backwards | longest hold (1/60 s) |
    |---|---|---|---|---|---|---|
    | 1 layer (control) | 294 / 297 | 3 (7, 186, 192) | 29.7 | 29.6 | 0 | 5 |
    | 8 layers, run 1 | 293 / 299 | 6 (2, 3, 8, 33, 35, 52) | 29.65 | 29.5 | 0 | 4 |
    | 8 layers, run 2 | 293 / 299 | 6 (49, 94, 123, 248, 263, 280) | 29.45 | 29.35 | 0 | 5 |
    | run (no capture) | app CPU, cores avg | GPU 3D engine of the app, % per second | peak working set / private MB | peak handles | decoders |
    |---|---|---|---|---|---|
    | 1 layer | 0.05 | 7.4–13.7 | 386 / 410 | 1 357 | 1 |
    | 8 layers | 0.63 | 6.7–10.1 | 1 101 / 1 143 | 1 610 | 8 |
    (With the capture running, 8 layers: CPU 0.75 / 0.89 cores, peak working set 1 607 / 1 610 MB.) The capture is not
    synchronised with the display (60 fps against 59 Hz), so a frame shown for one refresh can fall between two captures:
    the 1-layer control's 3 "skips" (1 %) are the method's noise, not the app's.
    Conclusion: the running app plays 8 layers of 1080p at 29.45–29.65 of the content's 30 frames per second, in order,
    no frame held longer than 5/60 s; about 2 % of the frames did not reach the screen against about 1 % noise at
    1 layer — about one extra missed frame in a hundred, spread over the run (run 1: in the first two seconds, while
    the eight decoders start). The display refresh (59–60 Hz) is not the limit: the content needs 30 new pictures per
    second and gets them; the UI thread's timer (10 ms) and the GPU compositor keep up — the app uses 0.6–0.9 of a core
    and the GPU's 3D engine at about 10 %. The baseline's "8 layers don't fit a 60 Hz tick" came from software rendering
    in the tool; it does not happen in the app. What does grow is memory: 8 layers need about 1.1 GB working set
    (1.6 GB peak in the capture runs) against 0.39 GB for one. Not measured: the GPU's own frame times (no PresentMon or
    similar tool installed, none downloaded), sound during playback, other monitors / refresh rates, other sources.
  - C accepted (2026-09-28): the Preview needs no optimization; the product owner chose A (the export), within D023;
    B and any other optimization not now.
  - A — the export decodes ahead and in parallel (2026-09-28; not committed, awaiting review). Architecture before:
    `ExportService.WriteVideoAsync` handled one output frame at a time — `ExportFrameSource.GetFrameAsync(n)` asked the
    picture layers' readers one after another, then the rasterizer drew the canvas, then `WriteFrameAsync` handed it to
    the encoder; each reader's `FfmpegVideoFrameStream` reads a frame's pixels from ffmpeg's stdout only when asked
    (`ReadExactlyAsync` into a new buffer), so ffmpeg could decode ahead only as far as the pipe's buffer — decoding,
    reading and rasterizing took turns on about one core. Change (two places, nothing else): 1) `ExportFrameSource.
    GetFrameAsync` starts every picture layer's `GetAsync` at once and awaits them together (`Task.WhenAll`): the layers'
    decoders work in parallel; each reader still gets its requests in ascending order and selects exactly as before
    (D009 / D022); the layers keep their order; the result is built only after every fetch ended — also when one
    failed (the first failure in layer order propagates), so no reader is still reading when the caller disposes the
    source; every layer's reader is created before any fetch starts (a clip that can't be exported fails before
    anything runs — the preflight blocks such clips anyway). 2) `ExportService.WriteVideoAsync` fetches frame n + 1 while frame n is rasterized and written — one frame
    ahead, never more (at most two output frames in flight); the requests stay ascending and one at a time; the
    frames drawn and written, their order, the encoder, the format and the progress are unchanged; on leaving early
    (cancelled, any failure) the fetch ahead is cancelled (a linked token) and awaited before the frame source disposes
    its readers. Tests: `Export.Tests/ExportServiceTests` (+2: while the encoder blocks at frame 3 exactly six source
    frames have been read — frame 4 fetched ahead, not further; a rasterizer failure while the fetch ahead is stuck in
    the decoder ends the export with that failure, every part released, and no stream disposed while a read on it was
    still running), `Export.Tests/ExportFrameSourceTests` (+2: two layers — the top layer's decoder opens while the
    bottom one's is held, the layers keep their order and frames; a failing layer waits for the other layer before the
    failure propagates, then everything is released); the 78 existing export tests unchanged and green (cancellation
    during audio / video / completion, a stuck encoder, decode / encoder / rasterizer failures); Export tests 5 × in a
    row green. Mutations (all caught): no fetch ahead → 1, the fetch ahead not cancelled on leaving → 1 (would hang),
    not awaited on leaving → 1 (after making the test decoder's cancelled read take 50 ms — a first version finished
    too fast to notice), layers one after another → 2, a failure leaving before the other layers end → 1.
    Verification: `dotnet build --no-incremental` 0 errors / 0 warnings; full suite with `--blame-hang`: 1773 passed,
    2 skipped (4K), 0 failed (Core 395, Timeline 260, Project 292, UI 328, Export 82, Rendering 58, Video 291,
    ExportEndToEnd 67 + 2); the parity suite with `AIVE_HEAVY_TESTS=1`: 69 of 69 (the 4K scenes too) — byte-equal
    canvases and every tolerance unchanged.
    Before / after (the baseline's scratch tool and scenarios; "before" measured again right before the change; RTF =
    duration / wall time):
    | canvas × layers | RTF before | RTF after (3 runs) | gain |
    |---|---|---|---|
    | 720p × 1 | 4.94 | 6.20 / 6.35 / 6.24 | +26 % |
    | 720p × 2 | 3.17 | 4.22 / 4.31 / 4.35 | +35 % |
    | 720p × 4 | 1.75 | 2.37 / 2.28 / 2.22 | +31 % |
    | 720p × 8 | 0.90 | 1.20 / 1.17 / 1.16 | +31 % |
    | 1080p × 1 | 2.23 | 3.16 / 3.17 / 3.11 | +41 % |
    | 1080p × 2 | 1.25 | 1.97 / 1.89 / 1.83 | +51 % |
    | 1080p × 4 | 0.66 | 1.08 / 1.03 / 1.03 | +58 % |
    | 1080p × 8 | 0.34 | 0.52 / 0.54 / 0.53 | +56 % |
    Breakdown after (1080p): the export process now uses 1.9–2.1 cores (1.2–1.3 before); the time waiting for decoded
    frames and rasterizing overlap (their sum exceeds the wall time); rasterizing per frame is a little slower
    (6.2 → 7.1 ms at 1 layer, 44 → 55 ms at 8 — the CPU is shared with the decoding now). Cancel (1080p × 2, 3 runs per
    stage): Preparing 13–23 ms, Audio 8 ms, Video 5 % 51–53 ms, Video 50 % 70–76 ms, Finalizing 39–43 ms — as before;
    nothing in the output folder and no ffmpeg at the return, none after 1 s. Repeated runs: 10 exports — handles
    626 → 632, private 83–89 MB, ffmpeg 0 after each; 10 playback sessions unchanged (the Preview isn't touched).
    Memory (a separate run with the heap after each GC — what survived — and the GC counts; before = the two files
    restored from HEAD for the measurement, then put back):
    | canvas × layers | post-GC heap MB before → after | peak working set MB before → after | gen2 GCs before → after |
    |---|---|---|---|
    | 1080p × 1 | 26 → 34 | 244 → 219–236 | 48 → 60 |
    | 1080p × 4 | 82–97 → 109–137 | 305–314 → 395–638 | 149 → 97–100 |
    | 1080p × 8 | 264–279 → 327 | 720–721 → 1133–1137 | 106 → 51–52 |
    | 720p × 8 | 217–232 → 371 | 615–616 → 1064–1068 | 70 → 50 |
    The live data grows by about one decoded frame per layer, as designed (1080p: 8.3 MB per layer; 720p × 8 more than
    that, +145 MB — not explained); the peak working set grows much more with many layers (+58 % at 1080p × 8, +73 % at
    720p × 8, ≈ +0.4 GB), mostly garbage not yet collected: every decoded frame is a new large array, they are made
    faster now and there are about half as many gen2 collections; at one layer there is no change.
    Conclusion: a measurable gain everywhere (+26–35 % at 720p, +41–58 % at 1080p; 720p × 8 now faster than real time,
    1080p × 4 too), with the same frames, order, encoder, format, cancellation and clean-up; but a memory regression
    with 4–8 layers — peak working set about +0.4 GB at 8 layers (1.1 GB for 8 × 1080p), no change at 1 layer. Not in
    this change (other optimizations, need their own decision): reusing the decoded frames' buffers (would remove most
    of the garbage — also the Preview's), a GC setting, a smaller look-ahead for many layers.
  - A review (2026-09-28): the speed-up accepted as effective, A not finally accepted because of the memory regression;
    not rolled back, no new optimization; next: diagnose the regression only.
  - A memory diagnosis (2026-09-28; no production code changed; the same scratch tool, scenarios and measurements).
    Method: the export's video loop rebuilt in the tool from the service's public parts (`ExportFrameSource`, the
    Avalonia rasterizer, the ffmpeg encoder; silent audio) in four variants — V0 sequential (the layers read one at a
    time — a wrapper around the app's decoder lets one frame read run at a time — and no fetch ahead: the code before
    A), V1 parallel layers only, V2 fetch ahead (n + 1) only, V3 both (= A, measured the same as the service's own
    export: RTF 0.55–0.56 at 1080p × 8). Measured, sampling every 20 ms: peak working set / private bytes; the managed
    heap including garbage (`GC.GetTotalMemory(false)`) and the GC's committed bytes; at every gen2 GC (background or
    blocking — `GetGCMemoryInfo(Background / FullBlocking)`) the heap that survived it (live data) and its LOH part;
    the decoded frames still alive then (weak references to every decoded frame's pixel array); the GC counts and the
    bytes allocated. Every run in a fresh process (the first comparison in one process mixed scenarios: a run after
    1080p × 8 kept its memory committed); a diagnostic floor with a forced full GC every 10 frames (in the tool only).
    Two earlier numbers were artifacts of the measurement, not of A: the "post-GC heap" of the previous entry was taken
    after the last GC of any kind — mostly gen0, which never looks at the large-object heap where every decoded frame
    lives (3.7 MB at 720p, 8.3 MB at 1080p), so it counted uncollected frames as live (hence 720p × 8 "+145 MB"); and
    the peaks of the scenarios run after 1080p × 8 in the same process included that scenario's committed memory
    (hence 720p × 8 "615 → 1066 MB").
    | canvas × layers | peak working set MB V0 → V3 | live after gen2 MB V0 → V3 | alive decoded frames at gen2 V0 → V3 | managed incl. garbage MB V0 → V3 | gen2 GCs V0 → V3 | allocated GB | RTF V0 → V3 |
    |---|---|---|---|---|---|---|---|
    | 1080p × 1 | 243 → 235 | 25 → 33 | 2 → 3 | 113 → 105 | 48 → 60 | 2.4 | 2.3 → 3.5 |
    | 1080p × 4 | 490 → 619 | 73 → 105 | 8 → 12 | 328 → 456–471 | 68 → 48 | 9.6 | 0.66 → 1.07 |
    | 1080p × 8 | 810 → 1130 | 113–120 → 200 | 15 → 24 | 606–614 → 901 | 121 → 53 | 19.1 | 0.34 → 0.56 |
    | 720p × 8 | 421 → 364 | 55 → 90 | 16 → 24 | 260 → 176–207 | 123 → 101 | 8.5 | 0.91 → 1.25 |
    (2 runs each, fresh processes; the repetitions agree within a few MB.) Single halves (one process, 1080p × 8): V1
    parallel layers only — peak 718 MB, 148 gen2, live 138 MB; V2 fetch ahead only — peak 1 100 MB, 65 gen2, live
    168 MB. Floor with a forced full GC every 10 frames: 1080p × 8 V0 809 MB (unchanged — it already runs a gen2 every
    2.5 frames), V3 715 MB (managed incl. garbage 391 MB instead of 902); 1080p × 4 V0 473, V3 523.
    Findings: 1) live data is bounded by design and exactly as expected — the decoded frames alive per picture layer
    are 2 before A (frame n; n + 1 being read / the reader's look-ahead) and 3 with A (frame n being rasterized, n + 1
    the reader's current frame, n + 2 being read), i.e. + one frame per layer: 1080p × 8 +66–87 MB, 720p × 8 +35 MB,
    1080p × 1 +8 MB; 2) the peak growth is garbage, not live frames: the same bytes are allocated (19.1 GB of frame
    arrays for 10 s at 1080p × 8 — every decoded frame is a new array on the large-object heap), but with the fetch
    ahead the GC runs about half as many gen2 collections, so about twice as much garbage lies between them; forcing
    collections removes ~415 MB of V3's peak (below the code before A); 3) the cause is the fetch ahead (n + 1), not
    the parallel layers (parallel layers alone lowered the peak at 1080p × 8); 4) why the GC schedules fewer gen2
    collections was not determined inside the GC — consistent with its large-object budget following the larger
    surviving set and the doubled allocation rate; 5) no growth over repetitions (the earlier 10-run check) — transient.
    A bound that holds without changing D023's behaviour: decoded frames alive ≤ 3 per picture layer of the frames n
    and n + 1 (readers ≤ one per such layer; a layer leaving the composition closes its reader), i.e. live frame bytes
    ≤ 3 × Σ width × height × 4 over those layers, plus the rasterizer's own bitmaps (one per layer, as before);
    garbage between GCs has no bound in the code — it is up to the GC.
  - A accepted (2026-09-28) after the memory diagnosis. Recorded: A keeps up to 3 decoded frames alive per picture
    layer (2 before A) — a bounded memory footprint by design, not a leak (no growth over 10 repeated exports); the
    peak working set can be higher than that because of transient large-object-heap garbage between gen2 collections
    (the fetch ahead doubles the allocation rate and halves the gen2 count), not live data; the earlier 720p × 8
    anomaly (+145 MB live, 615 → 1066 MB peak) was an artifact of the measurement method (a post-GC heap after gen0
    GCs; several scenarios in one process), not of A. D023 behaviour and parity unchanged. Not done, by decision: a
    frame buffer pool, GC tuning, a smaller look-ahead — possible separate optimizations, not needed now. Committed as
    `4bdf738`.
  - Step 9.7 closeout (2026-09-28): D024 "Refined in Step 9.7" (tool, leak criteria, baseline findings, A and its
    memory bound, what is not done); `ARCHITECTURE.md` Export section (parallel layer fetch, one frame ahead);
    `docs/PHASE9_MANUAL_TEST_PLAN.md` scenarios 53–56; `docs/DEVELOPMENT_PLAN.md` 9.7 done; `ROADMAP.md`. No code
    changed. Residual (not done, by decision): B, a frame buffer pool, GC tuning, a smaller look-ahead; the export
    with A was measured through the scratch tool (the service with the app's parts), not yet run in the app itself —
    scenarios 53–55 at 9.10.
    Verification: `dotnet build --no-incremental` 0 errors / 0 warnings; full suite with `--blame-hang`: 1773 passed,
    2 skipped (4K), 0 failed (Core 395, Timeline 260, Project 292, UI 328, Export 82, Rendering 58, Video 291,
    ExportEndToEnd 67 + 2).
- Step 9.7 accepted and closed (2026-09-28): A as `4bdf738`, the closeout as `1124138`.
- Step 9.8 — polish & cleanup (2026-09-28). Audit after 9.7 → one list of proposals by category; the product owner
  confirmed A1–A4, B1–B4, C1–C5, D1–D2, E1–E3 as the whole scope (not: export elapsed / remaining time, cache
  eviction / retry / online re-check, buffer pool / GC / look-ahead, a `Project.Tests` workaround, replacing timed
  waits). Details: D024 "Refined in Step 9.8".
  - A1 / D1, `PlaybackFrame.Picture`: the audit found no production reader (only `PlaybackService` filled it; the
    design-time stub in `MainWindow` built one) — but `PlaybackService.IsBuffering` used the compatibility picture
    internally (`_picture is null` until the topmost picture layer was certain after a restart). Removed `Picture` /
    `IsPictureCurrent` / `PreviewPicture` / `PictureKind` and `VideoPipeline.Compatibility`; the buffering state now
    follows `VideoFrameResult.IsTopSettled` (the same rule: the top picture layer's current frame or a placeholder,
    black when there is none; not pending or late) as a flag `_settled`, reset on every restart — same behaviour. The
    ≈ 45 test uses (Timeline 5 files, Video 3) read the layers through `PlaybackFrameView` (`TopPicture`,
    `IsTopCurrent`, `TopFrame`, in `PlaybackFakes.cs`); where a test only checked the compatibility view it checks the
    top layer's state / clip / frame instead. Not weakened — the same mutations on HEAD (a separate worktree) and
    after, failing Timeline.Tests: late frame counted as settled 2 → 0 (see below), pending counted as settled 7 → 1,
    offline placeholder not settled 4 → 4, black not settled 1 → 1, text taken as the top layer 1 → 1, no reset on a
    restart 0 → 1; the pipeline's own late handling (the same edits in both versions): late frame flagged current
    3 → 3, no late frame (pending instead) 1 → 2, last frame not kept 1 → 2, pending flagged current 0 → 0. The first
    "after" run had pending 0 and no-reset 0: the old tests caught these through the compatibility picture's content,
    which no longer exists, and nothing else checked buffering once a seek was ready — new test
    `AfterASeek_BufferingLastsUntilTheTopLayerHasItsCurrentFrame_EvenOnceTheSeekIsReady` (seek ready at frame 100,
    the clock at 110 not decoded yet: buffering, top pending; then frame 110, not buffering). "Late counted as
    settled" is equivalent through the public API now: a late frame needs an earlier current frame of the same layer
    in the same pipeline, which already settled it.
  - A2: `IVideoEngine` / `EngineProgress` removed (no implementation, no reference). A3: `ErrorTranslator`,
    `UserFacingError`, `FfmpegNotFoundException`, `UnsupportedMediaException`, `CorruptProjectFileException` and
    `Video.Tests/ErrorTranslatorTests` removed — checked first: the damaged-project message is `ProjectSerializer`'s
    `ProjectFileException` text shown by `ProjectFileWorkflow.OpenAsync` ("Couldn't open the project. …"), covered by
    `DamagedProjectMessageTests`. A4: every `ModuleInfo` summary describes the subsystem as it is.
  - B1 `AppPaths.UnsavedCacheRoot` (was `UnsavedThumbnailCacheRoot`, same folder); B2 `IMediaCacheLocation.cs` (was
    `IThumbnailCacheLocation.cs`, the three interfaces); B3 `StartupCacheCleanupTests`; B4 the
    `LayerPictureState.Unsupported` comment (the clip kind can't play the media kind — speed plays since D022).
  - E1 tooltips: Import Media (Ctrl+I), Export (Ctrl+E), Fit (\), ⏮ (←), ⏭ (→), Play (Space; L / K / J), + Video
    Track / + Audio Track. E2: `TimelineViewModel.IsEmpty` (no clip on any track, set in `Refresh`) shows "The timeline
    is empty" and how to add a clip at the bottom of the track area, not hit-testable (drops pass through) —
    `TimelineEmptyStateTests` (2). E3: `MediaImportWorkflow` reports "Importing N files…" before the picked files are
    checked and yields once to the dispatcher (Background priority) so the window renders it — the check runs on the
    UI thread (`MediaImportService.ImportManyAsync` is synchronous); import logic and threading unchanged; the result
    replaces it as before, "Import didn't finish." if the check throws (rethrown) — `ImportStatusTests` (4).
  - Found while verifying (not in the list, fixed): UI.Tests intermittently failed (`ShortcutRoutingTests`:
    NullReferenceException in `Dictionary.TryInsert` ← `AvaloniaProperty.GetMetadataWithOverrides` ← `new Button()`;
    once `MediaBrowserThumbnailTests` timed out in a full run) and once hung for 20 minutes (killed; no dump taken, so
    the hang's cause is inferred, not proven). Cause: three test classes (`ShortcutRoutingTests`,
    `PlaybackShortcutTests`, `LoopPlaybackTests`, since 9.6) create Avalonia controls in parallel xUnit collections, and
    Avalonia's property metadata caches are not thread-safe. Fix: one collection for them (`AvaloniaControlsCollection`).
    Before: 1 failure in 10 and 1 in 25 runs of UI.Tests, one hang; after: 30 of 30 green. HEAD had shown 15 of 15
    green — the race is older; the new test classes changed the scheduling.
  - C1–C5: README (state, layout, tests, docs, logs), ARCHITECTURE (module table, media pipeline, playback: the
    buffering rule instead of `Picture`; names), `progress.md` known issues (struck items removed, the underrun item on
    the layers' late flag), the Phase 9 manual test plan (legend incl. the 9.7 statuses, scenarios 57–59, removed test
    names).
  - Real app (`dotnet run`, Debug, UI Automation and real cursor moves, screenshots): the hint shows on a new project,
    disappears after + Text, returns after Undo, sits below the tracks; every tooltip of E1 appears with its shortcut;
    importing 600 `.wav` files selected in the Windows file dialog: the status bar showed "Importing 600 files…" from
    about 0.12 s to 0.5 s after Open, then "Imported 600 media files"; closed with Don't Save, no ffmpeg / ffprobe left.
    The first hint position (centred over the track area) crossed the A1 row — moved to the bottom.
    Verification: `dotnet build --no-incremental` 0 errors / 0 warnings; full suite with `--blame-hang`: 1779 passed,
    2 skipped (4K), 0 failed (Core 395, Timeline 261, Project 292, UI 334, Export 82, Rendering 58, Video 290,
    ExportEndToEnd 67 + 2); UI.Tests 30 × in a row green.
    Commits: `83cfa7f` (A1 / D1), `3867883` (A2–A4, B1–B3), `2872745` (Avalonia-control tests in one collection),
    `100fc69` (E1–E3), then the closeout (documentation, C1–C5, D024 "Refined in Step 9.8").
- Step 9.8 accepted and closed (2026-09-28): closeout `da4838c`.
- Step 9.9 — CI (2026-09-28). Check first: local ffmpeg / ffprobe 9.0.1-essentials_build-www.gyan.dev, installed by
  winget from `GyanD/codexffmpeg` 9.0.1 `ffmpeg-9.0.1-essentials_build.zip` (SHA256 from the winget manifest); .NET SDK
  8.0.424, no `global.json`, no `.github`; version-bound records: D009 (seek / `fps` behaviour), D022 (atempo latency,
  guarded by `FfmpegSpeedIntegrationTests`), D023 (colour, AAC priming), the Step 8.6 measurements; the tests use
  libx264, libx265, aac, lavfi sources, showinfo / ashowinfo / atempo / apad / scale / setparams — all in essentials.
  Proposed and confirmed: 9.0.1 essentials pinned by SHA256, `windows-2025`, SDK 8.0.424, Debug, `-warnaserror`,
  the full suite with TRX and a strict skip gate (D024 "Refined in Step 9.9").
  - `0b8da2b`: `.github/workflows/ci.yml` (actions: checkout v7, setup-dotnet v6, cache v6, upload-artifact v7 — the
    current majors) and `.github/scripts/Assert-TestResults.ps1`. Checked before publishing: the YAML parses; the
    version check's pattern matches the local ffmpeg / ffprobe; `dotnet build -warnaserror` 0 warnings; the gate on a
    full local TRX run passes (8 files, 1779 / 2 / 0) and fails on edited copies — a Video test skipped for missing
    ffmpeg, a 4K scene skipped with another reason, a failed test, a missing TRX file. Not run locally: the archive
    download and its SHA256 (the first CI run did both).
  - Published (authorised): `feat/phase-9-quality` pushed to `origin`, PR #7 to `main`
    (https://github.com/Proskagit/boulder-video-editor/pull/7).
  - First CI run (36462308940, `0b8da2b`): every step up to the tests green (FFmpeg downloaded, SHA256 and version
    right, build 0 warnings); 1 of 1781 tests failed — `AnalysisConcurrencyIntegrationTests`: the first four analyses
    ended at 4.78–4.86 s against the expected 1.8–3.5 s (2 s timeout); the gate reported it and the two allowed skips
    only. Cause: the absolute window included process start-up (the scripted ffprobe via `cmd`, the stream probe),
    far slower on the runner. `b96188c`: the test checks the property relatively (every first analysis ≥ 0.9 timeout;
    queued ones end ≥ 0.9 timeout after the last first one); a mutation without the slot limit (the max-processes
    assertion switched off) fails it (queued ended 0.0002 s after the first ones); 3 × green locally.
  - Second CI run (36463075887, `b96188c`): green — ffmpeg / ffprobe from the pinned copy, 9.0.1-essentials; build
    0 warnings / 0 errors; tests 1779 passed, 2 skipped (the two 4K scenes, "Heavy scenario"), 0 failed (Core 395,
    Timeline 261, Project 292, UI 334, Export 82, Rendering 58, Video 290, ExportEndToEnd 67 + 2); gate: 8 TRX files.
  - Limits (for 9.10 / the owner): `WasapiAudioOutputDeviceTests` pass on the runner without an audio device (nothing
    verified there — real devices stay manual); 4K scenes not in CI; making the check required (branch protection) is
    the owner's setting.
- Step 9.9 accepted and closed (2026-09-28): `0b8da2b`, `b96188c`, closeout `e8f2410` (CI run 36464019438 green: 1779 / 2 / 0).
- Step 9.10 — final verification & closeout (2026-09-28 / 29).
  - The Phase 9 manual test plan run as a whole in the real app (`dotnet run` at `e8f2410`), driven through its UI: the Windows
    file dialogs (file name box and buttons set through Win32 messages), UI Automation, real cursor moves and key presses
    (`keybd_event`, navigation keys as extended keys), screenshots; logs and processes read alongside; test media and
    projects generated with ffmpeg / as `project.json`. Results per scenario: `docs/PHASE9_MANUAL_TEST_PLAN.md` "Formal run
    (Step 9.10)". 58 pass, 0 fail, 9 without a manual form. Every export check went through the UI (5, 30, 43, 52–55).
  - Findings, classified with the product owner as plan clarifications (D024 "Refined in Step 9.10"): 55 — memory stays on a
    plateau after exports (401 MB idle → ~1.36 GB, flat over 10 exports, not returned after 30 s; before A: ~0.9 GB, the same
    pattern) — no growth, no leak; 53 — the Preview's ffmpeg of the open project stay after an export (only the export's must
    be gone); 52 — the progress window is modal: during an export the main window is disabled (IsWindowEnabled = false), real
    clicks / keys do nothing. The first 52 check had forced the disabled window to the foreground and injected keys; that
    is not what a user can do, so the plan was corrected and 52 re-checked with real input.
  - 14–17 with the owner: default device switched while paused (G6 → Mi TV) and while playing (sound stays until a seek), USB
    headphones unplugged while playing ("Playing without sound", no jump, next Play on the new default) and while paused
    (next Play at once on the new default); each checked by the app log and a loopback capture of the devices (the tone's
    frequency). A first 15 attempt with Loop on moved the sound at a loop wrap — a wrap is a new start (9.3e), repeated
    with a 1 h clip. Residual: the status bar keeps "Playing without sound…" after the sound is back.
  - `docs/EXPORT_MANUAL_TEST_PLAN.md` as a regression: 13 pass (9 optional, not hit); result log updated.
  - Quality gates: `dotnet build --no-incremental` 0 errors / 0 warnings; the full suite once and three times with
    `--blame-hang`: 1779 passed, 2 skipped (4K), 0 failed every time (Core 395, Timeline 261, Project 292, UI 334, Export 82,
    Rendering 58, Video 290, ExportEndToEnd 67 + 2); the 4K scenes with `AIVE_HEAVY_TESTS=1`: 2 / 2; CI green at `e8f2410`
    (run 36464019438) and on the closeout commit (see the PR).
  - Test-automation notes (not app defects): SendKeys' modifiers don't reach Avalonia (keybd_event does); non-extended
    arrow keys with Shift act as the numpad; the file dialogs' name box limits typed text, WM_SETTEXT does not; Serilog's
    file sinks flush with a delay, so log slices go by the lines' timestamps; a second app instance writes `app-*_001.log`.
  - After the closeout (2026-09-29): PR #7 had already been merged at `e8f2410` (merge `8210929`, CI green on `main`), so
    the closeout `dcb86cb` was only on the branch; a manual CI run on it (`workflow_dispatch`, 36537088545) failed twice on
    an unchanged tree: `AnalysisConcurrencyIntegrationTests` saw 2, then 3 hanging probes at once instead of 4 (sampled
    processes; the first analyses' 2 s windows no longer overlapped on a slow runner) and `WaveformIntegrationTests` got
    "FFprobe could not be found" (a new locator per analysis ran its PATH probe with a 5 s timeout, missed under load).
    Fixed in the tests only (product owner): the limit test holds the first analyses in their slots with a gate the test
    opens and checks by marker files that no queued analysis ran any ffprobe meanwhile; the timeout test measures from the
    first freed slot (a queued analysis ends ≥ one timeout after it) with a 10 s timeout that the stream probe must outlast;
    the waveform tests use the ffprobe found once. Mutations (all caught): no slot limit → both concurrency tests; timeout
    counted from queueing → both; waveform ignoring the container start time → the waveform test. Under a local load that
    saturates every core (three slow 4K encodes, the test run ~20× slower) the stream probe can outlast any fixed timeout —
    the one speed assumption left, stated in the test.
- Known issues mapped to Phase 9 steps: close hang, analysis cancellation / concurrency, audio device change,
  `ffmpeg-*.log`, backup message → 9.3 (done); Media Browser thumbnails / cache → 9.4 (done); timeline waveforms →
  9.5 (done); `AppPaths.UnsavedThumbnailCacheRoot` naming both caches → 9.8 (done, now `UnsavedCacheRoot`); hotkey
  guard not exercised in the running app → 9.6 (done, 9.6d); `PlaybackFrame.Picture` → 9.8 (done, removed);
  `Project.Tests` hang → watched (9.10);
  L1-c → stays open.

## Phase 8 (complete)

Phase 8 — Export: **complete** (accepted by the product owner on 2026-09-25; commit `8786491`, PR #6), branch `feat/phase-8-export` (from `2f0e26f`, the Phase 7 closeout).
Scope (DEVELOPMENT_PLAN): Timeline → MP4 (H.264/AAC) with everything Phase 7 added. Decisions: D023.

### Phase 8 — Export (complete)

Product decisions (product owner, 2026-09-24; recorded as D023):
- Architecture A: C# compositor + FFmpeg as the encoder only; the export is an offline rendering of the
  Preview from the same `PlaybackSnapshot` with the Core rules (D018/D009/D022/D013), no filtergraph.
  Core must not depend on Avalonia; the rasterizer is backend-specific and chosen by a spike in Step 3.
- Offline / unsupported / not analysed media block the export (preflight lists all of them); a decode
  error aborts it (no substitute frames/silence, partial output deleted); a missing font is a warning and
  falls back as in the Preview.
- Fixed format: CRF 18 + preset medium, no presets/bitrate; size = canvas, rate = exact project rate;
  always an AAC 48 kHz stereo track (silence if needed).
- Modal progress + Cancel, editing blocked; `LastExportSettings` is session state (not dirty, no undo);
  HDR/10-bit out of scope; performance secondary to correctness and parity.

Planned steps: 1 contract + D023 + preflight · 2 offline source-frame reading · 3 rasterizer spike +
compositor (shared draw plan in Core) · 4 offline audio mix (shared placement) · 5 FFmpeg encoder (Video) ·
6 `ExportService` orchestration · 7 export UI · 8 end-to-end Preview ↔ Export parity + measurement ·
9 closeout.

- Step 1 done (D023) — export contract, no rendering/encoding yet:
  - Core `Core/Export`: `ExportFormat` (MP4, H.264 CRF 18 / medium, AAC 48 kHz stereo 192 kbps, `.mp4`),
    `ExportOutput.For(snapshot)` (canvas, exact rate, whole frames covering the duration, matching
    48 kHz sample count), `ExportJob` (snapshot + full output path), `ExportProgress` / `ExportStage`,
    `ExportException` / `ExportFailure`, `ExportPreflight.Check(project, outputPath, environment)` →
    `ExportPreflightResult` (all issues, errors before warnings; job only without errors; issues grouped per
    media file / font with every affected clip in timeline order; only clips that reach the output).
  - `IExportService` now takes an `ExportJob` (was the mutable `Sequence` + media list + `ExportSettings`)
    and has `IsAvailableAsync` for the preflight.
  - `ExportSettings`: `Width`, `Height`, `FrameRate` (double), `VideoBitrateBps`, `AudioBitrateBps` removed
    (entity and DTO). Older `project.json` values are ignored on read (unknown properties, D014), never make
    a file damaged and are not written again; `formatVersion` stays 2; unknown format enums stay damaged.
  - Stale comments fixed (`SpanStatus.Unsupported`, `PlaybackSnapshotBuilder`, `IVideoEngine`, Export
    `ModuleInfo`, `ProjectSettings.AudioSampleRate` documented as unused); D010 marked partly superseded,
    D018/D021 point to D023; ARCHITECTURE (Export section), DEVELOPMENT_PLAN, ROADMAP updated.
  - Found while testing: `Path.GetFullPath` in .NET 8 accepts characters Windows can't store (`|`), so the
    preflight checks invalid file-name/path characters itself.
  - Tests: Core `ExportPreflightTests` (30 incl. theory rows), `ExportOutputTests` (10); Project
    `ExportSettingsPersistenceTests` (10: a Phase 7 `lastExportSettings` block loads, legacy values never
    damage, saving writes only path + format and stays v2, null/missing → defaults, unknown enums damaged);
    3 existing Project tests adjusted to the removed fields. Mutations (all caught): no relevance filter →
    1 failure, no "file gone now" check → 1, case-sensitive font grouping → 1, no invalid-character check → 1.
  - Full suite green: 1161 (Core 315, Timeline 257, Project 259, UI 193, Video 137); build 0 warnings.
  - Open before Steps 2–5: see the Step 1 report (rasterizer spike, move of `CompositionDrawPlan` to Core,
    extraction of the audio placement, `LastExportSettings` update API in Step 7).
- Step 1 accepted by the product owner (2026-09-24). Added before Step 2: `ExportOutputTests.Frame_count_boundary`
  (23.976 / 29.97 / 25: exactly N frames → N, last index N − 1; one tick more → N + 1; one tick less → N; less
  than one frame → 1) and, in Export.Tests, `FrameCount == FrameMath.CeilingFrame(Duration)` for 2 000 random
  durations per rate (playback's last frame is `CeilingFrame − 1`).
- Step 2 done (D023 refinement) — offline source-frame selection, no compositor/audio/encoder yet:
  - `src/Export`: `ExportFrameSource` (public; output frame n → `LayersAt(FromFrame(n))`, a decoded frame per
    picture layer, text layers without frame; ascending frames within `[0, FrameCount)`; readers only for the
    picture layers of the current frame) and `ExportPictureReader` (internal; per clip, sequential and
    blocking: opens at the layer's first visible frame with that frame's sample point, then advances while the
    next frame `IsAtOrBefore` the point — `SourceFrameSelector.Select` semantics incl. hold-first/hold-last;
    stills decoded once; full resolution (max 16384), software decoding; every failure → `ExportException`).
    `ExportDecodeSettings`. Export references Core only.
  - Reused Core: `PlaybackSnapshot` / `LayersAt` / `PictureLayer` / `TextLayer`, `SourceFrameSelector`
    (`SamplePoint` with `ClipSpeed`, `IsAtOrBefore`), `ExportOutput.FrameCount`, `IVideoDecoder` /
    `VideoDecodeRequest` / `DecodedFrame`, `MediaTime.FromFrame`. Nothing of D009/D018/D022 re-implemented.
  - New test project `tests/Export.Tests` (Core, Project, Timeline, Export; links `PlaybackFakes.cs` and
    `TimelineFixture.cs`; Timeline and Export grant it internals). `FakeVideoDecoder.FailAfter(…, software:)`
    added (a genuine failure also in software; default unchanged) and a stream of a source without frames
    now starts at 0 (was index −1, which produced a bogus frame).
  - Tests: Export `ExportFrameSelectionContractTests` (12 cases: every output frame vs the Preview's
    `VideoPipeline` playing from 0 and after seeks, same snapshot and fake sources — 23.976 / 29.97 × 1× /
    0.25× / 4× with trim start/end, split, a moved half (gap), a 25 fps 1.35× layer over both halves; hold-first
    (stream starting 3 frames late) at 23.976 / 29.97; hold-last (50-frame stream under a longer clip) at 1× and
    4×; culling under an opaque full-canvas video with an image and text — culled clip closed and reopened,
    still decoded once) and `ExportFrameSourceTests` (16: stalled decoder blocks instead of returning an
    earlier frame, mid-stream failure, open failures incl. ffmpeg missing, stream without frames, offline /
    unsupported span is an error, full resolution + software request, forward skipping in one stream and
    ascending order, cancellation and dispose, reader closed when the layer leaves, Export doesn't reference
    Timeline, FrameCount = CeilingFrame); Video `ExportFrameSourceIntegrationTests` (real ffmpeg, new
    1920 × 1080 test file: export frames 1920 × 1080 vs the Preview's ≤ 1280 × 720 — same frame numbers at 1×,
    0.25×, 4×, and equal to ⌊in + (n − start)·s + ½·min(s, 1)⌋; every stream closed); Core
    `ExportOutputTests.Frame_count_boundary` (3).
  - Mutations (all caught): speed ignored → 7 failures; next frame instead of the selected one → 15; no
    hold-last → 3; no culling → 1; no hold-first → 2; readers kept while culled → 2.
  - Found while testing: the new Video test first compared the process-wide `FfmpegProcess.LiveProcesses` and
    was flaky; replaced by counting its own streams. Its extra second then exposed a pre-existing race: the
    ffmpeg-decoding `DisplayOrientationIntegrationTests` ran outside the media collection, in parallel with the
    lifecycle tests that assert the global counter (`FfmpegAudioDecoderIntegrationTests.PlaybackLifecycle…`
    failed 1 in 3). Fixed by putting it in the media collection (Video.Tests 6 consecutive runs green; without
    the new test the race was not observed in 8 runs).
  - Full suite: 1193 (Core 318, Timeline 257, Project 259, UI 193, Export 28, Video 138), 3 consecutive runs
    green; `dotnet build --no-incremental` 0 warnings.
  - Open before Step 3: see the Step 2 report.
- Step 2 accepted by the product owner (2026-09-24).
- Step 3 done (D023 refinement) — shared composition plan + rasterizer; no audio/encoder/service/UI yet:
  - Refactor: `MediaTime.ToFrameCeiling` (Core); `FrameMath.CeilingFrame` delegates to it; Export's copies removed.
  - Spike (scratchpad, not in the repo), Avalonia 11.1.3 with the app's platform init: `RenderTargetBitmap` +
    `DrawingContext` off the UI thread work and match UI-thread bytes; text (3 families incl. a missing one × 3
    alignments) identical on both threads, a missing family falls back to the default (Segoe UI); 1 200 1080p renders
    on 4 threads deterministic; handles 586 → 586 and ~72–76 MB private over 3 × 400 renders; ~3.6 ms per 1080p
    frame. `SolidColorBrush` off the UI thread throws → immutable brushes only. First attempt hung: awaiting on the
    thread that ran `SetupWithoutStarting` posts continuations to a dispatcher nobody pumps (test harness now runs
    the dispatcher on its own thread). Decision: Avalonia offscreen, no SkiaSharp reference.
  - Core `Composition/CompositionDrawPlan.cs`: `ResolvedLayer`, `DrawOperation` → `FrameDraw` / `TextDraw` (text box
    rule documented), `CompositionDrawPlan` (`Build`, `Operation`, `Viewport`, `Bounds`, black `Background`),
    `ICompositionRasterizer`. UI: `PreviewDrawPlan` (Preview-only `PlaceholderDraw`, pending skipped) replaces the
    UI `CompositionDrawPlan`; `CompositionPainter` (the drawing routine formerly inside `CompositionView`, immutable
    brushes/pens, `Layout(text)`), `FrameBitmap` (straight-alpha bitmaps), `AvaloniaCompositionRasterizer`;
    `CompositionView` now = `PreviewDrawPlan` + `CompositionPainter`. Export: `ExportFrame` carries the canvas and
    `ResolvedLayer`s (was `ExportLayerPicture`) and builds its plan with `DrawPlan()`. UI.csproj allows unsafe code
    (pinning the caller's span for `CopyPixels`).
  - Found and fixed (Preview): layer bitmaps were `AlphaFormat.Opaque` → transparent image areas drawn opaque; now
    `Unpremul` (ffmpeg's straight alpha, verified). Video frames unchanged. Needs a manual look with a PNG with
    transparency in the running app.
  - Tests: Core `CompositionDrawPlanTests` (28: background/clip, vertical canvas, crop of each side at two decoded
    sizes, contain fit, scale/rotation 0/90/180/270/30/−90 and position = D018 transform, exact quarter turn,
    opacity + frame passed through, unknown source size, missing frame, order, culled/transparent/blank layers,
    text transform, viewport composition) and `MediaTimeTests` `ToFrameCeiling` (3); UI `CompositionDrawPlanTests`
    kept with unchanged expected values (API renamed) + "Preview plan without playback states = Core plan"; Export
    `ExportFrame.DrawPlan` test; new project `tests/Rendering.Tests` (50): `RasterGeometryTests` (black background,
    11 transform rows incl. sub-pixel position, 30°/45°, clipping, 5 crop rows with a quadrant pattern, opacity
    blend, straight alpha, order, vertical canvas, Preview letterbox clip, Preview == export bytes for pictures,
    rasterizer reuse over 300 frames with stable handles, contract errors) and `TextRenderingTests` (14 Preview ==
    export byte-equality cases: one/several lines, Left/Center/Right, 12/40/200 px, Segoe UI/Consolas/Times New
    Roman/missing family, rotation, scale, opacity, clipping at the canvas edge, trailing spaces; fallback = default
    family bytes; another family really used; box centred ±1 px; line alignment ±1 px; size and scale ×2 ±3 px;
    90° turn; colour and opacity; letterbox clip; preview at half size = export geometry / 2 ±2 px).
  - Mutations (all caught): old Opaque alpha → 1 failure (Rendering); viewport not applied → Core 1, UI 2; text box
    not centred → 6; crop not normalized → Core 5, UI 2; no clip → 2; no background → 17; alignment ignored → 1.
  - Full suite: 1276 (Core 349, Timeline 257, Project 259, UI 194, Export 29, Rendering 50, Video 138), 3
    consecutive runs green; build 0 warnings. App starts (shell initialized, no errors in the log).
  - Open before Steps 4/5: see the Step 3 report.
- Step 3 manually checked and accepted by the product owner (semi-transparent PNG in the running Preview; `Unpremul` stays).
- Step 4 done (D023 refinement) — offline audio PCM; no encoder/AAC/muxing/service/DI/UI:
  - Core `Playback/AudioPlacement.cs`: `AudioPlacement` (owned samples, source position for a timeline sample,
    placement of a decoded stream, decode request; 1× and other speeds) and `AudioMix` (gain, add, clamp).
    `AudioSpanReader` uses the placement instead of its inline formulas (ring buffer and underrun semantics unchanged;
    its mixing loop now `AudioMix.Add` over the ring's two contiguous pieces), `AudioMixer` uses `AudioMix.Clamp`,
    `AudioPipeline` `AudioMix.Gain`.
  - Export: `ExportAudioSource` (public, sequential `ReadAsync`, exactly `AudioSampleCount` frames of 48 kHz stereo
    float, silence where nothing plays) and `ExportAudioReader` (internal, per audible span, blocking, errors →
    `ExportException`). Muted clips / volume 0 are not decoded.
  - Test infrastructure: `FakeAudioDecoder.Fail(path, error)` and `FailAfter(path, samples)` (defaults unchanged);
    Export.Tests links `AudioFakes.cs`.
  - Tests: Core `AudioPlacementTests` (bounds, partition, span = timing, 1× exact ×4, speeds ×3, worked 0.25×/4× examples,
    resume/pause ×3, split ×3, trim ×3, SourceIn/SourceOut ×4, request) and `AudioMixTests` (6); Export
    `ExportAudioContractTests` (13: export == Preview pipeline + mixer, sample for sample — 1×/0.25×/4×/1.35× with trim,
    split, gap, hidden video track at 0.5, overlapping clip at 2, muted clip, muted track; exact 1× values; split without
    a seam; late-starting and short sources; decoder prerolls 20 000 / −3 000 / 0; clipping at +1 and −1 after the sum;
    silent project) and `ExportAudioSourceTests` (15: sequential exact count, whole frames, slow decoder waited for,
    open failures incl. ffmpeg missing, mid-stream failure, empty stream = silence, audible offline/unsupported → error,
    inaudible offline → silence without decoding, decoder only while the clip plays, cancellation, dispose); Video
    `ExportAudioIntegrationTests` (real ffmpeg, 0.25×/1×/4×: export == Preview exactly, bursts ±5.07 ms / ±0.05 ms at 1×,
    no drift, one stream opened and closed).
  - Mutations (all caught): speed placement with the 1× rule → Core 6, Timeline 1; pre-window samples not dropped → 1
    (needed the new preroll test — the fake's 100-sample preroll never produced a whole chunk before the window); export
    without clamp → 2; muted clips decoded → 6; Preview mixer without clamp → Timeline 1, Export 2; readers never
    released → 1.
  - Found: the ffmpeg decoder streams don't treat a non-zero exit after output as an error (audio: never; video: only
    before the first frame) — a mid-stream process failure would export as silence / a held frame. Not changed
    (touches Step 2 and realtime behaviour); proposal in the Step 4 report.
  - Full suite: 1338 (Core 380, Timeline 257, Project 259, UI 194, Export 57, Rendering 50, Video 141), 3 consecutive
    runs green; `--no-incremental` build of every project 0 warnings (App built to a scratch folder: the product owner's
    running app held its bin folder). No test leaves an ffmpeg process (the two running belonged to that app).
  - Open before Step 5: see the Step 4 report.
- Step 4 accepted (2026-09-24); follow-up before Step 5 — strict end of stream for the export:
  - Core: `VideoDecodeRequest.StrictEnd`, `AudioDecodeRequest.StrictEnd` (default false = the old behaviour).
  - Video: `FfmpegProcess.AbnormalExitAsync` (the one exit-code check); `FfmpegVideoFrameStream` throws `DecoderFailed` at the
    end when ffmpeg failed and nothing was delivered (unchanged) or the request is strict ("ended early after N frames");
    `FfmpegAudioStream` does the same when strict (before: never checked). `FfmpegVideoDecoder` / `FfmpegAudioDecoder` pass
    the flag through.
  - Export: `ExportPictureReader` and `ExportAudioReader` request `StrictEnd = true`. Preview readers don't set it.
  - Tests: Video `StrictEndOfStreamTests` (15; the "ffmpeg" is a script running the real ffmpeg with fixed arguments, then
    exiting with a chosen code): video and audio stream end × exit 3/0 × strict/not; cancellation while the end is pending
    (the script keeps stdout open 10 s) → `OperationCanceledException` within 8 s, no process left; `ExportFrameSource`
    with a crash after 10 frames → `DecodeFailed` at frame 9, normal exit → hold-last; `ExportAudioSource` crash after
    0.5 s → `DecodeFailed`, normal exit → silence; Preview `VideoPipeline` / `AudioPipeline` with the crashing decoder →
    last frame held (no DecodeError) / samples then silence. Every test checks `FfmpegProcess.LiveProcesses` and that no
    ffmpeg/ping it started is alive.
  - Mutations (all caught): video strict ignored → 2; audio strict ignored → 2; export video not strict → 1; export audio
    not strict → 1; strict everywhere (Preview changed) → 2.
  - Full suite: 1353 (Core 380, Timeline 257, Project 259, UI 194, Export 57, Rendering 50, Video 156), 3 consecutive runs
    green; every project built `--no-incremental` with 0 warnings (App to a scratch folder while the product owner's app
    was running). Afterwards the only ffmpeg alive belonged to that app.
- Step 5 done (D023 refinement) — ffmpeg encoder; no service/DI/UI/orchestration:
  - Experiments first (scratchpad, FFmpeg 9.0.1): colour (untagged = BT.601 matrix, red Y 81; `-colorspace` alone leaves
    primaries/transfer unknown; explicit scale + setparams = BT.709 limited ±1, all tags, RGB round trip ≤ 3), AAC priming
    (1024 samples, pts −1024, removed by the edit list; decoded = written samples; identical for single-pass and two-pass
    with stream copy), rational rates (exact pts and durations at 23.976/29.97/25/59.94/24/50, one frame included).
  - Core `Export/IExportEncoder.cs` (`IExportEncoder`, `IExportEncoding`). Video `FfmpegExportEncoder` +
    `FfmpegExportEncoding` (two passes, temp files next to the destination, move on success, cleanup otherwise);
    `FfmpegProcess`: optional stdin (`redirectStdin`, `Stdin`, `CloseStdin`), `Dispose` tolerates an unflushable stdin.
  - Tests (Video, real ffmpeg): `FfmpegExportEncoderTests` (17: command lines; 23.976/29.97/25/59.94 exact pts, duration,
    nb_frames, H.264 High yuv420p, BT.709 tags, every frame once in order; sub-frame project = 1 frame + 1 602 samples;
    colours ×2 (normal and padded stride): YUV vs BT.709 formula ±1, RGB round trip ≤ 3, byte order; AAC-LC 48 kHz
    stereo, 192 429 bit/s, peak unchanged; silent track; priming: pts −1024, start 0, duration_ts = samples, burst
    +0.5 sample; A/V sync 23.976/29.97/25, short project, audio starting later, several bursts: |Δ| ≤ 0.035 ms, audio end
    − video end ∈ [0, 1 sample]; audio up to the end / ending earlier) and `FfmpegExportEncoderFailureTests` (12: ffmpeg
    missing, folder missing, failing audio/video pass after reading and at once — existing destination untouched,
    success replaces the destination, cancelled write, write blocked by an encoder that never reads ended by
    cancellation < 5 s, cancelled completion, dispose of an unfinished encoding, order/count violations; every test:
    process counter at baseline, no ffmpeg/ping left, no temporary file).
  - Mutations: no BT.709 handling → 7 failures; rate as a rounded decimal → 6; output written straight to the
    destination → 5; exit code ignored → 2. An explicit kill-on-cancel for blocked writes survived its mutation (.NET
    already cancels the pending pipe write) and was removed again; the blocked-write test stays as the guard.
  - Full suite: 1382 (Core 380, Timeline 257, Project 259, UI 194, Export 57, Rendering 50, Video 185), 3 consecutive runs
    green; every project `--no-incremental` 0 warnings (App to a scratch folder, the product owner's app still running);
    only that app's ffmpeg alive afterwards.
  - Open before Step 6: see the Step 5 report.
- Step 5 accepted by the product owner (2026-09-24).
- Step 6 done (D023 refinement) — `ExportService` orchestration; no export UI / dialog / settings / lifecycle:
  - Export `ExportService : IExportService`: Preparing (one rasterizer from `Func<ICompositionRasterizer>`, encoder
    start) → Audio (`ExportAudioSource` → `WriteAudioAsync`, 0.5 s chunks, until the source ends) → Video (per frame
    `ExportFrameSource` → `ExportFrame.DrawPlan()` → rasterizer → one reused BGRA canvas, stride = width · 4 →
    `WriteFrameAsync`) → Finalizing (`CompleteAsync`). `Task.Run`; progress = existing `ExportProgress` (Preparing 0/1,
    Audio samples, Video frames, Finalizing 0/1 → 1/1 after success). No preflight re-checks, no composition/audio/output
    logic; failures and cancellation propagate unchanged; `await using` disposes sources, rasterizer and the unfinished
    encoding on every other exit. `IsAvailableAsync` = `IFfmpegLocator`. Export.csproj: Logging.Abstractions (as Video).
  - App DI: `IExportEncoder` → `FfmpegExportEncoder`, `Func<ICompositionRasterizer>` → `new AvaloniaCompositionRasterizer()`,
    `IExportService` → `ExportService` (resolved from `AddAiVideoEditor()` in a scratch console: ffmpeg found, available).
  - Tests: Export `ExportServiceTests` (21, fakes: stage order + exact audio = `ExportAudioSource`'s PCM + every frame's
    plan/canvas/stride; progress monotonic per stage, 1/1 only after completion; off the calling thread; one rasterizer
    per export; cancellation during audio / video / completion / blocked encoder writes (audio, frame) / before start;
    video + audio decode failure, ffmpeg missing in the decoder, encoder failures at start/audio/frame/complete incl.
    `OutputFailed` — same exception instance; rasterizer failure; every part disposed, encoding never completed).
    New project `tests/ExportEndToEnd.Tests` (26, real ffmpeg + Avalonia, links `RenderHarness.cs`, `FfmpegTools.cs`,
    `ExportEncoderHarness.cs`; Timeline/Video grant it internals): project → preflight → service → MP4, every export
    checked with ffprobe (H.264 yuv420p, size, exact `r_frame_rate`, `nb_frames`, AAC-LC 48 kHz stereo, decoded sample
    count = `AudioSampleCount`, duration ±1.1 ms), only the MP4 in its folder, `FfmpegProcess.LiveProcesses` = 0.
    Composition (9 scenes: solid, crop, scale, rotation, opacity, text, layers, vertical 180 × 320, hidden track):
    decoded MP4 vs encoded canvases mean |Δ| ≤ 3, Preview (its `VideoPipeline` + `CompositionView` at canvas size)
    byte-equal to the export canvas at frames 0/12/24, scene-specific pixels. Audio: one clip, overlap + muted clip +
    muted track, hidden video track keeps sound (black picture), 2× speed (bursts on the 0.25 s grid ±10 ms), no
    audio → silent AAC track. A/V sync at 23.976/29.97/25 (white frames vs burst onsets ±1 ms, no drift), 3-frame and
    1-frame projects. Failures: success replaces an existing file; cancellation in audio/video/finalizing; source
    deleted after preflight → `DecodeFailed`; folder deleted → `OutputFailed`; ffmpeg missing → `EncoderUnavailable` —
    destination byte-identical, no temporary file, no process.
  - Mutations (all caught): export on the caller's thread → 3 failures; Finalizing 1/1 before `CompleteAsync` → 4;
    encoding not disposed → 8; failures wrapped into `ExportException` → 8; cancellation turned into
    `ExportException` → 5; a rasterizer per frame → 3; frame source not disposed → 8; audio / frame write without the
    token → 1 each (needed the new blocked-encoder test). The first "caller's thread" mutation hung the off-thread test
    (its blocking factory waited forever); the wait is now bounded so the mutation fails instead.
  - Found: at 2× a burst starting exactly at the clip's `SourceOut` leaves ~10 ms of energy before the clip end
    (atempo window, Step 4 placement — Preview identical); not changed, the speed test ends the clip away from a burst.
- Step 6 accepted by the product owner (2026-09-25).
- Step 7 done (D023 refinement) — export UI; no pipeline change, no Step 8:
  - UI: `ExportWorkflow` (UI/Services, like `ProjectFileWorkflow`): `ExportPreflight` without the output file (its
    output-file issues wait for the second check) → errors listed apart from warnings, stop; warnings → Continue /
    Cancel → save-file picker (`IFilePickerService.PickSaveFileAsync`, new; `.mp4` filter, starts at
    `LastExportSettings.OutputPath` or the project folder + name; no overwrite prompt of its own) → `ExportPreflight`
    with the file (job + snapshot) → "Replace file?" for an existing file → `EditingLock` + modal progress window →
    `IExportService.ExportAsync` → close window, unlock → outcome: success (dialog + status with the path,
    `LastExportSettings.OutputPath` set, not dirty, no undo step), cancelled (status only), failure per `ExportFailure`
    (dialog + status, logged as warning), anything else = unexpected error (generic dialog, logged as error with the
    exception). Picker cancelled → silent.
  - `ExportProgressViewModel` (stage, done/total, percent = done/total, detail text, Cancel = `cts.Cancel()` once,
    "Cancelling…"); the service's reports only store the latest value, the window's `DispatcherTimer` pulls it
    (Preview tick pattern) — no marshalling, nothing after close. `IExportProgressDialog` /
    `AvaloniaExportProgressDialog` (modal, built in code like `AvaloniaDialogService`; title-bar close = Cancel; closes
    only via `Close()` after `ExportAsync` returned).
  - `EditingLock` (singleton, shared): Toolbar New/Open/Save/Save As/Undo/Redo/Import/Export, Timeline Split/Delete/
    +Video/+Audio/+Text and drag/trim/drop, Inspector clip fields (`IsEditingAllowed`, edits rejected and fields
    re-synced), Media Browser Import/Add to Timeline — all disabled while locked; playhead, zoom, snapping, selection,
    playback unchanged. Optional constructor parameters (old call sites unchanged); undo/redo semantics unchanged.
  - DI: `EditingLock`, `IExportProgressDialog` → `AvaloniaExportProgressDialog`, `ExportWorkflow`; the whole graph
    validated with `ValidateOnBuild` in a scratch console.
  - `LastExportSettings`: session state on the project, updated only after a successful export, never dirty/undoable.
    No new settings. (Serialization removed at the closeout, see below.)
  - Tests: UI `ExportWorkflowTests` (22: command availability/lock; empty project; errors vs warnings; warnings accept /
    stop; ffmpeg missing; picker cancelled silently; picker defaults; `.mov` reported not renamed; replace confirmation;
    success path / status / `LastExportSettings` / not dirty / no undo / nothing imported; progress values stage by stage
    and success only after the task; snapshot fixed at start; cancel = token only, window and lock kept while unwinding,
    no second export, not an error, destination unchanged; closing the window cancels; editing lock across panels incl.
    an Inspector edit and a drop while locked; 4 failure categories; unexpected error logged with the exception;
    distinct messages). Rendering `ExportProgressDialogTests` (2, real Avalonia window: timer shows progress, title-bar
    close only cancels, Cancel once, `Close` closes). UI internals visible to Rendering.Tests (window accessor).
  - Mutations (all caught): no editing lock → 2; no replace question → 1; cancellation as failure → 3;
    `LastExportSettings` on every outcome → 5; window closed on cancel → 1; first preflight ignored → 3; Inspector not
    guarded → 1; Timeline not locked → 1; unexpected error not logged → 1.
  - Manual test plan: `docs/EXPORT_MANUAL_TEST_PLAN.md` (14 scenarios) — run at the closeout, see below.
- Step 7 accepted by the product owner (2026-09-25) with one contract correction, done:
  - `LastExportSettings` is session-only (D023 corrected): `ProjectSerializer` no longer writes or reads it
    (`ProjectFileDto.LastExportSettings`, `ExportSettingsDto` and both mappings removed; recovery files use the same
    DTO). A `lastExportSettings` of an older file is ignored as an unknown property (D014) — any content, also formats
    that used to make the file damaged; an opened project starts empty. `ExportWorkflow` unchanged.
  - Tests: Project `ExportSettingsPersistenceTests` rewritten (10: project file and recovery file never contain it; an
    older file's value is not restored; 6 older values incl. unknown formats, null, number, string, array load; Save
    after an export through `ProjectService` writes no `lastExportSettings`, keeps the value for the session, Open
    starts empty); `ProjectSerializerRoundTripTests` expects it not restored; one validation test no longer removes it.
- Step 7 manual test (2026-09-25): `docs/EXPORT_MANUAL_TEST_PLAN.md` 14/14 PASS, 0 FAIL, 0 BLOCKED — the current build driven
  through its real UI (UI Automation; scenario projects written with `ProjectSerializer`, outputs mostly at the picker's
  suggested path). Successful MP4s checked with ffprobe (H.264 High yuv420p BT.709, exact rate/frames, AAC-LC 48 kHz
  stereo, full decode clean, flashes vs bursts ≤ 0.02 ms) and played with Windows Media Foundation; Preview == export on
  text / transforms / hidden track; cancel in Audio / Video / Finalizing and a source vanishing mid-export leave an
  existing output byte-identical and no temporary file; Replace only after confirmation; re-export byte-identical.
  Observed, not export defects: the Preview's idle decoders keep the sources under the playhead open (they can't be
  renamed while the project is open — Phase 5 behaviour); the known close hang after opening a project reproduced
  (not investigated, still open).
- Step 7 closed (2026-09-25).
- Step 8 — end-to-end Preview ↔ Export parity + measurement (D023 "Refined in Step 8"); tests only, no production change.
  Product owner decisions at the discovery (2026-09-25): A1 + A2 + A3 (1–2 cases) + A4; B software decoding in the tests
  (`Hardware = Auto` not a criterion); C measurement as a scratch tool, not in the suite, no `ExportService`
  instrumentation; D stop and report any production divergence (none was found); E sources PNG alpha, JPEG,
  display-matrix 90°, speed 0.25×/2×/4×, source rate ≠ project rate, 4K (no VFR/HEVC); F 4K/long/measurement scenarios
  out of the fast suite (`HeavyFfmpegFact` — only with `AIVE_HEAVY_TESTS=1`). Sub-steps, each accepted separately:
  - 8.1 generators (`E2EMedia`: `Pattern(rate, w, h, seconds, crf)`, `Pattern1080`, `Pattern4K`, `PngAlpha`, `Jpeg`,
    `Rotated90`; `ProjectBuilder.Image`) and `GeneratedMediaTests` — every generated file checked on itself with
    ffprobe/ffmpeg and as the app sees it (analysis, decoder, `PlaybackSnapshotBuilder`); 6 mutations caught.
  - 8.2 `ExportParityCanvasTests` (15, A1): the Preview draws the export canvas byte-equal for PNG alpha (plain and
    transformed), JPEG (plain and cropped), rotated video (landscape pillarbox, portrait), speed 0.25×/2×/4×, five
    source/project rate pairs and a mixed scene; independent oracle: the source frame chosen by an exact D009/D022
    computation, decoded by ffmpeg, against the canvas. 5 mutations caught (the ones in shared code only by the
    independent checks: oracle, alpha bands, pillarbox).
  - 8.3 `ExportParityScaledTests` (4 × 1080p + 2 heavy 4K, A2): decisions 1a (flat colour R ≤ 4, G ≤ 3, B ≤ 4) and 2a
    (geometry ±1 px on luma); bar edges by the mid-level crossing (the Preview's upscaled edges are soft). 5 mutations
    caught (shift 2 px, scale 1.01, B +5, G +4, next frame).
  - 8.4 `ExportParityViewportTests` (2, A3) and the shared `ParityMetrics`: 1920×1080 in 960×540 (scale ½) and
    1080×1920 pillarboxed in 960×540 (scale 0.28125, offset 328.125) against an exact area reduction of the canvas;
    4 mutations caught. One `Project.Tests` host hung once (1 of 23 runs, not reproducible, project unchanged) → final
    `--blame-hang` check at 8.7.
  - 8.5 `ExportParityEncodedTests` (5 picture + 4 sound, A4), decision 5c: MP4 → Preview = geometry (≤ 0.01 % of the
    pixels outside the ±1 px luma mask, decision 5a), bar edges ±1 px, same source frame (not in the static PNG scene);
    colour not a criterion there. Sound: AAC vs the Preview's `AudioPipeline` + `AudioMixer` — same length, lag and
    onsets ≤ 10 ms, SNR ≥ 20 dB sanity bound; 4/4 (SNR 28.4–39.5 dB, lag 0, onsets ≤ 0.48 ms). Mutations: P1 shift,
    P2 scale, P3 next frame, P5 BT.601 matrix tagged BT.709, S1 audio +15 ms, S2 volume ×0.5 caught; P4 (export colour
    B+6) intentionally not caught by this leg — the canvas-level parity (8.2, Step 6 composition) and `Rendering.Tests`
    catch it. The codec leg MP4 → export canvas: decision L1-c — no tolerance, measurement first.
  - 8.6 measurement only (scratch `CodecMeasure`, outside the repository; no thresholds, suite unchanged; two identical
    runs): MP4 → export canvas on the 30 existing scenes, every frame (820), 8-bit RGB, split into floor (same BT.709
    4:2:0 conversion, libx264 `-qp 0`, vs canvas) and quant (MP4 vs that lossless reference). Summary and limits in D023
    (2); per scene:

    | Scene | Size, frames | Total mean / p99 / max / PSNR | Floor mean / max / PSNR | Quant mean / p99 / max / PSNR |
    |---|---|---|---|---|
    | comp/solid | 320×180, 25 | 1.00 / 1 / 1 / 48.1 | 1.00 / 1 / 48.1 | 0.00 / 0 / 0 / lossless |
    | comp/opacity | 320×180, 25 | 1.33 / 2 / 2 / 45.1 | 1.33 / 2 / 45.1 | 0.00 / 0 / 0 / lossless |
    | comp/hidden | 320×180, 25 | 1.00 / 1 / 1 / 48.1 | 1.00 / 1 / 48.1 | 0.00 / 0 / 0 / lossless |
    | comp/text | 320×180, 25 | 0.14 / 3 / 62 / 50.5 | 0.01 / 1 / 67.7 | 0.14 / 3 / 62 / 50.6 |
    | canvas/png-alpha | 320×180, 25 | 2.60 / 52 / 197 / 26.7 | 2.55 / 193 / 26.7 | 0.21 / 4 / 19 / 50.4 |
    | canvas/jpeg | 320×180, 25 | 1.49 / 14 / 47 / 38.5 | 1.30 / 40 / 39.3 | 0.45 / 5 / 13 / 46.7 |
    | comp/crop | 320×180, 25 | 0.78 / 13 / 70 / 39.9 | 0.62 / 36 / 42.2 | 0.37 / 8 / 62 / 43.8 |
    | comp/scale | 320×180, 25 | 1.99 / 62 / 255 / 26.5 | 1.87 / 255 / 26.6 | 0.36 / 8 / 84 / 43.3 |
    | comp/rotation | 320×180, 25 | 1.11 / 17 / 75 / 37.3 | 0.86 / 43 / 38.8 | 0.51 / 9 / 67 / 42.5 |
    | comp/vertical | 180×320, 25 | 2.32 / 62 / 255 / 26.3 | 2.18 / 255 / 26.4 | 0.43 / 9 / 65 / 42.2 |
    | comp/layers | 320×180, 25 | 2.39 / 28 / 255 / 31.3 | 2.00 / 254 / 31.9 | 0.93 / 12 / 60 / 39.9 |
    | canvas/png-alpha-transformed | 320×180, 25 | 2.52 / 32 / 245 / 29.9 | 2.13 / 239 / 30.4 | 0.94 / 11 / 77 / 40.1 |
    | canvas/jpeg-crop | 320×180, 25 | 3.02 / 47 / 236 / 27.2 | 2.72 / 236 / 27.4 | 0.79 / 10 / 60 / 41.1 |
    | canvas/rotated-landscape | 320×180, 25 | 2.35 / 65 / 255 / 26.2 | 2.21 / 255 / 26.4 | 0.44 / 9 / 64 / 42.2 |
    | canvas/rotated-portrait | 180×320, 25 | 1.92 / 24 / 74 / 34.9 | 1.59 / 50 / 36.1 | 0.75 / 11 / 64 / 41.0 |
    | canvas/mixed | 320×180, 30 | 4.02 / 73 / 255 / 24.4 | 3.61 / 255 / 24.5 | 1.10 / 13 / 94 / 38.9 |
    | canvas/rate-24000_1001-30000_1001 | 320×180, 30 | 1.64 / 18 / 85 / 36.5 | 1.33 / 39 / 38.5 | 0.72 / 11 / 77 / 40.8 |
    | canvas/rate-25_1-30000_1001 | 320×180, 30 | 1.63 / 18 / 75 / 36.6 | 1.33 / 39 / 38.5 | 0.72 / 11 / 59 / 40.8 |
    | canvas/rate-60000_1001-25_1 | 320×180, 30 | 1.64 / 19 / 81 / 36.6 | 1.33 / 39 / 38.5 | 0.72 / 11 / 62 / 41.0 |
    | canvas/rate-50_1-24000_1001 | 320×180, 30 | 1.64 / 19 / 72 / 36.6 | 1.33 / 38 / 38.5 | 0.73 / 11 / 60 / 41.0 |
    | canvas/rate-30000_1001-25_1 | 320×180, 30 | 1.64 / 19 / 72 / 36.5 | 1.33 / 39 / 38.5 | 0.73 / 11 / 68 / 40.8 |
    | canvas/speed-5 | 320×180, 25 | 1.53 / 16 / 72 / 37.4 | 1.36 / 39 / 38.4 | 0.55 / 7 / 50 / 44.0 |
    | canvas/speed-40 | 320×180, 25 | 1.67 / 19 / 72 / 36.2 | 1.35 / 43 / 38.2 | 0.76 / 11 / 76 / 40.6 |
    | canvas/speed-80 | 320×180, 25 | 1.74 / 20 / 75 / 35.9 | 1.40 / 43 / 37.8 | 0.83 / 12 / 62 / 40.1 |
    | scaled/1080p-full | 1920×1080, 5 | 1.08 / 11 / 77 / 40.6 | 0.94 / 37 / 43.0 | 0.27 / 8 / 58 / 44.7 |
    | scaled/1080p-scaled | 1920×1080, 5 | 0.60 / 12 / 255 / 33.4 | 0.57 / 255 / 33.5 | 0.12 / 4 / 66 / 47.8 |
    | scaled/1080p-rotated | 1920×1080, 5 | 0.90 / 19 / 255 / 32.3 | 0.82 / 255 / 32.4 | 0.21 / 6 / 84 / 45.5 |
    | scaled/1080p-on-720p | 1280×720, 5 | 2.11 / 43 / 255 / 29.2 | 2.02 / 240 / 29.4 | 0.38 / 10 / 63 / 42.7 |
    | viewport/landscape | 1920×1080, 5 | 1.27 / 10 / 173 / 40.0 | 1.12 / 141 / 41.8 | 0.33 / 7 / 57 / 44.9 |
    | viewport/portrait | 1080×1920, 25 | 1.29 / 14 / 142 / 38.0 | 1.21 / 112 / 38.4 | 0.30 / 5 / 75 / 47.3 |

    Not measured in Step 8: export throughput, memory, handles, cancel latency (discovery C1) — 8.6 was scoped to the
    codec error by the product owner; throughput stays Phase 9.
  - 8.7 closeout (this entry): D023 "Refined in Step 8" separates the normative criteria, the 8.6 results and the
    measured H.264 characteristics; no codec tolerance added (open decision); ARCHITECTURE and ROADMAP updated,
    DEVELOPMENT_PLAN unchanged (Phase 8 stays unchecked until acceptance); verification below. The planned Step 9
    (closeout) is done as part of 8.7.
- Step 8 closed (2026-09-25); Phase 8 awaits the product owner's review of Step 8.7.
- Phase 8 accepted by the product owner (2026-09-25): Step 8.7 reviewed, work committed as `8786491`, PR #6 to
  `main`. The codec leg MP4 → export canvas stays an open product decision (L1-c, no numeric tolerance).

## Phase 7 (complete)

Phase 7 — Basic editing: **complete** — manually accepted by the product owner on 2026-09-24
(Steps 1–9, last checkpoint `48a3f54`; Step 10 closeout, see below), branch `feat/phase-7-basic-editing`
(from `main` `9fd38e7`, which contains Phases 5 and 6). Scope (DEVELOPMENT_PLAN): speed, volume, opacity,
transform, crop, text — static per-clip properties, no keyframes.

### Phase 7 — Basic editing (complete)

Product decisions (product owner, 2026-09-23):
- Compositing: the Preview draws layers on the GPU with Avalonia `DrawingContext`; D010's
  "topmost track wins" is dropped. Core owns the pure geometry (matrix, crop, opacity), the
  Preview only renders it. The rules must be reproducible by the Phase 8 export (→ D018).
- Canvas: `ProjectSettings.FrameWidth × FrameHeight` only (default 1920×1080), never taken from
  the first video; no resolution UI in Phase 7. The Preview must stop assuming a fixed 960×540.
- Speed: 0.25×–4×, UI step 0.05×, exact rational (not `double`), pitch preserved. Changing speed
  keeps Start, recomputes Duration / source range, is rejected on overlap (no ripple).
  `project.json` v2 that still reads v1 (→ D022; D021 is Step 8, text clips).
- Text: multiline; only the existing properties (text, font, size, color, alignment, position,
  scale, rotation, opacity). No outline/background/shadow/stroke.
- Transform UX: numeric Inspector fields only; no handles on the Preview.
- Volume: 0–200 % linear in the UI, linear gain internally; no dB.
- Mute: a separate state (never Volume = 0) for every clip that can carry audio, incl. VideoClip.
- Only the primary selected clip is edited; no multi-selection editing.
- Playhead / zoom / snapping are session state (D015, decided).

Steps: 1 D015 + zoom/snapping reload fix · 2 clip property editing foundation (edit service,
command, undo merging that respects the save point) · 3 load validation of property ranges ·
4 volume/mute end to end · 5 composition model in Core · 6 multi-layer playback with reader reuse
across property-only snapshot updates · 7 Preview rendering + Transform/Opacity/Crop in the
Inspector · 8 text clips · 9 speed · 10 closeout.

- Step 1 done — D015 decided (session state). Fixed: `TimelineViewModel` read zoom and snapping
  only in its constructor, so after New/Open/Recover it kept the previous project's values and
  the snapping toggle wrote them into the new sequence. On `ProjectChanged` it now cancels any
  gesture, clears the selection and reads zoom/snapping from the new sequence.
  Tests: `UI.Tests/TimelineSessionStateTests.cs` (3: New, Open restores saved values, playhead/
  zoom/snapping never dirty or undoable). Mutation: old handler → 2 failures.
- Step 2 done (D017) — clip property editing foundation, no UI/playback/persistence yet:
  `ITimelineEditService.SetClipProperties(clipId, ClipPropertyChange)` with typed
  `VisualProperties` / `AudioProperties` / `TextProperties` (Core/Entities/ClipProperties.cs) and
  `ClipPropertyLimits`; `ClipPropertyValidator` (kind, ranges, finite, crop, `#RRGGBB`; moved to
  Core in Step 3; locked track → reject without changes); `ClipPropertyValues` (capture/apply/diff) and
  `SetClipPropertiesCommand` (absolute before/after); `IMergeableCommand` + merging in
  `UndoRedoService` (not into the save point, not after Undo, new instance on merge, step removed
  when edits cancel out); `NotifyingCommand` forwards merging. Model: `VideoClip.IsMuted` added
  (split copies it). Tests: `Core.Tests/UndoRedoMergeTests.cs` (10),
  `Timeline.Tests/ClipPropertyEditTests.cs` (55 incl. theory cases; bit-exact undo/redo of all
  properties). Mutations: no save-point guard → 3 failures; merge after Undo → 2; no cancel-out → 1.
  Pending for later steps: persist `VideoClip.IsMuted` (Step 3), honour it in playback (Step 4).
- Step 3 done (D017) — load validation + `VideoClip.isMuted` in format v1 (optional, no version
  bump; older files load unmuted). `ClipPropertyValidator` moved to Core and gained
  `ValidateCurrent(clip)`; `ProjectSerializer` runs it on every clip read (project.json and
  recovery files share this path), so NaN/∞, out-of-range values, crop edges outside [0, 1) or
  opposite edges summing to ≥ 1, empty/missing font, bad color, over-long text → `ProjectFileException`
  before the current project is touched. Properties of another clip kind can't be represented
  (one DTO per clip type); stray JSON properties stay ignored (D014).
  Tests: `Project.Tests/ClipPropertyPersistenceTests.cs` (45 incl. theory cases: bit-exact round
  trip of every Phase 7 property, mute written/read, a literal Phase 6 v1 file without `isMuted`,
  30 damaged values, 8 non-finite literals, foreign properties ignored) and
  `ProjectServiceTests.Failed_open_of_a_project_with_an_invalid_clip_property_changes_nothing` (3:
  project, history, save point and events untouched). Finding: System.Text.Json reads `1e999` as
  ∞ — only the new validation rejects it; `NaN`/`"Infinity"` are already refused by the parser.
  Mutations: no validation on load → 36 failures; `isMuted` not read → 2, not written → 2; crop
  sum allowed to reach 1 → 2; NaN passing the range check → 4 (edit tests; on load the parser
  already blocks NaN).
- Step 4 done (D013 refinement, D017) — volume/mute end to end:
  - Inspector: AUDIO section (volume 0–200 %, step 1, linear; Mute checkbox, not focusable so
    Space stays Play/Pause) for AudioClips and VideoClips with an audio stream. Edits only via
    `SetClipProperties`; `ShowClip` fills the fields under a sync guard; rejected edit → status
    message + fields back to the model. `InspectorViewModel` now takes `ITimelineEditService` +
    `StatusService`.
  - Playback: `AudioSpan.IsMuted` / `EffectiveGain`; muted VideoClips and AudioClips stay in the
    snapshot (reader keeps running); `PlaybackSnapshot.DiffersOnlyInMix`; `PlaybackService` handles
    such updates without a resync (same VideoPipeline, readers, seek generation, picture;
    `AudioPipeline.UpdateMix`). Found by the existing `SupersededPipelines` test while
    implementing: the picture "current" version must be recorded where pipelines are created,
    otherwise a seek after a mix-only update never became current (fixed; regression test).
  - Tests: Core `PlaybackSnapshotMixTests` (6) + builder test updated; Timeline
    `MixUpdatePlaybackTests` (7: no pipeline/reader/generation change and no buffering over 9 edits
    incl. undo/redo and an opacity edit, paused, seek after mix-only update, mute silences only the
    clip's own audio and unmute restores its volume, volume 0 ≠ mute, clip starting muted, picture
    change reuses every audio reader); UI `InspectorAudioTests` (11: visibility, no echo edits incl.
    a 1/3 volume, one undo step / merged spins, undo/redo refresh, mute vs volume, rejection,
    selection follow, save → reopen, playing without decoder reopen); Video
    `MixUpdateIntegrationTests` (real ffmpeg: no ffmpeg process started, sine silent when muted,
    half amplitude at 50 %). `SupersededPipelines…` now makes a real timeline change (an identical
    snapshot is mix-only now). Mutations: no mix-only path → 4 failures (Timeline 2, UI 1, Video 1);
    mixer ignoring mute → 3; muted audio clips dropped from the snapshot → 4; no Inspector sync
    guard → 1; video mute not passed to the snapshot → 4.
- Step 5 done (D018) — composition model, pure Core (no playback/UI change):
  - `Core/Composition`: `Affine2D` (column vectors, Y down, exact quarter turns), `FrameSize`, `RectD`,
    `PointD`, `CompositionMath.Layout` / `TextTransform`, `LayerGeometry`, `CompositionLayer` →
    `PictureLayer` (`OccludesBelow`) / `TextLayer`, internal `ExactRational` (BigInteger) for the
    coverage proof. `ClipPropertyValidator.ValidateVisual` public; `VisualProperties.Default`.
  - Semantics fixed from the existing model: Position = centre offset from the canvas centre in canvas
    pixels (+Y down); rotation clockwise around the picture centre; fit = contain, before scale.
  - Snapshot: `PictureSpan.Visual` / `SourceSize` (from metadata), `TextSpan` + `VideoLayer.Texts`,
    `PlaybackSnapshot.Canvas`, `LayersAt(time)`. `DiffersOnlyInMix` → `DiffersOnlyInPresentation`
    (also ignores picture/text properties, source size, canvas) so a property edit still keeps every
    decoder (one-line call change in `PlaybackService`; Step 4 regression tests unchanged and green).
  - Tests: `Core.Tests/CompositionMathTests.cs` (40 incl. theory rows; exact matrices: landscape,
    portrait, letterbox, crop of each side, square crop, scale < 1 / > 1, 0/90/180/270/−90/±360 and
    30°, clockwise sign, position, opacity 0/0.5/1, all combined, five order-of-operation proofs,
    vertical and other canvases, exact-edge coverage incl. the double nearest 4/3, text transform,
    invalid input); `CompositionLayersTests.cs` (20: bottom-to-top by track order, time ranges,
    culling and its limits — transparency, 0.999 opacity, scale 0.99, crop, 0.5 px offset, rotation —,
    provable cover with crop/zoom and at 45°, images/offline/unknown size never cull, invisible
    clips, text layers, text and media on one track, vertical project, PictureAt unchanged);
    `PlaybackSnapshotMixTests` extended to presentation changes. Mutations (all caught): position
    before rotation → 2 failures, fit before crop → 2, rotation around the canvas origin → 11, scale
    not around the centre → 7, no culling → 3, occluder ignoring opacity → 2, image as occluder → 1,
    double-rounded coverage → 1, rotation sign flipped → 7, opacity-0 layers kept → 2. (Scale and
    rotation commute for uniform scale, so their mutual order is not observable.)
  - Coverage review (product owner): for arbitrary angles `CoversCanvas` never uses the bounding
    box — it inverts the layer matrix, maps all four canvas corners into the crop rectangle and
    requires them inside by a 10⁻⁶ margin (doubt → false). Added: 45° picture whose AABB covers the
    canvas but whose corners don't → false and the layer below stays in `LayersAt`; 30° × 2 with all
    corners inside → true and culls; conservative edge at 45°. Mutation "AABB instead of inverse
    containment" → 6 failures (incl. both new regression tests). Core tests: 243.
  - Flaky tests found before the checkpoint commit (full parallel runs, ~1 in 8): `MixUpdatePlayback
    Tests.VolumeAndMuteWhilePaused…` ("video reader reopened") and the Phase 5 `AudioPipelineTests.
    SnapshotUpdate_ReusesReaders…`. Cause: decoder-open counts depended on background timing — a
    reader of a pipeline retired right after creation still called `OpenAsync` from its task (with a
    cancelled token), and a new reader's open could land before or after the assertion.
    Product fix (pre-existing since Phase 5): `SpanReader` / `AudioSpanReader` check cancellation
    right before opening, so a reader retired before its task ran no longer starts an ffmpeg process
    only to kill it. Test fixes: `PlaybackService.RetiringSettledAsync()` (internal; Timeline internals
    now visible to UI.Tests) — count-based tests capture only after every retired pipeline is
    disposed; `AudioPipelineTests` waits for the opens it expects. Verified: 10 consecutive full-suite
    runs green (824 tests each).
  - Open for Step 6: rotation metadata of phone videos (ffprobe gives the coded size; D018
    consequences); how placeholders (offline/unsupported/decode error) are drawn in a composited
    preview.

- Step 6 (in progress) — product decisions (2026-09-23): orientation + per-layer playback state in
  Step 6, multi-layer rendering stays Step 7; `PlaybackFrame` keeps a compatibility picture for the
  current Preview and adds the full `LayerPicture` list; stale metadata is re-probed silently on Open
  (not dirty, missing files stay offline); Resolution shows the display size; placeholders take the
  clip's geometry (fallback: whole canvas) and never cull; a layer uncovered without a seek is
  Pending (no new seek generation, no Buffering); no reader reuse across structural changes; no
  decoder limit (measure); rotation 0/90/180/270 only, odd/mirrored → log + decoder-size fallback;
  SAR out of scope.
  - 6a done — orientation/display size: `MediaMetadata.DisplayRotation` (clockwise, null = not a
    right angle) + `DisplayWidth/DisplayHeight` (size of the frames the decoder delivers), `Width/Height`
    stay coded; `NeedsDisplaySizeProbe`. Probe (`Video/DisplayOrientation`): stream display matrix or
    legacy `rotate` tag → else first-frame display matrix (EXIF JPEGs) → else 0°, interpreted exactly
    like ffmpeg's autorotate (measured with ffmpeg 9: ±90 → transposed; 180 same size; odd angle →
    coded size; mirror detected by the matrix determinant, hflip alone reads −180). Decoder passes
    `-autorotate` explicitly. `project.json` v1: optional `displayRotation/displayWidth/displayHeight`;
    older metadata is kept (Duration etc. stay usable, missing files stay offline) and re-probed in the
    background by `MediaAnalysisCoordinator` (status stays Completed, not dirty, failure keeps the saved
    metadata); inconsistent values drop the metadata (D014). Builder: `SourceSize` = display size
    (null when unknown → no geometry, never culls); `AssetState` includes it. Inspector / Media Browser
    show the display size (+ "Rotated 90°"). Found + fixed: with an odd display angle ffmpeg prints a
    warning without a newline, gluing showinfo's time-base line onto it — `ShowInfoParser` no longer
    requires its prefix at the line start (such files decoded as DecodeError before).
    Tests: Video `DisplayOrientationTests` (rule: 26 cases; real ffmpeg: 9 files incl. EXIF JPEG —
    probed display size == decoded frame size), 2 `ShowInfoParserTests`; Project
    `MediaOrientationPersistenceTests` (10); Core 3 builder/asset-state tests (+ fixture display sizes);
    UI `MediaOrientationRefreshTests` (6). Full suite 876 green.
  - 6b/6c done (D019) — playback contract + multi-layer `VideoPipeline`: `LayerPicture` /
    `LayerPictureState` (Frame, Text, Pending, Offline, Unsupported, DecodeError; `IsCurrent` per layer;
    `PlaceholderArea(canvas)` = clip geometry or whole canvas), `PlaybackFrame.Layers` (bottom to top) +
    `Canvas`, compatibility `Picture` = topmost picture layer (the Preview is unchanged). Pipeline:
    readers for every decodable layer of `LayersAt` (+ prefetch at the next edge), keyed by clip,
    closed when no longer needed; per-layer late frames; runtime failures become placeholders and stop
    occluding (`LayersAt(time, mayOcclude)`); Ready = every layer at the start frame;
    `UpdatePresentation(snapshot)` from `PlaybackService` for presentation-only snapshots (same
    generation, no buffering, `_pictureSnapshotVersion` follows).
  - 6d done — tests: Timeline `MultiLayerPlaybackTests` (15: order, culling without reader, text,
    opacity 1 → 0.9 while playing with a gated decoder = Pending then Frame without new generation/
    buffering, back to 1 closes reader + stream, 20 toggles without leaks, per-layer late frame via
    the fake's new `HoldAfter`, seek ready for either slow layer, offline/unsupported placeholders
    with geometry that never cull, unknown size → whole canvas, failing occluder uncovers the layers
    below, vanished file → Offline, prefetch, dispose); Video `MultiLayerIntegrationTests` (real
    ffmpeg: one process per visible layer, opened/closed with opacity, none after Dispose). All
    Phase 5 and Step 4/5 tests unchanged and green. Mutations (9): UpdatePresentation ignored → 5
    failures, presentation change resyncing → 4, failed layer still occluding → 2, readers never
    closed → 1, no per-layer late frame → 1, Ready on the first layer only → 1 (after making the seek
    test a theory over both layers — the first version missed it), compatibility picture from the
    bottom → 5, placeholder always full canvas → 1, no prefetch → 2.
  - Load (temporary measurement, not in the repo): 1/2/4 layers of 1080p30 H.264, hardware and
    software decoding — 0 % late frames over 4 s, one ffmpeg process per layer; no decoder limit needed
    so far (rendering in Step 7 and 4K sources not measured).
  - Found: `SeekAsync` without `Update()` calls never completes when a source's preroll exceeds
    `BufferFrames` (e.g. MPEG-TS at frame 40) — unchanged since Phase 5, the Preview always ticks;
    documented in D019, not changed.
  - Full suite: 892 tests, 5 consecutive runs green. App starts (shell initialized, no errors).

- Step 6 checkpoint: commit `8592d10`.
- Step 7 (D020) — multi-layer Preview + visual Inspector:
  - 7a: `PreviewViewModel.Layers` / `Canvas` / `AreLayersCurrent`; keeps polling while a layer is
    pending or late (a layer uncovered while paused appears), keeps the previous layers while
    buffering, clears them on project change.
  - 7b/7c: `UI/Rendering`: `CompositionDrawPlan` (pure: viewport canvas → control, per-layer
    operations bottom to top, decoded-pixel source rect, geometry from the decoded size when unknown,
    placeholders in `PlaceholderArea`, text, pending skipped), `RenderConversions` (Affine2D → Avalonia
    Matrix), `CompositionView` (DrawingContext; clip to canvas; two WriteableBitmaps per layer).
    `PreviewView` hosts it (real canvas proportions, no fixed 960 × 540). Visually checked offscreen
    (scratch harness, not in the repo): transforms, opacity, crop, text, placeholder, clipping,
    vertical canvas.
  - 7d: Inspector Transform (Position X/Y, Scale %, Rotation, Opacity %) and Crop (L/T/R/B %, not for
    text), per-field edits with merge, sync guard, rejection.
  - UI compatibility path removed (view-model `CurrentFrame` / `PictureKind` / `PlaceholderText` /
    `IsPictureCurrent`, the view's bitmap copy); `PlaybackFrame.Picture` stays in Core (test oracle).
  - Tests: UI `PreviewLayersTests` (5), `CompositionDrawPlanTests` (21), `InspectorVisualTests` (7:
    kinds, exact values per field, merged steps + undo/redo refresh without edits, 1/3-precision
    no-echo, crop rejection, locked track, and opacity/scale/rotation edits while playing — same
    VideoPipeline and seek generation, no buffering, lower layer Pending → Frame, reader closed
    again); `PlaybackUiIntegrationTests` / `InspectorAudioTests` moved to the layers.
    Mutations: no polling while a layer is pending → 2 failures (the condition sits in two places;
    removing one alone is not observable — redundant by design); no visual sync guard → 1.
  - Performance (scratch measurement): copy/update 0.11–1.31 ms, offscreen render 0.8–13.9 ms per frame
    for 1–8 layers at 1280 × 720 — no optimization needed.
  - Full suite 925 green (3 consecutive runs); app starts without errors.
  - Open: manual visual check by the product owner. `PlaybackFrame.Picture` stays in Core — product
    owner decision 2026-09-24: deferred cleanup, not part of Phase 7 (≈ 47 assertions of the Phase 5–7
    playback tests use it as their oracle).
- Step 7 manual-check findings (2026-09-24; Step 7 checkpoint: commit `a1cf682`):
  - Numeric fields (all 10 Inspector fields) accepted spaces and foreign characters: NumericUpDown
    parses with `NumberStyles.Any` by default (ru-RU group separator is a space → "9 0" = 90; also
    "(5)", "5-", "1e2", currency). Fixed in one place: `UI/Common/NumericInput.ParsingStyle`
    (leading sign + decimal point only), applied to every NumericUpDown by a style in
    `InspectorView`. Invalid text keeps the last value (model untouched) and the field shows it
    again on blur. Found while checking: an emptied field made the value null, the `decimal`
    binding failed and the Inspector showed an InvalidCastException text (pre-existing since
    Step 4 for Volume). The 10 view-model fields are now `decimal?`: null is never an edit; on blur
    the view calls `InspectorViewModel.ShowModelValues()` (the existing `SyncFromModel`).
    Known, unchanged: fields commit per keystroke (existing behaviour), so a valid prefix typed
    before an invalid character ("9" of "9 0") is applied; the rest is rejected.
  - Clip edge resize not updating the timeline width: **closed as not reproduced** (product owner,
    2026-09-24): observed once during Step 7, not reproduced by the later checks below, no exact
    reproduction steps; no fix. VM geometry is correct
    (drag preview and committed Left/Width, start and end edge, after property edits, undo/redo),
    and in the running app nine scenarios resized correctly (end/start edge, after Inspector spinner
    and typed edits with focus kept, two tracks, zoom Fit, during playback, undo). No timeline code
    changed since Phase 7 Step 1.
  - Tests: UI `NumericInputTests` (25 incl. theory cases: plain numbers ru/en, 16 rejected inputs,
    the old default documented, emptied field → no edit + restore on blur), `TimelineTrimLayoutTests`
    (4). Mutations: `ParsingStyle = Any` → 11 failures; no relayout on TimelineChanged → 4.
  - Full suite 954 green (3 consecutive runs); app started, all 10 fields checked in the running
    app (UI Automation read-back): empty/"abc"/" 50" → model value, "9 0" → 9, "-4 5" → −4,
    "1e2" → 1, "5-" → 5, "1 000" → 1, "-12,5" and "150" accepted.

- Step 8 checkpoint: commit `5d81fe5`.
- Step 8 (D021) — text clips:
  - `ITimelineEditService.AddTextClip(start)`: topmost video track, frame-grid start, 5 s, defaults
    "Text" / Segoe UI / 48 / #FFFFFF / Center, one "Add Text" undo step; rejected without changes on
    overlap, locked track or no video track (no track is created). `EditPlan` passes its description
    to an insert-only command (the add used to be named "Add Clip").
  - "+ Text" in the timeline header (playhead, selects the new clip).
  - Inspector TEXT section: multiline text, font (installed fonts, `IFontCatalog` /
    `AvaloniaFontCatalog`; a missing font is listed first), size (`NumericInput`), `#RRGGBB` color +
    swatch (applied only when complete and valid per D017 — `ClipPropertyValidator.IsHexColor` made
    public), alignment; live, merged per field, sync guard, rejection → status + model value; the
    blur handler now restores any Inspector text field that holds a value it doesn't apply.
  - Timeline label: first line / `(empty text)`, recomputed on every refresh; `Name` is observable.
  - Tests: Timeline `TextClipEditTests` (10: defaults and placement, topmost track, 29.97 grid,
    negative start, one undo step + dirty, overlap/locked/no-track rejections, frame rate not locked,
    trim/move/split of text), UI `TextClipUiTests` (23: "+ Text" selection/Inspector/Preview layer/
    rejection/undo-redo; TEXT fields, per-field edits, merging, no echo edits, color while typing and on
    blur, rejected/empty values, whitespace text draws no layer, font list incl. a missing font, text
    edits while playing keep pipeline and seek generation; label rules and label through edit/undo/redo
    and split). Mutations: no label refresh → 2 failures; no text sync guard → 2; color applied
    unchecked → 1; not the topmost track → 6 (UI 4, Timeline 2).
  - Full suite 987 green (3 consecutive runs). Running app checked: "+ Text" on V2 at the playhead
    with the Inspector TEXT section and the Preview text; multiline text, size, color + swatch,
    alignment, font from the system list (MV Boli rendered); undo back to the added clip and redo
    through all five text edits, with fields, Preview and label following ("Text" ↔ "Hello"); incomplete color + Tab → model color; whitespace text →
    `(empty text)` and no Preview text.
  - Known risk (Phase 8, not solved here): a font missing on the machine silently falls back in the
    Preview, but ffmpeg `drawtext` in the export needs a font file.

- Step 9 checkpoint: commit `48a3f54` (together with the `ExecutableLocator` fix below).
- Step 9 (D022) — speed:
  - Product decisions (2026-09-24): 0.25×–4× in steps of 0.05×, exact fraction; a speed change keeps
    start, SourceIn and SourceOut, the duration is whole frames; one rounding rule for SetSpeed / trim
    / split / regrid / validation / loading; video and audio clips only; v1 files with speed ≠ 1 are
    damaged, saving writes v2; pitch kept via ffmpeg atempo; 1× keeps its exact path; a 1× clip may
    keep a source tail < 1 frame after a speed change back to 1× (owner's choice); no speed label, no
    high-speed decoding optimization.
  - Scratchpad measurement, FFmpeg 9.0.1 (MP4/AAC, 1 kHz bursts, energy centroids): PTS after atempo
    count output samples (source position must come from ashowinfo before atempo); pitch exact at
    0.25/0.5/2/4×; constant latency 449–483 source samples (one atempo), 542 (0.5·0.5 chain), 1573
    (2·2 chain → 4× uses one atempo=4); ≤ 3.4–5.5 ms residual after removing it, no drift; without apad
    the output ends 16–180 ms early, `apad=pad_dur=0.25` delivers the source to the end of the file.
  - Core: `ClipSpeed` (k/20, default = 1×), `SpeedTiming` (SourceLength/FramesFor/Fits); speed-aware
    `SourceFrameSelector.SamplePoint`, `AudioTiming.SourceTimeAt` / `TimelineSampleOfStreamStart`;
    `PictureSpan`/`AudioSpan.Speed`; the "only 1× can be played" status is gone.
  - Timeline: `SetClipSpeed` + `SetClipSpeedCommand` (mergeable); `ClipState.Speed`,
    `ClipState.Normalize`; speed paths in move/trim/split (`PlanTrimAtSpeed`) and `FrameRateRegrid`;
    validator uses the invariant for every speed; the D008 edit ban is lifted. Reader: tempo stream
    placement; `AudioDecodeRequest.Speed`.
  - Video: FFmpeg audio decoder `apad` + atempo chain, latency compensation in FirstSampleIndex
    (480 source samples, 540 below 0.5×).
  - Project: format v2 (`speedRatio`), v1 read with speed exactly 1.
  - UI: Inspector SPEED section (0.25–4.00×, step 0.05).
  - Tests (new): Core `ClipSpeedTests` (23) and `SpeedMappingTests` (6, BigInteger references);
    Timeline `SpeedEditTests` (27: worked examples, boundary speeds 0.25/0.5/0.95/1/1.05/2/4, round trips
    1→1.35→1 and 1→1.35→0.75→1, undo/redo with a non-multiple duration, merging, rejections, trim/move/
    split/regrid at speed, the two real one-tick normalization cases at 29.97 found by a search, 1× with
    a tail, randomized edits with speeds at 29.97/23.976/25) and `SpeedPlaybackTests` (12); Project
    `SpeedPersistenceTests` (22); Video `FfmpegSpeedIntegrationTests` (21, real ffmpeg); UI
    `SpeedInspectorTests` (8). Changed by decision: 4 tests that used speed 2 as "unsupported" (now a
    video clip on audio-only media), the D008 ban test (removed), 3 format-version expectations (v2),
    the v2 "+1 tick at 1×" corruption case (+1 frame now), the recovery "newer version" test's literal.
  - Mutations: speed change rewriting SourceOut → 10 failures; no normalization → 2; audio reader or
    frame selector ignoring the speed → 6 each; no latency compensation → 3 (the slow speeds; the 10 ms
    bound itself guards the fast ones); no apad → 10.
  - Full suite 1105 green. Running app: v1 with speed 2 refused as damaged; valid v1 opened; Speed
    field 2×/0.5×/0.25×/4× — clip lengths 5 / 20 / 40 / 2.48 s and the preview's burned-in source
    time 8.08 / 2.00 / 1.00 / 8.00 s at 4.04 / 4 / 4 / 2 s; playing at 2× ran ffmpeg with
    `…,ashowinfo,apad=pad_dur=0.25,atempo=2`; trim end, split, move of the 2× clip (source 6.000 s at
    5.00 s); undo ×3 / redo ×3; saved as v2 (`speedRatio` 2/1, no `speed`).
  - Found while testing: `ExecutableLocator` passed the first caller's token into the one-time ffmpeg
    probe; if that caller was cancelled (a reader retired right after opening a project) the probe
    failed and "ffmpeg not found" was cached for the whole run (on `5d81fe5` in 4 of 4 open + seek
    runs). Fixed in `48a3f54`: a caller-cancelled probe rethrows without caching; a genuine miss is
    still cached (`Video.Tests/ExecutableLocatorTests`, 5). Final Step 9 app check: 5 open + seek +
    play runs, ffmpeg found every time.

- Step 10 — closeout (2026-09-24, no product code changed):
  - Audit: the Phase 7 scope of `docs/DEVELOPMENT_PLAN.md` (speed, volume, opacity, transform, crop,
    text) and the product decisions above are implemented; D017–D022 match the code; ARCHITECTURE,
    ROADMAP and README brought up to date.

    | Property | Step | Automated tests | Manual check |
    |---|---|---|---|
    | Volume 0–200 %, mute (video + audio clips) | 2–4 | Timeline `ClipPropertyEditTests`, Core `PlaybackSnapshotMixTests`, Timeline `MixUpdatePlaybackTests`, UI `InspectorAudioTests`, Video `MixUpdateIntegrationTests`, Project `ClipPropertyPersistenceTests` | Step 4; Step 10 smoke (values; mute is not audible to automation) |
    | Opacity, transform (position, scale, rotation) | 2, 5–7 | Core `CompositionMathTests`, `CompositionLayersTests`, Timeline `MultiLayerPlaybackTests`, UI `CompositionDrawPlanTests`, `InspectorVisualTests`, `NumericInputTests`, Project `ClipPropertyPersistenceTests` | Step 7 (+ owner findings); Step 10 smoke |
    | Crop | 2, 5–7 | as transform, plus the crop cases of `ClipPropertyPersistenceTests` (load validation) | Step 7; Step 10 smoke |
    | Text clips | 8 | Timeline `TextClipEditTests`, UI `TextClipUiTests`, `CompositionDrawPlanTests` | Step 8 |
    | Speed 0.25–4× | 9 | Core `ClipSpeedTests`, `SpeedMappingTests`, Timeline `SpeedEditTests`, `SpeedPlaybackTests`, Project `SpeedPersistenceTests`, UI `SpeedInspectorTests`, Video `FfmpegSpeedIntegrationTests` | Step 9; Step 10 smoke |
    | Multi-layer playback / Preview | 6–7 | Timeline `MultiLayerPlaybackTests`, UI `PreviewLayersTests`, Video `MultiLayerIntegrationTests` | Steps 6–7 |
    | project.json v2, v1 read | 3, 9 | Project `ClipPropertyPersistenceTests`, `SpeedPersistenceTests`, `ProjectSerializer*Tests`, `RecoverySerializerTests` | Step 9; Step 10 smoke |

  - Coverage gap found by the audit: no automated test put speed ≠ 1 and the other properties on one
    clip (persistence maps them independently, split copies both through one `CloneClip`, speed
    commands touch only `ClipState`). Closed on the product owner's decision by one test:
    `SpeedPersistenceTests.A_clip_at_another_speed_keeps_every_phase7_property_in_v2` (speed 27/20,
    volume 1.5, mute, position, scale, rotation, opacity, crop → v2 with `speedRatio` 27/20 → load →
    every value exact → byte-identical re-serialization).
  - Automated: `dotnet build --no-incremental` 0 errors, 0 warnings; full suite 3 consecutive runs,
    1111 passed each (Core 275, Timeline 257, Project 249, UI 193, Video 137), 0 failed, 0 skipped.
  - Smoke (running app, scratch project, UI Automation): one video clip with speed 2×, volume 150 %,
    muted, position (200, −100), scale 60 %, rotation 15°, opacity 70 %, crop 10/5/10/5 %. Open → frame
    steps + seek → the decoder's source positions advance 0.08 s per 0.04 s timeline frame (2×); Play
    shows the rotated, scaled, offset, cropped, translucent picture; the Inspector shows every value;
    opacity edited to 80 % in the Inspector (title `*`) → Save → `project.json` v2 with `speedRatio`
    2/1, no `speed`, all values incl. the edit; reopen → same values, clean title, 2× mapping and
    playback again. Mute itself is not audible to automation (covered by
    `MixUpdateIntegrationTests`). Closing the app hung in this smoke even without playback — see the
    known issue below (not a Phase 7 regression).
  - Carried forward / deferred (not Phase 7):
    - Phase 8 constraints: the export must reproduce the composition rules of D018 exactly (crop → fit
      → scale → rotation → position → opacity; canvas = project frame size); a font missing on the
      machine silently falls back in the Preview, but ffmpeg `drawtext` needs a font file (D021); the
      speed rules of D022 (exact mapping, atempo with pitch kept) apply to the export as well.
    - `PlaybackFrame.Picture` cleanup (deferred, see Step 7).
    - The known issues below, in particular the close hang.

### Phase 6 — Project persistence (complete)

Branch `feat/phase-6-project-persistence` (from `acc1a49`), Phase 6 commit, merged into `main`.
Decisions: DECISIONS.md D014–D016.

Product decisions (2026-09-23): project = folder with `project.json` + `cache/` (no
`.aveproj`); autosave writes a separate recovery file every 2 min and never overwrites
`project.json`; save point is part of Phase 6; saved ffprobe metadata is reused on Open;
failed Open leaves the current project untouched; atomic save; missing media opens as
offline. Out of scope: relink, recent projects, copying media into the project.

- Step 1 done — format/serializer: `Project/Persistence/ProjectFileDto.cs` (format v1, DTOs
  separate from entities, `MediaTime` as long ticks, `FrameRate` as {num, den}, no runtime
  state), `ProjectSerializer` (validating load: ids, references, clip kind/track, frame grid,
  overlaps; absolute + relative media path), `ProjectFileStore` (atomic temp + `File.Replace`),
  `Core/Interfaces/ProjectFileException`. Tests: `tests/Project.Tests` (new project).
- Step 2 done — `ProjectService` Open/Save/SaveAs; save point in `UndoRedoService`
  (`CurrentPosition`, `MarkSavePoint`, `IsAtSavePoint`); dirty = history not at save point or
  a media import since the save; `SaveStateChanged` event. Open replaces the project only after
  full load + validation.
- Step 3 done — missing media: marked on the loaded project before it replaces the current one
  (first playback snapshot already Offline); not dirty, not saved. `ProjectFileWorkflow` (UI):
  open → analyse only present media without saved metadata → status message with the missing
  count. `MediaAnalysisCoordinator` never probes missing files. Media Browser row shows
  "Media offline".
- Step 4 done — autosave/recovery: `AutosaveService` (Project, implements the Core
  `IAutosaveService`, every 2 min, snapshot on the UI thread, atomic write) into
  `%LOCALAPPDATA%\AiVideoEditor\recovery\<projectId>.json` via `RecoveryStore` (one app-wide
  folder so startup can find recovery without recent projects; never `project.json`). Recovery
  file = project DTO + original folder, time, writer process. Obsolete (deleted) after a
  successful Save (`IProjectService.ProjectSaved`), when this session finds the project clean,
  on Discard, and at a clean shutdown; a per-project generation stops an autosave snapshotted
  before a Save from rewriting it. Startup (`ProjectFileWorkflow.StartSessionAsync`, on window
  Opened): scan → damaged files renamed `*.damaged`, files older than project.json removed,
  files of a running instance ignored → Recover / Discard / Not now dialog (`IDialogService`,
  `AvaloniaDialogService`) → `IProjectService.RestoreRecoveryAsync` (same validation as Open;
  project dirty, original folder) → autosave starts. Closing (`MainWindow.OnClosing` →
  `PrepareToCloseAsync`): a dirty project keeps a final recovery file, a clean one leaves none.
  (Step 5 then put the unsaved-changes prompt in front of this.) `AppPaths.AutosaveFile`
  (project cache) replaced by `AppPaths.RecoveryFolder`.
- Step 5 done — UI workflow: `ProjectFileWorkflow` New / Open (folder picker) / Save (Save As
  if never saved) / Save As (folder picker; confirms replacing another project's
  project.json) / Close, all behind one "save changes?" prompt (Save / Don't Save / Cancel via
  `IDialogService`; a Save that doesn't happen counts as Cancel; edits made during that save →
  asked again). "Don't Save" removes the discarded project's recovery file only after New/Open
  succeeded (a failed Open keeps project, changes and recovery); on Close it is
  `IAutosaveService.ShutdownAsync(keepUnsavedChanges: false)`. `IAutosaveService`:
  `DiscardCurrentRecoveryAsync()` → `DiscardRecoveryAsync(Guid)`. Toolbar commands are async
  (no re-entry), Save As button added; window title "Name[*] — AI Video Editor" follows
  `SaveStateChanged`; shortcuts Ctrl+N / Ctrl+O / Ctrl+S / Ctrl+Shift+S (like the other
  shortcuts, not while a text box has focus). `IFilePickerService.PickFolderAsync` added.
- Step 6 done — docs: DECISIONS D014 (format, Open/Save, missing media), D015 (save point,
  open question below), D016 (autosave, recovery, unsaved changes); ARCHITECTURE (projects
  table, "Project persistence" section, stale Phase 3–5 statements), ROADMAP,
  docs/DEVELOPMENT_PLAN.md (Phases 4–6 checked), README status.

Verification (closeout):
- Automated: 618 tests passed (Project 168, Core 162, Timeline 132, Video 76, UI 80);
  `dotnet build` 0 errors, 0 warnings.
- UI smoke test (UI Automation script, not in the repo): leftover recovery → dialog → Recover
  → title "Smoke Film* — …" → close → prompt → Cancel keeps the window → close → Don't Save →
  exit 0, recovery file removed.
- Manual UI check by the product owner (Step 5): passed — incl. the native folder pickers and
  the keyboard shortcuts.

Deferred / out of scope: relink of missing media, recent projects, copying media into the
project, re-checking missing media while the project is open, offering more than one
recovery file per start (older ones are offered at later starts).

Open questions carried forward from Phase 6:
- ~~Playhead position, zoom and snapping: project or session state?~~ Decided in Phase 7
  Step 1: session state (D015).
- Missing state is detected once on Open; a file that reappears later stays offline until the
  project is reopened (no relink in Phase 6).

### Phase 5 — Preview/playback (complete)

Branch `feat/phase-5-playback`. Decisions: DECISIONS.md D009–D013. Video playback (checkpoint
`85ca216`) and audio playback (Phase 5 closeout commit) are implemented, covered by automated
tests and manually validated by the product owner.

### Phase 5 checkpoint summary
- Implemented: exact source-frame selection (D009); ffmpeg discovery; ffmpeg CLI video
  decoder with PTS from `showinfo`, bounded time-based preroll and hardware → software
  fallback; anchor-based playback clock (Stopwatch now, audio-ready reference); immutable
  playback snapshot + builder; `PlaybackService` / `VideoPipeline` (seek generation and
  snapshot version guards, prefetch of the next clip, still images as one cached frame,
  Offline / Unsupported / DecodeError kept distinct, underrun rule D012); Preview UI
  (DispatcherTimer tick → `Update()`, WriteableBitmap), Play/Pause/Stop/Space, playhead ↔
  seek wiring without feedback, snapshot rebuild on timeline/media/project changes (skipped
  for media changes that don't affect the timeline).
- Tests: 335 passed (Core 130, Timeline 112, UI 30, Video 63 — the Video tests run real
  ffmpeg on generated media); build 0 errors, 0 warnings.
- Deferred (explicitly): volume/mute UI; audio device unplug / default-device change handling;
  limiter, crossfade/declick; J/K/L, loop, timeline autoscroll, scrub
  cache; compositing (opacity/transform/crop), text rendering, speed ≠ 1, transitions;
  HDR/10-bit tone mapping and color management; RequestAnimationFrame-synced ticking;
  dropped/late frame counter; export (Phase 8).

Step 1 (accepted) — source-frame selection foundation:
- `Core/Common/SourceTimestamp.cs` (`TimeBase`, `SourceTimestamp`),
  `Core/Playback/SourceFrameSelector.cs` (D009), `MediaMetadata.StartTime` /
  `AvgFrameRate`; unit + property tests.

Step 2 (accepted, closed) — FFmpeg video decoder:
- `Core/Playback/DecodedFrame.cs` (BGRA + `SourceTimestamp`), `Core/Playback/IVideoDecoder.cs`
  (`IVideoDecoder`, `VideoDecodeRequest`, `IVideoFrameStream`, `VideoDecodeException`).
- `IFfmpegLocator` (Core) / `FfmpegLocator` (Infrastructure): `Ffmpeg:FfmpegPath`, then PATH.
  Shared lookup logic in `Infrastructure/ExecutableLocator.cs` (FfprobeLocator uses it too).
- `Video/FfmpegVideoDecoder.cs`, `FfmpegVideoFrameStream.cs`, `ShowInfoParser.cs`:
  `-copyts`, bounded time-based preroll with validation/retry, `-fps_mode passthrough`,
  PTS from `showinfo` (internal), `-hwaccel auto` with software retry.
- DI: `IFfmpegLocator`, `IVideoDecoder` registered (not used by UI yet).
- `tests/Video.Tests` (new project): integration tests on generated media.
- Hardware fallback: an attempt with `-hwaccel` that fails, times out or yields no frames
  is relaunched without `-hwaccel` (tested with a rejected accelerator).

Step 3 (accepted) — playback core (D011, D012), no UI / NAudio:
- Mid-stream HW → software fallback keeps already decoded frames, drops duplicates by PTS,
  keeps the seek generation (fixed after review; deterministic test with a gated reopen).
- Review of PlaybackService/VideoPipeline (10 points): one defect fixed — `UpdateSnapshot`
  re-anchored the clock on every resync, dropping the real time between reading the position
  and the new anchor; it now re-anchors only when the position must be clamped.
  `PlaybackSnapshot.Version` renamed to `SnapshotVersion`. Regression tests added: resync
  keeps clock time, lower clip resumes after an overlapping top clip, superseded pipelines
  release decoder streams, no ffmpeg process left after seeks + dispose
  (`FfmpegVideoFrameStream.LiveProcesses`, internal diagnostic counter).

Step 4 (accepted, manually validated) — UI integration on the Stopwatch clock (no NAudio):
- `PreviewView` code-behind: a `DispatcherTimer` (10 ms, UI thread, only while attached) calls
  `PreviewViewModel.Tick()`; frames are copied into two alternating `WriteableBitmap`s.
- `PreviewViewModel`: Play/Pause/Stop over `IPlaybackService`; `Tick()` polls `Update()` only
  while playing, buffering, late, or right after a transport/seek/snapshot change; exposes
  `CurrentFrame`, `PictureKind`, `PlaceholderText`, `IsPlaying`, `IsBuffering`,
  `IsPictureCurrent`; rebuilds the `PlaybackSnapshot` on `TimelineChanged`,
  `MediaAssetsChanged` and `ProjectChanged` (the latter also pauses and seeks to the new
  project's playhead).
- `TimelineViewModel`: user playhead moves (`SetPlayhead` and everything built on it) raise
  `SeekRequested`; playback positions arrive through `ShowPlaybackPosition`, which never does —
  the only feedback guard. `MainWindowViewModel` wires SeekRequested → `Preview.Seek` and
  `PlaybackPositionChanged` → `ShowPlaybackPosition`. The Stop button now calls
  `IPlaybackService.Stop()` (the old `GoToStartRequested` event is gone). Space → Play/Pause.
- `MediaAssetsChanged` rebuilds the snapshot only if an asset referenced by a timeline clip
  (any track) changed in a playback-relevant way (`PlaybackSnapshotBuilder.CaptureAssetStates`);
  importing/analysing unrelated media no longer resyncs playback or reopens decoders.
- Tests: `UI.Tests/PlaybackUiIntegrationTests.cs` (12) — real shell wiring, project/timeline
  services and PlaybackService with the fake decoder/clock (shared from Timeline.Tests).
- Manual check in the running app done by the product owner: Play/Pause, Space, Stop,
  playhead click and drag while playing, consecutive clips, gap, upper/lower video tracks,
  image clip, missing file, timeline edits while playing and paused, long playback without
  drift.
- Core/Playback: `PlaybackClock` + `IReferenceClock`, `PlaybackSnapshot` (+ `PictureSpan`,
  `AudioSpan`, `PlaybackAsset`, `SpanStatus`), `PlaybackSnapshotBuilder`, new
  `IPlaybackService` (`PlaybackFrame`, `PreviewPicture`, `PictureKind`). The old unused
  `IPlaybackService` in `IProjectService.cs` was removed.
- Timeline/Playback: `PlaybackService`, `VideoPipeline`, `SpanReader`,
  `StopwatchReferenceClock`, `PlaybackSettings`. Registered in DI.
- Tests: `Core.Tests/PlaybackClockTests.cs`, `PlaybackSnapshotBuilderTests.cs`;
  `Timeline.Tests/Playback/*` (fake decoder + fake clock); `Video.Tests/PlaybackServiceIntegrationTests.cs`
  (real ffmpeg, two clips + gap, software and -hwaccel auto).

Audio (implemented, manually verified by listening, accepted) — architecture approved (audio device = master clock,
48 kHz stereo float32, AudioPipeline/AudioSpanReader/AudioMixer, silence on underrun and
errors, reader reuse across snapshot updates). D013 to be written once implementation/tests
confirm it.
- Step A1 done: WASAPI clock spike (scratch console app, not in the repo; NAudio.Wasapi 2.2.1 —
  3.x requires net9.0) on a Sound BlasterX G6, shared mode, mix format 48 kHz / 8 ch float:
  - `WasapiOut.GetPosition()` = bytes of `OutputWaveFormat` actually played (IAudioClock),
    not queued: written − position ≈ 130–140 ms with latency 100; rate ≈ 48 000 frames/s.
  - `OutputWaveFormat` equals the requested format (48k/2ch float; also 44.1k): WASAPI
    converts to the 8-ch mix itself; position units follow the requested format.
  - Monotonic while playing; **0 after Stop and restarts from 0** → the output must keep a
    cumulative base (read the position right before Stop). First non-zero position 30–65 ms
    after Play (start-up latency).
  - **`Pause()` only stops feeding**: the position keeps rising until the queued audio has
    played → never use Pause; pause = Stop (flush).
  - COM objects are apartment-bound: an `MMDevice` created on an MTA thread fails on an STA
    thread (E_NOINTERFACE). Creating device + WasapiOut on the STA (UI-like) thread works;
    `GetPosition()` then also works from MTA and thread-pool threads.
- Step A2 done: Core contracts `AudioFormat`, `IAudioSampleSource`, `IAudioOutput`,
  `IAudioDecoder`, `AudioDecodeRequest`, `IAudioSampleStream`, `AudioDecodeException`
  (Core/Playback/AudioContracts.cs) and exact sample math `AudioTiming`
  (clip samples `[ceil(S·fs), ceil(E·fs))`, per-clip source offset rounded once);
  `Core.Tests/AudioTimingTests.cs`.
- Step A3 done (awaiting review; D013): `AudioSpanReader`, `AudioMixer`, `AudioPipeline`
  (Timeline/Playback); `PlaybackService` drives the device (Play/Pause/Seek/UpdateSnapshot,
  device failure → Stopwatch, `IsAudioAvailable`); `FfmpegAudioDecoder` + shared
  `FfmpegProcess` (Video; the video stream now uses `FfmpegProcess` too, behaviour unchanged);
  `WasapiAudioOutput` (Audio, NAudio.Wasapi 2.2.1); DI registrations; one UI status message
  when playing without sound. Public `SetMasterClock` removed (the service owns the master).
- Findings: remuxing AAC from MP4 to MPEG-TS keeps the 1024-sample encoder priming at the
  container start (the MP4 edit list skipped it), so such a TS really plays 1024 samples later —
  our decoder matches ffmpeg's own plain decode there. A 1 ms preroll on AAC/MP4 needed 3 seek
  attempts; the default 200 ms needed 1.
- Tests: `Timeline.Tests/Playback/AudioPipelineTests.cs` (7), `AudioPlaybackServiceTests.cs` (8),
  fakes in `AudioFakes.cs`; `Video.Tests/FfmpegAudioDecoderIntegrationTests.cs` (10, incl. an
  A/V sync test: click at 2.02 s heard while video frame 50 is shown) and
  `WasapiAudioOutputDeviceTests.cs` (real device, STA thread; passes with a note if no device).
- Manual listening test in the real app by the product owner (2026-09-23): audio plays
  correctly. Not verified yet: device unplug during playback, default-device change while
  running (not handled: the output stays on the device it opened).
- Step A4 done (lifecycle hardening): superseded pipelines and readers retired by
  `VideoPipeline.Retain` / `AudioPipeline.Maintain` were disposed fire-and-forget; they are now
  tracked, and `PlaybackService.DisposeAsync` awaits all of them — after Dispose no reader,
  stream, background task or ffmpeg process is left (deterministic, not just eventual).
  New tests: Play→Pause→Play cycles (continuous audio, monotonic position, no reader pile-up),
  seeks backward/forward while playing, 20 snapshot updates while playing (continuity, reuse,
  no leaks), end of timeline releases readers, Dispose with pending opens and slow-closing
  streams; real device: clock monotonic over 6 Start/Stop sessions; real ffmpeg: full playback
  lifecycle leaves no ffmpeg process after Dispose.

Phase 4 — Timeline: implemented, accepted and merged into `main`.

## Last known state

2026-10-05: Phases 0–11 are complete and merged into `main` (last merge `47ed2fa`, PR #11, CI green). Phase 12 (editing
essentials, D027) is complete locally on `feat/phase-12-editing-essentials` (Step 12.9: 2324 passed, 2 skipped; the 4K
scenes 90 / 90); the branch is not published, so CI has not run. Open items carried forward: see "Known issues" (L1-c,
the audio status message after a device returns, the watched `Project.Tests` hang / failure, no timeline
virtualization, import not undoable, the 5 s PATH probe of the locators, the `F(end − start)` test helpers).

### Phase 4 — Timeline (historical notes)

Phase 4 implemented (decisions: DECISIONS.md D006–D008):
- Exact time model: rational `FrameRate`, integer frame grid in `MediaTime`
  (double-based frame conversions removed; D001 unchanged).
- Project frame rate: provisional 30 FPS, fixed by the first video (from
  `r_frame_rate`, fallback 30 FPS stated explicitly), existing clips re-gridded in the
  same undo step, atomic rejection if impossible.
- `ITimelineEditService` / `TimelineEditService`: add (button → end of V1/A1,
  drag-and-drop → track + position), move (incl. between tracks), trim (clamped),
  split (selection or everything under the playhead), delete, add track, snapping.
  All undoable; validation before execution.
- Default tracks V1/A1; `TimelineChanged` + IsDirty on any timeline change.
- Timeline panel: real tracks/clips, ruler (visible range only), playhead
  (ruler click/drag, frame steps), zoom (Ctrl+wheel at pointer, buttons/hotkeys at
  playhead, fit), selection (click, Ctrl+click), drag move with red invalid preview,
  trim handles, snap indicator.
- Inspector shows the selected clip (non-drop-frame timecode); Transform hidden until Phase 7.
- Preview shows the real playhead and sequence duration; Play reports "Phase 5".
- Hotkeys: Ctrl+Z, Ctrl+Y / Ctrl+Shift+Z, Delete/Backspace, S, N, ←/→,
  Shift+←/→, Home/End, Ctrl+= / Ctrl+−; ignored while a TextBox has focus.

## Completed

- Phase 0
- Phase 1
- Phase 2
- Phase 3
- Phase 4
- Phase 5 (video checkpoint `85ca216`, audio in the closeout commit)
- Phase 6
- Phase 7 (accepted 2026-09-24)
- Phase 8 (accepted 2026-09-25)
- Phase 9 (accepted 2026-09-29)
- Phase 10 (accepted 2026-10-01; PR #10 merged as `2e758f1`)
- Phase 11 (accepted 2026-10-05; PR #11 merged as `47ed2fa`, CI green)
- Phase 12 (complete locally 2026-10-05, Step 12.9; CI pending — not published)

## Known issues

- Export codec leg (D023 Step 8, decision L1-c): MP4 → export canvas has no numeric tolerance; the Step 8.6
  measurement is data for a future product decision, not a criterion. Open.
- `Project.Tests` hang seen once in Step 8.4 (1 of 23 runs, test not identified): not reproduced — the 8.4/8.5 runs
  and the three final `--blame-hang` runs of the closeout were clean. Watch for it; no fix. Phase 9 Step 9.3d: one
  unidentified `Project.Tests` failure (not a hang) in one full parallel run; not reproduced in 40 isolated and 8 full
  runs (with TRX results, so a recurrence names the test).

- Speed (D022): the atempo latency compensation is measured for FFmpeg 9.0.1; another ffmpeg version
  may shift it — `FfmpegSpeedIntegrationTests` (10 ms bound) catches that.
- New Project while `ImportManyAsync` is still checking the picked files adds them to the new project (the import
  adds to whatever project is current when it finishes). Out of scope of Step 9.3 (product owner, 2026-09-25).
  Fixed in Phase 12 Step 12.8 (D027 §7): `MediaImportWorkflow` keeps the import's project and adds nothing to another
  one (automated tests; the one-frame race not reproducible by hand).
- Tests: some Phase 12 test helpers (`TrackEditTests`, `RippleEditTests`, `TimelineRippleUiTests`) give text clips the
  length `F(end − start)` instead of `F(end) − F(start)` — a tick off the frame grid at 30 fps for some values; they
  pass because their clips happen to land on the grid. A separate cleanup (product owner, Step 12.6).

- Text clips (D021): the Preview (Avalonia) silently substitutes a font that isn't installed. Phase 8
  (D023) renders text like the Preview (no `drawtext`), so the export falls back the same way; the
  preflight warns about it. Embedding fonts in a project is out of scope.

- Preview color: footage from the Vivo X300 Pro (HDR / 10-bit) may look overexposed /
  washed out in the Preview. This is not a Phase 5 playback-correctness issue: the preview
  pipeline does not yet have the final HDR/10-bit → SDR color / tone-mapping pipeline
  (ffmpeg converts straight to 8-bit BGRA). Proper HDR/10-bit/color-management handling is
  deferred to the later color/export pipeline phase. Do not add ad-hoc ffmpeg color filters
  or tone-mapping hacks to the preview decoder to compensate in the meantime.

- MPEG-TS: ffmpeg `-ss` lands on the keyframe *after* the target, so TS seeks need
  preroll retries (3–4 decoder launches observed); correct but slower to open.
- `Video.Tests` needs ffmpeg/ffprobe on PATH; its tests are skipped otherwise.
- Playback underrun (D012): while decoding is slower than real time, a layer keeps its previous
  frame flagged late (`LayerPicture.IsCurrent = false`); late frames are flagged but not counted
  (no dropped-frame counter).
- Video decoder limitations (deliberately out of scope for now): HDR / 10-bit (no tone
  mapping), interlaced (no deinterlacing), SAR (non-square pixels ignored), rotation
  metadata (ffmpeg autorotate applies, not handled explicitly), resolution changes
  mid-stream (untested), phone-specific VFR quirks beyond the tested cases. A hardware
  failure after the first frame is not retried by the decoder (the caller must reopen).

- ffmpeg / ffprobe locators: a PATH probe slower than 5 s counts as "not found" for the app run (a heavily loaded machine
  could hit it; seen once on CI in a test). Known risk, unchanged (D024, Step 9.10).
- Audio device: after a device was lost during playback and the sound came back at the next Play, the status bar still says
  "Playing without sound…" (the status shows the last message until another one; D024 "Left as they are", Step 9.10). A real
  default-device change and a real removal were checked on hardware in Step 9.10 (scenarios 14–17).
- `MediaAnalysisCoordinator` relies on the captured UI SynchronizationContext.
- Timecode is non-drop-frame only (29.97 timecode drifts from wall clock by design).
- Timeline canvas is a plain ItemsControl/Canvas; very long timelines at maximum
  zoom are not virtualized (ruler is).
- Media import is not undoable (unchanged from Phase 2).

## Verification

2026-09-25 (Phase 8 closeout, Step 8.7; docs only, no code change):
- Working tree before the closeout identical to the end of Step 8.5 (hashes of every changed file); leftover MSBuild
  nodes from the 8.5 mutation runs stopped with `dotnet build-server shutdown`.
- `dotnet build AiVideoEditor.sln --no-incremental`: 0 errors, 0 warnings.
- `dotnet test`: 1 plain run + 3 consecutive runs with `--blame-hang --blame-hang-timeout 5m`, each 1494 passed,
  2 skipped (the 4K scenes), 0 failed — Core 380, Timeline 257, Project 259, UI 216, Export 78, Rendering 52,
  Video 185, ExportEndToEnd 67 (+2 skipped); no hang, no dump. Heavy 4K scenes once with `AIVE_HEAVY_TESTS=1`: 2/2.

2026-09-24 (Phase 7 closeout, Step 10):
- `dotnet build --no-incremental`: 0 errors, 0 warnings. `dotnet test`: 3 consecutive runs, 1111
  passed each (Core 275, Timeline 257, Project 249, UI 193, Video 137), 0 skipped.
- Running app: Phase 7 integration smoke passed (details in Step 10 above); close hang observed
  (known issue, pre-Phase 7).

2026-09-23 (Phase 5 audio, lifecycle):
- `dotnet build`: 0 errors, 0 warnings. `dotnet test`: 391 passed (Core 153, Timeline 132,
  UI 30, Video 76); audio tests repeated (Timeline 3×, Video 2×) without failures.
- Real device: 6 Start/Stop sessions sampled every ~2 ms — never backwards, frozen after Stop.
  Real ffmpeg lifecycle: 2 live processes while playing, 0 right after Dispose.
- Mutation: Dispose not awaiting retiring pipelines → the Dispose test fails (1 stream left).

2026-09-23 (Phase 5 audio):
- `dotnet build`: 0 errors, 0 warnings. `dotnet test`: 384 passed (Core 153, Timeline 127,
  UI 30, Video 74); audio tests repeated (Timeline 4×, Video 2×) without failures.
- Mutations: reader trusting the request instead of the real first sample → 5 failures;
  device clock not cumulative → device test fails; no reader reuse → 2 failures.
- App started in Development (DI incl. audio validated). No listening test by automation.

2026-09-23 (Phase 5 checkpoint):
- `dotnet build`: 0 errors, 0 warnings. `dotnet test`: 335 passed (Core 130, Timeline 112,
  UI 30, Video 63).
- Manual validation of video playback in the running app by the product owner: no
  functional complaints or blocking issues.

2026-09-23 (Phase 5 UI integration):
- `dotnet build`: 0 errors, 0 warnings. `dotnet test`: 335 passed (Core 130, Timeline 112,
  UI 30, Video 63); UI tests repeated 3× without failures.
- Mutations: playback position through the user path (seek feedback) → 1 failure; playhead
  moves not wired to seek → 4 failures; no snapshot rebuild on TimelineChanged → 11 failures;
  unconditional rebuild on MediaAssetsChanged → the unused-asset test fails.
- App started in Development (DI validated): window responsive, low idle CPU with the tick
  timer. Interactive playback in the running app was not exercised by automation.

2026-09-23 (Phase 5 playback core):
- `dotnet build`: 0 errors, 0 warnings. `dotnet test`: 323 passed (Core 130, Timeline 112,
  UI 18, Video 63). Playback service tests repeated 6× without failures.
- Mutations: holding the clock while buffering → 2 failures; accepting an unconfirmed
  frame → 4 failures; re-anchoring on every resync → 1 failure; not disposing superseded
  pipelines → 1 failure; clearing the buffer on HW fallback → 1 failure. Removing the explicit (version, generation) check → no failure: the
  service only reads the current pipeline and superseded readers stop publishing under a
  lock, so the check is a second line of defence (its only effective window — a pipeline
  becoming ready just before being superseded — is not reproducible deterministically).
- App started in Development (DI validated on build): shell initialized, no errors.

2026-09-23 (Phase 5 decoder):
- `dotnet build`: 0 errors, 0 warnings.
- `dotnet test`: 286 passed (Core 116, Timeline 92, UI 18, Video 60) with ffmpeg 9.0.1.
- `-hwaccel auto` verified to use DXVA2 (RTX 4070 SUPER) with the decoder's exact
  arguments for H.264, H.265 and TS; software and hardware select identical frames/PTS.
- Mutations: accepting the first frame without preroll validation → 6 failures;
  pairing PTS with the next frame → 52 failures; disabling the software relaunch →
  the fallback test fails (ffmpeg exit −22).

2026-09-23 (Phase 5 foundation):
- `dotnet build`: 0 errors, 0 warnings.
- `dotnet test`: 226 passed (Core.Tests 116, Timeline.Tests 92, UI.Tests 18).
- Mutation check: replacing δ by "frame start" or "frame midpoint only" makes the new
  tests fail (7 and 20+ Core failures; 10/12 property tests).
- ffprobe JSON checked on generated .ts/.mkv/.mp4: `start_time` "1.400000" /
  "0.000000" and `avg_frame_rate` present. Parsing itself has no unit test (no Video
  test project).

2026-09-23 (Phase 4):
- `dotnet build`: 0 errors, 0 warnings.
- `dotnet test`: 160 passed (Core.Tests 62, Timeline.Tests 80, UI.Tests 18).
- Manual run (Windows, generated test media incl. 29.97 FPS video): import, add image/
  audio/video, FPS lock to 29.97 with status message and re-grid, Inspector timecode,
  drag move, trim, ruler playhead, split via S, undo ×3, drag-and-drop from the Media
  Browser, add track + Ctrl+Z, S/N hotkeys.

## Instructions for Claude

Update this file after substantial milestones.

Do not fabricate progress.

When uncertain, inspect the repository and Git history first.
