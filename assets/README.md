# Assets

This folder has one folder for each type of file. Do not put files directly in `assets/`.

## Skin files (in Git)

`scripts/regenerate.sh` writes these files from the owner's GH3 files. `install.sh` copies each folder to `<game>/Mods/GH3Skin/assets/` with the same layout. `src/SkinAssetPaths.cs` has the paths that the mod reads, and `pipeline/paths.py` has the paths that the pipeline writes.

| Folder | Contents | Written by |
| --- | --- | --- |
| `gems/` | One folder for each gem color: `green/`, `red/`, `yellow/`, `blue/`, `orange/`, and `active/` (gems during active Star Power). Each folder has the note, the HOPO glow, the tap note, and the 16 animation frames of the Star Power note, glow, and tap | `hud` step (`pipeline.hud.textures`) |
| `sustains/` | Sustain tails for each color, Star Power, and a dropped note (`sustain-dead`), and the held versions with GH3's glow. The mod does not use `sustain.png` | `hud` step (`pipeline.hud.textures`) |
| `highway/` | The highway board, the rails (`highway-edges-*`), and a skull board that the mod does not use | `hud` step (`pipeline.hud.textures`) |
| `highway/beat-lines/` | The bar, beat, and half-beat lines. The `hud` step writes files that are different from the files in Git. Keep the files in Git (see `docs/PIPELINE.md`) | `hud` step (`pipeline.hud.textures`) |
| `highway/star-power/` | The eight frames of the active Star Power highway | `hud-video` step (`pipeline.hud.star_power`) |
| `hud/` | `counter-tuning.json` (the measured HUD placement and the streak counter animation, `HudTuning`) and `clear.png` (an empty sprite that hides the stock HUD). The mod does not use `HUD_lower_strike.png` | `clear.png`: `hud` step. The other files are not generated |
| `hud/rock-meter/` | The rock meter body, lights, needle, and tube. The mod does not use `HUD_rock_tube_glow_fill_b.png`, and no step writes it | `hud` step (`pipeline.hud.textures`) |
| `hud/score/` | The score panel body, the multiplier and amp digits (`HUD_score_nixie_*`, DXT5 `.dds`), and the multiplier lamps (`HUD_score_light_*`) | `hud` step (`pipeline.hud.textures`) |
| `hud/counter/` | The note-streak counter body and drum | `hud` step (`pipeline.hud.textures`) |
| `hud/messages/` | The light streak behind the gameplay messages (`hud-glowburst`) | `hud` step (`pipeline.hud.fonts`) |
| `hud/you-rock/` | The flames of the "You Rock!" screen | `hud` step (`pipeline.hud.fonts`) |
| `hud/ready/` | The "Star Power Ready" banner and lightning frames, and their layout (`ready-animation.json`) | `hud-video` step (`pipeline.hud.ready_animation`) |
| `fonts/` | The GH3 font atlases (`.png`) and glyph tables (`.json`): messages, streak counter, score, "You Rock!", and the fit font | `hud` step (`pipeline.hud.fonts`) |
| `hit-effects/` | The note-hit, held-note, and Star Power bonus effect textures | `hud` step (`pipeline.hud.hit_effects`) |
| `previews/` | The venue preview images (512 × 288) for the packages. `install.sh` does not copy them; the packages contain them | `scripts/capture-venue-previews.sh` |

## Generated files (not in Git)

| Folder | Contents | Written by |
| --- | --- | --- |
| `stage-packs/` | The public stage packs of the venues (the source form) | `venues` and `template` steps |
| `stages/` | The compiled venues, and `stages/events/` (the GH3 song direction). `install.sh` copies them as the older install, which the mod uses when no package is available | `venues`, `events`, and `template` steps |
| `libraries/` | The compiled band library (`gh3-band-v1`), with the shader bundles in `render/`. `install.sh` copies it as the older install, and copies the shader bundles to `Mods/GH3Skin/render/` | `library` and `render` steps |
| `packages/` | The `.chstage` and `.chlib` packages. `install.sh` copies them to `Mods/GH3Skin/Packages/` | `packages` step (`pipeline.package.build`) |
| `event-audit/` | Local research tools and reports. Not in Git and not installed | Manual |
