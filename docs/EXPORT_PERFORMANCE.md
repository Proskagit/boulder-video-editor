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
- **libx264 under heavy CPU contention is not bit-exact** (FFmpeg 9.0.1): the heavy end-to-end suite showed MP4s that differed
  (small encoder decisions, PSNR 40–60 dB between the files) while the canvases were byte-identical. The encoder's stdin was
  captured with a tee: the same bytes in both exports. Then reproduced with ffmpeg alone — the same input and the export's
  exact command line gave another file in 1 of 5 encodings with 14 encodes running beside it, never on an idle machine. Not
  caused by D059 (the sequential export is exposed the same way); the parity test therefore asserts the canvases and that
  no canvas changes while the encoder writes it, and only reports the MP4 hash. The benchmark's byte identity (every run of
  every scenario) holds on a normally loaded machine.
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
  **D074 §6 (2026-11-22, the owner):** «Высокое качество» raised to the highest level — p7 multipass **CQ 19** (the CQ held
  against CRF 14 at p5; p7 multipass only adds quality) and libx264 slow **CRF 14**.
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

## 7. Phase 41 (D060): the resolution and the export in the background

- **Resolution presets.** 4K UHD / Full HD / HD / SD by the short side (2160 / 1080 / 720 / 480) at the canvas's aspect ratio,
  even sides, within the H.264 level 5.1 / 4096 px limits (`ExportResolutions`). The frame is drawn directly at the output size
  by the same rasterizer (the canvas stretched onto the output) — a 4K project exported at Full HD rasterizes and encodes a
  quarter of the pixels, so it runs at about the speed of a 1080p export (the decoding stays at full resolution). The
  canvas's own size is the identity mapping — the export path and its bytes are those of Phase 40.
- **Background export.** Nothing on the export's path changed: the job, the pipeline, K, the encoders. Editing while it runs
  uses the UI thread and the CPU the user's work needs; the export's numbers in §1 were measured with an idle editor.
- **Source files** are held open read-only (sharing reads) for the export's duration — no copies, no cost.
- **ffmpeg processes** are in a kill-on-close job object — no cost; no orphan ffmpeg even if the app is killed.

## 8. Phase 57 (D076): HEVC and AV1 against libx264

Measured 2026-11-22/23 on the target machine (RTX 4070 SUPER, driver 621.24) with the **new minimal FFmpeg 9.0.2** (x265 4.1,
SVT-AV1 4.0.1, NVENC from nv-codec-headers 12.2.72.0; CI run 45066996109) for the encodings and gyan.dev 9.0.1 for VMAF
(`calibrate --vmaf-ffmpeg`). References: lossless canvases of the export (`run --reference`, 300 frames each) — **A** `4k`
(4K60 route), **B** `4k120` (a second 4K route), **C** `hevc-vert` (the vivo HLG clip through the HDR graph on 1080×1920),
**D** `4k-1080` (1080p60), **E** `4k30` (a third route in a 30 fps project). VMAF of every third frame (`--vmaf-subsample 3`; the 4K
model above 1080p). Results in `D:\aive-calibration-57\*.jsonl` (outside the repository).

**The rule of D059 §5:** a quality level's value is the largest CRF / CQ whose VMAF is not below its libx264 counterpart on
every reference — Maximum ↔ slow CRF 14, VeryHigh ↔ slow CRF 16, High ↔ medium CRF 18, Standard ↔ medium CRF 23, Compact ↔
medium CRF 28, «Очень быстро» ↔ veryfast CRF 18. The grid was searched on A and E (the deciding ones in D059) and every chosen
value then checked on all five.

### 8.1 A finding on the way: NVENC HEVC's automatic level

`hevc_nvenc` picks level 5.1 **Main tier** for 4K60, whose 40 Mbit/s buffer caps the stream at ~31 Mbit/s whatever the CQ (CQ 18,
20 and 22 gave the same file, VMAF 97.9). The export now asks for **High tier** with a 160 Mbit/s peak (`-tier high -maxrate 160M
-bufsize 160M`; level 5.1 High tier allows 160 Mbit/s — a conformant stream). Every HEVC NVENC value below is measured with it.

### 8.2 The values (the plan's tables)

