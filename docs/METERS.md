# Rock and Star Power meter correction — 2026-10-05

## Background color fade

The background blends continuously from yellow at the center of the dial to green or red at 15° from the center. The blend follows the smooth needle, including reversals, pause, and reset. The lamps still use the calibrated Clone Hero health thresholds below.

The GH3 recording shows a health-dependent blend across the yellow part of the dial. A check of 363 unobstructed yellow-to-green frames gave a mean blend-weight difference of 0.0086 from a linear 15° fade. The first red fade used the same range. Older GH3 recordings, `gh3-source-combo-chart-1600x900.mp4` and `gh3-source-combo-played-1600x900.mp4`, also show a gradual blend as health falls into red.

The original red, yellow, and green textures are layered in GH3's drawing order. A fully opaque lower layer stays behind the fading layer, which prevents the face from becoming dark during a transition. Pure yellow, red, and green use their original textures.

Logic checks cover intermediate colors, smooth progression at 30, 60, and 144 fps, and the existing needle and lamp behavior. The review folder is `/tmp/opencode/gh3-rock-fade-2026-10-05/`.

### Fade verification

- The Release build, logic tests, and `git diff --check` passed.
- The final native-state run checked 2,758 game frames. It included 65 red/yellow blend frames and 365 yellow/green blend frames. The background stayed opaque, the lamps retained their native thresholds, and Star Power activated, drained, and refilled.
- Image checks measured 2,572 of the 2,640 delivered video frames. The two short bulb-entry movements were checked separately. The face showed intermediate colors in both ranges. The mean blend-weight error was 0.0035; the maximum was 0.051, including color encoding and one-frame UI timing differences.
- The bulb entrance still passed the 55-frame GH3 motion comparison. Its maximum position difference was 0.313 pixels at the 900p comparison size.
- The preview was made from a lossless screen recording, then encoded at high quality to preserve the small color changes.

The installed DLL matches the Release build: SHA-256 `08815c9e8eff0463d0533e9f0eccf689f2e477450cb495e1691f7eb3620355a2`. The temporary native-state probe has been removed. It enabled native phrase awards for the bot, invoked native activation, and injected misses for the test.

