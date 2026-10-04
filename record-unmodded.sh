#!/usr/bin/env bash
set -euo pipefail
export MISE_QUIET=1
export WAYLAND_DISPLAY="${WAYLAND_DISPLAY:-wayland-1}"

project_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
base_dir="$(realpath "${GH3_UNMODDED_DIR:-$HOME/games/clonehero-1.1.0.6142}")"
song_dir="$(realpath "${GH3_SKIN_PREVIEW_SONG:-$HOME/games/clonehero-gh3-melon/PlayerData/Songs/GH3 Skin Preview}")"
instance="$(hyprctl instances -j | jq -r '.[0].instance')"
output="${GH3_STOCK_VIDEO:-$project_dir/preview-stock-gameplay.mp4}"
seconds="${GH3_STOCK_VIDEO_SECONDS:-15}"
trim="${GH3_STOCK_VIDEO_TRIM:-13.5}"
display_number="$((100 + $$ % 10000))"
log="$(mktemp /tmp/opencode/gh3-stock-video.XXXXXX.log)"
xlog="$(mktemp /tmp/opencode/gh3-stock-xwayland.XXXXXX.log)"
frame="$(mktemp /tmp/opencode/gh3-stock-frame.XXXXXX.png)"
raw="$(mktemp /tmp/opencode/gh3-stock-raw.XXXXXX.mp4)"
clean="$(mktemp /tmp/opencode/gh3-stock-clean.XXXXXX.mp4)"

cleanup() {
  if [[ -n "${recorder_pid:-}" ]]; then
    kill -INT "$recorder_pid" 2>/dev/null || true
    wait "$recorder_pid" 2>/dev/null || true
  fi
  for entry in /proc/[0-9]*; do
    pid="${entry##*/}"
    [[ "$(readlink "$entry/cwd" 2>/dev/null || true)" == "$base_dir" ]] || continue
    tr '\0' '\n' < "$entry/environ" 2>/dev/null | grep -Fqx "DISPLAY=:$display_number" || continue
    kill -TERM "$pid" 2>/dev/null || true
  done
  sleep 0.5
  for entry in /proc/[0-9]*; do
    pid="${entry##*/}"
    [[ "$(readlink "$entry/cwd" 2>/dev/null || true)" == "$base_dir" ]] || continue
    tr '\0' '\n' < "$entry/environ" 2>/dev/null | grep -Fqx "DISPLAY=:$display_number" || continue
    kill -KILL "$pid" 2>/dev/null || true
  done
  [[ -z "${game_pid:-}" ]] || wait "$game_pid" 2>/dev/null || true
  if [[ -n "${xwayland_pid:-}" ]]; then
    kill -TERM "$xwayland_pid" 2>/dev/null || true
    wait "$xwayland_pid" 2>/dev/null || true
  fi
  rm -f -- "$frame" "$raw" "$clean"
}
trap cleanup EXIT

Xwayland ":$display_number" -geometry 1600x900 > "$xlog" 2>&1 &
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

(
  cd "$base_dir"
  exec env -u LD_PRELOAD LD_LIBRARY_PATH="$base_dir" WAYLAND_DISPLAY='' DISPLAY=":$display_number" \
    ./clonehero --song "$song_dir" --player Guitar,Expert \
    -screen-fullscreen 0 -screen-width 1600 -screen-height 900
) > "$log" 2>&1 &
game_pid=$!

ready=false
for ((attempt = 0; attempt < 50; attempt++)); do
  kill -0 "$game_pid" 2>/dev/null || break
  if DISPLAY=":$display_number" ffmpeg -hide_banner -loglevel error -nostdin -y \
      -f x11grab -framerate 1 -video_size 1600x900 -draw_mouse 0 -i ":$display_number.0" \
      -frames:v 1 "$frame" >/dev/null 2>&1; then
    brightness="$(magick "$frame" -crop 800x150+400+730 +repage -colorspace gray -format '%[fx:mean]' info: 2>/dev/null || true)"
    if [[ -n "$brightness" ]] && awk -v value="$brightness" 'BEGIN { exit !(value > 0.04) }'; then
      ready=true
      break
    fi
  fi
  sleep 0.2
done
if [[ "$ready" != true ]]; then
  printf 'Stock gameplay did not appear. See %s and %s\n' "$log" "$xlog" >&2
  exit 1
fi
sleep 0.5

DISPLAY=":$display_number" ffmpeg -hide_banner -loglevel error -nostdin -y \
  -f x11grab -framerate 60 -video_size 1600x900 -draw_mouse 0 -i ":$display_number.0" \
  -c:v libx264 -preset veryfast -crf 22 -pix_fmt yuv420p "$raw" > "$log.recorder" 2>&1 &
recorder_pid=$!
sleep "$seconds"
kill -INT "$recorder_pid" 2>/dev/null || true
wait "$recorder_pid" || true
recorder_pid=""

if [[ ! -s "$raw" ]]; then
  printf 'Stock video capture failed. See %s.recorder\n' "$log" >&2
  exit 1
fi
ffmpeg -hide_banner -loglevel error -nostdin -y -i "$raw" -t "$trim" \
  -c:v libx264 -preset medium -crf 18 -pix_fmt yuv420p -movflags +faststart "$clean"
mv -- "$clean" "$output"
printf 'Recorded %s\n' "$output"
