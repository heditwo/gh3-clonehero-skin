#!/usr/bin/env bash
# Regenerate every generated asset from the owner's GH3 files, in dependency order.
# Usage: scripts/regenerate.sh [--clean] [step...]
# Steps: library venues events render hud template. The default is all steps.
# The optional hud-video step needs GH3_REFERENCE_VIDEO (the GH3 Star Power reference recording).
set -euo pipefail
export MISE_QUIET=1

project_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
data="${GH3_DATA:-$HOME/games/gh3-test-tools/game/DATA}"
game="${GH3_CLONE_HERO_DIR:-$HOME/games/clonehero-gh3-melon}"
cd "$project_dir"

clean=false
steps=()
for argument in "$@"; do
  case "$argument" in
    --clean) clean=true ;;
    library | venues | events | render | hud | hud-video | template) steps+=("$argument") ;;
    *) printf 'Unknown step: %s\n' "$argument" >&2; exit 2 ;;
  esac
done
(( ${#steps[@]} )) || steps=(library venues events render hud template)

stage_python=(uv run --with pillow --with numpy --with 'shapely>=2.1' python)
run() {
  local start=$SECONDS
  printf '== %s\n' "$*"
  "$@"
  printf '== %s s\n' "$((SECONDS - start))"
}

if [[ "$clean" == true ]]; then
  rm -rf assets/libraries/gh3-band-v1 assets/stages assets/stage-packs/gh3.* assets/stage-packs/example.studio \
    assets/stage-packs/conversion-report.json
fi

for step in "${steps[@]}"; do
  case "$step" in
    library) run "${stage_python[@]}" -m pipeline.library --data "$data" ;;
    venues) run "${stage_python[@]}" -m pipeline.venues --data "$data" ;;
    events) run python3 -m pipeline.events "$data" --library --output assets/stages/events ;;
    render)
      # The stage shader holds the GH3 templates that the compiled venues and the library use, so it follows them.
      run uv run --with UnityPy --with lz4 --with numpy python -m pipeline.render.shaders --game "$game"
      run "${stage_python[@]}" -m pipeline.render.particle_materials --source "$data/FXFILES/MaterialLibrary.bin.xen"
      ;;
    hud)
      run uv run --with pillow --with numpy python -m pipeline.hud.textures
      run uv run --with pillow python -m pipeline.hud.hit_effects --data "$data"
      run uv run --with pillow python -m pipeline.hud.fonts
      ;;
    hud-video)
      video="${GH3_REFERENCE_VIDEO:?Set GH3_REFERENCE_VIDEO to gh3-long-star-power-1600x900.mp4}"
      run uv run --with pillow --with numpy --with opencv-python-headless python -m pipeline.hud.star_power --video "$video"
      run uv run --with pillow --with numpy python -m pipeline.hud.ready_animation --video "$video"
      ;;
    template)
      run "${stage_python[@]}" -m pipeline.stagepack.template
      run "${stage_python[@]}" -m pipeline.stagepack.install assets/stage-packs/example.studio
      ;;
  esac
done
