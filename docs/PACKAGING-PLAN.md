# Packaging and creator workflow plan

Status: **decided, not started.** This document records the research and the decisions for the release format of this mod. Start a new work session from this file.

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
  - Bundle the existing Python validator (`stage_pack.py`, `stage_gltf.py`) so the add-on and the converter share one source of truth. Replace or bundle `shapely` and Pillow as wheels.
- **Characters:** GH3 rig templates, "transfer weights from GH3 body", hair-bone helpers, attachment previews (instrument, sticks, microphone), bind-pose and weight validation, and clip preview in Blender (convert clips to Actions) so creators can check deformation.
- The Blender glTF exporter does not export custom properties on Actions and cannot carry GH3 light attenuation, hotspot/field, or negative intensity. Therefore the add-on writes `cameras.json` and `lighting.json` itself and uses the GLB only for geometry, materials, and node extras.

### 4.4 Converter

- The existing Python export (`export-gh3-stages.py`, `prepare-*.py`) writes the GH3 venues and the library as packages in the new format. They ship with the release.
- Later (Pitch C): a standalone packer and validator (CLI and small GUI) so creators who use other tools can pack a folder of GLB + JSON.

## 5. Pitches considered

- **A. Blender-first open package (chosen core).** Add-on, `.chstage` zip, runtime importer and cache, picker.
- **B. Unity template + AssetBundles.** Rejected (section 3.5).
- **C. A + standalone packer + in-game dev mode (folder watch, reload, shot and mood cycler, light-group overlay) + VRM retargeting later.** Target end state; add after A works.

## 6. Work order

1. **Wait for the running parity work to merge** (`fix-venues`, `fix-nodeflags`, `fix-fingers`), then re-export all venues one time. The export pipeline is still changing.
2. **Spike** (short, measure and decide):
   - Compressed texture formats (BC7 / BC3) on Clone Hero Linux GL and Windows; `LoadImage` against worker decode + `LoadRawTextureData` for a 2048² texture.
   - Clip size and decode speed with quantized rotations (estimate: 1 GB library to 150–300 MB) and error against GH3 frames.
   - Load a skinned GH3 character from a GLB in Clone Hero; test `bindposes`, `SetBoneWeights` overloads, blend shapes.
   - Blender round trip of a GH3 rig GLB: do bone local frames and inverse bind matrices survive within tolerance?
   - Stage shader bundle on D3D11 (Windows).
   - Whether Clone Hero accepts `Custom/Stages/`.
3. **Freeze the package format v1** (manifest, schemas, file layout). Convert the ten GH3 venues and the library to packages.
4. **Runtime:** C# GLB importer, cache, loader, clip prefetch, stage and character pickers, chart cue tracks.
5. **Blender add-on:** venue MVP first (geometry, materials, band markers, wide and role cameras, lights with moods, validate, export, open package), then the full venue tool set, then characters on fixed rigs.
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
- Shader portability to Windows (D3D11) is not verified.
- IP: the release ships GH3-derived content by decision; keep it non-commercial and unbranded.
