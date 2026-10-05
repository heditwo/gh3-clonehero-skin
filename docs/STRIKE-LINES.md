# Strike-line correction — 2026-10-05

The later [meter correction](METERS.md) updates the needle and installed DLL hash. The line behavior described here still applies.

The moving beat lines now use the original GH3 textures and stay at highway level. The old `0.11` world-unit height offset is removed.

## Rendering

- `BeatlineVisual` uses a cached child renderer for each pooled line. The game still controls its timing, travel, and active state.
- The source textures are `079_1024x16_DXT5.png`, `090_1024x16_DXT5.png`, and `100_1024x16_DXT5.png` from the owner's extracted `global_gfx` bundle. These supply the bar, beat, and half-beat art at full resolution.
- The bottom of the sprite is at the native line position. Its bright face follows the base of an on-beat note.
- Line width and center follow the rail perspective measured in `gh3-fast-autoplay-1600x900.mp4`. The same camera rectangle and field-of-view scale apply to the rails and lines.
- A separate sprite material draws the lines above the highway and below the strings, notes, and fret buttons. GH3 strings cover each line at their crossing points. The native track material produced missing lower lines in repeated runs. Changing the highway render queue did not reliably correct this fault.
- The line material uses its own distance fade. Texture clamping prevents the lower texture edge from repeating above the line.

## Review files

Source and DLL backups, diagnostic builds, reference frames, and measurements are in `/tmp/opencode/gh3-strike-lines-2026-10-05/`. The reference contains real GH3 frames. The earlier raised-line video is `gh3-skin-preview-2026-10-05-1440p.mp4`.

The [GH3 comparison image](../comparison-gh3-strike-lines.png) shows native-pixel highway crops. The two frames have different note timing.

## Repeat the visual check

`check-beatlines.py` compares complete 16:9 PNG captures with actual GH3 reference frames. It measures line width, center, brightness, thickness, and string crossings. It can also track lines in a note-free 120 BPM video. The video check requires each measured line to pass behind the fret buttons and remain visible below them.

```bash
MISE_QUIET=1 uv run --with pillow --with numpy python check-beatlines.py \
  --reference /path/to/gh3-frames \
  --candidate /path/to/skin-frames \
  --note-free-video /path/to/note-free.mp4 \
  --output /path/to/line-metrics.json
```

Frame folders must contain `frame-*.png` files. Use an 11-second or longer video. Record the video with PNG capture disabled. Frequent 1440p PNG saves can stall the game and skip parts of the line motion.

The three texture files have the same RGBA pixels as the original GH3 assets. Geometry measurements use video references with encoding loss; they do not establish whole-frame pixel identity.

## Final results

The installed DLL matches `src/bin/Release/net6.0/Gh3Skin.dll`. Its SHA-256 is `ae3f0eef88317e59204889b4e82dd00916ca785b25b0da53519992db3ea71731`. The preview manifest records this same hash. No diagnostic mod remains installed.

The checker used 144 complete, centered GH3 reference lines. Width errors below are in 900p coordinates; multiply the 1440p row by 1.6 for native pixels.

| Capture | Median width error | 95th-percentile width error | Lines tracked past the frets | Early disappearance |
| --- | ---: | ---: | ---: | ---: |
| 1600 × 900 | 1.33 px | 2.83 px | 15 | 0 |
| 2560 × 1440 | 1.48 px | 2.36 px | 15 | 0 |

- Both 240-frame motion checks passed. Each tracked line remained visible below the fret row.
- String-crossing checks passed. The lines draw below the strings.
- Regular-line brightness and thickness differed from the reference measurement by 1.3% at 900p and 3.1% at 1440p.
- The old raised-line frames fail the lower-highway check. An intermediate build with lines above the strings fails the crossing check.
- Release build: no warnings or errors. Skin logic tests, capture-contract tests, Python syntax checks, and `git diff --check` passed.
- The final 1440p PNG run completed all five requested captures. An earlier, more frequent PNG run returned only nine of ten files; the final measurements use the complete set.

The report files are `verified-900p.json` and `verified-1440p.json` in the review folder. `verified-motion-1600x900.mp4` and `verified-motion-2560x1440.mp4` are the note-free motion checks.

The [31-second 1440p preview and GH3 comparison](https://filebin.net/gh3-lines-5c56a99bcbe7) are available until October 12, 2026. Both uploaded files passed SHA-256 verification. The local video is `gh3-lines-corrected-1440p.mp4`.
