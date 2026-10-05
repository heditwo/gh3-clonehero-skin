# Runtime fix checks — 2026-10-04 to 2026-10-05

The later [strike-line correction](STRIKE-LINES.md) replaces the raised stock beat lines. This report describes the earlier runtime-fix build and its DLL hash.

Review files are in `/tmp/opencode/gh3-fix-review.OrgUnB/`. The folder contains the original source archive and installed DLL, before/after videos, loader logs, the temporary runtime probe, and image comparisons.

The final DLL is installed in the isolated game copy. Its SHA-256 is `8af0b7c9c6aef36dae178f2973cab499c0559c08b463c9820cea496d16d1347e`. It matches the Release build. The temporary probe was removed.

## Lossless HUD comparison

The original and final DLLs captured `GH3 Counter Entrance Match` at 9.4 seconds after scene load. Both used visual charge `0.75`, a charge threshold of 20 notes, and rock `0.8`. The counter was settled at 25 notes. These PNG captures have no video encoding loss.

| Resolution | Score and counter | Rock meter and bulbs |
| --- | --- | --- |
| 1600 × 900 | 0 changed pixels | 0 changed pixels |
| 2560 × 1440 | 0 changed pixels | 0 changed pixels |

The measured rectangles at 900p were `(260, 580)–(488, 820)` and `(1080, 540)–(1350, 810)`. The 1440p rectangles used the same coordinates scaled by 1.6. This establishes pixel identity for these HUD regions in the sampled state.

Complete before/after frames are saved as [900p](../preview-before-after-900p.png) and [1440p](../preview-before-after-1440p.png) images in the project root. The review folder has `lossless-report.json` and `check-lossless.py`.

## Before/after images

The same 112-note chart and display overrides were recorded before and after the runtime changes at 1600 × 900 and 2560 × 1440. The gem matrix was recorded at 1600 × 900 with normal game state. `compare-captures.py` aligned the independent video clocks and wrote complete frame pairs and amplified difference images.

| Run | Samples | Mean full-frame RGB error, out of 255 | Largest sampled error |
| --- | ---: | ---: | ---: |
| Final 900p, long chart | 9 | 0.556 | 1.052 |
| Final 1440p, long chart | 9 | 1.115 | 1.627 |
| 900p, gem matrix | 10 | 0.332 | 0.564 |

These are encoded-video measurements. Black background pixels reduce the full-frame mean. The JSON reports also contain separate score/counter, rock/bulb, highway, banner, and visible-pixel measurements.

The inspected pairs retain HUD placement, digit proportions, gem size, fret art, and the ready-animation source frames. Note motion, flames, beat-line visibility, and some reel frames differ between independently timed runs. The video comparisons do not establish whole-frame pixel identity or identical beat-line visibility in every frame.

- `final-comparison-1600x900/`: ready-banner entrance, settled banner, counter, and normal gameplay.
- `final-comparison-2560x1440/`: the same checks at native 1440p resolution.
- `comparison-gems/`: original versus updated gem-matrix video.
- `matrix/run.7kEAiW/`: nine fresh stock-gem and nine fresh GH3-gem screenshots. All 18 frames survived capture.
- Repository `preview-gem-matrix.png`: regular notes, forced and natural HOPOs, taps, open notes, open HOPOs, open taps, and chords.

## Capture startup failures

One 1920 × 1200 attempt stopped before its screenshot. Core PID `3973034` shows a fault inside `libbass.so` during `BASS_StreamCreateFile`, followed by Unity's signal handler. Other threads were inspected. There was no OOM kill in the journal for that time. The exact trigger is not established; the native library has no useful symbols for the faulting instruction. The extracted core was deleted after inspection. The source chart and earlier captures were intact.

This failed attempt is excluded from visual results. The saved trace is `crash-backtrace.txt` in the review folder.

A later lossless capture with the original DLL lost its temporary Xwayland server. Core PID `4022913` resolves to `xwl_glamor_gbm_dispose_syncpts` during pixmap cleanup. The server log shows a reset after the readiness client disconnected. Capture and recording scripts now use `-noreset` to keep this temporary display alive between clients. Three lossless captures and a video then completed with this option. The saved trace is `xwayland-backtrace.txt`; the extracted core was deleted.

## Runtime checks

- Release build and install: passed, with zero warnings and errors.
- Logic checks: passed. They cover active multiplier art, ready/active/recharge transitions, version rejection, finite capture values, note classification, camera-area placement, and animation asset dimensions.
- Capture contract tests: passed. Supplied times and output folders survive capture; old frames cause a failure.
- Shell syntax, Python syntax, and `git diff --check`: passed.
- Three consecutive gameplay scene loads: one GH3 HUD in every sampled gameplay state. Each scene rebuilt its cache of 500 pooled note objects and 723 effect/render objects.
- Instrumented native Star Power runs: phrase awards increased engine charge to 25% and 50%; the banner appeared; native activation produced the correct 6× and 8× textures while the banner stayed hidden. Charge then drained to zero, the multiplier returned to 4×, and later phrase awards triggered the banner again. The temporary probe allowed the bot's native award routine to run. This was not an unmodified human-input test. See `power-six.jsonl`, `power-cycle.jsonl`, and the [active-state image](../preview-star-power-active.png).
- Final 4:3, 16:10, and ultrawide screenshots: rails follow the game camera area and meet the fret row. The 1920 × 1200 repeat completed. These are single-player layout samples, not complete mode coverage.

## Time and memory

The same temporary probe timed `SkinMod.OnLateUpdate` for the original and final DLLs on the same chart at 900p. The first three batches after warm-up each contained 600 calls:

| Measurement | Original | Final |
| --- | ---: | ---: |
| Batch median update time | 1.37–1.40 ms | 0.40–0.59 ms |
| Mean managed allocation per call | about 25.3 KiB | about 0.6 KiB |
| Readable HUD/animation textures | 312 | 0 |
| Readable HUD/animation RGBA data | 124.4 MiB | 0 |

The final build loads four extra active-multiplier textures. HUD/animation GPU RGBA data remains about 124.7 MiB. These figures describe texture data and the mod's update routine, not total process memory or whole-game frame time. Game and recording load varied during the test.

## Coverage limits

Human-controller Star Power earning, a left-handed profile, multiplayer fallback, and a live practice seek were not exercised. Portrait remains unsupported. The native audio-startup fault is recorded above; its exact cause remains unresolved.
