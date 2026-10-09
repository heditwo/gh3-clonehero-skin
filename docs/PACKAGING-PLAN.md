# Packaging and creator workflow plan

Status: **step 3 done: format v1 frozen ([PACKAGE-FORMAT.md](PACKAGE-FORMAT.md)), and the pipeline writes the ten venues and the library as packages (part 2). Step 4 part 1 (the C# importer and loader, [PACKAGE-FORMAT.md section 20](PACKAGE-FORMAT.md#20-c-importer)) done; the pickers are next. Step 5 parts 1 to 4 (Blender add-on foundation, venue MVP, the full venue tool set, and the character tools on the fixed GH3 rigs, [BLENDER-ADDON.md](BLENDER-ADDON.md)) done.** This document records the research and the decisions for the release format of this mod. Start a new work session from this file. The spike results are in [section 9](#9-spike-results).

## 1. Goal

Prepare the mod for a public release to the Clone Hero community:

- Players install the mod one time, then add venues and characters by dropping one file into a folder.
- Creators make venues and characters with ordinary tools (Blender) and export one file.
- The original GH3 venues, crowd, and band animations become ordinary packages in the same format.
- The current custom runtime files (`GH3STG02`, `GH3ANM01`, `GH3CLP01`, `pack.json` and so on) stay only as an internal cache.

## 2. Decisions

| # | Topic | Decision |
|---|---|---|
| 1 | GH3 content | The release **includes** the original GH3 venues, crowd, band, and animations, converted to the new format. Players do not need a GH3 copy. (The owner accepts the IP risk. Keep the project non-commercial and keep GH or Activision trademarks out of the product name.) |
| 2 | Characters | **Fixed GH3 skeletons only** for the first release (the GHWT:DE model: fit the mesh to the GH3 rig). Retargeting (VRM, arbitrary rigs) is a later improvement, only if the fixed rigs are not sufficient. |
| 3 | File types | `.chstage` for venues and `.chchar` for characters. The shared GH3 band, crowd, and animation library is also a package. |
| 4 | HUD art | The HUD is GH3 art. Two small gem sets are not from GH3: tap gems (from BetterGH3) and the cyan overlapping Star Power phrase gems (from FastGH3). Credit those projects, ask their authors, or draw replacements. This is a small cleanup item, not a blocker. |
| 5 | Release | Clean up the repository, then the owner posts it with a showcase video and a Discord link. No mod manager, gallery site, or index is needed for the first release. |
| 6 | Chosen pitch | **Pitch A, growing into Pitch C** (section 5). Not Unity AssetBundles. |

## 3. Research summary

Three research passes were made in October 2026 (rhythm-game ecosystem, asset formats and runtime loading, creator tooling). The main results follow. Sources are linked where they matter.

### 3.1 Clone Hero

- Clone Hero 1.1.0.6142 uses Unity **2022.3.62f2**, IL2CPP, and the built-in render pipeline (no URP). The AssetBundle, Animation, ParticleSystem, and Video modules are present.
- Official custom content is only 2D: `Documents/Clone Hero/Custom/<type>/` for image and video backgrounds, highways (512×1024 PNG/JPG), video highways (VP8 `.webm`), color profiles (INI), and game icons ([CH wiki](https://wiki.clonehero.net/books/clone-hero-manual/page/other-custom-content)). Group subfolders are allowed. **There is no 3D venue, band, crowd, camera, or lighting support.** This mod is the first 3D venue system for Clone Hero.
- Songs are distributed as `.sng` single-file containers or zips through Chorus Encore ([enchor.us](https://www.enchor.us/), [SNG spec](https://github.com/mdsitton/SngFileFormat)).
- v1.x mods use MelonLoader. Players know "drop a file in a Custom folder", not mod managers.
- Untested: whether Clone Hero accepts unknown subfolders such as `Custom/Stages/`.

### 3.2 Other games

- **YARG:** venues are `.yarground` Unity AssetBundles built from the full YARG Unity project (Unity 6000.3.5f2, URP). Characters are `.yargchar` bundles with a VRM 1.0 root (`Vrm10Instance`); v0.15 added custom VRM vocalists. YARG.Core parses the Rock Band `VENUE` track, not GH camera or lightshow notes. **YARG files cannot load in Clone Hero** (different Unity version, different render pipeline, YARG scripts). Copy the concepts, not the files.
- **Rock Band customs:** the `VENUE` MIDI track has camera and lighting cues (RB1/2 notes; RB3 text events) ([spec](https://github.com/TheNathannator/GuitarGame_ChartFormats/blob/main/docs/Implementation-Specific/Rock-Band/MIDI-Tracks/Venue.md)). Magma generates cues when the track is empty. Onyx and Nautilus convert venue data. Many converted charts carry a `VENUE` track that Clone Hero ignores.
- **GH modding (GHWT: Definitive Edition):** mods are folders in `DATA/MODS` with an INI file. Venues and characters are made in Blender with **NXTools** (formerly GHTools, [gitgud.io/fretworks/nxtools](https://gitgud.io/fretworks/nxtools), Blender 4.0+, active). NXTools imports GH3 scenes and has panels for camera frames, light snapshots and moods with live preview, pyro, waypoints, lightmaps, crowd, and a one-click "Export Venue Mod". Characters use fixed-skeleton template `.blend` files with weight transfer. GH chart toolkits write `CAMERAS` (GH3 notes 77–117) and `LIGHTSHOW` (mood notes 70–76) tracks, which match GH3 exactly.
- **Beat Saber, Trombone Champ, Synth Riders:** AssetBundles from a Unity template pinned to the game's Unity version. Good player experience (one file, one folder, a gallery). Known pain: a game update (Beat Saber 1.29.4) broke the shaders of all existing models; shaders must be built for each graphics API.
- **VRM 1.0:** glTF plus a standard humanoid map (55 bones, 15 required, fingers included), expressions, spring bones, and license metadata. The VRM Add-on for Blender is mature. It is the ecosystem to use when retargeting is added later.
- **osu!:** `.osk` zip, double-click or drag into the game. This is the target player experience.

### 3.3 What carries across games

- **Blender + glTF** as the creator pipeline. GH creators already use Blender and GH3 vocabulary (light groups Band / None / Crowd / Periph, camera prefixes, snapshots, moods).
- **Chart cue tracks** (Rock Band `VENUE`, GH `CAMERAS` and `LIGHTSHOW`), so existing charts direct venues.
- **VRM** as the humanoid standard (later).
- **One file, one folder, a picker in the game, fixed names instead of custom code, graceful fallback** when something is missing.

### 3.4 Runtime facts found in this Clone Hero build

| Fact | Consequence |
|---|---|
| `Unity.Collections` and `Unity.Mathematics` are absent; only Burst proxies exist | glTFast and UnityGLTF cannot run. Write a small C# GLB reader (estimate 1,500–2,500 lines). |
| `AvatarBuilder.BuildHumanAvatar` and `HumanDescription` are stripped; `AnimationClip.SetCurve` is stripped | No Mecanim humanoid at runtime. Retargeting (later) must be our own code. Band animation already runs in our own code (`BandPose`), so it fits. |
| `Texture2D.LoadImage` exists but decodes PNG on the main thread | Decode on a worker thread, compress to BC7/BC3 with mipmaps, cache, then upload with `LoadRawTextureData`. |
| Blend-shape and skinning setters exist as proxies, but other Mesh methods failed to unstrip | Test them before depending on them. |
| Band clips load lazily and synchronously during play (gzip decode on the main thread; the singer library is 389 MB gzipped) | Can hitch mid-song today. Prefetch the song's clips on a worker thread before play. |
| Injected MonoBehaviours in AssetBundles lose serialized fields under Il2CppInterop | AssetBundles cannot carry behaviour settings. One more reason not to use them. |
| The stage shader bundle may be OpenGL-only | Cross-platform risk (Windows D3D11) for any option. Check it. |

### 3.5 Format comparison

| Option | Verdict |
|---|---|
| **glTF 2.0 (GLB) + JSON sidecars in a zip, converted once to a runtime cache** | **Chosen.** Blender exports it natively; open; close to the existing pipeline (the source packs already use GLB with `extras.gh3*` keys). |
| glTF read directly at runtime, no cache | Slower (main-thread PNG decode). Not chosen. |
| glTFast or UnityGLTF ported | Blocked by missing packages, jobs, Burst, and shaders. |
| VRM 1.0 | Use later for retargeted characters. |
| Unity AssetBundle | Rejected: exact Unity version, per-platform shaders, breaks on Clone Hero upgrades, no custom scripts under IL2CPP. |
| FBX | Authoring input only (through Blender). Never loaded by the game. |
| Current custom binaries | Keep as the internal cache only. |

## 4. Target design

### 4.1 Packages

- A package is a **zip** with `manifest.json`. Use STORE for GLB and images, DEFLATE for JSON. An unpacked folder is also accepted for development.
- `manifest.json`: `format` (for example `chstage/1`), `id`, `version` (semver), `name`, `authors`, license and permissions, `requires` (for example the band library ID), `entry` files, SHA-256 per file, optional `minModVersion`, and `preview.png`.
- **Venue (`.chstage`):** `stage.glb` (geometry, materials, node extras) plus `cameras.json`, `lighting.json`, `scripts.json`, and placements. This is the existing source-pack contract (`docs/STAGE-PACKS.md`, `docs/CUSTOM-CONTENT.md`) in a zip.
- **Character (`.chchar`):** a GLB skinned to one of the fixed GH3 profiles (for example `gh3-guitarist-axel-v1`), plus `character.json` (profile, hair materials, attachments).
- **Library:** the GH3 band, crowd, clips, audio, movies, and particle textures as one package that venue and character packages `require`.
- GH3-specific data goes in JSON sidecars that refer to GLB nodes by name. Per-node and per-material tags stay in `extras.gh3*` (Blender writes custom properties as extras). Standard extensions where they fit: `KHR_materials_unlit`, `KHR_materials_emissive_strength`, `KHR_texture_transform`, `KHR_lights_punctual`, meshopt compression (not Draco). Lightmaps stay `occlusionTexture` on `TEXCOORD_1`.
- Publish the manifest and sidecar rules as JSON Schema so the add-on and the mod validate the same way.

### 4.2 Runtime

- Folders: `Custom/Stages/` and `Custom/Characters/` (group subfolders allowed). Fall back to a mod folder if Clone Hero rejects unknown `Custom` subfolders.
- On first use, a C# importer (worker thread) validates the package and writes a cache to `UserData/<mod>/Cache/<sha256>-<importerVersion>/`: meshes, BCn textures with mipmaps, quantized clips, JSON. A new importer version invalidates the cache.
- The loader reads the cache and uploads Unity objects on the main thread, spread over frames.
- Clip prefetch before the song starts (removes the mid-song decode).
- In-game picker: a Stage entry and a character picker per band role, with preview images. A song can suggest a stage (for example a `song.ini` tag or a `venue.chstage` file in the song folder); the player can turn this off.
- Chart cues: read the GH `CAMERAS` and `LIGHTSHOW` tracks and the Rock Band `VENUE` track (map RB camera subjects and lighting moods to GH3 camera groups and moods); generate cues when a chart has none.

### 4.3 Blender add-on (Blender 4.2+ extension)

- **Venues:**
  - "New stage from template" and "Open stage package" (rebuilds panels from GLB extras and the JSON sidecars).
  - GH3 material panel: lit / ambient / unlit, opaque / cutout / blend / additive, lightmap on UV2, two-sided, color scale, crowd atlas, movie slot, flipbook. The panel writes extras and a preview node tree.
  - Markers: band placement with proxy bodies, waypoints, effect emitters, crowd cards and roles, visibility groups (node flags), script objects, actors.
  - Cameras: Blender cameras with shot type, control, groups, anchor and look-at, screen offset; a shot browser and preview.
  - Lighting: light groups, snapshots per mood with capture and apply, fixtures with beam gizmos, live preview.
  - One **Validate** button (clickable errors) and one **Export** button (writes the `.chstage`, optionally into the game folder).
  - Bundle the package validator (`pipeline/package`, numpy only) so the add-on and the converter share one source of truth (done: `blender/build.py` copies it into the extension).
- **Characters (done, step 5 part 4):** GH3 rig templates, "transfer weights from GH3 body", hair-bone helpers, attachment previews (instrument, sticks, microphone), bind-pose and weight validation, and clip preview in Blender (convert clips to Actions) so creators can check deformation.
- The Blender glTF exporter does not export custom properties on Actions and cannot carry GH3 light attenuation, hotspot/field, or negative intensity. Therefore the add-on writes `cameras.json` and `lighting.json` itself and uses the GLB only for geometry, materials, and node extras.

### 4.4 Converter

- The existing Python export (`python -m pipeline.venues`, `python -m pipeline.library`, see [PIPELINE.md](PIPELINE.md)) writes the GH3 venues and the library as packages in the new format. They ship with the release.
- Later (Pitch C): a standalone packer and validator (CLI and small GUI) so creators who use other tools can pack a folder of GLB + JSON.

## 5. Pitches considered

- **A. Blender-first open package (chosen core).** Add-on, `.chstage` zip, runtime importer and cache, picker.
- **B. Unity template + AssetBundles.** Rejected (section 3.5).
- **C. A + standalone packer + in-game dev mode (folder watch, reload, shot and mood cycler, light-group overlay) + VRM retargeting later.** Target end state; add after A works.

## 6. Work order

1. **Done.** The parity work is merged and all venues are exported again (`main` @ `2f7b438`).
2. **Done** (results in [section 9](#9-spike-results)). **Spike** (short, measure and decide):
   - Compressed texture formats (BC7 / BC3) on Clone Hero Linux GL and Windows; `LoadImage` against worker decode + `LoadRawTextureData` for a 2048² texture.
   - Clip size and decode speed with quantized rotations (estimate: 1 GB library to 150–300 MB) and error against GH3 frames.
   - Load a skinned GH3 character from a GLB in Clone Hero; test `bindposes`, `SetBoneWeights` overloads, blend shapes.
   - Blender round trip of a GH3 rig GLB: do bone local frames and inverse bind matrices survive within tolerance?
   - Stage shader bundle on D3D11 (Windows).
   - Whether Clone Hero accepts `Custom/Stages/`.
3. **Freeze the package format v1** (manifest, schemas, file layout). Convert the ten GH3 venues and the library to packages.
   - **Part 1, done:** [PACKAGE-FORMAT.md](PACKAGE-FORMAT.md) defines the zip layout, `manifest.json`, the three kinds (`.chstage`, `.chchar`, `.chlib`), the venue, character, and library contracts, the q48 clip encoding, the texture, GLB, and extras rules, versioning, the cache key, and the folders. JSON Schemas are in `schemas/`. `python -m pipeline.package.validate` checks a zip or a development folder; `tests/test_package_format.py` tests it. Brotli works in the game (q48 + Brotli stays; section 18 of the format document).
   - **Dry check:** `python -m pipeline.package.sources` maps all ten venue source packs and `example.studio` to the venue layout. All sidecars, extras, actors, and (after part 2) texture references are valid.
   - **Part 2, done** ([PACKAGE-FORMAT.md section 19](PACKAGE-FORMAT.md#19-converter)): `python -m pipeline.package.build` (the `packages` step of `scripts/regenerate.sh`) writes the ten venues and `example.studio` as `.chstage` and the library as `gh3-band-v1.chlib`, as zip and as folder, reproducibly. The GLB writer names the images and writes `#<name>` texture references. The library JSON files have schemas. All packages are valid (0 errors). `python -m pipeline.package.roundtrip` converts the packages back to the current runtime files: the venues are byte-equal, the library is byte-equal or JSON-equal, and the clips are within the q48 bound. Sizes: venues 551 MB, library 658 MB (zip). The owner choices O1–O5 have defaults in `pipeline/package/decisions.py`. Open: `preview.png` for each venue (needs captures).
4. **Runtime:** C# GLB importer, cache, loader, clip prefetch, stage and character pickers, chart cue tracks.
   - **Part 1, done** ([PACKAGE-FORMAT.md section 20](PACKAGE-FORMAT.md#20-c-importer)): the mod finds packages in `Mods/GH3Skin/Packages/` and in `Custom/Stages` and `Custom/Characters` of each Clone Hero content folder, skips bad packages with one log line each, imports one package at a time on a background thread into `UserData/GH3Skin/Cache/<sha256>-i1/` (SHA-256 of each file checked; BC1/BC3 textures with mips from a managed encoder; q48 clips kept), and loads the venue and the library from the cache. Clip prefetch decodes the song's clips on worker threads at stage load. The C# cache equals the Python output (`pipeline.package.cachecheck`). The older install (`assets/stages`, `assets/libraries`) stays as a fallback. Measured: the cold import of all 12 packages takes 23.5 s (the stage waits 7 s for the library and gh3.dive); a warm stage load is 2.2 s against 3.5 s on `main`.
   - **Part 2, next:** stage and character pickers in the menu, character loading (`.chchar`), chart cue tracks, cache clean-up, and the removal of the older install.
5. **Blender add-on:** venue MVP first (geometry, materials, band markers, wide and role cameras, lights with moods, validate, export, open package), then the full venue tool set, then characters on fixed rigs.
   - **Part 1, done:** the foundation ([BLENDER-ADDON.md](BLENDER-ADDON.md)). CH Stage Tools is a Blender extension (minimum 4.2, tested on 4.2.23 and 5.2.1). It has property groups and panels for the manifest, materials, markers, cameras, and lighting, "New stage from template", and "Validate" with clickable issues. It bundles `pipeline/package` and the schemas at build time (`blender/build.py`). `blender/test.sh` runs a headless test in Blender.
   - **Part 2, done:** the venue MVP ([BLENDER-ADDON.md](BLENDER-ADDON.md)). **Export .chstage** writes a reproducible zip (and optionally the development folder and a copy into `Custom/Stages`) with `stage.glb`, the sidecars, the manifest hashes, a rendered `preview.png`, and lightmaps as the glTF occlusion texture on `TEXCOORD_1`. It runs the bundled validator and writes only a valid package. **Open .chstage** rebuilds the scene and all panels from a package. Round trip of the ten GH3 venues and `example.studio` on Blender 4.2.23 and 5.2.1 (open, export without edits, compare): all sidecars JSON-equal, all GLB checks within the tolerances, validator 0 errors and 0 warnings, the same bytes on a second export ([BLENDER-ADDON.md section 2.6](BLENDER-ADDON.md#26-round-trip-of-the-gh3-venues)). Camera bone anchors: the add-on keeps the anchor and the local offset, and Blender shows the camera relative to the performer root.
   - **Part 3, done:** the full venue tool set ([BLENDER-ADDON.md](BLENDER-ADDON.md) sections 1.6–1.10). All package files are in the `.blend` file (binary files packed), so export does not need the source package. Actors are Blender armatures and meshes with their clips (the clips that `actors.json` uses at Open, all clips on request; scene rate = clip rate); the panel edits the placement and all `actors.json` fields; an unchanged actor model exports as the packed GLB (byte-equal), a changed model through the actor rules (unit scale channels dropped, missing clips merged by joint name, names and images restored). All 29 GH3 actor models exported from Blender compile with `compile_actor` (clip error 3.3e-6 m and 0.00041°, skinned vertices 3e-6 m). A read-only script bank browser (objects, events, values; jump to object; simple typed values can change). A shot browser (group and type filters, preview) and GH3 proxy rigs from `gh3-band-v1.chlib` for camera bone anchors (the warning only without a library). A live lighting preview of snapshots and moods (EEVEE Material Preview, light linking by light group), beam overlay with a range gizmo, light-group colors. Lists of visibility groups, node flags, and light groups with show and hide. The round trip of the ten GH3 venues and `example.studio` stays exact on Blender 4.2.23 and 5.2.1 (60 headless checks on each version).
   - **Part 4, done:** characters on the fixed GH3 rigs ([BLENDER-ADDON.md](BLENDER-ADDON.md) sections 1.15 and 2.11). **New Character from GH3 Rig** loads the profile rig from `gh3-band-v1.chlib` with the GH3 body as a reference and the attachment (guitar, bass, microphone, drum kit) as helpers that export never writes. **Transfer Weights from the GH3 Body** (nearest face, interpolated; then 4 influences and sum 1), **Limit and Normalize**, **Assign to Hair Bones** (the hair chains of `band/hair.json`), and hair materials. **Validate** runs add-on checks with clickable objects and the bundled validator with the library (bind pose against the profile). **Clip Preview** converts library q48 clips to actions at 30 fps with the game arm and leg IK (a pure-Python Brotli decoder serves Blender builds without `brotli`). **Export .chchar** (zip, development folder, Custom/Characters; reproducible, validator-gated) and **Open .chchar**. The four GH3 band members as `.chchar` round-trip within the 9.6 tolerances (largest: 0.001 mm, 0.00054°, inverse bind 9.9e-6) with 0 validator errors on Blender 4.2.23 and 5.2.1. A test character made from Blender primitives validates, exports, and deforms with GH3 clips (EEVEE renders). The package format did not change. Open: the library has no separate instrument models for `attachments.instrument` (BLENDER-ADDON.md section 2.10).
   - **Next:** material preview node trees (GH3 shading in the viewport).
6. **Release cleanup:** repository and documentation cleanup, install instructions, a short creator guide with an example stage, credits for BetterGH3 and FastGH3 gem art (or replacements). The owner records the showcase video and posts the release.

## 7. Effort estimates (from the tooling research, one experienced developer)

| Component | Weeks |
|---|---:|
| Add-on scaffold, property groups, panels, manifest | 1–1.5 |
| Stage export (JSON, glTF export, validation, zip) | 2 |
| Material panel with preview | 2 |
| Markers (band, waypoints, emitters, crowd, visibility, scripts) | 1.5–2 |
| Cameras (properties, groups, preview) | 3 |
| Lighting (groups, snapshots and moods, fixtures, preview) | 3 |
| Open a package in Blender | 2–3 |
| Runtime (folder and zip discovery, picker, cache, versioning, reload) | 3–4 |
| Documentation, sample `.blend`, tutorial | 2 |
| Character runtime loader and selection | 3–4 |
| Character tools on fixed rigs | 4–6 |

A thin venue MVP is about 8–10 weeks of that estimate. Agent-driven work will be faster, but the order and dependencies stay the same.

## 8. Risks

- Blender API changes: set a minimum of 4.2 and test new versions.
- The Blender viewport cannot match GH3 shading exactly; the game is the reference, so fast reload matters.
- Fixed rigs restrict characters to GH3 proportions (accepted for v1).
- Shader portability to Windows (D3D11): the spike shows that the current bundles fail on D3D11 (section 9.5).
- IP: the release ships GH3-derived content by decision; keep it non-commercial and unbranded.

## 9. Spike results

The spike ran on 2026-10-09. Each item has a report in [docs/spike/](spike/) with the method, the numbers, and the evidence. The probe code is off by default (`GH3_SKIN_SPIKE=<name>`).

### 9.1 Textures ([report](spike/textures.md))

- Clone Hero Linux uses OpenGL Core 4.5. BC1, BC3, BC5, and BC7 upload with full mip chains and draw correctly. Check `SupportsTextureFormat` before upload (Unity's BC5 value is 27, not 32).
- Main-thread time for one 2048² texture with mipmaps (median): `LoadImage` 80 ms; worker PNG decode then RGBA32 upload 30–38 ms; BC7 or BC3 from a cache file 5–13 ms. `SetPixelData` is stripped, so the worker must make the mip chain.
- Encode time for one 2048² texture: BC1 or BC3 0.2–0.3 s in managed C#; BC7 8.5 s in managed C#, 1.5 s with a native encoder.
- **Decision:** packages ship PNG. The importer encodes BC1 (opaque) or BC3 (alpha) into the cache. BC7 needs a native encoder per platform; it is not in v1 unless the owner asks.

### 9.2 Custom folders ([report](spike/textures.md))

- Clone Hero ignores `Custom/Stages/` and `Custom/Characters/`, with group subfolders, zips, and loose images. The log shows no new errors, and the background and highway lists do not change.
- **Decision:** use these folders. Find the Custom folder through `GlobalVariables.uniqueCustomContentPaths`, not a fixed path (portable mode and extra content folders).

### 9.3 Skinned characters from GLB ([report](spike/skinned.md))

- A 142-line C# GLB reader (`src/Spike/GlbFile.cs`) loads the gh3.dive guitarist. All vertex channels are bit-identical to `GH3BND02`. After skinning, the maximum vertex difference is 0.7 µm.
- Main-thread time: mesh 11–24 ms; the 28 embedded PNGs 155 ms (the cache in 9.1 removes this).
- Working APIs: `bindposes`, `InternalSetBoneWeights`, blend shapes, `BakeMesh`. Absent: `mesh.boneWeights`, all `SetBoneWeights` overloads. The `mesh.triangles` getter fails; use `GetTrianglesImpl`.
- **Decision:** `.chchar` holds one skinned GLB. Joints map to GH3 bones by name. Inverse bind matrices are required. Four weights per vertex. Blend shapes are optional. The importer converts to the `GH3BND02` cache, and `StageAssets` draws it as today.

### 9.4 Animation clips ([report](spike/clips.md))

| Encoding | Size (1,138 clips) | Added error, max | Decode, one clip p50 / max |
|---|---:|---|---|
| Current (float32, gzip) | 455.6 MB | – | 7.5 / 64 ms |
| **q48 + Brotli 11** | **121.1 MB** | 0.007°, fingertip 0.03 cm | 1.6 / 13 ms |
| q32 + Brotli 11 | 91 MB | fingertip 0.9 cm (too large) | – |

- q48: 48-bit rotations, 16-bit positions per clip range, 30 fps, constant tracks stored once.
- One song's clip set decodes in 0.24 s on one worker thread (today: about 1.3 s on the main thread at stage setup).
- The library goes from about 1,000 MB to about 666 MB, not 150–300 MB. Movies (357 MB) and audio (108 MB) are the remainder.
- **Decision:** q48 + Brotli for v1. Brotli was tested on the game's .NET 6 runtime but not inside the game. Check it in the game before the format freeze; gzip 9 is the fallback (+10 MB).
- **In-game check (step 3):** `BrotliStream` decodes the 356 guitarist clips on a worker thread inside Clone Hero Linux, byte-equal to gzip, 134–176 ms for all clips (gzip 9: 107–138 ms). Brotli stays. See [PACKAGE-FORMAT.md section 18](PACKAGE-FORMAT.md#18-in-game-check-of-brotli).

### 9.5 Windows D3D11 ([report](spike/d3d11.md))

- All five shader bundles have OpenGL Core programs only. On Windows Clone Hero under Wine and DXVK, D3D11 (the default) draws the stage pink. `-force-glcore` draws it correctly.
- `Shader.isSupported` returns true on D3D11, so the mod does not detect the failure.
- The stage shader compiles from GLSL to DXBC on Linux (glslang, SPIRV-Cross, Wine `d3dcompiler_47`) in under 2 s. A D3D11 bundle was not built. Estimate: 3–5 days.
- Other Windows blockers (fixed): Il2CppInterop method names are different in the Windows build; `CloneHeroBindings` now binds methods by their obfuscated metadata name, which is the same on both platforms. `StageAudioBackend` finds BASS in `Clone Hero_Data/Plugins/x86_64`.
- **Owner decision:** all platforms. macOS is later (see [spike/macos.md](spike/macos.md)).
- Done after the spike: all five bundles have Direct3D 11 programs made with Microsoft FXC and `d3dcompiler_47.dll` from the Windows SDK NuGet package (see [PIPELINE.md](PIPELINE.md#shader-tools)). One bundle file serves OpenGL Core and Direct3D 11.

### 9.6 Blender round trip ([report](spike/blender.md))

- With the default bone mode (`BLENDER`), the GH3 guitarist, singer, and a Prison actor survive import, export, and `.blend` save: joint error 0.0007 mm and 0.0004°, GH3 clip error 0.0007 cm.
- `TEMPERANCE` and `FORTUNE` bone modes, flattened hierarchy, Z-up, and `merge_vertices` break the rig.
- `export_extras` and `export_attributes` are off by default. Without them, all `gh3*` extras and `_GH3_*` streams are lost. Extras that hold glTF texture indices are wrong even with extras on, because Blender renumbers textures.
- Blender samples clips at the scene rate (24 fps after import). Set the scene rate to the clip rate before import.
- `compile_actor` rejected every Blender output: Blender writes unit scale channels (fixed later).
- **Decisions:**
  - Characters: map by joint name; check each joint against the fixed GH3 profile (position 0.1 mm, rotation 0.01°, scale within 1e-4 of 1) and the IBMs against the joints (1e-4).
  - Accept and drop scale channels within 1e-4 of 1 (fix `compile_actor`).
  - Refer to textures in extras by image name or package path, not by glTF index.
  - The add-on sets the export options (extras and attributes on, Y-up, rest position, no flattening) and the import options (`BLENDER` bones, no vertex merge).
  - Blender 4.2.23 gives the same results as 5.2.1 when the add-on fixes a 4.2 importer bug (custom vertex attributes in a random order). The add-on minimum is 4.2 ([BLENDER-ADDON.md section 2.8](BLENDER-ADDON.md#28-blender-42-result)).
  - `compile_actor` now drops unit scale channels (fixed with the add-on foundation).
