#!/usr/bin/env bash
set -euo pipefail
export MISE_QUIET=1
export WAYLAND_DISPLAY="${WAYLAND_DISPLAY:-wayland-1}"

project_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
base_dir="$(realpath "${GH3_UNMODDED_DIR:-$HOME/games/clonehero-1.1.0.6142}")"
song_dir="$(realpath "${GH3_SKIN_PREVIEW_SONG:-$project_dir/charts/GH3 Combo Preview}")"
output_dir="${GH3_UNMODDED_FRAMES:-/tmp/opencode/gh3-unmodded}"
instance="$(hyprctl instances -j | jq -r '.[0].instance')"
mkdir -p "$output_dir"

is_preview_pid() {
  local pid="$1"
  [[ "$(readlink -f "/proc/$pid/cwd" 2>/dev/null || true)" == "$base_dir" ]] &&
    tr '\0' '\n' < "/proc/$pid/cmdline" 2>/dev/null | grep -Fqx -- "$song_dir"
}
preview_pids() {
  local entry
  for entry in /proc/[0-9]*; do
    if is_preview_pid "${entry##*/}"; then
      printf '%s\n' "${entry##*/}"
    fi
  done
}
declare -A existing_preview=()
while read -r pid; do
  existing_preview["$pid"]=1
done < <(preview_pids)
cleanup() {
  local pid
  kill -INT "${process:-}" 2>/dev/null || true
  while read -r pid; do
    if [[ -z "${existing_preview[$pid]:-}" ]]; then
      kill -KILL "$pid" 2>/dev/null || true
    fi
  done < <(preview_pids)
}
trap cleanup EXIT

(
  cd "$base_dir"
  env -u LD_PRELOAD LD_LIBRARY_PATH="$base_dir" DISPLAY="${DISPLAY:-:0}" WAYLAND_DISPLAY="${WAYLAND_DISPLAY:-wayland-1}" \
    timeout --signal=INT --kill-after=5s 38s ./clonehero \
    --song "$song_dir" --player Guitar,Expert \
    -screen-fullscreen 0 -screen-width 1600 -screen-height 900
) > "$output_dir/launch.log" 2>&1 &
process=$!

address=""
for ((attempt = 0; attempt < 15; attempt++)); do
  sleep 1
  while IFS=$'\t' read -r candidate pid; do
    if is_preview_pid "$pid"; then
      address="$candidate"
      break
    fi
  done < <(hyprctl -i "$instance" clients -j | jq -r '.[] | select(.class == "clonehero") | [.address, .pid] | @tsv')
  [[ -n "$address" ]] && break
done
if [[ -z "$address" ]]; then
  printf 'Could not find the unmodified Clone Hero window. See %s/launch.log\n' "$output_dir" >&2
  exit 1
fi

hyprctl -i "$instance" dispatch "hl.dsp.window.fullscreen({ action = 'unset', window = 'address:$address' })" >/dev/null
hyprctl -i "$instance" dispatch "hl.dsp.window.float({ action = 'set', window = 'address:$address' })" >/dev/null
hyprctl -i "$instance" dispatch "hl.dsp.window.resize({ x = 1600, y = 900, window = 'address:$address' })" >/dev/null

for ((frame = 0; frame < 48; frame++)); do
  sleep 0.5
  geometry="$(hyprctl -i "$instance" clients -j | jq -r --arg address "$address" '.[] | select(.address == $address) | "\(.at[0]),\(.at[1]) \(.size[0])x\(.size[1])"')"
  [[ -n "$geometry" ]] || break
  if [[ "$geometry" != *" 1600x900" ]]; then
    hyprctl -i "$instance" dispatch "hl.dsp.window.fullscreen({ action = 'unset', window = 'address:$address' })" >/dev/null
    hyprctl -i "$instance" dispatch "hl.dsp.window.float({ action = 'set', window = 'address:$address' })" >/dev/null
    hyprctl -i "$instance" dispatch "hl.dsp.window.resize({ x = 1600, y = 900, window = 'address:$address' })" >/dev/null
    sleep 0.2
    geometry="$(hyprctl -i "$instance" clients -j | jq -r --arg address "$address" '.[] | select(.address == $address) | "\(.at[0]),\(.at[1]) \(.size[0])x\(.size[1])"')"
  fi
  grim -g "$geometry" "$output_dir/frame-$(printf '%02d' "$frame").png"
done
printf 'Captured unmodified Clone Hero frames in %s\n' "$output_dir"
