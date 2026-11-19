# AI hold model (D034)

The AI hold markup (Phase 19) uses one pinned local model. The application never downloads, writes, replaces or repairs
it; without it only the hold markup is unavailable — everything else works.

## The model

| | |
|---|---|
| Id | `sam2.1-large-v1` |
| Network | SAM 2.1 Hiera Large (Meta), exported to ONNX by samexporter 0.5.0, opset 18 |
| Folder | `%LOCALAPPDATA%\BoulderVideoEditor\models\sam2.1-large\` (one copy per machine; never in the repository or a project) |
| `sam2.1_large.encoder.onnx` | 852 451 097 bytes, SHA-256 `5ca0f505138811202a60445427383559fb84afcd80e7a5e2d1ce5d9f546e3362` |
| `sam2.1_large.decoder.onnx` | 16 519 627 bytes, SHA-256 `d2ef7f723ed226f424d590cd382bfef135fdfd4cb74f3a76f6bd563a63b28f10` |

## Installing

0. **Release 1.3 (D070):** the Setup installer installs every model below into `%LOCALAPPDATA%\BoulderVideoEditor\models`
   (a correct file is never rewritten, a damaged one is repaired by running the installer again); the Portable archive carries
   them in `ai-models\` and the application offers to install them at its start or from «Настройки…» → «ИИ-модели» — each
   file copied into a temporary file and moved into place only with its pinned size and SHA-256; nothing is downloaded
   (`docs/DISTRIBUTION.md`). The uninstaller removes them only when its «Также удалить ИИ-модели» box is ticked.
1. **Release 1.0 (D066 §3):** unpack the models archive `AiVideoEditor-1.0.0-models.zip` (1.1: `BoulderVideoEditor-<version>-models.zip`) and run `Install-Models.cmd` — it
   installs every model below (also the people and audio models) with their sizes and SHA-256s checked
   (`tools/models`). Otherwise produce the two files with the recipe below (or copy them from a machine that has them).
2. Put both into `%LOCALAPPDATA%\BoulderVideoEditor\models\sam2.1-large\` with exactly these names.
3. The size and the SHA-256 of both files are checked at the first "Mark holds" of a session (with progress); a file of
   any other size or checksum is never loaded.

Messages (the status bar; the rest of the editor stays usable):
- *The hold model sam2.1-large-v1 is not installed: `<file>` is missing in `<folder>`. See docs/AI_MODEL.md.*
- *The hold model file `<file>` in `<folder>` is damaged or another version (expected sam2.1-large-v1, SHA-256 …). It was
  not loaded. See docs/AI_MODEL.md.*
- *AI runs on the CPU — processing may be slower.* — DirectML was not available or failed (D034 §3); the CPU is used for
  the rest of the session. Timings: "Performance" below.

## Runtime

- ONNX Runtime 1.24.4 with the DirectML execution provider (`Microsoft.ML.OnnxRuntime.DirectML`, MIT); the CPU provider
  of the same package is the fallback. Nothing leaves the machine.
- `DirectML.dll` is **not shipped** with the application: ONNX Runtime loads the copy that is part of Windows. The
  redistributable `DirectML.dll` (NuGet `Microsoft.AI.DirectML` 1.15.4, a dependency of the package above, only restored,
  never copied to the output) is under the *Microsoft Software License Terms — DirectX Machine Learning*: use and
  redistribution only in applications on Windows / Xbox, Microsoft may collect usage data (§2 of the terms), no reverse
  engineering. Shipping it next to the application would pin a newer DirectML than the system's, but it is a licence
  decision for the product owner, not taken in Phase 19. With the system copy the C# masks equal the Python reference
  (Step 19.4: median IoU 1.0000) and DirectML = CPU on the E1 set.
- Pre- and postprocessing as SAM 2 (`src/Vision/SamImageProcessing.cs`): an antialiased bilinear resize of the whole
  frame to 1024 × 1024 (PIL / torchvision weights), ImageNet normalization in RGB; the 256 × 256 logits upsampled
  bilinearly (align_corners = false) to the frame, threshold > 0.
- **Multi-point prompts.** The pinned decoder was exported with `multimask_output = True`, so it always returns three
  masks, also for several points. A candidate refined by Shift+click (D034 §1: one mask, no alternatives) shows the
  highest-scoring visible mask of the three (the same candidate rule as for one point: up to 2 % of the frame always,
  2–20 % only with score ≥ 0.9, larger hidden — D034 §1 as amended 2026-10-15). Verified locally:
  the refined candidate contains both clicks.

## Performance

D034 §7 asks for "frame preparation ≤ 1 s GPU / ≤ 10 s CPU, click preview ≤ 150 ms". Its reading (D034 implementation
notes, 2026-10-15):

| Measure | Definition | Criterion |
|---|---|---|
| Frame preparation | From a decoded frame in memory to the model being ready for the first click: preprocessing (resize to 1024², normalization) and the image encoder; sessions already loaded | GPU median ≤ 1 s; CPU ≤ 10 s |
| Click preview | The decoder for one prompt and the upsampling of its three masks to the frame | ≤ 150 ms |
| Frame decode | The app decoding the markup frame at full display size (separate, not in the frame preparation) | reported |
| Cold start | The SHA-256 check of both files and the creation of the two sessions — once per session (separate) | reported; shown by the progress ("Checking the hold model…", then "Loading the hold model…") |
| Full user scenario | From Mark holds to clickable: decode + frame preparation, plus the cold start on the first use in a session | reported |

Conditions: Windows 11, RTX 4070 SUPER (DirectML), 16-core CPU; the 7 E1 4K SDR frames (3840 × 2160); the local suite
with one test class at a time (classes sharing the GPU distort each other's timings); release build.

Values (2026-10-15; reports `<E1>/results/20261015-110340/report.md` and `<E1>/results/perf-20261015-110442/perf.md`;
ms unless stated):

| Measure | n | median | p95 | max | Criterion |
|---|---|---|---|---|---|
| Frame preparation, GPU, over the E1 frames (service, each frame once) | all | 350 | — | 472 | ≤ 1000 median — met |
| Frame preparation, GPU, the first after the sessions are created | 3 | 428 | 466 | 466 | — |
| of which: preprocessing / encoder (warm) | 21 | 41 / 192 | 54 / 198 | 58 / 198 | — |
| Frame preparation, CPU | 1 | 7400 | — | — | ≤ 10 s — met |
| Click preview, GPU, through the service (all E1 clicks) | 188 | 41 | — | 122 | ≤ 150 — met (also the maximum) |
| Click preview, GPU, decoder + upsampling (SDR clicks, profile) | 164 | 31 | 46 | 112 | — |
| Frame decode, 4K, app decoder | 8 | 699 | 868 | 868 | separate |
| Cold start: SHA-256 check / session creation | 3 | 455 / 3248 | 470 / 3514 | 470 / 3514 | separate |
| Cold start total (a fresh service's EnsureReady) | 3 | 3956 | 3967 | 3967 | separate |

Full user scenario (from these stages): the first Mark holds of a session ≈ 4.0 s cold start + 0.7 s decode + 0.43 s
preparation ≈ 5.1 s; later ones ≈ 0.7 s decode + 0.24–0.35 s preparation ≈ 0.95–1.05 s. Observed in the real app
(Debug build, 2026-10-15): the first Mark holds in a session 5.5 / 5.8 / 5.8 / 6.0 s (four sessions, median 5.8 s,
including the UI), a later one 1.1 s (one sample). Profile: `tests/Vision.ModelTests/PerformanceProfileTests.cs`.

## Licences (checked 2026-10-14)

| Component | Source | Licence | Notice | Conclusion |
|---|---|---|---|---|
| SAM 2 code at `2b90b9f5ceec907a1c18123530e92e794ad901a4` | github.com/facebookresearch/sam2 (API listing of the commit's tree; `sam_2-1.0.dist-info` of the pinned install) | Apache-2.0 (`LICENSE`) | **No `NOTICE` file** at this commit (top level: `LICENSE`, `LICENSE_cctorch` only) | Used for the export only; not shipped |
| `cc_torch` (in SAM 2) | `LICENSE_cctorch` at the same commit | BSD-3-Clause | — | A CUDA extension, not built (`SAM2_BUILD_CUDA=0`) and not part of the ONNX |
| SAM 2.1 checkpoint `sam2.1_hiera_large.pt` | `https://dl.fbaipublicfiles.com/segment_anything_2/092824/sam2.1_hiera_large.pt`; README of the commit: "The SAM 2 model checkpoints … are licensed under Apache 2.0" | Apache-2.0 | — | The source of the ONNX weights |
| samexporter 0.5.0 | PyPI; `samexporter-0.5.0.dist-info/licenses` | MIT (© 2023 Viet Anh Nguyen); third-party: `OPENAI_CLIP_LICENSE` (MIT, © 2021 OpenAI) | — | Used for the export only |
| onnx 1.23.2 / onnxscript 0.7.2 | PyPI metadata | Apache-2.0 / MIT | — | Export only |
| PyTorch 2.14.1 / torchvision 0.29.1 | download.pytorch.org (cu126) | BSD-3-Clause | — | Export only |
| ONNX Runtime 1.24.4 (`Microsoft.ML.OnnxRuntime.DirectML`) | NuGet; `LICENSE`, `ThirdPartyNotices.txt` in the package | MIT | `ThirdPartyNotices.txt` | Shipped (`onnxruntime.dll`); its notices travel with any distribution |
| DirectML redistributable 1.15.4 (`Microsoft.AI.DirectML`) | NuGet; `LICENSE.txt` | Microsoft Software License Terms | `ThirdPartyNotices.txt` | **Not shipped** (see Runtime) |

