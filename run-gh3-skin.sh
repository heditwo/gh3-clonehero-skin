#!/usr/bin/env bash
set -euo pipefail
export MISE_QUIET=1

game_dir="${GH3_CLONE_HERO_DIR:-$HOME/games/clonehero-gh3-melon}"
project_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
cd "$game_dir"

export LD_LIBRARY_PATH="$game_dir${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}"
export LD_PRELOAD="$game_dir/MelonLoader.Bootstrap.so${LD_PRELOAD:+:$LD_PRELOAD}"
export DISPLAY="${DISPLAY:-:0}"
export WAYLAND_DISPLAY="${WAYLAND_DISPLAY-wayland-1}"

if [[ "${1:-}" == "--preview" ]]; then
  shift
  song_dir="${GH3_SKIN_PREVIEW_SONG:-$game_dir/PlayerData/Songs/GH3 Skin Preview}"
  exec ./clonehero --melonloader.disablestartscreen \
    --song "$song_dir" --player Guitar,Expert \
    -screen-fullscreen 0 -screen-width 1600 -screen-height 900 "$@"
fi

exec ./clonehero --melonloader.disablestartscreen "$@"
