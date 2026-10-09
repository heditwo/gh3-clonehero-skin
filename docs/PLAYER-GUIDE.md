# Player guide: stages and band

This guide tells you how to select the stage and the band members in the game, and how to add stages and characters.

## 1. Open the picker

1. Start Clone Hero with the mod.
2. Stay in the menus. The picker does not open during a song.
3. Push **F6**. The picker window opens on top of the menu.
4. Click an item to select it. The choice is saved immediately.
5. Push **F6** again, or click **Close (F6)**, to close the window.

The new choice applies to the next song that you start.

While the picker is open, mouse clicks go only to the picker. The keyboard and the guitar still control the Clone Hero menu below the window. Close the picker before you use the menu again.

At the top of the menu screen, a small line shows the current stage and the F6 key. To remove this line, clear **Show the F6 hint in the menus** in the picker.

![The picker in the main menu](images/picker.png)

## 2. Select the stage

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
3. The original GH3 venue of a GH3 career song: a native song (`<name>_song.pak.xen` in the song folder), or a chart with the title and artist of a GH3 career song. GH3 bonus songs have no venue.

**Songs can select their stage** (on by default) controls items 1 to 3 when you select a stage name or **Random**:

- On: the song's stage plays. If the song has no stage, your choice plays.
- Off: your choice always plays.

With **Song's choice**, the song's stage always plays, if the song has one.

If a song selects a stage that is not installed, your choice plays, and the log tells you the stage ID.

## 3. Select the band members

The **Band** section has one line for each band member: guitarist, bassist, drummer, and singer.

1. Click **<** or **>** to go through the characters for this member.
2. **Default (venue)** is the band member of the GH3 venue.

A character plays with the animations of the member that it replaces. A character for one member can also play another member when the two GH3 skeletons have the same bones. In GH3, the guitarist and the bassist have the same bones; thus a guitarist character can play the bassist, and the reverse. The picker shows such a character with its original member in parentheses, for example "Axel Steel (GH3) (a guitarist)".

If a character cannot load, the venue default plays, and the log tells you why.

### Instruments

Each band member plays the instrument of the member that it replaces: the guitarist plays the guitar, the bassist plays the bass, the drummer plays the drum kit, and the singer has the microphone and stand. The mod adds the instrument to the character. Thus a guitarist character that plays the bassist has the bass.

A character that has an instrument in its own model keeps that instrument, also when it plays another member. The log line `GH3 band <member>: character <id> ... with <instrument>` tells you which instrument plays.

## 4. Direction options

Three check boxes at the bottom of the picker control the camera and light direction of songs. The first two are on by default; **Reduce flashing** is off. The change applies to the next song that you start.

| Option | On | Off |
| --- | --- | --- |
| **Use Rock Band VENUE tracks** | A chart with a Rock Band `VENUE` track gets camera cuts, lights, and effects from that track | The mod ignores `VENUE` tracks |
| **Generate direction for charts without it** | A chart without camera or light direction gets camera cuts and light changes from its sections | Such a chart keeps one light mood and the automatic cameras |
| **Reduce flashing** | Rock Band light flashes are weak and come at most once each second; strobes change at most five times each second | Light flashes follow the chart, at most about three each second |

## 5. Add stages and characters

1. Find your Clone Hero `Custom` folder. This is the folder that has `Highways` and `Image Backgrounds` (on Linux, usually `~/.clonehero/Custom`; in portable mode, `PlayerData/Custom` in the game folder).
2. Put stage files (`.chstage`) in `Custom/Stages/`.
3. Put character files (`.chchar`) in `Custom/Characters/`.
4. Start the game. The mod imports new files one time and keeps the result in `UserData/GH3Skin/Cache/` in the game folder. A large stage can take some seconds.

You can use group folders, for example `Custom/Stages/My Stages/`, to a depth of four folders.

When you remove a package, the mod deletes its cache folder at the next start.

## 6. Problems

| Problem | Do this |
| --- | --- |
| F6 does nothing | Make sure that you are in the menus, not in a song. Look in `MelonLoader/Latest.log` for lines that start with `GH3 picker` |
| A stage or a character is not in the list | Look in the log for `GH3 packages: skipped <file>: <reason>`. A package that imports shows `(importing)`; open the picker again later |
| The wrong stage plays | Look in the log for `GH3 stage choice: <stage> (<reason>)` |
| A band member has no instrument or the wrong instrument | Look in the log for `GH3 band <member>: character <id> ... with ...` |
| The direction options do not apply | Look in the log for `GH3 direction: Rock Band VENUE tracks on/off, generated direction on/off, reduce flashing on/off`. The test variables `GH3_SKIN_VENUE_TRACK`, `GH3_SKIN_GENERATED_DIRECTION`, and `GH3_SKIN_REDUCE_FLASHING` override the picker |
| You want the defaults again | Close the game and delete `UserData/GH3Skin/choices.json` in the game folder |
