# Hit effects

The skin draws GH3's own note-hit, held-note, and Star Power phrase effects. It hides all of Clone Hero's hit effects. Before this change, the skin replaced Clone Hero's flame sprites with frames from a third-party flames mod (`reference-flames/`, now removed).

## Regression: flames not visible

In the 0.9.8 build and later, no hit flame showed after the song intro.

- **Cause.** The pre-song intro (`GameplayStage.ShowHighway`, from fix-showflow) raises the highway with a vertical offset in the player camera projection. While the offset was active, Clone Hero's `TrackFadeManager` calculated the track fade (`_FadeParams`) of all of its "Fade Instance" materials through that projection. When the offset went back to zero, the values stayed one viewport too low: for example `(2.33, 2.44, 1.42, 1.49)` instead of `(1.07, 1.13, 0.47, 0.52)`. The track shader then made everything that uses these materials transparent: the hit flames, the held-note sparks, the native sustain glow, the gem bodies, and the open-note sustains. Only the neck material, which the skin sets itself, was correct.
- **Evidence.** A probe build logged `_FadeParams` of the flame material: correct values with `GH3_PROBE_NOLEAD` (the intro lead-in off), wrong values with the intro. Commit `e1c5ae2` (Merge fix-showflow) is the first commit without flames. Its parent `7c27a31` shows them.
- **Fix.** When the rise ends, `ShowHighway` calls `TrackFadeManager.Method_Public_Void_0()`. This recalculates the fade with the normal projection. The logged values are then the same as in a run without the intro.

## GH3 data and code

All values come from the original files: `DATA/PAK/qb.pak.xen` (decompiled with GH Toolkit), `DATA/ZONES/global.pak.xen` (materials in `global_gfx.scn`), and `GH3.exe`.

### Note hit

`guitar_events.qb`: event `hit_note` → `GuitarEvent_HitNote` → `GuitarEvent_HitNote_Spawned`. This script waits one game frame, then spawns `hit_note_fx`:

```
script hit_note_fx
    NoteFX <...>
    Wait 6 gameframes
    Destroy2DParticleSystem id = <particle_id> kill_when_empty
    Wait 10 gameframes
    DestroyScreenElement id = <fx_id>
```

`NoteFX` is a C function (GH3.exe `0x42add0`):

- It creates a `SpriteElement` in the highway container at the button position (`button_up_models.<color>.pos_2d`), with `clonematerial = sys_note_hit01` (`sys_note_hit_blue_01` when `star_power_used`), scale 1, z priority 10.
- It creates a 2D particle system 20 units above the button with `hit_particle_params` (`star_hit_particle_params` in Star Power).
- Both materials use template `AnimatedTexture_UI` with constants 4 × 4 cells at 60 frames per second, additive blend. The textures are `Note_Hit01.dds` (yellow-orange) and `0x9e700d1c` (blue), each 512 × 1024 (128 × 256 cells). Cells 13–15 are empty. Thus the flame plays 13 frames (217 ms) and the sprite is removed after 16 game frames.
- The particle parameters (`guitar_events.qb`): `sys_Particle_Spark01` (`lil_Spark.dds`, 32 × 32), colour (255,128,0,255) → (255,0,0,0), scale 1 → 0.5, one particle each 0.02 s, spread 160°, velocity 10, friction (0, 50), life 0.25 s. Star Power: colour (0,255,255,255) → (0,255,255,0).

Chords run `NoteFX` once for each button. HOPOs, taps, and strummed notes use the same effect: there is no HOPO-specific code. GH3 has no open note.

### Held note

`WhammyFXOn` (GH3.exe `0x42b3e0`) runs for each held button. Its helper (`0x42b110`) creates a `SpriteElement` with `sys_whammy_hold01` (`Whammy_hold_01.dds`, 4 × 4 cells at 30 frames per second, additive) at the button. It also creates a particle system with `whammy_particle_params` (as the hit sparks, but life 0.5 s) 20 units higher. `WhammyFXOff` removes the sprite and stops the particles. The held flame is the same in Star Power.

### Star Power phrase

`GuitarEvent_StarSequenceBonus` runs when the last note of a phrase is hit and no note of the phrase was missed. At each button of that note it creates:

