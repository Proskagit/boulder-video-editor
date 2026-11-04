# Phase 31 — the remaining manual checks (mini-instruction)

Everything else in A / B / E passed in automated tests (`docs/PHASE31_MANUAL_TEST_PLAN.md`, «Automated acceptance of
A, B and E»). Left for the product owner: **A2 (look)**, **C4**, **D1a**, **D3**, **D4**, **D5**. About 15 minutes.

**Run by the product owner on 2026-11-04:** A2, C4, D1a, D4, D5 — PASS; D3 — DEFERRED by the owner's decision (see its note); C5 (not in this instruction) — DEFERRED.
Phase 31 accepted by the product owner on 2026-11-04 with these two deferred checks. The record: `docs/PHASE31_MANUAL_TEST_PLAN.md`, «Manual acceptance by the product owner».

Both scripts are in `tools/manual/`; run the commands below from the repository root, on `main` with PR #38
(`4717fca`) or later — that build has the B1–B3 fixes.

## Starting

```bash
pwsh -File tools/manual/Start-Phase31Check.ps1
```

What it does:
- builds the Debug app;
- copies the E1 clip `1014(6).mp4` into `%TEMP%\aive-phase31-check` (the original is not touched);
- opens a project there with this clip on the timeline (10 s).

A different video: `-Clip "<path>"`. Every run recreates the project from scratch.

Before each check:
1. Click the clip on the timeline.
2. In the Inspector, «Предлагать зацепки автоматически» is on.
3. Click «Разметить зацепки» and wait for the proposals (up to ~8 s on 4K).

## Checks

| # | Steps | Expected | Result |
|---|---|---|---|
| **A2** | Wait for the proposals; Tab twice; Enter on the selected one | Proposals: thin grey dashed contours. The selected one: yellow dashed. The accepted hold: a solid outline, clearly different from both | PASS |
| **C4** | Accept 3–4 proposals on clearly visible holds (Enter); zoom the Preview; compare with a hold you add by hand (Shift+click → Enter) | The accepted contour follows the hold's edge no worse than the hand-made one | PASS |
| **D1a** | Fresh project (rerun the script). Accept three proposals one by one with Enter; then Ctrl+Z three times; then Ctrl+Y three times | Each Ctrl+Z removes one hold, latest first; after three, none left. Ctrl+Y brings them back one by one | PASS |
| **D3** | See the note below | Accepting stops at 500 holds with «На клипе может быть не больше 500 зацепок.» | DEFERRED |
| **D4** | Close the app. `Set-HoldModelHidden.ps1 -Hide`, then `Start-Phase31Check.ps1 -NoBuild`; select the clip, try «Разметить зацепки». Close the app, **`Set-HoldModelHidden.ps1 -Restore`** | No proposals start, no crash. The same model message as in ordinary manual markup (D034); the mode doesn't open. After `-Restore` everything works again | PASS |
| **D5** | `Start-Phase31Check.ps1 -NoBuild -Offline` (the project's copy of the clip is renamed, so the media is offline); select the clip | «Разметить зацепки» and the proposals are unavailable, exactly as for manual markup on offline media | PASS |

**D3 note:** you can't reach 500 holds by hand, because proposals appear only on a clip with no holds, at most 30 per pass.
D3 is DEFERRED by the product owner's decision of 2026-11-04 — not a PASS, not a blocker; an automated or other
technical check may be considered later.

Commands for D4:

```bash
pwsh -File tools/manual/Set-HoldModelHidden.ps1 -Hide
```

```bash
pwsh -File tools/manual/Start-Phase31Check.ps1 -NoBuild
```

```bash
pwsh -File tools/manual/Set-HoldModelHidden.ps1 -Restore
```

`-Hide` only renames the model folder (`sam2.1-large` → `sam2.1-large.hidden-for-D4`). Nothing is deleted. To check
where the model is: `-Status`.

## What to send back

- A line per check: `A2 PASS` / `C4 FAIL: …`.
- On a FAIL:
  - a screenshot of the frame (for A2 and C4, with the proposals or the contours visible);
  - what you pressed;
  - the log `%LOCALAPPDATA%\AiVideoEditor\logs\` for the time of the check.
- After D4, the output of `Set-HoldModelHidden.ps1 -Status` (it must say «Model in place»).

Phase 37 has no manual remainder. A1–A15 are covered by tests, A10 is measured. Only your acceptance decision is left.
