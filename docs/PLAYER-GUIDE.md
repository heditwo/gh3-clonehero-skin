# Player guide

This guide tells you how to install the mod, how to select the stage and the band members in the game, and how to add stages and characters.

## 1. Install the mod

You need Clone Hero `v1.1.0.6142-final` (64-bit) for Linux or Windows, and the release zip of the mod for your system: `gh3-skin-<version>-linux.zip` or `gh3-skin-<version>-windows.zip`. Use a separate copy of the game for the mod. macOS is not supported.

Each zip has one folder with `Mods/`, the install script, this guide, `release.json` (the mod version), and `SHA256SUMS` (the SHA-256 value of each file).

### On Linux

1. Close Clone Hero.
2. Extract the zip. In a terminal, go to the extracted folder.
3. Type `./install-linux.sh "<Clone Hero folder>"`. The Clone Hero folder is the folder that has the `clonehero` file.
4. The script:
   - Makes sure that the game is `v1.1.0.6142-final`.
   - Downloads MelonLoader 0.7.3 for Linux from GitHub, checks its SHA-256 value, and extracts it into the game folder. If MelonLoader 0.7.3 is already installed, the script keeps it.
   - Removes the files of an earlier mod version (`Mods/GH3Skin/assets`, `Packages`, `render`, and `counter-tuning.json`). It keeps your choices and the cache in `UserData/GH3Skin`.
   - Copies `Mods/Gh3Skin.dll` and `Mods/GH3Skin/` into the game folder.
   - Writes `clonehero-melonloader.sh` into the game folder. MelonLoader on Linux loads only when you start the game with this file.
5. Start the game with `clonehero-melonloader.sh`. The first start takes some minutes and needs the internet: MelonLoader prepares its files. Then the mod imports the stages, the characters, and the instruments (about 10–15 seconds on a PC with 8 cores). The game shows the menus while the mod imports.
6. Close the game, and do step 3 again. Now the script sets `force_offline_generation = true` in `UserData/Loader.cfg`. MelonLoader then does not connect to the internet at each start.
7. Always start the game with `clonehero-melonloader.sh`.

### On Windows

