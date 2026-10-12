# Current status and known gaps

Version **0.9.10** (`src/Packages/ModInfo.cs`; it reads package format 1.0 of all kinds: `chstage/1.0`, `chchar/1.0`, `chinst/1.0`, `chlib/1.0`). Release readiness: the release zips build and install, but nothing is published ([PACKAGING-PLAN.md section 10](PACKAGING-PLAN.md#10-before-a-release)).

## Platforms

| Platform | Status |
| --- | --- |
| Linux | Supported. Clone Hero `v1.1.0.6142-final` (64-bit) with MelonLoader 0.7.3. All tests and recordings run on Linux (OpenGL Core 4.5) |
| Windows | Supported, with the same `Gh3Skin.dll` and Direct3D 11 (the Windows default). Tested only under Wine 11.17 and DXVK 3.1.1 on Linux, not on Windows hardware ([Release build](#release-build)) |
| macOS | Not supported (PACKAGING-PLAN decision 7). The macOS build draws with Metal only, and the shader bundles have no Metal programs. MelonLoader 0.7.3 has no arm64 release, and Clone Hero's hardened runtime blocks the loader ([spike/macos.md](spike/macos.md)) |

The mod checks the game version and the platform at start. On another version or platform it logs `GH3 skin requires Clone Hero …` and stays off.

## Complete

- **Highway and notes.** GH3 camera and highway geometry (measured from the original GH3 recording), Axel's highway texture, GH3 rails, beat lines with GH3's thin half-beat lines up to the highway top, regular, HOPO, tap, and Star Power phrase gems with their 30 fps animation, cyan active Star Power gems, GH3 sustain bars with the held glow, whammy bend, and star whammy.
- **HUD.** Score (`num_a9`), streak counter (`num_a7`) with counters of five or more digits, amp and multiplier (captured GH3 DXT5 textures), lamps, rock meter with native health thresholds, six Star Power bulbs with the GH3 entrance, ready banner, active Star Power rails, "Note Streak!" and "Star Power Ready" messages, GH3 hit flames and sparks, intro HUD entrance, and "You Rock!" at the end of the song.
- **Stage.** All ten GH3 venues. Four performers with original rigs, instruments, clips, hair physics, fret-hand fingers, drummer IK, and 100-note hand fire. Crowd impostors per row. Original cameras with GH3's shot selection and geometry. Lightshow snapshots, beams, housings, and node flags. GH3 particles, Tesla, movies, outlines, screen color. All 921 used materials draw with their translated GH3 shader templates. Pre-song intro and post-song win sequences.
- **Direction.** `stage-events.json` versions 1 and 2, chart and MIDI direction tracks, the original song library for 159 songs, the Rock Band `VENUE` track (RB1/RB2 and RB3; camera cuts, lighting, pyro, fog), generated cues from chart sections for charts without direction, and singer lip sync from the chart's vocals (`PART VOCALS` notes and lyrics, or `.chart` lyrics) on songs without GH3 facial clips. Shared command dispatch for original and authored commands. The mod does not read GH3 song PAKs; the original song library comes from the band library package.
- **Packages.** The mod loads all stages, the band library, the character library, and the characters from package format 1.0 ([PACKAGE-FORMAT.md](PACKAGE-FORMAT.md)): `Mods/GH3Skin/Packages/`, `Custom/Stages`, `Custom/Characters`, `Custom/Instruments`, and `venue.chstage` in a song folder. It imports each package one time into a per-machine cache with BC1/BC3 textures, and it decodes the song's clips on worker threads before the song ([PACKAGE-FORMAT.md section 20](PACKAGE-FORMAT.md#20-c-importer)). Unused cache folders are deleted at start. The older install (`Mods/GH3Skin/assets/stages`, `assets/libraries`) was removed in step 6.
- **Pickers.** In the menus, F6 opens the stage and band picker ([PLAYER-GUIDE.md](PLAYER-GUIDE.md)): a stage, **Random**, or **Song's choice**, with preview images; a character for each band role; a guitar for the guitarist and a bass for the bassist; the direction options (Rock Band `VENUE` tracks, generated direction, reduce flashing). The default stage is **Song's choice**: the song's stage, else Kaiju Megadome (`gh3.budokan`), else a random stage. A song can select its stage (`venue.chstage` in the song folder, `gh3_stage` in `song.ini`, or the GH3 career venue of a GH3 song); the player can turn this off. A character on a band library profile plays on the rig of its role; a GH3 guitarist character plays on its own GH3 rig. In another role, a character has the instrument of that role from the library band model, unless the player selects an instrument.
- **Creator tools.** The Blender add-on CH Stage Tools (Blender 4.2 or later; tested on 4.2.23 and 5.2.1) makes, opens, validates, and exports stages (`.chstage`) and characters (`.chchar`) on the fixed GH3 rigs and on the GH3 guitarist profiles of the character library. The ten GH3 venues and the four GH3 band members open and export again within the round-trip tolerances ([BLENDER-ADDON.md](BLENDER-ADDON.md)). A stage with a script bank keeps its own lights unless the bank has GH3 `LevelLight` objects (fixed 2026-10-09; before, such a stage was dark). [CREATOR-GUIDE.md](CREATOR-GUIDE.md) and the examples in `examples/` (a stage and a character, CC0-1.0) show the workflow. Characters can have their own hair, skirt, and tail chains with body colliders and spring-bone stiffness, drag, and a bend limit (PACKAGE-FORMAT section 9.5; the add-on makes them along a strand, from bones, from MMD physics, or from VRM spring bones; 2026-10-10: the Project SEKAI twin tails no longer fold over the head, [BLENDER-ADDON.md 2.12](BLENDER-ADDON.md#212-chains)), and can keep the skeleton of their model with blink and lip sync shape keys: the add-on finds the humanoid in any skeleton and guides the set-up ([Characters on their own skeleton](#characters-on-their-own-skeleton), [Any humanoid model](#any-humanoid-model)).
- **Release build.** `scripts/build-release.sh` makes reproducible zips for Linux and Windows and the add-on zip ([VERIFICATION.md](VERIFICATION.md#release-build), [Release build](#release-build)).
- **GH3 characters.** All 72 playable GH3 characters ship as packages: Axel Steel, Casey Lynch, Izzy Sparks, Judy Nails, Johnny Napalm, Lars Umlaut, Midori, and Xavier Stone in two outfits with four styles each, Slash, Tom Morello, Lou and Thick Izzy, God of Rock, Grim Ripper, Bret Michaels, and the female singer ([PARITY-NOTES.md](PARITY-NOTES.md#playable-characters), [PIPELINE.md](PIPELINE.md#gh3-characters)). They keep their GH3 meshes, materials (all on translated GH3 templates, no fallback), textures, hair cards, and default GH3 guitar. Each guitarist plays on its own GH3 skeleton with its own animation set (stances, idles, strums, fret and finger sets, solos, intro, win and lose clips), ragdoll hair and accessories, IK, and scale (Judy Nails 0.9), from the character library `gh3-characters-v1` ([GH3 characters](#gh3-characters)). Each guitarist character plays the guitarist (with its guitar) and the bassist (with its own animation set and the library bass, as in GH3 co-op). Since 2026-10-11 every character also plays the other roles with the role's GH3 clips on its own rig ([Any character in any role](#any-character-in-any-role)). Since 2026-10-11 the female singer and Bret Michaels also play on their own GH3 skeleton, animation set, and ragdoll ([Singers](#singers)). Checked on gh3.dive, 38 runs, every variant in a close-up as guitarist or bassist, each character family in both roles, the two singers.
- **GH3 instruments and highways.** All 88 guitars and 20 basses of GH3 ship as instrument packages (`gh3.*.chinst`, [PACKAGE-FORMAT.md section 9.4](PACKAGE-FORMAT.md#94-instrument-chinst)); the picker and `GH3_SKIN_INSTRUMENT_*` select one for the guitarist and the bassist, with any character. The highway follows the player's character, as in GH3 (`create_guitarist`, [PARITY-NOTES.md](PARITY-NOTES.md#instruments-and-highways)): the 13 GH3 highways are in the guitarist character packages; a creator instrument can bring its own highway. Checked on gh3.dive and gh3.budokan with My Curse (2026-10-10): 21 guitars and basses of all body shapes in close-ups, the hands on the neck and strings; the highway of the default band, of five characters, of a creator instrument, and of the bassist for a bass part; the default highway against `main` ([evidence](#evidence-instruments)).

## GH3 characters

Since 0.9.9 each GH3 guitarist plays on its own GH3 rig profile of the character library `gh3-characters-v1` ([PACKAGE-FORMAT.md section 10.1](PACKAGE-FORMAT.md#101-character-library), [PARITY-NOTES.md](PARITY-NOTES.md#playable-characters)). Checks on 2026-10-09, gh3.dive, My Curse, game copy `~/games/clonehero-gh3-character-motion`:

| Check | Result |
| --- | --- |
| Clip data against GH3 (13 families as guitarist: Axel outfit 2 is the `axel` set; Casey, Izzy, Judy, Johnny, Lars, Midori, Xavier, Slash, Tom Morello, Lou, God of Rock, Grim Ripper) | The `[trace] band` lines of each run (74 song times from −6 s to 30 s, 4 bones) against the GH3 `.ska` clip sampled on the profile's GH3 skeleton at the same clip time: maximum 0.001 mm and 0.016°, all 12 runs (Axel's own run uses the library profile and is checked by the band library). The same for six outfit 2 profiles (Axel, Judy, Johnny, Izzy, Midori, Lou's Thick Izzy) and for Judy Nails as bassist with Lars Umlaut outfit 2 as guitarist (`tools/check-character-trace.py`). Each family plays its own clips: in these runs, the only body clip of Axel's set that another family plays is one `stance_b` clip of Lars Umlaut, which GH3's `LARS_animations` names. The God of Rock plays Axel's set, as in GH3 |
| Pictures | Each family at song times 4–28 s (full body; [evidence](#evidence-gh3-characters)): its own stance, idle, and strum; fret-hand fingers on the neck; the strum hand at the strings; hand fire after the 100-note streak; Judy Nails at 0.9 |
| Hair | Judy Nails: the hair swings with the head (video frames at 4 per second); all 20 profiles load their ragdoll (Grim Ripper: none, as on the GH3 PC port). God of Rock (2026-10-09, fix-rockgod-hair): on Axel's skeleton his hair covered his face; his rig now has the accessory bones of `GH3_Guitarist_RockGod`, which his ragdoll and mesh fit ([PARITY-NOTES.md](PARITY-NOTES.md#god-of-rock-hair)), and his hair and beard hang down as in his bind pose (gh3.artdeco close-ups, same camera sequence as before). Original GH3 check of the God of Rock (front and side close-ups): hair and beard hang from the head and swing with it, as in the mod before and after this change; the two mod states look nearly the same ([PARITY-NOTES.md](PARITY-NOTES.md#god-of-rock-hair)). All 19 profiles with hair, 240 s of My Curse outside the game (`GH3_CHARACTER_HAIR_ASSETS`): fastest hair joint 3.0–11.7 m/s; the other 18 profiles are byte-equal to before Hair motion against the original GH3 (2026-10-10, fix-hair-motion): GH3 simulates only the accessory bodies, with no spring to the bind pose; the mod's solver now uses the GH3 Havok values (limit frames and planes, speed limits, friction, soft limits, chain coupling). With the head path of GH3 recordings, the God of Rock's hair bounced 1.9–2.5 times as fast as in GH3 (1.5–12 Hz) and now 0.6–0.9 times; Judy Nails and Izzy Sparks stay near GH3 and their hair is nearer to the GH3 pose ([PARITY-NOTES.md](PARITY-NOTES.md#hair-dynamics)) |
| Bassist | Judy Nails as bassist with her own set and the library bass in her hands; Lars Umlaut outfit 2 as guitarist in the same run |
| Default band and Axel | Unchanged: the band library and Axel's and the singers' packages are byte-equal to `main`. Screenshots at song times 4–20 s against `main`: 32–47 dB PSNR, as two runs of the same build (32–42 dB) |
| Clip prefetch | 151–159 band clips for each song (157 with the default band), decoded in 29–59 ms on worker threads; no clip loads on the main thread; managed heap at the stage load 208–218 MB (`main`: 191–229 MB) |
| Original GH3 | One run of the original GH3 (quickplay My Curse, bot, only Judy Nails left as a guitarist profile; the patched `qb.pab.xen` was restored): GH3 plays My Curse in its career venue, not in the Dive, so the cameras differ. Judy's GH3 stance and strums look as in the mod; this is a visual check, not a measurement. The pose data is checked against the GH3 files (first row) |

### Singers

Owner report (2026-10-11): "The female singer has an incredibly elongated neck." Cause: the export moved her mesh to the male singer skeleton (`gh3-singer-male-v1`), and she played the male animation set. Her face followed the male head bone, which is 17 cm higher than hers, so the neck between her chest and her face was about 45 % too long. Now the two singer packages are on the character library profiles `gh3-singer-female-v1` and `gh3-singer-bret-v1` (their GH3 skeleton, animation set, ragdoll, and `singer_IK_Params`; [PARITY-NOTES.md](PARITY-NOTES.md#singers)). Checks on 2026-10-11, gh3.dive, game copy `~/games/clonehero-gh3-singer-rigs`:

| Check | Result |
| --- | --- |
| Neck (bind pose, package GLB) | Chest to head bone 0.305 m before, 0.211 m now (the GH3 skeleton: chest 1.255 m, neck 1.392 m, head 1.466 m). The vertices that follow `bone_neck` span 0.109 m before and 0.079 m now (the GH3 model). Blender render of the GH3 model at its bind pose against the earlier package: `sheet-bind-render.jpg` |
| Clip data against GH3 | `tools/check-character-trace.py` on the `[trace] band` lines: female singer in Hit Me With Your Best Shot (157 lines, her set clips, 3 song facial clips made on her skeleton): body 0.000 mm and 0.0065°, face bones 0.009 mm and 0.011° against the `.ska` clips on `GH3_Singer_Female`. Bret Michaels in Talk Dirty to Me (138 lines): body 0.000 mm and 0.004°, face 0.011 mm and 0.009° on `GH3_Singer_BretMichaels` |
| Original GH3 | Quickplay Hit Me With Your Best Shot and Talk Dirty to Me in the Dive (bot; GH3 selects the female singer and Bret Michaels from the song; the patched `qb.pab.xen` and the save were restored byte-equal, and all 907 files of the GH3 `DATA` folder have their earlier SHA-256). At the same song times and camera shots, her height, neck, and microphone stand look as in the mod now (`sheet-full-body.jpg`, `sheet-close-up.jpg`, video). The idle clips differ, because GH3 and the mod select them at random |
| Lip sync | "Yr Not Going Anywhere" (Rock Band chart, 197 syllables, vocal stem): the female singer's mouth moves as before, now with her normal neck (`sheet-lipsync.jpg`) |
| Default band | The band library, Axel's packages, and the 66 guitarist packages do not change; the guitarist part of `characters.json` and all 1,995 guitarist clips are byte-equal. Screenshots against `main` (My Curse, 7 GH3 camera times and 5 singer close-ups): 24.9–45.5 dB PSNR; two runs of this build: 23.1–54.9 dB |
| Hair | 240 s of My Curse outside the game (`GH3_CHARACTER_HAIR_ASSETS`): fastest hair joint 6.79 m/s (female), 3.31 m/s (Bret), below the warning speed |

Evidence: `~/.cache/singer-rigs/` (`cap/`: before and after captures with logs; `gh3-hitme/`, `gh3-talkdirty/`: original GH3 recordings; `sheet-*.jpg`; `render-*.png`; `singer-female-vs-gh3.mp4`, the mod and GH3 side by side at song 14–54 s).

### Evidence (GH3 characters)

`~/.cache/gh3-evidence/character-motion/` (frame strips, runs, logs, the trace check script `verify_trace.py`). God of Rock hair: `~/.cache/rockgod-hair/` (before and after videos and logs, `god-of-rock-before-after.mp4`; in `evidence/`: close-up pairs, the bind-pose render, the frame strip of the other characters, the measurement scripts and their output). Original GH3 check of the God of Rock: `~/.cache/rockgod-gh3/` (`gh3/front4`, `gh3/side1`: GH3 recordings; `mod/`: mod close-ups before and after; `gor-gh3-before-after.mp4`; `tools/`). Hair motion against GH3: `~/.cache/hair-motion/` (`gh3/`: GH3 recordings with the sampled Havok bodies; `mod/`: mod close-ups before and after; `bench/`: solver runs; `video/hair-motion-gh3-before-after.mp4`; `strip/families-after.jpg`; `evidence-plot.png`).

### Evidence (instruments)

`~/.cache/instruments/`: `strip-*` (close-ups of the guitarist and bassist with 18 instruments), `hw-*` (highway runs: default, `main`, characters, a creator instrument, the bass part), `budokan/`, `picker/` (picker frames and log), `importtest/` (cold import of the 108 instruments outside the game), `video/instruments-highways.mp4` (42 s, game audio), `gh3check/` (the original GH3 attempts and their restore logs). Small images: `/tmp/opencode/instruments/`.

## Toon characters

Since 2026-10-10 a character or instrument material can use toon (cel) shading, an inverted-hull outline, and hair layers that show the eyebrows over the bangs ([PACKAGE-FORMAT.md section 6.5.1](PACKAGE-FORMAT.md#651-toon-materials-and-hair-layers), [CREATOR-GUIDE.md section 5.6](CREATOR-GUIDE.md#56-make-the-materials-toon-look-and-eyebrows-over-the-hair)). The stage shader draws them (the surface model modes 3 and 4 in `Stage.glsl`, one program for OpenGL Core and Direct3D 11). GH3 materials do not use these modes. Checks on 2026-10-10 with Project SEKAI Miku (the SEKAI shadow color and value textures, outline width, eyebrows over the hair) and Tda Miku (from the MMD Tools data: toon ramps, sphere maps, edges; the eyebrows split from the face material), game copy `~/games/clonehero-gh3-toon`:

| Check | Result |
| --- | --- |
| GH3 band unchanged | gh3.dive and gh3.budokan, default band, 8 song times each, against `main`: mean difference 0.8–7.7 (0–255) for 15 of 16 frames; two runs of `main` give 0.5–5.7. The 16th frame (gh3.dive 60 s, a moving camera) differs by 16.7 against `main` and by 16.9 between two runs of this build: run-to-run noise |
| Eyebrows over the hair | `GH3_SKIN_DEBUG_HAIR_LAYER=mark`, head-locked views: front 1,031 and 1,084 marked pixels (452 and 849 with the depth test only), back and back from above 0 for both characters |
| Other band members | Toon guitarist and toon bassist, 8 views from behind the guitarist's head to the bassist's head, both orders: no feature showed through the other character's hair |
| Venue light | The characters take the color of the venue light (red, green, blue, and pink moods in gh3.dive and gh3.budokan) and get dark in blackouts |
| Windows Direct3D 11 (Wine, DXVK) | Same frames as Linux OpenGL; the marked pixels of each view are within 13 pixels of Linux. Windows OpenGL Core also draws |

Limits: the shading uses the key light and the ambient light of the band light group, not the GH3 spot lights one by one. The outline keeps its screen width (since 2026-10-11 also at every field of view, see [Neutral faces](#neutral-faces)); the per-vertex edge scale of MMD and the second outline normals of Project SEKAI (`_OUTLINE_SECOND_NORMAL`) are not used, so hard edges can show gaps. The Project SEKAI face shadow (SDF texture) is not used: the face uses the normal toon shading. The eyebrow mark uses a facing limit (`N·V` ≥ 0.3), so an eyebrow at a grazing angle is drawn only where the depth test passes.

Evidence: `~/.cache/toon/` (`before/`, `after/`, `it*/` head views, `hl/` hair-layer counts, `band/` two-character views, `win/` Windows frames, `video/toon-dive.mp4`, `scripts/` build scripts, `out/` packages). Small sheets: `/tmp/opencode/toon/`.

## Characters on their own skeleton

Since 2026-10-10 a creator character can keep the skeleton of its model (PACKAGING-PLAN decision 13, [PACKAGE-FORMAT.md section 9.6](PACKAGE-FORMAT.md#96-own-skeleton), [CREATOR-GUIDE.md section 5.10](CREATOR-GUIDE.md#510-use-any-humanoid-model-with-its-own-skeleton)). The GH3 rig of the profile plays the GH3 performance without a mesh, and the mod retargets it to the character's bones. Face blend shapes give blink and lip sync. GH3 characters and characters on a GH3 rig do not change. Checks on 2026-10-10, gh3.dive, My Curse and "Yr Not Going Anywhere" (Brad Sucks, a vocal chart), game copy `~/games/clonehero-gh3-retarget`, with Tda Miku Append (MMD) and Project SEKAI Miku (FBX from the game) on their own skeletons and the Midori animation set:

| Check | Result |
| --- | --- |
| Proportions | Joint ratios against the source model: torso/leg 0.469 (source 0.469), arm/leg 0.485 (0.486), upper arm/forearm 1.137 (1.141), thigh/shin 0.898 (0.898), shoulder/hip width 1.220 (1.208; the source values are rounded to 1 mm). SEKAI Miku: equal to the source. The fit to the Midori rig gave torso/leg 0.648, arm/leg 0.593, thigh/shin 0.755, shoulder/hip width 1.706 for both models |
| Poses | Side-by-side frames with Midori (her GH3 character package) at the same song times: the same stance, the crossed legs, the head turn, the guitar angle, and the hand places |
| Hands | `[trace] band` `handMiss` over 110 lines of each run: Tda Miku p50 0.01 cm, max 0.03 cm; SEKAI Miku max 0.00 cm (the instrument moves nearer when a goal is beyond the arm). Before the reach correction: p50 2.6 cm, max 8.4 cm (Tda Miku's shoulders are 4.4 cm narrower on each side than the scaled GH3 shoulders). Close-ups at 17 song times: the fret hand on the neck, the strum hand at the strings, hand fire on the hands |
| Twist | All 325 clips of the Midori set with the Python retarget (equal to the game within 0.005°): the largest twist step between adjacent arm bones is 45° (the forearm twist bones and their followers take 25 % steps), the hand against the forearm twist bone 0°, the upper arm against the shoulder 0° |
| Face | Singer with lip sync from 197 syllables: the mouth shapes change with the syllables (head-locked close-ups at 0.15 s steps); blink with the GH3 timing |
| GH3 characters | Midori against `main`: `tools/check-character-trace.py` 0.001 mm, 0.0073° (`main`) and 0.0092° (this build), 110 lines each. Screenshots against `main` at the same song times: default band 24.8–29.0 dB PSNR, Midori 28.7–40.1 dB (run-to-run noise: two runs of one build give 16.7–47 dB, [PACKAGE-FORMAT.md section 20.6](PACKAGE-FORMAT.md#206-characters)). The GH3 packages and their caches do not change |
| Performance | `GH3_SKIN_PROFILE=1`, 60 s: `band.retarget` (the GH3 sample, the retarget of 206 joints, the instrument, and the face) 0.12 ms per frame (median); `band.hair` 0.38 ms (40 chain joints); gen-0 collections 0.2 per second, as with Midori |
| Tests | `tests/RetargetLogicTests.cs` (identity, T-pose and bone rolls at three scales, twist bones, followers, root motion, leg and arm IK, the Python fixture), `tests/test_skeleton.py` (the name hints for MMD, VRoid, Mixamo, Unreal; the humanoid finder without names, turned, mirrored, with helpers; checks; the two fixtures), the Blender character test (own skeleton and any humanoid: 11 checks on 4.2.23 and 5.2.1) |

Limits: the GH3 facial clips drive only `a` (jaw) and blink (eyelids, past 20°); the other GH3 expressions have no shape. A role without a bone keeps its rest pose; spine bones between two mapped bones keep their rest pose relative to their parent. Sibling twist bones (Unity `ArmRoll`, `ForeArmRoll`) keep their rest pose under the arm; the forearm then takes half of the hand twist. The drum kit and the microphone stand do not move: a drummer with short arms can miss the drums. The Blender preview has no note-driven strum, fret, and finger animation (as for GH3 rigs). Windows was not run: the change has no shader or native code; it uses the blend shape methods of the spike (`Mesh.AddBlendShapeFrame`, `SkinnedMeshRenderer.SetBlendShapeWeight`).

Evidence: `~/.cache/retarget/` (`caps/`: frame runs and logs of `main` and this build; `video/`: the before and after videos, `retarget-miku.mp4`; `scripts/`: the Miku build scripts; `build/`: the packages). Small images: `/tmp/opencode/retarget/`.

### Any humanoid model

Since 2026-10-10 (fix-any-rig) the Blender add-on finds the humanoid in any skeleton from the hierarchy and the bone positions; the bone names are only a tie-breaker ([BLENDER-ADDON.md section 2.13](BLENDER-ADDON.md#213-own-skeleton)). **Set Up Skeleton** also prepares the model (transforms, front, mirrored exports, unit, rigid parts, Rigify deform parents), the **Skeleton** panel has a map editor with confidence marks, a map overlay, map presets, and **Check Clips** ([CREATOR-GUIDE.md section 5.10](CREATOR-GUIDE.md#510-use-any-humanoid-model-with-its-own-skeleton)). The runtime retarget now turns spine and neck joints between two roles part of the way, takes the hand direction and the IK target from another finger when there is no middle finger, and bends the arm and leg IK in the plane of the GH3 elbow or knee (before, a near-straight character limb could flip its bend: Check Clips found 51–166° flips in the solo clip on Seed-san and Alicia). The package format does not change.

Rig corpus (`tools/rig-corpus`, 30 models: 22 downloaded or generated, 8 synthetic variants of two of them), each run headless through the add-on flow and scored against a hand-made map (2026-10-10; Blender 5.2.1 and 4.2.23 give the same scores). Roles: the roles of the hand-made map that the automatic map gave; fixes: roles that a creator must change; to check: roles with a yellow or red mark; Check Clips: six clips of the Midori set with the retarget of the game:

| Model | Kind | Roles | Fixes | To check | Check Clips | In the game |
| --- | --- | --- | --- | --- | --- | --- |
| `xbot` | Mixamo, glTF, object scale 0.01 | 52/52 | 0 | 0 | no finding | guitarist, drummer |
| `soldier` | Mixamo, object rotation, a 2-joint left thumb, looks toward +Y | 49/49 | 0 | 0 | no finding | |
| `michelle` | Mixamo, object rotation and scale | 52/52 | 0 | 0 | no finding | |
| `samba` | Mixamo FBX, centimetres | 52/52 | 0 | 0 | no finding | |
| `polydancer` | 100Avatars FBX, Mixamo names, 10 cm tall | 40/40 | 0 | 0 | no finding | |
| `rpm` | Ready Player Me, eyes, morph targets | 54/54 | 0 | 0 | no finding | |
| `seed-san` | VRM 1.0, robot arms on a backpack | 53/53 | 0 | 0 | no finding | guitarist |
| `vrm1-twist` | VRoid names, aim and roll helper bones | 54/54 | 0 | 2 | no finding | guitarist, singer |
| `alicia` | VRM 0.x, looks toward +Y, 4 spine and 2 neck bones | 54/54 | 0 | 2 | no finding | |
| `unitychan` | Unity-chan, HumanIK names, twin tails | 52/52 | 0 | 32 | no finding | guitarist |
| `archer` | 3ds Max Biped, a bow in the left hand, 2 fingers and a thumb | 31/31 | 0 | 12 | no finding | guitarist |
| `warrior` | 3ds Max Biped, thighs under the spine, mitten hands, 7 mm tall | 30/30 | 0 | 9 | no finding | guitarist |
| `mannequiny` | Unreal-like names, no little finger | 45/45 | 0 | 0 | no finding | |
| `kaykit` | chibi, no fingers, no neck, IK and control bones | 18/18 | 0 | 2 | a foot slides in 7 % of the planted frames of the kick clip | guitarist |
| `cesium-man` | odd joint names, a Z-up parent node, no fingers | 19/19 | 0 | 0 | no finding | guitarist |
| `rigged-figure` | odd joint names, no fingers | 19/19 | 0 | 0 | no finding | |
| `rigify` | Blender Rigify (DEF bones under control bones, palm bones, twist halves) | 57/57 | 0 | 0 | no finding | guitarist |
| `tda-miku` | MMD (MMD Tools), Japanese names, D bones, twist bones | 57/57 | 0 | 0 | no finding | guitarist (new-flow build) |
| `sekai` | Unity export, two armatures, mirrored, 0.98 units tall | 52/52 | 0 | 0 | no finding | guitarist (new-flow build) |
| `syn-bip-cm-lying` | Xbot: 3ds Max Biped names, lying (90° on X), centimetres | 52/52 | 0 | 0 | no finding | |
| `syn-joints` | mannequiny: names `joint1`…`joint45` | 45/45 | 0 | 0 | no finding | guitarist |
| `syn-daz-spine` | Xbot: Daz Genesis names, 6 spine bones | 52/52 | 0 | 1 | no finding | |
| `syn-zh-mirrored` | mannequiny: Chinese names, mirrored, looks toward +X | 45/45 | 0 | 0 | no finding | guitarist |
| `syn-relaxed` | mannequiny: arms 75° down at rest | 45/45 | 0 | 0 | no finding | |
| `syn-nofingers-noneck` | Xbot: no fingers, no neck | 21/21 | 0 | 0 | no finding | |
| `syn-3fingers-nothumb` | Xbot: no thumb, no little finger | 40/40 | 0 | 0 | no finding | |
| `syn-helpers` | Xbot: IK, pole, control, and twist helper bones, a prop in one hand | 52/52 | 0 | 0 | no finding | |
| `robot-expressive` | parts that follow bones, feet not under the legs, three palm bones | 0/36 | 36 | — | — (Validate: the feet and the palms need a parent change in Edit Mode) | |
| `brainstem` | `Node_N` names, arms without elbows | not a humanoid in this sense | | | | |
| `godot-robot` | a robot without hands | not a humanoid in this sense | | | | |

27 of the 28 humanoid models map with no fix. RobotExpressive needs guided fixes (the panel note says "fewer than two limb pairs", and Validate names the bones whose parent is wrong); BrainStem and the Godot robot are not humanoids in this sense (the notes say "the arm chain has no elbow"). Run time: 1–9 s for each model in Blender.

In the game (gh3.dive, My Curse, game copy `~/games/clonehero-gh3-any-rig`): 11 corpus models as guitarist at 5 song times each (fret hand on the neck, strum hand at the strings), `vrm1-twist` as singer with blink and lip sync ("Yr Not Going Anywhere", 197 syllables), and `xbot` as drummer. `[trace] band` `handMiss` (78 lines each, 30 s): 0.00 cm for `xbot`, `unitychan`, `syn-joints`, `kaykit`, `archer`, `vrm1-twist`, and SEKAI Miku; Tda Miku p50 0.01 cm, max 0.02 cm (before: p50 0.01, max 0.03 cm). The two Mikus, made again with the new flow (no skeleton step in their scripts), have the same humanoid map, face, chains, and colliders as before (Tda Miku: two more followers, the weighted IK leg bones `足.L` and `足.R` now follow `足D`), and play as before (side-by-side frames). Default band against `main` at 5 song times: 27.8–40.7 dB PSNR (run-to-run noise: 16.7–47 dB).

Limits: the finder needs a skeleton with mirrored limbs, three joints in each arm and leg, and the legs below the arms; robots without hands or elbows are not supported. A model whose feet or palms are not children of the limb above them (IK rigs that export the controls) needs a parent change in Edit Mode. Three fingers map to index, middle, and ring (a model with the little finger instead of the ring finger gets the ring role on it). Sibling twist bones (Unity `ArmRoll`, Unreal `upperarm_twist_01`) keep their rest pose under the arm unless they start where a mapped bone starts. The jaw maps only by its name. VRM 0.x shape key names are lost in the plain glTF import (`target_0`…); pick the face shape keys by hand or use the VRM Add-on. The arm IK strength of the Blender preview and Check Clips comes from the GH3 hands near the instrument, not from the performance of the game. A chibi model (KayKit) plays, but its short arms hold the guitar inside its body. The hand-made maps of the corpus come from the bone names, the positions, and renders, made by the author of this change, not by a second person.

Evidence: `~/.cache/any-rig/` (`runs/r8/` corpus run with renders and packages, `caps/` game frames and logs, `video/any-rig-models.mp4` and `any-rig-blender-flow.mp4`, `build/` the new-flow Miku packages, `scripts/` the build and demo scripts, `SOURCES.md`). Small sheets: `/tmp/opencode/any-rig/`.

## Any character in any role

Since 2026-10-11 every character plays every band role, with one character model (PACKAGING-PLAN decision 15, [PACKAGE-FORMAT.md section 9.7](PACKAGE-FORMAT.md#97-character-model-and-band-roles)): a skeleton, a humanoid map, and the animation set. The GH3 rig of the set drives the skeleton through the humanoid map; when the rig is the skeleton (a GH3 character in its own role, a guitarist as the co-op bassist, the venue default), the set plays directly on it. In other roles a GH3 character plays the role's GH3 clips on its own rig, with its face bones, hair, and scale. The drum kit and the microphone stand take the size of the character. The picker offers every character for every role. One loader replaces the three loaders of fixed profiles, character library profiles, and own skeletons; `CharacterRig` and the role refusals are removed. Checks on 2026-10-11, game copy `~/games/clonehero-gh3-any-role`, gh3.dive and gh3.budokan, My Curse (`mycurse-native`) and "Yr Not Going Anywhere" (Rock Band chart):

| Check | Result |
| --- | --- |
| Own roles: poses | `[trace] pose` hashes (the bits of all local poses at song 5, 10, 20, 30, 45 s) equal to `main` for every member: the default band; Judy Nails guitarist with Lars Umlaut outfit 2 as co-op bassist; Axel Steel outfit 1 (fixed profile) with Tda Miku (own skeleton) as bassist; the female singer (Hit Me With Your Best Shot); Bret Michaels (Talk Dirty to Me) with the God of Rock |
| Own roles: clips | `tools/check-character-trace.py`, `main` / this build: Judy and Lars 116 lines 0.001 mm, 0.0070° / 0.0097°; female singer body 0.000 mm, 0.0080° / 0.0061°, face 0.007 / 0.005 mm; Bret body 0.001 mm, 0.0058° / 0.0068°, face 0.010 / 0.009 mm (sampling times differ between runs) |
| Own roles: hair, cache | Hair bench (60 s, 60 fps) output byte-equal to `main` for `band:guitarist`, `gh3-guitarist-judy-v1`, `gh3-singer-female-v1`, and `character:custom.miku`. `tools/PackageImport` cache of 7 packages (both libraries, Judy, Axel, the female singer, Tda Miku, KayKit): 4,656 files byte-equal, `cache.json` differs only in the import time |
| Own roles: frames | Screenshots against `main` (5 song times): default band 29.5–37.4 dB PSNR, Judy and Lars 23.9–32.7 dB, Axel and Miku 24.1–29.7 dB, singers 28.9–41.9 dB; two runs of `main`: 25.1–48.1 dB |
| Drummer | `[trace] drum` stick tip against the scaled GH3 stick tip at each drum note after 5 s (about 50 notes in each run): Lars Umlaut (tall: arm ratio 1.17), Axel Steel, SEKAI Miku (0.81), the default kit: 0.00 cm; KayKit Knight (chibi: hip ratio 0.64) p95 0.39 cm, max 0.62 cm; SEKAI Miku on gh3.budokan 163 notes p95 0.75 cm, max 1.6 cm. Count-off taps (arms up): KayKit up to 3.0 cm. Seat lift: KayKit −23 cm, Miku +11 cm, Lars +3 cm |
| Singer | When GH3 sings into the microphone (GH3 microphone head within 20 cm of the GH3 mouth), the character's microphone head to mouth distance is the GH3 distance times the stand scale: God of Rock 1.01 (stand 1.00), Judy Nails 0.89 (0.89), unitychan 0.79 (0.79), at most 19 cm. Judy Nails sings with her own face bones: GH3 facial clips on My Curse, lip sync with 197 syllables on "Yr Not Going Anywhere" (close-ups). xbot (no face) sings without face motion |
| Other roles: hands | `[trace] band` `handMiss` (108–117 lines each): female singer as guitarist, xbot as guitarist, KayKit as bassist, Judy and God of Rock as singers: 0.00 cm. Drummers between the notes: Lars, Axel 0.00 cm; SEKAI Miku and KayKit up to 6.1 and 8.6 cm when a GH3 hand is beyond their arm reach (the stick then turns to its tip) |
| Load time | `GH3_SKIN_TIMING=1`, stage load, median of 3: default band 948 ms (`main`) and 950 ms; three GH3 characters in their roles 1,128 and 1,126 ms; four characters in other roles 1,237 ms (new) |
| Tests | `tests/RetargetLogicTests.cs`: the GH3 rig maps to itself (52 roles), face bones keep their rest pose and take the GH3 jaw turn, the stand and kit scales, the throne, the hips on the scaled seat, the stick tips, bone lengths under a scale; the Python fixtures `retarget-singer.json` and `retarget-drummer.json` (the Blender preview equals the game, also for the moved gear) |

Limits: the drum kit is one size for the whole kit (the arm length ratio); a character with very short arms and long legs keeps its feet off the pedals and turns the sticks to reach the drums. The drummer rig has no face bones, so a drummer keeps a still face (no blink). The microphone stand and the drum kit do not change for a character in its own GH3 role. A character with its own skeleton and no `face` slots has no face motion.

Evidence: `~/.cache/any-role/` (`cap/`: runs and logs of `main` and this build; `bench/`: hair bench outputs; `pi/`: PackageImport caches; `video/`). Small images: `/tmp/opencode/any-role/`.

## Lip sync

Since 2026-10-11 the singer's face on charts without GH3 facial clips follows the words and the voice ([CUSTOM-CONTENT.md](CUSTOM-CONTENT.md#lip-sync-from-vocals)). Before, the mouth closed fully between all syllables and took one shape from the first vowel letter of each syllable; only the mouth moved. Now the lyrics give phonemes (CMU Pronouncing Dictionary), the phonemes give 15 visemes with coarticulation, the vocal stem gives the opening, and the brows, eyes, and head move. Songs with GH3 facial clips do not change. Checks on 2026-10-11, gh3.dive, game copy `~/games/clonehero-gh3-lipsync`:

| Check | Result |
| --- | --- |
| GH3 facial clips | My Curse (5 singer facial cues): the close-ups of `main` and this change at 20 song times show the same mouth poses; the log has `no sung notes` and no stem decode |
| Mouth in legato lines | Note pairs inside a phrase (gap at most 0.35 s, no m, b, or p between them, mouth open on both): the jaw falls below 35 % of the open level between them in 41–47 % of the pairs before and 13–19 % after (four Rock Band charts: "Yr Not Going Anywhere" 41→19 %, "Fake It" 47→16 %, "The Future Soon" 46→19 %, "Skullcrusher Mountain" 40→13 %; same chart without the stem 41→26 %) |
| Bilabials | All 241 note pairs with m, b, or p between them close the jaw (below 2°) after |
| Closed frames | Frames with the jaw below 2° during phrases: before 3.0–7.0 %, after 5.1–10.1 %. Most of the closed frames after are m, b, and p, and quiet word ends (stem loudness below 0.3); away from m, b, and p: before 2.6–5.8 %, after 2.5–4.7 % |
| Phonemes | 1,231 of 1,237 words of the four charts are in the dictionary (6 use spelling rules). Hand check of 20 syllables of "Yr Not Going Anywhere" (20.2–31.6 s): the first-letter rule gave the correct mouth shape for 8, the dictionary for 20 (for example "I've", "takes", "To", "you", "through", "phase") |
| Song start | 3 warm runs each, "Yr Not Going Anywhere": stage load on the main thread 862 ms before, 838 ms after (median); gameplay scene to song clock 2.17 s before, 2.11 s after. The first decode of the Opus stem (221.5 s) takes 665 ms on a worker thread; from the cache 300–350 ms after the chart is read |
| Characters | Sekai Miku, Tda Miku, `vrm1-twist`, and `seed-san` (made again with the add-on of this change: `browUp` and `browDown` found for all four): mouth, brows, eyes, and blink move in the close-ups |

The numbers come from a probe that runs the old and the new `StageLipSync` on the same charts and stems (`~/.cache/lipsync/probe`). Evidence: `~/.cache/lipsync/rec/` (close-up recordings before and after, with logs), `~/.cache/lipsync/load-before`, `load-after` (load times), `~/.cache/lipsync/video/lipsync-before-after.mp4`.

Limits: the stem gives the loudness only; the phoneme times inside a note come from rules, so a fast consonant cluster can be early or late by some 50 ms. Lyrics in other languages than English use the spelling rules. The GH3 rig poses are means of GH3 frames, so the extreme shapes (a wide "ee", a tight "oo") are softer than an animator's key poses. The brows and the eyes do not react to the meaning of the words. Windows was not tested in this change (the BASS decode uses the same `bass.dll` and its Opus plugin that Clone Hero loads; the path is passed as UTF-16 on Windows).

## Neutral faces

On 2026-10-11 the owner asked why Project SEKAI Miku smiles at rest and Tda Miku looks distressed. Measured causes and fixes (game copy `~/games/clonehero-gh3-neutral-face`, gh3.dive, a head-locked camera 3 m in front of the GH3 head frame with a 9° field of view, My Curse with GH3 facial clips and "Yr Not Going Anywhere" with lyrics and a vocal stem):

| Stage | Result |
| --- | --- |
| Source and export | The face geometry of the export is the source geometry (eye distance 0.07866 m in both for Tda Miku). 98.2 % (Tda) and 99.75 % (SEKAI) of the face skin weight is on the head and its child joints, so the face of the posed export at rest is the bind pose |
| Weights at rest | `[trace] face`: all face slots 0.000 and eye turns 0.00° at rest in both songs, as singer and as guitarist. The model data was not the cause |
| Outline (cause 1, all songs, all toon characters) | The width was in metres per metre of distance at every field of view. GH3 face close-ups use a long lens (5–15°): at 9° the line was 8.6 pixels wide and lay over the eyelids and the mouth corners (Tda: dark notches at the mouth, heavy lids; SEKAI: the lip line read as an open red mouth). Now the width is for a 45° view and changes with the field of view, so the line keeps its screen width: chin line 9 → 1 pixel, dark pixels around the mouth 541 → 163 (Tda) and 1,418 → 254 (SEKAI) |
| GH3 facial clips (cause 2) | Blink was `(lid turn − 5°) / 35°`. GH3 holds the singer's upper lids at 10–20° for seconds as an expression (570 clips: median 9.3°, blinks peak at 26°, held closed eyes 31.5°), which gave half-closed eyes (blink 0.42 at 60 s). Now blink starts at 20° and is full at 26°: blink above 0.2 in 32 % → 11 % of the samples of the singing part; the rest are blinks and GH3's closed-eye screams |
| Brows | The idle brow motion went down as well as up; with a `browDown` shape that was a small frown at rest (0.07–0.11). It now only lifts (0.000 at rest) |
| SEKAI smile | Not a bug: the untouched game mesh smiles with all 85 blend shapes at 0. In 80 Project SEKAI 3D MV face clips (`tools/research/neutral-face/sekai_face_rest.py`), the rest frames (no vowel shape, eyes open) have all shapes at 0 except `eye_wink_L`/`R` 4.9 and `look_down` 2.4 (median of the clip medians); half of the MVs add `mouth_happy`. Her real neutral is the base mesh, so no rest expression is applied |
| GH3 band | Default band against `main` at 3 song times: mean difference 0.7–3.9 (0–255); two runs of `main` differ by 1.0–3.4 |

Evidence: `~/.cache/neutral-face/sheets/` (a sheet for each Miku: source, export, and in-game rest and singing, before and after), `ev/before`, `ev/after` (captures with `[trace] face` logs), `video/neutral-face-after.mp4`, `sekai/` (the MV face clips). The packages were made again with the add-on of `main` (`browUp` and `browDown` found for both).

Limits: the outline still has one width for each material; the per-vertex edge scale of MMD (0 on the nose and the cheeks of Tda Miku) is not used. A GH3 eyelid turn between 10° and 20° (a narrowed GH3 look) shows no change on a character's face. Windows was not run: the change is C# only (no shader change).

## Known differences from GH3

| Area | Difference |
| --- | --- |
| Design | Clone Hero's fret buttons, strings, and button animation stay. The GH3 lower-left graphic is not shown. A pixel-identical frame is not possible |
| Layout | Portrait screens are not supported. Only one five-fret guitar player uses the skin; other layouts show the stock display |
| Timing | In recordings, GH3 gems reach the frets about 35 ms later than the skin's gems at the same audio time. The hit flame starts about 2 frames later than in GH3, because Clone Hero marks a hit later |
| Hit effects | Sparks use the skin's own random generator, not GH3's shared one. The brightness of `AnimatedTexture_UI` was not traced |
| Highway | The highway rise in the intro is a fit to the recording (starts 0.82 s earlier, 0.64 of the `move_highway_2d` speed); the cause is not known |
| Sustains | No GH3 reference shows a whammy, a sustain during active Star Power, or a dead sustain |
| Rendering | No shadow maps (`s13`, `c156`–`c159` sample unshadowed white), no bloom or fill-burst passes. `c119` (wibble direction) is zero. Some lightshow snapshots are brighter or darker than GH3 |
| Cameras | No hand-held shake (about ±6 px in GH3). Close-ups differ by up to about 85 px because the band pose differs. GH3's RNG is shared by all scripts, so its shot sequence cannot be reproduced |
| Band | Body and palm motion relative to close-up cameras differs; the finger motion agrees with GH3. Velocity does not change animation strength |
| Characters | The hair and accessory physics is the mod's own solver with the GH3 Havok values (cone and plane limits with their frames, hinge, prismatic, speed limits, friction), not Havok; it collides with the head capsule only (GH3 also collides it with the other ragdoll bodies; a test with the GH3 body capsules put the God of Rock's beard on his chest as in GH3 but made the hair shake, so it is not used, [PARITY-NOTES.md](PARITY-NOTES.md#hair-dynamics)). In GH3 runs the mod's hair is a little calmer than GH3 above 3 Hz (0.6–0.7 ×) ([PARITY-NOTES.md](PARITY-NOTES.md#hair-dynamics)). Axel Steel's first outfit keeps the library guitarist's hair (the 9 joints below the head; GH3's ragdoll has 6 more accessory joints on the chest). The two singers (Bret Michaels, the female singer) still play on the male singer's rig with his clips and hair. GH3 lets you unlock the bonus characters, outfits, and styles; the mod offers all of them |
| Lip sync | GH3 has no lip sync for songs without facial clips (the face stays still). The mod's lip sync on custom charts is its own addition: visemes from the lyrics (CMU Pronouncing Dictionary), opening from the vocal stem loudness, mouth poses measured from the GH3 facial clips ([CUSTOM-CONTENT.md](CUSTOM-CONTENT.md#lip-sync-from-vocals)). The stem gives the loudness, not the phonemes, so the timing of the mouth shapes inside a note comes from rules. Off with `GH3_SKIN_LIPSYNC=off`; no picker option yet |
| Show flow | No song title text, no button ripple, the hand fire does not stop at the win, no crowd `Idle` at the win. Practice mode keeps Clone Hero's lead-in |
| Lightshow | GH3 changes occur about 0.15 s before the cue times in the recording. The additive `Flare` billboards write no depth (GH3's material flags let them write depth, but the mod's draw order then removed the hair behind a card) |

## Remaining work

### Runtime and packages

- Rock Band VENUE: tested with 16 real charts (RB1 DLC, RBN1, RBN2, and Onyx conversions of RB3 customs; [tested charts](CUSTOM-CONTENT.md#tested-charts)). All their VENUE events are known to the mapper. Post-processing, spotlights, sing-alongs, and the directed-cut pre-roll are not used; the mapping has choices by judgment ([CUSTOM-CONTENT.md](CUSTOM-CONTENT.md#rock-band-venue-track)). The RBN1 and RB1 charts were not compared with a Rock Band recording.
- GH3 singers: the mod does not select the singer from the song's `singer` field (the player selects a singer), does not use the `*_Alt` microphones of `z_wikker`, `z_budokan`, and `z_hell`, does not play the facial idle loop between song facial clips, and does not run `singer_IK_Params` ([PARITY-NOTES.md](PARITY-NOTES.md#singers)).
- Picker: mouse only (no guitar or keyboard navigation inside the window; the keyboard still moves the Clone Hero menu below it). No picker option for lip sync (`GH3_SKIN_LIPSYNC=off` only). Songs in `.sng` containers cannot select a stage (the mod reads `song.ini` and `venue.chstage` from song folders only).
- Characters: blend shapes are animated only for the face slots (blink, the five mouth shapes, nine consonant visemes, brows, squint) of a character with its own skeleton. `attachments.instrument` names only the library instruments (one for each role); the player selects other instruments in the picker. A character's own instrument plays only in its own role.
- Instruments: the Blender add-on has no instrument panel (creators use the character template and `python -m pipeline.package.instrument`; a Blender export was not tested). The GH3 `demonik` camera shots for the Obliviaxe are not used (no GH3 script turns them on). The original GH3 was not recorded with another guitar: the menu automation stopped at the first-start screens (two tries; all GH3 files restored byte-equal); the rule comes from the GH3 scripts.
- Development folders are not watched while the game runs, Zip64 packages are not tested, and the importer rejects Ogg and MP3 samples in a library (it reads WAV only; the format permits them).
- A package import that does not end leaves the song without a stage (there is no time limit and no fallback).
- One complete ten-venue run on one build with exit 0 for all venues was not made after step 6.

### Commands and scripts

- Model-light controls: `SetModelLightsFadeTime`, `SetLightGroup`, `RemoveCurLightGroup`, the current-group getter, and `UpdateFxLighting`. No gameplay content calls them, so their priority is low.
- Shadow controls (`SetShadowDirFromLight`, used at setup in Art Deco and Soundcheck) and `KillLight`.
- Song-startup scripts: 139 songs have a second QB with 138 startup bodies (singer animation loaders such as `animload_Singer_Male_AnarchyInTheUK`, `0xad0b4d3b`, which is missing from `scripts.json`).
- `Band_PlayChordC` (`0x08d688e9`) and the chord wrappers; unknown commands `0x19957809` (13 `dlc51` calls) and `0xe3a71ab3` (one `dlc74` call).
- `Z_JPPlay_SetVideoScreens` and other venue-local material actions; complete encore sequences.
- RNG seed and draw order across scripts, actors, crowd, and particles; same-time ordering across systems.
- Gameplay events beyond guitar hits: chords, battle, and mode-dependent events.
- Crowd tempo and skill selection, reactions, and stage divers; audio mixing, voice limits, and fades.

### Rendering

- Shadow maps, bloom, and fill bursts.
- Particle candidate order, allocation reuse, the 150-emitter selection path, occlusion, and spline particles.
- Tesla update order, expiry, clock mapping, and sampler selection; outline stencil interaction; movie UV and blocking behavior.
- A matched GH3 and Clone Hero comparison of whole scenes.

### Authoring

- Blender add-on: no material preview node trees for GH3 shading in the viewport (toon materials have an EEVEE preview), and no GH3 script code editor ([BLENDER-ADDON.md section 1.14](BLENDER-ADDON.md#114-not-available-yet)).
- Own skeleton: the add-on reads the VRM humanoid map of the VRM Add-on but has no VRM expression import (the face fields take shape key names). VRM 0.x and 1.0 files were tested with the glTF importer, not with the VRM Add-on.
- Moonscraper: the editor fork has preservation fixes and an event browser. A typed command inspector and an original-song round trip in the editor UI are not complete. See [CUSTOM-CONTENT.md](CUSTOM-CONTENT.md#11-moonscraper).
- A general QB writer does not exist. The native PAK tool changes one camera note only.

## Release build

Records of the step 6 checks (2026-10-09). Nothing is published.

- Release zips 0.9.9 (2026-10-09, commit 62b66b4 of the character-motion branch, not tested from the zip): 1,673,743,956 bytes (Linux) and 1,673,747,309 bytes (Windows), 33.6 MB more than with all GH3 characters on the fixed profiles (1,640,161,639 bytes): the character library `gh3-characters-v1.chlib`. The add-on zip is 235,558 bytes.
- Release zips: `scripts/build-release.sh` builds `gh3-skin-0.9.8-linux.zip` and `gh3-skin-0.9.8-windows.zip` (1,228.78 MB each), and `chstage_tools-0.1.0.zip` (219,576 bytes) from a clean export of one commit; two builds gave the same bytes. The tests used the zips of 8b45c86. Installed size: `Mods/GH3Skin` 1,228,837,531 bytes and `Gh3Skin.dll` 1,269,248 bytes; the cache after the first start is 1.42 GB on Linux and 1.43 GB on Windows (one more test character). Verified from the zips only, in game copies without `Mods/GH3Skin` and `UserData/GH3Skin`: Linux with `install-linux.sh` and its launcher, and Windows under Wine 11.17 + DXVK 3.1.1 with `install-windows.ps1` (PowerShell 7 for Linux). Cold start: all 12 packages import in 22.0 s (Linux) and 23.8 s (Wine); the stage waits 6.1 s (Linux) and 6.6 s (Wine) for the library and gh3.dive. Warm start: cache check 97–103 ms, stage load 1.7–1.8 s. On gh3.dive with My Curse, the mean RGB difference against the `main` install at song times 2–60 s is 0.3–3.3 on Linux (two `main` runs: 0.5–1.5) and 0.8–7.1 under Wine (two `main` runs: 1.0–5.5); no magenta pixels. `[trace]` values agree in 135/138 (Linux) and 269/274 (Wine) samples, against 136/139 and 271/274 between two `main` runs. The F6 picker shows the 11 stages on both. The MelonLoader download, SHA-256 check, and extraction of both install scripts were tested on folders with only the game's `globalgamemanagers` (the test games had MelonLoader 0.7.3 already). Not tested: real Windows, a first start of a game without MelonLoader assemblies, `install-windows.bat`.
- Windows (Wine 11.17 + DXVK 3.1.1 on Linux, **not real Windows**): a fresh zip install of Clone Hero 1.1.0.6142 with `install-windows.ps1` (MelonLoader 0.7.3, the mod, the shader bundles, the HUD folders, and the 12 packages; no older install) plays from packages only. Measured: cold import of all packages 24.3–26.6 s (the stage waits 7 s for the library and gh3.dive); warm scan and cache check 95–107 ms; scene to stage loaded 1.4–3.2 s (median 2.2 s, 14 starts); textures BC1/BC3 on Direct3D 11 (364 textures of gh3.dive uploaded in 157 ms); stage audio through `Clone Hero_Data\Plugins\x86_64\bass.dll`. The F6 picker, a stage change, a custom character (`.chchar` in a Unicode group folder), `gh3_stage` in a CRLF `song.ini`, a song `venue.chstage`, a synthetic Rock Band VENUE chart, and the cache clean-up work. Frames at the same song times as Linux: mean RGB difference per frame 0.4–3.1 on dive, 0.5–5.3 on the venue chart, 1.6–8.3 on prison, and 3.1–16.3 on hell (fire, flames, and bot timing); no magenta pixels. Two Linux runs (before and after this change) differ by 0.5–2.6. `[trace]` values (score, streak, multiplier, rock, power, notes, lanes, stage events, audio) agree in 118–120 of 121–122 samples at the same song times (two runs); the others are at a note hit or a sound start between the two sample times. Not tested: real Windows hardware and drivers, the typical installation (`Documents\Clone Hero`), paths longer than 260 characters, `install-windows.bat` and the script under Windows PowerShell 5.1.

## Load times

Measured on 2026-10-10 with `scripts/measure-load.sh` ([VERIFICATION.md](VERIFICATION.md#load-times)) on a Linux game copy with the full package set: 11 stages, 2 libraries, 74 characters (72 GH3 and 2 creator characters), and 108 instruments (195 files, 1.7 GB). Song: My Curse (native chart) on gh3.dive, with a creator character (`custom.miku-sekai`) as the guitarist, Axel Steel as the bassist, and the ES-400 guitar. Values are the median of 3 runs (random stages: 1 run). "Before" is `main` 3313bed with only the timing lines added (e46d027). The PC (16 threads, 15 GB) had about 11 GB of memory in use by other work. At this memory load, the .NET runtime makes every garbage collection a full collection.

| Flow | Measure | Before | After |
| --- | --- | --- | --- |
| Warm start | Process start to the first main menu frame | 7.09 s | 6.59 s |
| | Mod work on the main thread before the first scene | 656 ms | 40 ms |
| | Package scan and cache check on the main thread | 157 ms | 2 ms (now on a background thread) |
| Cold start (empty cache) | Process start to the first main menu frame | 7.28 s | 6.61 s |
| | Package imports (all 195), total | 50.9 s | 25.8 s |
| | Process start to all packages imported | 58.3 s | 32.6 s |
| First song after a cold start | Gameplay scene to stage loaded | 9.18 s | 6.97 s |
| Song start, warm | Gameplay scene to stage loaded | 2.21 s | 1.47 s |
| | Stage load on the main thread | 1.90 s | 1.11 s |
| | Gameplay scene to the running song clock | 3.07 s | 2.32 s |
| Next song, same stage | Stage load on the main thread | 1.13 s | 0.46 s |
| | Gameplay scene to the running song clock | 1.93 s | 1.26 s |
| Next songs, random stages | Stage load on the main thread | 0.90 s | 0.45 s |
| F6 picker | Lists, first preview image | 73 ms, 92 ms | 56 ms, 77 ms |
| Memory | Peak resident, warm start to the menu | 963 MB | 1,050 MB |
| | Peak resident, warm song start | 1,188 MB | 1,216 MB |
| | Resident at song time 0 of the 5th song on the same stage | 1,335 MB | 1,229 MB |
| | Resident at the 9th song on random stages; peak | 1,515 MB; 1,620 MB | 1,214 MB; 1,394 MB |
| | Peak resident, cold start with all imports | 1,403 MB | 1,454 MB |

Windows under Wine 11.17 and DXVK (3 warm runs; cold: 1 run before, 2 runs after): mod work at start 675 ms to 44 ms, scan 246 ms to 3 ms, gameplay scene to stage loaded 2.26 s to 1.57 s, stage load 1.95 s to 1.08 s, gameplay scene to the running song clock 3.13 s to 2.45 s; cold start: all imports 61.4 s to 31.1 s, first song stage 11.0 s to 8.2 s after the gameplay scene.

Changes:

- The package scan and the cache check run on a background thread. Two import workers import packages at the same time (at most one library or stage). The cache key of a zip is the SHA-256 of its manifest, not of the zip file ([PACKAGE-FORMAT.md section 14](PACKAGE-FORMAT.md#14-cache)). A faster PNG decoder, the BC error from the encoder, and a faster actor clip sampler: the cache files are byte-equal to `main` (9,787 files of 194 packages).
- Worker threads decode the skin images; the main thread uploads them a few milliseconds in each frame of the loading screen and the menu (it ends at 6.5–6.7 s, before the first menu frame in the measured starts).
- The stage load reads the model folders, the actor clips, and the script banks on worker threads while the main thread builds the scene, parses `meshes.bin` without Unity calls, uploads textures from the pinned file bytes, uses one JSON options object, and runs a compacting collection at its end (50 ms; the memory of the next songs is lower than before).
- Checked: textures (547 skin images) equal to `LoadImage`, all meshes of gh3.dive and gh3.hell with the band, crowd, characters, and instrument equal to the old reader, screenshots of the default band and of the characters with an instrument within the run-to-run differences of `main`, a changed package imports again, a touched package does not, a removed package's cache folder is deleted, and a package in a `Custom/Stages` group folder imports.

What is left:

- MelonLoader and Clone Hero: with a mod that only writes the timing lines, the first main menu frame comes at 6.38–6.73 s (median 6.63 s, 3 runs). MelonLoader takes about 1.5 s before the first code of a mod and about 1 s more for its Il2Cpp support module. The skin adds no measured time before the menu now.
- JIT: the first song compiles code for about 0.6 s during its stage load (later songs almost none). A ReadyToRun build of `Gh3Skin.dll` (crossgen2 6.0.36) made the first stage load 0.89 s in one run, but most compiled methods are generic and interop code that ReadyToRun does not compile, the image decoder ran 2 times slower, and each platform needs its own DLL: not used.
- Clone Hero starts the song clock about 0.8 s after the stage load ends (without the stage: 0.97 s after the gameplay scene).
- Cold start: the band library and the song's stage import before the stage can load; the song starts without the stage, and the stage appears when it is ready (as before). The texture steps (PNG decode, mip chain, BC1/BC3) were 77% of the summed import time of the packages (29 of 38 s); the packages now store the textures pre-encoded ([Pre-encoded textures](#pre-encoded-textures)).
- The stage objects are not kept between songs (the next stage load is 0.46 s).
- The menus were not driven by keys: in the nested Xwayland display, Clone Hero took only the first key. Song starts use `--song`, and song changes use `GH3_SKIN_TIMING_RELOADS`.

Evidence: `~/.cache/load-speed/` (`before/`, `final/`, `win/`, `shots2/`, `full-base/` and `full-final/` for the byte comparison); small images in `/tmp/opencode/load-speed/`.

### Pre-encoded textures

Since 2026-10-10 the packages store the model textures in the GPU format (GH3TEX02: BC1, BC3, or RGBA32 with the full mip chain, [PACKAGE-FORMAT.md section 7](PACKAGE-FORMAT.md#7-textures)). The converter and the Blender add-on encode them at export with one shared encoder (`pipeline/package/texture.py`); the importer copies them into the cache. GLB files use DEFLATE in the zip.

Measured with `scripts/measure-load.sh` (3 runs each, median) on `~/games/clonehero-gh3-bc-textures` with 195 packages (the 193 GH3 packages and the two Miku test characters), the guitarist `custom.miku-sekai`, the bassist `gh3.axel.outfit1.style1`, the guitar `gh3.es400-blk`, My Curse on gh3.dive. "Before" is `main` f91785d with the PNG packages. The PC had a load average of 7–13 from other jobs.

| Flow | Measure | Before | After |
| --- | --- | --- | --- |
| Cold start (empty cache) | Package imports (all 195), wall time | 27.1 s | 9.8 s |
| | Process start to all packages imported | 34.2 s | 16.6 s |
| | Summed import time of the packages; texture work in it | 39.6 s; 30.8 s | 12.0 s; 0.3 s |
| | Process start to the first main menu frame | 7.12 s | 6.58 s |
| | Peak resident memory | 1,464 MB | 1,468 MB |
| First song after a cold start | Gameplay scene to stage loaded | 6.20 s | 5.14 s |
| | Process start to stage loaded | 13.90 s | 11.65 s |
| Warm start | Process start to the first main menu frame | 6.66 s | 6.56 s |
| Song start, warm | Gameplay scene to stage loaded | 1.51 s | 1.62 s |
| | Stage load on the main thread | 1,130 ms | 1,174 ms |
| | Gameplay scene to the running song clock | 2.37 s | 2.50 s |
| Sizes | The 193 GH3 packages | 1,779.9 MB | 1,138.4 MB |
| | The two Miku packages | 11.95 MB | 4.05 MB |
| | Release zip (`scripts/build-release.sh`, Linux; Windows +4 kB) | 1,799.1 MB | 1,157.6 MB |
| | Add-on zip | 321,014 bytes | 331,914 bytes |

The warm song start uses the same cache texture bytes as before (only the header changed); its differences are in the range of the single runs (stage loaded 7.97–8.11 s before, 8.06–8.31 s after, from the process start). The first song after a cold start still waits for the band library (2.0 s) and the stage package import; the rest of the import time is the SHA-256 check (2.1 GB), the inflate, and the mesh and clip conversion.

Checked: the cache of all 195 packages has the same texture data as the cache of `main` (4,404 files; the other files byte-equal except the source hashes of `pack.json`); `pipeline.package.cachecheck` gives `equal` for the venues and the band library; screenshots of gh3.dive at song times 2, 10, 20, 30, and 45 s differ between `main` and this change only where two runs also differ (the highway scroll, the animation of the band, the lights): on the pixels that are stable in two `main` runs and in two runs of this change, the mean difference is 0.1–1.1 RGB levels. The export through the Blender add-on takes 0.9–1.8 s more for the two Miku characters (section 19.6 of PACKAGE-FORMAT.md).

Evidence: `~/.cache/bc-textures/load/` (`before/`, `after/`), `~/.cache/bc-textures/shots/` (`before-1`, `before-2`, `after-1`, `after-2`), `/tmp/opencode/bc-textures/` (`cachecheck.json`, `equal-full2.json`, `validate-all.txt`, `stable-diff-2.png`).

## Stability

- Root cause of the random crashes on Linux (found 2026-10-09): MelonLoader 0.7.3 hooks six Il2Cpp functions with Dobby (LavaGang/Dobby `0651926`; the bug is also in its `master`). For each hook, Dobby maps one page with `MAP_FIXED` at (hook target − 2 GB), or (target − 1 GB) for relocated code, and does not check what is mapped there (`search_near_blank_page` in `NearMemoryArena.cc`). In every measured run without the guard, that address was memory in use: CoreCLR's executable reservation (loader heaps, JIT code), ReadyToRun code of framework assemblies (`System.Private.CoreLib.dll`, `System.Text.Json.dll`, `System.Net.Sockets.dll`), or other reservations. Dobby erases that memory, or the owner later writes over the jump address of the hook. The result depends on the address layout of each start, so the crash point changes from run to run.
- Evidence: in the crash of 06:50 (main @ 45f8e67), the main thread jumped through the hook of `il2cpp::vm::Class::FromName` (GameAssembly.so+0x2847140, reached through `il2cpp_class_from_name`), and the jump-address slot held CoreCLR data. In run `d-10` (no mod) the CLR wrote over the slots of `Class::FromName` and `Class::FromIl2CppType` 3.6 s after start, and the game crashed at the address that the CLR wrote. The other crashes stopped in `libclrjit.so` (abort) or `libcoreclr.so` after Dobby erased live CLR memory. In one run (`a-10`) Dobby erased 4 KB of `System.Private.CoreLib.dll` code, and the stage did not load (`Duplicate light housing object 00000000`). Causes that were suspected before (CLR reflection, finalizer threads, `libbass.so`, MelonLoader network threads) agree with this damage, but their link is not proved. Tools: `tools/hook-slots.py` (core dumps) and `tools/hook-page-sampler.py` (live runs).
- Fix: `HookPageGuard` (Linux only). In `OnEarlyInitializeMelon`, before MelonLoader installs the hooks, the mod sends the `mmap` calls of `MelonLoader.Bootstrap.so` through a small stub. Only Dobby's request (one page, read and execute, `MAP_FIXED`, anonymous) goes to the guard. The guard puts the page on the relocation table (`.rela.dyn`) of `GameAssembly.so`, which the dynamic loader does not read again after load and which is always in jump range. If that is not possible, it uses a free page in range, else the original request. The log shows `Linux hook page guard: on (...)` and `... hook page(s) moved to safe pages`. `GH3_SKIN_HOOK_GUARD=off` turns the guard off. A Linux kernel older than 4.17 turns it off.
- Measurement (2026-10-09, one start at a time: preview song, about 8 s of play, then SIGTERM; a failure is a crash, a hang, or a failed stage load). The exact 95% intervals are Clopper-Pearson.

  | Build | Failures | Rate (95% interval) | Runs with a hook page on memory in use |
  | --- | --- | --- | --- |
  | (a) main @ 45f8e67 | 3/40 | 8% (2–20%) | 40/40 |
  | (b) main, `GH3_SKIN_PACKAGES=off` (removed in step 6) | 1/20 | 5% (0.1–25%) | 20/20 |
  | (c) ae25880 (before the bindings and packages) | 2/20 | 10% (1–32%) | 20/20 |
  | (d) no mod | 1/20 | 5% (0.1–25%) | 20/20 |
  | (f) main with the guard | 0/70 | 0% (0–5%) | 0/70 |

  The differences between (a), (b), (c), and (d) are not significant (Fisher exact test, p = 1.0 for each pair). The crashes are not caused by the bindings, the package importer, or the mod. Without the guard: 7/100 failures; with the guard: 0/70 (Fisher exact, two-sided p = 0.042). This difference is significant at the 5% level, but only just; the stronger evidence is the hook page placement (100/100 runs on memory in use without the guard, 0/70 with it). Without the guard, 6 of 7 failures came from the 34 runs where a hook page erased live memory (not only reserved memory), against 1 failure in the other 66 runs (p = 0.006). A screenshot at song time 20 s is the same with and without the guard (only animated flames and lights differ).
- Windows under Wine: 31 starts (16 short starts over all ten venues, cold and warm cache, plus the test runs above) gave no crash and no hang (0/16, 95% interval 0–21%; 0/31, 0–11%). The guard is off on Windows (`Linux hook page guard: off (Linux only; …)`); no sign of the Linux Dobby problem was seen, but the Windows hook pages were not examined, and Wine is not Windows. One fault was found at the song end: `StageParticles.Dispose` set `depthTextureMode` on the camera of the unloaded Gameplay scene (`SEHException` in `Camera.set_depthTextureMode`); it now checks the camera first. On Linux, three runs of the full hell song on 1191b4b aborted at the song end in `il2cpp_raise_exception` from UnityPlayer (the same place is likely, not proved); one run on cf3af8e and one with the check did not.
- Open risks: hooks that another mod installs before this mod loads are not guarded. macOS was not examined (Windows `VirtualAlloc` cannot replace memory in use). The correct fix belongs in Dobby (check the gap before `MAP_FIXED`, or use `MAP_FIXED_NOREPLACE`); the owner decided not to report it upstream (PACKAGING-PLAN decision 11). The CoreCLR and JIT frames were not symbolized (Microsoft publishes no symbols for this 6.0.25 build).
- Each start on Linux makes a second `clonehero` process (a child of the game with the same command line, probably from MelonLoader's failed `Unable to create a snapshot` step). It does not stop when the game exits. The capture scripts stop it. Kill it after manual test runs, because it keeps any inherited file locks.
- Earlier temporary Xwayland displays reset between clients; the capture scripts use `-noreset`.
- Snapshot errors in the loader log occur in passing and failing runs.
