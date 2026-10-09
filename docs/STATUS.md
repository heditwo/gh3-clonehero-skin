# Current status and known gaps

Version **0.9.9** (`src/Packages/ModInfo.cs`; 0.9.9 reads package format 1.1). Release readiness: the release zips build and install, but nothing is published ([PACKAGING-PLAN.md section 10](PACKAGING-PLAN.md#10-before-a-release)).

## Platforms

| Platform | Status |
| --- | --- |
| Linux | Supported. Clone Hero `v1.1.0.6142-final` (64-bit) with MelonLoader 0.7.3. All tests and recordings run on Linux (OpenGL Core 4.5) |
| Windows | Supported, with the same `Gh3Skin.dll` and Direct3D 11 (the Windows default). Tested only under Wine 11.17 and DXVK 3.1.1 on Linux, not on Windows hardware ([Release build](#release-build)) |
| macOS | Not supported (PACKAGING-PLAN decision 7). The macOS build draws with Metal only, and the shader bundles have no Metal programs. MelonLoader 0.7.3 has no arm64 release, and Clone Hero's hardened runtime blocks the loader ([spike/macos.md](spike/macos.md)) |

The mod checks the game version and the platform at start. On another version or platform it logs `GH3 skin requires Clone Hero …` and stays off.

## Complete

- **Highway and notes.** GH3 camera and highway geometry (measured from the original GH3 recording), Axel's highway texture, GH3 rails, beat lines, regular, HOPO, tap, and Star Power phrase gems with their 30 fps animation, cyan active Star Power gems, GH3 sustain bars with the held glow, whammy bend, and star whammy.
- **HUD.** Score (`num_a9`), streak counter (`num_a7`) with counters of five or more digits, amp and multiplier (captured GH3 DXT5 textures), lamps, rock meter with native health thresholds, six Star Power bulbs with the GH3 entrance, ready banner, active Star Power rails, "Note Streak!" and "Star Power Ready" messages, GH3 hit flames and sparks, intro HUD entrance, and "You Rock!" at the end of the song.
- **Stage.** All ten GH3 venues. Four performers with original rigs, instruments, clips, hair physics, fret-hand fingers, drummer IK, and 100-note hand fire. Crowd impostors per row. Original cameras with GH3's shot selection and geometry. Lightshow snapshots, beams, housings, and node flags. GH3 particles, Tesla, movies, outlines, screen color. All 921 used materials draw with their translated GH3 shader templates. Pre-song intro and post-song win sequences.
- **Direction.** Native song PAK, `stage-events.json` versions 1 and 2, chart and MIDI direction tracks, the original song library for 159 songs, the Rock Band `VENUE` track (RB1/RB2 and RB3; camera cuts, lighting, pyro, fog), generated cues from chart sections for charts without direction, and singer lip sync from the chart's vocals (`PART VOCALS` notes and lyrics, or `.chart` lyrics) on songs without GH3 facial clips. Shared command dispatch for native and authored commands.
- **Packages.** The mod loads all stages, the band library, the character library, and the characters from package format v1 and 1.1 ([PACKAGE-FORMAT.md](PACKAGE-FORMAT.md)): `Mods/GH3Skin/Packages/`, `Custom/Stages`, `Custom/Characters`, and `venue.chstage` in a song folder. It imports each package one time into a per-machine cache with BC1/BC3 textures, and it decodes the song's clips on worker threads before the song ([PACKAGE-FORMAT.md section 20](PACKAGE-FORMAT.md#20-c-importer)). Unused cache folders are deleted at start. The older install (`Mods/GH3Skin/assets/stages`, `assets/libraries`) was removed in step 6.
- **Pickers.** In the menus, F6 opens the stage and band picker ([PLAYER-GUIDE.md](PLAYER-GUIDE.md)): a stage, **Random**, or **Song's choice**, with preview images; a character for each band role; the direction options (Rock Band `VENUE` tracks, generated direction, reduce flashing). The default stage is **Song's choice**: the song's stage, else Kaiju Megadome (`gh3.budokan`), else a random stage. A song can select its stage (`venue.chstage` in the song folder, `gh3_stage` in `song.ini`, or the GH3 career venue of a GH3 song); the player can turn this off. A character on a band library profile plays on the rig of its role; a GH3 guitarist character plays on its own GH3 rig. In another role, a character has the instrument of that role from the library band model.
- **Creator tools.** The Blender add-on CH Stage Tools (Blender 4.2 or later; tested on 4.2.23 and 5.2.1) makes, opens, validates, and exports stages (`.chstage`) and characters on the fixed GH3 rigs (`.chchar`). The ten GH3 venues and the four GH3 band members open and export again within the round-trip tolerances ([BLENDER-ADDON.md](BLENDER-ADDON.md)). A stage with a script bank keeps its own lights unless the bank has GH3 `LevelLight` objects (fixed 2026-10-09; before, such a stage was dark). [CREATOR-GUIDE.md](CREATOR-GUIDE.md) and the examples in `examples/` (a stage and a character, CC0-1.0) show the workflow.
- **Release build.** `scripts/build-release.sh` makes reproducible zips for Linux and Windows and the add-on zip ([VERIFICATION.md](VERIFICATION.md#release-build), [Release build](#release-build)).
- **GH3 characters.** All 72 playable GH3 characters ship as packages: Axel Steel, Casey Lynch, Izzy Sparks, Judy Nails, Johnny Napalm, Lars Umlaut, Midori, and Xavier Stone in two outfits with four styles each, Slash, Tom Morello, Lou and Thick Izzy, God of Rock, Grim Ripper, Bret Michaels, and the female singer ([PARITY-NOTES.md](PARITY-NOTES.md#playable-characters), [PIPELINE.md](PIPELINE.md#gh3-characters)). They keep their GH3 meshes, materials (all on translated GH3 templates, no fallback), textures, hair cards, and default GH3 guitar. Each guitarist plays on its own GH3 skeleton with its own animation set (stances, idles, strums, fret and finger sets, solos, intro, win and lose clips), ragdoll hair and accessories, IK, and scale (Judy Nails 0.9), from the character library `gh3-characters-v1` ([GH3 characters](#gh3-characters)). Each guitarist character plays the guitarist (with its guitar) and the bassist (with its own animation set and the library bass, as in GH3 co-op). Checked on gh3.dive, 38 runs, every variant in a close-up as guitarist or bassist, each character family in both roles, the two singers.

## GH3 characters

Since 0.9.9 each GH3 guitarist plays on its own GH3 rig profile of the character library `gh3-characters-v1` ([PACKAGE-FORMAT.md section 10.1](PACKAGE-FORMAT.md#101-character-library-format-11), [PARITY-NOTES.md](PARITY-NOTES.md#playable-characters)). Checks on 2026-10-09, gh3.dive, My Curse, game copy `~/games/clonehero-gh3-character-motion`:

| Check | Result |
| --- | --- |
| Clip data against GH3 (13 families as guitarist: Axel outfit 2 is the `axel` set; Casey, Izzy, Judy, Johnny, Lars, Midori, Xavier, Slash, Tom Morello, Lou, God of Rock, Grim Ripper) | The `[trace] band` lines of each run (74 song times from −6 s to 30 s, 4 bones) against the GH3 `.ska` clip sampled on the profile's GH3 skeleton at the same clip time: maximum 0.001 mm and 0.016°, all 12 runs (Axel's own run uses the library profile and is checked by the band library). The same for six outfit 2 profiles (Axel, Judy, Johnny, Izzy, Midori, Lou's Thick Izzy) and for Judy Nails as bassist with Lars Umlaut outfit 2 as guitarist (`tools/check-character-trace.py`). Each family plays its own clips: in these runs, the only body clip of Axel's set that another family plays is one `stance_b` clip of Lars Umlaut, which GH3's `LARS_animations` names. The God of Rock plays Axel's set, as in GH3 |
| Pictures | Each family at song times 4–28 s (full body; [evidence](#evidence-gh3-characters)): its own stance, idle, and strum; fret-hand fingers on the neck; the strum hand at the strings; hand fire after the 100-note streak; Judy Nails at 0.9 |
| Hair | Judy Nails: the hair swings with the head (video frames at 4 per second); all 20 profiles load their ragdoll (Grim Ripper: none, as on the GH3 PC port) |
| Bassist | Judy Nails as bassist with her own set and the library bass in her hands; Lars Umlaut outfit 2 as guitarist in the same run |
| Default band and Axel | Unchanged: the band library and Axel's and the singers' packages are byte-equal to `main`. Screenshots at song times 4–20 s against `main`: 32–47 dB PSNR, as two runs of the same build (32–42 dB) |
| Clip prefetch | 151–159 band clips for each song (157 with the default band), decoded in 29–59 ms on worker threads; no clip loads on the main thread; managed heap at the stage load 208–218 MB (`main`: 191–229 MB) |
| Not compared | The original GH3 with a non-Axel character (the menus, bot, and camera patches were not done); the clip data and the selection rules are checked against the GH3 files and scripts only |

### Evidence (GH3 characters)

`~/.cache/gh3-evidence/character-motion/` (frame strips, runs, logs, the trace check script `verify_trace.py`).

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
| Characters | The hair and accessory physics is the mod's spring solver with the GH3 joint limits (cone, hinge, prismatic), not Havok; it collides with the head capsule only. Axel Steel's first outfit keeps the library guitarist's hair (the 9 joints below the head; GH3's ragdoll has 6 more accessory joints on the chest). The two singers (Bret Michaels, the female singer) still play on the male singer's rig with his clips and hair. GH3 lets you unlock the bonus characters, outfits, and styles; the mod offers all of them |
| Lip sync | GH3 has no lip sync for songs without facial clips (the face stays still). The mod's lip sync on custom charts is its own addition, with mouth shapes measured from the GH3 facial clips. It reads syllable times, not the voice, so it does not show loudness or pitch. Off with `GH3_SKIN_LIPSYNC=off`; no picker option yet |
| Show flow | No song title text, no button ripple, the hand fire does not stop at the win, no crowd `Idle` at the win. Practice mode keeps Clone Hero's lead-in |
| Lightshow | GH3 changes occur about 0.15 s before the cue times in the recording. The additive `Flare` billboards write no depth (GH3's material flags let them write depth, but the mod's draw order then removed the hair behind a card) |

## Remaining work

### Runtime and packages

- Rock Band VENUE: tested with 16 real charts (RB1 DLC, RBN1, RBN2, and Onyx conversions of RB3 customs; [tested charts](CUSTOM-CONTENT.md#tested-charts)). All their VENUE events are known to the mapper. Post-processing, spotlights, sing-alongs, and the directed-cut pre-roll are not used; the mapping has choices by judgment ([CUSTOM-CONTENT.md](CUSTOM-CONTENT.md#rock-band-venue-track)). The RBN1 and RB1 charts were not compared with a Rock Band recording.
- GH3 characters: the two singers on their own rigs (`GH3_Singer_Female`, `GH3_Singer_BretMichaels`; `singer_female_animations`) need singer profiles in the character library and a rule for the song facial clips on other singer rigs. Instrument choice: GH3 has 88 guitars and 20 basses (`musician_instrument`); v1 has one library instrument for each role, and the GH3 characters carry their default guitar in their GLB. The Blender add-on makes characters on the four fixed profiles only (not on the character library profiles).
- Picker: mouse only (no guitar or keyboard navigation inside the window; the keyboard still moves the Clone Hero menu below it). No picker option for lip sync (`GH3_SKIN_LIPSYNC=off` only). Songs in `.sng` containers cannot select a stage (the mod reads `song.ini` and `venue.chstage` from song folders only).
- Characters: blend shapes are not animated. The v1 library has one instrument for each role, so `attachments.instrument` cannot select another instrument; a character's own instrument plays only in its own role.
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

- Blender add-on: no material preview node trees (GH3 shading in the viewport), and no GH3 script code editor ([BLENDER-ADDON.md section 1.14](BLENDER-ADDON.md#114-not-available-yet)).
- Characters on other rigs (retargeting, VRM) are not planned for v1 (PACKAGING-PLAN decision 2).
- Moonscraper: the editor fork has preservation fixes and an event browser. A typed command inspector and an original-song round trip in the editor UI are not complete. See [CUSTOM-CONTENT.md](CUSTOM-CONTENT.md#12-moonscraper).
- A general QB writer does not exist. The native PAK tool changes one camera note only.

## Release build

Records of the step 6 checks (2026-10-09). Nothing is published.

- Release zips 0.9.9 (2026-10-09, commit 62b66b4 of the character-motion branch, not tested from the zip): 1,673,743,956 bytes (Linux) and 1,673,747,309 bytes (Windows), 33.6 MB more than with the GH3 characters on format 1.0 (1,640,161,639 bytes): the character library `gh3-characters-v1.chlib`. The add-on zip is 235,558 bytes.
- Release zips: `scripts/build-release.sh` builds `gh3-skin-0.9.8-linux.zip` and `gh3-skin-0.9.8-windows.zip` (1,228.78 MB each), and `chstage_tools-0.1.0.zip` (219,576 bytes) from a clean export of one commit; two builds gave the same bytes. The tests used the zips of 8b45c86. Installed size: `Mods/GH3Skin` 1,228,837,531 bytes and `Gh3Skin.dll` 1,269,248 bytes; the cache after the first start is 1.42 GB on Linux and 1.43 GB on Windows (one more test character). Verified from the zips only, in game copies without `Mods/GH3Skin` and `UserData/GH3Skin`: Linux with `install-linux.sh` and its launcher, and Windows under Wine 11.17 + DXVK 3.1.1 with `install-windows.ps1` (PowerShell 7 for Linux). Cold start: all 12 packages import in 22.0 s (Linux) and 23.8 s (Wine); the stage waits 6.1 s (Linux) and 6.6 s (Wine) for the library and gh3.dive. Warm start: cache check 97–103 ms, stage load 1.7–1.8 s. On gh3.dive with My Curse, the mean RGB difference against the `main` install at song times 2–60 s is 0.3–3.3 on Linux (two `main` runs: 0.5–1.5) and 0.8–7.1 under Wine (two `main` runs: 1.0–5.5); no magenta pixels. `[trace]` values agree in 135/138 (Linux) and 269/274 (Wine) samples, against 136/139 and 271/274 between two `main` runs. The F6 picker shows the 11 stages on both. The MelonLoader download, SHA-256 check, and extraction of both install scripts were tested on folders with only the game's `globalgamemanagers` (the test games had MelonLoader 0.7.3 already). Not tested: real Windows, a first start of a game without MelonLoader assemblies, `install-windows.bat`.
- Windows (Wine 11.17 + DXVK 3.1.1 on Linux, **not real Windows**): a fresh zip install of Clone Hero 1.1.0.6142 with `install-windows.ps1` (MelonLoader 0.7.3, the mod, the shader bundles, the HUD folders, and the 12 packages; no older install) plays from packages only. Measured: cold import of all packages 24.3–26.6 s (the stage waits 7 s for the library and gh3.dive); warm scan and cache check 95–107 ms; scene to stage loaded 1.4–3.2 s (median 2.2 s, 14 starts); textures BC1/BC3 on Direct3D 11 (364 textures of gh3.dive uploaded in 157 ms); stage audio through `Clone Hero_Data\Plugins\x86_64\bass.dll`. The F6 picker, a stage change, a custom character (`.chchar` in a Unicode group folder), `gh3_stage` in a CRLF `song.ini`, a song `venue.chstage`, a synthetic Rock Band VENUE chart, and the cache clean-up work. Frames at the same song times as Linux: mean RGB difference per frame 0.4–3.1 on dive, 0.5–5.3 on the venue chart, 1.6–8.3 on prison, and 3.1–16.3 on hell (fire, flames, and bot timing); no magenta pixels. Two Linux runs (before and after this change) differ by 0.5–2.6. `[trace]` values (score, streak, multiplier, rock, power, notes, lanes, stage events, audio) agree in 118–120 of 121–122 samples at the same song times (two runs); the others are at a note hit or a sound start between the two sample times. Not tested: real Windows hardware and drivers, the typical installation (`Documents\Clone Hero`), paths longer than 260 characters, `install-windows.bat` and the script under Windows PowerShell 5.1.

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
