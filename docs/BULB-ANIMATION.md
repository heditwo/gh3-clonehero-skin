# Star Power bulb entrance

## GH3 reference

The source is `gh3-long-star-power-1600x900.mp4`, at 60 fps. Star Power becomes ready at 8.0 seconds. The reference was checked against the owner's GH3 HUD scripts from `~/games/gh3-autoplay/DATA/PAK/qb.pak.xen` and its `.pab.xen` file.

The extracted HUD script is `0x63f57f24.0x3ab79ef5.q`. Its entrance loop sets each bulb morph to 0.4 seconds and waits 0.2 seconds before the next bulb. The career HUD layout in `0xbec57122.0xfa5ffaf5.q` gives the slide distance and rotated parent containers.

The extra bulbs do not scale up in place. They slide out from behind the rock meter:

- Bulb 4 starts at 0.0 seconds, bulb 5 at 0.2 seconds, and bulb 6 at 0.4 seconds.
- Each slide takes 0.4 seconds. All three bulbs reach their final positions at 0.8 seconds.
- The original slide distance is 170 HUD units, or 148.75 pixels in the 900p reference. The directions are 0°, 21°, and 42° from vertical.
- The meter moves as a group during the first 0.45 seconds. It lifts and tilts, then shrinks and tilts back, then returns to rest. Frame measurements preserve the original motion, including its interrupted transitions.
- The measured scale peaks at 1.4453, then falls to 0.5995. The measured rotation ranges from 8.88° clockwise to 12.45° counterclockwise.
- When Star Power ends, the extra bulbs retract in reverse order. Each bulb moves 10% farther out for 0.1 seconds, then slides back for 0.4 seconds. Starts remain 0.2 seconds apart.

`StarPowerBulbAnimation` interpolates the measured meter poses between source frames. It uses the original slide durations and directions for the bulbs. The existing glass and glow textures move together. The meter background, lights, needle, and housing draw above the moving bulbs, as in GH3.

## Runtime behavior

The entrance starts when native charge reaches 50%. Activation continues an entrance already in progress. Active Star Power keeps the bulbs out while charge drains below 50%. After activation ends, the bulbs retract. A later recharge starts a new entrance. Pause stops the animation clock. A scene reset clears all animation state.

If charge changes during retraction, the next entrance starts from the current bulb positions. Joining a scene with Star Power already active starts with the bulbs fully out.

## Evidence

The review folder is `/tmp/opencode/gh3-bulb-entry-2026-10-05/`. It contains the source/DLL backup, extracted reference frames, measured meter transforms, measured bulb positions, temporary test probe, and runtime recordings.

Logic checks cover the entrance order and timing at 30, 60, and 144 fps, measured pulse peaks, pause, immediate activation, active drain, reverse retraction, interrupted retraction, recharge, and reset.

To compare the first entrance in a recording with GH3, run:

```bash
MISE_QUIET=1 uv run --with opencv-python-headless --with numpy python check-bulb-animation.py \
  gh3-long-star-power-1600x900.mp4 path/to/capture.mp4 --output path/to/motion-check.json
```

The checker scans the first 12 seconds, finds the first blue-bulb frame, and measures 55 meter poses. Each video uses its own settled meter as the shape reference. Thus health color and static artwork differences do not stand in for motion measurements. The report includes the measured scale, rotation, displacement, and matching-point count for every frame.

The report gives both the direct frame errors and errors with a one-frame capture tolerance. This tolerance allows for the different phases of the game and recorder clocks. It does not change the animation timing. The previous build fails this check because its meter does not move.

## Verified result — 2026-10-05

- Release build and logic checks passed. The Python checks, shell syntax checks, and `git diff --check` passed.
- The final native-state run checked 2,925 game frames. It included full charge, activation, drain, reverse retraction, and a second entrance after recharge. Glass and glow positions stayed aligned. The needle and health colors passed their existing boundary checks.
- The video comparison measured 55 entrance frames. The maximum meter-pose errors were 0.00130 in scale, 0.083° in rotation, 0.315 pixels horizontally, and 0.248 pixels vertically, at the 900p comparison size. These direct frame results did not need a phase adjustment.
- Each entrance was checked against 29 high-confidence bulb-position measurements from GH3. The maximum travel difference was under 4 pixels at 900p, including the game and recorder sampling offset.
- Before/after PNG checks found zero changed pixels in the settled meter at empty, half, and full charge. The checked crop was `(1090, 510, 250, 290)` at 900p. A `10×40` highway corner at `(1090, 760)` was excluded. This result applies to the meter region.
- The delivered 1440p video contains 2,640 frames. Static meter and score-frame checks found no missing-image bands. The meter check excludes its two short entrance movements; the entrance motion has a separate frame check.

The initial X11 recordings contained incomplete frames. The final video uses `gpu-screen-recorder` to capture the compositor output. The video scripts now use that method and record the capture region in their manifests.

The installed DLL matches the Release build: SHA-256 `fffb7bfd970a7a4859a8b1d4c6f6bf0dc5ddca18468129365fdf237bafc6be3b`. The temporary native-state probe has been removed. The probe enabled native phrase awards for the bot, invoked native activation, and injected misses for the test. Normal gameplay uses no probe.

### Previews

- [GH3 and mod comparison, at normal and quarter speed](https://filebin.net/gh3-bulbs-cd05698248cc/gh3-bulb-entry-comparison.mp4)
- [Full 1440p native-state test](https://filebin.net/gh3-bulbs-cd05698248cc/gh3-bulb-entry-1440p.mp4)

The links expire on October 12, 2026. The uploaded files passed SHA-256 checks against the local videos. The upload receipt and source manifest are in the review folder.
