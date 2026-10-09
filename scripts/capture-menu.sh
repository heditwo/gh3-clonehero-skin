#!/usr/bin/env bash
# Start Clone Hero in its menus in a nested Xwayland window and run the picker test steps of GH3_SKIN_PICKER_TEST
# (src/Menu/PickerTest.cs), for example "ready;wait 3;open;capture /tmp/picker.png;quit". Run it under the record lock.
set -euo pipefail
export MISE_QUIET=1

project_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
game_dir="$(realpath "${GH3_CLONE_HERO_DIR:-$HOME/games/clonehero-gh3-melon}")"
instance="$(hyprctl instances -j | jq -r '.[0].instance')"
width="${GH3_SKIN_PREVIEW_WIDTH:-1600}"
height="${GH3_SKIN_PREVIEW_HEIGHT:-900}"
seconds="${GH3_SKIN_MENU_SECONDS:-120}"
temporary_root="${TMPDIR:-/tmp}/opencode"
mkdir -p "$temporary_root"
if [[ -z "${GH3_SKIN_PICKER_TEST:-}" ]]; then
  printf 'Set GH3_SKIN_PICKER_TEST, for example "ready;wait 3;open;capture /tmp/picker.png;quit"\n' >&2
  exit 1
fi
display_number="$((100 + $$ % 10000))"
log="$(mktemp "$temporary_root/gh3-menu.XXXXXX.log")"
xlog="$(mktemp "$temporary_root/gh3-xwayland.XXXXXX.log")"

# Clone Hero under MelonLoader starts a second clonehero process that stays after the game quits; stop all processes of
# this game folder on this display.
cleanup() {
  for signal in TERM KILL; do
    for entry in /proc/[0-9]*; do
      pid="${entry##*/}"
      [[ "$(readlink "$entry/cwd" 2>/dev/null || true)" == "$game_dir" ]] || continue
      tr '\0' '\n' < "$entry/environ" 2>/dev/null | grep -Fqx "DISPLAY=:$display_number" || continue
      kill "-$signal" "$pid" 2>/dev/null || true
    done
    sleep 0.5
  done
  [[ -z "${input_pid:-}" ]] || kill "$input_pid" 2>/dev/null || true
  [[ -z "${game_pid:-}" ]] || wait "$game_pid" 2>/dev/null || true
  if [[ -n "${xwayland_pid:-}" ]]; then
    kill -TERM "$xwayland_pid" 2>/dev/null || true
    wait "$xwayland_pid" 2>/dev/null || true
  fi
}
trap cleanup EXIT

WAYLAND_DISPLAY="${WAYLAND_DISPLAY:-wayland-1}" Xwayland ":$display_number" -noreset -geometry "${width}x${height}" > "$xlog" 2>&1 &
xwayland_pid=$!
for ((attempt = 0; attempt < 30; attempt++)); do
  DISPLAY=":$display_number" xrandr --query >/dev/null 2>&1 && break
  kill -0 "$xwayland_pid" 2>/dev/null || break
  sleep 0.2
done
address=""
for ((attempt = 0; attempt < 30; attempt++)); do
  address="$(hyprctl -i "$instance" clients -j | jq -r --argjson pid "$xwayland_pid" '.[] | select(.pid == $pid) | .address')"
  [[ -z "$address" ]] || break
  sleep 0.2
done
if [[ -z "$address" ]]; then
  printf 'Xwayland window did not appear. See %s\n' "$xlog" >&2
  exit 1
fi
hyprctl -i "$instance" dispatch "hl.dsp.window.float({ action = 'on', window = 'address:$address' })" >/dev/null
hyprctl -i "$instance" dispatch "hl.dsp.window.resize({ x = $width, y = $height, window = 'address:$address' })" >/dev/null

: > "$game_dir/MelonLoader/Latest.log"
WAYLAND_DISPLAY='' DISPLAY=":$display_number" GH3_CLONE_HERO_DIR="$game_dir" \
  "$project_dir/run-gh3-skin.sh" -screen-fullscreen 0 -screen-width "$width" -screen-height "$height" > "$log" 2>&1 &
game_pid=$!
# GH3_SKIN_MENU_INPUT: a command that runs on the nested display while the game runs (for example XTest mouse clicks).
if [[ -n "${GH3_SKIN_MENU_INPUT:-}" ]]; then
  DISPLAY=":$display_number" bash -c "$GH3_SKIN_MENU_INPUT" >> "$log" 2>&1 &
  input_pid=$!
fi
for ((attempt = 0; attempt < seconds * 5; attempt++)); do
  sleep 0.2
  kill -0 "$game_pid" 2>/dev/null || break
  if grep -q "GH3 picker test: quit" "$game_dir/MelonLoader/Latest.log" 2>/dev/null; then
    sleep 2
    break
  fi
done
printf 'Menu run finished. Loader log: %s/MelonLoader/Latest.log; game output: %s\n' "$game_dir" "$log"
