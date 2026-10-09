# Verification and recording

## Automated checks

```bash
MISE_QUIET=1 scripts/test.sh
```

The script runs the C# logic tests (`tests/SkinLogic.Tests.csproj`, .NET 10 SDK), the Python pipeline tests (`tests/test_*.py`), and a syntax check of the shell scripts. Also build the mod with zero warnings:

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
| `GH3_SCRIPT_SOURCE`, `GH3_STAGE_SCRIPT_REPORT` | Decoded GH3 scripts and a report path |
| `GH3_NATIVE_*_ORACLE` | Reference JSON from the native emulation tools in the local `assets/event-audit/behavior-work/` folder (not in Git) |
| `GH3_PACKAGE_ASSETS=$PWD/assets` | Imports `assets/packages/gh3-band-v1.chlib` with the mod's importer and compares all 1,240 decoded q48 clips with the `GH3CLP01` clips of `assets/libraries` (bound 0.0075°). Needs about 1 GB in `TMPDIR`; set `TMPDIR` to a disk folder |
| `GH3_BENCH=<venue>`, `GH3_BENCH_SONG=<chart folder>` | Runs a venue's scripts and particles against a chart outside the game (`tests/ScriptBench.cs`); `GH3_BENCH_ALLOC=1` adds allocation samples |

After a change to the venue export or the shaders, run `python3 tools/audit-materials.py`. All used materials must draw with their GH3 template.

After a change to the shaders, also check Direct3D 11. The Windows build of Clone Hero runs under Wine with DXVK (see `tools/spike-d3d11/run-win.sh` and [the spike report](spike/d3d11.md)). Build the mod against the Windows interop assemblies (`GameRoot` of the Windows copy), and capture the same song times with `-force-d3d11` and `-force-glcore` (`GH3_SKIN_CAPTURE_CLOCK=song`). Compare the frames with the Linux frames. A stage with no program for the graphics API logs `GH3 stage is off: ... has no program for the <API> graphics API` and does not draw; `-force-vulkan` shows this message.

