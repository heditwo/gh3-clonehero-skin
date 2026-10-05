# Active Star Power highway effects

## Rendering

The active highway uses GH3's eight-frame lightning animation at 15 frames per second. It has a narrow bolt along each rail, a wide bolt outside each rail, and a cyan glow. The effect follows the same camera viewport as the existing rails. The rock meter and score display draw in front of it.

The native engine's active flag controls visibility. A charged meter alone does not start the effect. Activation starts the loop, depletion hides it, and another activation starts a new loop. The animation uses the game clock, so pause holds its current frame. Scene changes and backward song-time jumps clear the animation.

`GameplaySpriteRenderer` caches the 13 renderers under `player.spNeckRenderer.spController`. It sets `forceRenderingOff` on these renderers so native animation cannot show the stock center glow, side glows, highlights, or star towers. Reset restores each renderer's previous setting. These changes apply after the existing single-player compatibility check.

## Source and preparation

`prepare-star-power.py` uses the owner's extracted GH3 art:

- `010_1024x512_DXT1.png`: eight lightning frames.
- `053_128x512_DXT5.png`: cyan side glow.
- `gh3-long-star-power-1600x900.mp4`: recorded rail placement, color, and animation frames.

The original `global_gfx.scn.xen` material `sys_Big_Bolt01_sys_Big_Bolt01` contains the values 8 columns, 1 row, and 15 fps. The highway setup script creates two bolt layers and one glow layer on each side. The script is `/tmp/opencode/gh3-bulb-entry-2026-10-05/qb/0x40d71b70.0xb15abbd3.q`.

The preparation script groups reference frames from 11–19 seconds by their lightning frame. A median removes moving notes and particles. A mask excludes the highway center, both main HUD areas, and the lower-left meter. The original textures fill the excluded areas, with a soft join to the measured pixels.

Run:

```bash
MISE_QUIET=1 uv run --with numpy --with pillow --with opencv-python-headless python prepare-star-power.py
MISE_QUIET=1 bash ./install.sh
```

Eight RGBA textures use about 25.4 MiB of GPU memory. They load once. The active HUD uses one image and changes its texture at the animation rate.

## Checks

Logic checks cover activation, an inactive charged meter, frame rate, looping, pause, depletion, repeat activation, reset, and a backward clock.

`check-star-power.py` compares a gameplay recording with GH3 frames outside the preparation interval. It compares visible outer-rail regions, excludes the HUD, and matches animation phase independently. It reports RGB error and the overlap of bright pixels. It does not assert whole-frame identity.

Final checks on October 5, 2026:

| Check | 1600 × 900 | 2560 × 1440 |
| --- | ---: | ---: |
| Native-state frames | 2,757 | 2,759 |
| Active frames | 959 | 959 |
| Mean lightning-frame duration | 0.06673 s | 0.06669 s |
| Reference comparison frames | 180 | 180 |
| Mean RGB error, out of 255 | 2.84 | 3.47 |
| Bright-pixel overlap | 96.29% | 96.11% |

All 13 stock renderers stayed hidden in every native-state frame. The replacement appeared only while Star Power was active. The checks covered charge, activation, depletion, and refill. The automated probe enabled native phrase awards and activation for the bot. It was removed after recording.

Both recordings passed the existing bulb-entrance comparison. The 1440p recording also passed the score and meter capture-integrity check, with no incomplete HUD frames. The release build, skin logic tests, Python tests, Python syntax check, and `git diff --check` passed.

The DLL used for these recordings matched the installed release DLL. SHA-256:

```text
dda0ecc71dfcada99529f5287cd6e9d4f269cf21f2214ca76ea1c261d8869399
```

The review folder is `/tmp/opencode/gh3-active-highway-2026-10-05/`. It contains the previous source and DLL, material inspection, measurements, native-state probes, and before/after recordings.

To repeat the saved 1440p comparison:

```bash
MISE_QUIET=1 uv run --with numpy python check-star-power.py \
  gh3-long-star-power-1600x900.mp4 \
  /tmp/opencode/gh3-active-highway-2026-10-05/verified-1440.mp4 \
  --capture-start 17 \
  --output /tmp/opencode/gh3-active-highway-2026-10-05/verified-visual-1440.json
```

## Previews

- [GH3 and Clone Hero comparison](https://filebin.net/gh3-highway-b58977c4f552/gh3-active-highway-comparison.mp4)
- [1440p gameplay: charge, activation, depletion, and refill](https://filebin.net/gh3-highway-b58977c4f552/gh3-active-highway-1440p.mp4)

These links expire on October 12, 2026. Both videos identify the automated test. The local files are `gh3-active-highway-comparison.mp4` and `gh3-active-highway-1440p.mp4` in the project folder. The upload receipt is `upload.json` in the review folder.
