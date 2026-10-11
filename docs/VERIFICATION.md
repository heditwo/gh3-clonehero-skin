# Verification and recording

## Automated checks

```bash
MISE_QUIET=1 scripts/test.sh
```

The script runs the C# logic tests (`tests/SkinLogic.Tests.csproj`, .NET 10 SDK), `ruff check` and `ruff format --check` on the Python code ([CODE-STYLE.md](CODE-STYLE.md#python)), the Python tests (`tests/test_*.py`), and a syntax check of the shell scripts. `tests/test_docs_links.py` checks that every relative link and anchor in the Markdown files resolves and that each Mermaid diagram is small (at most 16 nodes). Also build the mod with zero warnings:

```bash
MISE_QUIET=1 dotnet build src/Gh3Skin.csproj -c Release --nologo
```

These optional variables add checks against real data to the C# tests:

| Variable | Check |
| --- | --- |
| `GH3_PACK_ASSETS=$PWD/assets` | Loads all compiled packs and the library: cameras, lighting, scripts, actors, particles, flag-driven nodes |
| `GH3_NATIVE_DATA=<GH3 DATA>` | Reads all native song PAKs; exact My Curse camera-edit round trip |
| `GH3_EXPORTED_EVENTS=<events folder>` | Compares exported song direction with native playback |
| `GH3_DIRECTION_ASSETS`, `GH3_CROWD_ASSETS`, `GH3_HAIR_ASSETS`, `GH3_LIGHTING_ASSETS`, `GH3_AUDIO_ASSETS` | Library parts |
| `GH3_HAIR_BENCH=<profile>` | Hair motion bench (no checks), see [Hair motion](#hair-motion). `GH3_HAIR_BENCH=character:<.chchar>` runs the profile hair and the custom chains of a character package, see [Custom chains](#custom-chains) |
| `GH3_CHARACTER_HAIR_ASSETS=<assets>` | Plays My Curse (240 s) with each profile of `<assets>/libraries/gh3-characters-v1` (with `<assets>/libraries/gh3-band-v1` and `<assets>/stages/events`). It prints the fastest hair joint and the largest mean turn from the rest pose for each profile, and fails at 30 m/s (`StageHair.WarningSpeed`). 2026-10-09: 3.0–11.7 m/s, 14–36° |
| `GH3_SCRIPT_SOURCE`, `GH3_STAGE_SCRIPT_REPORT` | Decoded GH3 scripts and a report path |
| `GH3_NATIVE_*_ORACLE` | Reference JSON from the native emulation tools in the local `assets/event-audit/behavior-work/` folder (not in Git) |
| `GH3_PACKAGE_ASSETS=$PWD/assets` | Imports `assets/packages/gh3-band-v1.chlib` with the mod's importer and compares all 1,240 decoded q48 clips with the `GH3CLP01` clips of `assets/libraries` (bound 0.0075°). Needs about 1 GB in `TMPDIR`; set `TMPDIR` to a disk folder |
| `GH3_BENCH=<venue>`, `GH3_BENCH_SONG=<chart folder>` | Runs a venue's scripts and particles against a chart outside the game (`tests/ScriptBench.cs`); `GH3_BENCH_ALLOC=1` adds allocation samples |

After a change to the venue export or the shaders, run `python3 tools/audit-materials.py`. All used materials must draw with their GH3 template.

After a change to the shaders, also check Direct3D 11. The Windows build of Clone Hero runs under Wine with DXVK (see `tools/windows/run-win.sh` and [the spike report](spike/d3d11.md)). Build the mod against the Windows interop assemblies (`GameRoot` of the Windows copy), and capture the same song times with `-force-d3d11` and `-force-glcore` (`GH3_SKIN_CAPTURE_CLOCK=song`). Compare the frames with the Linux frames. A stage with no program for the graphics API logs `GH3 stage is off: ... has no program for the <API> graphics API` and does not draw; `-force-vulkan` shows this message.

### Toon materials and hair layers

After a change to the toon shader (`toon()` in `src/Stage/Shaders/Stage.glsl`) or to the hair layers:

1. GH3 characters do not change: capture the same song times on `gh3.dive` and `gh3.budokan` before and after the change, with the default band, and compare the frames. The difference must be in the range of two runs of the same build (the band animation is not fully the same in two runs).
2. Capture a toon character with the head views of `GH3_SKIN_DEBUG_VIEW_HEAD=local` (front, three-quarter, side, back, back from above, front from above) and `GH3_SKIN_DEBUG_HAIR_LAYER=mark`. Count the flat-color pixels. The back views must have none. The front views must have more marked pixels than with `GH3_SKIN_DEBUG_HAIR_LAYER=off` (the eyebrows through the bangs).
3. Put two toon characters with hair layers on the guitarist and the bassist, and use `GH3_SKIN_DEBUG_VIEW_HEAD=through`. The color of the bassist must not show through the guitarist's hair.
4. Check Direct3D 11 under Wine with the same head views ([Test under Wine](#test-under-wine)).

Result on 2026-10-10 (Linux OpenGL, Project SEKAI Miku and Tda Miku, gh3.dive): marked pixels in the front view 1,031 and 1,084 (with `off`: 452 and 849); in the back view and the back view from above 0. With two characters (8 `through` views, both orders): the bassist's color showed only where the bassist's own face was in the open (her eyebrows over her own bangs), never on the guitarist's hair. Windows Direct3D 11 under Wine: the marked pixels of each head view are within 13 pixels of Linux. See [STATUS.md](STATUS.md#toon-characters).

After a change to `src/Packages/` or to the package converter, run the cache comparison in [PIPELINE.md](PIPELINE.md#check-the-result) (`pipeline.package.cachecheck`).

### Package import in the game

The log shows the package steps with the prefix `GH3 packages:`:

| Line | Meaning |
| --- | --- |
| `found N usable package(s)` | The scan; one `skipped <path>: <reason>` warning for each bad package |
| `imported <id> <version> in X s (…)` | Cold cache: the import time, the checked bytes, the textures, and the managed heap of one package |
| `<id> ready from the cache` or `all packages ready from the cache in X ms` | Warm cache |
| `GH3 stage waits for the import of <id>` | The song started before the import of the library or the stage ended; the stage loads when it ends |
| `GH3 stage source: package …` | The stage and library packages that the stage came from |
| `GH3 stage load time: X ms on the main thread (…)` | The stage load frame: cache texture uploads and the clip prefetch wait |
| `GH3 clips: clip … was not prefetched` | A clip decoded on the main thread during play (a prefetch gap) |

To test a cold import, delete `UserData/GH3Skin/Cache/` in the test game.

### Package textures

The packages store the model textures pre-encoded ([PACKAGE-FORMAT.md section 7](PACKAGE-FORMAT.md#7-textures)); the validator checks each one. After a change of the encoder (`pipeline/package/texture.py`), check that the output did not change: `tests/test_texture.py` has a fixed digest (equal to the C# encoder of 2026-10-10), and the whole package set can be compared with a cache of `tools/PackageImport` (the mip data after the header of each `.tex` file). After a change of the importer, `pipeline.package.cachecheck` compares the cache with the pipeline output ([PIPELINE.md](PIPELINE.md)).

### Python refactors

A refactor of the Python code must not change an output. Check it against `main`:

1. `scripts/test.sh`, and `blender/test.sh` with Blender 5.2.1 and 4.2 (`blender/test.sh OUT blender-clean <Blender 4.2>`).
2. Regenerate into the worktree with empty encoder caches (delete `assets/packages/.texture-cache` and `.clip-cache`) and compare the SHA-256 of every file of `assets/libraries`, `assets/characters`, `assets/instruments`, `assets/stages`, `assets/stage-packs`, and `assets/packages` with the outputs of `main` (`build-report.json` without its times). Include `venues` when `pipeline/gh3`, `pipeline/stagepack`, or `pipeline/venues` changed, and `render` when `pipeline/render` or `pipeline/gh3/shader.py` changed.
3. Encode every unique PNG of the assets with `pipeline.package.texture.encode` of both trees and compare the bytes.
4. Run the rig corpus (`tools/rig-corpus/run.sh OUT --check --export`) with both add-on builds and compare `results.json` and the exported packages.
5. Export the test characters with both add-on builds (a private `BLENDER_USER_RESOURCES`) and compare the packages.

Result of the readability branch (2026-10-10): see the commit messages of the branch and the report to the orchestrator.

### Load times

`GH3_SKIN_TIMING=1` writes `[timing]` lines: the time since the process start, the managed heap, the resident memory, the garbage collections, and the JIT time of each load phase (mod start, scenes, package scan, skin texture upload, each step of the stage load, the first frame with a running song clock, song time 0, the picker). It also writes `[timing] … long frame N ms` for each main-thread frame longer than 50 ms. `GH3_SKIN_TIMING_RELOADS=<n>` loads the gameplay scene again 4 s after song time 0, *n* times: a song change without the menus, for the stage load of the next song and the memory after each song.

Measure the user flows, three runs each, under the record lock:

```bash
GH3_CLONE_HERO_DIR=<copy> scripts/measure-load.sh ~/.cache/<name>/before 3
GH3_CLONE_HERO_DIR=<copy> scripts/measure-load.sh ~/.cache/<name>/after 3
uv run python tools/summarize-load.py ~/.cache/<name>/before ~/.cache/<name>/after
```

| Flow | Run |
| --- | --- |
| `warm-menu` | Start to the main menu with a warm cache |
| `cold-menu` | The same after the script deletes `UserData/GH3Skin/Cache`; it waits for the end of all imports |
| `warm-song`, `cold-song` | `--song` start (`GH3_SKIN_PREVIEW_SONG`, default the native My Curse chart; `GH3_SKIN_STAGE`, default `gh3.dive`) to the stage and the song clock |
| `picker` | The F6 picker in the menus (`GH3_SKIN_PICKER_TEST`) |
| `reload`, `reload-random` | Five songs on the same stage, nine songs on random stages (`GH3_SKIN_TIMING_RELOADS`) |

`tools/measure-load.py` starts the game in a nested Xwayland display, waits for log lines, and samples the resident memory, the read calls, and the open files of the game process (`samples.csv`). Its `key:` step sends synthetic key events to the game window; Clone Hero took the first key (join) but not the profile choice, so the flows start songs with `--song`, not through the menus. The flows use the picker choices of the game copy (`UserData/GH3Skin/choices.json`): set the characters and instruments that you want to measure.

To measure the cost of MelonLoader and the game without the mod, put a mod that only writes the same `[timing]` lines in `Mods/` instead of `Gh3Skin.dll`. Under Wine, run `tools/windows/run-win.sh` with `GH3_SKIN_TIMING=1` and copy `MelonLoader/Latest.log` to a folder `<flow>-<n>`; `tools/summarize-load.py` reads it the same way.

## Test game

Use an isolated Clone Hero copy, not the copy that you play with. Set `GH3_CLONE_HERO_DIR` for `install.sh`, `run-gh3-skin.sh`, and the capture scripts. The default is `~/games/clonehero-gh3-melon`.

Settings in the test copy that prevent crashes:

- `UserData/Loader.cfg`: `force_offline_generation = true`. Without it, MelonLoader contacts `api.melonloader.com` at each start. (This connection was a suspected crash cause before the hook page guard; see [STATUS.md](STATUS.md#stability).)
- The mod stops MelonLoader's bHaptics client at start (`GH3_SKIN_KEEP_BHAPTICS=1` keeps it).
- Linux only: without an ALSA sequencer seat (headless runs), use `tools/rtmidi-stub.c` as the game's `libRtMidi.so`. The Windows build has its own `RtMidi.dll` and needs no stub (it starts under Wine without one).

The loader log always has one `Unable to create a snapshot of process` error and an `Il2CppInterop` warning. They are not caused by the mod. Any other exception is a fault. On Linux the log must show `Linux hook page guard: on` and `hook page(s) moved to safe pages` (see [STATUS.md](STATUS.md#stability)). Each start leaves a second `clonehero` process; the capture scripts stop it, but stop it yourself after manual runs.

## Screenshots

```bash
MISE_QUIET=1 scripts/capture-preview.sh
```

The script starts a temporary Xwayland display, plays a chart with the command-line bot, and saves frames. It needs Hyprland, Xwayland, `xrandr`, `jq`, `ripgrep`, ImageMagick, and Python.

### Menus and the picker

```bash
flock /tmp/opencode/gh3-record.lock env GH3_CLONE_HERO_DIR=<copy> \
  GH3_SKIN_PICKER_TEST="ready;wait 3;open;apply stage=gh3.dive bassist=gh3.axel;capture /tmp/opencode/picker.png;close;quit" \
  bash scripts/capture-menu.sh
```

The script starts the game in its menus in a temporary Xwayland display and runs the picker steps (`ready` waits for the package import; `apply` also takes `venueTrack=on|off`, `generatedDirection=on|off`, and `reduceFlashing=on|off`). `GH3_SKIN_MENU_INPUT` runs a command on that display while the game runs, for example XTest mouse clicks and keys with `python-xlib`, to test the picker with real input. The picker writes `GH3 picker opened` and `GH3 picker closed: <choices>` to the log; each song writes `GH3 stage choice: <id> (<reason>)`, `GH3 direction: Rock Band VENUE tracks on|off, generated direction on|off, reduce flashing on|off`, and `GH3 band <role>: character <id> (...) with <instrument>`. Clone Hero's settings are in `~/.clonehero`, which all game copies share: add test packages to `~/.clonehero/Custom` only under the record lock, and remove them after the run.

## Video

```bash
MISE_QUIET=1 GH3_SKIN_STAGE=gh3.dive GH3_SKIN_PREVIEW_SONG=/path/to/mycurse \
  GH3_SKIN_VIDEO_SECONDS=40 GH3_SKIN_VIDEO_TRIM=40 GH3_SKIN_VIDEO=$HOME/.cache/gh3-skin-videos/dive.mp4 \
  scripts/record-preview.sh
```

The script records the compositor output in the game window's area with `gpu-screen-recorder`. X11 capture gave incomplete frames. Keep the window visible until the recording ends. The output has a `.log` (the loader log) and a `.json` manifest (DLL and chart hashes, settings, video size). A chart with the title **My Curse** and artist **Killswitch Engage** gets the original GH3 direction from the song library.

Record only one video at a time. Two game instances at the same time can freeze a 16 GB machine. When other agents can record, use a lock:

```bash
flock /tmp/opencode/gh3-record.lock scripts/record-preview.sh
```

`scripts/capture-stage.sh <stage> <output> [song] [times] [seconds]` captures timed frames on Xvfb without a desktop session. It uses software rendering and the local research audio wrapper in `assets/event-audit/`. Use it for frame captures, not for video.

## Windows

One `Gh3Skin.dll` works on Linux and Windows Clone Hero `v1.1.0.6142-final` (see [ARCHITECTURE.md](ARCHITECTURE.md#clone-hero-bindings)).

### Install on Windows

Players use `install-windows.bat` (it starts `install-windows.ps1`) from the Windows release zip; see [PLAYER-GUIDE.md section 1](PLAYER-GUIDE.md#1-install-the-mod) and [Release build](#release-build). `install-windows.ps1` removes `Mods\GH3Skin\assets`, `Packages`, `render`, and `counter-tuning.json` of an earlier install (also the older install in `assets\stages` and `assets\libraries`), then copies the release `Mods` folder. It installs MelonLoader 0.7.3 x64 (download or `-MelonLoaderZip <file>`, SHA-256 checked) and sets `force_offline_generation = true` after the first start (the first start needs the network for the assembly generator). The script also runs with PowerShell 7 on Linux (`pwsh -File install-windows.ps1 -GameDir <folder>`), which is how it was tested; PowerShell 7.4 for Windows does not start under this Wine.

`GH3_CLONE_HERO_DIR=<windows game folder> bash install.sh` installs into a Windows game folder directly (with the test charts, as on Linux). It builds against the interop assemblies of that folder; one DLL works on Linux and Windows.

The loader log is `<game>\MelonLoader\Latest.log`. A good start logs `Linux hook page guard: off (Linux only; this is Microsoft Windows …)`, `Loaded … GH3 skin gameplay sprites`, `GH3 packages: scanning …; textures BC1/BC3; WindowsPlayer Direct3D11`, and, in a song, `Stage audio uses BASS from …\Clone Hero_Data\Plugins\x86_64\bass.dll`. On another version or platform, the log shows `GH3 skin requires Clone Hero …` or `GH3 skin cannot bind to Clone Hero …` and the skin stays off.

### Test under Wine

The template is `~/games/clonehero-win-spike/` (game, Wine prefix, DXVK 3.1.1, `env.sh`; Wine 11.17 from `~/games/gh3-test-tools/wine-root`). Make a reflink copy for each agent and change `WINEPREFIX` in its `env.sh`. `tools/windows/run-win.sh` reads the copy from `GH3_WINDOWS_TEST_DIR`; `tools/windows/env.sh` is the template of `env.sh`. Each run stops the Wine server of the prefix at the end; also stop leftover `Clone Hero.exe` processes, because they keep the record lock. Run under the record lock:

```bash
flock /tmp/opencode/gh3-record.lock env GH3_WINDOWS_TEST_DIR=$HOME/games/clonehero-win-<name> GH3_SKIN_STAGE=gh3.dive GH3_SKIN_TRACE=1 \
  GH3_SKIN_CAPTURE_CLOCK=song GH3_SKIN_CAPTURE_TIMES=2,10,20,30,45,60 'GH3_SKIN_CAPTURE_DIR=Z:\home\<user>\win\frames' \
  bash tools/windows/run-win.sh 240 ~/win/run.log \
  --song 'Z:\home\hedi\.cache\gh3ref\charts\mycurse-native' --player Guitar,Expert \
  -screen-fullscreen 0 -screen-width 1600 -screen-height 900
```

Without a launch option the game uses Direct3D 11 (DXVK); `-force-glcore` selects OpenGL. Windows paths in variables need the `Z:` drive. Put the frames on a disk folder: `/tmp` here is a RAM disk with a quota, and a full `/tmp` also stops Xwayland (`Could not write pid to lock file`). Settings that the test copy needs: `analytics_opt = 3` in `PlayerData\settings.ini` (else the analytics consent dialog stays on top of the menus), and fonts with the name Arial in the prefix (`C:\windows\Fonts\arial.ttf`, for example Liberation Sans with the font name changed). Without Arial, Unity's IMGUI shows no text in the picker. The zip release of the game runs in portable mode: `Custom` and `Songs` are in `<game>\PlayerData`.

Run the same chart on Linux with `scripts/capture-preview.sh` and the same `GH3_SKIN_TRACE` and capture variables. Then compare the frames (same song times) and the `[trace]` lines of the two loader logs.

## Release build

Nothing is published. The release artifacts stay on the local disk, outside the repository.

```bash
MISE_QUIET=1 GH3_RELEASE_OUT=~/.cache/gh3-skin-release GH3_CLONE_HERO_DIR=<game copy> bash scripts/build-release.sh [<git ref>]
```

The script exports the commit (default `HEAD`; uncommitted changes are not included) with `git archive`, adds the generated packages (`assets/packages/*.chstage`, `*.chlib`) and shader bundles (`assets/libraries/gh3-band-v1/render/*.bundle`, `stage-templates.json`), builds the mod against the MelonLoader assemblies of `GH3_CLONE_HERO_DIR` (`install.sh --release`), and writes:

| File | Contents |
| --- | --- |
| `gh3-skin-<version>-linux.zip` | `gh3-skin-<version>-linux/`: `Mods/` (`Gh3Skin.dll`, `GH3Skin/assets/<skin folders>`, `GH3Skin/Packages/`, `GH3Skin/render/`), `install-linux.sh`, `PLAYER-GUIDE.md` with `images/`, `release.json` (version, platform, commit), `SHA256SUMS` (every file) |
| `gh3-skin-<version>-windows.zip` | The same, with `install-windows.ps1` and `install-windows.bat` instead of `install-linux.sh` |
| `chstage_tools-<add-on version>.zip` | The Blender add-on (`blender/build.py`) |
| `SHA256SUMS-<version>` | SHA-256 of the three files |

The version comes from `src/Packages/ModInfo.cs` (the only place). CREDITS, LICENSE, or COPYING files in the repository root go into the zips when they exist. The zips are reproducible: sorted entries, the commit time as the file time, fixed permissions, packages stored without a second compression. Two builds of one commit against the same game copy give the same bytes. A DLL built against another game copy (for example the Windows interop assemblies) has other bytes but works the same way.

`install-linux.sh` checks the game version, installs MelonLoader 0.7.3 (download or `--melonloader-zip <file>`, SHA-256 checked; an installed 0.7.3 is found by the SHA-256 of `MelonLoader.Bootstrap.so`), sets `force_offline_generation = true` after the first start, removes the same old files as `install-windows.ps1`, copies `Mods/`, and writes the launcher `clonehero-melonloader.sh` into the game folder (MelonLoader on Linux loads through `LD_PRELOAD`).

Verify a release from the zips only:

1. In a test game copy, delete `Mods/GH3Skin`, `Mods/Gh3Skin.dll`, and `UserData/GH3Skin`.
2. Unzip the platform zip to a folder outside the repository. Run `sha256sum -c SHA256SUMS` in it.
3. Linux: `./install-linux.sh <game>`. Windows under Wine: `pwsh -NoProfile -File ./install-windows.ps1 -GameDir <game>` (PowerShell 7 for Linux).
4. Cold start (the import of all packages), then a warm start, with `GH3_SKIN_CAPTURE_CLOCK=song` frames on gh3.dive with My Curse; compare them with frames of the current `main` install at the same song times. Open the picker (F6) with `GH3_SKIN_PICKER_TEST`.

## Compare two builds

Record the same chart, venue, and settings with both builds. Then:

```bash
MISE_QUIET=1 uv run --with pillow --with numpy python tools/compare-captures.py before.mp4 after.mp4 comparison \
  --times 6,10,14,18,22,26,30,34,37
```

The tool aligns the two videos and writes full side-by-side frames, amplified differences, and per-region metrics. Bot timing and the video encoder add differences of about 2–5 RGB levels (out of 255) between two runs of the same build. Look at the images as well as the metrics. `tools/compare-sheet.py` builds labeled contact sheets from frame folders.

## Compare with the original GH3

1. Run the original GH3 under Wine with `tools/gh3-reference/start-gh3.sh <display> [trace-regex]`, drive the menus with `keys.py`, and stop it with `stop-gh3.sh`. Work files go to `~/.cache/gh3ref`. Check each menu step with its screenshot. Record with `ffmpeg -f x11grab`. The GH3 bot plays when `bot_play` is 1 in the two `player*_status` defaults of `DATA/PAK/qb.pab.xen`; keep a copy of the original file.
2. `tools/Gh3SongTool chart <song>_song.pak.xen <notes.chart>` writes a Clone Hero chart with GH3's notes and Star Power phrases.
3. Align the videos on the music, or on camera cuts against the exported `gh3-camera` event times.
4. GH3 selects shots at random. Set `GH3_SKIN_CAMERA_SHOT=<shot name>` to force the matching shot in the mod.
5. To play a store character (for example the God of Rock), set `character_id` in the two `player*_status` defaults and unlock the character in `setup_character_tags` (the script runs only for a new save; it is LZSS-compressed in `qb.pab.xen`). Keep a copy of the save folder (`~/Documents/Aspyr/Guitar Hero III/Save`) and put it back after the run. The 2026-10-09 tools are in `~/.cache/rockgod-gh3/tools/`.

### Hair motion

Compare the hair motion with the original GH3 for the same head motion ([PARITY-NOTES.md](PARITY-NOTES.md#hair-dynamics)):

1. Record the original GH3 with a sampler of the live Havok bodies: `tools/research/hair-motion/gh3_session.py <character> <out> <seconds>` under `flock /tmp/opencode/gh3-original.lock flock /tmp/opencode/gh3-record.lock` (environment: `SONG=mycurse` puts My Curse first in the quickplay setlist, `SAVE_IN` a save made with the same patch, `ZONES=z_party:x,y,z` and `FOV` the head camera, `UNLOCK=1` the God of Rock). It writes `gh3.mp4`, `bodies.json`, and `bodies.npz`, and restores the patched files and the save byte-equal.
2. `replay.py <out> <profile> <x> <z> head` writes the head path (`head.bin`). Run the mod solver on it: `GH3_HAIR_BENCH=<profile> GH3_HAIR_BENCH_HEAD=<out>/head.bin GH3_HAIR_BENCH_OUT=<file> dotnet run --project tests/SkinLogic.Tests.csproj -c Release`. Without `GH3_HAIR_BENCH_HEAD` the profile plays My Curse as in the game; `GH3_HAIR_BENCH=band:<role>` runs a band library member; `GH3_HAIR_BENCH_FPS` sets the render rate.
3. `replay.py`, `ring.py`, and `evidence.py` compare the tips of the hair joints in the head frame: movement, speed by frequency band, ringing, and the distance from the GH3 tips. `fam.py` compares two solver builds on all profiles.
4. Body collision: `body_path.py <out> <profile> <x> <z> <file>` writes the paths of all recorded body bones; `GH3_HAIR_BENCH_BODIES=<file>` makes the bench play them (not only the head). `body_colliders.py <profile> <file>` writes the GH3 ragdoll body capsules; `GH3_HAIR_BENCH_BODY=<file>` adds them as colliders of the hair joints (the experiment of [PARITY-NOTES.md](PARITY-NOTES.md#hair-dynamics)).

### Custom chains

Check the custom chains of a character package (PACKAGE-FORMAT section 9.5) before a game run:

```sh
GH3_CHARACTER_HAIR_ASSETS=$PWD/assets GH3_HAIR_BENCH=character:<package.chchar> GH3_HAIR_BENCH_FPS=60 GH3_HAIR_BENCH_SECONDS=120 \
  GH3_HAIR_BENCH_OUT=<file> dotnet tests/bin/Release/net10.0/SkinLogic.Tests.dll
```

The bench imports the package (a `.chchar` zip or a development folder; a character with its own skeleton plays through the retarget of the game), plays My Curse with the animation set of its profile, and prints: finite poses, the fastest joint end (m/s), the solver time for each frame, and the depth of the joint ends in their colliders (p50, p99, maximum). `GH3_HAIR_BENCH_DETAIL=1` prints each joint; `GH3_HAIR_BENCH_MIDDLE=1` also measures the joint middles. The output file has, for each frame, the time, the head pose (x, y, z, qx, qy, qz, qw), and for each custom joint its start and end with physics and without physics (the bind shape on the animated rig; float32). `<file>.json` names the joints and their chain parents. `GH3_HAIR_BENCH_MOTION=<file>` plays a test head motion instead of the song (the head turns about its bind pose; the other bones keep the bind pose). Run it at 60 and at 144 fps: the poses at common times must be equal. In the game, `GH3_SKIN_PROFILE=1` logs `band.hair`, the time of the hair solver of all performers.

Naturalness (`tools/research/hair-motion/naturalness.py`, `uv run --with numpy python`):

1. `naturalness.py motions <dir>` writes three test head motions: `turn` (45° in 0.2 s, hold, back), `headbang` (±20° nods at 3 Hz for 2 s, then hold), and `sway` (±20° side tilts at 1.5 Hz for 2 s, then hold).
2. `naturalness.py measure <bench file> [<motion>]` prints, for each chain, in the head frame: the angle of the chain end from its bind direction seen from the chain start (p50, p95, maximum), the share of frames with the chain end more than 1 cm above the chain start, and the speed of the chain end relative to the head (p50, p95, maximum). With a motion: the hang angle before the motion, and after each stop the settle time (the end stays within 1 cm of its final place) and the number of swings larger than 1 cm.
3. `naturalness.py vrm <bench file> <out> --stiffness S --drag D --gravity G` runs the VRM spring bone (UniVRM VRMSpringBone, at 60 frames a second, no colliders) on the bind shape and the head motion of the bench file, and writes a bench file to measure. Use it as the reference: a natural chain trails, hangs, and settles as the VRM chain does.

Targets for a long twin tail on the GH3 clips (from the VRM reference with stiffness 0.5, drag 0.4, gravity 0.2 on the two Mikus): end angle p95 below 60°, the end never above the chain start, settle within about 1 s after a head move with at most 2 swings.

### GH3 characters

Each GH3 model PAK has the magazine photo of its variant (`Mag_Photo`, a 512 × 512 DDS image in the `.img` entry). Compare a close-up of each character with that photo:

1. Select the characters with `GH3_SKIN_CHARACTER_GUITARIST`, `_BASSIST`, and `_SINGER`.
2. Fix close-up cameras with `GH3_SKIN_DEBUG_VIEW` (one view for each role, `GH3_SKIN_DEBUG_VIEW_SECONDS=4`) and capture one frame in each view with `GH3_SKIN_CAPTURE_CLOCK=song` and `GH3_SKIN_CAPTURE_TIMES`.
3. Check the log line `GH3 band <role>: character <id> (...) with <instrument>[; hair of the guitarist profile (9 joints)]`, and `python3 tools/audit-materials.py` (the `characters` line has no `FALLBACK`).

The GH3 guitarists play on their own rig profiles. Check their motion against the GH3 clips:

1. Run the capture with `GH3_SKIN_TRACE=1` and a guitarist character, for example `GH3_SKIN_CHARACTER_GUITARIST=gh3.judy.outfit1.style1`. The log line `GH3 band guitarist: character <id> (...) on its GH3 profile <profile> with ...; animation set <set>, scale <s>, hair <n> joints | no hair physics, IK <ik>` names the profile.
2. Run `MISE_QUIET=1 uv run --with numpy --with pillow python tools/check-character-trace.py <game>/MelonLoader/Latest.log`. It samples the GH3 clip of each `[trace] band` line on the GH3 skeleton of the profile, at the logged clip time, and prints the largest difference of the four logged bones. Expected: about 0.001 mm and less than 0.02° (q48 and the 30 fps resampling). The tool needs `assets/libraries/gh3-characters-v1` and the GH3 data.
3. Look at the frames: the stance and the strum of the family, the fret hand on the neck, hand fire after the 100-note streak, and the size (Judy Nails 0.9).
4. The log must not have `GH3 band <role>: hair joint <bone> moves at <n> m/s`. The mod writes this warning one time for each performer when a hair joint moves faster than 30 m/s (a ragdoll that does not fit the rig). With `GH3_SKIN_TRACE=1`, `hairPeak=` on each `[trace] band` line gives the fastest hair joint until that time.

On 2026-10-09 (38 runs on gh3.dive, two guitarist characters and one singer in each run) every variant had a close-up in one role, each character family as guitarist and as bassist, and the colors and outfits agreed with the photos.

### Characters on their own skeleton

1. Select a character with `skeleton` (PACKAGE-FORMAT 9.6) for the guitarist, and run the capture with `GH3_SKIN_TRACE=1`. The log line `GH3 band guitarist: character <id> (...) on its own skeleton (<n> joints, <n> humanoid roles), retargeted from ...; hip scale ..., torso scale ..., instrument scale ...` names the source. Each `[trace] band` line ends with `retarget handMiss=<cm> move=<cm>`: `handMiss` must stay below 0.1 cm.
2. Run the same song times with the GH3 character of the profile (for example `gh3.midori.outfit1.style1`) and put the frames side by side: the stance, the legs, the head, the guitar angle, and the hands must agree. Fix the camera with `GH3_SKIN_DEBUG_VIEW` (full body, and a close view of the guitar for the fret and strum hands).
3. Measure the proportions: joint ratios of the package GLB (`pipeline/package/skeleton.py` `glb_skeleton`, `world_positions`) against the source model.
4. Twist: retarget all clips of the set with `skeleton.Retarget` (equal to the game, `tests/fixtures/retarget-case.json`) and measure the twist between adjacent arm bones about the limb axis.
5. Face: a singer on a chart with vocals (`GH3_SKIN_CHARACTER_SINGER`), with `GH3_SKIN_DEBUG_VIEW_HEAD=local` and `GH3_SKIN_DEBUG_VIEW_ROLE=singer` (for example the view `0.03,3,0,0.03,0,0,9`). The log has `GH3 band singer: face blend shapes with lip sync from the sung notes`.
6. GH3 characters must not change: `tools/check-character-trace.py` on a GH3 guitarist run, and screenshots of the default band against `main`.
7. `GH3_SKIN_PROFILE=1` logs `band.retarget`.
8. The rig corpus (the automatic map on many rigs): `tools/rig-corpus/fetch.sh`, then `RIG_CORPUS_DIR=<models> MMD_TOOLS_DIR=<MMD Tools extension folder, optional> flock /tmp/opencode/gh3-heavy.lock bash tools/rig-corpus/run.sh <out> --check` (add `--export --render` for packages and Workbench renders). `<out>/results.json` has, for each model, the score against its hand-made map, the notes, Check Clips, and Validate. Every model without `expect` in `corpus.json` must map with 0 fixes and no Check Clips error. Put some exported packages in `Mods/GH3Skin/Packages/` and capture them as guitarist (`GH3_SKIN_CHARACTER_GUITARIST=corpus.<id>`, view `-0.58,1.3,-1.6,-0.58,1.0,1.31,45` on gh3.dive), as singer, and as drummer (`GH3_SKIN_DEBUG_VIEW_HEAD=1`, `GH3_SKIN_DEBUG_VIEW_ROLE=drummer`, view `0.2,1.8,1.9,0,1.5,3,16`).

### Instruments and highways

1. Select instruments with `GH3_SKIN_INSTRUMENT_GUITARIST` and `_BASSIST`, with and without characters. The log has `GH3 band <role>: ... with instrument <id> (<name>; the player's choice)` and `GH3 highway: <source> (the player is the <role>)`.
2. Fix close-up cameras on the guitarist and the bassist (`GH3_SKIN_DEBUG_VIEW`, two views, `GH3_SKIN_DEBUG_VIEW_SECONDS=4`) and look at the hands: the fret hand on the neck and the strum hand at the strings, for different body shapes (Explorer, Flying V, Les Paul, SG, the GH3 designs, basses).
3. For the highway, capture the board (song times 10–30 s) with the default band, with characters, and with `GH3_SKIN_PREVIEW_PLAYER=Bass,Expert` and a chart with a bass part. Compare with the GH3 images in `assets/instruments/highways/`. The default run must look as `main` (the board differs between two runs only by timing).

## Runtime variables

These variables are for checks and captures. Normal play needs none of them.

| Variable | Purpose |
| --- | --- |
| `GH3_SKIN_STAGE` | Stage ID of an installed package, for example `gh3.hell` or `example.studio`; it overrides the picker and the song's stage. `random` or `song` act as the picker items. `off` shows the stock background. Without the variable, the picker choice (`UserData/GH3Skin/choices.json`, default `song`: the song's stage, else `gh3.budokan`) applies |
| `GH3_SKIN_CHARACTER_GUITARIST`, `_BASSIST`, `_DRUMMER`, `_SINGER` | Character package ID for a band role (or `default`); overrides the picker |
| `GH3_SKIN_INSTRUMENT_GUITARIST`, `_BASSIST` | Instrument package ID (a guitar for the guitarist, a bass for the bassist) or `default`; overrides the picker |
| `GH3_SKIN_PICKER_TEST` | Picker steps in the menus without a mouse (`src/Menu/PickerTest.cs`), for `scripts/capture-menu.sh`. `apply` takes `stage=`, `<role>=`, `guitar=`, `bass=`, and the direction options |
| `GH3_SKIN_PREVIEW_PLAYER` | The `--player` value of `run-gh3-skin.sh --preview`, default `Guitar,Expert`; `Bass,Expert` plays the bass part (the chart needs one) and makes the player the bassist for the highway |
| `GH3_SKIN_CAMERA` | `auto` (default), `fixed`, or a shot group: `wide`, `singer`, `guitarist`, `bassist`, `drummer`, `orbit` |
| `GH3_SKIN_CAMERA_SHOT` | Forces one shot name, to match a GH3 reference frame |
| `GH3_SKIN_VENUE_TRACK=off` | Ignores Rock Band `VENUE` tracks. Overrides the picker option; another value turns the tracks on |
| `GH3_SKIN_GENERATED_DIRECTION=off` | No cues from chart sections; a chart without direction keeps the static defaults (as before 2026-10-09). Overrides the picker option; another value turns it on |
| `GH3_SKIN_REDUCE_FLASHING=on` | Weaker, rarer Rock Band flares and slower strobes. Overrides the picker option; `off` or `0` turns it off |
| `GH3_SKIN_PREVIEW_SONG` | Chart folder for `run-gh3-skin.sh --preview` and the capture scripts |
| `GH3_SKIN_PREVIEW_WIDTH`, `GH3_SKIN_PREVIEW_HEIGHT` | Window size; default 1600 × 900 |
| `GH3_SKIN_CAPTURE_AFTER`, `GH3_SKIN_CAPTURE_TIMES` | Screenshot times |
| `GH3_SKIN_CAPTURE_CLOCK=song` | Screenshot times use the song clock, not the scene clock. Negative times capture GH3's intro before the song |
| `GH3_SKIN_CAPTURE_DIR`, `GH3_SKIN_SCREENSHOT` | Screenshot output |
| `GH3_SKIN_VIDEO`, `GH3_SKIN_VIDEO_SECONDS`, `GH3_SKIN_VIDEO_TRIM`, `GH3_SKIN_VIDEO_FPS` | Video output, length, and rate |
| `GH3_SKIN_VIDEO_X`, `GH3_SKIN_VIDEO_Y` | Window position; keep the whole window on the screen |
| `GH3_SKIN_VIDEO_AUDIO` | `game` records the game's audio, or give a `gpu-screen-recorder` audio source |
| `GH3_SKIN_PREVIEW_POWER`, `GH3_SKIN_PREVIEW_ROCK` | Display-only charge and rock values for screenshots |
| `GH3_SKIN_FORCE_POWER`, `GH3_SKIN_FORCE_ROCK` | Display-only charge and rock values, 0 to 1 |
| `GH3_SKIN_FORCE_POWER_AT_COMBO` | Applies the forced charge from this streak |
| `GH3_SKIN_FORCE_COMBO`, `GH3_SKIN_FORCE_COMBO_BREAK` | Adds to the shown streak; breaks it at a value. Tests counters of five or more digits |
| `GH3_SKIN_FORCE_STAR_POWER_ACTIVE=1` | Display-only active Star Power (cyan gems, hit colors) |
| `GH3_SKIN_FORCE_WHAMMY=<Hz>` | Sends a cosine whammy to Clone Hero's whammy input. Tests the sustain bend |
| `GH3_SKIN_KEEP_STOCK_GEMS=1`, `GH3_SKIN_KEEP_STOCK_FLAMES=1` | Keeps Clone Hero's gems or hit effects, for comparisons |
| `GH3_SKIN_DEBUG_VIEW="x,y,z,tx,ty,tz,fov[;...]"` | Fixes the stage camera and hides the highway; `GH3_SKIN_DEBUG_VIEW_SECONDS` cycles the views. With `GH3_SKIN_DEBUG_VIEW_HEAD=1` the camera stays at the view position and looks at the guitarist's head (close-ups of hair and face). With `GH3_SKIN_DEBUG_VIEW_HEAD=local` the position and the target are in the frame of the guitarist's head bone, and the head's +X axis is the up direction: the camera follows the head and shows the face at a fixed angle (for example `0.07,3,0,0.07,0,0,4.5` is front-on from 3 m, with a 4.5° field of view). `GH3_SKIN_DEBUG_VIEW_ROLE` selects the band member of the head views (default `guitarist`; for example `singer` for lip sync). With `GH3_SKIN_DEBUG_VIEW_HEAD=through` the camera is `x` metres behind the guitarist's head on the line from the bassist's head, `y` metres higher and `z` metres to the side, and looks at the bassist's head (draw order between band members) |
| `GH3_SKIN_DEBUG_HAIR_LAYER=mark` | Draws the `overHair` features of characters in one flat color for each character (magenta, green, yellow) without lighting, so a script can count their pixels. `off` does the same and ignores the hair layers (the depth test only) |
| `GH3_SKIN_DEBUG_SHOW=<id,...>` | Keeps these GH3 objects created |
| `GH3_SKIN_TIMING=1` | Logs the load phases (`[timing]`, [Load times](#load-times)); `tools/summarize-load.py` reads the logs |
| `GH3_SKIN_TIMING_RELOADS=<n>` | With `GH3_SKIN_TIMING`: loads the gameplay scene again 4 s after song time 0, *n* times |
| `GH3_SKIN_PROFILE=1` | Logs milliseconds, managed kilobytes, and gen-0 collections per subsystem each second; `tools/summarize-profile.py` reads the logs |
| `GH3_SKIN_KEEP_BHAPTICS=1` | Keeps MelonLoader's bHaptics client |
| `GH3_SKIN_HOOK_GUARD=off` | Turns off the Linux hook page guard ([STATUS.md](STATUS.md#stability)). Only for crash tests |
| `GH3_SKIN_LIPSYNC=off` | No singer lip sync on charts without GH3 facial clips ([CUSTOM-CONTENT.md](CUSTOM-CONTENT.md#lip-sync-from-vocals)) |
| `GH3_SKIN_TRACE=1` | Logs score, streak, multiplier, rock, charge, Star Power, note hits, stage note events, hit-flame lanes, pause, and stage audio voices twice per song second (`[trace]`). Compares platforms. Also logs `[trace] band` for the guitarist and the bassist: profile, animation set, stance, body clip, clip time, scale, and the clip pose of four bones (`tools/check-character-trace.py`) |
| `GH3_SKIN_TRACE_CHARGE=<0–1>` | With `GH3_SKIN_TRACE`: sets the native Star Power charge once at song time 20 s. This changes the game state; use it only for checks |

Forced values change only the display. The command-line bot does not earn Star Power. Do not use forced values to check meter behavior: they cannot show charge, activation, drain, or needle motion. Tests of earned Star Power need an instrumented build that allows the bot's native award routine, and the report must say so.

## Rules for results

- A report names the DLL (SHA-256) and the assets that it used.
- Keep failed runs. A passing report followed by a crash is a failed run. Exit 0 without a report is also a failed run.
- A small mean error is not pixel identity. Encoded video cannot prove pixel identity.
- Before you use a stock or old-build capture as a reference, look at it. Matching hashes did not catch a bad capture in the past.
