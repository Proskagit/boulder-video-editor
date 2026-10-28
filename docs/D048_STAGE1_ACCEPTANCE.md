# D048 stage 1 — acceptance on the owner's clips (prepared 2026-10-27)

D048 stays **Proposed**. This is the procedure for the stage 1 criteria that need real footage and the owner's judgement
(D048 §5, stage 1: 2 — the transfer on the owner's clips; 4 — accept / cancel / back to `T` in the app; 7 — precision /
recall at agreed thresholds). The automatic tests cover the rest on synthetic frames; they are not a substitute for this.

## What was already run (Claude, 2026-10-27; not an acceptance)

- Clips: `D:\Времянка для монтажа\Боулдер 27.09.2026\video_20260927_100405.mp4` (coral route) and `…_103744.mp4` (blue
  route), 3840×2160 HEVC, SDR (bt709). `T` = 11 s, the clean frame at 6 s.
- `tests/Vision.ModelTests/RouteSelectionRealClipTests` (local, outside the solution): transfer succeeded on both; click →
  review 22–28 s on the RTX 4070 SUPER; proposals at τ 0.06 / 0.08 / 0.10: 19 / 20 / 24 (100405), 28 / 50 / 74 (103744).
  At 0.08 the 103744 set also contains black and purple holds — the reference drifts to dark colours (as T1 warned).
- The app (Debug, `--open-project %TEMP%\aive-d048-route`, a 15 s copy of 100405): the whole scenario passed after the
  threading fix; 21 holds accepted at τ 0.09 in one step, Undo / Redo, Esc.

## What the owner needs to provide

1. **The reference set**: for each clip, `T` (the clean frame just before the climb) and the route's holds marked on the
   clean frame (`T − 5 s`) — or the owner's verdict per proposal: route hold / not / duplicate, plus the missed ones.
   Claude's T1 labels (`tools/hold-tracking-research/labels`) are not the owner's and cannot accept criterion 7.
2. **The clips** beyond 100405 / 103744 that represent the hard cases: a moving camera between `T − 5 s` and `T` (for
   criterion 2), dark / low-chroma routes (blue, black, purple), HDR footage (HLG or PQ, if the owner shoots it).
3. **The thresholds to agree**: the default tolerance (now 0.08), the score (0.5) and whether the D047 grid stays on by
   default (it raises recall and time: ≈ 22–28 s per run on 4K with it).

## Procedure per clip

1. Open a copy of the clip in a test project (never the working project); pause on `T`; select the clip.
2. «Разместить зацепки» → click one hold of the route on the frame at `T`.
3. Expect either the review on the clean frame or one of the messages (transfer failed, no hold at the point, nothing
   new, frame not decodable) with nothing changed and the playhead at `T`.
4. Criterion 2: check that the dashed control hold on the clean frame is the hold clicked at `T` (the same hold, not a
   neighbour); with a moving camera note the shift by eye or failed transfer.
5. Criterion 7: count at the default tolerance — true route holds, false ones (duplicates count as false), missed ones;
   then try ±0.02 and note the counts.
6. Criterion 4: «Принять» → one Undo step; the holds sit on their holds at `T` (the tracking); Undo restores the clip;
   «Отмена» / Esc changes nothing; the playhead is at `T` in both cases.
7. Note the time from the click to the review.

## To re-run the local harness

```bash
dotnet test tests/Vision.ModelTests/Vision.ModelTests.csproj --filter "FullyQualifiedName~RouteSelectionRealClipTests" --logger "console;verbosity=detailed"
```

`AIVE_BOULDER_DIR` overrides the clips folder, `AIVE_ROUTE_OUT` the folder of the overlay PNGs (the clean frame with the
proposals at τ 0.08).
