# Gem sprites and Star Power

`SkinMod` reads the game state once per frame. The gem renderer and HUD use the same native Star Power active flag. Charge alone, including a visual charge override, does not change the gems.

The renderer checks each active pooled note on every update. This changes gems already on the highway at activation and expiry. It also selects the correct art for new notes and reused pool objects.

## BetterGH3 taps

The owner's `BetterGH3-FULLPACKAGE_v1.0.2.zip` contains tap textures in `Guitar Hero III/DATA/ZONES/_DONOTDELETE.pak.xen` and `_DONOTDELETE.pab.xen`. Entry 2 is a texture bundle with 22 images. `prepare-assets.py` selects the 11 tap images by their `images\highway\tap2d_*.img` keys.

There are five lane textures, five phrase sheets, and one cyan active texture. Ordinary taps use 128 × 64 images. Phrase taps use 16-frame sheets with four columns and four rows. Each frame is 128 × 64. The prepared files are `<lane>-tap.png`, `<lane>-star-tap-<frame>.png`, and `active-tap.png`.

The package's `TapHopoChord.dll` selects `sys_tap2d_<lane>` materials. Its active branch uses entry 5, `sys_tap2d_starpower_sys_tap2d_starpower` (`0x31dfdf5c`). The older upstream GH3-Plus source uses battle gems and blue lane index 3. That older mapping does not apply to this package.

The DLL SHA-256 is `855dce84332782f1c972779030ec95498bdca78b3c503f80a228187ad2cf0ecc`. Its tap material tables are at RVAs `0x917c` and `0x9194`; the active selection is at RVA `0x11de`.

The cyan regular and HOPO textures come from `global_gfx` indices 39 and 27. They are distinct from the normal blue-lane textures.

## Active phrase art

FastGH3 has separate cyan phrase sheets for its overlapping Star Power support. These sheets are in `DATA/ZONES/global.pak.xen` and `global.pab.xen` in `FastGH3_1.1.zip`. `prepare-assets.py` extracts the following images by checksum:

| Variant | Image | Key |
| --- | --- | --- |
| Regular star | `images\highway\star2d_star.img` | `0x2b2f2c12` |
| HOPO star | `images\highway\star2d_star_hammer.img` | `0x26331841` |
| Tap star | `images\highway\star2d_star_tap.img` | `0xb278cfe7` |

Each sheet contains 16 frames in a 4 × 4 grid. The prepared files are `active-star-<frame>.png`, `active-star-glow-<frame>.png`, and `active-star-tap-<frame>.png`. The active array uses the same six note-kind indices as the lane array, so activation changes the art without changing the note type.

The blue-lane tap sheet has deep blue highlights. The active tap sheet has cyan highlights that match the rim and the GH3 reference footage. Both sheets contain transparent faces. The skin uses the native active sheet, including its alpha channel, without an added brightness or opacity adjustment.

## Active selection

| Note type | Inactive art | Active art |
| --- | --- | --- |
| Regular | Lane regular gem | Cyan regular gem |
| HOPO | Lane glowing gem | Cyan glowing gem |
| Phrase regular | Lane phrase animation | Cyan phrase animation |
| Phrase HOPO | Lane glowing phrase animation | Cyan glowing phrase animation |
| Tap | Lane tap gem | Cyan tap gem |
| Phrase tap | Lane phrase tap animation | Cyan phrase tap animation |

Clone Hero supports overlapping Star Power phrases. During active Star Power, phrase gems use cyan art and keep their star shapes and regular, HOPO, or tap animations. Ordinary gems use the corresponding cyan active textures. All gems return to their lane colors when Star Power ends. Clone Hero controls phrase awards and note rules. If a phrase is missed, the current native sprites determine which ordinary gem replaces it.

Clone Hero identifies an ordinary tap with body `spr_newnotes_strip4_3`. A phrase tap uses the animated `spr_star_notes_strip2_*` body. A phrase HOPO uses cap `spr_star_notes_cap_strip4_2`.

Open notes use Clone Hero art. Empty or unsupported pooled notes hide the custom gem and restore the native renderers.

## Star rotation

All phrase variants use the same 16-frame animation at 30 frames per second. One atlas cycle takes 0.533 seconds. `GemAnimation` selects the frame from Unity's scaled double-precision clock. The renderer reads it once per update, so all lanes stay in step. Activation and expiry change the color without restarting the animation.

The GH3 `global_gfx` material records from BetterGH3's `global.pab.xen` specify a 4 × 4 atlas and a rate of 30. The extracted `sys_star2d_blue_sys_star2d_blue` record starts at `0x7160`; the values are at offsets `0x100`, `0x110`, and `0x120` within that record. All ten regular and HOPO star materials use the same values. Its texture key `0xc76f7566` maps to global-gfx index 84. The blue HOPO material uses key `0xb695b723`, index 24.

## Checks

The Release build and skin-logic checks pass. All 88 initial PNG files match the decoded source pixels, including each phrase tap frame. The 48 active phrase frames also match the FastGH3 package and the textures used in the recorded GH3 reference, pixel for pixel.

An instrumented Clone Hero run used repeating five-lane chords for all six note types, with open notes between groups. The temporary probe allowed native phrase awards for the command-line bot. It called the native activation method, then let the engine drain the charge. The skin did not force the active flag.

- Activation: song time 10.202 seconds, charge 50%.
- Overlapping phrase award: song time 11.503 seconds, charge increased from 41.96% to 66.88% while active.
- Expiry: song time 22.213 seconds, charge 0%.
- 2,392 frames and 21,555 custom-note checks.
- All 90 combinations of five lanes, six note types, and three stages: before activation, active, and after expiry.
- Ten notes remained visible across activation. Six remained visible across expiry. Custom gems used the correct active or lane colors, and phrase gems kept their star variants.
- All 16 star frames used the 30 fps clock before activation, while active, and after expiry.
- Fifteen reused note objects, including open-note fallback.
- Charged but inactive notes kept their lane colors. Native note renderers stayed hidden for custom gems and returned for open notes.

The checker derives note type from chart timing and note position, independently of the skin's sprite classifier. It checks each star frame against the captured animation clock. Captures, asset checks, probe source, and the JSONL trace are in `/tmp/opencode/gh3-tap-shading-2026-10-05/`. The probe is removed from the installed game's `Mods` folder after verification.

`gh3-clonehero-tap-shading.mp4` shows GH3 through FastGH3 on the left and the installed Clone Hero skin on the right. Both games use the same chart. The 29-second, 3200 × 1040, 60 fps video aligns note hits and keeps both full HUDs visible. The games use different Star Power activation paths, as marked in the video. The adjacent `.mp4.json` file records the alignment, DLL hash, animation rate, and overlap checks.
