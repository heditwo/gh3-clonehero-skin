#!/usr/bin/env bash
# Texture spike run (docs/spike/textures.md). Usage: run-texture-spike.sh <output folder> [custom]
# With "custom", it adds test files to the Clone Hero Custom folder for the run and removes them after.
set -euo pipefail
export MISE_QUIET=1

project_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../../.." && pwd)"
output="$(realpath -m "$1")"
mode="${2:-}"
game_dir="$HOME/games/clonehero-gh3-spike-textures"
cache="${GH3_SKIN_SPIKE_TEXTURES_DIR:-/tmp/opencode/spike-textures/cache}"
custom="$HOME/.clonehero/Custom"
mkdir -p "$output"

created=()
cleanup() {
  for path in "${created[@]}"; do
    if [[ -d "$path" ]]; then rm -r -- "$path"; else rm -f -- "$path"; fi
  done
}
trap cleanup EXIT

add_file() {
  [[ ! -e "$2" ]] || { printf 'Already exists: %s\n' "$2" >&2; exit 1; }
  mkdir -p "$(dirname -- "$2")"
  cp -- "$1" "$2"
  created+=("$2")
}

package() {
  local folder
  folder="$(mktemp -d /tmp/opencode/spike-textures/package.XXXXXX)"
  printf '{"format":"%s/1","id":"spike.dummy","version":"0.0.1","name":"Spike dummy"}\n' "$1" > "$folder/manifest.json"
  head -c 4096 /dev/urandom > "$folder/stage.glb"
  magick -size 320x180 xc:magenta "$folder/preview.png"
  (cd "$folder" && zip -q -0 package.zip manifest.json stage.glb preview.png)
  add_file "$folder/package.zip" "$2"
  rm -r -- "$folder"
}

magick -size 640x360 xc:orange /tmp/opencode/spike-textures/background.png
magick -size 512x1024 xc:cyan /tmp/opencode/spike-textures/highway.png
add_file /tmp/opencode/spike-textures/background.png "$custom/Image Backgrounds/zz-spike-textures.png"
add_file /tmp/opencode/spike-textures/highway.png "$custom/Highways/zz-spike-textures.png"
if [[ "$mode" == custom ]]; then
  for folder in "$custom/Stages" "$custom/Characters"; do
    [[ ! -e "$folder" ]] || { printf 'Already exists: %s\n' "$folder" >&2; exit 1; }
    mkdir -p "$folder"
    created+=("$folder")
  done
  package chstage "$custom/Stages/spike-top.chstage"
  package chstage "$custom/Stages/Spike Group/spike-grouped.chstage"
  package chchar "$custom/Characters/Spike Group/spike-guitarist.chchar"
  mkdir -p "$custom/Stages/Spike Group/spike-unpacked"
  printf '{"format":"chstage/1","id":"spike.unpacked"}\n' > "$custom/Stages/Spike Group/spike-unpacked/manifest.json"
  magick -size 320x180 xc:magenta "$custom/Stages/Spike Group/spike-unpacked/preview.png"
fi
find "${created[@]}" | sort > "$output/custom-files.txt"

before="$(ls -t "$HOME/.clonehero/Logs" | sed -n 1p)"
HYPRLAND_INSTANCE_SIGNATURE="$(hyprctl instances -j | jq -r '.[0].instance')" WAYLAND_DISPLAY=wayland-1 \
  GH3_CLONE_HERO_DIR="$game_dir" GH3_SKIN_STAGE=gh3.dive \
  GH3_SKIN_PREVIEW_SONG="$HOME/.cache/gh3ref/charts/mycurse-native" GH3_SKIN_PREVIEW_WIDTH=1600 GH3_SKIN_PREVIEW_HEIGHT=900 \
  GH3_SKIN_SPIKE=textures GH3_SKIN_SPIKE_TEXTURES_DIR="$cache" GH3_SKIN_CAPTURE_TIMES=12,16 \
  GH3_SKIN_CAPTURE_DIR="$output" GH3_SKIN_SCREENSHOT="$output/selected.png" TMPDIR="$HOME/.cache/gh3ref/gpu" \
  bash "$project_dir/scripts/capture-preview.sh" || true
cp "$game_dir/MelonLoader/Latest.log" "$output/melon.log"
after="$(ls -t "$HOME/.clonehero/Logs" | sed -n 1p)"
[[ "$after" == "$before" ]] || cp "$HOME/.clonehero/Logs/$after" "$output/clonehero-session.log"