1. Close Clone Hero.
2. Extract the zip. In the extracted folder, double-click `install-windows.bat`. When the script asks, type the Clone Hero folder (the folder that has `Clone Hero.exe`). You can also drag the Clone Hero folder onto `install-windows.bat`.
3. The script:
   - Makes sure that the game is `v1.1.0.6142-final`.
   - Downloads MelonLoader 0.7.3 (64-bit) from GitHub, checks its SHA-256 value, and extracts it into the game folder. If MelonLoader 0.7.3 is already installed, the script keeps it.
   - Removes the files of an earlier mod version (`Mods\GH3Skin\assets`, `Packages`, `render`, and `counter-tuning.json`). It keeps your choices and the cache in `UserData\GH3Skin`.
   - Copies `Mods\Gh3Skin.dll` and `Mods\GH3Skin\` into the game folder.
4. Start Clone Hero one time. This first start takes some minutes and needs the internet: MelonLoader prepares its files. Then the mod imports the stages, the characters, and the instruments (about 10–15 seconds on a PC with 8 cores). The game shows the menus while the mod imports.
5. Close the game, and do step 2 again. Now the script sets `force_offline_generation = true` in `UserData\Loader.cfg`. MelonLoader then does not connect to the internet at each start.
6. Start Clone Hero. The mod uses Direct3D 11, the Windows default. You do not need a launch option.

Without the script, do these steps by hand: extract `MelonLoader.x64.zip` (version 0.7.3) into the game folder, start the game one time, close it, set `force_offline_generation = true`, remove the old files of step 3, and copy the `Mods` folder of the release into the game folder.

### Files of the mod

The mod keeps its files here (Windows paths; Linux uses `/`):

| Item | Folder |
| --- | --- |
| Mod, stages, characters, and shaders (about 1.65 GB) | `<game>\Mods\Gh3Skin.dll`, `<game>\Mods\GH3Skin\` |
| Cache of the imported packages (about 1.9 GB) | `<game>\UserData\GH3Skin\Cache\` |
| Your choices in the picker | `<game>\UserData\GH3Skin\choices.json` |
| Your stages, characters, and instruments | `Custom\Stages\`, `Custom\Characters\`, and `Custom\Instruments\` (see [section 6](#6-add-stages-characters-and-instruments)) |
| Log | `<game>\MelonLoader\Latest.log` |

To remove the cache, close the game and delete `UserData\GH3Skin\Cache\`. The mod makes it again at the next start.

On Windows, do not install the game in `C:\Program Files` with the zip file. Windows prevents writes to that folder, and the game and the mod must write files. Use a folder such as `C:\Games\Clone Hero`.

## 2. Open the picker

1. Start Clone Hero with the mod.
2. Stay in the menus. The picker does not open during a song.
3. Push **F6**. The picker window opens on top of the menu.
4. Click an item to select it. The choice is saved immediately.
5. Push **F6** again, or click **Close (F6)**, to close the window.

The new choice applies to the next song that you start.

While the picker is open, mouse clicks go only to the picker. The keyboard and the guitar still control the Clone Hero menu below the window. Close the picker before you use the menu again.

At the top of the menu screen, a small line shows the current stage and the F6 key. To remove this line, clear **Show the F6 hint in the menus** in the picker.

![The picker in the main menu](images/picker.png)

## 3. Select the stage

The **Stage** list has these items:

| Item | Result |
| --- | --- |
| A stage name | This stage plays for all songs |
| **Random** | A different random stage plays for each song. The same stage does not play two times in sequence |
| **Song's choice (else Kaiju Megadome)** | The stage that the song selects plays. If the song does not select a stage, Kaiju Megadome (`gh3.budokan`) plays. If Kaiju Megadome is not installed, a random stage plays |

**Song's choice** is the default when you have not used the picker. If you selected a stage with an earlier version of the mod, your choice stays.

The image on the right shows the stage below the mouse pointer, or the selected stage.

### Stages that a song selects

A song can select its stage in three ways. The mod looks for them in this order:

1. A stage package with the name `venue.chstage` in the song folder (a zip file or a folder). The mod imports it when you start the song. The first start of the song can take some seconds longer.
2. The key `gh3_stage` in the `[song]` section of `song.ini`, for example `gh3_stage = gh3.dive`. The value is the ID of an installed stage. Clone Hero does not use this key.
3. The key `stage` in the GH3 direction file `stage-events.json` of the song folder. The GH3 direction zip gives each GH3 career song its GH3 venue ([GH3 direction for GH3 songs](#gh3-direction-for-gh3-songs)). GH3 bonus songs have no venue.

The mod does not find a stage by the title or the artist of a song.

**Songs can select their stage and band** (on by default) controls items 1 to 3 when you select a stage name or **Random**:

- On: the song's stage plays. If the song has no stage, your choice plays.
- Off: your choice always plays.

With **Song's choice**, the song's stage always plays, if the song has one.

If a song selects a stage that is not installed, your choice plays, and the log tells you the stage ID.

## 4. Select the band members

The **Band** section has one line for each band member: guitarist, bassist, drummer, and singer.

1. Click **<** or **>** to go through the characters for this member, one outfit at a time.
2. Click **<<** or **>>** to go to the previous or next character (the first outfit of that character).
3. **Default (venue)** is the band member of the GH3 venue, or the band member that the song selects.

A song can select a band member with the key `characters` in its `stage-events.json`. The GH3 direction zip selects the GH3 singer of each GH3 song: the female singer for Barracuda, Closer, Down 'N Dirty, Hit Me With Your Best Shot, In Love, Kool Thing, and Radio Song, and Bret Michaels for Go That Far and Talk Dirty to Me. The song's member plays only when you keep **Default (venue)** for that member and **Songs can select their stage and band** is on. If the character is not installed, the venue default plays.

### GH3 characters

The mod has all the characters that GH3 lets you play, with all their outfits and styles (72 characters):

- Axel Steel, Casey Lynch, Izzy Sparks, Judy Nails, Johnny Napalm, Lars Umlaut, Midori, and Xavier Stone: two outfits with four styles each, for example "Judy Nails: Skool Grrl (Stripes)".
- Slash, Tom Morello, Lou (also his outfit "Thick Izzy"), God of Rock, and Grim Ripper.
- For the singer: Bret Michaels and the female singer of GH3.

All characters are available. You do not unlock them as in GH3. A guitarist character can play the guitarist or the bassist.

Bret Michaels and the female singer also move as in GH3: with their own skeletons, animations, and hair, and with the facial animations of GH3 songs. The female singer is smaller than the male singer, as in GH3.

Each GH3 guitarist character moves as in GH3: with its own skeleton, its own animations (stances, idles, strums, finger and fret movements, solos, intro, win, and lose), its own hair and accessory physics, and its own size (Judy Nails is smaller, as in GH3). As the bassist, a guitarist character keeps its own animations and plays the bass, as in a GH3 co-op game.

Every character can play every member: the guitarist, the bassist, the singer, and the drummer. The picker shows all characters for each member. The characters of that member come first; the others show their original member in parentheses, for example "Midori: Puffball (Aqua) (a guitarist)".

- In its own member, a GH3 character plays its own GH3 animations, as in GH3. A guitarist character that plays the bassist also keeps its own animations, as in GH3 co-op.
- In another member, the character plays the GH3 animations of that member on its own body. For example, Judy Nails as the singer sings with her own face (the GH3 facial animations and the lip sync of the song's vocals), and Lars Umlaut as the drummer plays the drums.
- The drum kit and the microphone stand take the size of the character: a small character gets a smaller kit and a lower stand, a tall character a larger kit and a higher stand. The sticks stay in the hands, and the singer brings the microphone to its own mouth.
- A character that keeps the skeleton of its model (made with **Use my model's skeleton** in the Blender add-on) also plays every member, and it blinks; as the singer it moves its mouth with the song's vocals when it has face shapes.

Differences from GH3:

- The hair moves with the GH3 joint limits, but it is not the GH3 physics engine. It collides only with the head.
- GH3 selects the singer from the song (for example the female singer in "Hit Me With Your Best Shot"). In the mod, the song's direction file selects the singer if you keep **Default (venue)** for the singer. GH3 removes the singer in two instrumental songs (Cliffs of Dover, Impulse); the mod keeps the singer.
- To play the GH3 guitarist characters and the two singers, the mod needs the character library `gh3-characters-v1.chlib` in `Mods/GH3Skin/Packages/` (the release installs it). A character that cannot load is not in the picker; the log tells you why.

If a character cannot load, the venue default plays, and the log tells you why.

### Instruments

Each GH3 guitarist character plays its GH3 guitar when it is the guitarist, for example the white Explorer for Axel Steel and Lars Umlaut, the worn red SG for Judy Nails, Johnny Napalm, and Midori, and the black Les Paul for Casey Lynch, Izzy Sparks, and Xavier Stone. The female singer has her GH3 microphone.

When a character plays another member, it plays the instrument of that member: a guitarist character that plays the bassist has the bass of the GH3 bassist, and as the drummer it has the drum kit. A character without an instrument in its model gets the instrument of the member that it plays (the guitarist plays the guitar, the bassist the bass, the drummer the drum kit, and the singer has the microphone and stand). The log line `GH3 band <member>: character <id> ... with <instrument>` tells you which instrument plays.

To select an instrument, use the **Guitar** line below the guitarist and the **Bass** line below the bassist:

1. Click **<** or **>** to go through the instruments one at a time.
2. Click **<<** or **>>** to go to the previous or next model (for example from the Les Paul Standard finishes to the Les Paul Custom).
3. **Default (the character's own)** keeps the rules above.

The mod has all 88 guitars and 20 basses of GH3, with all finishes, for example "Gibson X-Plorer Standard (Alpine White)", "Obliviaxe: Lou's", and "Tobias Killer B Four String (Natural)". All are available; you do not buy them as in GH3. The chosen instrument plays with every character and with the venue's band member.

### Highway

As in GH3, the highway image comes from your character: Judy Nails has her highway, Lars Umlaut his, and so on. The mod uses the character of the member that you play: the guitarist, or the bassist when you play a bass or rhythm part. Without a character, the highway of GH3's Axel Steel shows. A GH3 guitar does not change the highway, as in GH3. An instrument that a creator makes can have its own highway; when you select it, its highway shows.

## 5. Direction options

Three check boxes at the bottom of the picker control the camera and light direction of songs. The first two are on by default; **Reduce flashing** is off. The change applies to the next song that you start.

| Option | On | Off |
| --- | --- | --- |
| **Use Rock Band VENUE tracks** | A chart with a Rock Band `VENUE` track gets camera cuts, lights, and effects from that track | The mod ignores `VENUE` tracks |
| **Generate direction for charts without it** | A chart without camera or light direction gets camera cuts and light changes from its sections | Such a chart keeps one light mood and the automatic cameras |
| **Reduce flashing** | Rock Band light flashes are weak and come at most once each second; strobes change at most five times each second | Light flashes follow the chart, at most about three each second |

### GH3 direction for GH3 songs

GH3 songs have their GH3 camera cuts, light show, band performance, facial animations, venue, and singer only with their GH3 direction file. The release has a direction zip for the GH3 chart packs that most players use:

| Zip | Chart pack | Songs |
| --- | --- | --- |
| `gh3-legends-of-rock-direction-<version>.zip` | "Guitar Hero III - Legends of Rock" (Chorus Encore, enchor.us; Google Drive folder "Guitar Hero III (Individual Songs)") | 75 of 75 |
| `gh3-dlc-direction-<version>.zip` | "Guitar Hero III DLC" (Chorus Encore) | 76 of 77 |

1. Find the folder of the chart pack in your songs folder. It contains one folder for each song, for example `Killswitch Engage - My Curse`.
2. Extract the zip into that folder and accept to merge the folders. Each song folder gets one new file, `stage-events.json`.
3. Scan the songs again in Clone Hero.

The zip does not change your charts, your audio, or `song.ini`. To remove the direction, delete `stage-events.json` from the song folders. The README in the zip tells which version of the pack it fits. A song with another chart (another version, or a chart that you converted yourself) gets the generated direction; the mod does not find GH3 direction by the title of a song.

## 6. Add stages, characters, and instruments

1. Find your Clone Hero `Custom` folder. This is the folder that has `Highways` and `Image Backgrounds`. On Linux, it is usually `~/.clonehero/Custom`. On Windows, it is `Documents\Clone Hero\Custom` after a typical installation. In portable mode (and when you extract the Windows zip file), it is `PlayerData\Custom` in the game folder.
2. Put stage files (`.chstage`) in `Custom/Stages/`.
3. Put character files (`.chchar`) in `Custom/Characters/`.
4. Put instrument files (`.chinst`) in `Custom/Instruments/`.
5. Start the game. The mod imports new files one time and keeps the result in `UserData/GH3Skin/Cache/` in the game folder. A large stage can take some seconds.

You can use group folders, for example `Custom/Stages/My Stages/`, to a depth of four folders.

When you remove a package, the mod deletes its cache folder at the next start.

## 7. Problems

| Problem | Do this |
| --- | --- |
| F6 does nothing | Make sure that you are in the menus, not in a song. Look in `MelonLoader/Latest.log` for lines that start with `GH3 picker` |
| A stage or a character is not in the list | Look in the log for `GH3 packages: skipped <file>: <reason>`. A package that imports shows `(importing)`; open the picker again later |
| The wrong stage plays | Look in the log for `GH3 stage choice: <stage> (<reason>)` |
| A band member has no instrument or the wrong instrument | Look in the log for `GH3 band <member>: character <id> ... with ...` |
| The direction options do not apply | Look in the log for `GH3 direction: Rock Band VENUE tracks on/off, generated direction on/off, reduce flashing on/off`. The test variables `GH3_SKIN_VENUE_TRACK`, `GH3_SKIN_GENERATED_DIRECTION`, and `GH3_SKIN_REDUCE_FLASHING` override the picker |
| Windows: the script does not start ("running scripts is disabled") | Use `install-windows.bat`, not the `.ps1` file. The `.bat` file starts PowerShell with permission for this script only |
| Linux: the game starts without MelonLoader (no `MelonLoader/Latest.log`, or the log has no `GH3 Gameplay Skin` line) | Start the game with `clonehero-melonloader.sh` in the game folder, not with `clonehero` |
| Windows: the log has no `GH3 Gameplay Skin` line | MelonLoader is not installed or is another version. Do [section 1](#1-install-the-mod) again |
| You want the defaults again | Close the game and delete `UserData/GH3Skin/choices.json` in the game folder |