- a `sys_Big_Bolt01` sprite (`big_lighning01.dds`, 8 × 1 cells at 15 frames per second), scale `star_power_bolt_scale` (1.0, 1.5), rotated by `button_models.<color>.left_angle`, z priority 6;
- three particle systems: `sys_Particle_Star01` (blue stars), `sys_Particle_Star02` (white stars), and `sys_Particle_Spark01` (cyan sparks, 15 units higher). The parameters are in the script.

After `star_power_bolt_time` (0.3 s), the script removes the bolts one game frame apart and stops the particle systems.

### 2D particle systems

`Update2DParticleSystems` (GH3.exe `0x428ba0`) runs once per game frame (`script_postcallback_script`):

- It emits one particle per `emit_rate` seconds, up to 64. The direction is `emit_dir − spread/2 + rnd(spread)`, and the start offset is `emit_start_radius + rnd(emit_radius)` along it. Angle 0 is up the screen.
- Each frame, the position moves by the stored velocity, and the velocity changes by `friction × delta_time`.
- Colour fades in 12-bit fixed point; scale fades linearly over `time`.
- With `kill_when_empty`, the system stops emitting and is removed when its last particle dies.

## Implementation

- `prepare-hit-effects.py` exports the seven textures from `global.pak.xen`. It checks each material's texture, additive blend, and animation constants.
- `src/HitEffects.cs` (no Unity code; covered by `tests/HitEffectLogicTests.cs`) has the GH3 timing, the parameter sets, and the particle update. It runs whole 60 Hz game frames. It uses the button positions of `generate_pos_table` (GH3 1280 × 720 units).
- `src/Hud/HitEffectsHud.cs` finds hit notes, held sustains, and completed phrases from Clone Hero's note list. It draws the sprites and particles on the highway HUD layer with an additive shader. Thus the GH3 screen positions match the highway (the layer follows the player camera and the intro rise).
- `GameplaySpriteRenderer` disables Clone Hero's `Flames`, `SPSparks`, `SustainSpark`, `SustainSpark Overlay`, `lightning`, `lightning_white`, `Particles_NoteHit`, `Particles_Sustain`, and the open-note hit flames. `GH3_SKIN_KEEP_STOCK_FLAMES=1` keeps Clone Hero's effects and turns off the GH3 effects.

## Comparison with the GH3 recording

The comparison used `gh3-mycurse-raw.mp4` (song zero at 10.175 s) and a mod recording of the same chart in The Dive, aligned by audio cross-correlation. The files are in `~/.cache/gh3ref/agent-flames/cmp/`.

Median flame size over 54 to 75 single-note hits (song time 29.5–44.5 s), in pixels at 1600 × 900, by frame after the flame appears:

| Frame | 0 | 3 | 4 | 5 | 6 | 7 | 8 | 9 |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| GH3 area | 23 | 766 | 821 | 1584 | 1781 | 684 | 358 | 52 |
| Mod area | 20 | 122 | 712 | 1428 | 1845 | 1328 | 645 | 98 |
| GH3 height | 18 | 69 | 70 | 102 | 107 | 118 | 107 | 103 |
| Mod height | 18 | 43 | 70 | 92 | 110 | 120 | 123 | 110 |
| GH3 width | 14 | 39 | 42 | 52 | 54 | 61 | 68 | 68 |
| Mod width | 11 | 26 | 33 | 45 | 53 | 61 | 62 | 46 |

Size, shape, position, and duration agree within about one frame.

## Differences that remain

- **Timing.** In the mod, the flame starts a median of 4 frames after Clone Hero lights the button. In GH3, it starts 2 frames after the button lights. The skin uses GH3's delay (the hit frame plus one game frame). The extra delay comes from when Clone Hero marks a note as hit. It is not corrected. The mod's gems also reach the buttons about 3 frames later than GH3's at the same audio time (a known difference, see the highway notes in `GH3-PARITY-HANDOFF.md`).
- **Random values.** GH3 draws particle directions from its shared random generator, which all scripts use. The skin uses its own generator, so single sparks are not at the same positions.
- **Brightness.** The additive shader adds texture × vertex colour. GH3's `AnimatedTexture_UI` and `ImmediateMode_UI` pixel shaders were not traced; they can differ in brightness.
- **Star Power check.** The preview bot does not earn Star Power. The phrase bonus was checked on completed phrases in normal play. The Star Power hit colours were checked with `GH3_SKIN_FORCE_STAR_POWER_ACTIVE=1`.
- **Hit while holding.** The skin starts the held-note flame when Clone Hero lists the note as a held sustain. GH3 starts it from its own `whammy_on` state.
