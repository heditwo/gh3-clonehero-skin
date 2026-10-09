#!/usr/bin/env bash
set -euo pipefail
export MISE_QUIET=1
export WAYLAND_DISPLAY="${WAYLAND_DISPLAY:-wayland-1}"

project_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
game_dir="$(realpath "${GH3_CLONE_HERO_DIR:-$HOME/games/clonehero-gh3-melon}")"
song_dir="$(realpath "${GH3_SKIN_PREVIEW_SONG:-$project_dir/charts/GH3 Combo Preview}")"
instance="$(hyprctl instances -j | jq -r '.[0].instance')"
output="${GH3_SKIN_VIDEO:-$project_dir/preview-gameplay.mp4}"
seconds="${GH3_SKIN_VIDEO_SECONDS:-15}"
trim="${GH3_SKIN_VIDEO_TRIM:-13.5}"
framerate="${GH3_SKIN_VIDEO_FPS:-60}"
width="${GH3_SKIN_PREVIEW_WIDTH:-1600}"
height="${GH3_SKIN_PREVIEW_HEIGHT:-900}"
if [[ ! "$width" =~ ^[0-9]+$ || ! "$height" =~ ^[0-9]+$ ]] ||
  (( width < 640 || height < 480 || width % 2 || height % 2 )); then
  printf 'Video dimensions must be even and at least 640 x 480\n' >&2
  exit 1
fi
display_number="$((100 + $$ % 10000))"
temporary_root="${TMPDIR:-/tmp}/opencode"
mkdir -p "$temporary_root" "$(dirname -- "$output")"
log="$(mktemp "$temporary_root/gh3-video.XXXXXX.log")"
xlog="$(mktemp "$temporary_root/gh3-xwayland.XXXXXX.log")"
raw="$(mktemp "$temporary_root/gh3-video-raw.XXXXXX.mp4")"
clean="$(mktemp "$temporary_root/gh3-video-clean.XXXXXX.mp4")"

cleanup() {
  if [[ -n "${recorder_pid:-}" ]]; then
    kill -INT "$recorder_pid" 2>/dev/null || true
    wait "$recorder_pid" 2>/dev/null || true
  fi
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
  rm -f -- "$raw" "$clean"
}
trap cleanup EXIT

WAYLAND_DISPLAY="${WAYLAND_DISPLAY:-wayland-1}" Xwayland ":$display_number" -noreset -geometry "${width}x${height}" > "$xlog" 2>&1 &
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
hyprctl -i "$instance" dispatch "hl.dsp.window.float({ action = 'on', window = 'address:$address' })" >/dev/null
hyprctl -i "$instance" dispatch "hl.dsp.window.resize({ x = $width, y = $height, window = 'address:$address' })" >/dev/null
hyprctl -i "$instance" dispatch "hl.dsp.window.move({ x = ${GH3_SKIN_VIDEO_X:-32}, y = ${GH3_SKIN_VIDEO_Y:-64}, window = 'address:$address' })" >/dev/null
for ((attempt = 0; attempt < 30; attempt++)); do
  geometry="$(DISPLAY=":$display_number" xrandr --query 2>/dev/null | awk '/^Screen / { gsub(",", "", $10); print $8 "x" $10 }')"
  [[ "$geometry" != "${width}x${height}" ]] || break
  sleep 0.1
done
if [[ "$geometry" != "${width}x${height}" ]]; then
  printf 'Capture display has size %s; expected %sx%s\n' "$geometry" "$width" "$height" >&2
  exit 1
fi

started="$(date +%s)"
WAYLAND_DISPLAY='' DISPLAY=":$display_number" GH3_SKIN_PREVIEW_SONG="$song_dir" \
  "$project_dir/run-gh3-skin.sh" --preview > "$log" 2>&1 &
game_pid=$!
ready=false
for ((attempt = 0; attempt < 150; attempt++)); do
  kill -0 "$game_pid" 2>/dev/null || break
  if [[ -f "$game_dir/MelonLoader/Latest.log" ]] &&
    [[ "$(stat -c %Y "$game_dir/MelonLoader/Latest.log")" -ge "$started" ]] &&
    rg -q 'GH3 gameplay scene loaded' "$game_dir/MelonLoader/Latest.log"; then
    ready=true
    break
  fi
  sleep 0.2
done
if [[ "$ready" != true ]]; then
  printf 'Gameplay did not start. See %s\n' "$log" >&2
  exit 1
fi
sleep 0.5
read -r x y < <(hyprctl -i "$instance" clients -j | jq -r --arg address "$address" '.[] | select(.address == $address) | "\(.at[0]) \(.at[1])"')

audio=()
case "${GH3_SKIN_VIDEO_AUDIO:-}" in
  "") ;;
  game)
    application=""
    for ((attempt = 0; attempt < 50; attempt++)); do
      application="$(gpu-screen-recorder --list-application-audio 2>/dev/null | rg -i -m1 'clone ?hero' || true)"
      [[ -z "$application" ]] || break
      sleep 0.2
    done
    if [[ -z "$application" ]]; then
      printf 'Clone Hero audio stream not found\n' >&2
      exit 1
    fi
    audio=(-a "app:$application")
    ;;
  *) audio=(-a "$GH3_SKIN_VIDEO_AUDIO") ;;
esac
gpu-screen-recorder -w region -region "${width}x${height}+${x}+${y}" "${audio[@]}" \
  -f "$framerate" -fm cfr -cursor no -k h264 -q very_high -o "$raw" > "$log.recorder" 2>&1 &
recorder_pid=$!
sleep "$seconds"
kill -INT "$recorder_pid" 2>/dev/null || true
wait "$recorder_pid" || true
recorder_pid=""

if [[ ! -s "$raw" ]]; then
  printf 'Video capture failed. See %s.recorder\n' "$log" >&2
  exit 1
fi
ffmpeg -hide_banner -loglevel error -nostdin -y -i "$raw" -t "$trim" \
  -c:v libx264 -preset medium -crf 18 -pix_fmt yuv420p -c:a aac -b:a 192k -movflags +faststart "$clean"
mv -- "$clean" "$output"
cp -- "$game_dir/MelonLoader/Latest.log" "$output.log"
GH3_SKIN_VIDEO_BACKEND=gpu-screen-recorder GH3_SKIN_VIDEO_REGION="${width}x${height}+${x}+${y}" \
  python3 "$project_dir/tools/capture-manifest.py" "$output" "$song_dir" "$game_dir/Mods/Gh3Skin.dll"
printf 'Recorded %s\n' "$output"
