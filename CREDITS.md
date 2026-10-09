# Credits

This file lists the work of other people that this project uses, and the place where each item is used. It gives facts only. It is not legal advice.

## Not affiliated

This project is a non-commercial fan project. It is not affiliated with, endorsed by, or sponsored by Activision, Neversoft, RedOctane, Harmonix, or the Clone Hero developers. Guitar Hero is a trademark of its owner. Clone Hero is a game by its own developers; the mod changes it at run time and does not include Clone Hero files.

## Guitar Hero III content

The HUD art, the gems (except the two sets below), the highway board, the fonts, the venues, the band, the crowd, the animations, the audio, and the movies come from Guitar Hero III: Legends of Rock for PC (Neversoft, RedOctane, Activision, 2007). The pipeline (`pipeline/`) reads them from the owner's copy of the game (`docs/PIPELINE.md`). The packages that the pipeline makes from this content have the license `LicenseRef-GH3-Derived` in their manifests (`docs/PACKAGING-PLAN.md` decision 8).

The GH3 `DATA` folder on the development machine is the BetterGH3 full package v1.0.2 (see below): its `global.pak.xen` and `global.pab.xen` are byte-equal to the files in `BetterGH3-FULLPACKAGE_v1.0.2.zip`. The project did not compare these files with an unchanged retail copy.

## Gem and highway art from community projects

