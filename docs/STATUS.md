# Current status and known gaps

Version **0.9.8**. Clone Hero `v1.1.0.6142-final` with MelonLoader 0.7.3, Linux and Windows (one `Gh3Skin.dll`). Windows was tested under Wine with `-force-glcore` only; the stage shaders have no Direct3D 11 programs yet ([spike](spike/d3d11.md)). macOS is not supported.

## Complete

- **Highway and notes.** GH3 camera and highway geometry (measured from the original GH3 recording), Axel's highway texture, GH3 rails, beat lines, regular, HOPO, tap, and Star Power phrase gems with their 30 fps animation, cyan active Star Power gems, GH3 sustain bars with the held glow, whammy bend, and star whammy.
- **HUD.** Score (`num_a9`), streak counter (`num_a7`) with counters of five or more digits, amp and multiplier (captured GH3 DXT5 textures), lamps, rock meter with native health thresholds, six Star Power bulbs with the GH3 entrance, ready banner, active Star Power rails, "Note Streak!" and "Star Power Ready" messages, GH3 hit flames and sparks, intro HUD entrance, and "You Rock!" at the end of the song.
- **Stage.** All ten GH3 venues as stage packs. Four performers with original rigs, instruments, clips, hair physics, fret-hand fingers, drummer IK, and 100-note hand fire. Crowd impostors per row. Original cameras with GH3's shot selection and geometry. Lightshow snapshots, beams, housings, and node flags. GH3 particles, Tesla, movies, outlines, screen color. All 921 used materials draw with their translated GH3 shader templates. Pre-song intro and post-song win sequences.
- **Direction.** Native song PAK, `stage-events.json` versions 1 and 2, chart and MIDI direction tracks, the original song library for 159 songs, the Rock Band `VENUE` track (RB1/RB2 and RB3; camera cuts, lighting, pyro, fog), generated cues from chart sections for charts without direction, and singer lip sync from the chart's vocals (`PART VOCALS` notes and lyrics, or `.chart` lyrics) on songs without GH3 facial clips. Shared command dispatch for native and authored commands.
- **Custom stages.** The public stage-pack format, compiler, and example pack.
- **Packages.** The mod loads the venues and the library from package format v1 (`Mods/GH3Skin/Packages/`, `Custom/Stages`) through a per-machine cache with BC1/BC3 textures and a worker-thread clip prefetch ([PACKAGE-FORMAT.md section 20](PACKAGE-FORMAT.md#20-c-importer)). The older install stays as a fallback.
- **Pickers.** In the menus, F6 opens the stage and band picker ([PLAYER-GUIDE.md](PLAYER-GUIDE.md)): a stage, Random, or Song's choice, with preview images; a character for each band role; the direction options (Rock Band `VENUE` tracks, generated direction). The default stage is Song's choice, else Kaiju Megadome (`gh3.budokan`). A song can select its stage (`venue.chstage` in the song folder, `gh3_stage` in `song.ini`, or the GH3 career venue of a GH3 song); the player can turn this off. Character packages (`.chchar`) import into the cache and play on the role rigs through `StageAssets`, with the instrument of their role from the library band model (character `attachments`). Unused cache folders are deleted at start.

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
| Lip sync | GH3 has no lip sync for songs without facial clips (the face stays still). The mod's lip sync on custom charts is its own addition, with mouth shapes measured from the GH3 facial clips. It reads syllable times, not the voice, so it does not show loudness or pitch. Off with `GH3_SKIN_LIPSYNC=off`; no picker option yet |
| Show flow | No song title text, no button ripple, the hand fire does not stop at the win, no crowd `Idle` at the win. Practice mode keeps Clone Hero's lead-in |
| Lightshow | GH3 changes occur about 0.15 s before the cue times in the recording |

## Remaining work

### Packages

- Rock Band VENUE: tested with 16 real charts (RB1 DLC, RBN1, RBN2, and Onyx conversions of RB3 customs; [tested charts](CUSTOM-CONTENT.md#tested-charts)). All their VENUE events are known to the mapper. Post-processing, spotlights, sing-alongs, and the directed-cut pre-roll are not used; the mapping has choices by judgment ([CUSTOM-CONTENT.md](CUSTOM-CONTENT.md#rock-band-venue-track)). The RBN1 and RB1 charts were not compared with a Rock Band recording.
- Picker: mouse only (no guitar or keyboard navigation inside the window; the keyboard still moves the Clone Hero menu below it). Songs in `.sng` containers cannot suggest a stage (the mod reads `song.ini` and `venue.chstage` from song folders only). Character blend shapes are not animated. The v1 library has one instrument for each role, so `attachments.instrument` cannot select another instrument; a character with its own instrument in its GLB keeps it in every role.
- Development folders are not watched while the game runs, Zip64 packages are not tested, Ogg and MP3 samples in a library are rejected (the importer reads WAV only).
- Runtime stability: the CoreCLR faults seen on 2026-10-09 came from MelonLoader's Dobby hook pages; see [Stability](#stability). A start with a stuck import falls back to the older install after 120 s.

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

- Windows: the five shader bundles have Direct3D 11 programs, and the Windows build draws the stage with Direct3D 11 and OpenGL Core (measured under Wine and DXVK only, not on Windows hardware). The Windows bindings and the BASS path are separate work.
- One complete ten-venue run on one build with exit 0 for all venues.
- See [PACKAGING-PLAN.md](PACKAGING-PLAN.md) for the release package plan. Package format v1 is frozen ([PACKAGE-FORMAT.md](PACKAGE-FORMAT.md)). The `packages` step of `scripts/regenerate.sh` writes the ten venues and the library as packages (`assets/packages/`); the mod does not read them yet. The C# importer is next.
- The Blender add-on (CH Stage Tools, Blender 4.2 or later) makes, opens, validates, and exports venue packages (`.chstage`). The ten GH3 venues open and export again within the round-trip tolerances on Blender 4.2.23 and 5.2.1. It edits actors (armatures with clips), browses the script bank, previews shots (with GH3 proxy rigs for bone anchors) and lighting, and lists visibility and light groups. It also makes characters on the fixed GH3 rigs (weight transfer from the GH3 body, hair bones, clip preview with the game IK) and exports validated `.chchar` packages; the game does not load characters yet ([BLENDER-ADDON.md](BLENDER-ADDON.md)).

## Stability

- Root cause of the random crashes on Linux (found 2026-10-09): MelonLoader 0.7.3 hooks six Il2Cpp functions with Dobby (LavaGang/Dobby `0651926`; the bug is also in its `master`). For each hook, Dobby maps one page with `MAP_FIXED` at (hook target − 2 GB), or (target − 1 GB) for relocated code, and does not check what is mapped there (`search_near_blank_page` in `NearMemoryArena.cc`). In every measured run without the guard, that address was memory in use: CoreCLR's executable reservation (loader heaps, JIT code), ReadyToRun code of framework assemblies (`System.Private.CoreLib.dll`, `System.Text.Json.dll`, `System.Net.Sockets.dll`), or other reservations. Dobby erases that memory, or the owner later writes over the jump address of the hook. The result depends on the address layout of each start, so the crash point changes from run to run.
- Evidence: in the crash of 06:50 (main @ 45f8e67), the main thread jumped through the hook of `il2cpp::vm::Class::FromName` (GameAssembly.so+0x2847140, reached through `il2cpp_class_from_name`), and the jump-address slot held CoreCLR data. In run `d-10` (no mod) the CLR wrote over the slots of `Class::FromName` and `Class::FromIl2CppType` 3.6 s after start, and the game crashed at the address that the CLR wrote. The other crashes stopped in `libclrjit.so` (abort) or `libcoreclr.so` after Dobby erased live CLR memory. In one run (`a-10`) Dobby erased 4 KB of `System.Private.CoreLib.dll` code, and the stage did not load (`Duplicate light housing object 00000000`). The earlier causes in this list (CLR reflection, finalizer threads, `libbass.so`, MelonLoader network threads) agree with this damage, but their link is not proved. Tools: `tools/hook-slots.py` (core dumps) and `tools/hook-page-sampler.py` (live runs).
- Fix: `HookPageGuard` (Linux only). In `OnEarlyInitializeMelon`, before MelonLoader installs the hooks, the mod sends the `mmap` calls of `MelonLoader.Bootstrap.so` through a small stub. Only Dobby's request (one page, read and execute, `MAP_FIXED`, anonymous) goes to the guard. The guard puts the page on the relocation table (`.rela.dyn`) of `GameAssembly.so`, which the dynamic loader does not read again after load and which is always in jump range. If that is not possible, it uses a free page in range, else the original request. The log shows `Linux hook page guard: on (...)` and `... hook page(s) moved to safe pages`. `GH3_SKIN_HOOK_GUARD=off` turns the guard off. A Linux kernel older than 4.17 turns it off.
- Measurement (2026-10-09, one start at a time: preview song, about 8 s of play, then SIGTERM; a failure is a crash, a hang, or a failed stage load). The exact 95% intervals are Clopper-Pearson.

  | Build | Failures | Rate (95% interval) | Runs with a hook page on memory in use |
  | --- | --- | --- | --- |
  | (a) main @ 45f8e67 | 3/40 | 8% (2–20%) | 40/40 |
  | (b) main, `GH3_SKIN_PACKAGES=off` | 1/20 | 5% (0.1–25%) | 20/20 |
  | (c) ae25880 (before the bindings and packages) | 2/20 | 10% (1–32%) | 20/20 |
  | (d) no mod | 1/20 | 5% (0.1–25%) | 20/20 |
  | (f) main with the guard | 0/70 | 0% (0–5%) | 0/70 |

  The differences between (a), (b), (c), and (d) are not significant (Fisher exact test, p = 1.0 for each pair). The crashes are not caused by the bindings, the package importer, or the mod. Without the guard: 7/100 failures; with the guard: 0/70 (Fisher exact, two-sided p = 0.042). This difference is significant at the 5% level, but only just; the stronger evidence is the hook page placement (100/100 runs on memory in use without the guard, 0/70 with it). Without the guard, 6 of 7 failures came from the 34 runs where a hook page erased live memory (not only reserved memory), against 1 failure in the other 66 runs (p = 0.006). A screenshot at song time 20 s is the same with and without the guard (only animated flames and lights differ).
- Open risks: hooks that another mod installs before this mod loads are not guarded. Windows and macOS were not examined (Windows `VirtualAlloc` cannot replace memory in use). The fix belongs in Dobby (check the gap before `MAP_FIXED`, or use `MAP_FIXED_NOREPLACE`); report it to LavaGang/MelonLoader. The CoreCLR and JIT frames were not symbolized (Microsoft publishes no symbols for this 6.0.25 build).
- Each start makes a second `clonehero` process (a child of the game with the same command line, probably from MelonLoader's failed `Unable to create a snapshot` step). It does not stop when the game exits. The capture scripts stop it. Kill it after manual test runs, because it keeps any inherited file locks.
- Earlier temporary Xwayland displays reset between clients; the capture scripts use `-noreset`.
- Snapshot errors in the loader log occur in passing and failing runs.
