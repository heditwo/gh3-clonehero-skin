# Orchestrator handoff

Read this file completely before you start. You take over as the **orchestrator** of this project. Your mission is to build [PACKAGING-PLAN.md](PACKAGING-PLAN.md). The previous orchestrator wrote this file on 2026-10-09.

## 1. Mission

Build the release format and the creator workflow in [PACKAGING-PLAN.md](PACKAGING-PLAN.md). The owner approved the plan and its decisions (section 2 of the plan). Do not reopen them unless the owner asks.

- **Step 1 of the plan is done.** All parity work is merged, and all assets were regenerated from the cleaned-up pipeline on `main` @ `2f7b438`.
- **Start at step 2 (the spike).** Then do steps 3 to 6 in order.
- The plan names the old script names (`export-gh3-stages.py`, `prepare-*.py`). After the cleanup they are `python -m pipeline.venues`, `python -m pipeline.library`, and the other modules in [PIPELINE.md](PIPELINE.md). Update the plan when you touch those sections.

## 2. The owner's rules

| Topic | Rule |
| --- | --- |
| Git | You may commit and push to `origin main` for this repository. **Linear history only**: rebase agent branches onto `main` and fast-forward. **Never create merge commits.** The owner had `main` rewritten to remove them. |
| Worktrees | Put agent worktrees under `~/Work/worktrees/gh3-clonehero-skin/<name>`, never under `/tmp`. Remove each worktree and branch after you fast-forward it, but only if it has no uncommitted or unmerged work. |
| AWS, other repos | Not relevant here. Do not touch other repositories. |
| Comments | Only where needed; explain "why", not "what". |
| Docs | ASD-STE100 Simplified Technical English. Mermaid for diagrams, no ASCII art. Keep diagrams simple. The owner rejected a crowded diagram. |
| Shell | Set `MISE_QUIET=1`. Do not chain commands with `echo "---"` separators. |
| Videos | When the owner asks for a video, upload it to filebin: `curl -sS --fail -X POST -H "Content-Type: video/mp4" -H "Accept: application/json" --data-binary @f.mp4 "https://filebin.net/<random-bin>/<name>.mp4"`. Bins expire after about 6 days. |
| Evidence | Match GH3 from its data, scripts, shaders, and executable. Say when something is calibrated by eye. Report honestly. |
| Check agent claims | **Look at the evidence yourself before you pass a conclusion to the owner.** An agent once reported "the fingers match GH3" from a flawed metric; the owner saw the opposite in the same video, and the root cause (GH3 never applies the finger release) was found only after a memory trace of the running game. |
| Orchestration | The owner wants you to delegate work to subagents and to orchestrate. Do short checks yourself. |

## 3. Machine limits (important)

The PC locked up twice. It has 15 GB RAM and an RTX 3060 Laptop GPU (6 GB).

- **Recordings:** one at a time, always under `flock /tmp/opencode/gh3-record.lock`. The owner tested two captures at once and the PC froze. Do not try again.
- **Heavy jobs:** pipeline exports, library rebuilds, and shader builds run under `flock /tmp/opencode/gh3-heavy.lock`.
- **Original GH3:** only one instance, under `flock /tmp/opencode/gh3-original.lock`, and also hold the recording lock while it records.
- **Agents must block on locks in the foreground** (long shell timeout). Agents that end their turn "to wait for the lock" stop working.
- Crash notifications ("Process crashed") pile up at the right edge of the capture region. Dismiss them after captures: `HYPRLAND_INSTANCE_SIGNATURE=$(hyprctl instances -j | jq -r '.[0].instance') WAYLAND_DISPLAY=wayland-1 omarchy-shell notifications dismissAll`. Do not toggle Do Not Disturb.

## 4. Environment

| Item | Location |
| --- | --- |
| Repository | `/home/hedi/Work/personal/gh3-clonehero-skin`, remote `git@github.com:heditwo/gh3-clonehero-skin.git` |
| Main game install | `~/games/clonehero-gh3-melon` (Clone Hero 1.1.0.6142, Unity 2022.3.62f2, IL2CPP, MelonLoader 0.7.3). `bash install.sh` builds and installs into it. |
| GH3 data for the pipeline | `~/games/gh3-test-tools/game/DATA` |
| Original GH3 (playable, GPU capture) | `~/games/gh3-matched-capture/Guitar Hero III`. Tools in `~/.cache/gh3ref/` (`start-gh3-gpu.sh`, `pin-window.sh`). The bot patch and the camera-lock patch tools are in `~/.cache/gh3ref/agent-fingers3/tools/`. Always restore patched files; the original `qb.pab.xen` is in `~/.cache/gh3ref/qb.pab.xen.orig`. |
| GH3 reference recordings | `~/.cache/gh3ref/full/gh3-mycurse-raw.mp4` (My Curse in The Dive, 1600×900, song zero at 10.175 s), `mc-orig.raw` (its audio, mono f32le 4000 Hz, for alignment), `sbs.sh <modvideo> <modzero> <out> [label]` (side by side) |
| Test chart | `~/.cache/gh3ref/charts/mycurse-native` (GH3 notes and Star Power) |
| Blender | Blender 5.2.1 is installed. **Always run `blender-clean`** (`~/.local/bin/blender-clean`), not `blender`: from a mise shell, Blender takes mise's Python as its home and loses its standard library. Headless use works: `blender-clean --background --factory-startup --python-expr "..."`; EEVEE renders to PNG on the GPU. |
| Backup of the old history | Local branch `backup/main-before-linearize`. Delete it when the owner agrees. |
| Old asset copies | `~/.cache/gh3ref/stages-pre-cleanup`, `libraries-pre-cleanup`, `stages-before-*`, `libraries-before-*`, `root-captures/` (4.6 GB). Safe to delete after the owner agrees. |

