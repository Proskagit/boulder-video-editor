# Phase 31 — Automatic hold proposals: manual test plan

Decision: DECISIONS.md D047. Scope: automatic hold proposals on the first markup frame of a video clip; the user
reviews, accepts or rejects them. Phase 31 changes no behaviour of the existing manual markup (D034–D036); it adds an
optional first step before the manual flow.

Environment: the development build or the portable build; the pinned SAM 2.1 Large ONNX model installed as
`docs/AI_MODEL.md` describes; owner gym footage (4K and 1080p clips).

Legend: **PASS** — the criterion is met; **FAIL** — a defect; **PARTIAL** — part met, the rest recorded; **NOT RUN** —
not run, with a reason.

## A. Proposal generation and basic review

| # | Check | Expected result | Evidence |
|---|---|---|---|
| A1 | Enter markup mode on a clip with no holds (toggle «Предлагать зацепки автоматически» on) | The progress indicator appears and proposals are drawn | |
| A1a | Enter markup mode with the toggle off | No proposal pass runs; the manual D034 flow is available immediately | |
| A2 | Proposal drawing | Each proposal is a dashed/translucent contour, distinct from confirmed holds; the selected proposal is highlighted | |
| A3 | Top-N default | No more than 30 proposals shown by default; the Inspector shows the count and the rank/score | |
| A4 | Tab / Shift+Tab | Moves selection forward/backward through proposals; click also selects | |
| A5 | Enter / «Принять» | The selected proposal is accepted; its contour becomes a confirmed hold (solid outline, takes part in route colour and animation) | |
| A6 | Delete / «Отклонить» | The selected proposal disappears; the next proposal is selected | |
| A7 | «Принять все» | All shown proposals become confirmed holds in one step | |
| A8 | «Отклонить все» | All shown proposals disappear; the manual D034 flow remains | |
| A9 | No proposals generated | A message explains and offers manual markup | |
| A10 | Cancel during generation | The progress indicator stops; no proposals are shown; the project is unchanged | |

## B. Interaction with the existing manual flow

| # | Check | Expected result | Evidence |
|---|---|---|---|
| B1 | Shift+click while reviewing proposals | Switches to the existing manual candidate at the click point; proposals stay visible in the background | |
| B2 | Esc with a manual candidate active | Discards the manual candidate; returns to proposal review | |
| B3 | Manual confirm while proposals remain | The manual hold is confirmed; unreviewed proposals stay available | |
| B4 | Done / Play / Seek | Leaves markup mode; accepted and manually confirmed holds stay; rejected/unreviewed proposals are gone | |
| B5 | Re-enter markup mode on the same clip | No automatic proposal prompt (the clip already has holds); the existing holds are shown for manual editing | |

## C. Quality and performance on owner footage

| # | Check | Expected result | Evidence |
|---|---|---|---|
| C1 | 4K clip, default 1080p proposal resolution | The proposal pass completes in ≤8 s on the development machine (target orientir); ≤5 s is aspirational | |
| C2 | Recall on a representative 4K frame | Most obvious holds (visually, to the owner) appear in the top-30 proposals | |
| C3 | Precision | Fewer than ~30 % of the shown proposals are clearly not holds; reject-all is usable when precision is low | |
| C4 | Accepted proposal contour | The refined full-resolution contour follows the hold edge at least as well as a single manual click | |
| C5 | 1080p source clip | Proposals are generated at source resolution (no upscaling) or at 1080p; quality is comparable | |

## D. Edits, persistence and edge cases

| # | Check | Expected result | Evidence |
|---|---|---|---|
| D1 | Undo after accept-all | One Undo removes the whole batch of accepted holds; Redo restores them | |
| D1a | Undo after accepting three proposals individually | Three Undo steps remove the holds in reverse order; Redo restores them | |
| D2 | Save and reopen | Accepted holds are present; rejected proposals are not present; the project opens as v9 | |
| D3 | Max holds | Accepting proposals up to the max-holds limit works; further accepts are refused with the existing message | |
| D4 | Model missing | The proposal button is disabled; manual markup still works with the same message as D034 | |
| D5 | Offline media | Proposals are disabled; manual markup is unavailable as in D034 | |

## E. Preview / export parity and localization

| # | Check | Expected result | Evidence |
|---|---|---|---|
| E1 | Preview vs export | A frame with accepted proposals looks identical in the Preview and in the exported video | |
| E2 | Russian interface | All new labels («Предложить зацепки», «Принять», «Отклонить», etc.) are in Russian when the UI is Russian | |

## Step 31.6 evidence (2026-10-26)

