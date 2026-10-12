# Asset pipeline and regeneration

The Python package `pipeline/` makes all generated assets from the owner's GH3 PC files. Git keeps only the HUD and gem textures and the venue previews in `assets/` ([assets/README.md](../assets/README.md) lists the folders). The shared band library, the stage packs, and the song direction are local outputs. Git ignores them.

## Inputs

| Input | Default location | Used by |
| --- | --- | --- |
| GH3 PC `DATA` folder | `~/games/gh3-test-tools/game/DATA` | All GH3 steps (`GH3_DATA` in `scripts/regenerate.sh`) |
| Isolated Clone Hero copy | `~/games/clonehero-gh3-melon` | Shader bundles: they use the Unity `2022.3.62f2` format of this copy (`GH3_CLONE_HERO_DIR`) |
| Windows Clone Hero copy (same version, the official `CloneHero-Win-x64.zip` unpacked) | `~/games/clonehero-win/game` | Shader bundles: the Direct3D 11 program format (`Clone Hero_Data/Resources/unity_builtin_extra`, `GH3_CLONE_HERO_WINDOWS_DIR`). On the development machine, use `~/games/clonehero-win-spike/game` |
| Extracted GH3 HUD art | `~/games/gh3-skin-assets` (`global-gfx-png/`, `hud-png/`, `hud-runtime-dds/`) | HUD textures, Star Power highway |
| BetterGH3 package | `~/games/BetterGH3-FULLPACKAGE_v1.0.2.zip` | Tap gems |
| FastGH3 package | `~/games/gh3-test-tools/FastGH3_1.1.zip` | Cyan Star Power phrase gems |
| GH3 reference recording | `gh3-long-star-power-1600x900.mp4` (`GH3_REFERENCE_VIDEO`) | Ready banner, active Star Power highway |
| Rail reference frames | `pipeline/hud/references/gh3-fast-*.png` (in Git) | Highway rail overlays |

