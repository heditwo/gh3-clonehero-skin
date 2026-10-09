# GH3 parity handoff

## Update — 2026-10-07 (later session)

This section supersedes the priorities in sections 1, 5, and 6. The older sections remain as history.

### Direction change: measure against real GH3 output

Original GH3 PC now runs headless under Wine on a private Xvfb display, and its frames and shader constants can be recorded. Use matched captures, not isolated command emulation, to choose the next work. See [the matched capture workflow](#matched-capture-workflow).

### Findings that change the roadmap

- **Model-light commands are not used by gameplay content.** A byte search of every original PAK and script archive found `SetModelLightsFadeTime` only in `scripts\engine\tod_manager.qb`, `MoveLight` and `SetLightGroupIntensity` only in the debug `lighttool.qb`, and `EnableLightGroup` only in `menu_store.qb`. `SetLightGroup`, `UpdateFxLighting`, `RemoveCurLightGroup`, `PushWorldLights`, `PopWorldLights`, and `KillLight` are called nowhere. Section 6 is therefore low priority.
- **Venue scripts reach very few unresolved commands.** The asset-driven script check (`GH3_PACK_ASSETS=assets`) reports only shadow-map rendering (Art Deco and Soundcheck call `SetShadowDirFromLight` at setup) and two light-node trigger scripts that call an undefined `Shadow` name. A Neversoft release build ignores an unknown name, so those trigger scripts are effectively no-ops.
- **The largest visual gap was surface shading, not commands.** The converter discarded each venue's baked lightmap atlas, the second UV set, material diffuse constants, and the unlit/ambient template types. This is now fixed; see below.

### Implemented in this session

- `gh3_materials.py` classifies all 389 templates in `MaterialLibrary.bin.xen` from their pixel shaders: shading mode (`lit`, `ambient`, `unlit`), diffuse-constant register, and lightmap sampler (`*ChkBox*` templates; last sampler, or second-to-last for `Env_*`).
- Static scenery uses `GH3STG02` (44-byte vertices with a lightmap UV). The GLB carries the lightmap as `occlusionTexture` with `texCoord: 1`, the shading mode as `extras.gh3Shading`, and overbright material colors as `extras.gh3ColorScale` (29 venue materials use 1.5–10×, mostly neon and glow cards).
- `src/Stage/Shaders/Stage.glsl` implements the `D_*` core: `albedo × vertex color × (SH(n) × lightmap + key × sat(N·L) × lerp(1, lightmap, c178.w) + c176)`. `StageLightingRenderer.ShadeSurfaces` binds per-object constants from `StageLightScene.Sample`, the same selection that particles use. Band meshes use the same path with the `Band` group.
- Traced GH3 constants: `c137 = (1,1,1,1)`, `c138 = (0, 40, 30, 0.1)` (normal-map fade distance). For Dive, the mod's selection reproduces the traced ambient, key color, key direction, and SH values exactly.

### Crash findings

- `rtmidi_get_port_count` dereferences null when ALSA sequencer access is denied (no logged-in seat). The isolated test game now uses `tools/rtmidi-stub.c` as its `libRtMidi.so`. This is a test-environment fix only.
- MelonLoader's bHaptics client retries a WebSocket to `127.0.0.1:15881` on a timer; one socket disposal raised an access violation inside CoreCLR. The mod now calls `bHapticsManager.Disconnect()` in `OnEarlyInitializeMelon` (opt out with `GH3_SKIN_KEEP_BHAPTICS=1`). Earlier crash logs also show access violations on other socket and `SslStream` threads. The root cause is still unknown.

### Remaining visible gaps found by matched captures (Dive, Slow Ride)

1. `*_Pass1` decal layers (second diffuse UV set, mask, grime, logos): about 3% of venue geometry. Needs two more UV sets in the export. Partly fixed: see [venue material parity](#venue-material-parity-fix-venues).
2. The mural behind the Dive drummer is fixed (fix-dive). It is the `D_3ChkBox_Pass0_Pass1` layer of the backdrop cloth; see "Dive neon dancer and backdrop cloth".
3. `SkinShader`/`HairShader` specifics: normal maps, skin color ramp, rim tint, specular, and the projected shadow map (`s13`).
4. Normal and specular maps for `D_N_S_*` venue templates.
5. Shadow maps (`SetShadowDirFromLight`, `c178.w` already carries the key-light shadow amount).

### Sustain tails (fix-sustain)

Before this fix, sustains used the Clone Hero sustain sprite with the lane tint. The mod also set the alpha of the tail and its glow to 0.55 or less. Thus held tails were approximately 9 px wide (GH3: 21–25 px) with no white core, and unheld tails were almost not visible.

- GH3 draws the bar with the `sys_whammy2d_<lane|starpower|dead>` materials in `global.pak.xen`. The template is `WhammyBar_UI` (`texture × vertex colour`, alpha blend). The materials use 32 × 32 textures `0xb47133c1` (green), `0x6c97b6c6` (red), `0xec463e7a` (yellow), `0x037e6d2c` (blue), `0xca034a7a` (orange), `0xe78e1a35` (Star Power), and `0x2a589f46` (dead). Each texture has a white core and coloured sides. Rows 12–23 are the cap. Row 28 is the body.
- `prepare-assets.py` writes `sustain-<variant>.png` from these textures. The layout is the same as the layout of `spr_sustain_strip6_*` (73 × 70, bar in columns 22–51, sliced border at row 38). Thus the native sliced renderer keeps the length, cap, and width.
- `sustain-held-<variant>.png` adds the glow that GH3 shows around a held bar. The glow model is `bar + blur(1.8 × bar² + 0.55 × white bar mask)`, with a Gaussian sigma of 1.2 bar widths. The squared colour keeps the glow saturated. This model is a least-squares fit to held green, blue, and orange bars in `gh3-mycurse-raw.mp4` (164.8–167.9 s and 198.0–200.4 s, rows 600–690). It is calibrated by measurement, not taken from GH3 data. The GH3 glow source is not identified.
- `GameplaySpriteRenderer.UpdateSustain` keeps the last native frame for each renderer (frame 0 is held, frames 1–4 are not held, frame 5 is dropped). It selects the variant from `SustainState` (dead, then Star Power, then lane), sets the colour to white, and makes the renderer wider for the held sprite. The native `Sustain Glow` renderer is disabled. Its shader is procedural and ignores the sprite.

Measurements at 1600 × 900 (body = mean of the saturated bar run; halo = 0–0.25 bar widths outside it):

| Frame | GH3 | Mod 0.9.7 | Fix |
| --- | --- | --- | --- |
| Held green, 165.5 s | w 22, core (229,251,232), body (126,246,129), halo (106,178,108)/(69,125,71) | w 9, core (33,238,33), body (26,227,26) | w 21, core (229,252,228), body (126,246,125), halo (77,149,72)/(69,150,68) |
| Held blue, 165.5 s | w 24, core (220,249,255), body (105,194,246) | w 10, core (31,190,238), body (24,179,227) | w 20, core (223,250,253), body (117,222,248) |
| Held orange, 199.0 s | w 25, core (252,250,214), body (246,194,103) | w 12, core (247,203,85), body (232,188,73) | w 22, core (255,249,229), body (249,219,120) |
| Unheld green, 163.6 s, y 470 | w 10, core (148,251,146), body (77,241,79) | not detected | w 9, core (151,249,150), body (82,241,83) |

The first comparison image is `~/.cache/gh3ref/agent-sustain/sustain-compare.png`.

#### Whammy bend

GH3 bends the bar width, not the bar position:

- `control_whammy_pitchshift` (`guitar_input.qb`) calculates `whammy_scale = (len × 0.5 + 0.5) × 2 = len + 1` and calls `SetNewWhammyValue`. The bot uses `len = 0`, so the GH3 reference video has no whammy.
- `SetNewWhammyValue` (GH3.exe `0x41de60`) moves the player's wibble array (`whammywibble0`, 136 entries, start value 1) by `whammy_wibble_speed` (2) entries in each 60 Hz frame. It writes the new value into entry 0.
- The `WhammyBar` mesh (GH3.exe `0x601d30`, 128 rows) gives each row an entry index. Entry 0 is at the strike line and entry 128 is at the far end of the highway. The vertex shader of `WhammyBar_UI` multiplies the horizontal offset of each row by its entry. A full whammy thus makes the bar two times wider at that row. The pattern moves up the highway at 120 entries per second.

The mod reads Clone Hero's own whammy history, the `_GlobalWhammyTexture` (1024 columns, one row for each channel; green is channel 1, as `_uSustainProperties.x` of the native glow shows). `WhammyHistory` changes the history into the 128 GH3 entries. `SustainBend` then draws a held sustain as a 128 × 8 strip mesh with the native sustain material and property block. Thus the length, track fade, and sorting stay native. The strip is shown only when the history is not flat. Otherwise the native sliced sprite stays. Extra columns are necessary because a mesh with only edge vertices bends the white core into a zigzag.

`GH3_SKIN_FORCE_WHAMMY=<Hz>` sends a cosine whammy (0 to full) to Clone Hero's whammy input. Use it only for verification. `GH3_SKIN_FORCE_STAR_POWER_ACTIVE=1` sets the Star Power active flag for the skin, for verification only.

#### Second measurements

Mean of rows 600–690 in many frames (GH3 / fix), from `mc-after2.mp4`:

| Bar | Width | Core | Body | Halo (0.5–0.75 widths outside) |
| --- | --- | --- | --- | --- |
| Green, 164.8–167.9 s | 23.9 / 23.5 | (226,251,227) / (227,253,226) | (126,245,126) / (117,247,117) | (94,152,89) / (66,152,63) |
| Blue, 164.8–167.9 s | 24.9 / 22.8 | (215,250,254) / (230,252,255) | (103,195,247) / (113,206,248) | (92,114,162) / (74,99,163) |
| Orange, 198.0–200.4 s | 25.3 / 22.9 | (254,247,212) / (254,249,224) | (246,196,104) / (249,206,114) | (156,98,63) / (164,96,61) |

The orange body green channel is now 206 (GH3 196, before 221). The comparison image is `~/.cache/gh3ref/agent-sustain/sustain-compare-2.png`.

Open items:

- No GH3 reference shows a whammy, because the GH3 bot does not use the whammy bar. The bend follows the GH3 code, but no matched frame verifies it.
- No GH3 reference shows a sustain during active Star Power. The Star Power bar uses GH3's `sys_Whammy2D_StarPower` texture and was verified in the mod only.
- GH3 changes a held Star Power phrase sustain to the Star Power texture while the player uses the whammy (qb script `0x8fc2ccad`, which calls `MakeStarWhammy` and `MakeNormalWhammy`). Each 60 Hz frame, a whammy change of more than 0.03 sets a 5-frame counter. After 5 frames with the counter on, the bar turns blue. After 5 frames with the counter off, the bar goes back to the lane colour. `StarWhammy` (`src/SustainVariant.cs`) does the same. The mod finds held phrase notes in `GuitarNoteRenderer.HeldSustainNotes`: the note flags have `StarPower` (128), and `field_Public_UInt16_0` is the lane mask (bit = lane + 1, the same as the whammy channel).
- Dead (dropped) sustains are not verified in a capture.

### Drummer arms (fix-drumik)

Before this fix, the drummer arms moved too fast, went up behind the head, and the elbows jumped. There were three causes:

1. Each new drum note replaced the arm clip immediately at its first frame. There was no blend from the previous clip.
2. After a hit, the arm went back to the arm pose of the idle clip. The idle clip holds the sticks high, so the arm moved up after most hits.
3. The arm IK put the elbow on a pole that it calculated again each frame. For targets near the shoulder, the pole changed side and the elbow jumped by up to 14 m/s.

GH3 data and code that the fix uses:

- `Drum_iterator` (`guitar_hand_events.qb`, GH3.exe `0x42d730`) starts the hit clips `drum_anim_lead_time` (150 ms) before the note. `gem_scroller` adds this value to `time_offset`. Notes 37–45 play on the left arm and notes 49–57 on the right arm. Note 48 plays the kick foot. Note 70 plays the count-off stick taps (`sticktap_l`, `sticktap_r`), or `Test_Drum_HitHat_R` for a hi-hat count-off.
- Each arm, the foot, and each cymbal is a `DegenerateBlend` slot in `drummer_static_tree` (`guitar_anim_tree.qb`). A new clip blends in over the old output and plays one time. GH3.exe gives `drum_blend_time` (0.1 s) only to the left arm; the right arm and the stick taps use the `hero_play_anim` default of 0.2 s.
- `play_drummer_left_arm_anim` and `play_drummer_right_arm_anim` wait until the clip ends, then wait `drummer_arm_blend_out_delay` (0.25 s), then blend to `stickdown_l` or `stickdown_r` over 0.6 s.
- `drummer_autotwist` (`guitar_character.qb`) moves the twist strength toward the mean of `drummer_left_arm_twist_factors` and `drummer_right_arm_twist_factors` for the last note of each arm, by `drummer_twist_rate` (0.05) each frame. The twist clip `Test_Drum_BodyTwist_D` is a difference clip (`applydifference`) with this strength. The count-off and the arm return to sticks-down set the arm factor to 0.
- `drummer_IK_params` uses a hinge on the forearm axis (0, 0, −1) with cosine limits ±0.97. The fix turns the elbow only about this hinge axis to get the shoulder-to-wrist distance, then turns the upper arm to the target. The elbow does not jump to the other side.

`StageDrummer` keeps these slots. `prepare-direction.py` writes the new constants and the `bodytwist` layer. In My Curse (45–60 s), the 99th percentile hand speed went from 20 m/s to 4 m/s, and the 99th percentile elbow speed from 17 m/s to 3.6 m/s.

Follow-up (fix-drumik2):

- **Blend curve.** Corrected in fix-fingers: `0x659d40` and `0x659f50` are the `Update` and `Evaluate` slots of `CDegeneratePartialAnimNode` (RTTI vtable `0x8bd66c`), not of `CDegenerateBlendAnimNode` (vtable `0x8bd638`: `0x659730` update, `0x659860` evaluate). The band trees use `DegenerateBlend`. Its evaluate starts at the oldest branch and lerps each newer branch in with factor `1 − curve(weight of the older branch)`, and the curve is `linear`. Thus a new branch fades in linearly over its `BlendDuration`, and older branches still decay at the rate of the newest blend. `StageBlendTrack.Factors` now does this for the drummer slots, the body clip blend, and the guitar arm tracks.
- **IK and constraint bones.** The drummer now uses `StageGuitarRig` with the `Bone_IK_Hand_Slave` targets (`drummer_IK_params` is the same as `Hero_Ik_params` except for the hand targets) and the 14 `hero_ConstraintBones` entries (`0x658c40`: Split, SplitR, Twist, Twistchild). `StageDrumHands` is removed. Before this, the drummer twist and split bones (wrists, biceps, elbows, knees, thighs) stayed at the bind pose.
- **Ragdoll.** The `ragdoll` node of the drummer tree simulates only the accessory bones `Guitarist_accessory_bones` (`Bone_ACC_01`–`15`, the hair). The body rigid bodies follow the animation. `StageHair` already ports this from the same ragdoll (`d6b5340d`). No other change is necessary.
- **Body clip speed.** `hero_play_tempo_anim_cfunc` (`0x420d50`) only selects the slow, med, or fast clip (`0x420af0`: limits at the mean of two tempo constants). `hero_play_anim` plays it at speed 1. The mod played the body clip at `bpm / 145` (1.2 in My Curse), so the idle head and body motion was 20% too fast for all members. Now the speed is 1.
- **Difference clips.** `applydifference` (`0x4a3550`) multiplies the bone rotation by the difference rotation in the bone's own frame. `PerformanceClip.Apply(additive: true)` now uses `current * difference`. Only the drummer body twist uses this path.
- **Head at 173 s.** The head pitch and yaw come from the fast idle clips (`GH3_Drummer_Fst_Idle_01`: head pitch −22° to +20°) plus the body twist (up to about 38° of head yaw at strength 0.5). GH3 selects the idle clip and its phase at random, so the head pose of one frame cannot be matched. In three drummer shots, the fix now has the same range of head motion as GH3. Head tracking (look-at) is not in the drummer scripts.

Not verified: the per-bone weights of a difference clip (`+0x1040` in the anim buffer) are taken as 1.

### Dive neon dancer and backdrop cloth (fix-dive)

**Neon dancer.** The red neon dancer right of the stage is node `0xed7f462c` (material `0x80c0de9e`, template `AnimatedTexture`, two frames at 2 fps). The node has no `CreatedAtStart`. It has `CreatedFromVariable = 0xbcdc78f5`. GH3.exe reads an unnamed table (`0x1a22a5e6` in the global scripts) after each `LightShow_SetParams`. The table sets node flags for each `performance` value, and for each `mood` value if the performance is not `poor`. Flag `0xbcdc78f5` is on only for the `good` performance. Then the engine creates or kills each node whose `CreatedFromVariable` or `CreatedOnProgress` flag changed (`0x437c20`, `0x437d50`, `0x49a240`). Before this fix, the mod did not use `CreatedFromVariable` and did not apply the table. Thus the dancer never showed. Also:

- `prepare-stage.py` read the atlas constants only for `AnimatedTexture_Lit` (`f76185ad`). `AnimatedTexture` (`9893d156`) uses the same vertex constants c0–c3 (columns, rows, fps, frame offset). Both templates are now read. This adds atlas animation to 34 materials in 8 venues (all except Credits and Video). Before, those materials showed the full atlas.
- An animated atlas changes its alpha mask in each frame. Thus `stage_pack.py` does not cut the mesh to the alpha of frame 0. The shader alpha test (material `cutoff`) is used instead. (fix-venues made this general: see [venue material parity](#venue-material-parity-fix-venues).)
- `StageLightShowState.ApplyNodeFlags` applies the table. `StageScriptState.ChangeNodeObjects` creates a node only when all of its known flags are on.

**Backdrop cloth.** The cloth behind the drummer uses `D_3ChkBox_Pass0_Pass1` (`92af2fe3`). The pixel shader is `base(UV0) × light`, then `lerp(base, pass1(UV1) × c1 × light, pass1.a × mask(UV2).r × vertex.a)`. The Dive `pass1` texture `0xa77e41b6` is the painted monster banner. The base texture `0x164bace9` is grey brick. The mod drew only the base. `export-gh3-stages.py` (`pass1_layers`) now adds a second, alpha-blended copy of each such mesh that uses UV set 1, the `pass1` texture, and `c1` as its color. If the mask is not all white, the mask is multiplied into the `pass1` alpha (only when UV set 2 equals UV set 1). The overlay keeps the base lightmap. Overlays are added in Art Deco, Dive, Hell, Soundcheck, and Video. Party is not changed: its layer uses a different UV set for the mask. **Replaced by fix-venues:** the stage shader now mixes the layer in the same pass with its own mask UV (also in Party). The overlay meshes are removed.

Evidence: `~/.cache/gh3ref/agent-dive/dive-compare.png` (GH3, mod 0.9.8, fix at 174 s and 116.5 s), `~/.cache/gh3ref/agent-dive/hell-before-after.jpg`.

Not verified: Hell uses flags `0x637530e6` (on for `medium` and `good`) and `0x9cfa6026` (on only for `poor`) on 26 level objects. The flaming sign over the Hell stage now changes with performance. No GH3 footage of Hell is available to compare.

### Guitarist and bassist arms (fix-hands)

Before this fix, the arm and hand motion of the guitarist and bassist was rough. The hands popped on each note and the fingers jumped between chords. The cause was in `PerformancePlayer.Hands` and `StageBand.SolveArms`:

- Each strum, fret, and chord clip replaced the last clip in one frame. There was no blend.
- The strum started at the note time. GH3 starts the hand events 250 ms (`strum_anim_lead_time`) before the note.
- The strum set was wrong. Axel used the shared `Normal_Strums` and the bassist used `Bass_Pick_Strums`. The profiles in `guitar_band_profile.qb` (`Musician_Profiles`) give Axel `NormalAxel`, `fret_anims_lrg`, and `guitarist_finger_anims_Axel`, and give the bassist `Bass_Slap`, `fret_anims_med`, and `guitarist_finger_anims_Bassist`.
- The arm IK was a custom solver with a fixed elbow pole. GH3 uses the Havok two-joint solver. The twist and split bones did not follow the arms, so the wrists and elbows twisted.

What GH3 does (from `guitar_anim_tree.qb`, `guitar_hand_events.qb`, `guitar_character.qb`, and GH3.exe):

- `guitarist_static_tree` is `ConstraintBones(hero_ConstraintBones) → Ik(Hero_Ik_params) → PartialSwitch → ApplyDifference(hero_arm_branch, Body)`. The arm branch adds three `DegenerateBlend` nodes: `RightArm` (strum), `LeftArm` (fret position), and `LeftHand` (fingers). `ApplyDifference` (job `0x4a3550`) multiplies each rotation difference onto the body rotation and adds the position difference.
- A `DegenerateBlend` node (`0x659730`, `0x6599b0`) blends each new branch in linearly over its `BlendDuration`. All older blends advance at the rate of the newest blend. A branch is removed when the blend that covers it is complete.
- Blend times: strum 0.1 s, then `no_strum_anim` 0.2 s when the strum ends (`hero_strum_guitar`). Fret position 0.1 s (`fret_blend_time`). Finger press 0.07 s, finger release 0.2 s, release after the note length plus 0.01 s (`FretFingers_iterator_CFunc`, `0x42d0d0`). Strum length class: short below 150 ms, medium below 600 ms, long after that.
- `CIKAnimNode` (`0x656960`) calls `hkaTwoJointsIkSolver` (`0x79b120`) for each chain of `Hero_Ik_params`: hinge axis (0, 0, −1), cosine limits ±0.97 for the arms. The solver turns the second joint about the hinge, then turns the first joint to the target. Then the end bone gets the rotation of the target bone (`Bone_IK_Hand_Guitar_L/R`). The chain strength blends over 0.25 s when arms are enabled or disabled (`hero_toggle_arms`).
- `CConstraintBonesAnimNode` (`0x658c40`) then sets the twist and split bones: `Twistchild`, `Twist`, `Split`, and `SplitR` with the amounts of `hero_ConstraintBones`.

The fix:

- `prepare-direction.py` reads the profile of each member and writes every `*_Strums` set (the profile set is `strum/default/...`), the profile fret set, and the profile finger set. It also writes the finger blend constants. `gh3_formats.py` now reads QB string arrays.
- `StageBlendTrack` (`src/Stage/StageGuitarArms.cs`) is the `DegenerateBlend` node. `PerformancePlayer` makes the strum, fret, and finger tracks from the note and animation-note cues, 250 ms before each note, and applies them as differences.
- `StageGuitarRig` is the Havok IK and the constraint bones. It replaces `StageBand.SolveArms`.

Result (My Curse, Dive, 60 Hz pose samples, 5–230 s): the largest fret-hand fingertip speed went from 16.8 to 4.8 m/s. The 99th percentile of the fret-hand fingertip acceleration went from 346 to 61 m/s². The strum-hand values went from 7.3 to 4.4 m/s and from 357 to 152 m/s². Images: `~/.cache/gh3ref/agent-hands/cmp-guitarist-5fps.jpg`, `cmp-strum-20fps.jpg`, `cmp-frethand-20fps.jpg`, and `fingertip-speed-40-50s.png`.

Open items:

- Hand clips with `_d` in the name are stored as differences (their IK target keys are far from the bind pose). The mod applies the rotation keys as differences and the position keys relative to the bind pose. This puts the palms on the guitar targets, but no GH3 frame was compared bone by bone.
- In `hero_strum_guitar`, the `strum_guitar` event plays a strum only if `always_strum` is true (it is false). A hit note calls `handle_hit_note_CFunc` (`0x41f960`), which reads `playing_missed_note` but was not decoded further. The GH3 video shows a strum on each hit note, so the mod strums on each note.
- Converted clips are sampled at 30 Hz. GH3 decodes the compressed keys directly.

### Fret-hand fingers (fix-fingers)

The user reported that all fingers of the fret hand move at the same time. Findings:

- GH3 has no clip for one finger. `guitarist_static_tree` has one `LeftHand` `DegenerateBlend` node for the fingers (`hero_finger_branch`). `FretFingers_iterator_CFunc` (GH3.exe `0x42d0d0`) sends one `pose_fingers` event for each note, 250 ms before the note. The chord name is made from `gem_colors` (at most 4 colours, for example `green_yellow`). After the note length plus `finger_note_padding`, it sends `None`. `hero_play_chord` plays the clip from the profile finger set (`guitarist_finger_anims_Axel`) with `finger_press_blend_time` (0.07 s) or `finger_release_blend_time` (0.2 s).
- The finger set has 10 clips. Each clip curls only some fingers. For Axel, `roll3_1` (green) curls the index finger, `roll3_2` (red) the index and middle fingers, `roll3_3` (yellow) also the ring finger, and `roll3_4` (blue and orange) all four fingers. The `bar1`–`bar5` clips are for chords. Many chords use the same clip in GH3's own table (for example 11 chords use `bar5`). `prepare-direction.py` copies this table without change. The 40 unavailable guitarist references are body clips, face clips, and three unused `GH3_Hnd_Guit_Strum_strum_0x_D` clips. No finger clip is missing.
- The cause of the visible problem was the blend. The previous `StageBlendTrack` used the evaluate code of `CDegeneratePartialAnimNode`. With that code a new chord was half visible in the first frame, so every finger that changed jumped at the same time. With the `DegenerateBlend` evaluate, each finger moves smoothly to its new curl over 0.07 s.

Result (My Curse, 60 Hz pose samples from `PerformancePlayer`, fingertip distance from the neck axis): `~/.cache/gh3ref/agent-fingers/finger-curl-fixed.png` (12.3–16 s and 37.3–41.6 s, with the gems of each `pose_fingers` event). On a green note only the index finger moves. On a red note the index and middle fingers move. On a blue or orange note all four fingers move. The highest fingertip speed is now 0.8–2.5 m/s. Before the fix, `finger-curl-main.png` shows steps of 2–4 cm in one frame.

Open items: the video check is weak. The forced shot `d104c522` does not give GH3's framing at 37.3–41 s (`cmp-fingers-38.9s.jpg`), so no matched close-up of the fret hand was compared frame by frame.

### Node flags in all venues (fix-nodeflags)

GH3 creates and kills nodes that have `CreatedFromVariable` or `CreatedOnProgress` when their node flag changes. The flag names are `LS_PERF_POOR/MEDIUM/GOOD`, `LS_PERF_POOR_MEDIUM`, `LS_PERF_MEDIUM_GOOD`, `LS_PERF_POOR_MEDIUM_GOOD`, `LS_MOOD_FLARE/BLACKOUT/STROBE`, `LS_ALWAYS`, `LS_3_5_PRE/POST`, `LS_ENCORE_PRE/POST`, and two follow-spot flags (`0xfb1a2bba` guitarist, `0x0fcd6094` bassist). Before this fix, many of these nodes were not exported, and some flags were never set.

- **Flag sources.** The table `0x1a22a5e6` sets the performance and mood flags (fix-dive). `Progression_SetProgressionNodeFlags` (called by `start_gem_scroller`) sets `LS_ALWAYS`, `LS_3_5_*`, and `LS_ENCORE_*` from the game mode. `LightShow_InitEventMappings` always sets the guitarist spot flag and sets the bassist spot flag only when `current_num_players` is not 1. The mod now runs or copies these at stage start. The runtime now supports `AddParams` and line breaks inside parentheses in QB expressions. `Progression_SetProgressionNodeFlags` uses both.
- **Flare cards.** The flag-driven `_gfx_NodeArray` geometry is in the second, small `.scn` of each zone pak. Clone nodes give their source sector in field `0xedc063b9`, with `Pos`, `Angles`, and `Scale`. All of these meshes are `Flare` cards (template `379bed00`, additive, texture `f4f1e9d2` or the venue lens glow). The `Flare` vertex shader draws each quad around its centre along the camera axes. The export reads them (`flag_scenery`), and the material flag `billboard` makes the stage shader turn them to the camera (same path as crowd cards). Most are `LS_MOOD_FLARE` lens bursts. In Wikker, 11 are `LS_PERF_MEDIUM_GOOD` glows over the side towers.
- **Flare housings.** Party has 8 `GO_NoHousing01_Flare01` GameObjects (type `0x47685986`) on `LS_ENCORE_PRE/POST`. They are exported as fixtures with only their flare card. Fixtures that are not created at start get `created: false`.
- **Not visual, not exported.** The `GO_NoHousing01` follow spots (type `0xd33be42c`, 2 per venue) have no visible body. The model is a placeholder, and they have `FollowTarget` = guitarist or bassist. Their light is the `_Projector_StageOnly` projected spot, which the mod does not render. Two Dive `LevelLight` nodes (`LS_ENCORE_POST`) are light objects, not geometry. The Video node `0xfa7ff751` has `Model = none`.
- `tests/StageScriptAssetTests.cs` (with `GH3_PACK_ASSETS`) checks that each flag-driven visual node is exported, and that its object exists or is killed after a flare, strobe, blackout, and performance change.

Evidence: contact sheets in `~/.cache/gh3ref/agent-dive/nf/sheets/` (poor, medium, and good runs with `GH3_SKIN_FORCE_ROCK`; columns are blackout, chorus, verse, solo, flare, and strobe cues of My Curse). `perf-verse.jpg` shows Dive and Wikker neon that changes with performance.

### Performance findings

Measured with `GH3_SKIN_PROFILE=1`, which logs per-subsystem milliseconds per frame once a second. The test case is My Curse in Kaiju Megadome: about 14,000 live pyro particles at 48–51 s.

| Cause | Fix | Effect at peak |
| --- | --- | --- |
| One shared particle list scanned by every emitter operation (quadratic) | Per-emitter `Live` lists with the same swap-remove order | Simulation 1,484 → 4–6 ms |
| Unity `Vector3`/`Vector2`/`Color` constructors, operators, and `Il2CppStructArray` indexers are native calls in this game | Particle batches compute in `System.Numerics` and upload once per frame | Quad writes 20 → 0.7 ms |
| Per-frame sample array, per-emitter lookup, and per-particle closures | Iterate emitter lists directly; hoist the batch helper | Allocation 480 → about 130 MB/s |
| Per-surface light selection and about 11 native calls per surface every frame | Recompute only when `StageLightScene.Signature()` or the surface position changes; cached property IDs | Baseline 115 → about 155 fps |

Worst-case frame rate in that section went from 1 to about 43 fps on the RTX 3060 laptop. The laptop also throttles after heavy sections, roughly doubling all subsystem times. Rule for future code: avoid Unity struct constructors and operators in per-frame loops; use field initializers (`new Vector4 { x = ... }`) and `System.Numerics`.

#### Second optimization pass (0.9.1)

All ten venues were profiled with My Curse (64 s each) on the laptop GPU. `GH3_SKIN_PROFILE=1` now also logs managed kilobytes per frame and gen-0 collections per subsystem (`src/FrameProfile.cs`); `tools/summarize-profile.py <log>...` summarizes the logs. `GH3_BENCH=<venue> GH3_BENCH_SONG=<chart folder>` runs a venue's scripts and particles against a chart outside the game (`tests/ScriptBench.cs`); `GH3_BENCH_ALLOC=1` adds sampled allocation types.

| Cause | Fix |
| --- | --- |
| Stage actors (for example, Lou's Inferno props) used the CPU vertex-lighting path: `BakeMesh` plus per-vertex lighting every frame (2.4 ms) | Actors use the GPU surface shader with their light group |
| Per-surface light constants: seven `SetVector` calls and a property block per surface | One 7×N `RGBAHalf` texture (`_Gh3LightData`) per venue, indexed by `_Gh3LightIndex`, uploaded once per change |
| Light selection allocated query, candidate, and scratch arrays per sample | `StageLightSpatial.QueryInto`, cached light records, `stackalloc` scratch |
| `ParticleBatch.End` assigned full-capacity arrays for seven vertex streams (about 26 MB per frame in the Budokan pyro) | Upload only the used range through `Mesh.SetArrayForChannelImpl` and `SetIndicesImpl`; vertex counts grow in 64-quad steps; empty batches disable their renderer |
| A delegate per particle per step (`_runtime.NextRandom` method group), `Gh3Checksum` string and byte copies, LINQ in `Gh3QbValue.Field` | Cached delegate, table CRC without copies for ASCII, index loops |
| Particle view: two camera-path samples and new frustum planes per emitter per step | Cached per time step |

The public ranged `Mesh` overloads (`SetVertices(array, start, length)`, `SetIndices(array, start, length, ...)`, `SetVertexBufferData`, and similar) are stripped from this game and fail with "Method unstripping failed". Use the internal calls above.

| Venue | Median fps before → after | Minimum fps before → after |
| --- | --- | --- |
| Backyard Bash | 178 → 230 | 119 → 150 |
| Dive | 173 → 225 | 134 → 188 |
| Video shoot | 190 → 214 | 84 → 209 |
| Art Deco | 158 → 222 | 121 → 180 |
| Prison | 154 → 213 | 88 → 142 |
| Wikker | 186 → 208 | 165 → 190 |
| Budokan | 162 → 183 | 44 → 71 |
| Lou's Inferno | 116 → 198 | 100 → 172 |

Soundcheck and Credits stay at the 241 fps cap. Gen-0 collections fell from about 1 per second to about 0.2. The Budokan pyro peak (about 12,000 particles) still spends about 2.6 ms building quads, 2 ms uploading them, and 1.3 ms in scripts. The remaining CPU vertex-lit meshes (`LitMeshes`, materials without a GPU shading mode) cost about 0.7 ms.

Visible change: the Budokan stage diver (Crowd light group) now uses native GH3 light selection (cyan ambient, red key) instead of the earlier purple approximation.

#### Visual fixes (0.9.2)

- **Band brightness.** Band materials now use the diffuse part of GH3's `SkinShader` and `HairShader`, ported from the original pixel shaders (`tools/disassemble-ps.py <template>` prints them). Skin: `albedo × c0`, light `× lerp(c1, lerp(c2, 1, step(N·L − c4)), smooth(|N·L − c4| / c3))`, rim toward `c8` by `c10` with view power `c9`. Hair: `albedo × c1`, wrap lighting `sat((N·L + c0) / (1 + c0))`, minimum `c6`, rim toward `c8` by `c9`. The constants come from each material (the `character` field in band `stage.json`). Before this, the band used the venue shader without these factors and rendered about twice as bright (Budokan drummer: `c0` 0.77, skin ramp 0.45–0.73). In 0.9.3 the skin path also has the normal map (s0, strength `c5`, faded out between 30 and 40 units like `c138`) and specular (`pow(sat(reflect(−V, N) · −L), c6) × key × mask(s2) × c7`). Hair uses its normal map with strength `c7`. Band meshes get tangents from `Mesh.RecalculateTangentsImpl`; the normal map green channel is inverted (DirectX convention). Without normal maps and specular, the skin ramp made clothes look flat. The mod's band key light matches the traced GH3 constants for Dive (`c177`, `c178`). Hair specular and the projected shadow map are still not ported.
- **Light housing orientation.** `prepare-lighting.beam_rotation` used a wrong formula. A housing `Quat` (x, y, z, implied w) maps to `(−x, −y, z, w)` in stage space, which reproduces every housing node's static `Angles` exactly. Lens and beam cone both lie on local +Z.
- **Housing rotation priority.** Light show snapshots and `Obj_Rotate` both set a housing's orientation; the latest change now wins. Before, one `Obj_Rotate absolute=(−40, 90, 0)` at song start locked the Budokan floor searchlights so they pointed down for the whole song. When control passes from the script back to the light show, `StageHousingRotation` turns the housing from its current orientation at the light show's turn rate (π rad/s, at least 0.3 s) instead of snapping.
- **Effects behind the highway.** `StageHighwayMask` writes near depth over the highway's screen area (render queue 3005), clipped at the player camera's far plane. Particle batches and Tesla effects draw at queue 3010, so they never draw over the highway.
- **Pyro amount.** Particle LOD is off in GH3: the flag at `0xb4e322` is in `.bss`, and only the `EngineConfig` handler writes it; no script calls `EngineConfig`. The particle count therefore matches GH3 for the same rock meter value (Budokan pyro peak: about 8,300 particles at rock 0.5, 14,700 at rock 1.0).

#### Full-song comparison against original GH3 (0.9.4)

Original GH3 now records on the GPU: `~/.cache/gh3ref/start-gh3-gpu.sh :9310` starts it on a nested Xwayland, and `pin-window.sh :9310 <seconds>` keeps the window at 1600×900. GH3's built-in bot is enabled by setting `bot_play` to 1 in the two `player*_status` defaults of `DATA/PAK/qb.pab.xen` (offsets 0x5026c and 0x508dc + 4); keep a copy of the original. In this install the fret keys are 1–5 and the red fret is "back". The "unlock all songs" cheat opens the bonus list. Quickplay plays in The Dive. `Gh3SongTool chart <song_song.pak.xen> <notes.chart>` writes a Clone Hero chart with GH3's notes and Star Power phrases; `~/.cache/gh3ref/full/sbs.sh` builds a side-by-side synced on the music.

Fixed from that comparison: back-face culling (GH3 material flag +220 marks two-sided materials), Axel's highway texture, venue posters from `global_ad_tex.pak`, housing lens flares, and the "Note Streak!" and "Star Power Ready" messages. Remaining visible gaps: GH3's shadow map, bloom around bright lights, and overall stage brightness in some snapshots.

#### Highway speed and crowd (0.9.7)

- **Highway speed.** `HighwayScroll` moves the board texture by `songTime × speed / 6` UV (`field_Private_Single_1`, 7 by default), which matches the notes for the stock neck height (4.617). The skin stretches the neck to 5.05, so the board ran about 9% faster than the notes. `KeepHighwayAtNoteSpeed` scales the scroll speed by the stock height over the skin height.
- **Crowd cards face the camera.** `Billboard_Fast` stores each card's pivot in the normal slot and offsets the corners along the view axes (`c120`–`c122` = `g_InvView` in the shader constant table). The stage shader does the same when `_Gh3CamRight.w` is 1; `StageCrowd.Face` sets the camera axes every frame.
- **Crowd impostor view angle.** The cards face the camera, but the picture on each card turns with the view. `AddCrowdModelCam` (GH3.exe `0x433c20`) runs every frame. It copies the `bg_viewport` camera orientation to each crowd camera, aims it at the point 1 unit above the ped, and turns it about world Y by `25° × forward.x` of the view camera. GH3 parks the peds with `quat [0,1,0]` (turned 180°). Thus a side camera shows the audience from the side. Rows with `roty` (`z_video` side crowds) call `Obj_SetOrientation` and do not get the 25° turn. `StageCrowd.Place` does the same: each atlas cell is turned with the view, and a second atlas camera shows the peds that do not get the turn.
- **Crowd lighting.** GH3's crowd script parks ped *n* at `(-500, -200 - 100n, 0)` with `LightGroup = Crowd`, next to the venue's Crowd ambient and directional lights (both at x = -500). The peds now sample lights there instead of at the audience position.
- **Crowd card brightness.** The card pixel shader is `texture × c0 × vertex colour` with `c0 = 1`. The baked vertex colours go from black at the feet to about 0.66 at the head; the cards use them at double scale (0x80 = full). This matched GH3's crowd brightness in the comparison frames.

#### Highway geometry from original GH3

- **Cause.** The player camera, the rail overlay, the Star Power rail frames, and the beat-line width came from FastGH3 captures (`gh3-fast-*.png`, `gh3-long-star-power-1600x900.mp4`). FastGH3 is not the original game, and its highway has a different perspective. In the original GH3 recording (`~/.cache/gh3ref/full/gh3-mycurse-raw.mp4`) the rails meet at y 183.6 (FastGH3: 207.7) and the center is at x 800 (FastGH3: 804).
- **Measured GH3 values (1600 × 900).** Rail slope 0.5102 px per row. Fret centers at y 796.5, 128.3 px apart. A beat line τ seconds before the frets is at y = 183.6 + 438 / (τ + 0.72). The board starts at y 381 (`highway_top_width1` 160 at y 305 in the 1280 × 720 layout, from `highway_playline1` 655 − `highway_height1` 350). The board texture is 0.987 of the rail width. It repeats every 1 / 0.987 s of gem travel and scrolls by one repeat per second (1021–1027 texture rows per second, 1024 rows per repeat). The board fades in between y 384 and y 406.
- **Fix.** `HighwayView` calculates the player camera from these values: field of view 15.33°, pitch 4.56°, height 0.921, z −4.894. The neck is 0.970 × 6.10, so it ends at GH3's highway top. The texture tiling and the scroll speed (6 at note speed 7) are set on `HighwayScroll`. The frets move onto Clone Hero's gem lanes (0.1935 apart; the stock frets are 0.2 apart). The gem renderers spawn gems up to the highway top (Clone Hero stops at z 7.87). The track material's `_FadeParams` z and w are viewport heights from the top; the skin sets them to GH3's fade rows. Gems and beat lines use the same fade. `HudViewport` scales highway overlays relative to the new field of view. `highway_overlay.warp` moves the FastGH3 rail images onto GH3's rails in `prepare-assets.py` and `prepare-star-power.py`.
- **Result.** Texture perspective term: GH3 −446,100, skin −446,115 (row·px). Beat-line depth scale: GH3 438.1, skin 438.1. Gem depth scale: GH3 428.2, skin 426.8. Scroll: GH3 1021–1027, skin 1024 rows/s. Frets within 2.5 px. `DepthScale` (442.8) is calibrated so that beat lines and gems both agree; the beat lines and gems in each game sit slightly off the board plane.
- **Not matched.** In the recordings, GH3 gems reach the frets about 35 ms later relative to the audio than the skin's gems (a video delay, not geometry). Board brightness differs by up to about 10% in some rows (GH3 is warmer in the middle, darker below the frets). Gem sprite size was not compared. The score, rock meter, and other HUD positions were also tuned on FastGH3 captures; examine them against the original recording.

#### Sustain tails at the highway top (fix-susfar)

- **Cause.** Sustain tails appeared later than their gems. Clone Hero spawns the sustain at the same far limit as the gem (z 10.7) with its full length, but `TrackFadeManager` gives the "Sustains (Fade Instance)" material its own `_FadeParams` z and w (0.472, 0.522: y 425–470 at 900 rows). The skin had set only the neck material to GH3's rows (0.427, 0.451: y 384–406). So the tail stayed transparent in the top part of the board where the gems and the board are already visible.
- **Fix.** `GameplaySpriteRenderer` records each sustain material and sets the same z and w as the board on it every frame. This also corrects the values again when `TrackFadeManager` recalculates (for example at the end of the intro rise). The whammy strip (`SustainBend`) uses the sustain material, so it gets the same fade.
- **Result.** With trimmed copies of My Curse (from 95 s and 158 s), the tails of the sustains at 101.4 s and 164.1 s start at the highway top together with their gems and fade like the board, as in `gh3-mycurse-raw.mp4`.

MelonLoader's assembly generator contacted `api.melonloader.com` on every launch, and the leftover HTTPS connection crashed the CLR later. The installed game now has `force_offline_generation = true` in `UserData/Loader.cfg`; the original file is backed up as `~/.cache/gh3-install-backup-0.8.115/Loader.cfg.bak`.


#### Camera shot selection (fix-cameras)

- **GH3 logic.** `CameraCuts_StartCallback` (`guitar_cameras.qb`) calls `CameraCut_GetArraySize` (GH3.exe `0x41d770`). This function keeps the entries of the camera array that pass all filters: `includenodes` and `excludenodes` against the guitarist's target node, `Name` (from the note), `includetype` (the note's forced type), and `excludetype` (the type of the last shot, only when the note gives no name and no type). The filters apply together. If no entry passes, GH3 uses entry 0 and makes no random draw. Otherwise `GetRandomValue` (`0x52fba0`) draws `Rnd(count)` and `CameraCut_GetCameraIndex` (`0x41dc10`) maps it to the entry. A shot without a `Type` sets the last type to None. Then the control script calls `CameraCuts_CalcTime`: a camera note longer than 200 ms gives the duration. Else the duration is `RandomRange(3, 8)`, an integer of 3 to 8 seconds, extended to the next lightshow note 58. `CameraCuts_HandCam` makes a third draw (`0..100`) for the zoom, but only for LongShot and Mid shots. `TwoCam` shots with `UseHandCam` do not zoom. A note that only disables cuts (78) does not cut.
- **Random generator.** `Rnd` (`0x477120`) is the generator in `Gh3Random`. GH3 seeds it with `0x6680bf3` at boot (`0x4016f3`). The song start does not seed it again (only replays call `SetRandomSeeds`). All scripts share this generator: crowd, band, lightshow, particle, and menu scripts draw from it every frame. The startup ID code (`0x489a30`) can also seed it from the time since boot. Thus the shot sequence of one GH3 run cannot be reproduced.
- **Change.** `StageCameraPath` now does the same filters, draw order, integer timer, and zoom draw, with its own `Gh3Random` (boot seed). The exporter writes `normal` for shots without a type; these do not set a last type. A cue within one frame after the end of a timer starts the cut at the cue, as in GH3 (before, this gave a second, zero-length cut).
- **Check against the My Curse recording.** Each GH3 shot was found with matched mod renders. All 51 identified GH3 shots are in the candidate set that the mod uses for that cut (types 51 of 51). Per-shot matches with the mod's default seed: 17 of 51, expected by chance 18.3. A chi-square test of the GH3 shots against the uniform choice gives p = 0.21 (close-up), 0.22 (mid), 0.10 (stage).

#### Camera shot geometry (fix-camgeo)

- **Fault.** Shots with `LookAt` (all close-ups, the guitarist stage shots, singer, bassist, and orbit shots) aimed in the wrong direction. The pitch from `ScreenOffset` had the wrong sign. Thus the camera looked up and the guitarist was at the bottom of the screen (about 290 px too low in `Stage_Left01`). The look-at also used the world up axis, so the shots lost their authored roll.
- **GH3 logic.** `0x4badf0` reads a morph key: `hfov` or `FOV` (horizontal, degrees), `LockTo`, `LockToBone`, `Pos`, `Quat`, `LookAt`, `LookAtBone`, `LookAtPos`, and `ScreenOffset`. `0x4bb9d0` makes the camera of one key. With `LookAt`, it aims at the bone and uses the up axis of the `Quat` matrix (`0x4ba630`). Then `0x4b8a60` turns the view in camera space: first the pitch `ScreenOffset.y × FOV/2 ÷ aspect`, then the yaw `ScreenOffset.x × FOV/2` (angles, not screen tangents). A negative offset puts the target left of center and above center. `0x4bd700` makes the camera of the start key and of the end key, then interpolates them (default motion `linear`, `0x4bc93d`). `CCam_Orbit` turns the lock frame about its Y axis before the look-at (`0x4ba2d0`). GH3 PC sets the aspect to 16:9 at boot (`0x47b3a6`). The world-locked shots show that the horizontal FOV is correct as it is used now.
- **Change.** `StageCameraPath.Resolve` uses the GH3 pitch sign, the angle units, the pitch-then-yaw order, and the up axis of the authored `Quat`. The orbit turns the lock frame before the look-at. The HandCam zoom calculates the end key with the zoom FOV.
- **Result (My Curse, shot forced to the GH3 shot, same song time; background offset GH3 − mod at 1600×900).** World-locked shots (Mid, Drummer, Stage_Center02, StageDive, VertStageRear): 1 to 9 px, scale 0.996–1.003. `Stage_Left01`: −10/−2 px, roll 0.1° (before the order fix: 2.4°). Close-ups: 18 to 65 px. `Stage_Right01`, `Singer_Right`, `Singer_CloseUp`: up to 85 px, and 320 px when the GH3 singer leans out. In these shots the target bone is at the same screen position in both games. The rest of the difference comes from the band pose and the band position, not from the camera. GH3 also adds a small hand-held shake (about ±6 px), which the mod does not do. Contact sheet: `~/.cache/gh3ref/agent-camgeo/camera-geometry-gh3-vs-mod.jpg`.

#### Score and note-streak digits

- **Fault.** In 0.9.7, each streak digit was a pre-made tile with its own size and position. The third digit used a different tile at four digits, so it sat lower than the other digits. The score used fixed-width crops of the font.
- **GH3 data.** `create_2d_hud_elements` in `scripts/guitar/guitar_hud_2d.qb` makes the score and the four streak digits as text elements. The score uses font `num_a9` at (222, 70), justification right/bottom, scale 1.1, and `font_spacing` 5. Streak digit *i* (1 to 4) uses font `num_a7` at (222 − 37*i*, 78), centered, z 25. Digit 1 has colour (15, 15, 70, 200); the other digits have (230, 230, 230, 200). `career_hud_2d_elements` puts the drum, body, and note icon in `HUD2D_note_container` at `counter_pos`. The note icon (z 26) covers the fourth digit.
- **Fix.** `prepare-fonts.py` exports `num_a9`, `num_a7`, and `text_a6` from `ZONES/global.pak.xen` as atlases with glyph tables. `HudText` draws all digits of a line with one top line and one pitch. `ComboHud` uses GH3's container layout and the original drum, body, and icon textures. When a digit changes, only the highest changed digit rolls. The lower digits change immediately, as in GH3 frames at 099→100 and 999→1000.
- **Calibrated by eye.** The streak glyphs are at one texel for each 1080p pixel. Six-digit scores are narrowed to 176 GH3 units. The roll timing is unchanged from `counter-tuning.json`.
- **Check.** At 1110 notes, the digit centres are within 0.4 px of GH3 (1600×900). Five-digit score glyph runs are within 1 px.


#### Note streaks of five or more digits

- **Problem.** GH3 shows only four streak digits. Clone Hero streaks can be longer.
- **Layout.** `ComboReelLayout` (in `src/ComboReelAnimation.cs`) opens the counter at x = 95, between the first and second drum reels. For each digit after the fourth, it inserts one 37-unit drum reel (cut from `HUD_counter_drum`) and one 37-unit body strip (`HUD_counter_body_tile.png`, made by `prepare-assets.py`). The first reel and the left end of the body move left. GH3's layout for one to four digits does not change. The highest digit stays on the first reel; the other digits keep GH3's positions.
- **Room.** The lamp column is to the left of the counter. Five digits fit at full size. For six or seven digits, the counter shrinks about its right edge so that it does not cover the lamps.
- **Curves.** `Gh3Morphs` uses the DoMorph `motion` curves in GH3.exe at 0x55b010: `ease_in` = t², `ease_out` = 2t − t², `smooth` = 3t² − 2t³. The width opens like `hud_move_note_scorebar` (overshoot with `ease_out` in 0.3 s, settle with `ease_in` in a third of that time, then a 0.1 s + 0.1 s nudge). The new digit uses the scale and rotation steps of the "Note Streak!" text in `hud_show_note_streak_combo` at twice the speed. The flash is `hud_glowburst_alert` with its `Char_Select_Hilite1` sprite (`assets/hud-glowburst.png`, from `prepare-fonts.py`), scaled down to the counter. When the streak breaks, the digits fade in 0.1 s, the counter hops out, closes, and then leaves as before.
- **Calibrated by eye.** The overshoot (12 units), the squash amounts, the pop size, the flash size, and the six- and seven-digit shrink.
- **Test.** `GH3_SKIN_FORCE_COMBO=9975 GH3_SKIN_FORCE_COMBO_BREAK=10045` shows 9999→10000 and a break after about 25 s of My Curse. `ComboReelLogicTests` checks the curves, the reel layout, the lamp clearance, the animation, and the override.

#### Credits band (fix-credits)

- **Fault.** In the Credits venue, a drummer stood inside the box on top of the tower. A bassist and a singer were also on the tower. `z_credits_NodeArray` has only one band waypoint, `z_credits_TRG_Waypoint_Guitarist_Start`. The exporter added the missing members at fixed offsets from the guitarist (drummer: 3 units behind). These positions were not from GH3.
- **GH3 data.** `create_band` (`qb.pak`, script `0x3681c07d`) has a special path when `current_level` is `z_credits`: it calls `create_guitarist`, then `destroy_band_member` for `bassist`, `vocalist`, and `drummer`. Thus GH3 shows only the guitarist in Credits. `get_starting_position` gives `NONE` for a member without a `_start` waypoint.
- **Fix.** `export-gh3-stages.py` exports only the members that have a start waypoint. It does not add members. `stage_pack.py` and `StagePack.cs` accept one to four known performers. The source report lists the exported members in `band` (it replaces `addedBandPlacements`). The other nine venues have all four start waypoints, so their packs do not change.
- **Check.** Credits loads with 1 performer. The drummer camera (`GH3_SKIN_CAMERA=drummer`) shows each drummer on his riser in the other venues.

#### Pre-song and post-song sequences (fix-showflow)

- **Problem.** Clone Hero holds the song clock at −1 s while it loads, then starts the song. The band stood in a frozen pose before the first cue. After the last note, the HUD stayed on the screen until Clone Hero showed its results screen.
- **GH3 data.** The decompiled PC scripts (`guitar_transitions.qb`, `guitar_intro.qb`, `guitar_events.qb`, `guitar_hud.qb`, `guitar_character.qb`, `global_sound_logic.qb`) give the sequence:
  - `Common_Intro_Transition` (8000 ms): camera array `cameras_intro`, `play_intro_anims`, and `GH_SFX_Intro_WarmUp` (the venue's `z_<venue>_intro` crowd sample) at 100 ms. `play_intro_anims` changes the guitarist, bassist, and singer to `intro_smstg` (small venues: party, dive, video, prison, hell) or `intro`, and the drummer to `intro`. Then each member returns to `Stance_A` at the end of that animation.
  - `intro_sequence_props`: the highway rises at −2000 ms (`move_highway_2d`), the buttons ripple at −800 ms (`Notes_Ripple_Up_SFX`), and the HUD moves in from −400 ms for 200 ms, then swings (`move_2d_elements_to_default`) and plays `Song_Intro_Kick_SFX`.
  - `win_song` waits for the end of the last note, then 1.1 s (`IsWinPort`) and `Song_Win_Delay` (2 s). Then `GuitarEvent_SongWon_Spawned` plays `You_Rock_End_SFX`, starts the `Fret_Flames` movie, and shows "You Rock!" (`split_text_into_array_elements`, font `text_a10_large`, colour 223, centre (640, 360), a black shadow at 1.3 × 1.2 scale). `jiggle_text_array_elements` jiggles the letters for 3 s, then `explode_text_array_elements` throws them off the screen in 1 s. `waitAndKillHighway` hides the highway after 0.5 s and plays `Crowd_Fast_Surge_Cheer`. `You_Rock_Waiting_Crowd_SFX` plays a cheer and applause after 2 s. `Common_SongWon_Transition` (8000 ms) removes the HUD (`play_outro`), stops note cameras, cuts to `cameras_win`, and starts `play_win_anims` (`win_smstg`/`win` for the guitarists, `win` for the singer and drummer, arms off).
- **GH3 recording (My Curse).** The intro camera, the song title (−6.5 s to −2.4 s), the band intro animations, and the highway rise (−1.6 s to −0.6 s) are visible. "You Rock!", the flames, the HUD removal, and the win camera start at song time 253.8 s, 2.95 s after the last note (250.85 s). The highway goes 0.9 s later.
- **Change.**
  - `StageShowFlow` adds these cues to the stage timeline. `StageTimeline.Start` is the earliest stage time (−8 s with the intro). The band, crowd, hair, camera, script, and audio states now start at that time, not at 0.
  - `GameplayStage.ExtendLeadIn` sets Clone Hero's song clock (`GameManager.songTime` and the start time of `GameManager.sync`) to −8 s while the song is still paused. Thus Clone Hero plays an 8 s lead-in, and the song audio still starts at 0. `GameManager.songLength` is extended to the win time + 8 s, so the results screen comes after the win transition.
  - The win time is the end of Clone Hero's last note + 3.1 s.
  - `prepare-direction.py` also reads `axel_1.pak.xen`. Axel's intro (`GH3_Guit_Axel_A_Intro1`–`4`) and win animations are in that pak, and they were missing.
  - `gh3_direction.py` exports `cameras_win`. Dive's `cameras_win` is one `OrbitAndMorphTwoCam` shot.
  - `prepare-fonts.py` exports `text_a10_large` (`yourock-font`), `text_a1` (`fit-font`, for the letter fit), and the 45 `Fret_Flames` frames (`you-rock-flames.png`, 384 × 216 cells).
  - `YouRockHud` draws the text and the flames. `IntroHudAnimation` moves the highway (a vertical offset in the player camera projection) and the rock meter and score panel.
- **Calibrated by eye or by fit.** The highway rise in the recording starts 0.82 s earlier and runs at 0.64 of the `move_highway_2d` speed. These two values are a least-squares fit to nine board-top positions; the cause is not known. The jiggle random values come from a local generator, not from GH3's shared generator.
- **Check.** My Curse in The Dive (`~/.cache/gh3ref/agent-showflow/intro-compare.png`, `outro-compare.png`, `sbs-intro.mp4`, `sbs-outro.mp4`). The highway rise, the HUD entrance, the "You Rock!" start (253.8 s), the flames, the explosion of the letters, and the highway removal agree with GH3 within one or two frames. The audio envelope from 251 s to 262 s has a correlation of 0.97 with GH3.
- **Not done.** The song title text (`intro_song_info`), the button ripple, the hand flames that stay on during the win (GH3 `play_outro` stops them), the crowd `Idle` command of the win transition, and the win-camera geometry (owned by the camera work). Practice mode keeps Clone Hero's own lead-in.
- **Regenerate in main.** `prepare-library.py` (or `prepare-direction.py --output assets/libraries/gh3-band-v1/direction` and then the catalog/library manifests), `export-gh3-stages.py` for all venues (for `cameras_win`), and `prepare-fonts.py`.

#### Venue material parity (fix-venues)

- **Fault.** The exporter kept the GH3 alpha test (material byte +232) only for two templates (`D_N_S_1SeperateChnl`, `EnvMask_D_N_S_2ChkBox`). This was a Budokan fix that was not general. In all ten venues, 157 material entries with an alpha test drew as solid quads. Examples in Lou's Inferno: white backgrounds around the spike rows (`3d9af1e4`), the candelabra (`e509beab`), and the dragon tail. The `AnimatedTexture` template (unlit) did not animate, so the full flame atlas showed on one quad. The second texture, mask, and UV motion of layered templates were not exported. Thus the glowing parts of `D_3ChkBox_Pass0_Pass1` surfaces were missing (for example, the burning rims of the Inferno bass drums), `*_Pass1` decals and `Wibble_Simple` smoke, rings, and swirls drew as full rectangles, and `Wibble_Simple` did not move.
- **GH3 data.** `tools/disassemble-ps.py` and the vertex shaders (descriptor +0x50 size, +0x58 offset, directly before the pixel shader) give these rules:
  - Alpha test: all blend modes (0 opaque, 1 additive, 3 alpha blend) use the +232 reference.
  - `AnimatedTexture`, `AnimatedTexture_Lit`, and `AnimatedTexture_Billboard` share one atlas step: vertex constants c0 columns, c1 rows, c2 frames per second, c3 offset.
  - `D_3ChkBox_Pass0_Pass1`: colour = lerp(diffuse(UV0), layer(UV1) × c1, layer alpha × c1.a × mask(UV2).r × vertex alpha), then lighting. The alpha stays the diffuse alpha.
  - `D_N_S_4ChkBox_Pass1`: the same layer model with s3/s4 on UV1, colour c4, and UV scales c5.x/c5.y.
  - `D_3ChkBox_Pass1`: diffuse on UV set 1; alpha × mask(UV set 2).r.
  - `Wibble_Simple`: alpha × mask(UV set 1).r. The diffuse UV turns about (c9, c10) at c8 rad/s. Then it scrolls by c0·t + c1·sin(c2·t + c3) in U and by c4·t + c5·sin(c6·t + c7) in V.
  - Vertex flags bit 17 + 2n mark UV set n.
  - Many `D_N_S_*` pixel shaders scale the UV by a constant before the sample (`mul rN.xy, cK.c, v0`). `gh3_materials.classify` now returns these scales as `uvScales`, and the exporter applies them. Before, the mod ignored the scale (×3 and ×10 tiling on a few materials).
- **Fix.**
  - `prepare-stage.py` reads all UV sets and writes `GH3STG03` static meshes (60-byte vertices: the lightmap UV, then the layer UV and the mask UV).
  - The GLB carries the cutoff as `MASK`/`alphaCutoff`, or as `extras.gh3AlphaCutoff` for blended materials. It also carries `gh3MaskTexture`, `gh3LayerTexture`, `gh3LayerColor`, `gh3UvWibble`, and `TEXCOORD_2`/`TEXCOORD_3`.
  - `Stage.glsl` discards below the cutoff, mixes the layer, applies the mask, and turns and scrolls the UV. `StageUvWibble` computes the motion from the song clock.
  - The importer trims geometry to the alpha mask only for unshaded (particle-shader) materials, because the surface shader now discards. Budokan and Party now have fewer vertices.
- **Not ported (reported by the audit).**
  - `D_S_3ChkBox_Pass0_Pass1`, `D_NBlend_S_3ChkBox_Pass0_Pass1`, `D_N_S_4ChkBox_Pass1_Pass2`, `Env_D_N_S_Decal_3ChkBox_Pass1/Pass2`: these draw the base layer only.
  - `Wibble`, `Wibble_Rot`, `Wibble_Pos_*`, `Wibble_Simple_Billboard`: these are static.
  - Material +212 (a float, non-zero mostly on blended materials, 1–50) is probably a draw order. The mod does not use it yet.
  - The layer colour is c_layer / c_diffuse because the diffuse constant is in the vertex colours. When a diffuse channel is near 0, 8-bit vertex colours make that channel of the layer less accurate.
- **Regenerate.** Run `export-gh3-stages.py` (all venues) and `prepare-stage-shaders.py` (stage bundle). Older `GH3STG02` packs still load.

### Installation state

- Version `0.9.8` is installed in `~/games/clonehero-gh3-melon` with all ten re-exported venues, the regenerated band library, and the rebuilt `render/stage.bundle` (the light-data texture needs it). DLL SHA-256: `6355e437839341495fc084bfebfc49adb0a193f0964ec12db090c767899a5d52`.
- The previous `0.8.115` install (DLL and `Mods/GH3Skin`) is backed up in `~/.cache/gh3-install-backup-0.8.115`.
- Verification for this build: logic tests, asset-driven venue script checks, Python tests, and the 33-set regression pass. Headless captures of all ten venues load and render; with network isolation, Video and Budokan passed three of three runs each. Captures end by timeout, so they do not establish a clean process exit.
- Evidence (not in Git): `~/.cache/gh3ref/` holds the GH3 Slow Ride recording (`gh3-slowride.mp4`), the Dive constant trace (`dive-slowride-ps-constants.log.zst`), matched frames, and comparison sheets.

### Matched capture workflow

1. Record GH3 with `tools/gh3-reference/` (`start-gh3.sh <display> [trace-regex]`, `keys.py <shot> keys...`, `stop-gh3.sh`; work files go to `~/.cache/gh3ref`). Verify each menu step with its screenshot. The menu path is (Return, Return, Down+Return, Down Down Return, Up Return, Return). Record with `ffmpeg -f x11grab`. Set the start script's trace filter to log `wined3d` shader constants.
2. Align: camera cuts in the GH3 video match the exported `gh3-camera` event times; Slow Ride had song time = video time − 14.76 s.
3. Capture the mod: `tools/capture-stage.sh <stage> <out> <chart> <song times> <seconds>` with a chart whose title and artist match the original song (no authored events). `GH3_SKIN_CAPTURE_CLOCK=song` is the default there.
4. GH3 chooses shots within a camera group at random. Sweep candidates with `GH3_SKIN_CAMERA_SHOT=<shot name>` and pick the one matching GH3's framing. Combine frames with `tools/compare-sheet.py`.

The isolated test game's `Mods/GH3Skin/assets/stages` is a **symlink to this repository's `assets/stages`**. Never delete through it.

---

Status checked: **2026-10-07** (earlier session).

## 1. Goal and immediate direction

Reproduce GH3 stage and performance behavior in Clone Hero. This includes original commands, assets, timing, rendering, audio, and responses to gameplay. Original imports and custom authored content must use the same playback system.

**Full parity is not complete.** The current system has broad coverage and many reference checks, but preservation, command execution, rendered output, and full-scene parity are different milestones.

The user asked for full GH3 event coverage and does not want the work to stop at a small supported command list. The user also redirected the previous agent after loader and memory diagnosis took over the work. **Continue visible and audible effect implementation. Use the retained references and focused in-game checks first. Keep crash investigation bounded to a blocker.**

Recommended next task: complete a model-light command path through state, dispatch, and visible rendering. See [section 6](#6-immediate-work-model-light-controls). Do not restart the Tesla implementation or repeat the completed asset inventory.

## 2. Repository and installation state

| Item | State |
| --- | --- |
| Mod repository | `/home/hedi/Work/personal/gh3-clonehero-skin` |
| Branch and HEAD | `main`, `ee94b4e` — `Add custom stage packs and per-channel song direction` |
| Working tree | Large set of modified and untracked files. All later mod development is uncommitted. Preserve it. |
| Version string | Still `0.8.115`; it does not identify the latest development build. |
| Main Clone Hero installation | `/home/hedi/games/clonehero-gh3-melon/` |
| Main installed DLL | Earlier verified 0.8.115, without the later script/render development |
| Isolated live-test game | `/tmp/opencode/gh3-crowd-runtime/game/` |
| Editor repository | `/home/hedi/Work/personal/gh3-chart-editor/` |
| Editor HEAD | `3b419a256e8795dd9e553fede7a6e11b27d55401`; working tree clean at handoff |
| Editor fork | <https://github.com/heditwo/gh3-chart-editor> |
| Editor upstream | <https://github.com/FireFox2000000/Moonscraper-Chart-Editor> |
| Active live runners | None at handoff |

The editor commit was pushed to `master` and `event-editor` with user approval. No PR was created. Do not infer approval to commit or push the mod. The user's standing instructions require an explicit request for commits, pushes, or PRs.

Set `MISE_QUIET=1` for shell commands. Read the user's current `AGENTS.md` before work. Do not create a clean checkout and assume it contains this development state.

### Local files required to continue

| Path | Use |
| --- | --- |
| `assets/event-audit/behavior-work/` | Reference generators, captured reference JSON, logs, live reports, images, and probe project |
| `assets/event-audit/verified/` | Song and command inventory |
| `assets/libraries/gh3-band-v1/` | Shared rigs, clips, scripts, audio, textures, movies, and render metadata |
| `assets/stages/` | Ten imported venues and `example.studio` |
| `assets/stage-packs/` | Editable stage-pack sources |
| `assets/stages/events/` | Exported direction for 159 songs |
| `/home/hedi/games/gh3-test-tools/game/DATA/` | Original PC game data |
| `/home/hedi/games/gh3-test-tools/game/GH3.exe` | Original PC behavior reference |
| `/home/hedi/games/gh3-test-tools/toolkit-source/QBDebug/` | `gh3_keys.txt`, `keys.txt`, and `debug.txt` checksum dictionaries |
| `/home/hedi/games/gh3-matched-capture/` | Existing original-game capture setup |

Generated assets and much of the evidence are excluded from Git. A fresh agent on this machine can use them; a remote checkout alone cannot reproduce the current checks. Preserve failed evidence as well as passing evidence.

## 3. Requirements and design decisions to retain

- Support original commands and arbitrary authored command text, arguments, custom track names, and numeric note IDs. Editor presets are optional conveniences.
- Authors do not need native PAK/QB containers. The accepted editor workflow is save `.msce`, export `notes.mid`, and play with the mod. `.chart` remains usable where its format represents the data.
- A camera-only edit must preserve unrelated song data, including unknown fields, command order, durations, velocity where supported, and asset references.
- Preserve four performers, the default male singer, original rigs and instruments, cameras, lights, moving fixtures, crowd behavior, and 100-note-streak hand fire.
- Native, converted, and authored calls share `Gh3CommandDispatch`. Authored text uses `gh3 ` followed by `Gh3ScriptEvent.Encode` JSON.
- Retain typed values, repeated fields, unnamed flags, unresolved checksums, and source order. Do not flatten QB structures into dictionaries that lose this information.
- Chart/MIDI timing controls authored playback. Original row `time` remains metadata. Keep retained `Cues` separate from generated `PlaybackCues`.
- Direction v2 resolves each channel through file/clear → chart/MIDI → original-song library → defaults. Playable chart timing and explicit strums have priority. Direction v1 replaces the whole timeline. An optional native song PAK takes priority over the converted direction paths.
- Audio uses Clone Hero's BASS library. Unity's `AudioSource` class is stripped in this game.
- A missing binding must remain visible in diagnostics. Do not mark a command complete by returning success without its behavior.
- Known PC constant-result functions are an exception only where original complete bodies were verified. Runtime precedence is `Builtin(...) ?? _native(...) ?? Gh3PcCommands.Execute(key)`, followed by optional-missing handling or script execution.

## 4. Implemented baseline and verified evidence

Read [GH3-EVENT-COVERAGE.md](GH3-EVENT-COVERAGE.md) for the detailed, chronological ledger. Some early paragraphs describe older limits that later entries supersede. Older authoring documents also predate the working editor fork.

### Implemented areas

| Area | Current baseline | Limit |
| --- | --- | --- |
| Assets and presentation | Ten venues, four performers, instruments, original clips, crowd models, cameras, fixtures, and source object identities; drumstick, hair, transparency, and streak-fire fixes | Imported assets do not establish complete behavior or final composition |
| Direction and preservation | Native reader, typed JSON, chart/MIDI carriers, channel overrides, timing, pause, and seek | Full original-song editor UI round trip remains pending |
| Script runtime | Tested waits, branches, loops, locals, arithmetic, RNG, object context, callbacks, components, cancellation, and event wakeups | Engine bindings and whole-game scheduling are incomplete |
| Scenery and actors | Creation, hiding, movement, rotation, scaling, attachments, source animation callbacks, and parts of encore sequences | Complete sequences and game-state behavior remain partial |
| Crowd and sound | Lighters, hands, crowd profiles, divers, count-offs, reactions, BASS sample playback and bus controls | Exact selection, timing, mixing, and original-game comparisons remain partial |
| Lighting | Source lightshows, note maps, snapshots, independent fades, groups, transforms, spatial selection, particle light binding, and world-light controls | Model-light controls, shadows, and complete stage shading remain incomplete |
| Particles | Extensive source parsing, emission, birth, motion, bounce, wind, LOD, visibility, prewarm, geometry, ribbons, materials, maps, and per-emitter batches | Whole-stage ordering, observations, shared RNG, extra passes, and composition remain incomplete |
| Tesla | 30-slot state, original strip geometry, translated shader, source noise image, blend conversion, and song-clock rendering | Original scheduling, sampler selection, and shared composition remain unverified |
| Other effects | Movies, material changes, animated textures, outlines, and screen-color transforms | Further targets, passes, controls, clocks, and full-scene comparisons remain |
| Editor | Preservation fixes and F4 event browser with search, filters, presets, raw arguments, direction notes, ordering, and undo | Typed inspection, source-song UI import, stage/character authoring, and preview remain |

### Latest completed checks

- `assets/event-audit/behavior-work/world-lights-regression.log` passes **33 stored reference sets**, 159 song imports/exports, preservation of 6,297 calls, 102 crowd clips, 227 sound samples, and eleven packs.
- The regression runner loads saved reference files. It does not regenerate all references or run Clone Hero.
- The PC registry inventory has 1,170 entries. Only 136 complete constant-result bodies are covered by `Gh3PcCommands`, checked through 1,632 original calls. This is not support for the entire registry.
- Tesla state checks cover 2,676 actions and 61,577 slot states. Geometry, constants, and blends match 128 supplied draws across two original geometry paths.
- Video and Wikker each pass 351 Tesla GPU samples, including 36 source-noise samples, plus visible scene controls, exact restoration, pause, and repeat seek. Both exit 0. `tesla-live-runs.json` checks two runs and retains three failed attempts.
- `PushWorldLights` and `PopWorldLights` pass 340 reference commands and 4,351 queries. Party and Soundcheck pass visible controls and clean exits.
- `light-scene-live-runs.json` currently checks 21 passing runs on several recorded builds and retains eight failed attempts. It is not a single-build ten-venue result.
- The latest complete baseline sweep passed eight venues. Dive and Hell exited 139 before reports. A later Dive attempt exited 0 without a report and also failed verification.

### Build identities

| Evidence | SHA-256 |
| --- | --- |
| Current development DLL; world-light live checks | `5cb84de41850f9cee271dce39267baed3eae40bdfa2254aad51d313459a5b2e5` |
| Tesla live-check DLL | `20944efeb509db4675ab3e48d252c325646b26674353f0a8699e1f9c5984bb3a` |
| Earlier particle order/capacity/lit-trail DLL | `f184a63d77e67c6f24e7c2e64324f4f0787f8692c705b3c6155571c569dcbd72` |
| Main installed DLL | `4594d138dab419a9354f5e98c0afdf525fedc7e9d63d48c891293bc344d48ded` |
| Tesla shader bundle | `d37895ca9b95f3fce0f613e68924e9a4ff1b2e25ba6bd74d595c261e0575969d` |
| Tesla noise PNG | `faa04d6d1c461815f5a2ce533e576f78914f7e3719553a86b57f48e0afe326fe` |
| Original PC executable | `a565dd36e47e9a5705df12ceef6bd345c9c06e3fd8be8aee362987d9f40d3d2b` |

Do not attribute a passing run to a later DLL. Use each run's `execution.json`, `result.json`, retained `stage-events.json`, and images.

## 5. Remaining parity work

### A. Complete commands and source dependencies

- [ ] Finish model-light controls: `SetModelLightsFadeTime`, `SetLightGroup`, `RemoveCurLightGroup`, the corresponding current-group getter, and `UpdateFxLighting`.
- [ ] Complete shadow direction/render controls, including `SetShadowDirFromLight`, and remaining world-light lifecycle such as `KillLight`. Push/pop are already implemented.
- [ ] Finish remaining light setters, attenuation changes, initialization, reset, cancellation, and spatial-update timing.
- [ ] Audit remaining applicable engine and object-member commands. Classify each as implemented, partial, missing, verified PC no-op/constant, or outside stage/performance scope. The global registry alone omits object members.
- [ ] Reconcile song-startup QBs with export and playback. There are 139 songs with a second QB and 138 startup bodies, separate from the 33 distinct direct song-event commands.
- [ ] Resolve missing singer animation loaders. Example: `AnarchyInTheUK_song_startup` (`0x0a44fa5a`) calls `animload_Singer_Male_AnarchyInTheUK` (`0xad0b4d3b`), absent from exported `scripts.json`.
- [ ] Resolve `Band_PlayChordC` (`0x08d688e9`) and the wider chord-wrapper path.
- [ ] Identify direct song command `0x19957809`: 13 `dlc51` performance calls with `Anim`, `name`, and `Cycle`. Identify `0xe3a71ab3`: one `dlc74` performance call. Neither was found in the native global registry.
- [ ] Complete venue-local model/material actions such as `Z_JPPlay_SetVideoScreens`, and verify complete encore sequences rather than isolated calls.

Start with `src/Native/`, `src/Stage/StageScriptState.cs`, `StageScriptBank.cs`, `StageLightScene.cs`, `StageLightTransition.cs`, and `StageObjectState.cs`.

Completion gate: source and authored calls reach the same binding; defaults, types, repeated parameters, missing objects, ordering, and cancellation are checked; the expected effect is visible or audible in-game. Keep partial diagnostics where the remaining renderer or scheduling behavior is still missing.

### B. Finish rendered effects and scene composition

- [ ] Add actual shadow maps and source `Shadow` behavior. Storing `ShadowIntensity` is not shadow rendering.
- [ ] Add bloom and fill-burst passes and their source controls.
- [ ] Complete particle spatial candidate order, original creation order, allocation reuse/failure behavior, the 150-success selection path, render observations, geometry/upload order, worker partitions, and shared ordering with stage geometry.
- [ ] Complete particle occlusion, additional viewports and flags, and separate spline-particle `knot` behavior.
- [ ] Verify attachment velocity sampling and camera/update order in a complete scene.
- [ ] Verify Tesla engine update order, expiry scheduling, original clock mapping, texture sampler selection, and composition with other effects. Its strip and shader translation are already implemented.
- [ ] Complete outline stencil/blend interaction and object coverage; verify screen-effect clocks, types, and viewport targets.
- [ ] Complete movie UV/orientation, replacement/blocking/wait behavior, and matched pause/seek output. Original frames already render.
- [ ] Verify Budokan's animated backdrop atlas in final output and complete general material targeting, prefix rules, and parameters.
- [ ] Capture matched GH3 and Clone Hero scenes with the same song time, camera, venue, and performance state. Compare full output, not just an isolated shader or changed-pixel count.

Main files: `StageParticles.cs`, `StageParticle*.cs`, `StageTesla*.cs`, `StageLightingRenderer.cs`, `StageLight*.cs`, `StageOutlines.cs`, `StageScreenEffects.cs`, `StageMovies.cs`, `StageMaterialState.cs`, `StageTextureAnimation.cs`, and `src/Stage/Shaders/`.

Completion gate: source visuals and controls match in a representative original scene and an authored scene; pause, seek, restart, restoration, and process exit pass. Record the remaining limits of any isolated comparison.

### C. Complete script and gameplay scheduling

- [ ] Verify seed initialization and random draw order across scripts, actors, crowd, particles, and queued work. Individual RNG and history tests already pass.
- [ ] Verify global time scaling and clock mapping, general spawned-task waits, and same-time ordering across systems.
- [ ] Complete script-level bounce callback parameter comparison. Current native evidence substitutes the script runner.
- [ ] Verify complete component creation, update, event-wakeup, cancellation, removal, and reset order in a scene.
- [ ] Extend live gameplay events beyond colored guitar hits: drum events, chords, battle/deathlick behavior, and relevant mode-dependent events.
- [ ] Complete band command parameters, animation strength from velocity, blend/timing rules, character profile and song-specific asset selection, and facial/instrument behavior.
- [ ] Complete crowd tempo/skill selection, camera-dependent representation, reactions, diver behavior, and matched full-scene checks.

Main files: `Gh3ScriptRuntime.cs`, `Gh3ComponentWait.cs`, `Gh3Random.cs`, `StageScriptState.cs`, `StageActorState.cs`, `StageScriptActorState.cs`, `StagePerformance.cs`, `BandAnimation.cs`, `StageNoteHits.cs`, and `StageCrowd*.cs`.

Completion gate: actual Clone Hero input and game-state changes cause the expected GH3 actions. A command-line bot is useful for deterministic input but does not cover every gameplay mode.

### D. Complete audio parity

- [ ] Match buses, sample selection, random scheduling, simultaneous voice mixing, fades, locks, cancellation, and game-mode rules.
- [ ] Compare audible pause/resume, backward/forward seek, restart, interrupted count-offs, and crowd reactions against GH3.
- [ ] Keep sample playback evidence separate from mixing fidelity. The Budokan correlation capture proves audible source samples, not complete mixing parity.

Main files: `StageAudioState.cs`, `StageAudioBackend.cs`, `StageAudio.cs`, `StageSoundBank.cs`, `prepare-stage-audio.py`, and `behavior-work/runtime-audio/`.

### E. Finish authoring and the complete workflow

- [ ] Extend the editor's typed command inspector without restricting raw command text or unknown values.
- [ ] Import an original song through the editor UI; save, reopen, edit one camera event, undo, and export. Compare all unrelated tracks and fields after every step.
- [ ] Check tick/millisecond conversion and repeated saves for timing drift, including tempo edits.
- [ ] Verify original imports and custom authored events through the same Clone Hero playback path.
- [ ] Implement custom stage and character loading/selection, a usable Blender authoring workflow, and editor-to-game preview.
- [ ] Decide the remaining binary-writing work against the actual authoring requirement. A general QB writer is absent, but native containers are not required for the accepted workflow.

The editor is based on upstream `7b4a7eab62e286b2a7bc2b21d2c22d4f65ff44fd`, version 1.5.13. Its completed Linux checks passed 50 cases. Existing UI evidence is in `/tmp/opencode/gh3-editor-tests/verified-results-5/`. Build it from the editor repository with `MISE_QUIET=1 python tools/build.py build/linux`.

Use [MOONSCRAPER-PRESERVATION.md](MOONSCRAPER-PRESERVATION.md), [MOONSCRAPER.md](MOONSCRAPER.md), [CUSTOM-CONTENT.md](CUSTOM-CONTENT.md), and [STAGE-PACKS.md](STAGE-PACKS.md). Some planning text is older than the pushed editor fixes.

### F. Validate and deliver

- [ ] Obtain a complete ten-venue result on one identified development build with reports and exit 0 for every venue. Keep all failed attempts.
- [ ] Finish matched original-game visual/audio comparisons and close the remaining applicable coverage entries.
- [ ] Refresh shader bundles, render metadata, library/stage manifests, and file hashes after asset changes.
- [ ] Build and install verified changes in the main game, then verify installed DLL and asset hashes. The current main game does not contain the development DLL.
- [ ] Update version/release notes and remove stale coverage claims. Commit or push only after an explicit user request.

## 6. Immediate work: model-light controls

This was the next effect task when the handoff was requested. **No model-light implementation was added.** The latest runtime changes remain Tesla and world-light controls.

Suggested sequence:

1. Choose one original model-light use case from the exported scripts and one small authored case.
2. Define its target object, initial group, group changes, fade duration, removal behavior, and expected visible result.
3. Add model-light state and connect it to the shared command path and renderer. Avoid a success-only binding.
4. Check original behavior where the existing evidence is insufficient, then run focused logic/reference checks.
5. Add an in-game image control with restoration and pause/seek checks.
6. Update the coverage ledger, then proceed to the next incomplete effect.

Relevant current structure:

- `StageScriptState.Reset` creates `LightScene`; `Native` sends light commands through it. Reset and replay must also reset new model-light state.
- `StageLightScene` owns lights, groups, per-light channels, mutable poses, and spatial membership.
- `StageLightingRenderer` shades static and band meshes using their stored source group. It currently has no complete per-model light-group stack/fade path.
- `StageAssets.LitMesh` and `LitBandMesh` retain group data. Object identity also exists in `Objects` and `MaterialSurfaces`; inspect the asset-loading code before adding another identity mapping.

Retained reference notes, for a specific behavior question only:

| Command/path | Existing evidence |
| --- | --- |
| `SetModelLightsFadeTime` | Wrapper `0x541060`, setter `0x5d12c0`. Reads parameter checksum `0x906b67ba`, handles nonpositive input, then writes a global fade duration. Parameter values and render use still need tests. |
| Model-light update | `0x5d1360` updates stored blend entries with a supplied delta and the shared fade duration. |
| `RemoveCurLightGroup` | Wrapper `0x540ed0` resolves a named object's model component. `0x5d14c0` decreases stack count only above one. |
| Current-group getter | Wrapper at `0x540f40` calls `0x5d14d0`; it reads the top group and returns field checksum `0x7e4813a7`. Resolve the registry name before binding it. |
| `SetLightGroup` | `0x412df0` enters shared dispatch `0x412b80`; the retained listing starts with a jump to `0xd7c938`. The complete command path has not been verified. |
| `UpdateFxLighting` | `0x541100` calls `0x5901c0`. Retained instructions show iteration over stored FX records and color writes. Full object/renderer semantics remain unverified. |

Listings are under `behavior-work/native-541060-light-commands.txt`, `native-540ed0-light-commands.txt`, `native-412df0-light-commands.txt`, `native-412b80-light-lifecycle.txt`, `native-5d12c0-model-lights.txt`, and `native-5901c0-model-lights.txt`. These notes are leads, not completed parity evidence.

## 7. Build and verification commands

Run from the mod repository root. Use unique log names to preserve prior results.

### Logic/reference regression and builds

```bash
MISE_QUIET=1 python assets/event-audit/behavior-work/run-regression.py next-parity-regression.log
MISE_QUIET=1 dotnet build src/Gh3Skin.csproj -c Release --nologo
MISE_QUIET=1 dotnet build assets/event-audit/behavior-work/probe/BehaviorProbe.csproj -c Release --nologo
MISE_QUIET=1 git diff --check
```

The runtime/probe target .NET 6; the logic tests and song tool use .NET 10. The installed SDKs currently build them. Python asset tools use `uv` dependencies named below or in their existing commands.

### Live tests

**Run only one live runner at a time.** Runners share the test game, test song, display, assets, and probe DLL. They copy the current built DLLs into the isolated game and archive earlier output under `runtime-history/`.

Use a disk-backed temporary directory under the user's cache:

```bash
MISE_QUIET=1 GH3_BEHAVIOR_PARTICLE=tesla \
  TMPDIR="$(mktemp -d "$HOME/.cache/gh3-XXXXXX")" \
  python assets/event-audit/behavior-work/run-runtime.py video wikker

MISE_QUIET=1 GH3_BEHAVIOR_LIGHTS=1 GH3_BEHAVIOR_WORLD_LIGHTS=1 \
  TMPDIR="$(mktemp -d "$HOME/.cache/gh3-XXXXXX")" \
  python assets/event-audit/behavior-work/run-runtime.py party soundcheck
```

These are separate runs; wait for each to finish. The runner configures the isolated audio session, offline loader, and display `:18293`.

Other useful modes:

| Environment | Check |
| --- | --- |
| No effect-mode variable | General venue behavior baseline |
| `GH3_BEHAVIOR_PARTICLE=order` | Authored particle draw order |
| `GH3_BEHAVIOR_PARTICLE=render-list` | 190 emitters and submission cap |
| `GH3_BEHAVIOR_RIBBON=capacity` | Large ribbon geometry |
| `GH3_BEHAVIOR_PARTICLE=lod`, `visibility`, or `fillpath` | Focused particle behavior |
| `GH3_BEHAVIOR_RIBBON=v`, `x`, `flat`, or `wind` | Source geometry, update script, or wind |
| `GH3_BEHAVIOR_INPUT=1` | Controlled gameplay note input |
| `GH3_BEHAVIOR_REALTIME=1` | Realtime/audio capture path |

Lit-trail mode uses all of these: `GH3_BEHAVIOR_LIGHTS=1`, `GH3_BEHAVIOR_LIGHT_UPDATES=1`, `GH3_BEHAVIOR_PARTICLE_LIGHTS=1`, `GH3_BEHAVIOR_PARTICLE_MAPS=1`, and `GH3_BEHAVIOR_PARTICLE_LIT_TRAIL=1`.

The ten venue arguments are `party dive video artdeco prison wikker budokan hell soundcheck credits`. The runner stops at a failed venue. Do not describe later venues as attempted if the process stopped early.

### Evidence summaries

```bash
MISE_QUIET=1 uv run --with numpy --with pillow python assets/event-audit/behavior-work/summarize-tesla-runs.py
MISE_QUIET=1 uv run --with numpy --with pillow python assets/event-audit/behavior-work/summarize-light-runs.py
MISE_QUIET=1 uv run --with numpy --with pillow python assets/event-audit/behavior-work/summarize-particle-render-runs.py
```

Assertions, rendered/audible output, process exit, and build hashes are separate checks. A passing report followed by a crash is a failed run. Exit 0 without a report is also a failed run. Require exact restoration and exact repeat pairs where the existing probe calls for them.

### Regenerate assets only when needed

```bash
MISE_QUIET=1 uv run --with UnityPy --with lz4 --with numpy python prepare-stage-shaders.py
MISE_QUIET=1 uv run --with pillow --with numpy python prepare-particle-materials.py
MISE_QUIET=1 uv run --with pillow --with numpy python prepare-tesla.py
MISE_QUIET=1 uv run --with pillow --with numpy --with 'shapely>=2.1' python export-gh3-stages.py --venue party
```

`prepare-stage-shaders.py` produces screen-color, outline, particle, and Tesla bundles for Unity `2022.3.62f2` and OpenGL. Particle rendering also requires `render/particle-materials.json`. Tesla requires `tesla.bundle`, `tesla-noise.png`, and its source/hash metadata `tesla-material.json`. `prepare-library.py` now calls the Tesla texture exporter.

Reference generators named `check-native-*.py` usually need `uv run --with pefile --with unicorn --with numpy`. Check each script's imports before running it. `check-native-tesla-shader.py` needs `numpy` and `pillow`. Regenerate only references affected by a change, and retain their comparison scope and substituted-function list.

## 8. Important behavior and integration details

### Script runtime

- Actor callbacks use `animeventtablename`. Engine scheduled callbacks use separate `CancelScheduled` behavior.
- Component scripts retain locals and waits, advance through component updates, and are excluded from general global scheduling and `Cancel`. Event wakeups permit their next update.
- Random-choice history is task-owned and uses the original reusable 100-entry pool. Particle draws use the shared main RNG; `TimeSeed` uses a separate CRT RNG.
- Missing Dive/Wikker mood-transition definitions are optional engine calls returning false. Explicit missing authored calls retain diagnostics.
- `HasScript` checks type-7 script definitions, not every recognized engine command.

### Lights and materials

- Light color, intensity, specular, and shadow channels are independent. Duration truncates to milliseconds. Default easing is `smooth`; omitted easing retains the prior curve.
- Direct setters and snapshots share mutable channel state. Omitted rows retain fades; omitted specular/shadow fields retain current values. Group scaling does not mutate stored channels.
- Light renderer identity comes from `StageLightDefinition.Object`.
- `MoveLight` preserves the position-to-bounds-center offset. Direction changes leave bounds unchanged. Neither setter rebuilds spatial membership; changing `Dynamic` registers current bounds.
- `PushWorldLights` disables all current groups. `PopWorldLights` enables all current groups. They do not restore a saved stack.
- Source `Shadow` still travels through the legacy `SpecularPower` field in parts of the implementation. Actual shadow rendering is unfinished.
- `UpdateMaterialProperty time` is a whole-second hold followed by restoration. A later update does not cancel an earlier restoration.

### Particles and Tesla

- Preserve source-field order, node-prefix order, bone IDs, texture flags, material identity, and shader constants.
- Emission uses a fixed script frame, native accumulator resets, velocity-first motion, and frame-based color progression. `FillPath` consumes an extra random draw only when the emitter moves.
- `EmitScript` is a verified PC no-op. Bounce callbacks resume a persistent component with empty setup parameters; do not invent collision-buffer locals.
- Attached particles have the native `lockobj` off-screen exemption. Visibility uses quantized bounds, retained observations, and the preceding fixed-frame camera pose.
- QuickMaterial defaults to additive blending. FlatRibbonTrail heads can be lit while the trail quads stay unlit. Lighting is per emitter.
- Native particle keys retain original shader IDs/flags. Equal keys reverse submission order in the checked path. Runtime batches cap each mesh at 8,192 quads and the current submission list at 150 emitters; complete candidate ordering is still pending.
- Nonzero `default_light_group` overrides the particle fallback. The exported source value is `None`, checksum `0x806fff30`.
- Tesla allocation stops at the first free slot or matching active name. A free slot before a match can produce duplicate names. Set/delete use the first matching slot.
- Tesla omitted name is zero, start defaults to UnitX, and end defaults to zero. Exact-zero remaining lifetime stays active; only a decrement below zero removes a positive-lifetime effect.
- Tesla `fadeOut` controls the shader edge. The tapered path derives amplitude from `lightWidth`, rather than the stored `amplitude` parameter.

### Unity and probe constraints

- Use `AssetBundle.mainAsset`, `mesh.triangles`, named color channels, and `CommandBuffer.Internal_DrawMesh(..., null)` with this stripped game build. `Mesh.SetTriangles`, the `Color` indexer, and `Vector4.Magnitude` have failed unstripping.
- Retain shaders/assets across unused-asset unloading. Stage cleanup runs in `OnApplicationQuit`; `OnDeinitializeMelon` clears the reference and performs the same cleanup if needed.
- Particle mesh payload uses normals/tangents, width and bitangent X in UV2, and bitangent Y/Z in UV3. Tesla uses a separate mesh/shader path.
- Raw Unity pixel hashes are bottom-up. Reverse decoded PNG rows before hashing.
- Standard exact repeat pairs are steps 7/8, 9/10, 9/12, and 12/13.
- Map checks need separate normal/specular differences somewhere in the run. An early hidden or minimum-light sample can legitimately have no difference.
- Keep unrelated gameplay input fixed during lighting/effect comparisons. Live note pulses previously caused a Credits pause mismatch through speaker scale.

## 9. Known blockers and retained diagnostic work

Intermittent startup failures remain unresolved. They have occurred before reports, during reflection/class initialization, on finalizer threads, and around native calls. There have also been unexplained zero/duplicate object-ID errors. Do not assume one cause explains all failures.

The shutdown `GameObject.SetActive` failure was fixed by earlier cleanup. That fix does not resolve startup failures. .NET 6.0.25, 6.0.36, and disabled tiered compilation have all had failing attempts. Requested hostfxr overrides were ignored in one experiment. The isolated dependency-path experiment was restored.

Use these retained files if a current effect test is blocked:

- `behavior-work/crash-3067110-stacks.txt` and `crash-3067110-module.txt`: null CoreCLR module fields; corruption origin unknown.
- `behavior-work/crash-3069722-stacks.txt`: Dive baseline crash, null `DomainLocalModule` in reflection.
- `behavior-work/crash-3071297-stacks.txt`, `crash-3071297-hook.txt`, and `crash-3071297-hook-segments.json`: Hell baseline crash; a native jump reads an invalid target from bytes that resemble JIT instructions.
- `behavior-work/bootstrap-mmap-disassembly.txt` and `bootstrap-near-disassembly.txt`: allocator inspection leads, not a proven crash fix.
- `behavior-work/check-bootstrap-hook-memory.c`: scratch diagnostic written before interruption. It was not compiled or run, is not part of the mod, and is not required to resume effect development.
- `behavior-work/clr-symbols/libcoreclr.so.dbg`: matching symbols used in retained stack reports.

The recent allocator investigation did not establish a complete causal chain or produce a runtime fix. Do not claim otherwise or make it the default next task. The user asked to return to effects.

Earlier managed dump analysis could not initialize the matching DAC. Timeout attachment was denied by `ptrace`. Snapshot-error messages occurred in both passing and failing runs. Examined crash intervals had no recorded OOM kill. These facts limit the conclusions; a successful retry does not prove a fix.

## 10. Completion standard

Mark an applicable behavior complete only when all relevant checks pass:

1. Its original source command, parameters, types, flags, references, and defaults are retained.
2. Imported and authored versions use the same implementation.
3. Object context, waits, callbacks, same-time ordering, cancellation, and game-state conditions are correct.
4. The expected effect is visible or audible in Clone Hero.
5. Pause, seek, restart, and replacement/reset behavior are correct.
6. Original-game comparison covers the behavior, including composition where needed.
7. The report identifies the DLL/assets, retains failures, and ends with process exit 0.
8. The coverage ledger states the verified scope without treating parser or isolated-state tests as full parity.

Keep a short active checklist for each implementation step. Deliver complete command-to-output paths and update evidence as they pass. The remaining work is to close these gaps, not to rebuild the systems already listed as implemented.
