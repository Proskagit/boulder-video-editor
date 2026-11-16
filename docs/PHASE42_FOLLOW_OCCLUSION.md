# Phase 42 — Follow and Occlusion (D061)

The decision record is DECISIONS.md D061; the models are in `docs/AI_MODEL.md` ("People models"); the manual plan is
`docs/PHASE42_MANUAL_TEST_PLAN.md`. This document describes the user-visible behaviour, the processing, the requirements, the
limits and the benchmark on the four test videos.

## 1. What the user gets

- **Follow** («Следить за скалолазом», Inspector → «Слежение и люди» of a video clip): the visible part of the picture moves
  with the chosen climber, the climber's torso kept at the canvas centre, smoothly, never with a black border. The clip's
  scale is the room the frame has to move (no Auto Zoom): a vertical video on a vertical canvas needs Scale above «Заполнить
  кадр». Smoothness: Мягкая / Обычная (default) / Быстрая. With several people the user picks the climber in the Preview
  («Выбрать скалолаза», click); the only person of a clip is taken without a pick. Losses are bridged: a short miss glides, a
  longer one holds the frame and then moves on smoothly; another person is never followed silently.
- **Occlusion** («Скрывать контуры за людьми»): every person in the frame is segmented separately; a hold's outline disappears
  exactly where a person is in front of it and stays where the hold is seen. «Показывать маски людей в Preview» shows each
  person's mask in its own colour.
- **Background analysis**: once per video file, cached between sessions; progress, «Остановить анализ», «Повторить анализ», a
  message when something fails; the export waits for a running analysis and warns when Follow or Occlusion cannot work.

## 2. Processing

```
video file ─► decoder (1024 px, hardware) ─► every 0.1 s: RF-DETR-M → RTMPose-m → appearance → tracks      (Follow)
                                         └► every 1/15 s: SAM 2.1 tiny video, one memory per person → masks (Occlusion)
cache (%LOCALAPPDATA%\AiVideoEditor\cache\people, by the file's content + model + version)
snapshot (Preview = export) ─► by the shown frame's timestamp: the clip's position (Follow); the outline's mask (Occlusion)
```

Details: D061 §3–§5. Everything is in normalized coordinates of the source frame and source time, so editing the clip (crop,
scale, canvas, split, trim, speed) never re-analyses.

## 3. Requirements

- Follow: the people models; DirectML GPU recommended, works on the CPU (slower analysis).
- Occlusion: the people models and a **DirectML GPU**; without one: a message, the outlines drawn as before.
- Disk: ≈ 348 MB of models; the cache ≈ 1–5 MB per minute of video (masks), at most 2 GB (oldest removed first).
- Memory: the analysis process peaks at ≈ 1.5–1.8 GB with up to 6 people segmented at once.

## 4. Benchmark (2026-11-16, Ryzen 7 7800X3D, RTX 4070 SUPER, 32 GB, Windows 11)

The four vertical test videos (4K HEVC 60 fps, display 2160 × 3840), analysed by the product's code (`tools/people-bench`),
the originals read in place (never copied, re-encoded or changed). Follow evaluated on a 1080 × 1920 canvas at Scale 150 %
(the room to move on both axes), smoothness Normal. Analysis time is separate from rendering: the export's speed is Phase 40's
(the camera and the masks are looked up per frame — no measurable change).

### 4.1 Analysis time

| Video | Duration | Detection pass | People tracks | Masks pass | People segmented |
|---|---|---|---|---|---|
| `VID_20251217_191039` | 80.1 s | 28.0 s (**2.9×** real time) | 3 | 143.9 s (0.56×) | 1 |
| `VID_20251217_194717` | 35.4 s | 12.8 s (**2.8×**) | 1 | 66.5 s (0.53×) | 1 |
| `VID_20251223_195009` | 13.4 s | 6.8 s (**2.0×**) | 9 | 37.5 s (0.36×) | 2 |
| `VID_20251223_195901` | 93.4 s | 32.9 s (**2.8×**) | 16 | 314.2 s (0.30×) | 16 (over time) |

