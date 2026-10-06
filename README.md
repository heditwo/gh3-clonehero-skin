# GH3 gameplay skin for Clone Hero

This local mod uses art from the owner's BetterGH3 PC copy. It targets Linux Clone Hero **v1.1.0.6142-final** with **MelonLoader 0.7.3**. The local build is `0.8.106`.

## Install and run

Requirements: .NET 6 runtime, a .NET SDK that can build `net6.0`, and an isolated Clone Hero copy with generated MelonLoader assemblies. The logic test project needs the .NET 10 SDK. Set `GH3_CLONE_HERO_DIR` if the isolated game is not at `~/games/clonehero-gh3-melon`.

```bash
MISE_QUIET=1 bash ./install.sh
MISE_QUIET=1 bash ./run-gh3-skin.sh
```

The installer builds the mod, copies the prepared assets and tuning file, and installs all bundled test charts. Preview scripts use the charts in this repository. A direct build also accepts `dotnet build src/Gh3Skin.csproj -p:GameRoot=/path/to/game`.

## Current behavior

- Black highway, GH3 rails, regular gems, glowing HOPO gems, animated phrase gems, BetterGH3 tap gems, and hit effects.
- GH3 moving beat lines at highway level, with the original bar, beat, and half-beat textures. Clone Hero supplies the fret buttons, strings, button animations, and open-note art.
- Active Star Power changes regular notes, HOPOs, and taps to their cyan art. Overlapping phrase notes use the corresponding cyan star textures from FastGH3. Stars animate at GH3's 30 frames per second. Lane colors return when Star Power ends. See [gem selection and assets](docs/GEMS.md).
- Score, streak, rock meter, charge, active Star Power, and multiplier come from the game. Active Star Power uses the blue 2×, 4×, 6×, or 8× art.
- Score digits align to a fixed right edge inside the amp. Long scores use narrower digits to stay inside the display.
- The rock needle follows the current health value directly. Its background blends from yellow toward red or green with the needle's distance from the center. The indicator lamps use Clone Hero's one-third and two-thirds health thresholds at the GH3 dial boundaries.
- The extra Star Power bulbs slide out in sequence when charge reaches 50%. The meter lifts, tilts, and settles with the GH3 entrance motion. See [the animation measurements](docs/BULB-ANIMATION.md).
- Active Star Power uses GH3's animated rail lightning and cyan glow. The stock highway effect is hidden. See [the source and checks](docs/STAR-POWER-HIGHWAY.md).
- The ready banner runs for 126 frames at 60 fps when charge reaches 50%. Activation hides it. It can run again after the charge drops and refills.
- The note-streak counter appears at 25 notes. Its measured entrance and reel animation use `counter-tuning.json`. Gray reel backgrounds extend behind the lower frame, with the numbers drawn separately.
- One five-fret guitar player is supported. Other player layouts use the stock display. Other game versions are rejected before obfuscated fields are read.
- HUD images fit a centered 16:9 area. Rails and ready effects follow the actual game camera area and field of view. Portrait layout is not supported.
- The original GH3 Kaiju Megadome venue renders behind the highway with an animated band and crowd, moving cameras, stage lights, and song-triggered pyro, sparks, fireworks, and smoke. The band uses GH3's default male singer, Axel, the bassist, and the drummer, with their instruments. Original song cues drive camera selection, stances, strums, drum strokes, and facial clips. Lighting uses the original venue snapshots, light groups, and fades; blackout states keep fill lights on. The crowd uses six original models and 12 cheering clips. See [3D stages and song cues](docs/STAGES.md) for asset preparation, supported cues, and translation limits.

The game-state field map is in [docs/GAME-BINDINGS.md](docs/GAME-BINDINGS.md). Runtime check results are in [docs/VERIFICATION.md](docs/VERIFICATION.md). Later corrections are in [docs/STRIKE-LINES.md](docs/STRIKE-LINES.md) and [docs/METERS.md](docs/METERS.md). Previous GH3 comparisons are in [docs/VISUAL-HISTORY.md](docs/VISUAL-HISTORY.md).

## Checks and captures

```bash
MISE_QUIET=1 dotnet run --project tests/SkinLogic.Tests.csproj -c Release
MISE_QUIET=1 uv run --with pillow --with numpy --with 'shapely>=2.1' python -m unittest discover -s tests -p 'test_*.py'
MISE_QUIET=1 bash ./capture-preview.sh
MISE_QUIET=1 bash ./record-preview.sh
MISE_QUIET=1 bash ./compare-gems.sh
```

