# Phase 45 — manual test plan (D064: audio editing, mixing and clean-up)

Build: Debug or Release of `feat/phase-45-audio-mixing`. Models: `%LOCALAPPDATA%\AiVideoEditor\models\audio-v1\`
(`denoiser_model.onnx`, `silero_vad.onnx` — docs/AI_MODEL.md "Audio models"). Media: a bouldering video with talk and hall noise,
a music file (MP3 / WAV), ideally a long recording (≥ 20 min). Listen with headphones. Each row: steps → expected result.

## A. Volume on the timeline

| # | Steps | Expected |
|---|---|---|
| A1 | Put a music clip on A1 and a video with sound on V1. | A thin yellow line in each clip: in the middle of the audio clip, in the middle of the video clip's lower (waveform) half. |
| A2 | Select the audio clip, hover its line. | Vertical cursor, the hint «Перетаскивание — громкость клипа…». |
| A3 | Drag the line up / down while playing. | The volume changes while dragging (heard), the label shows dB and %; the Inspector's volume follows; one Ctrl+Z restores it. |
| A4 | Double click the line at 3 places; drag the middle point down and right. | Points appear without a change in sound; the dragged point moves in time between its neighbours and down; the sound dips smoothly there (no click). |
| A5 | Double click a point; select another point and press Delete. | Both removed (the clip stays); Undo brings them back. |
| A6 | Drag the line of a clip with points. | The whole curve moves up / down together. |
| A7 | Drag the top-left square handle inward ~1 s; the top-right ~0.5 s. | Shaded ramps appear; the sound fades in / out; the Inspector shows the frames (video clip: «Появление звука / Затухание звука», the picture is not faded). |
| A8 | Press Esc during a drag. | The values before the drag come back. |
| A9 | Lock the track; try to drag the line. | Nothing changes; double click reports the track is locked. |

## B. Edits keep the automation

| # | Steps | Expected |
|---|---|---|
| B1 | Clip with points: Split between two points. | Both parts show their part of the curve; the sound across the cut is unchanged. |
| B2 | Trim the start past a point, then trim back. | The point disappears from view and comes back at the same sound. |
| B3 | Move the clip; change its speed. | The points stay on the same sound. |
| B4 | Duplicate / Copy–Paste the clip. | The copy has the same points, fades and clean-up. |
| B5 | Unlink Audio of a video clip with points and clean-up. | The new audio clip keeps them; the sound is unchanged. |
| B6 | Save, close, reopen the project. | Points, fades, clean-up, track volume, solo, ducking and the master are all back. |

## C. Tracks and the mixer

| # | Steps | Expected |
|---|---|---|
| C1 | Track header «S» on A1. | Only A1 is heard; S lit yellow; the export preflight warns «Включено Solo». |
| C2 | M on a solo track. | Silent (mute beats solo). |
| C3 | 🔊 on a track → volume slider. | The whole track gets louder / quieter; one undo step per drag. |
| C4 | Tool bar «Микшер»: master 150 %, tracks at 200 %. | Loud but no harsh digital clipping (soft limit at −1 dB). |

## D. Ducking

| # | Steps | Expected |
|---|---|---|
| D1 | Music on A1, the talking video on V1. A1 → 🔊 → «Приглушать во время речи», speech on V1. | The status shows «Определение речи…», then «Речь найдена…»; the music dips while people talk and recovers after. |
| D2 | Change depth, «Начало», «Восстановление», sensitivity while playing. | The effect follows at once; each drag one undo step. |
| D3 | Add points on the music clip. | They still apply together with the ducking. |
| D4 | «Закрепить в точки». | The ducking turns off, the music sounds the same, its clips now have points that can be edited. |
| D5 | Delete the speech track. | The ducking switches off in the same step; Undo restores both. |
| D6 | Rename `silero_vad.onnx` and restart. | The status says the speech model is not set up; the export warns and exports without ducking. |

## E. Clean-up

| # | Steps | Expected |
|---|---|---|
| E1 | Video clip with hall noise: Inspector «Шумоподавление» 60. | «Обработка N %», then «Очищено»; the hiss / hum is lower, the voice natural. |
| E2 | «Улучшение речи» 80 on a clip with talk and music. | The voice is clearer, the background much lower; no metallic artefacts at 50–80. |
| E3 | Toggle «Исходный звук (сравнение)». | The Preview switches between original and processed; the export ignores the toggle. |
| E4 | Split the cleaned clip. | No new processing (both parts share the file). |
| E5 | Long recording (≥ 20 min), «Улучшение речи» 100; «Остановить» halfway, then «Повторить». | Progress, the UI stays responsive, playback continues with the original sound; stop / retry work. |
| E6 | Export while a clean-up still runs. | The preflight blocks with «ещё очищается»; after it is ready the export goes through. |
| E7 | Rename `denoiser_model.onnx`, set «Улучшение речи». | «Не очищено — звучит исходный звук: …» with the reason; the export warns. |
| E8 | Change the source file on disk (e.g. replace it), reopen. | The clean-up runs again (another key); the old cache is not used. |

## F. Preview = Export

| # | Steps | Expected |
|---|---|---|
| F1 | A project using every feature above; export with NVENC and with libx264. | The exported sound equals what the Preview played (points, fades, ducking, clean-up, track / master volume, solo); start and end of the clips exact; 48 kHz stereo AAC. |
| F2 | Export a long project (≥ 20 min). | Completes; the video encoding speed is not lower than before (the cleaned sounds are only read). |