The detection pass was decoder-bound with software decoding (74 fps, 1.1–1.2× real time); with hardware decoding (337 fps)
it is model-bound. The masks pass costs ≈ 45 ms per frame + ≈ 75 ms per person (SAM 2.1 tiny's memory attention dominates).
The far spectators of `195009` (smaller than 12 % of the frame) are not segmented.

### 4.2 Follow

| Video | Target | Seen | Losses ≥ 0.5 s | Tracks joined | Switches to another person | Jitter (RMS of the 2nd difference, canvas px) | Speed p95 (px/s) | Torso offset from the centre, median / p95 |
|---|---|---|---|---|---|---|---|---|
| `191039` | automatic | 100 % | 0 | 1 | 0 | 2.1 | 237 | 6.8 % / 19 % |
| `194717` | automatic | 100 % | 0 | 1 | 0 | 2.5 | 252 | 4.0 % / 24 % |
| `195009` | automatic (the far spectators ignored) | 100 % | 0 | 1 | 0 | 1.6 | 273 | 5.5 % / 34 % |
| `195901` | picked at 0:10 (several people of the climber's size) | 93.7 % | 3 (3.3 s, 1.5 s, 0.5 s) | 3 | 0 | 2.0 | 313 | 26 % / 49 % |

The offset is the climber's torso from the canvas centre in fractions of the canvas; large values mean the window is at the
edge of the source (the climber at the frame edge — the window cannot go further without a black border) or the climber is
lost (the frame holds). `195901` is filmed close up with a camera that lags: the climber is often at or beyond the edge.

### 4.3 The hard moments of `VID_20251223_195901` (contact sheets checked by eye)

| Time | What happens | Result |
|---|---|---|
| 0:12–0:16 | another person at the left edge (and the operator's hand at 0:14) | followed; the other person is a separate track and mask; no switch |
| 0:20 | a climber in yellow on the left | separate person, separate mask |
| 0:34.6–0:37.9 | the climber leaves the frame; a person in white climbs into the frame at 0:35 | **lost** (correct: the climber is not in the frame); the person in white (track 6) is **not** followed — another appearance; the frame holds, then moves on smoothly at 0:37.9 |
| 0:46.1–0:47.6 | only the climber's legs at the left edge | **lost** for 1.5 s: the legs' appearance does not match the torso's — a limit (§5) |
| 1:03–1:05 | a man crosses close in front of the camera | not followed; his own mask, separate from the climber's; the climber lost 0.5 s while hidden |
| 1:11–1:14 | the operator's hand over the frame | segmented as a person in front (correct for Occlusion), not followed |
| 1:20–1:33 | close-up, part of the body | followed |

### 4.4 Occlusion

- Faithfulness of the masks to SAM 2.1 (PyTorch): mean IoU 0.995 (the graphs), 0.989 end to end in C# (docs/AI_MODEL.md).
- Separation: on every checked frame with two or more people each has its own mask; the masks never overlap (one owner per
  pixel by construction). A person split by the tracker into two tracks is no longer started twice (D061 §4: a detection
  already covered by a mask is that person).
- The outlines in pixels (`PeopleOcclusionRenderTests`): hidden under a person, unchanged beside them, byte-identical without
  people; the Preview and the export run the same painter.
- Not measured: the pixel accuracy of a mask's edge against a hand-drawn mask (research T-14); outlines with real holds on the
  four videos (they have no marked holds) — part of the manual plan (C2, C4).

## 5. Limits and known problem scenarios

| Scenario | Behaviour | Possible solution |
|---|---|---|
| Vertical video at «Заполнить кадр» on a vertical canvas | Follow has no room — the Inspector says to zoom in | a fixed «Follow zoom» preset (not Auto Zoom) — the owner's decision |
| Less than half of the body at the frame edge after a loss (`195901` 0:46) | lost until the torso is seen again | match appearance by body part (legs / torso) or by the SAM mask's continuity across the loss |
| The climber fully out of the frame | lost; the frame holds and moves on smoothly when the climber returns | inherent |
| Several people of the climber's size in the clip | the user picks the climber once | — |
| A detector miss for a whole stretch (research: 0–15 % on the wall) | Follow glides / holds; Occlusion: no mask there | a second detector or a lower threshold with SAM re-prompts |
| People smaller than 12 % of the frame | not segmented | lower the limit (costs ≈ 75 ms per person per frame) |
| Position X / Y and the Move tool while Follow moves the axis | no effect on that axis | hide / disable them while Follow is on |
| Clip speed ≠ 1 | the camera's smoothing is in source time | smooth in timeline time |
| Strong shake, low light, 120 / 240 fps | not in the test set | — |

## 6. Files

- Core: `src/Core/People/*` (tracks, Follow, camera, masks, occlusion, analyzer), `Core/Entities/FollowSettings.cs`,
  `Core/Interfaces/IPeopleVision.cs`; the snapshot and the draw plan (`PictureSpan.Follow` / `PeopleMasks`,
  `CompositionDrawPlan.GeometryAt`, `OutlineDraw.Occlusion`); the preflight (`ExportPeople`, three issue kinds).
- Vision: `src/Vision/People/*`; Media: `Caching/PeopleAnalysisCacheStore.cs`; UI: `Services/PeopleAnalysisCoordinator.cs`,
  `ViewModels/Panels/InspectorViewModel.People.cs`, `PreviewViewModel.People.cs`, `Rendering/PeopleOverlay.cs`, the painter.
- Tools: `tools/people-models/*` (export, validation, comparison, contact sheets), `tools/people-bench`.