## 5. Documentation map

| File | Contents |
| --- | --- |
| [ARCHITECTURE.md](ARCHITECTURE.md) | Runtime modules, pipeline, asset formats, data flow |
| [PIPELINE.md](PIPELINE.md) | Regeneration commands and order. `scripts/regenerate.sh` runs all steps; the shader step must run after the venues |
| [VERIFICATION.md](VERIFICATION.md) | Tests, recording, runtime variables, comparisons with GH3 |
| [STATUS.md](STATUS.md) | What is complete and the known differences from GH3 |
| [PARITY-NOTES.md](PARITY-NOTES.md) | GH3 facts: addresses, constants, script names, formulas |
| [CUSTOM-CONTENT.md](CUSTOM-CONTENT.md) | The current stage-pack authoring contract |
| [PACKAGING-PLAN.md](PACKAGING-PLAN.md) | Your mission |

Tests: `bash scripts/test.sh` (C# logic tests, Python tests, shell syntax). Material coverage: `python3 tools/audit-materials.py` (921 materials, all translated GH3 templates; it exits non-zero on a fallback).

## 6. How the previous orchestrator worked

1. **One agent per task**, each with its own worktree, branch `fix-<name>`, and a reflink copy of the game: `cp -a --reflink=always ~/games/clonehero-gh3-melon ~/games/clonehero-gh3-<name>`. Give the worktree real reflink copies of the generated assets: `cp -a --reflink=always assets/stages assets/stage-packs assets/libraries <worktree>/assets/` plus the other gitignored files under `assets/`.
2. **A shared brief file** with the rules (workspace, deploy command, recording command, locks, Il2Cpp limits, code style, deliverable). The old briefs are `/tmp/opencode/agent-brief.md` and `/tmp/opencode/agent-brief-2.md`; they may be gone after a reboot, and their paths and commands predate the cleanup. Write a new brief from sections 2 to 5 of this file before you start agents.
3. **Each agent must deliver:** root cause with evidence, changed files, verification images or videos, the commit, and the regeneration commands that `main` needs.
4. **Integration:** rebase the agent branch onto `main`, fast-forward, run `scripts/regenerate.sh` steps that the change needs (under the heavy lock), `scripts/test.sh`, `install.sh`, a short recording check, push, then remove the worktree and branch.
5. **Report to the owner** in short tables and plain words. Show one comparison image yourself when it matters. Upload videos when asked.
6. **Recording command** (from the repository):
   `flock /tmp/opencode/gh3-record.lock env HYPRLAND_INSTANCE_SIGNATURE=$(hyprctl instances -j | jq -r '.[0].instance') WAYLAND_DISPLAY=wayland-1 GH3_SKIN_STAGE=gh3.dive GH3_SKIN_PREVIEW_SONG=$HOME/.cache/gh3ref/charts/mycurse-native GH3_SKIN_PREVIEW_WIDTH=1600 GH3_SKIN_PREVIEW_HEIGHT=900 GH3_SKIN_VIDEO_X=3840 GH3_SKIN_VIDEO_Y=90 GH3_SKIN_VIDEO=<out.mp4> GH3_SKIN_VIDEO_SECONDS=<n> GH3_SKIN_VIDEO_TRIM=<n> TMPDIR=$HOME/.cache/gh3ref/gpu bash scripts/record-preview.sh`
   Add `GH3_SKIN_VIDEO_AUDIO=game` for sound. Retry up to 3 times if the video is missing or the log lacks "stage loaded". Song zero is about 10.5–11.1 s into a mod recording (8 s intro); align by audio cross-correlation with `mc-orig.raw`. Use `GH3_CLONE_HERO_DIR` to record an agent's game copy.

## 7. Current state

- `main` @ `2f7b438`, pushed. Mod version 0.9.8 plus all later work. All assets regenerated and installed. No agents are running. No open worktrees.
- Known gaps from GH3 are listed in [STATUS.md](STATUS.md). The ones the owner noticed or is likely to ask about:
  - The guitarist and singer render about 25–30% darker than GH3 (the bassist matches). The cause is probably the lighting inputs or the missing shadow map, not the materials.
  - Body and hand drift in close-ups differs from GH3.
  - The hit flame starts about 2 frames later than GH3.
  - Rock Band `VENUE` tracks are not read yet (planned in PACKAGING-PLAN section 4.2).
- Two local, gitignored research scripts in `assets/event-audit/behavior-work/` were updated to import `pipeline.gh3.formats`.

## 8. Facts that matter for the packaging work

- Clone Hero runs the mod in IL2CPP. Many Unity methods are stripped ("Method unstripping failed" in the log). Stripped: ranged `Mesh` setters, `GetComponentInChildren<T>(bool)`, shader property enumeration, `AvatarBuilder.BuildHumanAvatar`, `HumanDescription`, `AnimationClip.SetCurve`. `Unity.Collections` and `Unity.Mathematics` are absent. `Texture2D.LoadImage` and `LoadRawTextureData` work.
- Avoid Unity struct constructors and operators in per-frame loops.
- The stage shader bundle is built without the Unity editor (UnityPy patching, `pipeline/render`). It holds the translated GH3 templates; `render/stage-templates.json` must ship with it.
- Current runtime mesh formats: `GH3STG04` (venues) and `GH3BND02` (skinned). These become the internal cache in the plan.
- Animation clips load lazily on the main thread during play; the plan prefetches them.