**Distributed since release 1.0** (D066 §3) in the separate models archive, with the Apache-2.0 text, the attribution
(SAM 2 has no NOTICE file at the pinned commit) and the statement that the files are an ONNX conversion of the SAM 2.1
Hiera Large checkpoint made with samexporter 0.5.0 (`tools/models/THIRD-PARTY-NOTICES.txt`, `tools/models/licenses`).

## Export recipe (gives the SHA-256s above)

Windows 11 x64, Python 3.12.10, in an empty folder:

```
py -3.12 -m venv venv
venv\Scripts\python -m pip install torch==2.14.1 torchvision==0.29.1 --index-url https://download.pytorch.org/whl/cu126
set SAM2_BUILD_CUDA=0
venv\Scripts\python -m pip install git+https://github.com/facebookresearch/sam2.git@2b90b9f5ceec907a1c18123530e92e794ad901a4
venv\Scripts\python -m pip install --no-deps samexporter==0.5.0
venv\Scripts\python -m pip install onnx==1.23.2 onnxscript==0.7.2 onnx-ir==1.0.0 hydra-core==1.3.7 iopath==0.1.10 numpy==2.5.2
mkdir ckpt onnx
curl -L -o ckpt\sam2.1_hiera_large.pt https://dl.fbaipublicfiles.com/segment_anything_2/092824/sam2.1_hiera_large.pt
```

