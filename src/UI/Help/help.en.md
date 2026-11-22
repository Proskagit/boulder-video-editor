# Quick start

A bouldering video in ten steps.

## Steps

- **1. Project.** On the start screen press «Create project».
- **2. Clips.** Add videos to «Media» («+ Import», Ctrl+I or by dragging) and move them onto the timeline; several selected files go in their recording order.
- **3. Training.** «Import FIT Training…» on the toolbar: the climbs from your watch (COROS, Garmin and others) are bound to the clips by themselves — «Boulder» routes with their grade and status, the heart rate in the frame. A miss is easy to fix by hand.
- **4. Edit.** Trim and split off what you don't need (S) — the magnetic main track closes the gaps. Between clips — the «Transition» button (up to 5 s).
- **5. Holds.** Select a clip, pause and press «Mark holds»: a click on a hold gives its outline. The first click on a route proposes all its holds of that colour — review them and press «Accept»; the outlines follow the camera.
- **6. Colour.** The «COLOUR» section → «Auto enhance», then adjust the sliders if you like.
- **7. Follow.** When the climber is well visible and the frame has room (a vertical video from a horizontal one, a zoom), switch on «Follow the climber»: the frame follows the climber and the hold outlines hide behind people («Hide outlines behind people»).
- **8. Sound.** Music on a sound track; ducking under speech, «Noise reduction» and «Voice enhancement» when people talk.
- **9. Design.** Text, templates («Boulder», «Summary», «Heart rate», «Highlight», REC, VHS).
- **10. Export.** «Export» (Ctrl+E): the resolution and the folder; with an NVIDIA card it is fast through NVENC, in the background — you can keep working. The YouTube timestamps are there too.

# Getting started

Boulder Video Editor is an editor for bouldering videos. Its core is working with holds: one click on a hold gives its exact outline, the route is highlighted, the outlines follow the camera and hide behind people. A training file from your watch (FIT) lays the climbs out on the clips by itself.

## The main path

- **Import.** Add videos to the «Media» panel («+ Import» or Ctrl+I) and drag them onto the timeline.
- **Edit.** Trim and arrange the clips, split off what you don't need (S).
- **Holds.** Pause on a clip and press «Mark holds» in the Inspector.
- **Training.** «Import FIT Training…» on the toolbar binds the climbs to the clips.
- **Design.** Add a template («Boulder», «Heart rate» and others), text and transitions.
- **Export.** «Export» (Ctrl+E) saves the finished MP4 video.

## The editor's window

- **The toolbar** at the top: the project, undo, the training, the project settings, export, the settings and this help.
- **Media** on the left: the project's files.
- **Preview** in the middle: the timeline's frame or the source file. What you see here is what gets exported.
- **The Inspector** on the right: the selected clip's properties as one list; its sections collapse. The icon rail left of the list (Clip, Text, People, Holds, FIT, Sound) scrolls to a group.
- **The start screen** at launch: «Create project», «Open…» and the recent projects.
- The right end of the toolbar shows whether the project is saved: «Saved at 10:26» or «Unsaved changes».
- **The timeline** at the bottom: the video and sound tracks.

## What the AI features need

Marking holds, following people and cleaning sound need local AI models (about 1.2 GB). The installer places them with the program. The portable version offers to install them from its ai-models folder at startup; later this is in «Settings…» → «AI models» (which also shows whether every model is in place and repairs damaged ones). Without them the editor works, and these features say the model is missing. Nothing is downloaded from or sent to the internet.

# The project and its files

- **A project is a folder** with a project.json file. «New» (Ctrl+N), «Open» (Ctrl+O), «Recent» — the latest projects.
- **«Save»** (Ctrl+S) and **«Save As»** (Ctrl+Shift+S). «Save As» opens a window: the project's name and its folder («Change…» picks another); the project is saved into a folder of that name — handy for keeping versions side by side: «Project v1», «Project v2». If a project of that name is already there, the editor asks at once whether to replace it.
- **Videos are not copied** into the project: it refers to your files. Don't move or rename them while you need the project.
- **Autosave.** If the editor closed without saving, it offers to recover your work at the next start.
- **«Project Settings…»** — the project's frame size and frame rate.
- **«Settings…»** — the application's settings: language, FFmpeg, markup, FIT trainings.

# Media and the timeline

## Media

- «+ Import» adds videos, sounds and pictures. Select files with a rubber band (Ctrl adds to the selection) and drag them onto the timeline as a group.
- Several selected files can be removed from the project at once; if clips use them, the editor asks once and removes the clips with them. The files on disk stay.
- A double click opens a file in the source: in Preview pick the part you need (I — in, O — out) and put it on the timeline («,» — insert, «.» — overwrite).
- A file dropped on a track lands where it is dropped (on an empty track too); on V1 with the magnet on — at the nearest cut.

