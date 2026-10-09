# Creator guide: stages and characters

This guide tells you how to make a stage (`.chstage`) and a character (`.chchar`) for the mod with Blender and the CH Stage Tools add-on. The reference for each panel is [BLENDER-ADDON.md](BLENDER-ADDON.md). The reference for the file format is [PACKAGE-FORMAT.md](PACKAGE-FORMAT.md).

The folder [`examples/`](../examples/) has an example stage and an example character. Open them in Blender to see a finished result.

## 1. Before you start

You need:

- Blender 4.2 LTS or later (Linux, Windows, or macOS). The add-on also works on macOS, but the mod does not run on macOS Clone Hero.
- The add-on file `chstage_tools-<version>.zip`. To make it from the repository: `python3 blender/build.py` (the file goes to `blender/dist/`).
- Clone Hero with the mod installed ([PLAYER-GUIDE.md](PLAYER-GUIDE.md)), to test your package.
- For characters and for camera bone anchors: the GH3 band library `gh3-band-v1.chlib`. The mod installs it in `<game>/Mods/GH3Skin/Packages/`.

## 2. Install the add-on

1. In Blender, open **Edit > Preferences > Get Extensions**.
2. Open the menu at the top right (the arrow), and click **Install from Disk**.
3. Select `chstage_tools-<version>.zip`. Do not extract the zip file. Blender installs and enables the add-on.
4. Open **Edit > Preferences > Add-ons > CH Stage Tools**, and set these fields:
   - **Custom/Stages folder** and **Custom/Characters folder**: the folders of Clone Hero (see [PLAYER-GUIDE.md section 6](PLAYER-GUIDE.md#6-add-stages-and-characters)). Export can then copy your package into the game.
   - **GH3 library (.chlib)**: `gh3-band-v1.chlib`.

![The add-on preferences](images/creator/addon-preferences.png)

The stage tools are in the 3D view sidebar (push **N**), tab **CH Stage**. The character tools are in the tab **CH Character**.

## 3. Make your first stage

The example of this section is `examples/example-club.blend`.

### 3.1 Start from the template

1. Click **File > New > New Stage from Template**. The add-on makes the scene `CH Stage`.
2. Open **Stage Package > Package Information** and set:
   - **ID**: a stable ID with lowercase letters, digits, `.`, and `-`, for example `yourname.my-club`. Do not change it in later versions.
   - **Name**: the name that the picker shows.
   - **Version**: `1.0.0`. Increase it when you publish a change.
   - **Authors** and **License** (an SPDX ID, for example `CC-BY-4.0`). Set **Redistribute**, **Modify**, and **Commercial use**.
   - **Preview**: **Render** makes a 512 × 288 image from the scene camera.

![The stage package panel](images/creator/stage-package.png)

Rules:

- Blender is Z up. Do not turn the scene. The add-on converts the data for the game.
- The performers look toward Blender −Y. The audience is on the −Y side.
- 1 Blender unit is 1 metre. A performer is about 1.8 m tall.

### 3.2 Make the geometry

1. Delete the template boxes `floor`, `backdrop`, `left`, and `right`, or change them.
2. Make your scenery with usual Blender meshes: a stage floor at Z = 0 (the band stands at Z = 0), walls, a truss, speakers.
3. Keep the meshes simple. The example has 17 meshes and 624 vertices.

Export writes all scenery meshes. It does not write markers, cameras, lights, or proxy bodies.

### 3.3 Add a material

1. Select a mesh. Use a usual Principled BSDF material. Connect an **Image Texture** to **Base Color**. The image must be PNG.
2. Open **Properties > Material > CH Stage Material** and set:

| Field | Values |
| --- | --- |
| **Shading** | **Lit** (the stage lights light the surface; the default), **Ambient**, **Unlit** (full brightness, for example a sign), **Not set** |
| **Transparency** | **Opaque**, **Cutout** (alpha test, for example a truss), **Blend**, **Additive** (for example a light glow) |
| **Two-sided** | Draws both sides of each face |

![The material panel with a cutout truss](images/creator/stage-material.png)

The example uses **Lit** for the walls and the floor, **Cutout** with **Two-sided** for the truss, and **Unlit** with **Additive** for the neon star. For a lightmap, see [BLENDER-ADDON.md section 1.13](BLENDER-ADDON.md#113-rules-to-know).

### 3.4 Put the band on the stage

The template has four band placements (`band-guitarist`, `band-bassist`, `band-singer`, `band-drummer`) with proxy bodies. A proxy body shows the size of a performer. Export does not write it.

1. Select a band placement. Move it and turn it around Z. The arrow of the proxy body points to the audience.
2. To add a placement, put the 3D cursor at the position, then click **Add Marker > Band placement** in **Stage Package**. Set the **Role** in **Stage Object**.

![A band placement](images/creator/stage-band-marker.png)

Each role can have one placement. A stage needs at least one placement. The template also has a waypoint (`guitarist_walk01`, a place where the guitarist can walk) and effect emitters (pyro, sparks, fireworks, smoke).

### 3.5 Set two cameras

The template has two camera shots:

| Shot | Groups | Use |
| --- | --- | --- |
| `wide` | `wide`, `default` | The whole stage. The group `wide` is necessary |
| `guitar` | `guitarist`, `default` | The guitarist. **Anchor**: Guitarist, root: the camera moves with the guitarist |

1. Select a camera. Look through it (**View > Cameras > Set Active Object as Camera**) and move it.
2. In **Camera Shot**, set the groups. The game selects a shot from a group when the chart or the generated direction asks for that group: `wide`, `guitarist`, `bassist`, `singer`, `drummer`, `orbit`, or `default`. A group that the stage does not have uses the shots of `wide`.
3. **Look at**: with a target (for example Singer, head), the game turns the camera to the target. With **None** and **Write rotation**, the game uses the rotation of the camera.
4. Set the render size to 16:9 (the template uses 1920 × 1080). The game uses the horizontal field of view of the camera.

![The guitarist camera](images/creator/stage-camera.png)

The highway covers the center of the screen. In the example, the guitarist camera turns 22° to the left, so the guitarist is at the right of the highway.

### 3.6 Set a light and two moods

The template has two stage lights (`Stage` for the scenery and `Band` for the performers, both **Ambient**), two snapshots (`fill` and `chorus`), and three moods (`intro`, `blackout`, and `chorus`). A snapshot holds the color and the intensity of each stage light. A mood is a list of snapshots. Charts select the moods.

1. Open **Lighting**. Select the snapshot `fill`, and click **Apply**. The add-on sets the lights to the snapshot.
2. Select the light `Stage`. Change its Blender light color, and its **Intensity** in **Stage Light** (for example warm white, 0.7).
3. Select the snapshot `fill` again, and click **Capture**. The snapshot gets the new settings.
4. Do steps 1 to 3 again for `chorus` with other colors (for example magenta and cyan).
5. Look at the moods: `intro` and `blackout` use `fill`; `chorus` uses `chorus`, then `fill`.
6. Turn on **Live lighting preview** and select a mood to see it in the 3D view.

![The lighting panel with the live preview of the chorus mood](images/creator/stage-lighting.png)

Rules:

- The moods `intro` and `blackout` are necessary.
- Every snapshot must have settings for every stage light. Click **Capture** after you add a light.
- A scenery object uses the light group `Stage` when its **Light group** (in **Stage Object**) is empty. The band uses `Band`.
- Charts can ask for the moods `intro`, `verse`, `chorus`, `solo`, `flare`, `blackout`, and `strobe`. When the stage does not have a mood, the game plays `intro`.

### 3.7 Validate and export

1. Click **Validate**. The list shows errors and warnings. Click the arrow of an issue to select its object. Correct all errors.
2. Click **Export .chstage**. Select a file name (the default is `<ID>.chstage`). Options:
   - **Copy to Custom/Stages**: also copies the package into the game.
   - **Development folder**: also writes the unpacked files to `unpacked/<name>.chstage`.
   - **Preview**: **Render**, **Image**, or **None**.
3. The add-on writes the package only when there is no error.

### 3.8 Play the stage

1. Put the `.chstage` file into `Custom/Stages` of Clone Hero (or use **Copy to Custom/Stages**).
2. Start Clone Hero. The mod imports the package one time.
3. In the menus, push **F6**. Select your stage in the **Stage** list. Push **F6** again to close the picker.
4. Start a song.

![The F6 picker with the example stage and the example guitarist](images/creator/game-picker.png)

## 4. Edit a GH3 venue

1. Click **File > Import > CH Stage Package (.chstage)**. Select a venue of the mod, for example `<game>/Mods/GH3Skin/Packages/gh3.dive.chstage`. A venue opens in 1 to 19 seconds.
2. Change something, for example the color of a snapshot, a camera, or a material.
3. In **Package Information**, set a new **ID** (for example `yourname.dive-blue`) and a new **Name**. When two packages have the same ID, the mod uses only the package with the higher version (the log tells you `... is not used: ... has the same id`).
4. Click **Validate**, then **Export .chstage**. Put the file into `Custom/Stages`.

![A GH3 venue opened in Blender, with its script bank](images/creator/venue-open.png)

The `.blend` file holds all package data. Export does not need the source package after Open. Unchanged rows (shots, lights, materials, actors) export exactly as in the source. The script bank (`scripts.json`) shows the GH3 objects and values; you can change simple values, not the script code. See [BLENDER-ADDON.md sections 1.4 to 1.10](BLENDER-ADDON.md#14-start-from-a-package-for-example-a-gh3-venue).

The GH3 venues have the license `LicenseRef-GH3-Derived` with the permission `modify: false`. Keep a changed GH3 venue for your own use.

## 5. Make a character on a GH3 rig

A character is a mesh on one fixed GH3 rig. The game plays the GH3 animations of the band member on your mesh and adds the instrument. You do not make a skeleton or animations. The example is `examples/example-guitarist.blend`.

1. Set **GH3 library (.chlib)** in the add-on preferences (section 2).
2. Click **File > New > New Character from GH3 Rig**. Select a profile: guitarist, bassist, singer, or drummer. The add-on makes a scene with the rig, the GH3 body as a wireframe reference, the instrument as a preview, and a preview camera. Export never writes the reference body and the instrument.
3. Do not move, turn, scale, or edit the rig.
4. Model your character over the reference body, or import a mesh and move it onto the reference body. Use PNG images.
5. Select your meshes, and click **Transfer Weights from the GH3 Body** in **Weights and Hair**. Each vertex gets the weights of the nearest point on the GH3 body (4 bones at most).
6. Change the weights with Weight Paint if necessary. Then click **Limit and Normalize**.
7. Hair: select the hair cards, select a chain in **Hair bones of the profile**, and click **Assign to Hair Bones**. Make the hair material active and click **+** under **Hair materials**.
8. Optional: click a clip in **Clip Preview** and click **Play** to see your character move.

![Weight Paint after Transfer Weights, with the GH3 body and the guitar helpers](images/creator/character-weights.png)

9. Set the package information in **Character Package > Package Information** (ID, name, version, authors, license).
10. Click **Validate**. Correct all errors.
11. Click **Export .chchar**. Put the file into `Custom/Characters` of Clone Hero.

![The character package panel after Validate](images/creator/character-package.png)

To use the character:

1. Start Clone Hero, and push **F6** in the menus.
2. In **Band**, click **<** or **>** on the line of the band member (for example **Guitarist**) until your character shows.
3. Start a song. A guitarist character can also play the bassist (the two rigs have the same bones).

![The example guitarist in the example club](images/creator/game-example.png)

The example `.blend` file has no helpers (the GH3 body and the guitar are not in the file). Set the library in the preferences, then click the reload button in **Weights and Hair** to load them.

## 6. Direction in charts

The mod plays camera cuts, lights, and effects from the chart. Your stage gets them through its camera groups and moods. Chart authors can use these sources:

| Source | What it does | Reference |
| --- | --- | --- |
| GH `CAMERAS` and `LIGHTSHOW` tracks | GH3 camera notes (77–117) and light notes in a `.chart` section or a MIDI track | [CUSTOM-CONTENT.md, Direction tracks](CUSTOM-CONTENT.md#direction-tracks) |
| Text events | `stage pyro`, `stage lights chorus`, `[stage camera singer]`, and more, in `[Events]` | [CUSTOM-CONTENT.md, Text events](CUSTOM-CONTENT.md#text-events) |
| Rock Band `VENUE` track | Camera cuts, lighting, and effects of converted Rock Band charts (MIDI only) | [CUSTOM-CONTENT.md, Rock Band VENUE track](CUSTOM-CONTENT.md#rock-band-venue-track) |
| No direction | The mod makes cuts and light changes from the chart sections | [CUSTOM-CONTENT.md, Generated direction](CUSTOM-CONTENT.md#generated-direction) |
| `gh3_stage = <id>` in the `[song]` section of `song.ini` | The song selects an installed stage | [PLAYER-GUIDE.md, Stages that a song selects](PLAYER-GUIDE.md#stages-that-a-song-selects) |
| `venue.chstage` in the song folder | The song brings its own stage. The mod imports it when the song starts | [PLAYER-GUIDE.md, Stages that a song selects](PLAYER-GUIDE.md#stages-that-a-song-selects) |

On a custom stage, the GH3 camera notes select these groups: 84 and 86 close-up shots, 85 mid shots, 87 `wide`, 88 `singer`, 89 `drummer`, 95 `guitarist`, 97 `bassist`, the orbit notes `orbit`, and the other notes `default`. Give your stage shots in these groups, so that charts can cut to each band member.

## 7. Problems

### Validate and export messages

| Message | Do this |
| --- | --- |
| `the stage needs at least one band placement` | Add a band placement (**Add Marker > Band placement**) |
| `two band placements for the role ...` | Keep one placement for each role |
| `no camera shot is in the group 'wide' (required)` | Add the group `wide` to a camera shot |
| `shot '...': anchor ... needs a band placement for ...` | Add the placement, or change the anchor of the shot |
| `shot '...': a point without rotation needs a look-at target` | Turn on **Write rotation**, or set **Look at** |
| `lighting needs the mood 'intro'` (or `'blackout'`) | Add the mood in **Lighting > Moods** |
| `snapshot '...' has no settings for the light '...'; use Capture` | Select the snapshot and click **Capture** |
| `lighting needs at least one stage light` | Select a light and turn on **Stage light** |
| `the stage has no scenery meshes` | Add a mesh that is not a marker |
| `shot '...': the game places the camera relative to the ... bone` (warning) | Set the GH3 library in the preferences and click **Load GH3 Rig Proxies** |
| `mesh '...' has no Armature modifier for the rig` | Click **Transfer Weights** |
| `mesh '...': N vertices have no weight on a rig bone` | Click **Transfer Weights**, or paint the weights |
| `... more than 4 influences` or `... do not sum to 1` | Click **Limit and Normalize** |
| `mesh '...' has its own transform` | Click **Transfer Weights**, or **Object > Apply > All Transforms** |
| `the rig must stay at the origin ...` or `the rig has no bone ...` | Clear the rig transform, or start again from **New** |
| `material '...': image '...' is ...; packages use PNG only` | Save the image as PNG |
| `no GH3 library: ...` (warning) | Set **GH3 library (.chlib)** in the add-on preferences |

### In the game

Look in `<game>/MelonLoader/Latest.log`:

| Log line | Meaning |
| --- | --- |
| `GH3 packages: imported <id> <version> in ...` | The mod read your package. The line gives the meshes, textures, and materials |
| `GH3 packages: skipped <file>: <reason>` | The package is not valid. Validate it in Blender |
| `GH3 stage choice: <id> (<reason>)` | The stage that plays, and why (the player's choice, the song, `GH3_SKIN_STAGE`) |
| `GH3 stage loaded: <name>` | The stage is on the screen |
| `GH3 band guitarist: character <id> (...) with instrument ...` | Your character plays, with this instrument |
| `GH3 band <role>: character <id> is not installed or did not import; the venue default plays` | The character file is not in `Custom/Characters`, or it did not import (look for `skipped`) |

| Problem | Do this |
| --- | --- |
| The stage is not in the picker | Look for `skipped` in the log. Make sure that the file is in `Custom/Stages` and that its extension is `.chstage` |
| The stage and the band are dark in the game | A stage with a `scripts.json` (script bank) uses the GH3 script lights. A bank without GH3 light objects gives no light. Make the stage from **New Stage from Template** of an add-on from 2026-10-09 or later (the template has no script bank) |
| The camera shows only the highway | Move the camera, or turn it so that the performers are to the left or the right of the highway |
| A change does not show | Export again and start the game again. The mod imports a package again when its file changes. When two packages have the same ID, the mod uses the higher version |
