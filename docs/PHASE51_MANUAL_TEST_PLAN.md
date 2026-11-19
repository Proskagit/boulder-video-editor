# Phase 51 — the installer and the distribution: the manual check (D070)

For the product owner. The automated part (`Test-Installer.ps1`, the unit tests) ran on the build machine; what needs a
**clean Windows** (no .NET SDK, no FFmpeg on PATH, no models, no settings of the owner) and **eyes** is here.

**Where:** a new Windows account on this PC (as for 1.0, D066 §7 — Windows Sandbox does not start here) or another PC / VM
with Windows 10 / 11 x64. A new account has its own `%LOCALAPPDATA%`, so the owner's models, settings and projects are not
seen and cannot be touched. Copy into it (e.g. `C:\Users\Public\bve`): `BoulderVideoEditor-1.3.0-setup.exe`,
`BoulderVideoEditor-1.3.0-win-x64-portable.zip`, two short test clips (copies) and a FIT file (a copy).

Mark each row PASS / FAIL / note.

## A. Setup

| # | Steps | Expected |
|---|---|---|
| A1 | Run the setup. | No administrator prompt (UAC). SmartScreen may warn «Неизвестный издатель» (not signed, D070 §1): «Подробнее» → «Выполнить в любом случае». The wizard is dark, with the editor's icon, in Russian. |
| A2 | The folder page. | `C:\Users\<user>\AppData\Local\Programs\Boulder Video Editor`; «Обзор…» allows another folder. |
| A3 | The tasks page. | «Создать значок на Рабочем столе» ticked. Leave it. |
| A4 | «Готово к установке». | The folder, the shortcut, and the line that the AI models (about 1.2 GB) go to `%LOCALAPPDATA%\BoulderVideoEditor\models`. |
| A5 | «Установить», wait, keep «Запустить Boulder Video Editor» ticked, «Завершить». | The editor starts; the start screen. No question about models (the installer placed them). |
| A6 | Check the desktop and the Start menu. | «Boulder Video Editor» on both, with the icon; both start the editor. |
| A7 | «Параметры» → «Приложения». | «Boulder Video Editor», version 1.3.0, size about 1.4 GB. |
| A8 | «Настройки…» → «ИИ-модели». | «Все модели на месте (около 1,2 ГБ).», the folder; no install button; the note that the installer restores them. |
| A9 | Create a project, import the clips, add them to the timeline, play, export a short MP4. | Import (FFprobe), Preview, export (FFmpeg from `ffmpeg\bin`) work; with an NVIDIA card NVENC is offered. |
| A10 | «Разметить зацепки» on a clip and click a hold. | The contour appears (the model checked on first use, a short progress). With a GPU it runs on it; without one the status says the CPU is used. |
| A11 | Follow / Occlusion on a clip with a person; «Улучшение голоса» on a clip with speech. | Both work (the people and audio models). |
| A12 | Save the project, close the editor, start it from the shortcut. | The project in «Недавние»; opens. |

## B. Update, repair

| # | Steps | Expected |
|---|---|---|
| B1 | With the editor running, run the setup again. | It asks to close Boulder Video Editor first. |
| B2 | Close the editor, run the setup again. | No folder page (the installed folder is used); quick (the models are already in place — not copied again); the desktop shortcut as before. One entry in «Приложения». |
| B3 | Delete `%LOCALAPPDATA%\BoulderVideoEditor\models\audio-v1\silero_vad.onnx`, run the setup again. | The file is back; «Настройки…» → «ИИ-модели» shows every model in place. |
| B4 | After B2 / B3. | The project of A12, the settings and «Недавние» are as before. |

(An update to a really newer version is checked automatically by `Test-Installer.ps1`, T2; it can be repeated by hand with
the next release.)

## C. Uninstall

