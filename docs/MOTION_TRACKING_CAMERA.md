# Motion Tracking Camera — automatic virtual camera following an object (concept)

**Status: Future / Backlog** — recorded on 2026-11-08 from the product owner's concept. Not a phase, no step, no acceptance
criteria, no decision, not approved for implementation. No tracking algorithm is chosen. This file is the basis for a
later, separate technical specification and its decision in `DECISIONS.md`.

Russian working name: «Motion Tracking Camera — автоматическая виртуальная камера с отслеживанием объекта».

## 1. Concept (the product owner's text, condensed)

The user picks a moving object in the Preview (a climber, a ball, a car, an animal…). The editor analyses its motion and
drives the position and the scale of the visible part of the source picture so that the object stays in the centre of
the frame. Not bound to an aspect ratio; usable on any footage.

**Main principle:** the user chooses the object and the camera parameters; the editor itself analyses the motion and
produces a finished smooth follow — no manual cutting of clips, no fitting of joins, no fixing of black areas.

### Camera modes

| Mode | Behaviour |
|---|---|
| **Follow** | The camera moves after the object inside the available source picture; the scale stays as it is. |
| **Fixed Zoom** | The user sets the camera's scale with a slider; the camera follows at that constant zoom. |
| **Dynamic Zoom** | The scale changes automatically and smoothly with the object's position and size, within a base scale and allowed limits set by the user. |

In every mode the main task is to keep the object in the centre of the visible area.

### Transitions of the camera (Transformation Duration)

- **Start Transition** — at the start of the range the camera keeps its original framing, then moves smoothly into the
  follow.
- **End Transition** — at the end of the range the camera returns smoothly to the original position and scale.
- Set independently (e.g. 1 s / 1 s); a zero duration means the switch happens without smoothing.
- These are part of the virtual camera's animation, **not** clip-to-clip transitions (D044).

### Settings of the first version

Object selection directly in the Preview; the time range; the mode; the zoom; the start / end transition durations; the
smoothness of the motion; limits of the allowed movement and zoom. The interface stays simple: automatic work first, no
manual editing of the camera path.

### Constraints and stability

- The camera never leaves the source picture; no black areas outside the source video — position and zoom are corrected
  within the allowed limits when needed.
- Temporary loss of the object and its re-detection are handled.
- No sudden jumps of position or zoom.
- No silent switch of the follow to another object.

### Architecture asked for by the concept

Two independent components:
- **Object Tracking** — analyses the picture and produces a time series of the object's position (and, where possible,
  size); knows nothing about how the result is used.
- **Virtual Camera** — takes the tracking data and computes the visible area's position and scale as a smooth animation,
  with the mode, the transitions and the source-bound constraints.

The result is non-destructive and stays editable.

### Later, not in a first version

Look-ahead framing (offset by the motion's direction); manual editing of the camera path; several objects; other
intelligent-framing algorithms.

### Examples

Bouldering / climbing — following the athlete across the wall; football — the ball; motorsport — a car; wildlife — an
animal; ordinary editing — a virtual camera move inside a wide source.

## 2. How it fits the current architecture (checked on `main` `2a8a2f9`, 2026-11-08)

### Transform and crop today

- A clip's framing is static: `PositionX`, `PositionY`, `Scale`, `RotationDegrees`, `Opacity` and `Crop` on `VideoClip` /
  `ImageClip` (`src/Core/Entities/Clip.cs`, bundled as `VisualProperties` in `ClipProperties.cs`; limits in
  `ClipPropertyLimits`: scale 0.01–10).
- `CropRect(Left, Top, Right, Bottom)` — normalized 0..1 insets of the source.
- `CompositionMath.Layout` (D018): crop → "contain" fit of the cropped source → scale → rotation → position (the centre's
  offset from the canvas centre in canvas pixels). Preview gestures: `PreviewTransformMath` (D038). Fill / Fit (D041) write a
  one-shot `Scale`; no mode is stored.

### Keyframes

None exist; every clip property is constant over the clip. D042 §3 puts "full motion design (keyframes, speed ramps, motion
paths)" outside the plan unless a separate justification appears. The virtual camera is an **automatically computed**
time-varying framing, not user keyframes, but it is a motion path of the picture: taking it up needs that separate
justification and the product owner's decision (D042 §3, "a new large function"). D052 K1 (2026-11-08) also deferred
manual key frames for the hold tracking; the first version of the concept asks for no manual camera path, so it needs no
key frames either — but its "manual editing of the camera path" (later, §1) would, and the two should then share one
design.

