# GH3 skin handoff — 2026-10-04

## Runtime-fix update

The latest [meter correction](docs/METERS.md) fixes the needle geometry and verifies native charge through full, active, empty, and refill states.

The later [strike-line correction](docs/STRIKE-LINES.md) supersedes the beat-line details below.

The later runtime-fix work supersedes the status below. See [docs/VERIFICATION.md](docs/VERIFICATION.md) for capture results and limits, and [README.md](README.md) for current commands. The current local code adds active Star Power multipliers, cached scene objects, pooled-note handling, camera-area rail placement, CPU texture-copy release, and persistent capture outputs. The original source and installed DLL are saved in `/tmp/opencode/gh3-fix-review.OrgUnB/`. Changes remain local.

The remaining sections preserve the earlier handoff and its capture provenance.

## Current state

- The project is at `~/Work/personal/gh3-clonehero-skin` on `main`. `HEAD` and `origin/main` are both `e858b69`. The current candidate has local changes that are **not committed or pushed**. It is installed only in `~/games/clonehero-gh3-melon`, the isolated test copy.
- The source still identifies the mod as v0.8.98. Do not present the local candidate as a released version.
- The candidate keeps Clone Hero's original fret buttons, strings, and button animation. It omits the GH3 lower-left graphic. Its HUD and rail layout have resolution-dependent placement.
- Regular GH3 gems stay at Clone Hero's note depth. Star gem sprites use 550 pixels per unit; regular gems use 720. This is the latest size candidate, not a user-approved final match.
- Moving beat lines use Clone Hero's original sprite, not the GH3 beat-line image. `GameplaySpriteRenderer` places each line at local height `0.11` to keep it above the highway surface. A note-free test at 1600 × 900 showed lines reaching the fret row. A 2560 × 1440 normal-play capture was also made. Do not claim that every resolution and game state has passed visual review.

## Changes that are still local

- `src/GameplaySpriteRenderer.cs`: note depth and size correction; stock moving beat lines placed above the highway.
- `src/SkinAssets.cs`: star gem size and removal of the unused beat-line replacement sprite.
- `src/GameplayHud.cs` and new `src/HudViewport.cs`: HUD layout at different screen sizes.
- `record-preview.sh`, `record-unmodded.sh`, and `run-gh3-skin.sh`: selectable preview resolution.
- `tests/Program.cs`, `tests/SkinLogic.Tests.csproj`, and `README.md`: viewport checks and current behavior.

## Latest comparison and a bad reference

- [Full-frame stock-versus-mod comparison, 5120 × 1440](https://filebin.net/gh3-verified-stock-comparison-051a64bfe4/stock-vs-mod-full-5120x1440.mp4): each game frame is 2560 × 1440.
- [Native-pixel highway comparison, 2560 × 960](https://filebin.net/gh3-verified-stock-comparison-051a64bfe4/stock-vs-mod-highways-native-2560x960.mp4): a crop from each capture, with no image scaling. These links expire on October 11, 2026.
- **Do not use** `/tmp/opencode/gh3-stock-2560x1440.mp4` or the older comparison at `gh3-stock-vs-fixed-mod-5fcb006041`. That stock capture showed beat lines stopping early. A fresh capture of the same chart, `/tmp/opencode/gh3-stock-2560x1440-recheck.mp4`, showed them reaching the lower highway. The reason the first stock run differed is **unknown**. Both runs used a game directory whose executable and checked assets match `~/games/CloneHero-1.1.0.6142-Linux.tar`. BepInEx has no installed plugins there and was not preloaded by `record-unmodded.sh`. This does not explain the different results.
- A stock comparison must include a visual check that its lines reach the fret row **before** it is used as a reference. Matching binary files alone did not catch the bad capture.

## Checks and limits

- The latest build and install to the isolated copy succeeded. The logic tests, shell syntax checks, and `git diff --check` passed. There is no automated test for beat-line visibility or for a valid stock capture.
- The 1440p comparison and note-free test are available for user review. Visual acceptance is **pending**. The user's own 1440p PC has not been tested.
- Portrait rail placement is wrong and portrait is **not supported**. Earlier captures of 4:3, 16:10, ultrawide, and other sizes predate the latest gem and beat-line work; do not claim those layouts are finished. The non-16:9 rails need more review.
- The preview bot does not earn Star Power. Forced meter values change only the visual display. An earned charge in normal play, and the ready animation that follows it, remain unverified.
- This is not a pixel-perfect GH3 match. The required Clone Hero fret art and absent GH3 lower-left graphic differ by design.

## How to continue

1. Ask for feedback on the **latest** comparison, especially the star size, lines near the frets, and 2560 × 1440 HUD placement. Do not use the older comparison as evidence.
2. If a line fault remains, reproduce it in normal play and in a note-free chart. Check the stock capture before comparing. Find why the earlier stock run had a different result; do not assume the cause is video scaling.
3. Check the latest build at each required aspect ratio, with special attention to the rail art. Keep portrait marked unsupported until it is corrected and verified.
4. Test an earned Star Power charge by playing a chart without forced meter values.
5. After a code change, run `MISE_QUIET=1 bash ./install.sh`, `MISE_QUIET=1 dotnet run --project tests/SkinLogic.Tests.csproj -c Release --nologo`, shell syntax checks for changed scripts, and `git diff --check`. Record full-resolution gameplay with `GH3_SKIN_PREVIEW_WIDTH=2560 GH3_SKIN_PREVIEW_HEIGHT=1440 bash ./record-preview.sh` when the change affects 1440p. Do not commit or push without a request.