## Preview quality

- The button right of the time under the Preview (an eye and the quality): **«Original»** (the default), **«High (1440p)»**, **«Medium (1080p)»**, **«Minimum (720p)»** — how large the Preview shows the videos. It changes at once; lower is lighter for the computer. The export is always made from the originals at full quality.

# Marking holds

A local neural network draws a hold's outline from one click.

## Marking by hand

- Pause on a clip and press **«Mark holds»** in the Inspector's «Holds» section.
- Click a hold — its outline appears. **Enter** confirms, **Esc** discards, **Tab** switches between the outline's variants.
- **Shift+click** refines an outline that took too much. A selected hold's points can be dragged; **Alt+click** adds a point, **Delete** removes the hold.
- **«Done»** leaves the markup. The marked holds stay.

## The route from its first hold

- **«Suggest the route»** (off by default — tick it in «Holds» or in «Settings…») — in «Mark holds», click one hold of a route that has no holds yet. The window «Please wait, analysing» appears (a few seconds; «Cancel» or Esc — the markup goes on by hand).
- Then the holds of the same colour found near it (a route is a compact group; the same colour at the other end of the frame is another route) are shown in the colour the route will get, at most 15. Select a wrong one by a click and press Delete; click a missed one and press Enter.
- **«Accept»** (Ctrl+A) adds all the proposals in one step (Ctrl+Z undoes it); then you can go on marking by hand. «Discard» removes the proposals. «Done» keeps what is shown and finishes the markup; the route's animation (the orange area) starts on the frame you marked on.
- The analysis runs once per markup session and only on the frame you clicked. The rule is strict: better a missed hold than a wrong one — add the missing ones by a click. Turn the check box off to make the first click mark one hold, as before.

## Look and animation

- **Colour, width, keyline** — how the outlines look. The keyline makes the line visible on a busy wall.
- **«Animation»** — the holds appear one by one, then the whole route shows and fades out. «Order» — bottom to top, left to right and so on; «Set to playhead» — start the animation at the current frame.
- **«Follow the camera»** — the outlines stick to the wall and stay on the holds when the camera moves. Where the camera's position is unreliable, they are hidden.
- **Smoothing** of new outlines is in «Settings…».

# FIT trainings and routes

A training file from your watch (COROS, Garmin and others) knows when you climbed and what your heart rate was. The editor uses it to lay the climbs out on the clips.

- **«Import FIT Training…» → «Import…»** — choose a .fit file. The project keeps a link to it; «Point to the file…» — if the file has moved.
- **The climbs are bound to the clips** by the time they were filmed. If the watch and the phone disagree, adjust «Training clock correction, frames» in the same menu.
- The clip's **«FIT training»** section in the Inspector shows its climb. The link can be set or removed by hand; wrong climbs are declined, and «Declined climbs» on the toolbar brings them back.
- **The route** in the «Holds» section — which route the marked holds belong to. Next to a FIT climb the nearest route is chosen; pick another or «New route».
- **«Lay out intro and final»** places the route's and the result's display at the climb's marks.

# Following and people in the frame

The Inspector's **«Follow and people»** section.

- **«Follow the climber»** — the visible part of the frame moves with the chosen person. It needs room: scale the clip beyond «Fill». Follow the whole clip or «from here» — from the current frame.
- **«Smoothness»** — how gently the frame catches up. **«Climber in the frame»** — where they stand in the frame; easier to drag the frame in Preview.
- **«Hide outlines behind people»** — the hold outlines hide behind anyone who covers them. It switches on by itself when a route gets its first holds (you can switch it off here). «Follow and people» and «Holds» also open for a «Boulder» plate anchored to a clip.
- The people analysis runs in the background with its progress in the section. Its results are kept, so it isn't computed twice.

# Text, templates and transitions