### Time-varying values already in the pipeline

- Per-frame values are computed in C# from whole-frame indices, never as FFmpeg expressions: the Zoom / Push transitions
  (`TransitionPresentation`, D044) reach a layer as `CanvasMotion(Scale, OffsetX, OffsetY)` applied after its own transform;
  the text motion `TextMotionRule` (D043) uses an eased progress.
- All of it is assembled in `PlaybackSnapshot.LayersAt(t)` (`src/Core/Playback/`), shared by the Preview and the export.

### Rendering — one path for Preview and export

`PlaybackSnapshot` → `CompositionLayer`s → `CompositionDrawPlan.Build` → `DrawOperation`s (`Affine2D`) → `CompositionPainter`
(Preview in `CompositionView`; export through `AvaloniaCompositionRasterizer` → BGRA frames → `FfmpegExportEncoder` over a
raw-video pipe). There is no FFmpeg filter graph, so a per-frame camera needs **no new FFmpeg capability**: it is one more
per-frame value in `LayersAt`, and Preview = export (D018 / D023) holds by construction.

### Camera motion analysis of R2 → E (D052) — related, not the same

- `CameraMotionEstimator` (`src/Core/Motion/`) estimates the **global** motion of the wall relative to the marking frame
  (pyramidal Lucas–Kanade on wall corners, `FeatureTracker`; RANSAC similarity / homography). It does **not** track an
  object; a moving climber is exactly what it rejects as an outlier.
- Reusable patterns: the analysis track keyed by the video fingerprint, the request and an `AnalysisVersion`, kept in an app
  cache (`ICameraMotionCache` / `CameraMotionCacheStore`, `%LOCALAPPDATA%\AiVideoEditor\cache\camera-motion`, LRU), not in
  `project.json`; `CameraMotionCoordinator` running the analysis with a status and the export waiting for it
  (`ExportWorkflow`, `ExportPreflight`); per-sample reliability with the lookup states `NoData` / `Hidden` / `Aligned` — a
  ready model for "object lost / re-found".
- Possible use: the camera motion could separate the object's motion from the camera's (relevant for hand-held footage) —
  a research question, not decided.

### Vision

`src/Vision` has one ONNX model: SAM 2.1 Hiera Large, click-prompted image segmentation of holds (D034, `docs/AI_MODEL.md`),
DirectML with a CPU fallback. No detection or tracking model exists. Whether SAM 2's video propagation (its memory
mechanism, not part of the current image-only export) or another tracker is suitable is for the research.

### Measured costs that bound the design (development machine; `docs/PHASE_HOLD_TRACKING.md` §7.2, §19.5)

- SAM 2.1 Large: encoder ≈ 0.35 s per frame on the GPU, ≈ 7.4 s on the CPU; 852 MB encoder. A per-frame SAM pass over a
  3-minute 30 fps clip (5400 frames) would be ≈ 30 min on the GPU even before any memory / propagation step — a SAM-based
  tracker is practical only on a sparse sampling or with a lighter model.
- The R2 → E camera analysis (grey frames at 960 px, one every 0.1 s, denser on jolts): 47–617 s clips took 861 s in all,
  a 617 s clip 179 s — about 0.3 × the clip's duration; its track ≈ 80 bytes per sample. A comparable object analysis
  has to be of the same order to be usable in an editing session.
- Rendering a per-frame framing is cheap (one more affine per layer); the cost is all in the analysis.

These are facts about the existing code, not estimates of an object tracker's cost.

### Persistence

`project.json` v14 (`ProjectSerializer.CurrentFormatVersion`); clip properties through `ClipDto` / `VideoClipDto` / `CropDto`
(`src/Project/Persistence/ProjectFileDto.cs`). A future camera would need a new format version for its parameters only.

## 3. Direction for the future specification (proposals, not decisions)

1. **Two components as asked:** an object track (Core contract + an analysis implementation, cached like the camera-motion
   track, never in `project.json`) and a pure Core function *track + parameters → camera framing at source time `t`*.
2. **Representation of the camera:** a viewport in normalized source coordinates (centre, zoom) per frame, applied as an
   effective crop / scale of the picture layer inside `LayersAt` — so the hold contours, which are mapped through the
   layer's transform, stay on their holds, and the "inside the source" constraint is a clamp of a rectangle to [0, 1].
3. **Non-destructive:** the clip's own Position / Scale / Crop stay the base framing (the start and the end of the
   transitions); the project stores only the camera's parameters (object prompt at a source time, range, mode, zoom and
   its limits, transition durations, smoothness) — Undo / Redo one step per user action, Split / Duplicate rules to decide.
