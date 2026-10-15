# AI hold model (D034)

The AI hold markup (Phase 19) uses one pinned local model. The application never downloads, writes, replaces or repairs
it; without it only the hold markup is unavailable — everything else works.

## The model

| | |
|---|---|
| Id | `sam2.1-large-v1` |
| Network | SAM 2.1 Hiera Large (Meta), exported to ONNX by samexporter 0.5.0, opset 18 |
| Folder | `%LOCALAPPDATA%\AiVideoEditor\models\sam2.1-large\` (one copy per machine; never in the repository or a project) |
| `sam2.1_large.encoder.onnx` | 852 451 097 bytes, SHA-256 `5ca0f505138811202a60445427383559fb84afcd80e7a5e2d1ce5d9f546e3362` |
| `sam2.1_large.decoder.onnx` | 16 519 627 bytes, SHA-256 `d2ef7f723ed226f424d590cd382bfef135fdfd4cb74f3a76f6bd563a63b28f10` |

## Installing

1. Produce the two files with the recipe below (or copy them from a machine that has them).
2. Put both into `%LOCALAPPDATA%\AiVideoEditor\models\sam2.1-large\` with exactly these names.
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
  highest-scoring visible mask of the three (masks over 2 % of the frame hidden, as for one point). Verified locally:
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

The weights are not distributed by this project. Should they ever be: the Apache-2.0 text, the attribution notices
(SAM 2 has no NOTICE file at the pinned commit) and a statement that the files are an ONNX conversion of the SAM 2.1
Hiera Large checkpoint made with samexporter 0.5.0.

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