- **Text** — the «Add a text clip» button above the timeline. The font, colour, outline, background and its entrance animation are in the Inspector.
- **Templates** — ready-made plates: «Boulder» (the route, grade, status TOP, FLASH or attempt), «Summary», «Heart rate» (in the menu once a training is imported), «Highlight», REC, VHS. The template's fields are in the Inspector's «Template» section; «Status appears» sets when the result shows. A «Boulder» takes «Attempts» when not every attempt is in the edit, and the language of its words (Русский or English; «Flash» is always English — an orange slanted block on the frame). The status is also written on the «Boulder»'s lilac part on the timeline.
- **Summary** — a board of the routes before the template (after the previous «Summary»): number, result, attempts, climb time (from the orange area's end to the lilac's start), grade, average and maximum heart rate; at the bottom the tops, flashes, heart rate and time on routes. Attempts of one route (the same number and grade) are one row. The rows come in one by one during the first half of the template (scrolling past 10), the totals hold the second half. «Own background» turns the board into a full-frame card. In the YouTube timestamps «Summary» is a line of its own with its header.
- **A transition** — the «Transition» button above the timeline: for two selected neighbouring clips, for one clip (its cut nearest the playhead) or, with nothing selected, for the cut under the playhead (a click on the ruler near a cut puts the playhead exactly on it): the menu has 13 transitions with a diagram and a line each — dissolve, dip to black, flash, zoom, push, blur, wipe, circle, doors, zoom out, spin, whip, glitch. Push, wipe and whip take a side in the Inspector. Up to 5 s long: in the Inspector or by dragging an edge of the transition's zone on the timeline. Where a clip has no media beyond the cut, its edge frame is held during the transition. On a sound track it is a cross-fade.
- The **«Heart rate»** plate is added only where the training has a heart rate for that place of the video; otherwise the editor says so in a window.

# Sound

- **A clip's volume** and its fade in / out are in the Inspector's «Audio» section. Volume points can be set and the line dragged right on the timeline.
- **The mixer** (the button above the timeline): the master volume, each track's volume, «Mute», «Solo».
- **«Lower during speech (ducking)»** — the music gets quieter by itself while a voice sounds on the chosen track.
- A clip's **«Clean-up»**: «Noise reduction» removes a hall's steady noise, «Voice enhancement» brings out the voice. The original file is never changed; «Play the original sound (compare)» in the mixer lets you compare.

# Export

- **«Export»** (Ctrl+E) — choose the place and the file's name. The name offered is the date and time, for example 20261118_1430.mp4.
- The export window: the name, the folder, the resolution and the quality profile; the encoder and fine settings are in «More settings». The expected file size is shown below; the window scrolls when the settings and the timestamps are opened. A new project exports at «High quality» with NVENC (without an NVIDIA card — libx264 with the same profile). With an in and out set, only that part is exported.
- A new project not saved yet is exported at once, without saving.
- When it is done you see how long it took and «Show in folder».
- **The export runs in the background:** keep working. The video holds the project as it was when the export started.
- The export's progress shows in the editor's window, and it can be cancelled. With an NVIDIA graphics card the encoding runs on it.

# Keyboard shortcuts

## The project

- **Ctrl+N** — new project, **Ctrl+O** — open, **Ctrl+S** — save, **Ctrl+Shift+S** — save as
- **Ctrl+I** — import, **Ctrl+E** — export
- **Ctrl+Z** — undo, **Ctrl+Y** or **Ctrl+Shift+Z** — redo
- **F1** — help

## Playback

- **Space** — play / pause, **K** — pause, **L** — play, **Ctrl+L** — loop
- **← / →** — a frame back / forward, with **Shift** — a second, **J** — a second back
- **Home / End** — to the start / the end

## The timeline

- **S** — split, **Delete** — delete, **Ctrl+D** — duplicate, **Ctrl+C / Ctrl+V** — copy / paste
- **Ctrl+A** — select all, **Esc** — clear the selection
- **Q / W** — trim the start / end to the playhead (with Shift the following clips move); the same «Trim Left / Right» buttons are on the timeline's bar. With nothing selected — the video under the playhead
- **Alt+← / Alt+→** — nudge the selection by a frame (with Shift by a second)
- **I / O** — in / out, **«,» / «.»** — insert / overwrite from the source
- **N** — the main track magnet, **Ctrl+= / Ctrl+−** — zoom, **\** — the whole timeline

## Marking holds

- **Enter** — confirm, **Esc** — discard, **Tab / Shift+Tab** — another outline variant
- **Delete** — delete the hold, **Ctrl+A / Ctrl+D** — accept / reject all the route's proposals

# Where the data lives

- **Projects** — in the folders you chose when saving.
- **Settings, models, recovery and caches** — in %LOCALAPPDATA%\BoulderVideoEditor. It is shared by the installed and the portable versions; uninstalling leaves it alone (the models only if their removal is ticked).
- **The program** — in its installation folder (by default %LOCALAPPDATA%\Programs\Boulder Video Editor) or the portable folder; the editor writes nothing there.
- **Logs** — in %LOCALAPPDATA%\BoulderVideoEditor\logs. If something goes wrong, attach the app-*.log, errors-*.log and ffmpeg-*.log files from there to your description.
