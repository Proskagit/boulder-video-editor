# Export performance (D059, Phase 40)

Measured 2026-11-10/11 on the target machine: AMD Ryzen 7 7800X3D (8 C / 16 T), NVIDIA RTX 4070 SUPER (driver 621.14),
32 GB RAM, NVMe, Windows 11, FFmpeg 9.0.1 essentials (gyan.dev). Tool: `tools/export-bench` (D059 §6). Every number is
the **median of 3 runs** of 600 frames (10 s at 60 fps), each run its own process, unless marked «1 run» (`real`, `long`:
7 200 frames, 2 min). Sources: `VideosForTest/12x10` (4K60 / 4K120 / 4K30 H.264 SDR) and the vivo HEVC 10-bit HLG clip.

Baseline = `07252bf` (before D059) with the harness built `-p:DefineConstants=BASELINE`. «After» = the branch
`feat/export-performance`, the pool size chosen automatically (K = 5 with libx264, 8 with NVENC on 16 threads).

## 1. Speed — the video stage, fps

| scenario | baseline | libx264 Balanced (default) | × | NVENC «Быстрый» | × | NVENC «Сбалансированный» | × |
|---|---|---|---|---|---|---|---|
| 4K60, 1 layer, 4K canvas | 12.1 | 30.7 | **2.55** | 46.6 | **3.86** | 44.7 | 3.71 |
| 4K60 → 1920×1080 | 12.9 | 61.4 | **4.76** | 69.3 | 5.37 | 66.1 | 5.13 |
| 4K60 → 1080×1920 | 12.7 | 68.2 | **5.37** | 70.8 | 5.57 | — | — |
| 4K60 → 1080×1920, blur | 13.0 | 60.9 | **4.70** | 68.1 | 5.26 | 66.9 | 5.16 |
| 4K60 → 2160×3840, blur | 11.6 | 28.9 | 2.49 | 44.5 | 3.83 | — | — |
| 4K120 source → 60 fps | 6.1 | 25.4 | **4.13** | 33.6 | 5.46 | — | — |
| 4K + text, «Боулдер», heart rate, animated outlines | 12.7 | 29.4 | 2.32 | 43.1 | 3.40 | — | — |
| 4K + VHS | 12.6 | 16.7 | 1.33 | 28.1 | 2.24 | — | — |
| 4K, two video layers | 11.6 | 22.8 | 1.96 | 30.8 | 2.65 | — | — |
| 4K, Dissolve + Push | 11.6 | 28.9 | 2.50 | 40.5 | 3.50 | — | — |
| HEVC HLG → 1080×1920 (HDR graph) | 11.9 | 32.8 | 2.76 | 33.7 | 2.83 | — | — |
| `real` (12×10 training, 1 run) | 11.6¹ | 28.9 | 2.48 | 27.9 | 2.40 | — | — |
| `long` (7 200 frames, 1 run) | 11.8¹ | 28.3 | 2.39 | 44.7 | 3.78 | — | — |

¹ The baseline's `real` / `long` single runs partly overlapped another baseline run (an operator error, see
`progress.md`); the sequential baseline is capped by the transport per process (77 ms a frame), and both values match the
other 4K baselines (12.0–12.1), so they are kept as indicative.

Whole export (wall time incl. audio and finalising), 10 s of 4K60: 50.9 s → 21.3 s (libx264) → 13.7 s (NVENC «Быстрый»).

### Against the research's targets (§11)

| target | result | met |
|---|---|---|
| 4K60 ≥ 2× after E1 | E1 alone was measured by the research (26 fps, ×2.0); after E1–E2 ×2.55 | yes |
| 4K60 ≥ 3× after E1 + E2 (libx264) | ×2.55 | **no** — see §3 |
| 4K60 ≥ 5× after E1–E3 (NVENC) | ×3.7–3.9 | **no** — see §3 |
| 4K → 1080p and vertical ≥ 3× after E1–E2 | ×4.7–5.4 (libx264) | yes |
| 120 fps sources ≥ 3× after E1 | ×4.1 (libx264), ×5.5 (NVENC) | yes |
| no scenario slower | slowest gain ×1.33 (VHS, libx264) | yes |

