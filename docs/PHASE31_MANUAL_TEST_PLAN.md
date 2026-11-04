# Phase 31 — Automatic hold proposals: manual test plan

Decision: DECISIONS.md D047. Scope: automatic hold proposals on the first markup frame of a video clip; the user
reviews, accepts or rejects them. Phase 31 changes no behaviour of the existing manual markup (D034–D036); it adds an
optional first step before the manual flow.

Environment: the development build or the portable build; the pinned SAM 2.1 Large ONNX model installed as
`docs/AI_MODEL.md` describes; owner gym footage (4K and 1080p clips).

Legend: **PASS** — the criterion is met; **FAIL** — a defect; **PARTIAL** — part met, the rest recorded; **NOT RUN** —
not run, with a reason; **DEFERRED** — not run by the product owner's decision, with its reason; not a PASS and not a
blocker.

**State (2026-11-04): accepted by the product owner with two deferred checks, D3 and C5** — «Manual acceptance by the
product owner» below. Every other row has a PASS.

## A. Proposal generation and basic review

| # | Check | Expected result | Evidence |
|---|---|---|---|
| A1 | Enter markup mode on a clip with no holds (toggle «Предлагать зацепки автоматически» on) | The progress indicator appears and proposals are drawn | |
| A1a | Enter markup mode with the toggle off | No proposal pass runs; the manual D034 flow is available immediately | |
| A2 | Proposal drawing | Each proposal is a dashed/translucent contour, distinct from confirmed holds; the selected proposal is highlighted | **PASS** — the logic automated (2026-11-04), the look checked by the product owner (2026-11-04) |
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
| C4 | Accepted proposal contour | The refined full-resolution contour follows the hold edge at least as well as a single manual click | **PASS — the product owner, 2026-11-04 (by hand, `docs/PHASE31_REMAINING_CHECKS.md`)** |
| C5 | 1080p source clip | Proposals are generated at source resolution (no upscaling) or at 1080p; quality is comparable | **DEFERRED — the product owner's decision of 2026-11-04: the owner's working material is 4K; the visual check on a 1080p clip (no stretching, proposals roughly as good as on 4K) waits until it is needed in practice; not a PASS** |

## D. Edits, persistence and edge cases

| # | Check | Expected result | Evidence |
|---|---|---|---|
| D1 | Undo after accept-all | One Undo removes the whole batch of accepted holds; Redo restores them | |
| D1a | Undo after accepting three proposals individually | Three Undo steps remove the holds in reverse order; Redo restores them | **PASS — the product owner, 2026-11-04 (by hand, `docs/PHASE31_REMAINING_CHECKS.md`)** |
| D2 | Save and reopen | Accepted holds are present; rejected proposals are not present; the project opens as v9 | |
| D3 | Max holds | Accepting proposals up to the max-holds limit works; further accepts are refused with the existing message | **DEFERRED — the product owner's decision of 2026-11-04: 500 holds by hand are impractical (proposals only on a clip without holds, ≤ 30 per pass); not a PASS; an automated or other technical check may be considered later** |
| D4 | Model missing | The proposal button is disabled; manual markup still works with the same message as D034 | **PASS — the product owner, 2026-11-04 (by hand, `docs/PHASE31_REMAINING_CHECKS.md`)**: the model hidden by `Set-HoldModelHidden.ps1`, then restored |
| D5 | Offline media | Proposals are disabled; manual markup is unavailable as in D034 | **PASS — the product owner, 2026-11-04 (by hand, `docs/PHASE31_REMAINING_CHECKS.md`)**: «Разметить зацепки» greyed out on the offline clip, no markup possible |

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

## Automated acceptance of A, B and E (2026-11-04)

Run on `main` `3bb5fa1` plus the branch `test/phase-37-31-acceptance`. Two levels: the view-model tests
`HoldMarkupProposalUiTests` (UI.Tests, in CI, a fake generator) and the real-model tests
`Phase31RealModelAcceptanceTests` (Vision.ModelTests, local, outside the solution): the pinned SAM 2.1 Large model, the
E1 4K clip, the real `HoldMarkupViewModel` with its real proposal pass, refinement, Undo and project. The clip is read
only; no project is saved outside a temporary folder.

**Three defects found and fixed** (`HoldMarkupViewModel`, within D047 §2; each test below failed before the fix):
- B1 — Shift+click over a proposal selected the proposal instead of starting a manual candidate at the point; now
  Shift+click always starts the manual candidate and the review stays.
- B2 — Esc with no candidate left the whole markup mode while proposals were under review; now the review stays (a
  candidate is discarded first, as before).
- B3 — Enter with a manual candidate accepted the selected proposal instead of confirming the candidate; now the
  candidate is confirmed and the unreviewed proposals stay.

