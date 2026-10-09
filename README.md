# GH3 gameplay skin for Clone Hero

This mod makes Clone Hero look and play like Guitar Hero III. It replaces the highway, gems, sustains, hit effects, and HUD with the GH3 art and animation. It shows the original GH3 venues behind the highway, with the band, crowd, cameras, lights, and effects that the original songs direct.

The mod uses art and data from your own GH3 PC copy. The repository does not contain GH3 game files.

## Requirements

- Linux or Windows (64-bit) Clone Hero **v1.1.0.6142-final** with **MelonLoader 0.7.3**. Other game versions are rejected. Use a separate copy of the game for the mod.
- .NET 6 runtime and a .NET SDK that builds `net6.0`. The tests and the GH3 song tool need the .NET 10 SDK.
- For the 3D stages: your GH3 PC files, `uv`, Python 3.12, and FFmpeg. See [docs/PIPELINE.md](docs/PIPELINE.md).

## Install

```bash
MISE_QUIET=1 scripts/regenerate.sh --clean     # once, and after a pipeline change
MISE_QUIET=1 bash install.sh
```

`install.sh` builds the mod and copies it, the prepared assets, the venues, and the test charts into the game. Set `GH3_CLONE_HERO_DIR` if the game is not at `~/games/clonehero-gh3-melon`. Without the generated stage assets, the skin works with the stock background.

For Windows, `install.sh --release <folder>` writes a release folder with `install-windows.bat`. On Windows, the player runs that file. It installs MelonLoader 0.7.3 and the mod. See [PLAYER-GUIDE.md section 6](docs/PLAYER-GUIDE.md#6-install-on-windows).

## Play

```bash
MISE_QUIET=1 bash run-gh3-skin.sh
```

In the menus, push **F6** to select the venue (a venue, **Random**, or **Song's choice**) and the band members. See [docs/PLAYER-GUIDE.md](docs/PLAYER-GUIDE.md). For tests, `GH3_SKIN_STAGE` selects a venue with its pack ID and overrides the picker:

```bash
MISE_QUIET=1 GH3_SKIN_STAGE=gh3.hell bash run-gh3-skin.sh
```

| Pack ID | Venue |
| --- | --- |
| `gh3.party` | Backyard Bash |
| `gh3.dive` | Mitch's Moose Lounge |
| `gh3.video` | Video Shoot |
| `gh3.artdeco` | Ye Olde Royal Odeon |
| `gh3.prison` | Shanker's Island |
| `gh3.wikker` | Desert Rock Tour |
| `gh3.budokan` | Kaiju Megadome (default when the song selects no stage) |
| `gh3.hell` | Lou's Inferno |
| `gh3.soundcheck` | Soundcheck |
| `gh3.credits` | Credits |

`off` shows Clone Hero's stock background. `run-gh3-skin.sh --preview` plays a test chart with the bot (`GH3_SKIN_PREVIEW_SONG` selects the chart folder).

Songs from GH3 get their original direction (cameras, lights, band, pyro) when the chart's title and artist match. You can also add direction to your own charts. See [docs/CUSTOM-CONTENT.md](docs/CUSTOM-CONTENT.md).

## Limits

- One five-fret guitar player. Other layouts use the stock display.
- 16:9 and other landscape screens. Portrait is not supported.
- Clone Hero's fret buttons and strings stay.

See [docs/STATUS.md](docs/STATUS.md) for the known differences from GH3.

## Documentation

| Document | Contents |
| --- | --- |
| [PLAYER-GUIDE.md](docs/PLAYER-GUIDE.md) | Select the stage and the band in the game; add stages and characters |
| [ARCHITECTURE.md](docs/ARCHITECTURE.md) | Runtime modules, pipeline, asset formats, data flow |
| [PIPELINE.md](docs/PIPELINE.md) | Regenerate the assets from GH3 data |
| [VERIFICATION.md](docs/VERIFICATION.md) | Tests, recordings, comparisons, runtime variables |
| [STATUS.md](docs/STATUS.md) | Current status and known gaps |
| [PARITY-NOTES.md](docs/PARITY-NOTES.md) | GH3 facts: addresses, constants, scripts, formulas |
| [CUSTOM-CONTENT.md](docs/CUSTOM-CONTENT.md) | Stage packs, song direction, native song files, Moonscraper |
| [PACKAGING-PLAN.md](docs/PACKAGING-PLAN.md) | Release package plan |

## Development

```bash
MISE_QUIET=1 scripts/test.sh
```

| Path | Contents |
| --- | --- |
| `src/` | The mod (C#) |
| `pipeline/` | Asset pipeline (Python, `python -m pipeline.<step>`) |
| `scripts/` | `regenerate.sh`, `test.sh`, capture and recording scripts |
| `tests/` | C# logic tests and Python pipeline tests |
| `tools/` | GH3 song tool, audits, comparison and reference tools |
| `assets/` | Prepared HUD and gem textures (in Git) and generated stage assets (not in Git), one folder for each type ([assets/README.md](assets/README.md)) |
| `charts/` | Test charts |