The checkpoint: 898 083 611 bytes, SHA-256 `2647878d5dfa5098f2f8649825738a9345572bae2d4350a2468587ece47dd318`. Then:

```
venv\Scripts\python -m samexporter.export_sam2 --checkpoint ckpt\sam2.1_hiera_large.pt --output_encoder onnx\sam2.1_large.encoder.onnx --output_decoder onnx\sam2.1_large.decoder.onnx --model_type sam2.1_hiera_large
certutil -hashfile onnx\sam2.1_large.encoder.onnx SHA256
certutil -hashfile onnx\sam2.1_large.decoder.onnx SHA256
```

The export was byte-identical on repetition in this environment. Other versions of any package may give other files,
which the application refuses (by design: the masks were graded for exactly these files). The full `pip freeze` of the
reference environment is kept with the E1 reference data (`env/requirements-lock.txt`, outside the repository).

## Testing

- Regular CI (`dotnet test AiVideoEditor.sln`) needs neither the weights nor a GPU (`tests/Vision.Tests` uses a test
  double of the session).
- The local suite with the real weights, outside the solution:
  `dotnet test tests/Vision.ModelTests/Vision.ModelTests.csproj -c Release` — environment `AIVE_AI_MODEL_DIR` (default:
  the folder above) and `AIVE_AI_E1_DIR` (default `%USERPROFILE%\Desktop\AiVideoEditor-E1-Reference`); ffmpeg on PATH. It
  writes its report to `<E1>\results\<timestamp>\`.

---

# People models (D061, Phase 42: Follow and Occlusion)

A second pinned model set, independent of the hold model. Without it only Follow and the outlines hidden behind people are
unavailable (the Inspector and the export say so); everything else works. The application never downloads, writes,
replaces or repairs it.

## The models

| | |
|---|---|
| Id | `people-v2` (part of every cached people analysis's key; `people-v1` until S1 — only the detector's file differed) |
| Folder | `%LOCALAPPDATA%\BoulderVideoEditor\models\people-v2\` (one copy per machine; never in the repository or a project) |
| Verified | size and SHA-256 of every file, once per session, before the first use (`HoldModelVerifier`) |

| File | Network | Bytes | SHA-256 |
|---|---|---|---|
| `rfdetr_m_576.onnx` | RF-DETR-M (Roboflow), COCO, 576 × 576 — the person detector; exported from the official checkpoint `rf-detr-medium.pth` with `rfdetr` 1.12.1 (`tools/people-models/export_rfdetr.py`, S1) | 133 424 287 | `ec23de6746c6737b47c34fd24f06583e80a2253924efc32c27b716157ae1632a` |
| `rtmpose_m_body7_256x192.onnx` | RTMPose-m body7 (OpenMMLab) — 17 COCO keypoints, the torso centre (`rtmpose-m_simcc-body7_pt-body7_420e-256x192-e48f03d0_20230504.onnx`) | 54 330 655 | `5c0a4bf67953e6d2ac43ce15e77dc9d5d354ae18430a47d2c5963a7bc5683e3c` |
| `sam2.1_tiny_video.encoder.onnx` | SAM 2.1 Hiera tiny — the image encoder (+ the high-resolution projections) | 109 408 012 | `8bd43ed78fb7c56eebec730592003a8b795604a61c22f9a68229c3da770016cf` |
| `sam2.1_tiny_video.memory_attention.onnx` | the memory attention, RoPE without complex numbers, **float16** inside (float32 inputs / outputs) | 16 238 215 | `5b3e99ecba4374f0b7e3adfafa956909a874673a14852ae2853c4c571f40b7df` |
| `sam2.1_tiny_video.decoder_multi.onnx` | prompt encoder + mask decoder, 3 masks (tracking frames) | 17 884 116 | `136f739f10fc44b79bb09bdb6a207577625711a4186175d2b6bf978bfbb31cc1` |
| `sam2.1_tiny_video.decoder_single.onnx` | the same, 1 mask with the stability fallback (a box prompt) | 17 893 385 | `a63a960ae5a23c9509605f2425727065d20caad2ac2677ef87e4cf3689eaa4f1` |
| `sam2.1_tiny_video.memory_encoder.onnx` | a frame's mask → its memory | 5 591 772 | `0be6bdbea360072de1fa6a3b042f5415fbe2bff4618670f5846e82c852c0236c` |
| `sam2.1_tiny_video.pointer_pos.onnx` | the object pointers' temporal encoding | 67 131 | `b8db9c1ddc26c967caa7cf2d49b1528d2e0ff55dfef0e689d99c9a2caf06c1da` |
| `sam2.1_tiny_video.constants.bin` | `no_mem_embed` [256], `maskmem_tpos_enc` [7 × 64] (float32 LE, `SAM2CST1`) | 2 832 | `3ffeae7ff4579e5913f8fbf4de1a6ae0e1ccc7a09157052d7d14da19736ab61b` |

Total ≈ 348 MB. Messages (the Inspector, the export's warnings):
- *The people models people-v2 are not installed in `<folder>`: Follow and the outlines hidden behind people are
  unavailable. See docs/AI_MODEL.md.*
- *A file of the people models people-v2 in `<folder>` is damaged or another version. It was not loaded.*
- *Hiding the outlines behind people needs a DirectML graphics card; on this computer only Follow is available.*

## Runtime

- The same ONNX Runtime 1.24.4 with DirectML as the hold model (the system's `DirectML.dll`, see above).
- **Detector and pose**: DirectML; on a DirectML failure (session or a known device error in a run) the CPU for the rest of
  the session — Follow keeps working, slower (RF-DETR-M ≈ 150 ms per frame on the CPU, research B.6).
- **The video segmentation needs DirectML**: on the CPU the encoder alone takes ≈ 0.8 s per frame (research C.2); without a
  usable GPU Occlusion is refused with the message above, the outlines are drawn as before.
- Pre- and postprocessing: the detector's input is the frame stretched to 576² (bilinear), RGB / 255 ImageNet-normalized
  (rtmlib's reference feeds BGR — on the checked frames RGB scores the climber at least as well, and RGB is what the network
  was trained on); class 1 through a sigmoid, boxes already normalized. The pose crop: the box × 1.25 at 192 : 256, RGB
  normalized by the mmpose mean / deviation, SimCC by arg-max (split ratio 2); the torso centre = the mean of the confident
  shoulders and hips (≥ 0.3, at least two, at least one shoulder), else the box centre. SAM: the frame stretched to 1024² by
  the antialiased bilinear of `SamImageProcessing` (the hold model's), the logits upsampled bilinearly (align corners false)
  to the mask grid.
- The streaming loop of the video mode is C# (`Sam2VideoSegmenter`): per person the prompted frames (at most 3) and the last
  six frames as spatial memories with their temporal encodings, the object pointers of up to 16 frames (signed distance /
  15); a person starts with its detector box (single mask, binarized for the memory), then without prompts (3 masks, the best
  by IoU). The memory is bounded (feature maps for 6 frames, pointers for 16): ≈ 15 MB per person.

## Validation (2026-11-16, RTX 4070 SUPER, driver 621.24)

| Check | Result |
|---|---|
| The ONNX graphs + the streaming loop (Python, ORT DirectML) against the official `SAM2VideoPredictor` (PyTorch CUDA, float32), `VID_20251223_195009.mp4` 0–10 s at 30 Hz (301 frames), the climber prompted by its RF-DETR box | mean IoU of the masks **0.995** (min 0.976, p5 0.989), pixel agreement 99.97 %, 0 empty frames on either side (`tools/people-models/validate_sam2_video.py`) |
| The same with the float16 memory attention | mean IoU 0.995 (min 0.976) — unchanged |
| The C# port end to end (the app's decoder, `SamImageProcessing`, the C# loop) against the PyTorch predictor on the same segment (300 frames, 360 × 640 grid) | mean IoU **0.989**, median 0.994, p5 0.929, min 0.883 (the two decoders scale differently; `tools/people-models/compare_sam.py`) |
| Speed of one person, C# loop | 8.3 frames / s (encoder 43 ms, memory attention 33–56 ms, decoder 6.5 ms, memory encoder 2 ms) |

## Licences (checked 2026-11-16)

| Component | Licence | Conclusion |
|---|---|---|
| RF-DETR (Roboflow) — code and the N–L weights | Apache-2.0 | the detector; since S1 (`people-v2`) exported from the official checkpoint with `rfdetr` 1.12.1 (until then the conversion distributed with rtmlib, Apache-2.0) |
| RTMPose (OpenMMLab mmpose) — code and weights | Apache-2.0 | the pose |
| SAM 2.1 (Meta) — `sam2` 1.1.0 and the checkpoint `sam2.1_hiera_tiny.pt` | Apache-2.0 | the source of the six graphs |
| PyTorch 2.11 / onnx 1.19.1 | BSD-3-Clause / Apache-2.0 | export only |
| ONNX Runtime 1.24.4 | MIT | shipped (as for the hold model) |

Not used (licence): Ultralytics YOLO (AGPL-3.0), RVM (GPL-3.0), the YOLOX HumanArt weights (dataset terms unverified) —
research B.3. **Distributed since release 1.0** (D066 §3) in the models archive with the Apache-2.0 texts, the copyright
notices and the statement of the conversions (`tools/models/THIRD-PARTY-NOTICES.txt`).

**RF-DETR-M, confirmed 2026-11-16 from the primary sources:** the repository's `LICENSE` (github.com/roboflow/rf-detr, branch
`develop`) is the plain Apache License 2.0 ("Copyright 2025 Roboflow, Inc.", no NOTICE file); its README lists «RF-DETR Nano,
Small, Medium and Large detection» under Apache 2.0 (the table row «RF-DETR-M … 576x576 | Apache 2.0») and says they may be shipped
in closed-source commercial products, self-hosted included; roboflow.com/model-licenses/rf-detr gives the same. The Platform
Model License 1.0 (usage tracking, no extraction of the base weights, an enterprise licence for commercial self-hosting) covers
only Atto, Femto, Pico, XL, 2XL and the NAS models — not Medium. **So RF-DETR-M may be distributed with the application**, with
the Apache-2.0 text and the Roboflow copyright notice in the third-party notices. **Caveat:** the pinned ONNX is a third-party
conversion (rtmlib's download from the Hugging Face repository `saifkhichi96/opendetect`, declared Apache-2.0, an empty model
card): its descent from Roboflow's official Medium checkpoint is consistent with what the file is (COCO, 91 classes with the
background, 300 queries, 576 × 576 — the README's RF-DETR-M) but not documented. Before a distribution (S1), re-export it from
the official `rfdetr` package (`model.export()`), check the people boxes against this file on the benchmark clips, and pin the
new file — a step for S1, not needed for development. **Done in S1 (2026-11-17, D066 §3):** `tools/people-models/export_rfdetr.py`
with `rfdetr` 1.12.1 and `torch` 2.15.0 (CPU) exported the official checkpoint `rf-detr-medium.pth` (404 992 918 bytes,
SHA-256 `749ff6071828aaffac63e204c4f4135ed3d6cdae4d702e086c360edc3b5768c8`, its MD5 checked by the package) — the same
interface (`input` [1, 3, 576, 576], `dets` [1, 300, 4], `labels` [1, 300, 91]); `tools/people-models/compare_detector.py` on 180
frames of three real clips (2026-09-27, 1 fps): the same 291 confident people as the old conversion, every box matched (IoU
1.000) — so the old file was the official model too. The new file is pinned as `people-v2`; `tools/people-bench` on the
reference part of `video_20260927_101034` (9.6–25.2 s): 156 frames, one track, on the GPU.

## Export recipe (gives the SHA-256s above)

The research environment (Windows 11, Python 3.12): `torch==2.11.0+cu128`, `sam2==1.1.0`, `hydra-core==1.3.8`,
`numpy==2.5.4`, `onnx==1.19.1`, `onnxruntime-directml==1.24.4`, `rtmlib==0.0.16`. The checkpoint `sam2.1_hiera_tiny.pt`:
156 008 466 bytes, SHA-256 `7402e0d864fa82708a20fbd15bc84245c2f26dff0eb43a4b5b93452deb34be69` (Meta's
`dl.fbaipublicfiles.com/segment_anything_2/092824/`). Then:

```
python -I tools/people-models/export_sam2_video.py <sam2.1_hiera_tiny.pt> <out folder>
```

writes the six graphs and the constants (a repeated export was byte-identical here). The detector and the pose are rtmlib's
files (its download cache `%USERPROFILE%\.cache\rtmlib\hub\checkpoints\`), copied under the names above.

## Testing

- CI (`dotnet test AiVideoEditor.sln`) needs neither the models nor a GPU: the analysis is tested with fakes of the decoder
  and the models (`PeopleAnalyzerTests`, `PeopleAnalysisCoordinatorTests`), the composition and the painter with synthetic
  tracks and masks.
- On the target machine with the models: `tools/people-bench` (the product's analyzer on real clips, timings, Follow's
  metrics), `tools/people-models/compare_sam.py` (the C# segmenter against PyTorch) and `people_sheet.py` (contact sheets).

# Audio models (D064, Phase 45: voice enhancement and ducking)

The audio clean-up's voice enhancement and the ducking's speech detection use two pinned local models "audio-v1". The
application never downloads, writes, replaces or repairs them; the noise reduction needs no model (FFmpeg `afftdn`).

| | |
|---|---|
| Folder | `%LOCALAPPDATA%\BoulderVideoEditor\models\audio-v1\` (one copy per machine; never in the repository or a project) |
| `denoiser_model.onnx` | DeepFilterNet3, the torchDF streaming single-graph export (github.com/grazder/DeepFilterNet), from `https://huggingface.co/kimtos-labs/denoiser-dfn3/resolve/main/denoiser_model.onnx` (commit `ffc593dc7a6443fe0f2a8fc93b90f32c4afc9e0e`) — 16 104 687 bytes, SHA-256 `fe5eb64fa2e4154c83f8e4935e82871c850c154387ee892e0ab65fe179e7d8c9` (equal to the digest the model card publishes) |
| `silero_vad.onnx` | Silero VAD v5, from `https://raw.githubusercontent.com/snakers4/silero-vad/master/src/silero_vad/data/silero_vad.onnx` (downloaded 2026-11-17) — 2 327 524 bytes, SHA-256 `1a153a22f4509e292a94e67d6f9b85e8deb25b4988682b7e174c65279d8788e3` |
| Runtime | ONNX Runtime 1.24.4 (the Vision package), CPU execution provider, one thread per session run; DeepFilterNet3 fails to initialise under DirectML (AccessViolation), so both models run on the CPU |

