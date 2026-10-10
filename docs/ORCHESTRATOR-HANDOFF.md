# Orchestrator handoff

Read this file completely before you start. You take over as the **orchestrator** of this project. The previous orchestrator and its agents wrote and updated this file on 2026-10-09.

## 1. Mission and state

The mission was [PACKAGING-PLAN.md](PACKAGING-PLAN.md): a release format and a creator workflow. Steps 1 to 6 are done. Step 6 is **release readiness**, not a release: nothing is published, tagged, uploaded, or announced. Build artifacts stay on the local disk.

- `main` @ `d04137c` when this file was written (plus the commit that adds this file). Use `git log` for the current commit.
- The mod is version 0.9.10 (package format 1.0 of all kinds: the character library `gh3-characters-v1`, the GH3 guitarists on their own rigs, and the Havok limit fields of the hair joints). It loads all stages, the band library, and the characters from packages. Linux is tested; Windows is tested under Wine only; macOS is not supported ([STATUS.md](STATUS.md#platforms)).
- What remains before a release (owner decisions and tests on real hardware): [PACKAGING-PLAN.md section 10](PACKAGING-PLAN.md#10-before-a-release).
- Known gaps from GH3 and the remaining work: [STATUS.md](STATUS.md).
- The owner decisions of the plan (section 2 of the plan) are final. Do not reopen them unless the owner asks.

## 2. The owner's rules

| Topic | Rule |
| --- | --- |
| Git | You may commit and push to `origin main` for this repository. **Linear history only**: rebase agent branches onto `main` and fast-forward. **Never create merge commits.** The owner had `main` rewritten to remove them. |
| Release | Do not publish, tag, upload, or announce a release. The owner does that. |
| Worktrees | Put agent worktrees under `~/Work/worktrees/gh3-clonehero-skin/<name>`, never under `/tmp`. Remove each worktree and branch after you fast-forward it, but only if it has no uncommitted or unmerged work. |
| AWS, other repos | Not relevant here. Do not touch other repositories. |
| Comments | Only where needed; explain "why", not "what". |
| Docs | ASD-STE100 Simplified Technical English. Mermaid for diagrams, no ASCII art. Keep diagrams simple. The owner rejected a crowded diagram. `tests/test_docs_links.py` checks links, anchors, and the diagram size. |
| Shell | Set `MISE_QUIET=1`. Do not chain commands with `echo "---"` separators. |
| Videos | When the owner asks for a video, upload it to filebin: `curl -sS --fail -X POST -H "Content-Type: video/mp4" -H "Accept: application/json" --data-binary @f.mp4 "https://filebin.net/<random-bin>/<name>.mp4"`. Bins expire after about 6 days. |
| Evidence | Match GH3 from its data, scripts, shaders, and executable. Say when something is calibrated by eye. Report honestly. Never present an estimate as a measurement. |
| Check agent claims | **Look at the evidence yourself before you pass a conclusion to the owner.** An agent once reported "the fingers match GH3" from a flawed metric; the owner saw the opposite in the same video, and the root cause (GH3 never applies the finger release) was found only after a memory trace of the running game. |
| Orchestration | The owner wants you to delegate work to subagents and to orchestrate. Do short checks yourself. |

## 3. Machine limits (important)

The PC locked up twice. It has 15 GB RAM and an RTX 3060 Laptop GPU (6 GB). Swap is already in use.

- **Game runs and recordings:** one at a time on the whole machine, always under `flock /tmp/opencode/gh3-record.lock`. This includes Windows runs under Wine. The owner tested two captures at once and the PC froze. Do not try again.
- **Heavy jobs** (more than 1 GB of memory: pipeline exports, library rebuilds, shader builds, Blender, large Python analysis): under `flock /tmp/opencode/gh3-heavy.lock`.
- **Original GH3:** only one instance, under `flock /tmp/opencode/gh3-original.lock`, and also hold the recording lock while it records.
- **Agents must block on locks in the foreground** (long shell timeout, for example one hour). Agents that end their turn "to wait for the lock" stop working. Agents must not start background jobs that run after their command returns.
- **`/tmp` is a RAM disk.** It takes memory from the 15 GB. Keep `/tmp/opencode/<name>/` small (logs, a few images). Put videos, frame dumps, downloads, caches, and large outputs under `~/.cache/<name>/`. A full `/tmp` also stops Xwayland.
- **Every Clone Hero start leaves a second `clonehero` process** (a child with the same command line). The capture scripts stop it. After a manual run, stop it yourself, because it keeps inherited file locks (also the record lock). Under Wine, also stop leftover `Clone Hero.exe` processes.
- **`~/.clonehero` is shared by all Linux game copies** (settings and `~/.clonehero/Custom`). Add test packages to `~/.clonehero/Custom` only under the record lock, and remove them after the run. The Windows copies use `PlayerData` in their game folder (portable mode).
- Crash notifications ("Process crashed") pile up at the right edge of the capture region. Dismiss them after captures: `HYPRLAND_INSTANCE_SIGNATURE=$(hyprctl instances -j | jq -r '.[0].instance') WAYLAND_DISPLAY=wayland-1 omarchy-shell notifications dismissAll`. Do not toggle Do Not Disturb.
- **Agent model:** start agents with the model `anthropic/claude-opus-5-5-personal-oauth`.

## 4. Environment

| Item | Location |
| --- | --- |
| Repository | `/home/hedi/Work/personal/gh3-clonehero-skin`, remote `git@github.com:heditwo/gh3-clonehero-skin.git` |
| Main game install | `~/games/clonehero-gh3-melon` (Clone Hero 1.1.0.6142 Linux, Unity 2022.3.62f2, IL2CPP, MelonLoader 0.7.3). `bash install.sh` builds and installs into it. Agents never deploy to it; they use their own reflink copy |
| Windows game copies | `~/games/clonehero-win-spike` (the template: game, Wine prefix, DXVK 3.1.1, `env.sh`, `run-win.sh`; do not change it) and `~/games/clonehero-win-e2e` (end-to-end release tests). Wine 11.17 is in `~/games/gh3-test-tools/wine-root`. See [VERIFICATION.md](VERIFICATION.md#test-under-wine) |
| Windows game for the shader build | `~/games/clonehero-win-spike/game` (`GH3_CLONE_HERO_WINDOWS_DIR`; the default `~/games/clonehero-win/game` does not exist on this machine) |
| GH3 data for the pipeline | `~/games/gh3-test-tools/game/DATA` |
| Original GH3 (playable, GPU capture) | `~/games/gh3-matched-capture/Guitar Hero III`. Tools in `~/.cache/gh3ref/` (`start-gh3-gpu.sh`, `pin-window.sh`). The bot patch and the camera-lock patch tools are in `~/.cache/gh3ref/agent-fingers3/tools/`. Always restore patched files; the original `qb.pab.xen` is in `~/.cache/gh3ref/qb.pab.xen.orig` |
| GH3 reference recordings | `~/.cache/gh3ref/full/gh3-mycurse-raw.mp4` (My Curse in The Dive, 1600×900, song zero at 10.175 s), `mc-orig.raw` (its audio, mono f32le 4000 Hz, for alignment), `sbs.sh <modvideo> <modzero> <out> [label]` (side by side) |
| Test chart | `~/.cache/gh3ref/charts/mycurse-native` (GH3 notes and Star Power) |
| Blender | Blender 5.2.1: **always `blender-clean`** (`~/.local/bin/blender-clean`), never `blender`; from a mise shell, Blender takes mise's Python as its home and loses its standard library. Blender 4.2.23 (the add-on minimum): `~/.cache/blender/blender-4.2-clean`. Headless: `blender-clean --background --factory-startup --python-expr "..."`; EEVEE renders to PNG on the GPU |
| Evidence of finished agents | `~/.cache/gh3-evidence/<name>/` (moved from `/tmp/opencode/`; about 6 GB). The docs give these paths |
| Release build output | `GH3_RELEASE_OUT`, for example `~/.cache/gh3-skin-release` ([VERIFICATION.md](VERIFICATION.md#release-build)). Not in the repository |
| Backup of the old history | Local branch `backup/main-before-linearize`. Delete it when the owner agrees |

## 5. Documentation map

The README has one entry for each audience: [PLAYER-GUIDE.md](PLAYER-GUIDE.md) (players; the only place with the install steps), [CREATOR-GUIDE.md](CREATOR-GUIDE.md) (creators), and the developer table in the [README](../README.md#for-developers). [STATUS.md](STATUS.md) is the current state. [PACKAGING-PLAN.md](PACKAGING-PLAN.md) is the record of the plan. The spike reports in [spike/](spike/) are records.

Tests: `bash scripts/test.sh` (C# logic tests, Python tests with the link check, shell syntax). Material coverage: `python3 tools/audit-materials.py` (921 materials, all translated GH3 templates; it exits non-zero on a fallback).

## 6. How agents are run

1. **One agent per task**, each with its own worktree under `~/Work/worktrees/gh3-clonehero-skin/<name>`, branch `fix-<name>` from the current `main`, and, if it runs the game, a reflink copy of the game: `cp -a --reflink=always ~/games/clonehero-gh3-melon ~/games/clonehero-gh3-<name>`. Give the worktree reflink copies of the generated assets: `cp -a --reflink=always assets/stages assets/stage-packs assets/libraries assets/packages <worktree>/assets/`. For Windows work: `cp -a --reflink=always ~/games/clonehero-win-spike ~/games/clonehero-win-<name>`, then change `here=` in its `run-win.sh` and `WINEPREFIX` in its `env.sh`.
2. **Brief files.** The agents read two shared brief files: `/tmp/opencode/spike-brief.md` (rules, locks, environment, Il2Cpp facts, how to run probe code and record) and `/tmp/opencode/platform-brief.md` (an addendum for production work and Windows). They are in `/tmp`, a RAM disk, so **they can be gone after a reboot**. Their key content, to write them again:
   - Context: the main repository is the orchestrator's `main`; the agent works and commits only in its worktree, on its branch, with plain commits on top of `main`. Never push, never merge, never touch `main` or other worktrees.
   - The rules of section 2 (comments, docs, shell) and the machine limits of section 3 (locks, foreground waits, `/tmp` size, notifications).
   - The environment of section 4, the deploy command `GH3_CLONE_HERO_DIR=<copy> bash install.sh` (never to `~/games/clonehero-gh3-melon`), the recording command of step 6 below, and "retry up to 3 times if the video is missing or the log lacks `stage loaded`".
   - The Il2Cpp facts of [ARCHITECTURE.md](ARCHITECTURE.md#il2cpp-constraints) and the asset layout of [assets/README.md](../assets/README.md) (`src/SkinAssetPaths.cs` and `pipeline/paths.py` hold all paths).
   - Probe code (spikes only) is off by default, runs only with an environment variable such as `GH3_SKIN_SPIKE=<name>`, and logs with a clear prefix.
   - Production work: Linux must stay exactly as before, with a screenshot or recording as proof; update the docs that the change makes wrong; no `docs/spike/` report unless asked.
   - The owner goal: the mod works on all platforms that Clone Hero supports. The Windows facts of [spike/d3d11.md](spike/d3d11.md) and [VERIFICATION.md](VERIFICATION.md#windows).
   - Step 6 is release readiness: nothing is published.
   - Deliverable: root cause or design with evidence, changed files, verification images, the commits, the regeneration commands that `main` needs, and a short final message with what the orchestrator must check or decide.
3. **Integration:** rebase the agent branch onto `main`, fast-forward, run the `scripts/regenerate.sh` steps that the change needs (under the heavy lock), `scripts/test.sh`, `install.sh`, a short recording check, push, then remove the worktree, the branch, and the game copy. Move the agent's evidence from `/tmp/opencode/<name>/` to `~/.cache/gh3-evidence/<name>/`.
4. **Report to the owner** in short tables and plain words. Show one comparison image yourself when it matters. Upload videos when asked.
5. **Recording command** (from the repository or a worktree):
   `flock /tmp/opencode/gh3-record.lock env HYPRLAND_INSTANCE_SIGNATURE=$(hyprctl instances -j | jq -r '.[0].instance') WAYLAND_DISPLAY=wayland-1 GH3_CLONE_HERO_DIR=<copy> GH3_SKIN_STAGE=gh3.dive GH3_SKIN_PREVIEW_SONG=$HOME/.cache/gh3ref/charts/mycurse-native GH3_SKIN_PREVIEW_WIDTH=1600 GH3_SKIN_PREVIEW_HEIGHT=900 GH3_SKIN_VIDEO_X=3840 GH3_SKIN_VIDEO_Y=90 GH3_SKIN_VIDEO=<out.mp4> GH3_SKIN_VIDEO_SECONDS=<n> GH3_SKIN_VIDEO_TRIM=<n> TMPDIR=$HOME/.cache/gh3ref/gpu bash scripts/record-preview.sh`
   Put `<out.mp4>` under `~/.cache`. Add `GH3_SKIN_VIDEO_AUDIO=game` for sound. Song zero is about 10.5–11.1 s into a mod recording (8 s intro); align by audio cross-correlation with `mc-orig.raw`. Prefer screenshots (`scripts/capture-preview.sh`) when a still frame proves the point.

## 7. Open items

- The release items of [PACKAGING-PLAN.md section 10](PACKAGING-PLAN.md#10-before-a-release): the repository license, the BetterGH3 and FastGH3 gem art (credit only, or permission, or replacements), the schema `$id` URL, the publication, Windows on real hardware, a first start without MelonLoader assemblies, and one complete ten-venue run on the release build.
- The gaps in [STATUS.md](STATUS.md#remaining-work). The ones the owner noticed or is likely to ask about:
  - The guitarist and singer render about 25–30% darker than GH3 (the bassist matches). The cause is probably the lighting inputs or the missing shadow map, not the materials.
  - Body and hand drift in close-ups differs from GH3.
  - The hit flame starts about 2 frames later than GH3.
  - The picker works with the mouse only.
- `scripts/regenerate.sh` (hud step) rewrites `assets/highway/beat-lines/beatline*.png` with output that differs from the committed files. The committed files are the reference; restore them with `git checkout -- assets/highway/beat-lines` after a full regeneration, or fix the generator.
- Two local, gitignored research scripts in `assets/event-audit/behavior-work/` import `pipeline.gh3.formats`.
