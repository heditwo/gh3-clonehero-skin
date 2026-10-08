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

1. `*_Pass1` decal layers (second diffuse UV set, mask, grime, logos): about 3% of venue geometry. Needs two more UV sets in the export.
2. The mural behind the Dive drummer is still missing; its source is not yet identified (not a `Pass1` decal in Dive).
3. `SkinShader`/`HairShader` specifics: normal maps, skin color ramp, rim tint, specular, and the projected shadow map (`s13`).
4. Normal and specular maps for `D_N_S_*` venue templates.
5. Shadow maps (`SetShadowDirFromLight`, `c178.w` already carries the key-light shadow amount).

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

Fixed from that comparison: back-face culling (GH3 material flag +220 marks two-sided materials), Axel's highway texture, venue posters from `global_ad_tex.pak`, housing lens flares, and the "Note Streak!" and "Star Power Ready" messages. Remaining visible gaps: GH3's shadow map, the camera-shot choice (GH3 picks shots at random), bloom around bright lights, and overall stage brightness in some snapshots.

#### Highway speed and crowd (0.9.7)

- **Highway speed.** `HighwayScroll` moves the board texture by `songTime × speed / 6` UV (`field_Private_Single_1`, 7 by default), which matches the notes for the stock neck height (4.617). The skin stretches the neck to 5.05, so the board ran about 9% faster than the notes. `KeepHighwayAtNoteSpeed` scales the scroll speed by the stock height over the skin height.
- **Crowd cards face the camera.** `Billboard_Fast` stores each card's pivot in the normal slot and offsets the corners along the view axes (`c120`–`c122`). The stage shader does the same when `_Gh3CamRight.w` is 1; `StageCrowd.Face` sets the camera axes every frame.
- **Crowd lighting.** GH3's crowd script parks ped *n* at `(-500, -200 - 100n, 0)` with `LightGroup = Crowd`, next to the venue's Crowd ambient and directional lights (both at x = -500). The peds now sample lights there instead of at the audience position.
- **Crowd card brightness.** The card pixel shader is `texture × c0 × vertex colour` with `c0 = 1`. The baked vertex colours go from black at the feet to about 0.66 at the head; the cards use them at double scale (0x80 = full). This matched GH3's crowd brightness in the comparison frames.

MelonLoader's assembly generator contacted `api.melonloader.com` on every launch, and the leftover HTTPS connection crashed the CLR later. The installed game now has `force_offline_generation = true` in `UserData/Loader.cfg`; the original file is backed up as `~/.cache/gh3-install-backup-0.8.115/Loader.cfg.bak`.


#### Score and note-streak digits

- **Fault.** In 0.9.7, each streak digit was a pre-made tile with its own size and position. The third digit used a different tile at four digits, so it sat lower than the other digits. The score used fixed-width crops of the font.
- **GH3 data.** `create_2d_hud_elements` in `scripts/guitar/guitar_hud_2d.qb` makes the score and the four streak digits as text elements. The score uses font `num_a9` at (222, 70), justification right/bottom, scale 1.1, and `font_spacing` 5. Streak digit *i* (1 to 4) uses font `num_a7` at (222 − 37*i*, 78), centered, z 25. Digit 1 has colour (15, 15, 70, 200); the other digits have (230, 230, 230, 200). `career_hud_2d_elements` puts the drum, body, and note icon in `HUD2D_note_container` at `counter_pos`. The note icon (z 26) covers the fourth digit.
- **Fix.** `prepare-fonts.py` exports `num_a9`, `num_a7`, and `text_a6` from `ZONES/global.pak.xen` as atlases with glyph tables. `HudText` draws all digits of a line with one top line and one pitch. `ComboHud` uses GH3's container layout and the original drum, body, and icon textures. When a digit changes, only the highest changed digit rolls. The lower digits change immediately, as in GH3 frames at 099→100 and 999→1000.
- **Calibrated by eye.** The streak glyphs are at one texel for each 1080p pixel. Six-digit scores are narrowed to 176 GH3 units. The roll timing is unchanged from `counter-tuning.json`.
- **Check.** At 1110 notes, the digit centres are within 0.4 px of GH3 (1600×900). Five-digit score glyph runs are within 1 px.

### Installation state

- Version `0.9.7` is installed in `~/games/clonehero-gh3-melon` with all ten re-exported venues, the regenerated band library, and the rebuilt `render/stage.bundle` (the light-data texture needs it). DLL SHA-256: `5c88b784a552f9c85ce4464f52e932584caabf20ffef72051d4a330ea56bf7a0`.
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