4. **Smoothing offline:** the whole track is known before rendering, so a non-causal smoothing with speed / acceleration
   limits and a dead zone is possible; the transitions are an eased blend between the base framing and the follow.
5. **Time:** the range and the track in source time, `MediaTime` ticks (D007); transition durations as ticks, rounded to
   whole frames like the other per-frame rules; the clip's Speed maps timeline → source time as today.

### Limits that follow from the concept itself (audit 2026-11-08)

- **Headroom.** "Never outside the source" means the camera can move only where the source is larger than the visible
  area. A clip fitted exactly to the canvas (Scale 1, no crop — e.g. 4K on a 4K or 1080p landscape canvas) leaves **no room
  for Follow** at all; movement appears only with a zoom > 1 or a canvas of another shape (a vertical canvas over
  landscape footage, D041). Follow without zoom is therefore useful mainly on vertical canvases.
- **Resolution.** A zoom of K on a 4K source exported at 4K shows 3840 / K source pixels across: 2× is effectively
  1080p. Dynamic Zoom's upper limit is a quality limit, not only a framing one.
- **The object leaving the source** (the climber at the top of a tall wall, outside a tripod's frame) cannot be followed
  — only held at the clamp.
- **Climbing-specific difficulty for a tracker:** a large, deforming body with limbs far from its centre; chalk, motion
  blur on dynamic moves; the camera often static (object motion = image motion), sometimes hand-held (both mixed);
  the climber's colours close to holds or walls. What "the centre of the object" is (a box centre, the body's centre of
  mass, the torso) is a product question with a visible effect.

## 4. Open questions for the technical research

- The tracking algorithm — candidate families to compare, **none chosen**: (a) classical single-object trackers
  (correlation filters such as KCF / CSRT / MOSSE; point tracking with the existing pyramidal Lucas–Kanade code of
  `FeatureTracker` on the object, not the wall); (b) a person detector plus association (the object a climber — licences
  vary widely between model families and have to be checked); (c) a dedicated learned single-object tracker exported to
  ONNX; (d) SAM 2 video propagation (needs an export of its memory modules, not in `src/Vision` today; cost above). Measured
  for each: accuracy on the real footage (climbing first), speed on 4K, licence, model size, DirectML / CPU, determinism.
- The evaluation: a set of the owner's climbing clips (tripod, hand-held, vertical / landscape, a fall, a climber leaving
  the frame) with hand-annotated boxes on sampled frames, an offline harness in `tools/` like
  `tools/hold-tracking-check/`; tracking metrics (centre error, overlap, frames lost, re-detections, wrong switches) and
  camera metrics computed from a track (object inside a central zone, clamp events, jerk / acceleration). Thresholds are
  an owner decision after the measurement, as E-4 was.
- Storage and determinism: the object track as derived data in the application cache (keyed like `CameraMotionCacheStore`:
  the file's fingerprint, the request incl. the object prompt, an `AnalysisVersion`), never in `project.json`; Preview and
  export read the same cached track, so they agree by construction on one machine; a GPU / CPU or model change re-analyses
  and may change the result — to be measured and stated, not hidden.
- How the object is chosen (a click, a box, a SAM mask) and how a wrong switch to another object is detected / prevented.
- The loss policy: hold the last framing, drift back, or ease to the base framing; the re-detection rule.
- Rotation ≠ 0 and the source-bound clamp; interaction with the user's crop (the camera inside it or replacing it).
- Interaction with Fill / Fit and vertical canvases (D041), with transition `CanvasMotion` (D044), with the hold contours
  and their camera motion (D052), with templates (D045 / D048).
- Where the camera lives in the model (a clip property, a separate effect) and the `project.json` change.
- The relation to D042 §3 (motion paths) — the justification to present with the decision.
- A first-version scope (e.g. Follow and Fixed Zoom first, Dynamic Zoom later) — a product decision.

## 5. Rules of this record

- No phase is created (the product owner, 2026-11-08); no implementation is started; no algorithm is chosen.
- The roadmap audit of 2026-11-08 (`docs/ROADMAP_AUDIT_2026-11-08.md`) keeps the concept in Future / Backlog: it is not
  ready for a phase (no tracker, no evaluation data, no D042 §3 justification). Its proposed first step, when the owner
  takes it up, is a research **R4** without product code (§4), which may run in parallel with other work as R1–R3 did.
- Taking it up needs: the research above → a technical specification → a decision in `DECISIONS.md` with the product
  owner's approval → a phase number.