Previews: [actual-speed close-up](https://filebin.net/gh3-fade-e438483d9cc0/gh3-rock-color-fade-closeup.mp4) and [full 1440p test](https://filebin.net/gh3-fade-e438483d9cc0/gh3-rock-color-fade-1440p.mp4). The links expire on October 12, 2026. Both uploads passed SHA-256 verification against the local files.

## Native threshold update

Clone Hero uses red at or below `1f / 3`, yellow above `1f / 3` and at or below `2f / 3`, and green above `2f / 3`. The previous linear needle mapping changed the effective boundaries to about 27.3% and 71.5%. That mapping was incorrect.

`HudState.RockNeedleAngle` now maps five calibration points:

| Native health | Needle angle |
| --- | ---: |
| 0% | +43° |
| 33⅓% | +19.5°, red/yellow divider |
| 50% | 0°, vertical |
| 66⅔% | −18.5°, yellow/green divider |
| 100% | −43° |

The mapping is continuous and monotonic between these points. Exact threshold equality follows Clone Hero: one third is red; two thirds is yellow. Logic tests also check the adjacent single-precision values on both sides of each boundary and 10,001 health values across the full range.

Needle motion remains smooth. Color follows the displayed needle, so a short visual delay remains while it moves across a divider. The health ranges now agree with Clone Hero; the animated display is not an instantaneous raw-health indicator.

The review folder is `/tmp/opencode/gh3-native-thresholds-2026-10-05/`. The runtime boundary test compares the skin with Clone Hero's actual native classifier. The earlier results below are retained as history.

### Native threshold verification

The native-threshold DLL SHA-256 was `63844ac49435ae55614b3ad7f594a972be549442cbaeaa50adc89ddc384df70e`. It matched that Release build and corrected video manifest. Its temporary probe was removed. The later [bulb entrance update](BULB-ANIMATION.md) adds meter and bulb motion.

- The 900p boundary test recorded 1,782 runtime frames. All 605 settled frames matched Clone Hero's native color classifier across 13 health values. These include exact thresholds and the adjacent single-precision values on each side.
- The 1440p native-state test checked 2,923 runtime frames. The needle moved between health updates on 1,253 frames. The five color transitions followed native state with observed animation delays of 0.08–0.27 seconds.
- Native Star Power reached full charge at song time 11.76, activated at 13.76, drained to zero at 29.76, and refilled at 41.77. All six bulbs, the ready banner, and the single HUD passed their checks.
- The image check measured all 2,640 frames of the delivered 44-second video. It found no green or red changes before the needle entered those dial sections. The largest adjacent-frame angle change was 1.27 degrees.
- The Release build, logic tests, and `git diff --check` passed.

The [corrected gameplay video and actual-speed close-up](https://filebin.net/gh3-native-1cb7f22cf8b9) are available until October 12, 2026. Both uploads passed size and SHA-256 verification. Local files are `gh3-meter-native-thresholds-1440p.mp4` and `gh3-meter-native-thresholds-closeup.mp4`. They are labeled as automated tests with scripted misses. Their adjacent `.json` files contain the test conditions and results.

## Earlier motion and color update

This motion correction replaced stepped needle updates. Its linear health mapping was later replaced by the native-threshold update above. The original green threshold was 58% raw health. At that value, the needle angle was only 6.88 degrees into the right half of the yellow section.

- `RockMeterState` now moves the needle on every frame, including frames with no new health value.
- Movement accelerates and settles smoothly. Its maximum speed is 90 degrees per second. A long frame cannot skip more than 3 degrees of visible movement.
- The displayed needle angle controls the background and lamp color. Green starts at −18.5 degrees; red starts at +19.5 degrees. These angles put the tip inside the colored regions of `HUD_rock_lights_all.png`, past the divider gaps.
- The needle keeps the measured GH3 pivot, length, and endpoints. It does not overshoot those endpoints.
- Pause stops the needle. A scene reset clears its motion.
- Logic tests cover 30, 60, and 144 fps, full-range changes, reversals, frame stalls, pause, and reset. Half-second positions differ by less than 0.15 degrees across those frame rates.

The motion review folder is `/tmp/opencode/gh3-meter-motion-2026-10-05/`. It contains the previous build, source backup, per-frame runtime logs, and recordings. The sections below preserve the first meter correction and its measurements.

### Motion verification

The earlier motion-correction DLL SHA-256 was `761e5c968c37d009d434140cc6428d785657e1938ac327f778b5ec03bc242563`. It matched that Release build and video manifest. Its temporary probe was removed.

- The 900p step test checked 1,498 runtime frames. The needle continued moving on 527 frames with no new health value. All six color changes occurred at the displayed needle's section boundary. Full-range moves, reversals, and settling passed.
- The 1440p native-state test checked 2,919 runtime frames. The needle moved on 1,266 frames between health updates. All five color changes followed the displayed needle. Native Star Power still reached full charge, activated, drained to zero, and refilled.
- An image check measured the needle and meter color in all 2,640 frames of the delivered gameplay video. It found zero early green or red frames. The largest adjacent-frame angle change was 0.93 degrees, down from 4.09 degrees in the previous video.
- The same image check found 336 early green frames and 39 early red frames in the previous video. Thus the check detects the reported fault.
- The Release build, logic tests, and `git diff --check` passed.

The [44-second gameplay video and actual-speed close-up](https://filebin.net/gh3-smooth-751a1d7999e3) are available until October 12, 2026. Both uploads passed size and SHA-256 verification. Local files are `gh3-meter-smooth-1440p.mp4` and `gh3-meter-smooth-closeup.mp4`.

## Cause

The previous shared preview used `GH3_SKIN_FORCE_ROCK=0.8` and `GH3_SKIN_FORCE_POWER=0.75`. These settings held the needle at one position and prevented the displayed charge from reaching full. They changed only the display.

The rock needle also had incorrect geometry. Its pivot was too high and too far right. The needle was too short, its neutral angle was 10 degrees off vertical, and it drew in front of the lower housing.

## Correction

- The needle is vertical at 50% rock. Its endpoints are 43 degrees to each side.
- The 900p pivot is now `(1217, 760.5)`. A fit to 23 GH3 reference frames gives `(1217.0, 760.6)`, with a 0.10-pixel RMS line-fit error. One frame with an obstructed needle was excluded.
- The needle extends behind the lower housing. The housing covers the moving root, as it does in GH3.
- The visible tip radius is about 95 pixels at 900p. The previous needle was about 76 pixels long.
- The first correction changed needle angle directly on note hits. The later motion update above replaces this stepped movement.
- The six Star Power bulbs read native charge. At 100%, all six glow at full strength. During activation, they stay blue as charge drains below 50%.

Source and DLL backups, reference measurements, and the temporary test probe are in `/tmp/opencode/gh3-meters-2026-10-05/`. The GH3 reference is `gh3-long-star-power-1600x900.mp4`.

## Test method

The native-state test uses an automated player. A temporary probe permits the game's native phrase-award routine for this player, activates native Star Power after full charge, and injects native misses to exercise the rock meter. Awards are suppressed during activation so the meter can drain completely. No visual rock or charge override is used. This is an instrumented test, not a human-controller recording.

A separate geometry test sets rock to 0%, 25%, 50%, 75%, and 100% for still captures. These stills test the needle endpoints and pivot, not earning behavior.

## Verification

The [meter range image](../preview-meter-range.png) shows all five needle positions, the GH3 reference, and native full charge. The full-rock needle differs from the sampled GH3 line by 0.13 degrees and 1.45 pixels horizontally at 900p.

The first meter-correction DLL SHA-256 was `78324400111cb7e1c0a9cccbc374a612009e5d4c60d015fd457616a3efe3d26c`. It matched that Release build and its capture manifest.

The 900p native-state test checked 465 samples. Charge first reached 100% at song time 11.81, activated at 13.81, drained to zero at 29.82, and returned to 100% at 41.82. Native rock ranged from 6.6% to 103.9%; the display correctly limits the upper endpoint to 100%. All three rock colors appeared.

The 1440p repeat checked 463 samples and passed the same checks. Charge reached full at 11.84, activated at 13.82, reached zero at 29.82, and refilled at 41.84. The report files are `native-1600x900-checks.json` and `native-2560x1440-checks.json` in the review folder.

Every sampled bulb matched native charge, including all six full bulbs. The ready banner closed during activation. The needle stayed behind the housing, and exactly one HUD remained active. The native-state checker uses single-precision arithmetic for bulb boundaries, as the game does.

The Release build and skin logic tests passed with no warnings or errors.

The temporary probe was removed after capture. Only `Gh3Skin.dll` remains in the game's mod folder. The skin does not grant charge or inject misses in normal play.

The [44-second 1440p meter-cycle video and range image](https://filebin.net/gh3-meters-724a0894706f) are available until October 12, 2026. Both uploaded files passed size and SHA-256 verification. The local video is `gh3-meter-cycle-1440p.mp4`. It is labeled as an automated native-state test; its `.json` file records the probe conditions and test results.