| level (counterpart) | NVENC H.264 | NVENC HEVC | NVENC AV1 | libx264 | libx265 | SVT-AV1 |
|---|---|---|---|---|---|---|
| Максимальное (slow CRF 14) | p7 mp **CQ 19**¹ | p7 mp CQ 17 | CQ 20 | CRF 14 | CRF 13 | CRF 10 |
| Очень высокое (slow CRF 16) | p7 mp CQ 20 | p7 mp CQ 19 | CQ 22 | CRF 16 | CRF 14 | CRF 11 |
| Высокое (medium CRF 18) | p5 CQ 22 | p5 CQ 22 | CQ 28 | CRF 18 | CRF 15 | CRF 12 |
| Стандартное (medium CRF 23) | CQ 27 | CQ 27 | CQ 32 | CRF 23 | CRF 20 | CRF 30 |
| Компактное (medium CRF 28) | CQ 32 | CQ 33 | CQ 42 | CRF 28 | CRF 26 | CRF 40 |
| «Очень быстро» (veryfast CRF 18) | p2 +3 (CQ 25) | p2 +3 (CQ 25) | p2 +4 (CQ 32) | veryfast | veryfast +3 (CRF 18) | preset 11 +10 (CRF 22) |

The speeds: NVENC p2 / p3 / p5 / p7 (p7 with multipass); x264 / x265 by name; SVT-AV1 presets 11 / 9 / 7 / 5.
¹ D074 §6's value (the owner's), calibrated then against medium CRF 14; against slow CRF 14 it is 0.02 VMAF below on D only (above
on the other four) — kept. Three first choices fell 0.03–0.13 below on B and were lowered one step (x265 slow 14 → 13, medium 16 →
15; SVT-AV1 14 → 12; the «Очень быстро» offsets are counted from the lowered values). The software AV1 tests are skipped on
the dev FFmpeg (gyan.dev 9.0.1 has no SVT-AV1) and run on the minimal build.

### 8.3 What the codecs give — Mbit/s on A (4K60) at each level's value, and the encoder's own speed

| level | x264 | NVENC H.264 | NVENC HEVC | NVENC AV1 | libx265 | SVT-AV1 |
|---|---|---|---|---|---|---|
| Максимальное | 54.0 | **71.8** | 94.8 | 91.6 | 70.3 | 78.3 |
| Очень высокое | 42.1 | **62.2** | 69.7 | 71.5 | 60.3 | — |
| Высокое | 32.2 | 48.9 | 44.4 | **34.5** | 44.4 | 43.7 |
| Стандартное | 13.2 | 21.6 | 18.2 | 19.8 | 17.9 | 12.8 |
| Компактное | 4.8 | 9.6 | 7.0 | 4.7 | 5.5 | 6.3 |
| Очень быстро | 21.5 | 34.4 | 27.6 | 19.5 | 21.3 | 20.8 |
| encoder alone, fps (4K60, medium / p5) | 41 | 60 | 85 | 90 | 17 | 31 |

- **At the highest qualities HEVC and AV1 make no smaller file** than H.264 NVENC by VMAF (VMAF is near 99 there and every codec
  needs many bits to hold it); libx264 stays the smallest. **At «Высокое» AV1 NVENC is ~30 % smaller than H.264 NVENC** (34.5
  against 48.9) and HEVC NVENC ~10 %. Lower, every codec is small; SVT-AV1 and libx264 the smallest.
- **libx265 makes HEVC no smaller than libx264** at high quality (and 2–3× slower); SVT-AV1 at preset 7 about as large as libx264
  at «Высокое».
- **The export's speed** (the real export, 4K60, 600 frames, 2 runs, p7 multipass): H.264 44.5–46.5 fps, AV1 46.1–46.7, HEVC 41.6 —
  the pipeline, not the encoder, limits (§3); AV1 is as fast as H.264, HEVC at p7 ~10 % slower.
- **The owner's decision (D076 §3, after these numbers):** «Максимальный», «Высокий», «Быстрый» — H.264 NVENC; «Средний» — AV1 NVENC;
  HEVC and AV1 for any level by hand; the «?» in «Подробные настройки» sums this table up.

### 8.4 The size estimate

`ExportSizeEstimate` reads, per codec and encoder, the bits per pixel of A at a reference value (libx265 medium CRF 16 0.075, slow
0.087, veryfast 0.061; NVENC HEVC p5 CQ 22 0.089; SVT-AV1 preset 7 CRF 14 0.074, preset 5 0.096, preset 11 0.073; NVENC AV1 p5 CQ 28
0.069, p7 0.063, p2 0.068) and their halving (x265 / NVENC HEVC every 4 steps, SVT-AV1 every 10, NVENC AV1 every 4.5); the size and
the rate scale as for H.264 (§6). The range stays 0.6–1.6× the median — «оценка».

Limits: the same as §5 (climbing footage of one gym, a steady camera; one driver); the HEVC / AV1 values must be re-checked when the
driver or FFmpeg changes (`calibrate`, the settings `x265:` / `svtav1:` / `hevcnvenc:` / `av1nvenc:`).

## 9. FU-EX.2 research (2026-11-23): the export on the graphics card