Captures need Hyprland, Xwayland, `xrandr`, `jq`, `ripgrep`, FFmpeg, ImageMagick, Python, and `uv`. Video capture also needs `gpu-screen-recorder`. Each script uses a temporary Xwayland display and stops its game and display processes when it ends. Videos have no audio.

Video capture reads the compositor output in the game window's region. This avoids incomplete frames from X11 capture. Keep the game window visible and in place until recording ends.

| Setting | Purpose |
| --- | --- |
| `GH3_SKIN_PREVIEW_SONG` | Chart folder; defaults to `charts/GH3 Combo Preview` |
| `GH3_SKIN_PREVIEW_WIDTH`, `GH3_SKIN_PREVIEW_HEIGHT` | Capture size; defaults to 1600 × 900 |
| `GH3_SKIN_CAPTURE_AFTER` | First screenshot time; defaults to 8 seconds in the shell script |
| `GH3_SKIN_CAPTURE_TIMES` | Comma-separated times since the gameplay scene loaded |
| `GH3_SKIN_CAPTURE_DIR` | Persistent frame folder; must contain no old `frame-*.png` files |
| `GH3_SKIN_SCREENSHOT` | Selected screenshot path; defaults to `preview.png` in the shell script |
| `GH3_SKIN_VIDEO` | Video output path |
| `GH3_SKIN_VIDEO_SECONDS`, `GH3_SKIN_VIDEO_TRIM` | Recording and output durations |
| `GH3_SKIN_VIDEO_FPS` | Recording rate; defaults to 60 |
| `GH3_SKIN_VIDEO_X`, `GH3_SKIN_VIDEO_Y` | Capture window position; defaults to 32, 64. Keep the whole window inside the screen. |
| `GH3_SKIN_FORCE_POWER`, `GH3_SKIN_FORCE_ROCK` | Visual-only values from 0 to 1 |
| `GH3_SKIN_FORCE_POWER_AT_COMBO` | Streak threshold for the visual charge override |
| `GH3_SKIN_KEEP_STOCK_GEMS`, `GH3_SKIN_KEEP_STOCK_FLAMES` | Set to `1` for stock art comparisons |
| `GH3_SKIN_STAGE` | Prepared venue folder; defaults to `budokan`. Use `off` for the stock background. |
| `GH3_SKIN_CAMERA` | Defaults to `auto`. Use `fixed` for the original stage view, or a shot name from `docs/STAGES.md`. |

`GH3_SKIN_PREVIEW_POWER` and `GH3_SKIN_PREVIEW_ROCK` apply only when screenshot capture is enabled. Forced values change the display only. The command-line bot does not earn Star Power in the unmodified game engine.

Leave all rock and power overrides unset when checking meter behavior. A fixed-value art preview cannot show charge earning, activation, drain, or needle movement. See [the native-state meter test](docs/METERS.md).

Use `GH3_SKIN_PREVIEW_WIDTH=2560 GH3_SKIN_PREVIEW_HEIGHT=1440` for 1440p. Each video gets a `.log` and `.json` file with the loader log, binary hash, chart hashes, capture settings, and actual video dimensions. `compare-gems.sh` keeps all 18 source frames in a new run folder.

For a before/after comparison, record the same chart and settings at 60 fps with both builds, then run:

```bash
MISE_QUIET=1 uv run --with pillow --with numpy python compare-captures.py before.mp4 after.mp4 comparison
```

Use `--times 2,4,6,8,10` for a shorter video. The tool aligns frames and writes complete side-by-side images, amplified differences, and per-region metrics. Encoded captures and separate game clocks can differ; inspect the images with the metrics.

## Assets and code

`SkinMod` manages scene resets. `GameplayScene` caches the player and score manager. `GameplaySpriteRenderer` caches pooled objects and maps each note to the nearest local fret. `GameplayHud` assembles the components in `src/Hud/`; each component owns its local layout and animation. See [the component structure](docs/HUD-COMPONENTS.md). `SkinAssets` keeps GPU textures across scenes and releases their readable CPU copies. `GameplayStage` owns the stage camera and scene resources. `StageTimeline` evaluates cues against the song clock.

Prepared assets are in `assets/`. To regenerate them, use `uv run --with pillow prepare-assets.py`. To regenerate the ready animation, put the owner's `gh3-long-star-power-1600x900.mp4` in the project root and run `uv run --with pillow --with numpy prepare-ready-animation.py`. These steps need the local source art and recordings. `counter-lab.py --measure` checks reel placement against the stored reference frames.
