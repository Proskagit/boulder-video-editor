# Export benchmark (D059 E0)

A permanent, repeatable benchmark of the export pipeline: the real `ExportService` (ffmpeg decoders, the Avalonia
rasterizer, the ffmpeg encoder) on generated projects over the owner's test videos. A console program outside the
solution (like `tests/Vision.ModelTests`), built and run by hand on the target machine — never in CI.

```powershell
dotnet build tools/export-bench -c Release
$bench = "tools/export-bench/bin/Release/net8.0/ExportBench.exe"
& $bench list                                                   # the scenarios
& $bench suite --scenarios 4k,4k-1080,vert-blur --runs 3 --frames 600 --label after --results results.jsonl
& $bench suite --scenarios 4k --runs 3 --encoding profile:Fast:nvenc --label nvenc
& $bench run 4k --frames 120 --keep --hash-canvases             # one run, the MP4 kept, the canvases hashed
```

- Every run of a suite is its own process (`run`), so nothing is shared between runs; the suite prints the **medians**
  as a Markdown table and appends every run to `--results` (JSON lines).
- Per run: wall time; the stages (prepare / audio / video / finalize); the video stage's fps; per frame the rasterizer
  time (summed over all rasterizers), the encoder write time and the decoder's time per source frame; the number of
  rasterizers; source frames read; Gen-2 collections; the process's and the system's CPU (cores); GPU, NVENC and NVDEC
  load and the VRAM increase (`nvidia-smi`, every 250 ms); the peak working set and private bytes; the file size and
  bitrate; the SHA-256 of the MP4 (byte identity between variants) and, with `--hash-canvases`, of all canvases in order.
- `--media` (or `AIVE_BENCH_MEDIA`): the test videos, default `C:\AiVideoEditor\VideosForTest` (`12x10\1014*.mp4`, the
  vivo HLG clip). `--frames N` overrides a scenario's length. `--rasterizers K` fixes the pool size (default: chosen per
  export, `ExportPipelineSettings`). `--reference PATH` also writes the canvases losslessly (yuv420p after the export's
  BT.709 conversion, x264 QP 0) — the reference for VMAF.
- `--encoding`: `default` (Balanced, libx264), `x264:QUALITY:PRESET` (Custom, the D028 names), `profile:Fast|Balanced|HighQuality[:nvenc]`,
  `custom:x264|nvenc:QUALITY:PRESET`, `bitrate:x264|nvenc:MBPS`; any of them with `@Uhd4K|FullHd|Hd|Sd` exports at that
  resolution preset (D060 §3), e.g. `default@FullHd`.
- Research switches (FU-EX.2, `docs/EXPORT_PERFORMANCE.md` §9; never the app's behaviour): `AIVE_BENCH_HWDEC=1` decodes the export's
  sources with `-hwaccel auto`; `AIVE_BENCH_DECODE_MAX=WxH` has the decoder scale them down to that bound.

## Calibration (D059 §5)

`calibrate --refs a.mkv,b.mkv --results calibration.jsonl [--settings x264:medium:18,nvenc:p5:22]` encodes every lossless
reference with every setting (the export's own codec options, `FfmpegExportEncoder.CodecArgumentsFor`) and measures the
encoder-only speed, the bitrate, bits per pixel and frame, and VMAF against the reference (the 4K model above 1080p, every
frame; mean, minimum and the mean of the worst 1 %). A setting is `x264:PRESET:CRF` or `nvenc:PRESET:CQ[:mp][:noaq]`.

## A baseline of an older commit

The harness builds against the commit before D059 with `-p:DefineConstants=BASELINE` (only `default` and
`x264:QUALITY:PRESET` encodings, no calibration):

```powershell
git worktree add --detach ..\AiVideoEditor-baseline 07252bf
Copy-Item tools\export-bench ..\AiVideoEditor-baseline\tools\ -Recurse
dotnet build ..\AiVideoEditor-baseline\tools\export-bench -c Release -p:DefineConstants=BASELINE
```

## Scenarios

| name | what | frames |
|---|---|---|
| `4k` | 4K60 H.264 SDR, one layer, 4K canvas | 600 |
| `4k-1080` | 4K60 source → 1920×1080 | 600 |
| `vert` | 4K60 source → 1080×1920 (contain) | 600 |
| `vert-blur` | → 1080×1920 with the blurred background | 600 |
| `vert4k-blur` | → 2160×3840 with the blurred background | 600 |
| `4k120` | a 4K120 source in a 60 fps project | 600 |
| `overlays` | 4K + outlined text, «Боулдер», heart rate, animated hold outlines | 600 |
| `vhs` | 4K + the VHS template | 600 |
| `tracks2` | two 4K video layers (picture in picture) | 600 |
| `transitions` | three 4K clips joined by a Dissolve and a Push | 600 |
| `hevc-vert` | 4K60 HEVC 10-bit HLG → 1080×1920 (the HDR graph) | 600 |
| `long` | the twelve 12×10 clips back to back, repeated | 36 000 |
| `real` | a 12×10 training: 12 clips with sound, «Боулдер» per route, titles, outlines | 7 200 |

Results of D059: `docs/EXPORT_PERFORMANCE.md`.