| # | Steps | Expected |
|---|---|---|
| C1 | «Приложения» → Boulder Video Editor → «Удалить». | A window: the program's files and shortcuts are removed, projects / videos / exports / settings are not; «Также удалить ИИ-модели (около 1,2 ГБ)…» **unticked**, with the note about the portable version. |
| C2 | Leave it unticked, «Удалить», confirm. | The program folder and both shortcuts are gone; the entry is gone. `%LOCALAPPDATA%\BoulderVideoEditor` stays with `models`, `config`, `recovery`; the project folder of A12 and its export stay. |
| C3 | Install again (A1–A5), uninstall with «Также удалить ИИ-модели» ticked. | Also `models\sam2.1-large`, `people-v2`, `audio-v1` are gone; `config` (settings, «Недавние»), the project and the export stay. |

## D. Portable

| # | Steps | Expected |
|---|---|---|
| D1 | After C3 (no models). Unpack the portable zip whole into e.g. `C:\Users\<user>\Desktop\BVE test` (a path with a space). | A folder with `BoulderVideoEditor.exe`, `ffmpeg\`, `ai-models\`, README.txt. |
| D2 | Run `BoulderVideoEditor.exe`. | The start screen, then the question «ИИ-модели не установлены…» with «Установить» / «Не сейчас» (about 1.2 GB to `%LOCALAPPDATA%\BoulderVideoEditor\models`). |
| D3 | «Не сейчас». | The editor works (import, timeline, export). «Разметить зацепки» says the model is not installed and names «Настройки…» → «ИИ-модели». |
| D4 | Close, start again. | The question again (the models are still missing). «Не сейчас». |
| D5 | «Настройки…» → «ИИ-модели». | «Не хватает файлов моделей: 13.», the folder, «Установить из папки ai-models». |
| D6 | «Установить из папки ai-models», close the settings, work in the editor meanwhile (scroll the timeline, play). | The status bar shows «ИИ-модели: N %» with a bar and ✕; the editor stays responsive; at the end «ИИ-модели установлены: скопировано файлов — 13, уже были на месте — 0.» |
| D7 | Without restarting: «Разметить зацепки». | Works. |
| D8 | Close, start again. | No question about the models. |
| D9 | Delete `models\people-v2` in `%LOCALAPPDATA%\BoulderVideoEditor`, start; «Установить»; while the bar is under 50 % press ✕. | «Установка ИИ-моделей прервана…»; in `models\people-v2` no `*.installing` file is left. |
| D10 | «Настройки…» → «ИИ-модели» → «Установить из папки ai-models» again, and while it runs close the editor's window. | The question «Идёт установка ИИ-моделей…»: «Продолжить установку» keeps the window; then close again → «Прервать и закрыть» closes it. |
| D11 | Start again, «Установить», let it finish. | Every model in place («уже были на месте» counts the files copied before). |
| D12 | Copy the portable folder elsewhere, delete `ai-models\` in the copy, start the copy. | No question (models installed); the same settings and «Недавние» as the first copy. |

## W. The web Setup (after the models release `models-1.0` is published)

| # | Steps | Expected |
|---|---|---|
| W1 | After C3 (no models, no program): run `BoulderVideoEditor-1.3.0-web-setup.exe` (about 50 MB). | The same wizard; «Готово к установке» says the models are downloaded from GitHub (internet needed). |
| W2 | «Установить». | The progress shows the model files being downloaded (about 1.2 GB); then «Запустить». «Настройки…» → «ИИ-модели»: every model in place; «Разметить зацепки» works. |
| W3 | Run the web Setup again. | Quick: nothing downloaded (the models are in place). |
| W4 | Disconnect the network, delete `models\audio-v1`, run the web Setup. | A download error with retry / cancel; cancel leaves the installed program as it was. |

## E. Not by hand

Covered automatically, noted for the record: the web Setup against a local server (light, a tampered asset refused, the
download, nothing requested again — `Test-Installer.ps1` W0–W3); the installer's update over the previous version, the downgrade refusal, the
running application's refusal, the two uninstalls (with and without the models), the files of the per-user folder kept
(`Test-Installer.ps1`); the models installer's damaged package, full disk, cancel, crash leftovers, other files untouched
(`ModelPackageInstallerTests`); the start's offer, the background installation, the settings section, closing during it
(`ModelSetupServiceTests`).
