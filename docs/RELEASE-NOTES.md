# GH3 Gameplay Skin 0.9.10

This is the draft of the text for the GitHub release. The orchestrator copies it into the release body, with absolute links (`~/.cache/release-prep/release-body.md`). Nothing is published yet.

The GH3 Gameplay Skin is a mod for Clone Hero. It makes Clone Hero look and play like Guitar Hero III: Legends of Rock. It replaces the highway, the gems, the sustains, the hit effects, and the HUD with the GH3 art and animation. Behind the highway, it shows the ten GH3 venues with the band, the crowd, the cameras, the lights, and the effects. GH3 songs get their original GH3 direction with the direction zips below. Other charts get direction from their GH or Rock Band venue tracks, or from their sections. In the menus, push **F6** to select the stage, the band members, and their instruments: all 72 playable GH3 characters and all 108 GH3 guitars and basses are in the mod.

## Downloads

| File | Contents |
| --- | --- |
| `gh3-skin-0.9.10-linux.zip` | The mod for Linux: `Gh3Skin.dll`, the GH3 art, the ten GH3 venues, the GH3 band and character libraries, the GH3 characters and instruments, the shader bundles, `install-linux.sh`, and the player guide |
| `gh3-skin-0.9.10-windows.zip` | The same for Windows, with `install-windows.bat` and `install-windows.ps1` |
| `gh3-legends-of-rock-direction-0.9.10.zip` | The GH3 direction (`stage-events.json`) for the chart pack "Guitar Hero III - Legends of Rock" of Chorus Encore: 75 of 75 songs. No charts and no audio |
| `gh3-dlc-direction-0.9.10.zip` | The GH3 direction for the chart pack "Guitar Hero III DLC" of Chorus Encore: 76 of 77 songs. No charts and no audio |
| `chstage_tools-0.1.0.zip` | CH Stage Tools, the Blender add-on (Blender 4.2 or later) to make stages (`.chstage`) and characters (`.chchar`) |
| `gh3-skin-examples-0.9.10.zip` | For creators: the example stage and character (`.blend` and package, CC0-1.0) |
| `SHA256SUMS-0.9.10` | The SHA-256 value of each download |

You need only one of the two game zips. The direction zips, the add-on, and the examples are optional.

## Requirements

- Clone Hero `v1.1.0.6142-final`, 64-bit, for Linux or Windows. The mod does not start on other versions. macOS is not supported.
- MelonLoader 0.7.3. The install script downloads it from GitHub, checks its SHA-256 value, and installs it. The first start of the game needs the internet.
- About 1.2 GB of disk space for the mod and 1.9 GB for its cache.

## Install

Use a separate copy of Clone Hero for the mod. The [player guide](PLAYER-GUIDE.md#1-install-the-mod) gives all the steps.

1. Close Clone Hero. Extract the zip for your system.
2. Linux: in the extracted folder, type `./install-linux.sh "<Clone Hero folder>"`. Windows: double-click `install-windows.bat` and type the Clone Hero folder.
3. Start the game one time (on Linux with `clonehero-melonloader.sh` in the game folder). The first start takes some minutes: MelonLoader prepares its files, and the mod imports its packages.
4. Close the game, and run the install script again. It stops the MelonLoader network check at each start.
5. In the menus, push **F6** to open the picker.

## GH3 direction for the GH3 chart packs

The GH3 songs get their GH3 camera cuts, light show, band performance, facial animations, venue, and singer only with a `stage-events.json` file in the song folder. The direction zips have these files for the two GH3 chart packs of Chorus Encore. Each file must go into the song folder with the matching chart. Extract the zip into a new folder and run its install script: `install-direction.bat` on Windows, `sh install-direction.sh "<songs folder>"` on Linux. The script finds the song folders by the SHA-256 of their `notes.mid`, also in subfolders (for example `Quickplay`, `Bonus`, or `gh3dlc/gh3dlc`). It shows the list first and never replaces a file. See the [player guide](PLAYER-GUIDE.md#gh3-direction-for-gh3-songs) and the README in the zip.

## Blender add-on

CH Stage Tools makes, opens, validates, and exports stages and characters for the mod, on the GH3 rigs or on the skeleton of your own model. It opens the ten GH3 venues too. Install `chstage_tools-0.1.0.zip` in Blender 4.2 or later with **Edit > Preferences > Get Extensions > Install from Disk**. The [creator guide](CREATOR-GUIDE.md) shows how to make a stage and a character.

## Known differences and limits

- Only one five-fret guitar player uses the skin. Other layouts show the Clone Hero display. Portrait screens are not supported.
- Windows was tested only under Wine and DXVK on Linux, not on Windows hardware. The Windows install script was not tested with Windows PowerShell 5.1.
- Rendering: no shadows, no bloom, and no hand-held camera shake. The GH3 random camera sequence cannot be the same as in GH3.
- The hair physics is the mod's own solver with the GH3 values, not the Havok engine of GH3.
- The picker works with the mouse only. Songs in `.sng` files cannot select a stage and cannot have a direction file.
- The lip sync on custom charts is an addition of the mod (GH3 has no lip sync). It reads English words best.
- On Linux, always start the game with `clonehero-melonloader.sh`.

The full list is in [STATUS.md](STATUS.md#known-differences-from-gh3).

## Credits and licenses

- The code, the tools, and the documentation of the repository are MIT ([LICENSE](../LICENSE)). The Blender add-on is GPL-3.0-or-later.
- The venues, the band, the characters, the instruments, the animations, the audio, and the art in the game zips and the direction files come from Guitar Hero III: Legends of Rock for PC (Neversoft, RedOctane, Activision, 2007). The pipeline made them from the owner's copy of the game. These files are not covered by the MIT license; their packages have the license `LicenseRef-GH3-Derived`.
- The tap gems come from BetterGH3 by lunalawl. The cyan Star Power phrase gems and the highway rails come from FastGH3 by donnaken15.
- The lip sync uses the CMU Pronouncing Dictionary (Carnegie Mellon University, BSD-style license, in the zips as `LICENSE-cmudict.txt`).
- MelonLoader (Apache-2.0) loads the mod. The zips do not contain it.
- The direction zips fit the chart packs of Chorus Encore (charter Neversoft). They do not contain charts or audio.

[CREDITS.md](../CREDITS.md) lists all third-party work.

This is a non-commercial fan project. It is not affiliated with, endorsed by, or sponsored by Activision, Neversoft, RedOctane, Harmonix, or the Clone Hero developers. Guitar Hero is a trademark of its owner.