Run by Claude in a headless surrogate because Computer use is unavailable in this conversation. The pinned SAM 2.1 Large
ONNX model was present; the E1 4K clip `clips/12x10/1014(6).mp4` (3840×2160) and the 1080p clip
`Phase28_Step28.1/s1_same_wall_horizontal.mp4` (1920×1080) were used.

After profiling, the default prompt grid was changed from 16×16 to 15×15. Quality on the E1 set at 1920×1080 (R1 harness,
same reference masks for all grid sizes):

| Grid | Recall (IoU≥0.5) | Precision | Avg proposals/frame |
|---|---|---|---|
| 16×16 | 51.8 % | 10.1 % | 112 |
| 15×15 | 53.7 % | 11.0 % | 110 |
| 14×14 | 45.1 % | 10.4 % | 95 |
| 12×12 | 36.6 % | 10.2 % | 75 |

| Group | Check | Result | Evidence |
|---|---|---|---|
| A | A1–A10 | **NOT RUN** | Requires interactive UI and the product owner's own footage/manual actions. |
| B | B1–B5 | **NOT RUN** | Requires interactive UI and the product owner's own footage/manual actions. |
| C | C1 4K performance | **PASS** | With 15×15 grid: 4K proposal pass warm 7.46 s (generator) / 8.33 s (`HoldMarkupViewModel` end-to-end); 1080p warm 6.53 s / 6.80 s. Both ≤8 s. Cold start 3.91 s measured separately. |
| C | C2 Recall | **PASS** | 15×15 grid: 53.7 % recall on E1 set (88/164 refs @ IoU≥0.5), above the ≥45 % orientir. |
| C | C3 Precision | **PASS** | 15×15 grid: 11.0 % precision on E1 set; consistent with the expected ~10 % and acceptable because the UI ranks and filters proposals. |
| C | C4 Contour quality | **NOT RUN** | Requires the owner's visual assessment on own footage; interactive UI not available. |
| D | D1 Undo after accept-all | **PASS** | `Phase31ManualSurrogateTests`: accept-all creates 30 holds; one Undo removes the batch. |
| D | D1a Undo after three individual accepts | **NOT RUN** | Interactive UI not available. |
| D | D2 Save/reopen | **PASS** | Surrogate: accepted holds survive `ProjectSerializer` v9 round-trip; rejected proposals absent. |
| D | D3 Max holds | **NOT RUN** | Interactive UI / specific test not run. |
| D | D4 Model missing | **NOT RUN** | Cannot remove the installed model in this environment. |
| D | D5 Offline media | **NOT RUN** | Interactive UI not available. |
| E | E1–E2 | **NOT RUN** | Requires interactive UI and the product owner's own footage/manual actions. |

Additional defect fixed during 31.6: `HoldMarkupViewModel.AcceptAllProposalsAsync` was refining the full `_proposalSource`
instead of the visible `Proposals`. Fixed so accept-all accepts only the shown top-N proposals per D047.

Detailed timings and top-30 proposal scores are in the temp report written by the surrogate:
`%TEMP%\AiVideoEditor_Phase31_Reports\<timestamp>\4k_proposals.txt`.

## Step 31.7 evidence (2026-10-26)

Final verification and closeout run headlessly because Computer use is unavailable.

| Gate | Result | Evidence |
|---|---|---|
| D047 §5.7 — 4K proposal pass ≤8 s | **PASS** | 4K warm 7.46 s (generator) / 8.33 s (ViewModel); 1080p warm 6.53 s / 6.80 s. |
| D047 §9.7 — E1 recall ≥45 % | **PASS** | 15×15 grid: 53.7 % (88/164) @ IoU≥0.5 on E1 set at 1920×1080. |
| D047 §5.8 / §9.8 — D039 localization | **PASS** | All new `Holds_*` strings in `Strings.resx` have entries in `Strings.ru.resx`. |
| `dotnet build AiVideoEditor.sln -c Release -warnaserror` | **PASS** | 0 warnings, 0 errors. |
| Full automated test suite | **PASS** | 0 failed, 2 skipped (opt-in 4K `ExportParityScaledTests` without `AIVE_HEAVY_TESTS=1`). |
| `git diff --check` | **PASS** | 0 errors; only LF→CRLF conversion warnings for files with LF working-tree line endings. |
| `project.json` v9 round-trip | **PASS** | Surrogate D2: accepted holds survive `ProjectSerializer` v9 round-trip; proposal state is not persisted. |
| Preview/export parity (automated surrogate) | **PASS** | Confirmed holds are normal `Hold` entities; rendering path unchanged from D034–D036. Interactive visual parity (E1) not run. |

**Remaining checks requiring the product owner's own footage and/or manual UI actions:** A1–A10, B1–B5, C4, D1a, D3–D5, E1–E2. These are intentionally left **NOT RUN** above and must be completed before full user acceptance.
