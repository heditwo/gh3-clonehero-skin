#!/usr/bin/env bash
set -euo pipefail
export MISE_QUIET=1

game_dir="${GH3_CLONE_HERO_DIR:-$HOME/games/clonehero-gh3-melon}"
project_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
game_dir="$(realpath "$game_dir")"
cd "$game_dir"

export LD_LIBRARY_PATH="$game_dir${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}"
export LD_PRELOAD="$game_dir/MelonLoader.Bootstrap.so${LD_PRELOAD:+:$LD_PRELOAD}"
export DISPLAY="${DISPLAY:-:0}"
export WAYLAND_DISPLAY="${WAYLAND_DISPLAY-wayland-1}"

if [[ "${1:-}" == "--preview" ]]; then
  shift
  song_dir="${GH3_SKIN_PREVIEW_SONG:-$project_dir/charts/GH3 Combo Preview}"
  if [[ ! -f "$song_dir/notes.chart" && ! -f "$song_dir/notes.mid" ]]; then
    printf 'No chart found in %s\n' "$song_dir" >&2
    exit 1
  fi
  preview_width="${GH3_SKIN_PREVIEW_WIDTH:-1600}"
  preview_height="${GH3_SKIN_PREVIEW_HEIGHT:-900}"
  if [[ ! "$preview_width" =~ ^[0-9]+$ || ! "$preview_height" =~ ^[0-9]+$ ]] ||
    (( preview_width < 640 || preview_height < 480 )); then
    printf 'Preview resolution must be at least 640 x 480\n' >&2
    exit 1
  fi
  exec ./clonehero --melonloader.disablestartscreen \
    --song "$song_dir" --player Guitar,Expert \
    -screen-fullscreen 0 -screen-width "$preview_width" -screen-height "$preview_height" "$@"
fi

exec ./clonehero --melonloader.disablestartscreen "$@"
