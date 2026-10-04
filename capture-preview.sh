#!/usr/bin/env bash
set -euo pipefail
export MISE_QUIET=1

project_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
game_dir="$(realpath "${GH3_CLONE_HERO_DIR:-$HOME/games/clonehero-gh3-melon}")"
song_dir="$(realpath "${GH3_SKIN_PREVIEW_SONG:-$game_dir/PlayerData/Songs/GH3 Skin Preview}")"
instance="$(hyprctl instances -j | jq -r '.[0].instance')"
capture_after="${GH3_SKIN_CAPTURE_AFTER:-8}"
duration="$(awk -v seconds="$capture_after" 'BEGIN { printf "%.0f", seconds + 20 }')"
display_number="$((100 + $$ % 10000))"
log="$(mktemp /tmp/opencode/gh3-preview.XXXXXX.log)"
xlog="$(mktemp /tmp/opencode/gh3-xwayland.XXXXXX.log)"
resized="$(mktemp /tmp/opencode/gh3-preview.XXXXXX.png)"
frames="$(mktemp -d /tmp/opencode/gh3-preview-frames.XXXXXX)"
capture_times="$(awk -v start="$capture_after" 'BEGIN { for (i = 0; i < 7; i++) printf "%s%.1f", i ? "," : "", start + i }')"

cleanup() {
  for entry in /proc/[0-9]*; do
    pid="${entry##*/}"
    [[ "$(readlink "$entry/cwd" 2>/dev/null || true)" == "$game_dir" ]] || continue
    tr '\0' '\n' < "$entry/environ" 2>/dev/null | grep -Fqx "DISPLAY=:$display_number" || continue
    kill -TERM "$pid" 2>/dev/null || true
  done
  sleep 0.5
  for entry in /proc/[0-9]*; do
    pid="${entry##*/}"
    [[ "$(readlink "$entry/cwd" 2>/dev/null || true)" == "$game_dir" ]] || continue
    tr '\0' '\n' < "$entry/environ" 2>/dev/null | grep -Fqx "DISPLAY=:$display_number" || continue
    kill -KILL "$pid" 2>/dev/null || true
  done
  [[ -z "${game_pid:-}" ]] || wait "$game_pid" 2>/dev/null || true
  if [[ -n "${xwayland_pid:-}" ]]; then
    kill -TERM "$xwayland_pid" 2>/dev/null || true
    wait "$xwayland_pid" 2>/dev/null || true
  fi
  rm -f -- "$resized"
  rm -rf -- "$frames"
}
trap cleanup EXIT

WAYLAND_DISPLAY="${WAYLAND_DISPLAY:-wayland-1}" Xwayland ":$display_number" -geometry 1600x900 > "$xlog" 2>&1 &
xwayland_pid=$!
ready=false
for ((attempt = 0; attempt < 30; attempt++)); do
  if DISPLAY=":$display_number" xrandr --query >/dev/null 2>&1; then
    ready=true
    break
  fi
  kill -0 "$xwayland_pid" 2>/dev/null || break
  sleep 0.2
done
if [[ "$ready" != true ]]; then
  printf 'Xwayland did not start. See %s\n' "$xlog" >&2
  exit 1
fi

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
hyprctl -i "$instance" dispatch "hl.dsp.window.float({ action = 'set', window = 'address:$address' })" >/dev/null

WAYLAND_DISPLAY='' DISPLAY=":$display_number" GH3_SKIN_PREVIEW_SONG="$song_dir" GH3_SKIN_CAPTURE_AFTER=1000 \
  GH3_SKIN_CAPTURE_TIMES="$capture_times" GH3_SKIN_CAPTURE_DIR="$frames" \
  "$project_dir/run-gh3-skin.sh" --preview > "$log" 2>&1 &
game_pid=$!
declare -A checked=()
for ((attempt = 0; attempt < duration * 5; attempt++)); do
  sleep 0.2
  for frame in "$frames"/frame-*.png; do
    [[ -f "$frame" && -z "${checked[$frame]:-}" ]] || continue
    brightness="$(magick "$frame" -gravity southeast -crop 40%x40%+0+0 +repage -colorspace gray -format '%[fx:mean]' info: 2>/dev/null || true)"
    [[ -n "$brightness" ]] || continue
    checked["$frame"]=1
    if awk -v value="$brightness" 'BEGIN { exit !(value > 0.01) }'; then
      ffmpeg -hide_banner -loglevel error -nostdin -y -i "$frame" \
        -vf 'scale=1600:900:force_original_aspect_ratio=increase:flags=lanczos,crop=1600:900' \
        -frames:v 1 "$resized"
      mv -- "$resized" "$project_dir/preview.png"
      printf 'Captured %s (1600 x 900). Loader log: %s/MelonLoader/Latest.log\n' "$project_dir/preview.png" "$game_dir"
      exit 0
    fi
  done
done

printf 'Preview failed. See %s and %s/MelonLoader/Latest.log\n' "$log" "$game_dir" >&2
exit 1