Interfaces measured with ONNX Runtime 1.24.4: DeepFilterNet3 — inputs `input_frame` float[480], `states` float[45304],
`atten_lim_db` float scalar; outputs `enhanced_audio_frame` float[480], `new_states` float[45304], `lsnr` float[1]; 48 kHz mono;
the output 1 440 samples behind the input (cross-correlation on a camera recording). Silero VAD v5 — inputs `input` float[1, 576]
(64 samples of context + 512), `state` float[2, 1, 128], `sr` int64 scalar (16000); outputs `output` float[1, 1] (the speech
probability), `stateN`.

Messages: *The audio model … could not be loaded* (a load failure), and the D034 messages for a missing or another-version file
(the model id `audio-v1`). Without the voice model the voice enhancement fails with its reason and the clip plays (and exports)
its original sound; without the speech model the ducking is unavailable (the export warns and mixes without it).

## Licences (checked 2026-11-17)

| Component | Source | Licence | Conclusion |
|---|---|---|---|
| DeepFilterNet3 weights and code | github.com/Rikorose/DeepFilterNet README: "All code and model weights in this repository are dual-licensed under either MIT … or Apache 2.0, at your option" | MIT OR Apache-2.0 | Usable; its notice travels with a distribution |
| The ONNX export | huggingface.co/kimtos-labs/denoiser-dfn3 (metadata `apache-2.0`, text "MIT OR Apache-2.0") via the torchDF path of grazder/DeepFilterNet | as above | Usable |
| Silero VAD v5 | github.com/snakers4/silero-vad | MIT | Usable; its notice travels with a distribution |
| ONNX Runtime 1.24.4 | as for the hold model | MIT | Already shipped |

Benchmark and quality notes: `docs/PHASE45_AUDIO.md`.
