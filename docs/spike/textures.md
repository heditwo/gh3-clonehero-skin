# Spike: textures and the Custom folder

Spike agent: `textures`. Branch: `spike-textures`. Plan items: `docs/PACKAGING-PLAN.md` section 6, step 2, items 1 and 6.

## Questions

- **A.** Which compressed formats (BC1, BC3, BC5, BC7) does Clone Hero Linux (OpenGL core) support?
- **B.** How long does it take to load one 2048 × 2048 RGBA texture with mipmaps? Compare `LoadImage`, worker PNG decode, and pre-compressed BCn. How long does it take to encode BC7 and BC3?
- **C.** Does Clone Hero accept unknown folders such as `Custom/Stages/` and `Custom/Characters/`?

## Method

- Probe: `src/Spike/TextureSpike.cs` and `src/Spike/PngDecoder.cs` (removed in step 6; `tools/research/spike-textures/run-texture-spike.sh` needs them). The probe runs only when `GH3_SKIN_SPIKE=textures`. It starts on gameplay frame 30, runs all measurements in one frame, logs with the prefix `[spike-textures]`, and then shows the textures on a UI overlay.
- Test image: `assets/stages/gh3.budokan/textures/ab079663.png` (2048 × 2048, 8-bit RGBA, 2.31 MB PNG, alpha is 255 everywhere). This texture came from GH3, where it was DXT-compressed.
- Offline encoding: `tools/research/spike-textures/encode_textures.py` (Python, `etcpak` 0.9.15) writes a full mip chain (12 levels, box filter) as raw BCn blocks in a small `.gh3tex` file (24-byte header). `tools/research/spike-textures/csharp-encoder/` measures the managed encoder BCnEncoder.NET 2.2.1 on .NET 10.
- Game runs: `tools/research/spike-textures/run-texture-spike.sh` under `flock /tmp/opencode/gh3-record.lock`, game copy `~/games/clonehero-gh3-spike-textures`, stage `gh3.dive`, chart `mycurse-native`, 1600 × 900. Run 2 is the normal run. Run 3 adds the test files to `Custom/` (question C).
- Timing: `Stopwatch`. Each case has 1 warm-up run and 7 measured runs. "Main" stops when the Unity call returns. "Main + sync" also calls `GetNativeTexturePtr()`, which waits for the render thread (Unity uses a separate render thread here). The "main" times include the copy of the managed `byte[]` into an `Il2CppStructArray<byte>`.
- Correctness: the probe draws each texture into a render texture (`Graphics.Blit`), reads it back, and compares it with the `LoadImage` texture at mip 0 (2048 × 2048) and mip 5 (64 × 64).
- Machine: i7-11800H (16 threads), RTX 3060 Laptop GPU, NVIDIA 610.57.04, 15 GB RAM.

## A. Format support

Graphics API (from `SystemInfo`): `graphicsDeviceType=OpenGLCore`, `graphicsDeviceVersion='OpenGL 4.5.0 NVIDIA 610.57.04'`, `renderingThreadingMode=MultiThreaded`, `maxTextureSize=32768`, color space Gamma.

| Format | Unity `TextureFormat` | `SupportsTextureFormat` | `IsFormatSupported(..., Sample)` | Upload with 12 mips + draw | Mip 0 mean abs. diff. (0–255) | Mip 5 mean abs. diff. |
|---|---:|---|---|---|---:|---:|
| RGBA32 (reference, `LoadImage`) | 4 | True | — | yes | 0.00 | 0.00 |
| RGBA32 (worker decode) | 4 | True | — | yes | 0.00 | 1.72 |
| BC1 (DXT1) | 10 | True | True (`RGBA_DXT1_SRGB`) | yes | 0.66 | 6.58 |
| BC3 (DXT5) | 12 | True | True (`RGBA_DXT5_SRGB`) | yes | 0.66 | 6.58 |
| BC5 | **27** | True | True (`RG_BC5_UNorm`) | yes (R and G only) | 0.31 | 4.22 |
| BC7 | 25 | True | True (`RGBA_BC7_SRGB`, `RGBA_BC7_UNorm`) | yes | 0.13 | 2.97 |

Also reported: BC4, BC6H, and ETC2_RGBA8 are True. ASTC is False.