The owner's question after Phase 57: how hard is it to move the export to the graphics card, and does it give 2–3×? Measured
2026-11-23 on the target machine (RTX 4070 SUPER, driver 621.24), the minimal FFmpeg 9.0.2 of 1.10.0 (the app's) and gyan.dev
9.0.1 (CUDA filters, NVDEC — the ceilings only). Source `12x10\1014(2).mp4` / `1014.mp4` (4K60 H.264), 600 frames; the app's
numbers by `tools/export-bench` (2 runs, median), scenario `4k`. No code of the app changed.

### 9.1 The ceilings — what a pipeline entirely on the GPU could reach

`ffmpeg -hwaccel cuda -hwaccel_output_format cuda` → NVENC with the export's exact options (`-tune hq`, spatial AQ, look-ahead 20,
3 B-frames, `-multipass qres`): the frames never leave the card, nothing is drawn — the upper bound of any GPU export.

| preset (the export's encoder) | the app today, fps | the ceiling on the GPU, fps | at most |
|---|---|---|---|
| «Максимальный» 4K, H.264 p7 multipass CQ 19 | 45.4 | 78.5 | ×1.7 |
| «Высокий» 4K, H.264 p7 multipass CQ 20 | ~45 | 78.5 | ×1.7 |
| «Высокое» by hand 4K, H.264 p5 CQ 22 | ~45 | 65.4 | ×1.45 |
| 4K, AV1 p7 multipass | 46 (§8.3) | 74.8 | ×1.6 |
| 4K, HEVC p7 multipass, High tier | 41.6 (§8.3) | 41.1 | ×1.0 — NVENC itself |
| 4K, H.264 p2 («Очень быстро») | 46.6 | 166.6 | ×3.6 |
| «Средний» 2K, AV1 p5 CQ 28 | 61.1 | 204 | ×3.3 |
| «Быстрый» Full HD, H.264 p2 CQ 25 | 69.4 | 229 | ×3.3 |

- **At the 4K quality presets the encoder is the wall:** NVENC encodes 4K at p7 / p5 at 65–78 fps however the frames arrive. Two or
  three NVENC sessions at once give 61 / 63 / 65 fps in total (the card's encoders are already in use); `-split_encode_mode forced`
  changes nothing (H.264 has none; HEVC / AV1 the same speed). So no GPU pipeline makes «Максимальный» / «Высокий» 2–3× faster:
  **×1.7 at most**.
- 2–3× is possible only where NVENC is fast: the downscaled presets («Средний», «Быстрый») and p1–p4 at 4K.

### 9.2 Where the 4K export loses its time today

- **The pipeline is capped at ~46 fps at 4K whatever the encoder:** p2 (NVENC ceiling 167) 46.6, p7 45.4; 2, 4, 8 rasterizers 43.7 /
  47.5 / 46.5.
- **A shared resource, not one slow stage:** two 4K exports at once make 23.2 + 23.2 = 46.4 fps in total (the system's CPU 14.9 of 16).
- **CPU by process** (4K, «Максимальный», 46 fps): the app 3.8 cores (the frame into the bitmap, Skia, the read-back, the pipes), the
  decoder's ffmpeg 4.6 (H.264 decoding, YUV → BGRA, the pipe), the encoder's ffmpeg 2.0 (the pipe, BGRA → YUV, NVENC).
- **Decoding on the GPU (`-hwaccel auto`, D3D11VA) in the export:** the decoder 4.6 → 3.0 cores, the speed unchanged — 45.7 / 63.5 /
  71.3 fps («Максимальный» / «Средний» / «Быстрый», +0.7–3 %). And its 4K BGRA frames are **not the bytes of the software decoder**
  (the MP4's SHA-256 changed), so the export would no longer be deterministic. Not worth it.
- **The encoder's colour conversion on the GPU** (NVENC fed BGRA, converting itself) in one ffmpeg: 75.0 fps against 75.6 with the CPU
  conversion — no gain (the CPU conversion is multithreaded and cheap); CUDA `scale_cuda` cannot convert BGRA at all.
- **The app's decoder alone** (the export's request, high-throughput pipe, frames dropped): 103 fps software, 108 hardware — 2.2× the
  export's 46 fps; ffmpeg alone decodes the same file to BGRA at 157 fps.
- So the limit is **the memory traffic of the 4K BGRA frames**: every 33 MB frame passes the memory about fifteen times (decode,
  YUV → BGRA, the pipe both ways, the read into an array, the bitmap copy, the draw, the read-back, the encoder's pipe both ways, BGRA →
  YUV) — fewer cores busy (hardware decoding) does not help, fewer passes would. `DOTNET_GCRetainVM=1` (memory kept instead of handed
  back and re-committed for every 33 MB frame): 45.4 → 47.2 fps (+4 %), the same bytes.

### 9.3 A finding: scaling the sources in the decoder (no GPU)

A 4K source in a Full HD / 2K export is decoded, carried and copied at 4K and only the painter stretches it onto the smaller canvas
(§7). Decoded at the size it is drawn at (the bench's `AIVE_BENCH_DECODE_MAX`, the decoder's own `scale`):

| preset | today | decoded at the output size | × |
|---|---|---|---|
| «Быстрый» (Full HD, H.264 p2) | 69.4 | 151.7 (159.3 with hardware decoding) | **×2.2** |
| «Средний» (2K, AV1 p5) | 61.1 | 98.8 | **×1.6** |

The picture (lossless canvases, 180 frames, Full HD): PSNR 50.3 dB / VMAF 99.5 against today's canvases — the same to the eye — and
slightly closer to a Lanczos downscale of the source (46.9 dB against today's 46.2). The canvases are not byte-identical to today's:
these presets' files change. Not measured: crops, zooms, Motion Tracking Camera, picture in picture and the blurred background (the
size a clip is decoded at must follow its largest drawn size over the whole clip, and `FrameSourceRect` its source pixels).

### 9.4 What the GPU export would take

The whole frame on the card: NVDEC, the composition, NVENC without a read-back. The composition is the Preview's painter
(`CompositionPainter`, `TemplatePainter`, `SummaryPainter`, the text glyph cache, the treatment surface — about 3 300 lines on Avalonia /
Skia, drawing frames with colour correction and transition effects, the HDR LUT graph, the blurred background, texts with outlines and
shadows, templates (VHS, «Боулдер», «Итоги»…), hold outlines, the heart rate, people overlays): it would need **a second renderer**
on the GPU (D3D11 / CUDA, with the decoder's and the encoder's surfaces shared in-process — libav in the app instead of ffmpeg
processes, or a minimal FFmpeg rebuilt with CUDA filters), and Preview = export, built today by construction (one painter), would have
to be measured instead. A large architectural change (D059 §8 rejected it for the same reasons) for ×1.7 at most on the 4K quality
presets.

### 9.5 The conclusion

- **The «pure GPU» export is not a calm change and does not give 2–3× where it matters:** at 4K with the quality presets NVENC itself
  stops at 65–78 fps (×1.45–1.7 at most), and the cost is a second renderer.
- **A hybrid on the GPU (hardware decoding, the conversion on the card) gives 0–4 %** — the CPU cores were not the limit.
- **Without the GPU (ROADMAP FU-EX.2):** (c) decoding at the drawn size — ×2.2 «Быстрый», ×1.6 «Средний» (and every export below the source's size);
  (a) fewer passes of the 4K frames (D059 E6: a frame buffer pool / `GCRetainVM` +4 %, the decoded frame straight into the bitmap, the
  canvas straight into the encoder's buffer) — estimated +10–25 % at 4K. Each a phase of its own, on the owner's word.

## 10. Phase 58 (D077): decoding at the drawn size, the block copy

Measured 2026-11-23 on the target machine, the minimal FFmpeg 9.0.2 of 1.10.0, `tools/export-bench`, 600 frames, median of 2
(4K «Максимальный» of 3). «Before» = the same build with the decoder forced to full size (`AIVE_BENCH_DECODE_MAX=16384x16384`)
or 1.10.0's numbers of §9; NVENC everywhere.

| scenario | encoding | before, fps | after, fps | × | MP4 |
|---|---|---|---|---|---|
| `4k` | «Быстрый» (Full HD, H.264 p2) | 69.4 | 150.1 | **2.16** | changed |
| `4k` | «Средний» (2K, AV1 p5) | 61.1 | 96.8 | **1.58** | changed |
| `4k-1080` | «Максимальный» (H.264 p7) | 66.4 | 148.7 | **2.24** | changed |
| `vert` (4K → 1080 × 1920) | «Максимальный» | 65.7 | 188.3 | **2.87** | changed |
| `vert-blur` | «Максимальный» | 65.5 | 160.5 | **2.45** | changed |
| `hevc-vert` (HDR HLG → 1080 × 1920) | «Максимальный» | 34.4 | 76.1 | **2.21** | changed |
| `tracks2` (two 4K layers, picture in picture) | «Максимальный» | 30.0 | 39.6 | 1.32 | changed |
| `4k` | «Максимальный» (4K, H.264 p7 multipass) | 45.4 | 48.6 | 1.07 | **identical** |

The peak working set fell with the frames (vertical 1.8 → 0.6 GB). The picture: VMAF 99.5 / PSNR 50.3 dB against the full-size
canvases at Full HD; against a Lanczos downscale of the source the new canvases are closer (Full HD 46.9 against 46.2 dB; vertical
41.0 against 37.1 — the painter's 3.5× shrink of a 4K frame aliased the wall's texture, which also cost bits: 11.2 → 7.0 Mbit/s at
the same CQ). 4K exports of 4K footage keep their bytes; the 4K limit stays the memory traffic of §9.2.