## 2. Resources (medians)

| | baseline 4K | libx264 4K | NVENC «Быстрый» 4K |
|---|---|---|---|
| system CPU (cores of 16) | ~5.6 | 15.0 | 11.4 |
| GPU 3D / NVENC load | — / 0 % | — / 0 % | 16 % / 24 % (Balanced p5: 60 %) |
| VRAM increase | 0 | 0 | ~0.9 GB |
| peak working set | 0.84 GB | 3.5 GB | 3.7 GB |
| two 4K layers / VHS (peak) | 1.3 / 0.9 GB | 5.4 / 3.8 GB | 4.6 / 5.7 GB |

The memory is bounded by construction (§D059 §2: ≤ Q + K + 1 decoded frames, K + 2 canvases, K rasterizers) and did not
grow over the 2-minute runs; most of the working set is transient large-object garbage of the decoded 4K frames between
gen-2 collections. On a machine with less memory K shrinks (a quarter of the available memory).

## 3. Why 4K reaches ×2.5 / ×3.9 and not ×3 / ×5

- **libx264 medium is now the limit at 4K.** With the transport and the rasterizer gone as bottlenecks, the CPU is full
  (15 of 16 threads busy) and the encoder's input blocks (17–30 ms a write). The same 30 fps with K = 3, 4, 5, 6, 8
  rasterizers (`ksweep`); the research measured libx264 medium alone at 41.6 fps on an idle CPU — here it shares the CPU
  with the decoder and the rasterizers. The D028 default output (byte-identical by requirement) cannot go faster on this
  CPU; «Быстрый» exists for that.
- **With NVENC the CPU side limits at ~45–47 fps** (4K): the same with K = 4, 6, 8; NVENC 24–60 % loaded; the decoder
  waits ~20 ms a source frame although ffmpeg decodes this file to BGRA at 174 fps alone — the decoding process, the
  BGRA conversion, the pipe copy, the bitmap copy, the Skia draw, the read-back and the encoder's YUV conversion share the
  cores and the memory bandwidth (about ten 33 MB passes a frame). A synchronous pipe read changed nothing (45 fps). The
  next steps are E6 items the research named (a decoded-frame buffer pool, fewer copies per frame, the high-rate decode
  skipping) — each a change of its own, measured, not part of this phase.
- **VHS** costs two full-canvas passes per frame (read back, treat, upload) in every rasterizer; **two layers** double the
  decoding and the bitmaps. Both still gained (×1.3–2.6).

## 4. Correctness

- libx264 «Сбалансированный»: **every MP4 byte-identical to the baseline** — all 13 scenarios (SHA-256 of 3 + 3 runs, and
  the single long runs). The canvases are therefore identical too.
- `ExportPipelineParityEndToEndTests`: the canvases of K = 1 over stdout and K = 3 / 6 over the pipe byte-identical and the
  MP4 SHA-256 equal on scenes with outlined bold text, «Боулдер», «Хайлайт», the heart rate, animated hold outlines, VHS,
  blur on a vertical canvas, Dissolve, Push and a picture in picture; the whole parity suite (Preview ↔ export) green.
- Found and fixed during the measurements (D059 §2): a self-join hang when a rasterizer failed, and the failure behind it —
  Avalonia's `GlyphRun` type initializer racing when two threads draw their first text (now warmed up once before any
  parallel drawing). 0 failures in 20 repeated runs after the fix (1 in 18 before).

## 5. NVENC against libx264 (calibration, D059 §5)

References: lossless copies of the export's canvases (yuv420p after the export's BT.709 conversion), 360 frames — A, B:
two 4K60 routes (12×10); C: the vivo HEVC HLG clip through the HDR graph on 1080×1920; D: 1080p60; E: 4K30 (180 frames).
VMAF every frame (the 4K model for A, B, E); Mbit/s; the encoder alone on an idle machine.