The mip 5 differences come from different mip filters (Unity's filter for `LoadImage`, a box filter for the others) and from BC encoding of small levels. They are not upload errors. The screenshot shows all six textures. The top row is minified (trilinear). The bottom row uses `mipMapBias = 4`, so it shows the small mip levels.

Evidence: `/tmp/opencode/spike-textures/textures-onscreen.png` (from run 3; tile order: LoadImage, worker RGBA32, BC1, BC3, BC5, BC7), `/tmp/opencode/spike-textures/run2/frame-00.png`.

**Pitfall found in run 1:** `TextureFormat.BC5` is 27, not 32 (32 is `PVRTC_RGB4`). With an unsupported format, Unity logs "Texture creation failed" and `LoadRawTextureData` throws `NullReferenceException`. The importer must check `SupportsTextureFormat` before it creates a texture.

## B. Load time, 2048 × 2048 with full mip chain

Times in ms. "Run 2" and "run 3" are two game starts. Each cell is median / max of 7 runs.

| Path | Thread | Run 2 | Run 3 |
|---|---|---:|---:|
| **B1** `LoadImage(png)` (texture created with `mipChain=true`) | main | 81.7 / 135.4 | 78.7 / 84.4 |
| B1 + render-thread sync | main | 104.5 / 152.6 | 91.0 / 94.7 |
| **B2** managed PNG decode (`ZLibStream`) | worker | 105.4 / 110.6 | 99.5 / 102.1 |
| B2 PNG decode + box mip chain | worker | 129.6 / 138.2 | 116.8 / 121.5 |
| B2 `LoadRawTextureData` (RGBA32, 22.4 MB chain) + `Apply(false, true)` | main | 38.1 / 47.6 | 30.1 / 36.4 |
| B2 main + render-thread sync | main | 49.5 / 57.4 | 36.0 / 41.3 |
| B2 of which: copy into `Il2CppStructArray` | main | 7.6 / 20.5 | 10.0 / 15.7 |
| **B3** BC1: read file + `LoadRawTextureData` + `Apply(false, true)` | main | 4.9 / 9.5 | 3.0 / 4.3 |
| B3 BC3 | main | 13.1 / 16.1 | 7.5 / 17.5 |
| B3 BC5 | main | 13.6 / 31.4 | 7.5 / 13.0 |
| B3 **BC7** | main | 11.4 / 19.7 | 5.3 / 11.0 |
| B3 BC7 + render-thread sync | main | 13.9 / 23.2 | 7.8 / 13.5 |

- File read is included in B3. It is 1–5 ms (warm page cache).
- `Texture2D.SetPixelData` (to upload only level 0 and let Unity build the mips) is stripped: "Method unstripping failed". Therefore the worker path must build the mip chain itself.
- The worker decoder is a small spike decoder (8-bit RGB/RGBA, no interlace, about 150 lines). It gives the same mip 0 pixels as `LoadImage` (difference 0.00).

### Encode cost (offline, same CPU, not in the game)

| Encoder | Format | Threads | Full chain, median / max (ms) | Size with mips | PSNR RGB, mip 0 |
|---|---|---|---:|---:|---:|
| etcpak 0.9.15 (native, via Python) | BC1 | 1 | 16 / 20 | 2.80 MB | 42.2 dB |
| etcpak | BC3 | 1 | 20 / 23 | 5.59 MB | 42.2 dB |
| etcpak | BC5 | 1 | 22 / 26 | 5.59 MB | 49.0 dB (R, G) |
| etcpak (bc7enc) | BC7 | 1 | 1,457 / 1,528 | 5.59 MB | 56.9 dB |
| BCnEncoder.NET 2.2.1 (managed) Fast | BC1 | 16 / 1 | 154 / 178 ; 194 / 197 | 2.80 MB | 51.8 dB |
| BCnEncoder.NET Fast | BC3 | 16 / 1 | 166 / 259 ; 303 / 308 | 5.59 MB | 44.7 dB |
| BCnEncoder.NET Balanced | BC1 / BC3 | 1 | 2,246 / 2,253 ; 2,282 / 2,323 | — | 59.7 / 59.6 dB |
| BCnEncoder.NET Fast | BC7 | 16 / 1 | 8,453 / 9,100 ; 83,778 / 84,020 | 5.59 MB | 50.5 dB |

- The python run is from the second encode pass (5 runs). The first pass gave the same order (BC7 1,209 ms median).
- PSNR values are high because the source came from GH3 DXT data. A good BC1 encoder can find the original blocks again. Do not use these PSNR values for new creator art.
- Package size for one texture: PNG 2.31 MB. BC7 with mips, gzip -9: 2.34 MB. BC1: 1.18 MB. BC3 (constant alpha): 1.32 MB.

Evidence: `/tmp/opencode/spike-textures/run2/melon.log`, `/tmp/opencode/spike-textures/run3/melon.log`, `/tmp/opencode/spike-textures/encode-python.json`, `/tmp/opencode/spike-textures/encode-csharp.txt`, `/tmp/opencode/spike-textures/encode-csharp-psnr.txt`.

## C. Custom folder

Where this game copy reads `Custom` (from `Il2Cpp.GlobalVariables` in the game):

| Field | Value |
|---|---|
| `isPortable` | False |
| `defaultCustomContentPath` | `/home/hedi/.clonehero/Custom` |
| `uniqueCustomContentPaths` | `['/home/hedi/.clonehero/Custom']` (also includes `[custom_content_directories]` from `settings.ini`) |
| `defaultSongPath` | `/home/hedi/.clonehero/Songs` |
| `persistentDataPath` | `/home/hedi/.config/unity3d/srylain Inc_/Clone Hero` |

The game folder `PlayerData/` is **not** used in this copy (non-portable mode). `~/.clonehero` is shared with all game copies. In portable mode, the Clone Hero wiki says that `Custom` is in `<game>/PlayerData/`. I did not test portable mode.

Test (run 3, compared with run 2). The script added these files, ran the game, and then removed them:

- `Custom/Stages/spike-top.chstage` (zip: `manifest.json`, `stage.glb`, `preview.png`)
- `Custom/Stages/Spike Group/spike-grouped.chstage`
- `Custom/Stages/Spike Group/spike-unpacked/` (folder with `manifest.json` and a loose `preview.png`)
- `Custom/Characters/Spike Group/spike-guitarist.chchar`
- In both runs: `Custom/Image Backgrounds/zz-spike-textures.png` and `Custom/Highways/zz-spike-textures.png`, to prove that the stock scan still works.

Result:

| Check | Run 2 (no Stages) | Run 3 (with Stages, Characters) |
|---|---|---|
| Game starts, song plays, stage loads | yes | yes |
| `backgroundImagePaths` | 1 (the test background) | 1 (the same; the loose `preview.png` in `Stages/` is not listed) |
| `Highways` | 6 (5 stock + test highway) | 6 (the same) |
| New errors or warnings in the Clone Hero session log | — | none (the log has the same 3 warnings as run 2) |
| New errors in the MelonLoader log | — | none (only the known MelonLoader start-up dump error) |

Clone Hero ignores the unknown folders. I did not open the in-game background or highway menus by hand. The proof is the scanned lists above.

**Shared folder:** the test used `~/.clonehero/Custom` because this copy has no other `Custom` folder. The script made all changes under the record lock and removed all test files at exit. After the runs, `~/.clonehero/Custom` has the same 8 entries as before (checked with `find`).

## Recommendation for package format v1

1. **Cache format:** raw BCn blocks with a full mip chain and a small header (like `.gh3tex`), uploaded with `LoadRawTextureData` + `Apply(false, true)`. Use **BC7** for color and color + alpha when the encoder is native or offline. Use **BC1** (opaque) and **BC3** (alpha) when the importer encodes in managed code. Keep BC5 for future normal maps. Check `SupportsTextureFormat` and fall back to RGBA32.
   - Main-thread cost per 2048² texture: BCn 3–14 ms. Worker PNG + RGBA32 upload: 30–50 ms. `LoadImage`: 80–105 ms. A GH3 venue has 4–37 megapixels of textures (about 1–9 textures of 2048²). Estimate (not measured): about 0.1 s of main-thread time for a BCn venue, against 0.4–0.9 s with `LoadImage`.
2. **Packages ship PNG** (inside the GLB, as glTF requires). The importer encodes on the player's machine, on worker threads, on first use:
   - BC1/BC3 in managed code is fast enough: about 0.2–0.3 s per 2048² on one thread (BCnEncoder.NET Fast). A venue takes some seconds one time.
   - BC7 in managed code is too slow (BCnEncoder.NET: 8.5 s per 2048² on 16 threads). BC7 on the player's machine needs a native encoder (bc7enc in etcpak: 1.5 s per 2048² on one thread). This adds a native library per platform.
   - Until the cache is ready, the loader can use worker PNG decode + RGBA32 upload. This works today (measured above).
   - Optional, later: the converter and the add-on can add pre-compressed BC7 next to the PNG (for example as `MSFT_texture_dds`). For GH3 venues the converter can keep the original GH3 DXT data. The importer then uses this data and skips the encode.
3. **Folder location:** use `Custom/Stages/` and `Custom/Characters/` with group subfolders. Clone Hero ignores them. Do not hard-code `~/.clonehero`: read `GlobalVariables.uniqueCustomContentPaths` (or `defaultCustomContentPath`), so portable mode and extra content folders work.

## Open risks

- Windows (D3D11) and AMD/Intel GPUs: not tested. D3D11 supports BC1–BC7, but Clone Hero on Windows can use other APIs.
- The in-game "main" times do not include all GPU work. The render-thread sync (`GetNativeTexturePtr`) shows the part that the render thread does. The driver can still do more work later.
- Many textures in one frame: not measured. The loader must spread uploads over frames.
- BCnEncoder.NET was measured on .NET 10 outside the game, not in the MelonLoader .NET 6 runtime. It needs `Microsoft.Toolkit.HighPerformance`. Test it in the game before you use it.
- sRGB: the game uses Gamma color space. The probe created all textures as sRGB (`linear=false`). Linear data (normal maps, BC5) must use `linear=true`.

## Not tested

- Portable mode (`PlayerData/Custom`), extra `custom_content_directories`, Windows and Android.
- Interlaced, 16-bit, palette, or grayscale PNG files in the managed decoder.
- Visual quality of BC1/BC3 against BC7 on creator art (the test image came from DXT data).
- Memory use of the import path. Disk read time with a cold page cache.
- The in-game background and highway menus by hand.
