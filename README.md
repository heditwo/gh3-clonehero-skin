# GH3 gameplay skin for Clone Hero

This mod makes Clone Hero look and play like Guitar Hero III. It replaces the highway, the gems, the sustains, the hit effects, and the HUD with the GH3 art and animation. Behind the highway, it shows the GH3 venues with the band, the crowd, the cameras, the lights, and the effects. Songs from GH3 get their original direction, and other charts get direction from their GH or Rock Band venue tracks or from their sections. Players add stages and characters as single files, and creators make them in Blender.

![Mitch's Moose Lounge behind the GH3 highway](docs/images/gameplay-dive.png)

| The F6 picker | A stage and a character made with the Blender add-on |
| --- | --- |
| ![The stage and band picker](docs/images/picker.png) | ![The example club and the example guitarist](docs/images/creator/game-example.png) |

Status: release readiness. Nothing is published yet.

## For players

- Clone Hero **v1.1.0.6142-final** (64-bit) with MelonLoader 0.7.3. The install script installs MelonLoader.
- Install from the release zip for your system: **Linux** ([PLAYER-GUIDE.md, On Linux](docs/PLAYER-GUIDE.md#on-linux)) or **Windows** ([PLAYER-GUIDE.md, On Windows](docs/PLAYER-GUIDE.md#on-windows)).
- **macOS**: not supported yet ([spike/macos.md](docs/spike/macos.md)).
- In the menus, push **F6** to select the stage and the band ([PLAYER-GUIDE.md](docs/PLAYER-GUIDE.md)).
- Add stages to `Custom/Stages` and characters to `Custom/Characters` ([PLAYER-GUIDE.md section 5](docs/PLAYER-GUIDE.md#5-add-stages-and-characters)).

Build and install from the repository (it needs your GH3 PC files and the tools of [PIPELINE.md](docs/PIPELINE.md)):

```bash
MISE_QUIET=1 scripts/regenerate.sh --clean
MISE_QUIET=1 GH3_CLONE_HERO_DIR=<game folder> bash install.sh
MISE_QUIET=1 GH3_CLONE_HERO_DIR=<game folder> bash run-gh3-skin.sh
```

`scripts/build-release.sh` makes the release zips for Linux and Windows and the Blender add-on zip ([VERIFICATION.md](docs/VERIFICATION.md#release-build)).

Limits: one five-fret guitar player; landscape screens only. See [STATUS.md](docs/STATUS.md).

## For creators

- [CREATOR-GUIDE.md](docs/CREATOR-GUIDE.md): make a stage and a character with the CH Stage Tools add-on for Blender, edit a GH3 venue, and add direction to charts.
- [examples/](examples/): an example stage and character (`.blend` and package, CC0-1.0).
- References: [BLENDER-ADDON.md](docs/BLENDER-ADDON.md), [PACKAGE-FORMAT.md](docs/PACKAGE-FORMAT.md), [CUSTOM-CONTENT.md](docs/CUSTOM-CONTENT.md).

## For developers

| Document | Contents |
| --- | --- |
| [ARCHITECTURE.md](docs/ARCHITECTURE.md) | Runtime modules, pipeline, asset formats, data flow |
| [PIPELINE.md](docs/PIPELINE.md) | Make the assets and packages from the GH3 files |
| [VERIFICATION.md](docs/VERIFICATION.md) | Tests, screenshots, recordings, runtime variables |
| [STATUS.md](docs/STATUS.md) | Current status and known differences from GH3 |
| [PARITY-NOTES.md](docs/PARITY-NOTES.md) | GH3 facts: addresses, constants, scripts, formulas |
| [PACKAGING-PLAN.md](docs/PACKAGING-PLAN.md) | Package plan and decisions |

Run all tests with `MISE_QUIET=1 scripts/test.sh`.

## Credits

The tap gems come from BetterGH3, and the cyan Star Power phrase gems and the highway rails come from FastGH3. [CREDITS.md](CREDITS.md) lists all third-party work and tools.

This is a non-commercial fan project. It is not affiliated with Activision, Neversoft, RedOctane, Harmonix, or the Clone Hero developers. The GH3 content comes from the owner's copy of the game. The repository has the prepared HUD and gem textures (`assets/`), but not the GH3 game files; the pipeline makes the venues, the band, and the other stage content from the GH3 files.