You also need `uv`, Python 3.12, the .NET 10 SDK (for `tools/Gh3SongTool`, which the library, venue, and event steps build), FFmpeg (for the Video Shoot movies and the reference recording), and Wine (for the `render` step, see [Shader tools](#shader-tools)).

## Regenerate everything

```bash
MISE_QUIET=1 scripts/regenerate.sh --clean
GH3_REFERENCE_VIDEO=/path/to/gh3-long-star-power-1600x900.mp4 MISE_QUIET=1 scripts/regenerate.sh hud-video
MISE_QUIET=1 bash install.sh
```

`--clean` deletes `assets/libraries/gh3-band-v1`, `assets/libraries/gh3-characters-v1`, `assets/characters`, `assets/instruments`, `assets/stages`, and the generated packs in `assets/stage-packs` before the build. Use it for a clean build. Without it, files from earlier exports can stay in the output folders.

The script runs the steps one after the other. On a machine with little memory (for example 16 GB), do not run other large jobs at the same time. The venue export is the largest step (about 6 minutes, more than 1 GB of memory). When other agents or jobs can run, put the command under a shared lock:

```bash
flock /tmp/opencode/gh3-heavy.lock scripts/regenerate.sh --clean
```

## Steps and order

You can give one or more step names to `scripts/regenerate.sh`. Keep this order:

```mermaid
flowchart LR
    library --> venues --> render
    library --> render
    library --> template
    library --> characters
    characters --> render
    characters --> packages
    instruments --> render
    instruments --> packages
    venues --> packages
    events --> packages
    template --> packages
    hud
    hudvideo[hud-video]
```

| Step | Command | Output |
| --- | --- | --- |
| `library` | `python -m pipeline.library --data DATA` | `assets/libraries/gh3-band-v1/`: band, direction clips, hair, hand fire, crowd, stage audio, global scripts, Video Shoot movies, Tesla noise, `catalog.json`, `library.json`. `--hair-only` rebuilds only `band/hair.json` (and `library.json`) |
| `characters` | `python -m pipeline.character_library --data DATA`, then `python -m pipeline.characters --data DATA` | `assets/libraries/gh3-characters-v1/` (`characters.json`, `clips/*.bin.gz`), and `assets/characters/<id>/` (`meshes.bin`, `stage.json`, `textures/`, `character.json`) for the 72 playable GH3 characters, and `assets/characters/inventory.json` ([GH3 characters](#gh3-characters)) |
| `instruments` | `python -m pipeline.instruments --data DATA` | `assets/instruments/<id>/` (`meshes.bin`, `stage.json`, `textures/`, `instrument.json`) for the 88 guitars and 20 basses of GH3, `assets/instruments/highways/<character>.png` (the 13 GH3 highways), and `inventory.json` ([GH3 instruments](#gh3-instruments)) |
| `venues` | `python -m pipeline.venues --data DATA` | `assets/stage-packs/gh3.*` (editable source packs) and `assets/stages/gh3.*` (compiled packs), `assets/stages/index.json` |
| `events` | `python3 -m pipeline.events DATA --library --output assets/stages/events` | Original song direction for all songs, `index.json`, `export-report.json` |
| `render` | `python -m pipeline.render.shaders --game GAME --windows-game WINDOWS_GAME` and `python -m pipeline.render.particle_materials` | `render/*.bundle`, `render/stage-templates.json`, `render/particle-materials.json` in the library |
| `hud` | `python -m pipeline.hud.textures`, `pipeline.hud.hit_effects`, `pipeline.hud.fonts` | `assets/gems/`, `sustains/`, `highway/` (with `beat-lines/`), `hud/` (`clear.png`, `rock-meter/`, `score/`, `counter/`, `messages/`, `you-rock/`), `fonts/`, `hit-effects/` |
| `hud-video` | `python -m pipeline.hud.star_power --video V`, `pipeline.hud.ready_animation --video V` | `assets/highway/star-power/active-highway-*.png`, `assets/hud/ready/` (`ready-*.png`, `ready-animation.json`) |
| `template` | `python -m pipeline.stagepack.template`, then `python -m pipeline.stagepack.install assets/stage-packs/example.studio` | The `example.studio` custom stage |
| `packages` | `python -m pipeline.package.build`, then `python -m pipeline.package.validate` | `assets/packages/*.chstage`, `gh3-band-v1.chlib`, `gh3-characters-v1.chlib`, `gh3.*.chchar`, and `gh3.*.chinst` (package format v1 and 1.1), the same packages as folders in `assets/packages/unpacked/`, `build-report.json` |

Each command uses `uv run --with ...` for its Python packages. See `scripts/regenerate.sh` for the exact package list of each step.

**The `render` step must come after `venues`, `library`, `characters`, and `instruments`.** The stage shader contains only the GH3 material templates that the compiled venues, the band, the crowd, the GH3 characters, and the GH3 instruments use (the instruments add `EnvMask_D_N_S_2ChkBox_Pass1`) (the characters add five templates: `SkinShader_Pass1_Pass2`, `SkinShader_Pass2`, `SkinShaderEnvMap`, `SkinShader_VertexColor_Pass3`, `SkinShaderEnvMap_VertexColor`). If you run it first, the shader has too few templates and the materials use the approximate fallback model.

The `venues` step compiles each pack against the library catalog. Thus run `library` first.

The `packages` step reads the source packs, the library folder, and the song direction. Thus run it last. Its output (`assets/packages/`) is what `install.sh` and the release build install. The step encodes the GLB images to pre-encoded GH3TEX02 textures (BC1/BC3 with mipmaps, [PACKAGE-FORMAT.md section 7](PACKAGE-FORMAT.md#7-textures)), so the players do not encode them. The source packs and the runtime folders keep PNG. The clip encoder (q48 + Brotli 11) keeps a cache in `assets/packages/.clip-cache/`, and the texture encoder in `assets/packages/.texture-cache/` (about 0.7 GB), so a second run takes less time (about 50 s with full caches; about 2 min with an empty texture cache and `--jobs 6`). Use `--jobs N` to set the number of encoder processes (default 2, about 0.1 GB each).

## GH3 characters

`python -m pipeline.characters` reads the character tables of the GH3 QB files (`Musician_Profiles`, `musician_body`, `musician_instrument`, and the store lists; [PARITY-NOTES.md](PARITY-NOTES.md#playable-characters)) and exports every playable character in every outfit and style: 64 variants of the eight main characters, Slash, Tom Morello, Lou and Thick Izzy, God of Rock, Grim Ripper, and the two other singers (72 in total). `--only <id>` exports one character. It takes about 80 s and 0.3 GB of memory; the output is about 0.4 GB.

Each character keeps the GH3 data as the library band does (`pipeline/library/band.py`): the meshes of its model PAK and of the default instrument of its GH3 profile (guitarists; the female singer keeps her microphone), the materials with their GH3 templates and constants, the textures, and the combined hair cards.

**Guitarists** keep their GH3 skeleton and its original inverse bind matrices (no mesh move). Their rig profile is a profile of the character library `gh3-characters-v1` ([PACKAGE-FORMAT.md section 10.1](PACKAGE-FORMAT.md#101-character-library)): `gh3-guitarist-<name>-v1`, and `gh3-guitarist-<name>-outfit2-v1` for the second outfit when GH3 gives it another skeleton or ragdoll (`skeleton2`, `ragdoll2`). Axel Steel's first outfit uses the library profile `gh3-guitarist-axel-v1` (the same skeleton), so his packages and the library guitarist do not change.

**The two singers** also keep their GH3 skeleton (no mesh move; before 2026-10-11 the export moved their mesh to `GH3_Singer_Male`, which made the female singer's neck about 45 % too long). Their rig profiles are `gh3-singer-female-v1` (`GH3_Singer_Female`) and `gh3-singer-bret-v1` (`GH3_Singer_BretMichaels`) in `gh3-characters-v1`. The female singer keeps her microphone (`Mic_AT_ATM610_Fem`, on the stand bones of her skeleton); Bret Michaels uses the library microphone.

Axel Steel (outfit 1, style 1) on his own rig gives the same body and hair parts as the library guitarist (byte-equal; the instrument differs). The `packages` step writes `assets/packages/<id>.chchar` (one package for each outfit and style, license and permissions from `pipeline/package/decisions.py`; `requires` `gh3-characters-v1` for the characters on a character library profile), and the validator checks them against the profile rigs (`--library`, `--character-library`).

### Character library

`python -m pipeline.character_library` (the first command of the `characters` step) writes `assets/libraries/gh3-characters-v1/` from the GH3 QB tables and PAKs. It takes about 60 s and 0.3 GB of memory.

| Part | GH3 source | Result |
| --- | --- | --- |
| Profiles | `Musician_Profiles`: `Skeleton`/`skeleton2`, `ragdoll`/`ragdoll2`, `ik_params`, `scale`; `create_band_member` uses the outfit 2 values for outfit 2 | 22 profiles: the eight main characters with two outfits each (15 profiles; Axel's first outfit is the band library profile), Slash, Tom Morello, Lou (both outfits use one skeleton and ragdoll), God of Rock (Axel's skeleton with the accessory bones of `GH3_Guitarist_RockGod`, which his ragdoll and mesh fit; [PARITY-NOTES.md](PARITY-NOTES.md#god-of-rock-hair)), Grim Ripper, and the singers `gh3-singer-female-v1` and `gh3-singer-bret-v1` (the vocalist profiles `Singer_Female` and `Singer_Bret`) |
| Animation sets | `anim_set` (`<name>_animations` in `guitar_animation_data_<name>.qb`), `strum_anims`, `finger_anims`, `fret_anims`; clips from the anim PAK of the first outfit (`anims_pak`; all outfits and styles of a character have the same clips), the model PAK (intro and win clips), `perm_anims` (hand clips), and Judy's walk clips (`normal_walk_data`) | 14 sets: `axel` (also God of Rock), `casey`, `izzy`, `judy`, `johnny`, `lars`, `midori`, `xavier`, `slash`, `morello`, `lou`, and `grim-ripper` (Morello's clips with the `Normal` strums of its profile); `singer-male` (`singer_male_animations`, Bret Michaels; 78 clips, byte-equal to the band library singer clips) and `singer-female` (`singer_female_animations`, 92 clips) without hand layers. 2,165 clips, each stored one time |
| Clips | `.ska` | Sampled at 30 frames per second on the clip rig `GH3_Guitarist_Axel` (as the band library clips; `pipeline.library.direction.write_clip`); the singer sets on the clip rig `singer` of `clipRigs` (`GH3_Singer_Male`, the rig of the band library singer and of its song facial clips). The `axel` set clips are byte-equal to the band library guitarist clips, except the four walk clips: the band library moves them to Axel's rig, the character library keeps them as GH3 plays them |
| Hair | `ragdoll`/`ragdoll2` (Havok 4.6), `guitarist_accessory_bones` | All 15 accessory joints of each ragdoll, on any body bone, with `hkRagdollConstraintData`, `hkLimitedHingeConstraintData` (hinge), and `hkPrismaticConstraintData` (prismatic) joints, and box or capsule bodies (`pipeline.library.hair.ragdoll_member`, `all_chains`). The export stops when a ragdoll body is more than 0.25 m or 45° from its skeleton bone (a ragdoll for another skeleton). The Grim Ripper has no hair: on the PC port `create_guitarist` does not call `ragdoll_setaccessorybones` for him (`IsWinPort`). Each `hkRagdollConstraintData` joint also gets its Havok limit frames (`twistAxis`, `coneAxis`, `planeAxis`, in the bind frame of the joint) and each joint the `maxAngularVelocity` of its body (also in `band/hair.json` of the band library) |
| IK | `Hero_Ik_params`, `Satan_Ik_params`, `singer_IK_Params` (`guitar_anim_tree.qb`) | Lou uses `Satan_Ik_params` (the legs end at the toes); the singers `singer_IK_Params` (two leg chains; `vocalist_static_tree` uses it for every vocalist) |

The pipeline output keeps the GH3CLP01 clips (`clips/<clip>.bin.gz`); the `packages` step encodes them as q48 + Brotli in `gh3-characters-v1.chlib`. `install.sh` and `scripts/build-release.sh` ship `assets/packages/gh3.*.chchar` in `Mods/GH3Skin/Packages/`.

Sizes (2026-10-09): 72 packages, 411,351,870 bytes (2.1–8.3 MB, mean 5.7 MB); with the guitarists on their own rigs 411,307,452 bytes, and the character library `gh3-characters-v1.chlib` 33,614,373 bytes (35,247,296 bytes as a folder: 1,995 clips and `characters.json`, 1.1 MB). Its cache folder is 34,964,581 bytes; its cold import takes 0.13–0.15 s. Cold import of the band library and the 72 characters with `tools/PackageImport`: 18.2–18.4 s with all characters on the fixed profiles, 18.6–19.3 s with the character library and the guitarists on its profiles; cache 1,362,197,700 against 1,397,163,753 bytes. Release zips (`scripts/build-release.sh`, commit 35d862f) 1,640,161,639 bytes (Linux) and 1,640,164,985 bytes (Windows), against 1,228.78 MB before. Installed `Mods/GH3Skin` 1,640,198,608 bytes (was 1,228,837,531). Cache after the first start 1.92 GB (the characters 0.50 GB). Cold import of all packages 45.1 s (was 22.0 s); the stage still waits only for the library, the venue, and the selected characters. With the singer profiles (2026-10-11): `gh3-characters-v1.chlib` 37,825,717 bytes (2,165 clips; folder 39,698,352 bytes), `gh3.singer-female.chchar` 1,771,386 bytes, `gh3.singer-bret.chchar` 821,692 bytes.

## GH3 instruments

`python -m pipeline.instruments` reads `musician_instrument` and the store lists of the GH3 QB files ([PARITY-NOTES.md](PARITY-NOTES.md#instruments-and-highways)) and exports every guitar and bass (`Type` `guitar` or `bass`; 108). `--only <id>` exports one instrument. It takes about 17 s and 0.2 GB of memory; the output is about 96 MB.

- Each instrument keeps its GH3 mesh parts, materials (templates and constants), and textures, as the characters do (`pipeline.characters.load_models`). The export stops when a vertex follows another bone than `bone_guitar_body`.
- IDs: `gh3.<desc_name without Instrument_ and Guitar_>`, for example `gh3.es335-red`, `gh3.demonik`, `gh3.tobias-b4`. Names: brand, model, series, and finish, for example "Gibson Les Paul Standard (Ebony)"; GH3's own designs read "Obliviaxe: Lou's". The manifest description has the GH3 store text and how GH3 offers the instrument.
- The `packages` step binds each instrument to the rig of `gh3-guitarist-axel-v1` (guitars) or `gh3-bassist-v1` (basses) and writes `gh3.<name>.chinst`.
- Highways: the export also writes `<name>_1_Highway` of the model PAK of each GH3 guitarist profile (13 images, 512 × 1024) to `highways/`. Axel's highway is byte-equal to `assets/highway/highway.png`. The `packages` step adds the highway of the profile to each guitarist character (`character.json` `highway`). All outfits and styles of a profile have the same highway (checked: 70 of 70).

## Shader tools

The `render` step makes an OpenGL Core program and a Direct3D 11 program for each shader (see [ARCHITECTURE.md](ARCHITECTURE.md#shader-programs-for-each-graphics-api)). `pipeline/render/toolchain.py` gets the Direct3D 11 tools. They need no system package and no root access:

| Tool | Source | Use |
| --- | --- | --- |
| glslang 16.6.0 | Khronos GitHub release `glslang-16.6.0-linux-x86_64-release.tar.gz` | GLSL to SPIR-V |
| SPIRV-Cross 2021-01-15 | Khronos GitHub release `spirv-cross-gcc-trusty-64bit-9acb9ec31f.tar.gz` (the last release with Linux binaries) | SPIR-V to HLSL |
| FXC and `d3dcompiler_47.dll` (Microsoft) | NuGet package `Microsoft.Windows.SDK.CPP` 10.0.26100.7705: `c/bin/10.0.26100.0/x64/fxc.exe` and `c/Redist/D3D/x64/d3dcompiler_47.dll`. Only these two files download (HTTP range requests, about 2 MB) | HLSL to DXBC, under Wine |
| Wine | `wine` on `PATH`, or `GH3_WINE=/path/to/wine` | Runs FXC on Linux |

- The first run downloads the files to `~/.cache/gh3-skin/shader-tools` (`GH3_SHADER_TOOLS` sets another folder). Each file has a fixed SHA-256 value.
- The Windows SDK package requires acceptance of its license (`https://aka.ms/WinSDKLicenseURL`). Read it, then set `GH3_ACCEPT_WINDOWS_SDK_LICENSE=1` for the first run. Without it, the step stops with a message.
- `GH3_GLSLANG` and `GH3_SPIRV_CROSS` select other binaries.
- The tools use their own Wine prefix in the cache folder. The step does not use a GPU. It takes about 45 s for the five bundles; the stage shader is the largest part.
- The output is deterministic: two runs give the same bundle bytes.

```bash
GH3_WINE=~/games/gh3-test-tools/wine-root/usr/bin/wine GH3_ACCEPT_WINDOWS_SDK_LICENSE=1 \
  GH3_CLONE_HERO_WINDOWS_DIR=~/games/clonehero-win-spike/game flock /tmp/opencode/gh3-heavy.lock scripts/regenerate.sh render
```

## Partial updates

| Change | Run |
| --- | --- |
| A file in `src/Stage/Shaders/` or `pipeline/gh3/shader.py` | `render` |
| Band or crowd mesh conversion only | `uv run --with pillow --with numpy --with 'shapely>=2.1' python -m pipeline.library --models-only`, then `render` |
| One venue | `python -m pipeline.venues --venue z_dive`, then `render` |
| One song, for a song folder | `python3 -m pipeline.events DATA/SONGS/mycurse_song.pak.xen --output "<song folder>/stage-events.json"` |
| A custom stage pack | `python -m pipeline.stagepack.install <source pack folder>` |
| Venue preview images (`assets/previews/<id>.png`) | `GH3_CLONE_HERO_DIR=<game copy> scripts/capture-venue-previews.sh [id ...]` (one game run for each venue, under the record lock), then `python -m pipeline.package.build --no-library` |
| Test characters from the library members | `python -m pipeline.package.character --library assets/packages/gh3-band-v1.chlib --output DIR [--role ROLE ...]` writes `gh3.<role>.chchar` (body and hair, no instrument). Do not write them to `assets/packages`: `install.sh` installs `assets/packages/gh3.*.chchar` |
| GH3 characters | `python -m pipeline.character_library`, `python -m pipeline.characters [--only ID ...]`, then `render` (new templates only), then `python -m pipeline.package.build --no-venues --no-library` |
| GH3 instruments or highways | `python -m pipeline.instruments [--only ID ...]`, then `render` (new templates only), then `python -m pipeline.package.build --no-venues --no-library --no-character-library` (the characters carry the highways) |

After a change, run `install.sh`. The installer copies the skin folders (`gems`, `sustains`, `highway`, `hud`, `fonts`, `hit-effects`) to `Mods/GH3Skin/assets/` with the same layout, the packages (`assets/packages/*.chstage`, `gh3-band-v1.chlib`, `gh3-characters-v1.chlib`, the GH3 characters `gh3.*.chchar`, and the GH3 instruments `gh3.*.chinst`, zip form, with their file times) to `Mods/GH3Skin/Packages/`, and the shader bundles (`assets/libraries/gh3-band-v1/render/*.bundle`, `stage-templates.json`) to `Mods/GH3Skin/render/`. It first removes the skin folders and the files of earlier installs (the flat files in `Mods/GH3Skin/assets/`, `Mods/GH3Skin/counter-tuning.json`, and the older install in `Mods/GH3Skin/assets/stages` and `assets/libraries`), so that no old file stays. The mod loads stages only from packages. A changed package gets a new cache entry in `UserData/GH3Skin/Cache/` at the next start.

`assets/stages` (the compiled venues and `stages/events`) and `assets/libraries` (the compiled library and the shader bundles) are intermediate outputs: the `packages` step (`pipeline.package.build`) reads them, and the tests (`GH3_PACK_ASSETS`, `GH3_PACKAGE_ASSETS`) and `tools/audit-materials.py` check them. The installer does not copy them, except the shader bundles.

To make the release zips, see [VERIFICATION.md](VERIFICATION.md#release-build).

## Check the result

```bash
python3 tools/audit-materials.py
```

The audit lists how each used material draws. The expected result is `2031 template` (921 for the band, crowd, and ten venues, and 1,110 for the 72 GH3 characters) and no `FALLBACK` lines.

The exporters are deterministic. The package files are reproducible: two runs of the `packages` step give byte-equal `.chstage` and `.chlib` files (sorted zip entries, fixed times and attributes, DEFLATE level 6 of the same zlib, and the deterministic texture encoder).

The round-trip check converts each package back to the compiled runtime files and compares them with `assets/stages` and `assets/libraries/gh3-band-v1`:

```bash
flock /tmp/opencode/gh3-heavy.lock env MISE_QUIET=1 uv run --with pillow --with numpy --with 'shapely>=2.1' --with brotli \
  python -m pipeline.package.roundtrip --json /tmp/opencode/roundtrip.json
```

Expected result: every compiled venue file is byte-equal, except `pack.json` (equal without its source hashes, because the package GLBs hold GH3TEX02 images) and the textures (the package texture is the encoder output of the compiled PNG); the library JSON, sounds, movies, and rigs are byte-equal or JSON-equal, and its textures are the encoder output of the library PNG files; 159 V coordinates of the guitarist and drummer meshes differ by at most 3e-8 (glTF stores 1 − v); all 1,240 clips are within the q48 error bound.

The four library band members can be written as character packages (`gh3.<role>.chchar`, the round-trip reference of the Blender character tools, [BLENDER-ADDON.md section 2.11](BLENDER-ADDON.md#211-characters)):

```bash
MISE_QUIET=1 uv run --with numpy --with brotli python -m pipeline.package.character --library assets/packages/gh3-band-v1.chlib --output /tmp/opencode/characters
```

The C# importer must give the same cache content as the Python pipeline. Import all packages outside the game, then compare (about 20 s and 0.5 GB for the import; 1.5 GB of disk):

```bash
dotnet run --project tools/PackageImport -c Release -- assets/packages <cache folder>
flock /tmp/opencode/gh3-heavy.lock env MISE_QUIET=1 uv run --with numpy --with pillow --with brotli \
  python -m pipeline.package.cachecheck <cache folder> --json /tmp/opencode/cachecheck.json
```

Expected result: every package `equal` (character caches are skipped; `preview.png` is a copy of the manifest preview); textures BC1/BC3 with a minimum first-level PSNR above 36 dB, and the RGBA32 textures pixel-equal (`rgbaExact`). With `--rgba` (a GPU without BC1/BC3) the importer decodes the BC1/BC3 textures, so only the RGBA32 textures are pixel-equal. cachecheck does not know the character library and the instruments: give it a folder with links to the venue and band library cache folders only. Put the cache folder on a disk, not on `/tmp` (a RAM disk here).

The `hud` step makes `assets/highway/beat-lines/beatline*.png` files that are different from the files in Git (also with the code before the cleanup). Do not commit the new files without a visual check. To keep the files in Git, run `git checkout -- assets/highway/beat-lines`.

## Package layout

| Module | Contents |
| --- | --- |
| `pipeline/gh3/` | Readers for GH3 PC data: PAK and QB (`formats`), the `DATA` folder with source hashes, QB script files, and QB scalars (`data`), camera banks and performance commands (`direction`), scenes and textures (`scene`), skeletons and clips (`band_formats`), Direct3D 9 shader bytecode (`d3d9`), the material library and the template classes (`materials`), shader translation (`shader`), Havok ragdolls (`havok`), hair cards (`hair_mesh`), alpha cut-outs (`mesh_cutout`), typed scripts (`script_bank`, `venue_scripts`). Each reader names its format constants at the top of the module |
| `pipeline/stagepack/` | The public stage-pack format: GLB IO (`gltf`), the compiler (`pack`), skinned actors (`actor`), the `install` and `template` commands |
| `pipeline/package/` | Package format v1 ([PACKAGE-FORMAT.md](PACKAGE-FORMAT.md)): converter (`build`), owner choices of the converter (`decisions`), round-trip check (`roundtrip`), runtime file readers and writers (`runtime`: GH3BND02, GH3ANM01, GH3CLP01, GH3VID01, model folders), validator (`validate` with `validate_stage`, `validate_character`, `validate_library`; package files and report in `source`, shared checks in `checks`, library profiles and IDs in `profiles`), schema evaluator for `schemas/` (`schema`), GLB split, join, and reader (`glb`), GH3TEX02 textures (`texture`), q48 clip codec (`q48`), rotation conversions (`rotations`), dry conversion check of the current outputs (`sources`), comparison of the C# importer cache with the Python output (`cachecheck`). The Blender add-on bundles part of it (`blender/build.py`) |
| `pipeline/library/` | The shared band library command and its steps; `models` reads the GH3 model PAKs for the band, the characters, and the instruments |
| `pipeline/characters.py` | The playable GH3 characters: inventory from the QB tables, rig profile of each variant, and export (each character on its own GH3 skeleton) |
| `pipeline/character_library.py` | The character library `gh3-characters-v1`: GH3 guitarist profiles, animation sets, hair, IK |
| `pipeline/venues/` | The venue exporter, lighting export, and transforms |
| `pipeline/render/` | Shader bundles and particle material fields |
| `pipeline/hud/` | HUD, gem, highway, hit-effect, and font textures |
| `pipeline/events.py` | Song direction export |
| `pipeline/songtool.py` | Builds `tools/Gh3SongTool`: the C# song PAK reader, with the QB and script code of the mod |
| `pipeline/paths.py` | Default locations |