After a change to `src/Packages/` or to the package converter, run the cache comparison in [PIPELINE.md](PIPELINE.md#check-the-result) (`pipeline.package.cachecheck`).

### Package import in the game

The log shows the package steps with the prefix `GH3 packages:`:

| Line | Meaning |
| --- | --- |
| `found N usable package(s)` | The scan; one `skipped <path>: <reason>` warning for each bad package |
| `imported <id> <version> in X s (…)` | Cold cache: the import time, the checked bytes, the textures, and the managed heap of one package |
| `<id> ready from the cache` or `all packages ready from the cache in X ms` | Warm cache |
| `GH3 stage source: package …` or `… (older install)` | Where the stage came from |
| `GH3 stage load time: X ms on the main thread (…)` | The stage load frame: cache texture uploads and the clip prefetch wait |
| `GH3 clips: clip … was not prefetched` | A clip decoded on the main thread during play (a prefetch gap) |

To test a cold import, delete `UserData/GH3Skin/Cache/` in the test game. To compare with the older install in the same build, set `GH3_SKIN_PACKAGES=off`.

## Test game

Use an isolated Clone Hero copy, not the copy that you play with. Set `GH3_CLONE_HERO_DIR` for `install.sh`, `run-gh3-skin.sh`, and the capture scripts. The default is `~/games/clonehero-gh3-melon`.

Settings in the test copy that prevent crashes:

- `UserData/Loader.cfg`: `force_offline_generation = true`. Without it, MelonLoader contacts `api.melonloader.com` at each start, and the open HTTPS connection can crash the CLR later.
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

The script starts the game in its menus in a temporary Xwayland display and runs the picker steps (`ready` waits for the package import; `apply` also takes `venueTrack=on|off` and `generatedDirection=on|off`). `GH3_SKIN_MENU_INPUT` runs a command on that display while the game runs, for example XTest mouse clicks and keys with `python-xlib`, to test the picker with real input. The picker writes `GH3 picker opened` and `GH3 picker closed: <choices>` to the log; each song writes `GH3 stage choice: <id> (<reason>)`, `GH3 direction: Rock Band VENUE tracks on|off, generated direction on|off`, and `GH3 band <role>: character <id> (...) with <instrument>`. Clone Hero's settings are in `~/.clonehero`, which all game copies share: add test packages to `~/.clonehero/Custom` only under the record lock, and remove them after the run.

## Video

```bash
MISE_QUIET=1 GH3_SKIN_STAGE=gh3.dive GH3_SKIN_PREVIEW_SONG=/path/to/mycurse \
  GH3_SKIN_VIDEO_SECONDS=40 GH3_SKIN_VIDEO_TRIM=40 GH3_SKIN_VIDEO=/tmp/opencode/dive.mp4 \
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

1. Install Clone Hero 1.1.0.6142 for Windows (`CloneHero-Win-x64.zip`).
2. Extract MelonLoader 0.7.3 x64 (`MelonLoader.x64.zip`) into the game folder, next to `Clone Hero.exe`. The folder then has `version.dll` and `MelonLoader\`.
3. Start the game once and close it. MelonLoader generates `MelonLoader\Il2CppAssemblies\` (the first start needs the network). Then set `force_offline_generation = true` in `UserData\Loader.cfg` (see [Test game](#test-game)).
4. Copy the files to the same places as on Linux:
   - `Gh3Skin.dll` to `<game>\Mods\Gh3Skin.dll`.
   - The skin folders of `assets` (`gems\`, `sustains\`, `highway\`, `hud\`, `fonts\`, `hit-effects\`) and `stages\`, `libraries\` to `<game>\Mods\GH3Skin\assets\`, with the same layout. Remove the files of an earlier install first: the files directly in `Mods\GH3Skin\assets\` and `Mods\GH3Skin\counter-tuning.json`.
5. Start Clone Hero with the launch option `-force-glcore`. Without it, Clone Hero uses Direct3D 11, and the stage is pink because the shader bundles have OpenGL programs only ([spike](spike/d3d11.md)).

From Linux, `GH3_CLONE_HERO_DIR=<windows game folder> bash install.sh` does step 4. It builds against the Windows interop assemblies of that folder; the result works on Linux too.

The loader log is `<game>\MelonLoader\Latest.log`. A good start logs `Loaded … GH3 skin gameplay sprites` and, in a song, `Stage audio uses BASS from …\Clone Hero_Data\Plugins\x86_64\bass.dll`. On another version or platform, the log shows `GH3 skin requires Clone Hero …` or `GH3 skin cannot bind to Clone Hero …` and the skin stays off.

### Test under Wine

The test setup is in `~/games/clonehero-win-spike/` (game, Wine prefix, DXVK 3.1.1, `env.sh`, `run-win.sh`; Wine 11.17 from `~/games/gh3-test-tools/wine-root`). Make a reflink copy for each agent and change `here=` in `run-win.sh` and `WINEPREFIX` in `env.sh`. Run under the record lock:

```bash
flock /tmp/opencode/gh3-record.lock env GH3_SKIN_STAGE=gh3.dive GH3_SKIN_TRACE=1 \
  GH3_SKIN_CAPTURE_CLOCK=song GH3_SKIN_CAPTURE_TIMES=2,10,20,30,45,60 'GH3_SKIN_CAPTURE_DIR=Z:\tmp\opencode\win\frames' \
  bash ~/games/clonehero-win-<name>/run-win.sh 240 /tmp/opencode/win/run.log \
  --song 'Z:\home\hedi\.cache\gh3ref\charts\mycurse-native' --player Guitar,Expert \
  -screen-fullscreen 0 -screen-width 1600 -screen-height 900 -force-glcore
```

Windows paths in variables need the `Z:` drive. Run the same chart on Linux with `scripts/capture-preview.sh` and the same `GH3_SKIN_TRACE` and capture variables. Then compare the `[trace]` lines of the two loader logs at the same song times.

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

## Runtime variables

These variables are for checks and captures. Normal play needs none of them.

| Variable | Purpose |
| --- | --- |
| `GH3_SKIN_STAGE` | Stage ID (package or older pack), for example `gh3.hell` or `example.studio`; it overrides the picker and the song's stage. `random` or `song` act as the picker items. `off` shows the stock background. Without the variable, the picker choice (`UserData/GH3Skin/choices.json`, default `song`: the song's stage, else `gh3.budokan`) applies |
| `GH3_SKIN_CHARACTER_GUITARIST`, `_BASSIST`, `_DRUMMER`, `_SINGER` | Character package ID for a band role (or `default`); overrides the picker |
| `GH3_SKIN_PICKER_TEST` | Picker steps in the menus without a mouse (`src/Menu/PickerTest.cs`), for `scripts/capture-menu.sh` |
| `GH3_SKIN_PACKAGES=off` | Ignores packages; the stage loads from the older install (`assets/stages`, `assets/libraries`) |
| `GH3_SKIN_CAMERA` | `auto` (default), `fixed`, or a shot group: `wide`, `singer`, `guitarist`, `bassist`, `drummer`, `orbit` |
| `GH3_SKIN_CAMERA_SHOT` | Forces one shot name, to match a GH3 reference frame |
| `GH3_SKIN_VENUE_TRACK=off` | Ignores Rock Band `VENUE` tracks. Overrides the picker option; another value turns the tracks on |
| `GH3_SKIN_GENERATED_DIRECTION=off` | No cues from chart sections; a chart without direction keeps the static defaults (as before 2026-10-09). Overrides the picker option; another value turns it on |
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
| `GH3_SKIN_DEBUG_VIEW="x,y,z,tx,ty,tz,fov[;...]"` | Fixes the stage camera and hides the highway; `GH3_SKIN_DEBUG_VIEW_SECONDS` cycles the views |
| `GH3_SKIN_DEBUG_SHOW=<id,...>` | Keeps these GH3 objects created |
| `GH3_SKIN_PROFILE=1` | Logs milliseconds, managed kilobytes, and gen-0 collections per subsystem each second; `tools/summarize-profile.py` reads the logs |
| `GH3_SKIN_KEEP_BHAPTICS=1` | Keeps MelonLoader's bHaptics client |
| `GH3_SKIN_TRACE=1` | Logs score, streak, multiplier, rock, charge, Star Power, note hits, stage note events, hit-flame lanes, pause, and stage audio voices twice per song second (`[trace]`). Compares platforms |
| `GH3_SKIN_TRACE_CHARGE=<0–1>` | With `GH3_SKIN_TRACE`: sets the native Star Power charge once at song time 20 s. This changes the game state; use it only for checks |

Forced values change only the display. The command-line bot does not earn Star Power. Do not use forced values to check meter behavior: they cannot show charge, activation, drain, or needle motion. Tests of earned Star Power need an instrumented build that allows the bot's native award routine, and the report must say so.

## Rules for results

- A report names the DLL (SHA-256) and the assets that it used.
- Keep failed runs. A passing report followed by a crash is a failed run. Exit 0 without a report is also a failed run.
- A small mean error is not pixel identity. Encoded video cannot prove pixel identity.
- Before you use a stock or old-build capture as a reference, look at it. Matching hashes did not catch a bad capture in the past.
