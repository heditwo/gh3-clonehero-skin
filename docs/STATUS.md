# Current status and known gaps

Version **0.9.8**. Clone Hero `v1.1.0.6142-final` with MelonLoader 0.7.3, Linux and Windows (one `Gh3Skin.dll`). Windows was tested under Wine with `-force-glcore` only; the stage shaders have no Direct3D 11 programs yet ([spike](spike/d3d11.md)). macOS is not supported.

## Complete

- **Highway and notes.** GH3 camera and highway geometry (measured from the original GH3 recording), Axel's highway texture, GH3 rails, beat lines, regular, HOPO, tap, and Star Power phrase gems with their 30 fps animation, cyan active Star Power gems, GH3 sustain bars with the held glow, whammy bend, and star whammy.
- **HUD.** Score (`num_a9`), streak counter (`num_a7`) with counters of five or more digits, amp and multiplier (captured GH3 DXT5 textures), lamps, rock meter with native health thresholds, six Star Power bulbs with the GH3 entrance, ready banner, active Star Power rails, "Note Streak!" and "Star Power Ready" messages, GH3 hit flames and sparks, intro HUD entrance, and "You Rock!" at the end of the song.
- **Stage.** All ten GH3 venues as stage packs. Four performers with original rigs, instruments, clips, hair physics, fret-hand fingers, drummer IK, and 100-note hand fire. Crowd impostors per row. Original cameras with GH3's shot selection and geometry. Lightshow snapshots, beams, housings, and node flags. GH3 particles, Tesla, movies, outlines, screen color. All 921 used materials draw with their translated GH3 shader templates. Pre-song intro and post-song win sequences.
- **Direction.** Native song PAK, `stage-events.json` versions 1 and 2, chart and MIDI direction tracks, and the original song library for 159 songs. Shared command dispatch for native and authored commands.
- **Custom stages.** The public stage-pack format, compiler, and example pack.

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
| Show flow | No song title text, no button ripple, the hand fire does not stop at the win, no crowd `Idle` at the win. Practice mode keeps Clone Hero's lead-in |
| Lightshow | GH3 changes occur about 0.15 s before the cue times in the recording |

## Remaining work

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

- Skin-pack import and selection (fixed-rig templates).
- Moonscraper: the editor fork has preservation fixes and an event browser. A typed command inspector and an original-song round trip in the editor UI are not complete. See [CUSTOM-CONTENT.md](CUSTOM-CONTENT.md#12-moonscraper).
- A general QB writer does not exist. The native PAK tool changes one camera note only.

### Delivery

- One complete ten-venue run on one build with exit 0 for all venues.
- See [PACKAGING-PLAN.md](PACKAGING-PLAN.md) for the release package plan. Package format v1 is frozen ([PACKAGE-FORMAT.md](PACKAGE-FORMAT.md)); the conversion of the ten venues and the library is next.

## Stability

- Clone Hero sometimes crashes at start or during a run (segmentation fault). Seen causes include CLR reflection, finalizer threads, `libbass.so` in `BASS_StreamCreateFile`, and MelonLoader network threads. No single cause is known. A successful retry does not prove a fix. The cleanup pass on 2026-10-09 saw one such crash on the main install (Dive) and none on the new build in two runs.
- Earlier temporary Xwayland displays reset between clients; the capture scripts use `-noreset`.
- Snapshot errors in the loader log occur in passing and failing runs.
