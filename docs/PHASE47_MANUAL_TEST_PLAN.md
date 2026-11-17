# Phase 47 — manual test plan (D066: S1, the release 1.0)

**Result (the owner, 2026-11-18):** run in a new Windows account of the owner's PC (Windows Sandbox could not start there:
system PnP buses disabled). A1–A5, A7–A10 PASS; A6 NOT CONFIRMED (a GPU present: Occlusion works); B1–B3 PASS; C1 — the notices
accepted as they are; C2 — the draft release made, published by the owner.

Build: `artifacts/release/AiVideoEditor-1.0.0-win-x64.zip` (with FFmpeg) and `AiVideoEditor-1.0.0-models.zip` from
`feat/s1-release-1.0`. Media: **copies** of a few bouldering videos (4K and 1080p) and their FIT. Each row: steps → expected.

## 0. Preparing Windows Sandbox (once)

1. «Включение или отключение компонентов Windows» → «Песочница Windows» → OK → restart.
2. `pwsh -File tools/portable/New-SandboxCheck.ps1 -Media <folder with the copies>` → `artifacts/release/S1-clean-check.wsb`.
3. Double-click the `.wsb`. Inside the sandbox: copy `Desktop\release\*.zip` to the desktop and unpack both.

## A. Clean machine (Windows Sandbox: no .NET, no FFmpeg, no GPU)

| # | Steps | Expected |
|---|---|---|
| A1 | Start `AiVideoEditor.exe` from the unpacked folder (before the models). | Opens without an error; no request to install .NET or FFmpeg. |
| A2 | Import the videos and the FIT from `Desktop\media`. | Thumbnails, durations, the FIT's routes; nothing reported missing about FFmpeg. |
| A3 | «Разметить зацепки» without the models. | A clear message that the model is missing; everything else works. |
| A4 | Run `Install-Models.cmd` from the models folder; restart the editor. | All files «installed», the green line at the end. |
| A5 | Mark holds on a clip (CPU: slower). | The CPU message; the holds marked; the outlines smooth (D065). |
| A6 | Follow on a climber; Occlusion. | Follow works on the CPU; Occlusion reports it needs a DirectML graphics card. |
| A7 | Noise reduction and voice enhancement on a clip with speech; ducking of music under it. | Processed (CPU); the clean-up badge on the clip; the sound changes. |
| A8 | Export 1080p and 4K. | MP4s that play; the export uses x264 (no NVIDIA here) without an error. |
| A9 | Save, close, reopen the project. | Everything back as it was. |
| A10 | `THIRD-PARTY-NOTICES.txt`, `ffmpeg\THIRD-PARTY-NOTICES.txt`, `BUILD-INFO.txt`. | Present; version 1.0.0; FFmpeg «aive-minimal». |

## B. The owner's machine (GPU)

| # | Steps | Expected |
|---|---|---|
| B1 | The portable 1.0.0 with its FFmpeg, the models from the archive. | Hold markup and Occlusion on DirectML. |
| B2 | Export a 4K clip with «Быстро» (NVENC). | NVENC used (the export dialog / log), the file plays. |
| B3 | Follow on the Phase 42 reference clip (people-v2). | The climber followed as before (the people analysis made again once). |

## C. Release (the owner)

| # | Steps | Expected |
|---|---|---|
| C1 | Review the notices and the source offer (legal). | Accepted, or changes listed. |
| C2 | Publish the GitHub Release `v1.0.0` with the four assets and the notes. | The release page; the hashes in `SHA256SUMS` match the files. |
