# 3D stage and song effects

The first supported venue is GH3's **Kaiju Megadome** (`z_budokan`). It uses the owner's original meshes, textures, band models, and performance animations. The camera moves between stage and performer views. The stage runs behind the highway during the supported single-player guitar mode.

Latest preview: [corrected hair layers with automatic cameras, 900p60 with audio](https://filebin.net/gh3-hair-sorting-d1c0de7fe0b2/hair-sorting-preview.mp4), version **0.8.106**. The link expires on **2026-10-13**.

## Prepare and install

The local GH3 source is `~/games/gh3-test-tools/game/DATA`. Change the input paths below if needed.

```bash
MISE_QUIET=1 uv run --with pillow --with 'shapely>=2.1' python prepare-stage.py \
  --pak "$HOME/games/gh3-test-tools/game/DATA/ZONES/z_budokan.pak.xen"
MISE_QUIET=1 uv run --with pillow --with numpy --with 'shapely>=2.1' python prepare-band.py \
  --data "$HOME/games/gh3-test-tools/game/DATA"
MISE_QUIET=1 uv run --with numpy python prepare-hair.py \
  --data "$HOME/games/gh3-test-tools/game/DATA"
MISE_QUIET=1 uv run --with pillow --with numpy python prepare-crowd.py \
  --data "$HOME/games/gh3-test-tools/game/DATA"
MISE_QUIET=1 uv run --with numpy python prepare-direction.py \
  --data "$HOME/games/gh3-test-tools/game/DATA"
MISE_QUIET=1 python3 prepare-lighting.py \
  --data "$HOME/games/gh3-test-tools/game/DATA"
MISE_QUIET=1 python3 export-stage-events.py \
  "$HOME/games/gh3-test-tools/game/DATA" --library --output assets/stages/events
MISE_QUIET=1 bash install.sh
MISE_QUIET=1 bash run-gh3-skin.sh
```

The venue converter writes `assets/stages/budokan/`. The band, crowd, and direction converters write its `band/`, `crowd/`, and `direction/` subfolders. The lighting converter writes `lighting.json` in the venue folder. The library exporter writes `assets/stages/events/`. These generated files are local and are excluded from Git. The installer copies them into the isolated game. The current source copy supplies 74 song direction tracks with 529 pyro triggers. When upgrading an older stage conversion, rerun `prepare-stage.py` to add light groups, crowd material tags, and the atlas layout.

Use `GH3_SKIN_STAGE=off` to use the stock background. A missing or invalid stage leaves the game running with its stock background and writes the cause to the loader log.

For the included 28-second effect test:

```bash
MISE_QUIET=1 GH3_SKIN_PREVIEW_SONG="$PWD/charts/GH3 Stage Preview" \
  bash run-gh3-skin.sh --preview
```

It has pyro at 6 and 8 seconds, sparks at 11 seconds, fireworks at 12 and 20 seconds, smoke at 15 seconds, a blackout at 16 seconds, and a strobe at 18 seconds. These times use the song clock. Capture script times start when the gameplay scene loads.

## Where cues come from

Cue priority:

1. `stage-events.json` in the current song folder. An empty `events` array also takes priority.
2. Venue events in the current `notes.chart` or `notes.mid`. They replace matching channels in the original library. For example, a custom camera track replaces the original camera track and keeps the original lighting and band cues.
3. The original GH3 direction library, matched by song title **and** artist. Matching ignores case, accents, spaces, and punctuation. Ambiguous matches are skipped.

The library supplies the original GH3 cue times. Songs with edited audio or a different recording can need an offset. To make a song-specific copy:

```bash
MISE_QUIET=1 python3 export-stage-events.py \
  "$HOME/games/gh3-test-tools/game/DATA/SONGS/mycurse_song.pak.xen" \
  --output "/path/to/Clone Hero song/stage-events.json"
```

The command creates a new file. It does not replace an existing file. Set `offsetSeconds` in that file to shift all cues. A positive value runs cues later. Earlier lighting cues still establish the correct state when the offset is negative.

Songs without venue cues use the venue's `Intro01` lighting state. They do not get made-up pyro events. The loader log gives the selected cue source and count. Older stage conversions without `lighting.json` use the earlier blue-light fallback.

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

## Rendering and timing

The venue has 356 visible mesh parts and 75,566 vertices after drum alpha cutouts. The converter applies saved transforms to movable props and uses each node's `CreatedAtStart` flag. It converts GH3 coordinates and triangle order for Unity. Six crowd meshes contain 1,340 audience cards. Their textures update from the animated crowd atlas described below.

The stage camera uses its own layer. The player camera clears depth before it draws the highway. Stage setup saves camera masks and clear flags; teardown restores them. Scene changes release the stage meshes, textures, and materials. A song restart or practice seek reuses the stage geometry.

Lights and particles use `GameManager.songTime`. Particle positions are calculated from each cue's age. They do not depend on accumulated frame time. Thus a held clock freezes effects, a backward seek reproduces them, and a forward seek does not fire every skipped cue. Particle capacity is limited to 2,048 glowing particles and 256 smoke particles.

### Original venue lighting

`prepare-lighting.py` reads Kaiju Megadome's light nodes, snapshot table, mood mapping, color overrides, and override exclusions from the owner's source files. The export has **30 scene lights**, **13 snapshots**, and **76 beam housings** per snapshot. Venue meshes retain their original light groups. Band, crowd, backdrop, neon signs, and stage geometry receive their own light settings.

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

The converter reads the original skeletons from `global.pak.xen` and its PAB file. It checks each skeleton against its inverse bind matrices. It converts 35 skinned mesh parts with 33,277 vertices after drum alpha cutouts and 471 bones. It selects color textures by material template, so normal maps are not used as visible color textures.

`prepare-direction.py` reads the original character state tables and exports 1,051 body, hand, drum, walking, and facial clips. Body selection uses the current stance, queued action, and song tempo. Stance changes normally wait for the current clip; `no_wait` commands interrupt it. The player supports original transitions, repeat counts, cycle flags, and commands that disable automatic arms. Body changes blend over 0.2 seconds. The converter samples poses at 30 fps; Unity interpolates them at the display rate.

Guitar and bass use original fret, chord, and strum layers. Expert song notes set strum times and chord masks; original animation notes select fret positions. Hand rotation deltas apply in parent space. This keeps the grip aligned with the neck. A two-bone arm solver puts the hands at the instrument targets. Drum notes select the original limb strokes and cymbal clips. Original facial commands start the named song clip at its authored time. Walking commands use venue waypoints and the shared original walk cycle, with speed taken from its root movement.

The exporter removes unavailable clip references and records them in `missingClips`. The runtime falls back to an available idle group when an action has no converted clip. Song clips and hand layers load before playback. When direction assets are absent, the older two-clip band animation remains available. For versions before 0.8.103, rerun `prepare-band.py` and `install.sh` to update the fallback hand grips.

The band uses the original venue start positions. Mesh skinning runs in Unity. Clone Hero strips the managed `BoneWeight1` type, so the loader sends validated weight buffers through the available `Mesh.InternalSetBoneWeights` entry point. The band, camera, and effects all read the song clock. A held clock keeps the same pose and camera view. A seek samples the new time directly.

The drum texture's orange corners have zero alpha in the source. Clone Hero's available particle shader does not discard them in cutout mode. `gh3_mesh_cutout.py` trims opaque drum meshes to the source alpha mask and cutoff during conversion. It preserves texture coordinates, surface normals, vertex colors, triangle order, and skin weights. The drums keep depth writes. For versions before 0.8.104, rerun `prepare-stage.py`, `prepare-band.py`, and `install.sh` with the dependencies shown above.

### Hair motion

`prepare-hair.py` reads the original Havok 4.6 ragdolls and the `guitarist_accessory_bones` list. It exports `band/hair.json` with **54 hair joints**: 15 for the singer, nine for Axel, 15 for the bassist, and 15 for the drummer. The export includes physical parent links, bind offsets, segment lengths, mass, friction, damping, angular limits, and head collision capsules. Two singer hair bones have different animation and physics parents; the solver uses their physics chain and converts its result back to the animation skeleton.

The runtime uses a translated spring solver with gravity, motion inertia, source cone limits, and head collision checks. It does not run Havok or reproduce its complete constraint solver. Source plane and twist limits remain in the export; the current solver applies a no-twist swing bounded by the cone limit. Each step is **1/120 second** on the song clock. Display frames interpolate between steps. A held clock keeps the hair still. One-second checkpoints reproduce hair motion after a seek. Scene teardown releases each performer's simulation state.

To add hair motion to an existing installation, run `prepare-hair.py` and `install.sh`. Hair physics also works with the fallback two-clip band animations.

### Hair layer order

`prepare-band.py` combines each performer's hair cards into one skinned mesh. Axel's two hair textures use a padded atlas that preserves wrapped texture coordinates. The runtime sorts all hair triangles back-to-front from their current animated positions and the stage camera direction. Hair uses alpha blending without depth writes, so clear parts of a card cannot hide another hair layer. This also allows layers from separate source meshes to draw in the correct order. The sorter uses reusable buffers.

For versions before **0.8.106**, rerun `prepare-band.py` and `install.sh` to enable hair sorting. Body and instrument surfaces retain depth writes. The stage root stays at the scene origin to preserve precision in bone transforms and small instrument parts. Camera layer masks separate the stage from the highway.

The automatic camera uses Kaiju Megadome's original positions, rotations, lens values, target bones, screen offsets, and shot groups. It translates `CameraCuts_NoteMapping` and the two-camera, orbit, pan, spline, and stepped-motion controls. Camera notes can select a shot group, force a type or name, set its duration, or enable and disable automatic cuts. Without an explicit duration, cuts use the original 3–8 second range and advance to the next original lighting downbeat. Selection avoids the previous shot type where the source rule requires it. A fixed random seed makes seeks repeatable. The highway camera stays fixed.

The bundled `GH3 Stage Preview` chart has explicit camera cues. It shows all four performers and returns to a wide view for the first fireworks sequence.

Use `GH3_SKIN_CAMERA=fixed` for the original wide stage view. To inspect one moving shot, set this variable to `wide`, `singer`, `guitarist`, `bassist`, `drummer`, or `orbit`. The default is `auto`.

Charts and MIDI text events can select a shot with `[stage camera singer]`, for example. An event file uses:

```json
{ "time": 12, "type": "camera", "value": "guitarist" }
```

The latest camera cue selects the shot group until the next camera cue. A named environment override takes priority. Original camera notes use `gh3-camera` events with the note number in `value` and the note length in `duration`. MIDI tracks named `CAMERAS`, `ANIM`, `DRUMS`, and `PERFORMANCE`, with an optional `GH3 ` prefix, supply original direction notes.

Custom chart text can also use `stage stance guitarist stance_b`, `stage perform guitarist jump`, and `stage walk guitarist guitarist_walk01`. Custom performance text starts immediately. Song event files can use the exported `role`, `immediate`, `loop`, `count`, and `disableArms` fields.

## Crowd animation

The crowd uses the original `crowd_ped_01` through `crowd_ped_04`, `crowd_pedf_1`, and `crowd_pedf_2` models. `prepare-crowd.py` converts the 28-bone `GH3_ped` skeleton and two original cheering clips for each model from `perm_anims.pak.xen`.

At runtime, 24 small animated figures render into a 1024 × 768 texture atlas. The venue's audience cards display its 24 cells. This animates all 1,340 spectators with 672 animated bones, rather than a separate skeleton for each spectator. Each group has a different time offset. The figures clap, sway, and raise their arms using the original clips, with smooth transitions between loops.

The atlas has a separate camera and layer. The stage and highway cameras cannot see its source models. A separate alpha pass supplies the silhouettes because the game's particle shader writes only color. Stage lighting applies to the audience cards. Scene cleanup releases the atlas, camera, meshes, and textures.

Crowd poses use the song clock. A pause holds the pose; a seek samples the new time directly. The crowd remains on the venue's original flat cards. It does not move through the venue or change its cheering state with player performance. If the converted crowd assets are absent, the original static atlas remains available.

## Current scope

The renderer imports the original venue geometry, band, crowd models, animation clips, lighting snapshots, and lightshow timing. Unity effects reproduce pyro, sparks, fireworks, smoke, and lighting changes. The original GH3 particle scripts and shaders do not run inside Clone Hero. Venue-specific scripted set changes are not implemented.

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