| setting | VMAF A | B | C | D | E | Mbit/s A (4K60) | encoder fps A |
|---|---|---|---|---|---|---|---|
| x264 veryfast CRF 18 | 95.46 | 95.59 | 87.56 | 92.50 | 94.24 | 21.6 | 81 |
| **x264 medium CRF 18** (D028 default) | 96.94 | 96.92 | 89.75 | 94.03 | 95.20 | 32.5 | 32 |
| x264 slow CRF 16 | 97.56 | 97.52 | 90.43 | 94.74 | 95.75 | 42.6 | 18 |
| NVENC p2 CQ 25 («Быстрый») | 96.64 | 96.71 | 89.96 | 93.74 | 94.46 | 35.0 | 139 |
| NVENC p5 CQ 22 («Сбалансированный») | 97.55 | 97.59 | 90.72 | 94.66 | 95.52 | 49.8 | 57 |
| NVENC p5 CQ 24 | 96.98 | 97.04 | 90.14 | 94.05 | 94.86 | 36.7 | 58 |
| NVENC p7 multipass CQ 20 («Высокое качество») | 97.92 | 97.90 | 91.15 | 95.04 | 95.96 | 63.6 | 68 |
| NVENC p5 CQ 26 without spatial AQ | 96.72 | 96.82 | 90.01 | 93.82 | 94.76 | 24.2 | 56 |

The rule: the largest CQ whose VMAF is not below the libx264 counterpart's on **all five** references.
- «Быстрый» (↔ veryfast CRF 18): p2 **CQ 25** (CQ 28 falls below on A).
- «Сбалансированный» (↔ medium CRF 18): p5 **CQ 22** (CQ 24 falls below on E, the 30 fps clip).
- «Высокое качество» (↔ slow CRF 16): p7 multipass **CQ 20** (CQ 23 falls below on A).
- «Пользовательский» (↔ medium CRF 14 / 18 / 23 / 28): p5 **CQ 19 / 22 / 27 / 32** (each the largest CQ measured to hold
  on every reference; CQ 18–33 measured).

What this means: at the same VMAF, H.264 NVENC needs **about 1.5× the bitrate of libx264 medium** (49.8 against
32.5 Mbit/s at Balanced on 4K60) — larger files for 2–4× the encoder speed; «Быстрый» NVENC lands at libx264 medium's size
(34.9 Mbit/s measured in the pipeline) with a VMAF between veryfast and medium. Spatial AQ lowers VMAF at the same CQ
(VMAF favours flat quantization) but keeps the export's choice for the visual check: AQ spends bits on the flat wall and the
dark areas, where block artefacts show. **The owner's visual check** (wall texture, chalk, fine detail, fast motion, dark
parts) decides; it is row C2 of `docs/PHASE40_MANUAL_TEST_PLAN.md`.

Limits of the calibration: five clips of one kind (indoor climbing, a mostly steady camera); VMAF's mean (the minimum and
the worst 1 % behave alike, in `calibration.jsonl` of the run); one driver (621.14). The CQ values are conservative for
such footage and must be re-checked when the driver or FFmpeg changes (the harness repeats it: `calibrate`).

## 6. The size estimate against real files

`ExportSizeEstimate` uses the bpp of §5 (x264 medium CRF 18: 0.067 at 4K60; NVENC p5 CQ 22: 0.106), the measured scaling by
the quality (half the bits every 4 CRF / 4.5 CQ), the canvas (libx264 1080p ≈ 0.75× the 4K bpp) and the rate (30 fps: 1.53× /
1.22× per frame), and a range of 0.6–1.6× the median. Measured in the pipeline (median bitrates of §1's runs):

| file | measured Mbit/s | estimate range Mbit/s | inside |
|---|---|---|---|
| 4K60 Balanced libx264 | 32.9 | 20–53 | yes |
| 4K60 → 1080p Balanced libx264 | 9.4 | 3.8–10.1 | yes |
| 4K60 «Быстрый» NVENC (CQ 25) | 34.9 | 22–59 | yes |
| 1080×1920 contain (black bars) libx264 | 3.2 | 3.8–10.1 | **no** — below: the bars cost almost nothing |
| 4K + VHS libx264 | 129.7 | 20–53 | **no** — above: VHS adds noise in every frame |

So the estimate is honest only as a range for ordinary footage; the dialog says «оценка» and, at constant quality, that the
size depends on the picture. An average bitrate («Пользовательский») is the way to get a predictable size (±7 %).
