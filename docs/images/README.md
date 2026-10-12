# Documentation images

This folder has the images of `README.md` and the guides. `tests/test_docs_images.py` checks the rules below.

## Rules

- Save every image in full colour (RGB). Do not reduce an image to a colour palette: no `pngquant`, no `magick -colors`, no Pillow `quantize`. A 256-colour palette makes the small, saturated HUD parts grey and adds dither noise.
- Game captures (gameplay and the picker): JPEG, quality 90, 4:4:4 chroma (no chroma subsampling), at most 1600 px wide. 4:4:4 keeps the thin coloured lines of the HUD and the gem rims sharp.
- Blender screenshots: PNG, at the capture size.
- Each image is at most 1920 px wide and 1.5 MB.
- Every image in this folder is used by a Markdown file or by `scripts/build-release.sh`.

`tools/doc-image.py` writes the files with these settings:

```bash
uv run --with pillow python tools/doc-image.py <capture.png> docs/images/<name>.jpg --width 1600
uv run --with pillow python tools/doc-image.py <screenshot.png> docs/images/creator/<name>.png [--crop x,y,width,height]
```

## Capture a game image

1. Capture at 1920 × 1080 with the song clock, at some candidate times. Run it under the record lock with your test game copy ([VERIFICATION.md](../VERIFICATION.md#screenshots)):

   ```bash
   flock /tmp/opencode/gh3-record.lock env GH3_CLONE_HERO_DIR=<copy> \
     GH3_SKIN_STAGE=gh3.dive GH3_SKIN_PREVIEW_SONG=$HOME/.cache/gh3ref/charts/mycurse-native \
     GH3_SKIN_PREVIEW_WIDTH=1920 GH3_SKIN_PREVIEW_HEIGHT=1080 GH3_SKIN_CAPTURE_CLOCK=song \
     GH3_SKIN_CAPTURE_TIMES=33,34,35,36,37,38 GH3_SKIN_CAPTURE_DIR=$HOME/.cache/<name>/frames \
     bash scripts/capture-preview.sh
   ```

2. Look at each frame. Select a frame with normal stage lighting (not a strong mood of one colour) and the full HUD in view.
3. Write the JPEG with `tools/doc-image.py` (see above).

For the picker, use `scripts/capture-menu.sh` with `GH3_SKIN_PICKER_TEST` and the same window size.

## Sources of the current images

| Image | Capture |
| --- | --- |
| `gameplay-dive.jpg` | `gh3.dive`, `mycurse-native`, default band, song time 36 s |
| `picker.jpg` | `GH3_SKIN_PICKER_TEST="ready;wait 3;open;apply stage=gh3.dive guitarist=gh3.judy.outfit1.style1;wait 2;capture <file>;close;quit"` |
| `creator/game-picker.jpg` | The same with `apply stage=example.club guitarist=<Miku package>` (`examples/example.club.chstage` installed), cropped to the picker window (`--crop 255,36,1410,1008`) |
| `creator/game-miku-guitarist.jpg` | `gh3.dive`, `mycurse-native`, `GH3_SKIN_CHARACTER_GUITARIST=<Miku package>`, song time 31 s |
| `creator/game-miku-singer.jpg` | `gh3.budokan`, the Brad Sucks chart "You're Not Going Anywhere" (Rock Band `VENUE` camera cuts), `GH3_SKIN_CHARACTER_SINGER=<Miku package>`, song time 69.5 s |
| `creator/stage-*.png`, `venue-open.png`, `addon-preferences.png` | `blender/tests/screenshots_guide.py` |
| `creator/miku-*.png` | `blender/tests/screenshots_miku.py`, cropped to the left 1316 px (`miku-toon.png`: 1600 px) and the top 814 px |
