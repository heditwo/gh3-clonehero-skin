# 3D stage and song effects

Build **0.8.112** supports all ten GH3 performance venues as ordinary stage packs and version 2 song-direction files. **Kaiju Megadome** (`gh3.budokan`) is the default. The camera moves between stage and performer views. The stage runs behind the highway during the supported single-player guitar mode.

See [STAGE-PACKS.md](STAGE-PACKS.md) for the venue list, common importer, and working custom-stage template. The [custom-content specification](CUSTOM-CONTENT.md) describes the current version 2 song direction and skin-pack proposal. [MOONSCRAPER.md](MOONSCRAPER.md) defines the updated authoring target and records the format audit.

Latest preview: [100-note streak hand fire with automatic cameras, 900p60 with audio](https://filebin.net/gh3-hand-fire-7390d10c14bb/hand-fire-preview.mp4), version **0.8.110**. The link expires on **2026-10-13**.

## Prepare and install

The local GH3 source is `~/games/gh3-test-tools/game/DATA`. Change the input paths below if needed.

```bash
MISE_QUIET=1 uv run --with pillow --with numpy --with 'shapely>=2.1' python prepare-library.py \
  --data "$HOME/games/gh3-test-tools/game/DATA"
MISE_QUIET=1 uv run --with pillow --with numpy --with 'shapely>=2.1' python export-gh3-stages.py \
  --data "$HOME/games/gh3-test-tools/game/DATA"
MISE_QUIET=1 python3 export-stage-events.py \
  "$HOME/games/gh3-test-tools/game/DATA" --library --output assets/stages/events
MISE_QUIET=1 uv run --with UnityPy --with numpy python prepare-stage-shaders.py
MISE_QUIET=1 uv run --with pillow --with numpy python prepare-particle-materials.py
MISE_QUIET=1 uv run --with pillow --with numpy python prepare-tesla.py
MISE_QUIET=1 bash install.sh
MISE_QUIET=1 bash run-gh3-skin.sh
```

The shared library is in `assets/libraries/gh3-band-v1/`. Editable source packs are in `assets/stage-packs/`; compiled packs are in `assets/stages/<id>/`. The song exporter writes `assets/stages/events/`. These generated files are local and are excluded from Git. The installer copies them into the isolated game. The source copy supplies 159 song archives; 74 have title and artist metadata.

The screen-color, outline, particle, and Tesla shaders use the installed Clone Hero Unity `2022.3.62f2` format and OpenGL. Use `prepare-stage-shaders.py --game <path>` if the game is in another directory. Their sources are in `src/Stage/Shaders/`. Rebuild the shared library's shader bundles when these sources change.

Particle sorting also needs `render/particle-materials.json` in the shared library. `prepare-particle-materials.py` exports the 144 original PC shader variants. Use `--source <MaterialLibrary.bin.xen>` to select the original material library.

Tesla rendering needs `render/tesla.bundle` and `render/tesla-noise.png`. `prepare-tesla.py` exports the original noise image and records its source hash in `render/tesla-material.json`. Use `--data <GH3 DATA directory>` to select the original installation.

Use `GH3_SKIN_STAGE=off` to use the stock background. A missing or invalid stage leaves the game running with its stock background and writes the cause to the loader log.

For the included 28-second effect test:

```bash
MISE_QUIET=1 GH3_SKIN_PREVIEW_SONG="$PWD/charts/GH3 Stage Preview" \
  bash run-gh3-skin.sh --preview
```

It has pyro at 6 and 8 seconds, sparks at 11 seconds, fireworks at 12 and 20 seconds, smoke at 15 seconds, a blackout at 16 seconds, and a strobe at 18 seconds. These times use the song clock. Capture script times start when the gameplay scene loads.

## Where cues come from

Build **0.8.113** reads the standard `CAMERAS`, `LIGHTSHOW`, `ANIMS`, `DRUMS`, and `CROWD` note tracks from `.chart` as well as MIDI. Notes keep their numeric IDs and durations; MIDI velocity is retained in runtime cues. `DRUMS` is the animation track, separate from playable drums. No JSON direction file is required. This track support does not yet implement every GH3 command or provide a Moonscraper editor UI.

Build **0.8.113** first checks for one native `<song>_song.pak.xen` in the song folder. When present, it reads direction directly from the native QB data, with the original Expert-note gestures and fretbar timing. This native path takes priority over the sources below. See [NATIVE-GH3.md](NATIVE-GH3.md) for the camera edit tool and current playback limits.

Without a native song PAK, a version 2 `stage-events.json` selects a source for each channel when the song loads:

1. Events in the song file, or a matching `clearChannels` entry.
2. Explicit direction events in `notes.chart` or `notes.mid`.
3. The original GH3 song direction library, when `originalSongLookup` is `auto` (the default).
4. Default stage and band behavior.

A channel groups related commands. Camera commands form one channel. All lighting state commands form another. Each performer's stance, action, and body-clip commands form one body channel. Face, walking, arm control, strum style, strums, frets, drums, and each effect type have separate channels. The loader replaces the selected channel for the whole song, combines the retained events, and sorts them by time. Events at the same time retain their order within their source.

Explicit file or chart strum cues take priority over gestures derived from Expert guitar/bass notes. Those note-driven gestures take priority over the original library. The playable chart's tempo map always takes priority over file or library timing. Without a chart tempo map, timing comes from the file, the original library, or the shared default tempo, in that order.

An empty version 2 `events` array inherits available directions. `clearChannels` removes inherited cues from the named channels and selects their default behavior. It cannot include `timing`, and a file cannot both clear and supply the same channel. Set `originalSongLookup` to `off` to skip original-song matching. `offsetSeconds` shifts only events in this file.

Version 1 files retain complete-timeline replacement, including an empty `events` array. Without a song file, the existing chart-over-library behavior is retained. Original library matching uses song title **and** artist. It ignores case, accents, spaces, and punctuation. Ambiguous matches are skipped.

The library supplies the original GH3 cue times. Songs with edited audio or a different recording can need an offset. To make a song-specific copy:

```bash
MISE_QUIET=1 python3 export-stage-events.py \
  "$HOME/games/gh3-test-tools/game/DATA/SONGS/mycurse_song.pak.xen" \
  --output "/path/to/Clone Hero song/stage-events.json"
```

The command creates a new file. It does not replace an existing file. Set `offsetSeconds` in that file to shift all cues. A positive value runs cues later. Earlier lighting cues still establish the correct state when the offset is negative.

Songs without venue cues use the stage's initial lighting state. They do not get made-up pyro events. The loader log gives the selected cue source and count. For version 2, it also lists each supplied or cleared channel and its source. Packs without an authored lighting bank use the importer's basic fill lights.

## Supported events

In a chart's `[Events]` section, use text such as:

```text
2304 = E "stage pyro"
4608 = E "stage fireworks"
6144 = E "stage blackout"
6528 = E "stage lights red"
```

The tick positions use the chart's resolution, tempo map, and `Offset`. MIDI files use their PPQN division and tempo events across tracks. MIDI formats 0 and 1 are supported.

| Text event | Result |
| --- | --- |
| `stage pyro` | Flame jets, sparks, and residual smoke |
| `stage sparks` | Spark fountains |
| `stage fireworks` | Rising rockets and colored bursts |
| `stage smoke` | Smoke columns |
| `stage flash` | Brief stage light flash |
| `stage blackout` | Original blackout state, with fill lights on |
| `stage strobe` | Strobe mode; GH3 note 60 controls each toggle |
| `stage lights NAME` | Color or mood change |
| `[bonusfx]`, `[bonusfx_optional]` | Pyro |
| `[lighting (NAME)]` | Color or mood change |

Colors: `red`, `green`, `blue`, `yellow`, `orange`, `purple`, `magenta`, `cyan`, and `white`. Moods: `intro`, `verse`, `chorus`, `solo`, `flare`, `blackout`, and `strobe`. Other Rock Band lighting and camera events are not translated.

MIDI text events are read from `EVENTS`, `VENUE`, `GH3 VENUE`, and `LIGHTSHOW`. GH3 note events are read only from `GH3 VENUE` and `LIGHTSHOW`:

| GH3 note | Meaning |
| --- | --- |
| 39–53 | Default blend or a blend from 0 to 1 second |
| 56 | Pyro |
| 57, 58 | Next light state; 57 keeps beam positions, 58 uses saved positions |
| 60 | Strobe toggle |
| 61–69 | Purple, orange, clear override, magenta, white, yellow, blue, red, green |
| 70–76 | Strobe, flare, blackout, solo, chorus, verse, intro |

These mappings were checked against `LightShow_NoteMapping` in the local GH3 `scripts/fx/lightshow.qb`. The exporter also reads `LightShow_SetTime` and the older `LightShow SetTime` script events. It lists other lightshow scripts under `unmappedLightshowScripts`.

### Event file

Use this camera-only version 2 file to keep inherited lighting, band, and effect cues:

```json
{
  "version": 2,
  "library": "gh3-band-v1",
  "events": [
    { "time": 0, "type": "camera", "value": "wide", "duration": 8 },
    { "time": 24, "type": "camera", "value": "guitarist", "duration": 4 }
  ]
}
```

Save it as `stage-events.json` beside the song's chart. See the [full channel list and event fields](CUSTOM-CONTENT.md#6-song-direction-version-2). The runtime checks authored action and clip references against the installed role profiles. A malformed file or conflicting channel produces a loader diagnostic and uses default stage behavior.

The legacy version 1 format replaces all song direction:

```json
{
  "version": 1,
  "offsetSeconds": 0,
  "events": [
    { "time": 0, "type": "lighting", "value": "blue" },
    { "time": 6, "type": "pyro", "duration": 0.6 },
    { "time": 12, "type": "fireworks" },
    { "time": 16, "type": "lighting", "value": "blackout" }
  ]
}
```

Times are seconds on the chart timeline. `duration` controls emission time for fountains and smoke, up to 30 seconds. Fireworks and flashes have fixed lifetimes. The exporter stores original note events as `gh3-note` with the MIDI note number in `value`.

Chart/MIDI text direction also accepts `stage clip ROLE ID`, `stage arms ROLE MODE`, `stage strum-style ROLE STYLE`, and `stage strum ROLE MASK`. A text strum has zero sustain duration; use JSON `duration` for an authored sustain. `stage fret NOTE` and `stage drum NOTE` use the original animation-note maps. Playable `PART DRUMS` notes do not automatically become drum-animation cues.

## Rendering and timing

Kaiju Megadome has 356 visible mesh parts and 73,957 referenced vertices after GLB import and drum alpha cutouts. The converter applies saved transforms to movable props and uses each node's `CreatedAtStart` flag. The importer converts glTF coordinates and triangle order for Unity. Six crowd meshes contain 1,340 audience cards. Their textures update from the animated crowd atlas described below.

The stage camera uses its own layer. The player camera clears depth before it draws the highway. Stage setup saves camera masks and clear flags; teardown restores them. Scene changes release the stage meshes, textures, and materials. A song restart or practice seek reuses the stage geometry.

Lights and particles use `GameManager.songTime`. Particle positions are calculated from each cue's age. They do not depend on accumulated frame time. Thus a held clock freezes effects, a backward seek reproduces them, and a forward seek does not fire every skipped cue. Particle capacity is limited to 2,048 glowing particles and 256 smoke particles.

### Streak hand fire

From **0.8.110**, a **100-note streak** starts flames and sparks on both hands of the stage guitarist. The effect uses the same live streak counter as the HUD. Star Power does not change its threshold. A miss or overstrum stops emission. Existing particles finish within 0.8 seconds, and a new 100-note streak starts the effect again.

`prepare-hand-fire.py` exports `GP_4X_FingerFlames01` and `GP_4X_FingerSparks01` from `qb.pak.xen`, plus their original images from `global.pak.xen`. It preserves emission rates, lifetimes, speeds, sizes, colors, spread, and force. The flame texture has four frames in a 2 × 2 layout at 20 fps. Each hand emits 24 flames and 64 sparks per second from `Bone_Hand_Middle_Mid_L/R`. The right spark emitter uses the source script's reversed direction. Additive camera-facing quads use two reusable mesh buffers.

GH3 starts this effect with `GuitarEvent_Multiplier4xOn`. This port uses the requested **100-note** trigger. Particles use the song clock and sampled hand poses. A pause freezes them. A song change or seek clears the old trails; a streak at or above 100 starts fresh particles at the new position. The translated particle motion approximates GH3's native particle controller.

To upgrade, run `prepare-hand-fire.py` and `install.sh`. The export adds two PNG images and `hand-fire.json`. Missing hand-fire assets disable this effect and add a message to the loader log.

### Original venue lighting

`prepare-lighting.py` reads venue light nodes, snapshot tables, mood mappings, color overrides, and override exclusions from the owner's source files. Kaiju Megadome's pack has **37 scene lights**, **13 snapshots**, and **76 beam housings** per snapshot. This includes seven lights that are absent from its first snapshot. Venue meshes retain their original light groups. Band, crowd, backdrop, neon signs, and stage geometry receive their own light settings.

The player selects the original mood snapshots and follows song blend times. It cycles the mood's snapshot list on snapshot-change cues. Strobes follow note toggles and preserve beam positions. Color overrides leave the source's excluded fill lights unchanged. Snapshot selection uses the normal performance tier.

`Blackout01` turns off the beam volumes but keeps fill lighting. Its band ambient intensity is **0.25**. In My Curse, the first blackout begins at **2.086 seconds** and uses the song's **1.6-second** fade. The renderer no longer turns this cue into a near-black tint over every material.

Diffuse lighting uses surface normals, light directions, and source attenuation ranges. The renderer calculates fixed light weights before playback. Animated band lighting samples the current skinned positions and normals. Reusable native buffers limit allocation during playback. This keeps the existing transparent hair and clothing materials. Beam positions, rotations, colors, sizes, and density come from the selected snapshot.

Unity draws this translated lighting. GH3's original shaders, specular response, shadows, and beam-volume shader are not reproduced exactly. The beam volumes use additive cone meshes. The original per-light values are preserved in `lighting.json`.

## Band and cameras

The band has four performers:

| Role | Original model | Instrument |
| --- | --- | --- |
| Singer | `Singer_Male`, GH3's default male singer | Audio-Technica microphone and stand |
| Guitarist | `axel_1`, Axel Steel | Black Gibson SG |
| Bassist | `Bassist` | Tobias bass (`tobB4`) |
| Drummer | `Drummer` | Animated drum parts and sticks (`Drum_Set001`), with the venue's fixed drum kit |

The converter reads the original skeletons from `global.pak.xen` and its PAB file. It checks each skeleton against its inverse bind matrices. It converts 29 skinned mesh parts with 33,371 vertices after prop alpha cutouts and hair merging, with 471 bones. It selects color textures by material template, so normal maps are not used as visible color textures.

`prepare-direction.py` reads the original character state tables and exports 1,051 body, hand, drum, walking, and facial clips. Body selection uses the current stance, queued action, and song tempo. Stance changes normally wait for the current clip; `no_wait` commands interrupt it. The player supports original transitions, repeat counts, cycle flags, and commands that disable automatic arms. Body changes blend over 0.2 seconds. The converter samples poses at 30 fps; Unity interpolates them at the display rate.

Guitar and bass use original fret, chord, and strum layers. Expert song notes set strum times and chord masks; original animation notes select fret positions. Hand rotation deltas apply in parent space. This keeps the grip aligned with the neck. A two-bone arm solver puts the hands at the instrument targets. Drum notes select the original limb strokes and cymbal clips. Original facial commands start the named song clip at its authored time. Walking commands use venue waypoints and the shared original walk cycle, with speed taken from its root movement.

The exporter removes unavailable clip references and records them in `missingClips`. The runtime falls back to an available idle group when an action has no converted clip. Song clips and hand layers load before playback. When direction assets are absent, the older two-clip band animation remains available. For versions before 0.8.103, rerun `prepare-band.py` and `install.sh` to update the fallback hand grips.

The band uses the original venue start positions. Mesh skinning runs in Unity. Clone Hero strips the managed `BoneWeight1` type, so the loader sends validated weight buffers through the available `Mesh.InternalSetBoneWeights` entry point. The band, camera, and effects all read the song clock. A held clock keeps the same pose and camera view. A seek samples the new time directly.

The drum texture's orange corners have zero alpha in the source. Clone Hero's available particle shader does not discard them in cutout mode. `gh3_mesh_cutout.py` trims opaque drum meshes to the source alpha mask and cutoff during conversion. It preserves texture coordinates, surface normals, vertex colors, triangle order, and skin weights. The drums keep depth writes. For versions before 0.8.104, rerun `prepare-stage.py`, `prepare-band.py`, and `install.sh` with the dependencies shown above.

### Drummer hand targets

From **0.8.109**, `StageDrumHands` applies the drummer's two-bone arm IK after the body and hit layers. The original `drummer_static_tree` requires this step. It moves each wrist to `Bone_IK_Hand_Slave_L/R` and applies the target's palm rotation. The solver uses the original negative-Z elbow hinge and cosine limits of **−0.97 to 0.97**. Arm lengths stay fixed. If a mixed pose puts a target beyond the arm's reach, the stick keeps its animated offset from the reachable grip. Stick rotations and finger animations remain authored.

The correction also applies to fallback drum animations. It uses the sampled song pose, so pauses and seeks reproduce the grip. Existing 0.8.108 assets work with the correction; run `install.sh` to update.

### Light housings and microphone base

`prepare-fixtures.py` exports the solid surfaces of the venue's **71 light housings** from three original models. The runtime shares their meshes and materials. Each housing follows the matching lightshow position and rotation, including fades between snapshots. Housings remain visible when their light beams switch off. The two tank searchlights keep their fixed source positions. Nodes marked `GO_NoHousing01` have no visible housing. Native flare cards and reflective glass passes are not part of the solid-housing export.

From **0.8.108**, housing movement has its own timing. Instant light fades can no longer cause instant turns. Position and rotation use cubic curves that preserve velocity when a new cue interrupts a move. Rotation curves are normalized. Moves take at least 0.3 seconds; large turns and longer authored fades take more time. Beams and housings use the same pose. Color cues, strobes, and note 57 continue the current move. Off snapshots supply authored aim angles, rather than a default rotation. Their housings move smoothly while dark. Song-time sampling makes pauses and seeks repeatable. This movement curve is a port approximation; it is not GH3's native fixture controller. Run `install.sh` to update from 0.8.107.

The microphone stand's underside uses a circular texture mask. In **0.8.107**, `prepare-band.py` applies the same source-mask geometry conversion used for the drums. This removes the opaque corners that were visible when the singer lifted or tilted the stand. To update an existing installation, run `prepare-fixtures.py`, `prepare-band.py`, and `install.sh`.

### Hair motion

`prepare-hair.py` reads the original Havok 4.6 ragdolls and the `guitarist_accessory_bones` list. It exports `band/hair.json` with **54 hair joints**: 15 for the singer, nine for Axel, 15 for the bassist, and 15 for the drummer. The export includes physical parent links, bind offsets, segment lengths, mass, friction, damping, angular limits, and head collision capsules. Two singer hair bones have different animation and physics parents; the solver uses their physics chain and converts its result back to the animation skeleton.

The runtime uses a translated spring solver with gravity, motion inertia, source cone limits, and head collision checks. It does not run Havok or reproduce its complete constraint solver. Source plane and twist limits remain in the export; the current solver applies a no-twist swing bounded by the cone limit. Each step is **1/120 second** on the song clock. Display frames interpolate between steps. A held clock keeps the hair still. One-second checkpoints reproduce hair motion after a seek. Scene teardown releases each performer's simulation state.

To add hair motion to an existing installation, run `prepare-hair.py` and `install.sh`. Hair physics also works with the fallback two-clip band animations.

### Hair layer order

`prepare-band.py` combines each performer's hair cards into one skinned mesh. Axel's two hair textures use a padded atlas that preserves wrapped texture coordinates. The runtime sorts all hair triangles back-to-front from their current animated positions and the stage camera direction. Hair uses alpha blending without depth writes, so clear parts of a card cannot hide another hair layer. This also allows layers from separate source meshes to draw in the correct order. The sorter uses reusable buffers.

For versions before **0.8.106**, rerun `prepare-band.py` and `install.sh` to enable hair sorting. Body and instrument surfaces retain depth writes. The stage root stays at the scene origin to preserve precision in bone transforms and small instrument parts. Camera layer masks separate the stage from the highway.

The automatic camera uses the selected venue's original positions, rotations, lens values, target bones, screen offsets, and shot groups. It translates `CameraCuts_NoteMapping` and the two-camera, orbit, pan, spline, and stepped-motion controls. Camera notes can select a shot group, force a type or name, set its duration, or enable and disable automatic cuts. Without an explicit duration, cuts use the original 3–8 second range and advance to the next original lighting downbeat. Selection avoids the previous shot type where the source rule requires it. A fixed random seed makes seeks repeatable. The highway camera stays fixed.

The bundled `GH3 Stage Preview` chart has explicit camera cues. It shows all four performers and returns to a wide view for the first fireworks sequence.

Use `GH3_SKIN_CAMERA=fixed` for the original wide stage view. To inspect one moving shot, set this variable to `wide`, `singer`, `guitarist`, `bassist`, `drummer`, or `orbit`. The default is `auto`.

Charts and MIDI text events can select a shot with `[stage camera singer]`, for example. An event file uses:

```json
{ "time": 12, "type": "camera", "value": "guitarist" }
```

The latest camera cue selects the shot group until the next camera cue. A named environment override takes priority. Original camera notes use `gh3-camera` events with the note number in `value` and the note length in `duration`. MIDI tracks named `CAMERAS`, `ANIM`, `DRUMS`, and `PERFORMANCE`, with an optional `GH3 ` prefix, supply original direction notes.

Custom chart text can also use `stage stance guitarist stance_b`, `stage perform guitarist jump`, and `stage walk guitarist guitarist_walk01`. Custom performance text starts immediately. Song event files can use the exported `role`, `immediate`, `loop`, `count`, and `disableArms` fields.

## Crowd animation

The crowd uses the original `crowd_ped_01` through `crowd_ped_04`, `crowd_pedf_1`, `crowd_pedf_2`, two prison models, and two biker models. `prepare-crowd.py` converts the 28-bone `GH3_ped` skeleton. Build 0.8.115 adds all eight source profiles, their 102 referenced clips from `perm_anims.pak.xen`, and the original hand variants. Stage packs select the model and orientation for each profile.

At runtime, 24 small animated figures render into a 1024 × 768 texture atlas. The venue's audience cards display its 24 cells. This animates all 1,340 spectators with 672 animated bones, rather than a separate skeleton for each spectator. Each group has a different time offset. The figures clap, sway, and raise their arms using the original clips, with smooth transitions between loops.

The atlas has a separate camera and layer. The stage and highway cameras cannot see its source models. A separate alpha pass supplies the silhouettes because the game's particle shader writes only color. Stage lighting applies to the audience cards. Scene cleanup releases the atlas, camera, meshes, and textures.

Crowd poses use the song clock. A pause holds the pose; a seek restores the selected animation at the new time. Source animation groups respond to tempo and player health. Lighter commands change the hands and animation, monitor performance, and toggle the venue's lighter meshes. The crowd remains on the venue's original flat cards. If the converted crowd assets are absent, the original static atlas remains available. See [event coverage](GH3-EVENT-COVERAGE.md#crowd-commands-in-08115) for remaining native behavior checks.

## Current scope

The renderer imports the original venue geometry, band, crowd models, animation clips, lighting snapshots, and lightshow timing. The shared QB runtime executes source venue and particle scripts. Translated OpenGL shaders draw source particles and Tesla effects. Scenery creation, movement, animation callbacks, and encore scripts have partial playback support. Full command coverage, shadows, and original-game composition comparisons remain incomplete. See [GH3 event coverage](GH3-EVENT-COVERAGE.md) for the verified scope and remaining work.

Original song cues now drive cameras, body actions, playing gestures, and facial clips. For other charts, expert guitar and bass notes supply playing gestures and the tempo map controls body speed. Such charts need explicit drum, face, and performance cues for those features.

This is a translation of the available data and script rules. GH3's native camera smoothing, hand-camera noise, spline interpolation, animation-speed selection, and walk start/stop controller are not reproduced exactly. Walking uses the original cycle with a short blend. Body selection uses the normal performance tier; player-score reactions and battle logic are not connected. Crowd reactions, stage divers, and non-facial performance-note commands are not implemented. Untranslated script commands are listed in each exported song file. Facial playback uses authored song clips; it does not generate lip sync from lyrics.

Only Kaiju Megadome has been checked visually. The mesh format can hold other converted stages, but their materials, props, and crowd atlases can need extra conversion work. Songs in packed archives can use the title/artist library; direct chart cue reading and song-local event files need loose files.

## Format references

- [SceneConv GH3 PC readers](https://github.com/atljp/thps-modding-resources/tree/main/Tools/SceneConv/Backend): binary layout reference for scenes, meshes, and materials.
- [GH3 key names](https://github.com/atljp/thps-modding-resources/blob/main/Tools/NodeQBC/Keys/gh3/gh3_keys.txt): names for node and lightshow checksums.
- [GH SDK skeleton template](https://github.com/atljp/thps-modding-resources/blob/main/Guitar%20Hero%20SDK/Resources/Templates/Skeleton.bt): skeleton layout reference.
- The local GH Toolkit `SKA/SkaFile.cs`: SKA block, time, and compression layout reference.
- The owner's `z_budokan` node data, texture dictionary, song metadata, and lightshow scripts: source data for the local conversion and cue-map checks.
- The owner's `guitar_cameras.qb`, `guitar_character.qb`, `guitar_hand_events.qb`, `guitar_midi_anim_funcs.qb`, and character animation tables: source rules for camera and performance playback. The local GH Toolkit QB decompiler was used to inspect scripts. The converters read the original PAK/QB/SKA data directly and do not need that decompiler.

## Checks on 2026-10-05

- Release build and install passed. The C# logic checks and eight Python checks passed.
- In-game captures at 1600 × 900 showed the venue, crowd, flame jets, sparks, fireworks, smoke, and light changes behind the HUD.
- Direct game screenshots at 2560 × 1440 showed the full HUD and stage. The selected image is `stage-preview-1440p.png`. The 1440p desktop video exceeded the visible desktop area and was rejected. Use the 900p `stage-preview.mp4` for motion review.
- An uninstrumented test with a silent chart named **My Curse** by **Killswitch Engage** selected `mycurse.json` automatically. It loaded 1,279 original cues and 18 pyro events. Captures showed the repeated jets in the original 45.274-second pyro sequence. This checks cue lookup and timing; it does not compare audio synchronization against the original recording.
- A temporary runtime probe held the stage clock at 6.35 seconds, jumped forward to 100 seconds, then returned to 6.35 seconds. The held and repeated frames had the same particle-position hash and 251 active particles. The forward jump had zero active particles. The probe also checked fireworks and blackout.
- The probe loaded the gameplay scene three times. Each check found one stage root and one stage camera. The probe was removed after the test. These were instrumented clock and scene tests, rather than menu-driven practice tests.

Local evidence is in `/tmp/opencode/gh3-stage-research/`: `mycurse.log`, `mycurse-frames/`, `lifecycle.jsonl`, and `lifecycle.mp4.log`.

### Band and camera checks

- The release build, C# logic checks, and 14 Python checks passed. The checks include compressed animation tracks, partial hand poses, quaternion signs, clip transitions, camera cues, and seek behavior.
- All 196 installed venue and band files matched the generated files by SHA-256.
- The runtime probe held song time at 6.35 seconds, jumped to 100 seconds, then returned to 6.35 seconds. Bone-pose hashes, camera transforms, particle counts, and particle-position hashes matched the held and repeated samples.
- Three gameplay scene loads each had one stage root, one stage camera, four performers, 471 bones, and 35 meshes with skin weights. The temporary probe was removed after the checks.
- These are instrumented clock and scene tests. They are not menu-driven pause or practice tests.

Band runtime evidence is in `/tmp/opencode/gh3-stage-research/band-lifecycle.jsonl` and `band-lifecycle.mp4.log`.

### GPU frame-time check and replacement preview

The first GPU preview is `band-preview-gpu.mp4`: **1600 × 900, 29 seconds, 60 fps**. It has 1,740 frames with continuous 1/60-second timestamps. Image differences in the highway confirm motion after the initial count-in. `band-preview-gpu.png` is a frame from this video. The depth-fix preview below replaces it.

The RTX 3060 Laptop GPU rendered the game through VirtualGL's EGL backend. Xvfb received the rendered images; FFmpeg captured them and NVENC encoded the video. This path works while the desktop is unavailable. The first second of capture was removed because encoder startup left a timestamp gap. The final video has no timestamp gaps.

Three runs used the same chart, resolution, and HUD. A temporary helper measured the wall-clock interval between Unity updates from song time 2 to 26 seconds, with no frame cap or VSync. These intervals include rendering and synchronous GPU readback through VirtualGL. They measure complete frame-loop throughput, rather than only the stage update function. They are not isolated GPU execution times.

| Run | Frames measured | Mean frame time | 95th percentile | Maximum |
| --- | ---: | ---: | ---: | ---: |
| Stock background (`GH3_SKIN_STAGE=off`) | 10,414 | 2.30 ms | 3.10 ms | 8.05 ms |
| Full stage, band, effects, and cameras | 7,298 | 3.29 ms | 4.22 ms | 11.63 ms |
| Full stage while recording | 6,545 | 3.67 ms | 5.20 ms | 15.23 ms |

No measured frame in these runs exceeded the 16.67 ms budget for 60 fps. The stage added about 0.98 ms to the mean frame time in the runs without video capture. These results cover the demonstration chart at 900p on this machine.

Local evidence is in `/tmp/opencode/gh3-stage-research/`: `gpu-off.json`, `gpu-budokan.json`, `gpu-highway-difference.txt`, and `gpu-full-difference.txt`. The capture run's raw intervals are in `band-preview-gpu.mp4.frames.json` beside the video. Its `.json` manifest includes the renderer, timing summary, source hashes, and video hash. The capture helper and script are in `headless/` and `run-gpu.sh` in the research folder. The helper skipped MIDI port discovery, enabled background rendering, and removed the frame cap. It was removed from the installed game after capture.

Uploaded files were checked against Filebin's SHA-256 values. These links expire on **2026-10-12**:

- [GPU band video, 900p60](https://filebin.net/gh3-band-gpu-6f5c70e37fc3/band-preview-gpu.mp4)
- [GPU band screenshot](https://filebin.net/gh3-band-gpu-6f5c70e37fc3/band-preview-gpu.png)

The earlier `band-preview.mp4` used software OpenGL after the desktop stopped in a system out-of-memory event. It had uneven frame pacing and repeated frames. The GPU video above replaces that preview. The software video's encoded frame rate does not show game performance.

### Band depth fix in 0.8.99

- Enabled depth writes for 14 alpha-blended band meshes and moved their draw order after opaque surfaces. Previously, rear hair and clothing surfaces could draw over nearer surfaces.
- Removed the stage's 10,000-unit world offset. The camera layer masks already isolate its geometry. This preserves transform precision around close instrument surfaces.
- Compared fixed-clock images at song times 3, 9, 15, 20, and 24 seconds. The original and corrected images are in `depth-before/` and `depth-write-blend/` under the research folder. A runtime check confirmed 35 band meshes with depth writes and a stage root at `(0, 0, 0)`.
- The release build, installation, and C# logic checks passed. The temporary runtime helper was removed after capture.

The updated `band-preview-depth.mp4` is **1600 × 900, 29 seconds, 60 fps**. It has 1,740 frames, no timestamp gaps, and no near-identical adjacent highway frames in the image-difference check. During recording, 6,645 measured frame intervals had a 3.61 ms mean, 5.21 ms 95th percentile, and 18.42 ms maximum. Two intervals exceeded 16.67 ms. The measurement method is the same as the GPU check above.

The video, `.log`, `.json`, and `.frames.json` files preserve the capture evidence. Uploaded hashes match the local files. These links expire on **2026-10-13**:

- [Updated band video](https://filebin.net/gh3-depth-fix-146b59259097/band-preview-depth.mp4)
- [Updated band screenshot](https://filebin.net/gh3-depth-fix-146b59259097/band-preview-depth.png)

### Animated crowd checks in 0.8.100

- The release build, installation, C# logic checks, and 16 Python checks passed. The new checks cover crowd atlas rows, cell wrap, and rejection of triangles that join separate audience cards.
- All 249 installed venue, band, and crowd files matched the generated assets by SHA-256.
- Atlas captures showed changed poses in all 24 cells. The alpha channel contains transparent backgrounds and opaque figures.
- A runtime probe held song time at 6.35 seconds, jumped to 100 seconds, then returned to 6.35 seconds. The 672 crowd bones had the same pose hash in the held and returned samples.
- Three scene loads each had one crowd atlas root, one crowd camera, one stage camera, and 24 crowd figures. Camera masks kept the atlas layer separate. These are instrumented clock and scene checks.

Runtime evidence is in `/tmp/opencode/gh3-stage-research/crowd-lifecycle.jsonl`, `crowd-lifecycle.log`, and `crowd-alpha-fixed/`. The test helpers were removed from the installed game. Capture cleanup also removes diagnostic child processes created during the test runs.

The first `crowd-preview.mp4` uses the `wide` camera override to show the audience. This override suppressed the chart's performer shots. The replacement below restores the normal camera sequence. The first capture is **1600 × 900, 29 seconds, 60 fps**, with 1,740 frames and no timestamp gaps. During recording, 5,217 frame intervals had a 4.60 ms mean, 6.10 ms 95th percentile, and 14.48 ms maximum. None exceeded 16.67 ms. The highway check found one near-identical adjacent frame pair. The video has `.log`, `.json`, and `.frames.json` evidence files.

Uploaded hashes match the local files. These links expire on **2026-10-13**:

- [Animated crowd video](https://filebin.net/gh3-crowd-01004753f404/crowd-preview.mp4)
- [Animated crowd screenshot](https://filebin.net/gh3-crowd-01004753f404/crowd-preview.png)

### Restored camera sequence preview

`crowd-band-preview.mp4` uses `GH3_SKIN_CAMERA=auto`. It follows the preview chart's camera cues and shows the singer, guitarist, drummer, bassist, and wide views with the animated crowd. Its manifest records this setting. Use `auto` for normal previews; named shot overrides are for shot inspection.

The replacement is **1600 × 900, 29 seconds, 60 fps**, with 1,740 frames and no timestamp gaps. During recording, 5,301 measured frame intervals had a 4.53 ms mean, 6.24 ms 95th percentile, and 23.73 ms maximum. Five intervals exceeded 16.67 ms. The video and installed binary hashes are recorded in `crowd-band-preview.mp4.json`; its `.log` and `.frames.json` files preserve the runtime evidence. Temporary capture helpers were removed after recording.

Uploaded hashes match the local files. These links expire on **2026-10-13**:

- [Animated crowd with normal camera sequence](https://filebin.net/gh3-crowd-camera-f72d92af0a5f/crowd-band-preview.mp4)
- [Performer-view screenshot](https://filebin.net/gh3-crowd-camera-f72d92af0a5f/crowd-band-preview.png)

### Original direction checks in 0.8.101

- Release build and installation passed. C# logic checks and 20 Python checks passed. Tests cover original camera-note lengths, queued and immediate stances, sparse bone masks, tempo changes, chart chords, and camera-track priority.
- An asset check read all **1,051** converted clips. It sampled the full **My Curse** timeline at 0.1-second intervals and checked that poses stayed finite and rotations stayed normalized. Backward seeks restored the same layered poses.
- **My Curse / Killswitch Engage** loads **7,767 cues**, including **53 camera cues**, **2,916 drum cues**, **10 guitarist stance cues**, **5 singer facial clips**, and **18 pyro events**.
- All **1,377** installed stage and cue-library files matched the generated files by SHA-256. The installed binary matched the release build.
- A runtime probe held the stage clock at 45.5 seconds, advanced to 180 seconds, then returned to 45.5 seconds. The band, crowd, camera, and effects returned to the same values. Three scene loads each had one stage root, one stage camera, four performers, 1,143 band and crowd bones, and 35 weighted band meshes. These were instrumented clock and scene checks.

`original-direction-preview.mp4` is **1600 × 900, 64 seconds, 60 fps**, with stereo game audio. It uses the owner's original My Curse audio stems and expert chart data, automatic library matching, and `GH3_SKIN_CAMERA=auto`. It has no added camera cues or held-clock overrides. The video shows original shot changes, stance changes, facial clips, drum motion, and the original pyro sequence. It has 3,840 frames with continuous 1/60-second timestamps. After the first five seconds, the highway-motion check found 17 near-identical adjacent pairs out of 3,539.

During recording, **12,457** Unity update intervals over song time 2–60 seconds had a **4.66 ms mean**, **7.07 ms 95th percentile**, and **51.73 ms maximum**. Twenty-two intervals exceeded 16.67 ms; four exceeded 33.33 ms. The RTX 3060 rendered through VirtualGL EGL. Timing includes rendering and synchronous readback, as in the earlier checks.

The video has adjacent `.log`, `.json`, and `.frames.json` evidence files. Its manifest includes hashes for the installed direction assets and song cue library. Additional evidence is in `/tmp/opencode/gh3-stage-research/direction/`: `conversion.log`, `lifecycle.jsonl`, `lifecycle.log`, `contact.png`, and `video-motion.json`. Temporary capture and runtime-probe DLLs were removed after recording.

Uploaded hashes match the local files. These links expire on **2026-10-13**:

- [Original direction preview with audio](https://filebin.net/gh3-original-direction-01927668fe95/original-direction-preview.mp4)
- [Singer facial-performance screenshot](https://filebin.net/gh3-original-direction-01927668fe95/original-direction-preview.png)

## Lighting checks on 2026-10-06 (0.8.102)

- Release build and installation passed. C# lighting checks and all 21 Python checks passed. The checks cover blackout fades, color exclusions, note-driven strobes, beam-position controls, custom color cues, and backward seeks.
- All **390** exported light settings matched the original snapshot colors, intensities, and attenuation ranges. All **1,378** installed stage and cue-library files matched the generated assets by SHA-256. The installed binary matched the release build.
- A fixed-clock GPU capture at **3.8 seconds** in My Curse shows the band under the original blackout fill lights. All beam densities are zero at this point. The full song's light values were checked at 0.1-second intervals.
- Three runtime scene loads each had **391 lit venue and band meshes**, **76 beam housings**, and **35 weighted band meshes**. Holding at 3.8 seconds, seeking to 52.7 seconds, and returning restored identical vertex-color hashes. The blackout had zero active beams; the later state had 71. These were instrumented clock and scene checks.

Local lighting evidence is in `/tmp/opencode/gh3-stage-research/lighting/`. The [blackout screenshot](https://filebin.net/gh3-original-lighting-cda47c59374f/original-lighting-blackout.png) was checked against Filebin's SHA-256 value. The link expires on **2026-10-13**.

The final `original-lighting-preview.mp4` is **1600 × 900, 64 seconds, 60 fps**, with stereo game audio and the normal automatic camera sequence. It has **3,840 frames** with continuous 1/60-second timestamps. The highway-motion check found **one** near-identical pair among **3,539** pairs after the first five seconds. The preview uses the original My Curse chart and audio, with no held-clock override.

During recording, **6,763** Unity update intervals over song time 2–60 seconds had an **8.58 ms mean**, **11.47 ms 95th percentile**, and **33.39 ms maximum**. Ten intervals exceeded 16.67 ms; one exceeded 33.33 ms. Fixed venue light weights and prepared band light samples removed repeated lighting calculations. Timing includes rendering and VirtualGL readback on the RTX 3060. A private PipeWire null sink supplied the capture audio.

The video has adjacent `.log`, `.json`, and `.frames.json` files. The lighting research folder also contains `asset-validation.json`, `lifecycle.jsonl`, `lifecycle.log`, `final-contact.png`, `video-motion.json`, and `upload.json`. Temporary capture and probe DLLs were removed. All capture processes were stopped.

Uploaded hashes match the local files. These links expire on **2026-10-13**:

- [Original lighting preview with audio](https://filebin.net/gh3-original-lighting-cda47c59374f/original-lighting-preview.mp4)
- [Updated performer screenshot](https://filebin.net/gh3-original-lighting-cda47c59374f/original-lighting-preview.png)

## Hand grip checks on 2026-10-06 (0.8.103)

The hand layers used the wrong rotation order. This turned the left-hand grip away from the neck on the guitarist and bassist. The runtime now applies rotation deltas in parent space. The fallback converter uses the matching order in GH3 coordinates. The band assets were regenerated and installed.

C# checks passed, including a rotation-order regression, sparse layer masks, all **1,051** converted clips, the full My Curse direction track, and backward seeks. All **19** Python format checks passed. The release build had no warnings or errors.

Before-and-after GPU captures use identical stage times of **18, 28, and 40 seconds** with automatic cameras. The close views show the guitarist's fingers around the neck. `grip-comparison.png` compares cropped images at 18 seconds. Local evidence is in `/tmp/opencode/gh3-stage-research/grip/`.

The installed binary and all **97** band files match the build and generated assets. The new preview has **3,840 frames**, **64 seconds**, **1600 × 900 resolution**, and stereo audio at **60 fps**. It uses normal playback and automatic cameras. The motion check found seven near-identical highway frame pairs among 3,539 pairs after five seconds.

During recording, 6,814 Unity update intervals had an **8.51 ms mean**, **11.58 ms 95th percentile**, and **43.54 ms maximum**. Twenty-four intervals exceeded 16.67 ms; one exceeded 33.33 ms. The video has adjacent `.log`, `.json`, and `.frames.json` files. Temporary helper DLLs and capture processes were removed.

Uploaded hashes match the local files. These links expire on **2026-10-13**:

- [Updated preview with audio](https://filebin.net/gh3-hand-grip-4179f52fa6a0/grip-fixed-preview.mp4)
- [Hand grip before and after](https://filebin.net/gh3-hand-grip-4179f52fa6a0/grip-comparison.png)

## Drum alpha checks on 2026-10-06 (0.8.104)

The source drum atlas `e5184e79` has zero-alpha orange pixels outside the round drum heads. The runtime cutout mode drew those pixels as solid corners. A GPU comparison confirmed that alpha blending with depth writes removed the color but hid parts of the drums behind it. Disabling depth writes caused incorrect face order.

The converter now trims the affected venue and animated drum meshes to their source alpha masks. The contour tolerance is **0.35 texture pixels**. The fixed view has round drum heads with correct depth order. The comparison uses the same drummer camera, pose, and lighting at **40 seconds**, with the highway omitted for inspection.

All **25** Python checks passed. Four new checks cover mask holes, triangle winding, alpha thresholds, repeated texture coordinates, interpolated skin weights, normals, colors, and fully opaque or transparent masks. The release build passed with no warnings or errors. Evidence is in `/tmp/opencode/gh3-stage-research/drums/`.

- [Drum edges before and after](https://filebin.net/gh3-drum-edges-4ed13f599c4d/drum-cutout-comparison.png), checked against Filebin's SHA-256 value. The link expires on **2026-10-13**.

The final installed binary and **99** venue and band files match the generated files. The preview is **1600 × 900, 64 seconds, 60 fps**, with **3,840 frames**, stereo audio, and automatic cameras. The motion check found 21 near-identical highway frame pairs among 3,539 pairs after five seconds.

During recording, 6,178 Unity update intervals had a **9.39 ms mean**, **13.45 ms 95th percentile**, and **30.86 ms maximum**. No interval exceeded 33.33 ms. The video has adjacent `.log`, `.json`, and `.frames.json` evidence files. Temporary capture DLLs and processes were removed.

- [Updated preview with audio](https://filebin.net/gh3-drum-edges-4ed13f599c4d/drum-cutout-preview.mp4), checked against Filebin's SHA-256 value. The link expires on **2026-10-13**.

## Hair checks on 2026-10-06 (0.8.105)

C# checks passed for head-driven motion, attached roots, cone limits, identical results at **30, 60, and 144 fps**, held time, and backward seeks. All **54** exported joints remained finite and normalized through the first minute of My Curse. All **27** Python checks passed, including original Havok fields and invalid packfile bounds. The release build had no warnings or errors.

Before-and-after GPU images use the same automatic camera and song time of **56.5 seconds**. The updated hair has a separate simulated pose. The normal-playback video has **3,840 frames**, **64 seconds**, **1600 × 900 resolution**, stereo audio, and automatic cameras at **60 fps**. The singer close-up near the end shows the hair motion. The motion check found three near-identical highway frame pairs among 3,539 pairs after five seconds.

During recording, 6,445 Unity update intervals had a **9.00 ms mean**, **12.83 ms 95th percentile**, and **31.38 ms maximum**. No interval exceeded 33.33 ms. The video manifest matches the installed binary and `band/hair.json`. Temporary capture DLLs and processes were removed. Evidence is in `/tmp/opencode/gh3-stage-research/hair/` and the video's adjacent `.log`, `.json`, and `.frames.json` files.

Uploaded hashes match the local files. These links expire on **2026-10-13**:

- [Hair motion preview with audio](https://filebin.net/gh3-hair-motion-f2d43cb456d5/hair-motion-preview.mp4)
- [Hair pose before and after](https://filebin.net/gh3-hair-motion-f2d43cb456d5/hair-motion-comparison.png)

## Hair layer checks on 2026-10-06 (0.8.106)

The previous depth-writing hair cards hid other layers through clear texture pixels. Fixed triangle order also caused rear layers to cover nearer layers at some camera angles. The converter now produces one sortable hair mesh per performer: **2,046 vertices and 2,540 triangles** in total. These meshes draw back-to-front without depth writes.

Before-and-after GPU captures use the same pose and lighting at **56.5 seconds**, with eight camera angles for each of the four performers. The singer's rear views show the clearest difference: the large holes between hair layers are removed. Evidence is in `/tmp/opencode/gh3-stage-research/hair-sorting/angles-before/` and `angles-after/`.

All **29 Python checks** passed, including wrapped atlas sampling and preserved vertex attributes and skin weights. C# checks passed for camera-angle changes, animated vertex changes, and stable order for equal-depth triangles. The release build had no warnings or errors. File checks confirmed that body and instrument geometry, source textures, animations, and hair physics data match 0.8.105. The installed binary and all **1,305 stage files** match the local build.

The normal-camera recording has **3,840 frames**, **64 seconds**, **1600 × 900 resolution**, and stereo audio at **60 fps**. During recording, 6,132 Unity update intervals had a **9.46 ms mean**, **13.42 ms 95th percentile**, and **30.32 ms maximum**. No interval exceeded 33.33 ms. Video timestamps are continuous; 18 of 3,539 highway frame pairs after five seconds were near-identical. The video manifest matches the installed binary and all stage assets. Temporary capture DLLs and processes were removed.

Uploaded hashes match the local files. These links expire on **2026-10-13**:

- [Hair layer preview with audio](https://filebin.net/gh3-hair-sorting-d1c0de7fe0b2/hair-sorting-preview.mp4)
- [Hair layers before and after](https://filebin.net/gh3-hair-sorting-d1c0de7fe0b2/hair-sorting-comparison.png)

## Light housing and microphone checks on 2026-10-06 (0.8.107)

The venue's light housings are separate `.mdl` assets in `global.pak.xen`. The previous port drew the beams but did not load these models. The new fixture export contains **71 housings** from three shared models. All housing IDs have matching entries in every lighting snapshot. Runtime checks confirmed that all 71 housings load.

The microphone stand's bottom face used an opaque square where the source texture specifies a circular alpha mask. The correction trims this face to that mask and keeps its original skin weights. Fixed-clock GPU comparisons show the housing at **40 seconds** and the lifted microphone base at **44.5 seconds**. The latter makes the square-to-circle correction visible. Files are in `/tmp/opencode/gh3-stage-research/fixtures/`.

All **32 Python checks** passed, including fixture selection, fixed housing positions, and opaque prop masks. C# checks passed, including the original hair and lighting checks. The release build had no warnings or errors. Asset comparison found changes only in the singer's microphone-stand mesh among the existing band meshes. The installed binary and all **1,316 stage files** match the local build.

The normal-camera video has **3,840 frames**, **64 seconds**, **1600 × 900 resolution**, and stereo audio at **60 fps**. During recording, 5,618 Unity update intervals had a **10.32 ms mean**, **15.21 ms 95th percentile**, and **38.72 ms maximum**. Two intervals exceeded 33.33 ms. Video timestamps are continuous; 31 of 3,539 highway frame pairs after five seconds were near-identical. The video manifest matches the installed binary and all stage assets. Temporary capture DLLs and processes were removed.

Uploaded hashes match the local files. These links expire on **2026-10-13**:

- [Light housing and microphone preview](https://filebin.net/gh3-fixtures-b9a049600801/fixtures-preview.mp4)
- [Before-and-after comparison](https://filebin.net/gh3-fixtures-b9a049600801/fixtures-comparison.png)

## Light housing movement checks on 2026-10-06 (0.8.108)

Some My Curse cues set the light fade to zero. In 0.8.107, housings used that fade and could turn **80 degrees in one update**. Blackout and flare snapshots also have different authored aim angles. The source scripts use those angles; the defect was instant movement, rather than a reset to a default rotation.

The movement curve now has separate timing. A full-song comparison covered **980 lighting changes** and **76 beam tracks**. The largest rotation discontinuity fell below **0.00001 degrees**. At 60 fps, the largest sampled turn was **2.93 degrees per frame**, with a peak speed below **176 degrees per second**. All light energy, color, beam density, size, and range samples matched the previous lighting logic.

The C# checks passed, including interrupted movement, zero-time fades, dark movement, strobe timing, equivalent quaternion signs, pauses, frame rates, and backward seeks. The release build passed with no warnings or errors. All **1,316 stage assets** are unchanged from 0.8.107, and the installed assets and binary match the local build.

During normal playback, a temporary probe compared all **71 housing transforms** and their active beams against the motion tracks at **1,531 times**. The largest position error was below **0.000001 units**; the largest quaternion error was below **0.000001**. It also confirmed movement while the lights were dark. Evidence is in `/tmp/opencode/gh3-stage-research/fixture-motion/`: `after-motion.json`, `runtime-motion.json`, `runtime-summary.json`, and `installed-validation.json`.

The automatic-camera preview has **3,840 frames**, **64 seconds**, **1600 × 900 resolution**, and stereo audio at **60 fps**. During recording with the motion probe, 5,851 Unity update intervals had a **9.91 ms mean**, **13.86 ms 95th percentile**, and **31.54 ms maximum**. None exceeded 33.33 ms. Video timestamps are continuous. Nine of 3,539 highway frame pairs after five seconds were near-identical. The video manifest matches the installed binary and all stage assets. Temporary capture DLLs and processes were removed.

The uploaded hash matches the local file: [smooth housing movement preview](https://filebin.net/gh3-fixture-motion-566c6e5f701f/fixture-motion-preview.mp4). The link expires on **2026-10-13**.

## Drumstick grip checks on 2026-10-06 (0.8.109)

The source `drummer_static_tree` applies `Drummer_IK_Params` after it combines body and drum-hit animations. The port omitted this arm adjustment. The hand targets and sticks followed the hit clips, while the palms could miss those targets by **0.535 metres**. This was a missing IK step. The guitar correction in 0.8.103 addressed the order of additive hand rotations.

The correction uses the original hand targets, palm rotations, elbow hinge, and hinge limits. It preserves the arm lengths and the animated stick-to-grip offsets, including poses that exceed the arm's reach. C# checks covered **7,591 song poses** at 30 fps, fallback animations, reach limits, finger and body channels, held clocks, and backward seeks. The full direction, hair, lighting, and skin logic checks passed. The release build had no warnings or errors. All **1,316 stage assets** match 0.8.108.

A separate 0.1-second full-song scan found a maximum error below **0.000001 metres** for both reachable hand targets and stick-to-grip offsets. Fixed-clock GPU comparisons checked both hands at **20, 37.2, 40, 53.2, and 84.8 seconds**. The ten in-game grip offsets also matched within **0.000001 metres**. Each pose was held across display frames before capture. The comparisons show the sticks held in the hands during drum hits.

Evidence is in `/tmp/opencode/gh3-stage-research/drumsticks/`: `before-summary.json`, `after-summary.json`, `before-synchronized/`, `after-synchronized/`, `runtime-validation.json`, and `asset-validation.json`.

[Drumstick grip comparison](https://filebin.net/gh3-drumsticks-fd388efe6ecb/drumsticks-comparison.png). The uploaded hash matches the local file. The link expires on **2026-10-13**.

The normal-camera preview has **3,840 frames**, **64 seconds**, **1600 × 900 resolution**, and stereo audio at **60 fps**. During recording, 5,934 Unity update intervals had a **9.77 ms mean**, **13.73 ms 95th percentile**, and **27.66 ms maximum**. None exceeded 33.33 ms. Video timestamps are continuous; 23 of 3,539 highway frame pairs after five seconds were near-identical. The video manifest matches the installed binary and all stage assets. Temporary capture DLLs and processes were removed.

The uploaded hash matches the local file: [corrected drumstick preview](https://filebin.net/gh3-drumsticks-fd388efe6ecb/drumsticks-preview.mp4). The link expires on **2026-10-13**.

## Streak hand-fire checks on 2026-10-06 (0.8.110)

The release build passed without warnings or errors. All **33 Python checks** passed, including the new QB pair/vector-array reader used by the particle exporter. The C# checks passed with the original assets. They cover the 99-to-100 boundary, both hand attachments, later milestones, streak breaks and recovery, particle expiry, pauses, song resets, forward and backward seeks, and matching particle motion at **30, 60, 120, and 144 fps**.

A GPU run sampled the live game streak and hand-fire state **2,137 times**. The hands had no particles at **99 notes**. They became active at **100 notes**, at **20.912 seconds** in My Curse. Every sampled state matched the threshold. The largest sample had **108 particles**. Close-up captures show both hands with the original flame and spark images. The comparison hides the effect for the left image and shows it for the right image at the same pose.

The installed binary matches the release build. All **1,319 stage files** match the local assets. The earlier 1,316 files are unchanged; this version adds the hand-fire definition and two textures. Evidence is in `/tmp/opencode/gh3-stage-research/hand-fire/`, including `runtime.json`, `runtime-summary.json`, `installed-validation.json`, and `closeups/`.

[Hand-fire comparison](https://filebin.net/gh3-hand-fire-7390d10c14bb/hand-fire-comparison.png). The uploaded hash matches the local file. The link expires on **2026-10-13**.

The normal-camera preview has **3,840 frames**, **64 seconds**, **1600 × 900 resolution**, and stereo audio at **60 fps**. During recording, 5,706 Unity update intervals had a **10.17 ms mean**, **14.16 ms 95th percentile**, and **29.03 ms maximum**. None exceeded 33.33 ms. Video timestamps are continuous; 16 of 3,539 highway frame pairs after five seconds were near-identical. The video manifest matches the installed binary and all stage assets. Temporary capture DLLs and processes were removed.

The uploaded hash matches the local file: [100-note streak hand-fire preview](https://filebin.net/gh3-hand-fire-7390d10c14bb/hand-fire-preview.mp4). The link expires on **2026-10-13**.

### Version 2 song direction checks

Build **0.8.112** passed the channel-priority, clearing, role, action-reference, offset, equal-time ordering, chart/MIDI timing, and version 1 compatibility checks. The original-asset regression checks passed for 1,051 clips, hair, drum hands, hand fire, lighting, and all ten venue packs plus Studio.

A GPU check used My Curse with a camera-only version 2 file. It loaded **6,962 cues**, including the original **10 guitar stance cues**, **2,916 drum cues**, **5 singer facial cues**, and **18 effects**. The source log confirmed that cameras came from the file, guitar strums came from the playable chart, and timing came from its tempo map. Three inspected frames showed the requested stage, guitar, and drummer shots with inherited lighting and band playback.

A second GPU check added `clearChannels: ["pyro"]`. It loaded **6,944 cues** and **zero effects**, with the same inherited stance, drum, and face counts. The final installed binary matches this capture and the release build. The build had zero warnings and errors. Local evidence is in `assets/stage-packs/song-direction-v2-check.json` and `assets/stage-packs/.showcase/song-direction-v2*.mp4` with their manifests and logs.