| Project | Version and file | What the project uses | Where |
| --- | --- | --- | --- |
| BetterGH3 by lunalawl ([release trailer](https://www.youtube.com/watch?v=RWRoxHwAM9E)) | `BetterGH3-FULLPACKAGE_v1.0.2.zip`, `DATA/ZONES/_DONOTDELETE.pak.xen` and `.pab.xen`, texture bundle entry 2 | The tap gems: `tap2d_<lane>` (five lanes), `tap2d_<lane>_star` (16-frame Star Power phrase sheets), and `tap2d_starpower` (tap gem during active Star Power). 11 images | `pipeline/hud/textures.py` writes `assets/gems/<lane>/<lane>-tap.png`, `<lane>-star-tap-<frame>.png`, and `assets/gems/active/active-tap.png` |
| FastGH3 by donnaken15 ([github.com/donnaken15/FastGH3](https://github.com/donnaken15/FastGH3)) | `FastGH3_1.1.zip`, `DATA/ZONES/global.pak.xen` and `.pab.xen` | The cyan Star Power phrase gems for active Star Power: `star2d_star`, `star2d_star_hammer`, and `star2d_star_tap` (16-frame sheets) | `pipeline/hud/textures.py` writes `assets/gems/active/active-star-<frame>.png`, `active-star-glow-<frame>.png`, and `active-star-tap-<frame>.png` |
| FastGH3 | Two 1600 × 900 screen captures of FastGH3: `pipeline/hud/references/gh3-fast-six-bulbs-1600x900.png` and `gh3-fast-autoplay-gameplay-1600x900.png` (in Git) | The highway rail overlays (normal and blue), cut from the captures and moved onto the GH3 rail lines (`pipeline/hud/highway_overlay.py`) | `assets/highway/highway-edges-normal.png`, `highway-edges-blue.png` |

`docs/PACKAGING-PLAN.md` decision 4 records the choice: credit these projects, ask their authors, or draw replacements.

## Software that the mod uses at run time (not in this repository)

| Software | License | Use |
| --- | --- | --- |
| [MelonLoader](https://github.com/LavaGang/MelonLoader) 0.7.3 | Apache-2.0 | Loads the mod into Clone Hero. `install-linux.sh` and `install-windows.ps1` download the official release from GitHub and check its SHA-256 value. The release zips do not contain it. The mod builds against its assemblies (`MelonLoader.dll`, Il2CppInterop) and the Il2Cpp proxy assemblies that it makes from the game. None of these files are in this repository |
| [Il2CppInterop](https://github.com/BepInEx/Il2CppInterop) (part of MelonLoader) | LGPL-3.0 | The bridge between the mod and the Il2Cpp game code |
| [Dobby](https://github.com/jmpews/Dobby) (part of MelonLoader) | Apache-2.0 | MelonLoader's hook library. The mod has no Dobby code. `src/HookPageGuard.cs` moves Dobby's hook pages on Linux to stop a crash (`docs/STATUS.md`, Stability) |
| BASS (part of Clone Hero) | Proprietary (un4seen) | The stage audio plays through Clone Hero's own `libbass.so` or `bass.dll` |

## Software in this repository

| Item | Source | Notes |
| --- | --- | --- |
| Brotli decoder in Python (`blender/chstage_tools/brotli_decode.py`) | Written for this project from [RFC 7932](https://www.rfc-editor.org/rfc/rfc7932) | `brotli_dictionary.bin` is the static dictionary of RFC 7932 (Appendix A), zlib-compressed. The tests compare the decoder with the Python `brotli` module |
| BC1 and BC3 texture encoder (`src/Packages`) | Written for this project | No third-party code |
| GLB reader and writer, q48 clip codec, package validator | Written for this project | `pipeline/package`, `src/Packages` |

## Tools that only the build and the tests use (not shipped)

| Tool | License | Use |
| --- | --- | --- |
| [glslang](https://github.com/KhronosGroup/glslang) 16.6.0 | BSD-3-Clause and others (see the project) | Shader build: GLSL to SPIR-V. `pipeline/render/toolchain.py` downloads the release binary and checks its SHA-256 value |
| [SPIRV-Cross](https://github.com/KhronosGroup/SPIRV-Cross) 2021-01-15 | Apache-2.0 | Shader build: SPIR-V to HLSL |
| Microsoft FXC (`fxc.exe`) and `d3dcompiler_47.dll` from the Windows SDK NuGet package `Microsoft.Windows.SDK.CPP` 10.0.26100.7705 | [Windows SDK license](https://aka.ms/WinSDKLicenseURL) | Shader build: HLSL to Direct3D 11 bytecode, under Wine. The download starts only with `GH3_ACCEPT_WINDOWS_SDK_LICENSE=1` (`docs/PACKAGING-PLAN.md` decision 8). The tools stay in a user cache folder. The install and the release folder contain only the compiled shader bundles, not these tools |
| [Blender](https://www.blender.org/) and its glTF 2.0 add-on | GPL-2.0-or-later (Blender), Apache-2.0 (glTF add-on) | The CH Stage Tools add-on uses Blender's glTF importer and exporter. Blender is not in this repository |
| Python packages: [NumPy](https://numpy.org/), [Pillow](https://python-pillow.org/), [Shapely](https://shapely.readthedocs.io/), [jsonschema](https://github.com/python-jsonschema/jsonschema), [Brotli](https://github.com/google/brotli) | BSD-3-Clause, MIT-CMU, BSD-3-Clause, MIT, MIT | The pipeline and the tests (`uv run --with ...`). The add-on bundles none of them; Blender includes NumPy |
| [FFmpeg](https://ffmpeg.org/) | LGPL-2.1-or-later or GPL | The pipeline: Video Shoot movie frames and the reference recording |
| [Wine](https://www.winehq.org/) | LGPL-2.1-or-later | Runs FXC and the Windows tests on Linux |
| .NET SDK and runtime | MIT | Builds the mod, the tests, and the tools |
| Research tools: `ZstdSharp.Port` (`tools/ClipBench`), `Mono.Cecil` (`tools/MoonscraperAudit`), `BCnEncoder.Net` (`tools/research/spike-textures/csharp-encoder`) | MIT | NuGet packages of local measurement tools. They are not part of the mod |

## Example content

The example stage and character in `examples/` are made by this project with Blender primitives and generated textures (`examples/build_examples.py`). Their manifests give the license `CC0-1.0`. The example character uses the GH3 guitarist rig (bone names and bind pose), because package format v1 requires the fixed GH3 rigs (`docs/PACKAGING-PLAN.md` decision 2).

## Licenses of this repository

- The CH Stage Tools add-on (`blender/chstage_tools/`) is GPL-3.0-or-later (`blender/chstage_tools/LICENSE`), as its manifest says. Blender add-ons that use the `bpy` API must have a GPL-compatible license.
- The other parts of the repository are MIT ([LICENSE](LICENSE)): use them as you want, and keep the copyright notice. MIT does not cover the GH3 content and the content made from it, or the BetterGH3 and FastGH3 art.
