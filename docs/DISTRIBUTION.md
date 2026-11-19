# Distribution — Setup and Portable (D070)

Boulder Video Editor is distributed for Windows 10 / 11 x64 in two ways, built together from one commit by one command:

| File | What it is |
|---|---|
| `BoulderVideoEditor-<version>-setup.exe` | The installer: the program, .NET, FFmpeg and the AI models; per user, no administrator rights. |
| `BoulderVideoEditor-<version>-web-setup.exe` | The light installer (~50 MB): the program, .NET, FFmpeg; the AI models downloaded from the `models-<n>` release on GitHub, each checked by its SHA-256 (D070 §7). Needs internet. |
| `BoulderVideoEditor-<version>-win-x64-portable.zip` | One archive: the portable program (.NET, FFmpeg) and the AI models in `ai-models\`. |
| `aive-ffmpeg-minimal-<ffmpeg>-sources.tar` | The complete corresponding source of the bundled FFmpeg (GPL-2.0-or-later, D066 §6). |
| `SHA256SUMS` | The checksums of the files above. |

Nothing is downloaded by the application, the full Setup or the Portable archive; only the web Setup downloads the models,
from the separate GitHub release `models-<models.json version>` (now `models-1.0`), whose assets are the pinned files.
There is no automatic update.

## Where things live

| What | Where | Written by | Removed by the uninstaller |
|---|---|---|---|
| The program (Setup) | `%LOCALAPPDATA%\Programs\Boulder Video Editor` (default; chosen on the first install) | the installer only | yes |
| The program (Portable) | the unpacked folder, anywhere | nobody — the application never writes its folder | — |
| AI models | `%LOCALAPPDATA%\BoulderVideoEditor\models\<folder>` | the installer; the application only on «Установить» (Portable) | only with «Также удалить ИИ-модели» |
| Settings, recent projects | `%LOCALAPPDATA%\BoulderVideoEditor\config` | the application | never |
| Recovery, caches, logs | `%LOCALAPPDATA%\BoulderVideoEditor\recovery`, `cache`, `logs` | the application | never |
| Projects, media, exports | wherever the user saves them | the application | never |

The per-user folder is shared by the installed version and every portable copy: one set of models, one settings file. Two
portable copies of different versions in different folders therefore see the same settings and models — as two installed
versions would. Shortcuts: the Start menu (`%APPDATA%\Microsoft\Windows\Start Menu\Programs\Boulder Video Editor.lnk`) and,
when ticked, the desktop. The uninstall entry: `HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\{564FF1BA-2649-4C46-9BF9-C48DCCEA4DA9}_is1`.

## The layout

```
Boulder Video Editor\                     (Setup)            BoulderVideoEditor-<v>-win-x64-portable\   (Portable)
  BoulderVideoEditor.exe, *.dll, .NET runtime                  the same program files
  appsettings.json  → ffmpeg\bin\ffmpeg.exe                     the same
  ffmpeg\bin\ffmpeg.exe, ffprobe.exe, notices, licences        the same
  README.txt (Setup), BUILD-INFO.txt                           README.txt (Portable), BUILD-INFO.txt
  THIRD-PARTY-NOTICES.txt, licenses\, docs\AI_MODEL.md         the same
  models-licenses\ (the models' notices)                       ai-models\  models.json, models\<folder>\<file>,
  unins000.exe, unins000.dat                                               THIRD-PARTY-NOTICES.txt, licenses\, Install-Models.cmd
```

The models (`tools/models/models.json`, pinned in `HoldModel`, `PeopleModel`, `AudioModel`; Vision.Tests checks both):
`sam2.1-large` (hold markup), `people-v2` (Follow, Occlusion), `audio-v1` (voice enhancement, ducking) — about 1.2 GB.

## Building a release

Once per build machine:

```
winget install --id JRSoftware.InnoSetup -e --scope user      # Inno Setup 6 (6.7.3 used for 1.3.0)
```

The .NET SDK is pinned by `global.json`. The inputs:
- **the minimal FFmpeg package** — the unpacked `aive-ffmpeg-minimal-*-win64.zip` of the "Minimal FFmpeg" workflow
  (`tools/ffmpeg-minimal`, D066 §2) — or the `ffmpeg\` folder of an earlier release built from it (1.3.0 used the 1.0
  release's: FFmpeg 9.0.2, unchanged);
- **the models** — a folder with the pinned files (default `%LOCALAPPDATA%\BoulderVideoEditor\models`; every file is checked
  by its size and SHA-256 and refused otherwise);
- **the FFmpeg sources tar** of that FFmpeg (attached to the release next to the binaries).

From a clean checkout of the release commit (a dirty tree is marked `.dirty` in the version):

```
pwsh -File tools/distribution/New-Release.ps1 -Version 1.3.0 -FfmpegPackage <ffmpeg package folder> -FfmpegSourcesTar <sources.tar>
```

It writes `artifacts/release/<version>/` (the setup, the web setup, the portable zip, the tar, `SHA256SUMS`; the models
release's assets in `models-<n>-assets\`; the portable folder unpacked
beside them; the work folders in `stage\`). Steps inside: `tools/portable/Publish-Portable.ps1` (self-contained win-x64,
`-FfmpegPackage`), `tools/models/Pack-Models.ps1 -NoZip`, the portable folder with `ai-models\` zipped (models stored,
the rest compressed), `tools/installer/New-ModelsInclude.ps1` and `ISCC` on `tools/installer/BoulderVideoEditor.iss`.
`-SkipSetup` builds the portable archive only; `-SkipPortableZip` leaves it unpacked.

Then check the installer on this machine without touching the real installation (a test AppId, name and per-user folder):

```
pwsh -File tools/installer/Test-Installer.ps1 -Stage artifacts/release/<version>/stage -Version <version>
```

and run `docs/PHASE51_MANUAL_TEST_PLAN.md` on a clean Windows account.

**The models release** (once per models version; the web Setup downloads from it): a published GitHub release with the tag
`models-1.0` whose assets are the files of `artifacts/release/<version>/models-1.0-assets\` (13 model files, `SHA256SUMS`)
and the models' notices (`stage\BoulderVideoEditor-<version>-models\THIRD-PARTY-NOTICES.txt`, `licenses\`):

```
gh release create models-1.0 artifacts/release/1.3.0/models-1.0-assets/* --title "AI models 1.0" --notes "The AI models of Boulder Video Editor (for the web Setup)."
```

Never delete or replace it while a web Setup that points at it is published; a new model is a new models release. The GitHub Release (a tag `v<version>`, the four
files, release notes) is published by the product owner.

### A new version

1. Merge the changes; pick the version (`major.minor.patch`).
2. If a model changes: a new folder name (e.g. `people-v3`), its files pinned in `src/Vision`, `tools/models/models.json`
   updated (Vision.Tests checks them), the licence checked (`docs/AI_MODEL.md`). The uninstaller's list of model folders is
   generated from `models.json` (+ `people-v1`); add an older folder there (`New-ModelsInclude.ps1`) when a model is replaced,
   so «Также удалить ИИ-модели» still removes it.
3. If FFmpeg changes: a new minimal FFmpeg package and its sources tar (`tools/ffmpeg-minimal`).
4. Build (`New-Release.ps1`), run `Test-Installer.ps1`, the manual plan, write the release notes, publish.

**Never change the installer's AppId** — it is what lets a new Setup update the installed program instead of installing a
second copy.

### Code signing

1.3.0 is not signed: Windows SmartScreen may show «Windows защитил ваш компьютер» / «Неизвестный издатель» for the setup and the
exe — «Подробнее» → «Выполнить в любом случае». To sign later: sign `BoulderVideoEditor.exe` (and the other own `AiVideoEditor.*.dll`)
in the published folder before the portable zip and the setup are made, and the setup itself with Inno's `SignTool=` directive
(`signtool sign /fd sha256 /tr <timestamp server> …`) — both belong in `New-Release.ps1` between steps 1 and 3.

## Installing, updating, removing (the user's view)

- **Setup:** run the setup → the folder (first time only) → «Создать значок на Рабочем столе» → «Установить» →
  «Запустить Boulder Video Editor». No administrator rights.
- **Web Setup:** the same, but the missing models are downloaded during the installation (internet needed; a download whose
  SHA-256 differs stops the installation, nothing of it is kept). The full and the web Setup update the same installation.
- **Update:** run the new version's setup: the same folder, the same entry, the shortcuts as chosen before; settings,
  projects and models kept; a model in place with its SHA-256 is not copied again, a damaged or missing one is. Installing an
  older version over a newer one asks first (`/SUPPRESSMSGBOXES` answers No). A running editor is asked to be closed first.
- **Repair the models:** run the setup again (Setup) or «Настройки…» → «ИИ-модели» → «Установить из папки ai-models» (Portable).
- **Uninstall:** «Параметры» → «Приложения» → Boulder Video Editor → «Удалить»: the first window offers «Также удалить
  ИИ-модели» (off by default), then the confirmation. The program's files and shortcuts go; the per-user folder stays (the
  models too, unless ticked). Silent: `unins000.exe /VERYSILENT [/DELETEMODELS=1]`.
- **Portable:** unpack the whole archive anywhere, run `BoulderVideoEditor.exe`; at the first start «Установить» copies the
  models in the background (the status bar shows the progress and ✕), «Не сейчас» leaves the AI features off. After that
  `ai-models\` may be deleted. An update is a new folder.
