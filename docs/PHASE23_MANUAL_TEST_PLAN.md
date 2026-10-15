# Phase 23 — manual test plan

Manual checks of the direct Move, Scale (corner handles) and Crop in the Preview (DECISIONS.md D038). Run in the real app on
Windows. Fixture: a project with a landscape video, a portrait (phone) video stored with a 90° rotation flag, an image and a
text clip, a 2 × 2 composition of four videos on V1–V4 (each scaled to 50 % and moved to a quarter of the canvas), one
clip rotated by 30°, and a locked track for the lock rows. Canvas 1920 × 1080 and a portrait 1080 × 1920 copy.

Status column — kinds of evidence, never mixed:
- **auto** — covered by automated tests only, not checked in the real app;
- **local (Claude)** / **PO** — the real-app run on Windows / the product owner's check.

| # | Scenario | Expected | Automated coverage | Status |
|---|---|---|---|---|
| 1 | Move and Crop buttons | Both off by default, the Preview takes no pointer input; pressing one turns the other off; off in Source mode and during an export; turning one on leaves the hold markup mode and vice versa | `PreviewTransformUiTests` | NOT RUN |
| 2 | The target's frame | With Move on and a clip under the playhead selected: a light-blue frame with a keyline on its visible picture (rotated with the clip), the centre marked, four corner handles; nothing in the export | `PreviewTransformUiTests` | NOT RUN |
| 3 | Click selection in the 2 × 2 composition | A click on another quarter selects its clip (timeline and Inspector follow); a click on empty canvas changes nothing; inside the selected clip's frame the selected clip wins | `PreviewTransformUiTests` | NOT RUN |
| 4 | Move a landscape clip | The picture follows the pointer; the Inspector's Position X / Y change live in whole pixels; Shift keeps one axis; one Ctrl+Z restores, Ctrl+Y repeats | `PreviewTransformMathTests`, `PreviewTransformUiTests` | NOT RUN |
| 5 | Move a rotated (30°) clip and a text clip | Same as row 4; the rotation unchanged | `PreviewTransformMathTests`, `PreviewTransformUiTests` | NOT RUN |
| 6 | Scale by a corner | Proportional around the centre, the centre does not move (Position unchanged), the opposite corner moves symmetrically; Inspector Scale live (0.01 %); one undo step | `PreviewTransformMathTests`, `PreviewTransformUiTests` | NOT RUN |
| 7 | Scale a rotated clip; scale to the limits | The corner follows the diagonal; at 1 % and 1000 % the frame stops, no error; dragging past the centre gives 1 % | `PreviewTransformMathTests` | NOT RUN |
| 8 | Crop the left edge of a landscape clip | Only the left edge moves; the rest of the picture stays exactly in place; Inspector Crop L, Scale and Position change; one undo step | `PreviewTransformMathTests`, `PreviewTransformUiTests` | NOT RUN |
| 9 | Crop the top of the portrait (rotation-flag) video on a landscape canvas | The picture does not grow or jump (anchored); the crop relates to the upright picture | `PreviewTransformMathTests` | NOT RUN |
| 10 | Crop by a corner; crop a rotated clip | Both edges follow; the handles lie on the rotated frame; anchored | `PreviewTransformMathTests`, `PreviewTransformUiTests` | NOT RUN |
| 11 | Crop to the minimum / out past the source edge | The edge stops (at least 1 % / 4 px stays; never outside the source); the full source outline dashed | `PreviewTransformMathTests` | NOT RUN |
| 12 | Crop typed in the Inspector | As before Phase 23: the picture re-fits (D018) | existing Inspector tests | NOT RUN |
| 13 | Crop on a text clip | No crop handles; the bar says text has no crop | `PreviewTransformUiTests` | NOT RUN |
| 14 | Esc during Move / Scale / Crop | The clip back exactly; no undo step, nothing to redo; Esc again turns the tool off | `PreviewTransformUiTests` | NOT RUN |
| 15 | Ctrl+Z / Ctrl+S / Space during a drag | Nothing happens until the release (keys swallowed) | `ShortcutRoutingTests`, `PreviewTransformUiTests` | NOT RUN |
| 16 | Two drags in a row; a drag after an Inspector edit | Two undo steps; the Inspector edit its own step | `ClipPropertyGestureTests`, `PreviewTransformUiTests` | NOT RUN |
| 17 | Playing | Frame and handles hidden, a press does nothing; after Pause available again | `PreviewTransformUiTests` | NOT RUN |
| 18 | Locked track | Grey dashed frame, no handles; a press says "Track … is locked.", nothing changes | `PreviewTransformUiTests` | NOT RUN |
| 19 | During an export | The tool is off and cannot be turned on | `PreviewTransformUiTests` | NOT RUN |
| 20 | Save after gestures; reopen | `formatVersion` 6; values as shown; the title loses "*" after Save; undo history consistent | `PreviewTransformUiTests` | NOT RUN |
| 21 | Preview vs export | An exported frame of the transformed 2 × 2 composition, the rotated and the cropped portrait clips matches the Preview frame | `PreviewTransformParityTests` | NOT RUN |
| 22 | Portrait canvas 1080 × 1920 | Move, Scale, Crop as in rows 4–10 | `PreviewTransformMathTests` | NOT RUN |
| 23 | Hold markup and holds after a crop / move | The confirmed outlines stay on the holds; the markup mode works after the tool is off | existing Phase 19–21 suites | NOT RUN |
| 24 | Window resize / small Preview | The frame and handles follow; small clips keep usable handles | — | NOT RUN |