| # | What it checks | How | Evidence | Result |
|---|---|---|---|---|
| A1 | toggle on: progress, then proposals | unit + real model | `Starting_markup_generates_ranked_proposals_…`; real: `A1_A3_A5_A6_A7_A8_…` — `IsGeneratingProposals` and `ProposalProgress` observed during the real pass, then ranked proposals and their contours | **PASS** |
| A1a | toggle off: no pass, manual flow at once | unit | `A1a_with_the_toggle_off_…` (new): no generator call, no progress, a click makes a candidate | **PASS** |
| A2 | proposals apart from confirmed holds, the selected one highlighted; dashed / translucent look | unit + manual look | `A2_proposals_are_their_own_layer_…` (new): `ProposalContours` apart from the holds, the selected index follows Tab and click; the look itself (grey dashed pen, the selected one yellow dashed, `HoldMarkupOverlay`) is not compared by pixels — checked by the product owner by hand on 2026-11-04 | **PASS** (logic automated; the look by the owner) |
| A3 | ≤ 30 by default; the count, rank and score shown | unit + real model | `A3_at_most_thirty_…` (new): 40 generated → 30 shown, labels with rank and score; real: ≤ 30 ranked by score | **PASS** |
| A4 | Tab / Shift+Tab, click selects | unit | `Tab_with_proposals_cycles_the_selection`, `Shortcuts_route_proposal_review_commands`, `Clicking_inside_a_proposal_contour_selects_it` | **PASS** |
| A5 | Enter / «Принять» → a confirmed hold | unit + real model | `Accepting_a_single_proposal_…`; real: the accepted proposal refined at full resolution into a `Hold` with its surface colour, one Undo step | **PASS** |
| A6 | Delete / «Отклонить» → gone, the next selected | unit + real model | `A6_rejecting_selects_the_next_proposal` (new); real: the next one selected | **PASS** |
| A7 | «Принять все» → the shown ones in one step | unit + real model | `AcceptAll_adds_all_refined_holds_in_one_undo_step`; real: exactly the shown ones accepted, one Undo step; the top-N window then shows the next of the remaining proposals | **PASS** |
| A8 | «Отклонить все» → gone, manual flow stays | unit + real model | `RejectAll_clears_proposals_…`, `Manual_click_after_rejecting_all_…`; real: on a new pass reject-all, then a manual candidate | **PASS** |
| A9 | none generated → a message, manual markup | unit | `A9_no_proposals_say_so_…` (new): `Holds_NoProposals` shown, a click makes a candidate | **PASS** |
| A10 | cancel → progress stops, nothing shown, project unchanged | unit + real model | `A10_cancelling_the_generation_…` (new); real: `A10_cancelling_the_real_pass_…` — no proposals, no Undo step, `IsDirty` unchanged | **PASS** |
| B1 | Shift+click → manual candidate at the point, proposals stay | unit + real model | `B1_shift_click_starts_a_manual_candidate_even_over_a_proposal_…` (new; failed before the fix); real: Shift+click on a proposal's centroid | **PASS** (defect fixed) |
| B2 | Esc discards the candidate, back to the review | unit + real model | `B2_Esc_discards_the_manual_candidate_…` (new; failed before the fix) | **PASS** (defect fixed) |
| B3 | a manual confirm keeps the unreviewed proposals | unit + real model | `B3_a_manual_hold_confirmed_with_Enter_…` (new; failed before the fix) | **PASS** (defect fixed) |
| B4 | Done / Play / Seek leave the mode; accepted and manual holds stay, proposals gone | unit + real model | `B4_Done_Play_or_Seek_…` (new, one case each); real: a seek | **PASS** |
| B5 | re-entering a clip with holds: no proposals, holds shown | unit + real model | `Re_entering_a_clip_with_holds_skips_proposals`, `B5_re_entering_…` (new); real: re-entered after B4 | **PASS** |
| E1 | a frame with accepted proposals the same in Preview and export | real model + render tests | real: `E1_the_accepted_real_holds_are_exactly_the_outlines_…` — the `PlaybackSnapshot` outline polygons and colour equal the accepted holds; Preview and export draw that snapshot outline by the same renderer: `HoldOutlineRenderTests`, `ExportHoldAnimationEndToEndTests` | **PASS** |
| E2 | the new texts in Russian (and English) | unit | `E2_the_review_texts_follow_the_interface_language` (new): ru and en | **PASS** |

**A2 — the look (manual; PASS by the product owner on 2026-11-04).** Why it was manual: the pen style is code in `HoldMarkupOverlay`; no test compares the drawn pixels, and
whether proposals are «distinct» from confirmed holds is the owner's visual judgement. Steps: open any project with a
video clip without holds; turn «Предлагать зацепки автоматически» on; enter the hold markup; wait for the proposals;
accept one with Enter; press Tab twice. Expected: proposals are thin grey dashed contours; the selected one is yellow
dashed; the accepted hold is a solid outline unlike both. On a failure send: a screenshot of the frame with proposals
and the accepted hold, and the row's number.

Out of the scope of this run: C4, D1a, D3–D5 — see «Manual acceptance by the product owner» below.

## Manual acceptance by the product owner (2026-11-04)

Run by the product owner by hand on a build of `main` with PR #38 (the B1–B3 fixes), with the instruction and the scripts
of `docs/PHASE31_REMAINING_CHECKS.md` (`tools/manual/Start-Phase31Check.ps1`, `tools/manual/Set-HoldModelHidden.ps1`) on
a temporary copy of the E1 4K clip. Recorded as reported by the owner; no automated result is used in its place.

| # | Check | Result |
|---|---|---|
| A2 | the look: proposals grey dashed, the selected one yellow dashed, the accepted hold a solid outline | **PASS** |
| C4 | the accepted contour follows the hold edge at least as well as a manual click | **PASS** |
| D1a | three accepts one by one; three Undo in reverse order; Redo restores them | **PASS** |
| D4 | model missing (hidden, then restored): no proposal pass, no crash, the D034 message | **PASS** |
| D5 | offline media: «Разметить зацепки» greyed out, no markup | **PASS** |
| D3 | max holds (500) | **DEFERRED** — impractical by hand (proposals only on a clip without holds, ≤ 30 per pass); no automated test |
| C5 | 1080p source clip | **DEFERRED** — the owner works on 4K; the 1080p visual check waits until it is needed |

With this run every row of A, B, C1–C4, D1, D1a, D2, D4, D5 and E has a PASS. **D3** and **C5** are **DEFERRED** by the
product owner's decision of 2026-11-04 (not PASS, not blockers of later work; D3 may get an automated or other technical
check later, C5 is run when 1080p footage matters in practice). **Phase 31 is accepted by the product owner on
2026-11-04 with these two deferred checks.**
