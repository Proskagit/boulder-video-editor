# Phase 45 — Audio editing, mixing and clean-up (D064)

Branch `feat/phase-45-audio-mixing`, `project.json` **v25**. Decision: DECISIONS.md **D064**. Manual plan:
`docs/PHASE45_MANUAL_TEST_PLAN.md`. Models: `docs/AI_MODEL.md`, "Audio models".

## 1. What the user gets

| Task part | Where | How it works |
|---|---|---|
| Several audio tracks, import, move, trim, split, delete / undo | existing (D008, D027, D030, D032) | unchanged; volume points and clean-up go with Split / Duplicate / Paste / Unlink Audio |
| Clip volume | the line in the clip, the Inspector | 0–200 %; dragging the line changes it (with points: the whole curve) |
| Volume points / keyframes | the line in the clip, «Ключ. кадр», the diamonds | amendment 1: a video clip's keyframes and volume points are one set (added, moved, removed together); on an audio clip «Ключ. кадр» and the diamonds edit its points; double click on the line — a point on the frame; drag — time and value; double click / Delete — remove; Inspector «Сбросить точки»; the Inspector's «Громкость» between two points raises the whole segment |
| Fade in / out of the sound | two square handles at the top corners | drag inward; whole frames; audio clip — its fades; video clip — sound-only fades (the picture's D025 fade stays separate); exact values in the Inspector |
| Track volume, mute, solo | track header (M, S, 🔊 flyout), «Микшер» | volume 0–200 %; while any track is solo only solo tracks sound — in the export too (the preflight warns) |
| Master volume | «Микшер» | 0–200 %; the mix ends with a stateless soft clip at −1 dBFS — never overloads |
| Ducking | track header 🔊 → «Приглушать во время речи» | the speech track, depth −3…−40 dB, «Начало» (attack, 0–2000 ms, the sound is already down when the speech starts), «Восстановление» (release, 50–3000 ms), sensitivity; computed, multiplied with the clips' points (never changes them); «Закрепить в точки» writes it into the points for manual correction |
| Noise reduction | Inspector «Очистка звука» | FFmpeg `highpass` + `afftdn`, strength 0–100; steady noise: hall, ventilation, hum, hiss, wind rumble |
| Voice enhancement | Inspector «Очистка звука» | DeepFilterNet3 (local neural model), strength 0–100 = the share of the separated voice blended with the delay-compensated dry sound |
| Original sound of a video clip | the same line / handles / clean-up on the video clip | no extraction needed; Unlink Audio still exists and keeps points and clean-up |
| Compare original / processed | «Исходный звук (сравнение)» in the Inspector and the mixer | the Preview only; never saved, never exported |
| Preview = Export | one snapshot, one `AudioMix` | tested sample for sample (`ExportAudioContractTests`) |

## 2. Technology choices

- **Noise reduction — FFmpeg `afftdn` (+ `highpass`)**: already a dependency (LGPL build, D002), no model, ×300 real time,
  steady-noise only and limited to 24 dB so a voice never turns robotic. `afftdn` delays its raw output by a constant
  **1 200 samples** at 48 kHz (half its 50 ms window; measured with FFmpeg 9.0.1): the filter pads 1 200 samples after the source
  (`apad`) and the processor drops 1 200 from the start — aligned to ±1 sample (`AudioCleanupIntegrationTests`).
- **Voice enhancement — DeepFilterNet3** (MIT OR Apache-2.0), the torchDF streaming single-graph ONNX: 48 kHz full band, 16 MB,
  hop 480 samples, state 45 304 floats, output 1 440 samples late (measured). Its own `atten_lim_db` mixes the noisy input without
  the delay (a comb filter — measured: −6 dB gave a *lower* RMS than −20 dB), so the strength is the processor's own
  delay-compensated blend. DirectML fails to initialise this graph (AccessViolation in `InferenceSession.Init`); a 480-sample hop
  gains nothing on a GPU anyway — **CPU**, 30 s chunks with 1 s warm-up processed in parallel (min(cores / 2, 8)), deterministic.
  Mono: the voice is the mid of the channels; the dry part keeps the stereo.
- **Speech detection — Silero VAD v5** (MIT, 2.3 MB ONNX, CPU): 16 kHz (a 3-sample mean from 48 kHz), 32 ms windows.
- **Considered, not used**: RNNoise / `arnndn` (lower quality, the model files' licence unclear); Demucs / MDX-Net for voice vs
  music separation (80–300 MB, slow on CPU, the weights' licences vary — DeepFilterNet already lowers music noticeably);
  separate de-reverb / de-wind / de-click models (no proven gain worth their size — DeepFilterNet reduces some reverb and wind; the
  noise reduction's highpass takes the wind's rumble). A limiter as a user effect, EQ, compressor, transcription etc. — out of
  scope (task part 13).
- ONNX Runtime 1.24.4 (MIT) is already shipped (Vision); no new NuGet package.

## 3. Non-destructive processing and the cache

The whole source file is processed once per setting into a 48 kHz stereo float WAV aligned to the source's sample 0, in
`%LOCALAPPDATA%\AiVideoEditor\cache\audio` (key: the file's content fingerprint, start time, NR / VE strengths, the algorithm and
model version; LRU above 4 GB). Every clip of the file with the same setting shares it; a changed or relinked file is another key.
The source file is never written. Until the cleaned sound is ready the Preview plays the original (the Inspector shows «Обработка
N %»); the export waits for it (preflight error) and warns for a failed one. A slider's drag starts one job after it settles
(600 ms). Limits: a RIFF WAV holds about 3 h of stereo float — a longer source is refused with a message.

## 4. Benchmark (2026-11-17)

Ryzen 7 5800X (8 cores / 16 threads), RTX 4070 SUPER (not used by these models), 32 GB, Windows 11, FFmpeg 9.0.1;
`AudioModelBenchmarkTests` (local, `AIVE_AUDIO_BENCHMARK=1`), a 30-minute AAC recording (the bouldering test video's sound, looped):

| Job (30 min of sound) | Time | Real-time factor | Peak working set of the test process |
|---|---|---|---|
| Noise reduction 50 | 6.0 s | ×298 | 276 MB |
| Voice enhancement 100 | 61.6 s | ×29 | 1 045 MB |
| Both (50 / 70) | 65.7 s | ×27 | 1 147 MB |
| Speech analysis (Silero) | 8.8 s | ×204 | — |

DeepFilterNet3 single-thread: 36 s of sound in 5.8 s (RTF 0.16). On a camera recording with talk («человек бар», 36 s) the
voice enhancement lowered the RMS from 0.065 to 0.048 and Silero found speech in 76 % of it. A Ryzen 7 7800X3D (8 cores) is
expected to run the same jobs at least as fast (the same parallelism, higher IPC). VRAM: none used.

## 5. Tests

- Core: `AudioMixRulesTests` (points' interpolation, ducking curve, speech intervals, soft clip, the gain curve block-size
  independence), preflight audio checks in `ExportPreflightTests`.
- Timeline: `AudioMixEditTests` (points through Split / Trim / move / Duplicate / Unlink, sound fades, solo, track and master
  volume, mix-only snapshots, ducking validation and curve, deleting the speech track, «Закрепить в точки»); `AudioPipelineTests`.
- Project: `AudioMixPersistenceTests` (v25 round trip byte for byte, v24 → v25, damaged values, newer file).
- Export: `ExportAudioContractTests.The_D064_mix_is_sample_for_sample_the_preview_and_never_reaches_full_scale`.
- Video (real FFmpeg, CI): `AudioCleanupIntegrationTests` (NR alignment ±1 sample and length, chunk seams exact, blend, speech
  windows, missing model, cancel).
- UI: `AudioProcessingCoordinatorTests`; Rendering: `VolumeEnvelopeViewTests`.
- Local only (real models): `Vision.ModelTests/AudioModelBenchmarkTests`.

## 6. Known limits

- The voice enhancement is mono (the voice in both channels); the noise reduction handles steady noise only (a sudden crash is
  not removed); speech under loud music is reduced but not perfectly separated.
- Ducking reads the speech of the speech track's clips' sources (their cleaned sound when cleaned) — a clip's own mute excludes
  it, the track's mute / solo do not (they change what is heard, not where speech is).
- Unlink Audio of a clip with both a picture fade and a sound fade on the same edge keeps the longer of the two.
- A volume point drag snaps to nothing (free time); points are kept outside a trimmed range and come back with a trim back.
