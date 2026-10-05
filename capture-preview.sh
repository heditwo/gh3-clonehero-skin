#!/usr/bin/env bash
set -euo pipefail
export MISE_QUIET=1

project_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
game_dir="$(realpath "${GH3_CLONE_HERO_DIR:-$HOME/games/clonehero-gh3-melon}")"
song_dir="$(realpath "${GH3_SKIN_PREVIEW_SONG:-$project_dir/charts/GH3 Combo Preview}")"
instance="$(hyprctl instances -j | jq -r '.[0].instance')"
capture_after="${GH3_SKIN_CAPTURE_AFTER:-8}"
width="${GH3_SKIN_PREVIEW_WIDTH:-1600}"
height="${GH3_SKIN_PREVIEW_HEIGHT:-900}"
output="${GH3_SKIN_SCREENSHOT:-$project_dir/preview.png}"
temporary_root="${TMPDIR:-/tmp}/opencode"
mkdir -p "$temporary_root" "$(dirname -- "$output")"
if [[ ! "$width" =~ ^[0-9]+$ || ! "$height" =~ ^[0-9]+$ ]] ||
  (( width < 640 || height < 480 )); then
  printf 'Preview resolution must be at least 640 x 480\n' >&2
  exit 1
fi
display_number="$((100 + $$ % 10000))"
log="$(mktemp "$temporary_root/gh3-preview.XXXXXX.log")"
xlog="$(mktemp "$temporary_root/gh3-xwayland.XXXXXX.log")"
frames="${GH3_SKIN_CAPTURE_DIR:-$(mktemp -d "$temporary_root/gh3-preview-frames.XXXXXX")}"
mkdir -p "$frames"
frames="$(realpath "$frames")"
capture_times="${GH3_SKIN_CAPTURE_TIMES:-$(awk -v start="$capture_after" 'BEGIN { for (i = 0; i < 7; i++) printf "%s%.1f", i ? "," : "", start + i }')}"
read -r expected duration < <(python3 - "$capture_times" <<'PY'
import math
import sys
times = sorted(set(float(value) for value in sys.argv[1].split(',') if value))
if not times or any(not math.isfinite(value) or value <= 0 for value in times):
    raise SystemExit("Capture times must be finite positive seconds")
print(len(times), math.ceil(times[-1]) + 30)
PY
)
if compgen -G "$frames/frame-*.png" >/dev/null; then
  printf 'Capture folder must contain no frame-*.png files: %s\n' "$frames" >&2
  exit 1
fi

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
  if [[ -z "${GH3_SKIN_CAPTURE_DIR:-}" ]]; then
    rm -rf -- "$frames"
  fi
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

WAYLAND_DISPLAY='' DISPLAY=":$display_number" GH3_SKIN_PREVIEW_SONG="$song_dir" GH3_SKIN_CAPTURE_AFTER=0 \
  GH3_SKIN_CAPTURE_TIMES="$capture_times" GH3_SKIN_CAPTURE_DIR="$frames" \
  "$project_dir/run-gh3-skin.sh" --preview > "$log" 2>&1 &
game_pid=$!
declare -A checked=()
selected=""
for ((attempt = 0; attempt < duration * 5; attempt++)); do
  sleep 0.2
  kill -0 "$game_pid" 2>/dev/null || break
  for frame in "$frames"/frame-*.png; do
    [[ -f "$frame" && -z "${checked[$frame]:-}" ]] || continue
    brightness="$(magick "$frame" -gravity southeast -crop 40%x40%+0+0 +repage -colorspace gray -format '%[fx:mean]' info: 2>/dev/null || true)"
    [[ -n "$brightness" ]] || continue
    checked["$frame"]=1
    if [[ -z "$selected" ]] && awk -v value="$brightness" 'BEGIN { exit !(value > 0.01) }'; then
      selected="$frame"
    fi
  done
  if [[ -n "$selected" ]] && { [[ -z "${GH3_SKIN_CAPTURE_TIMES:-}" ]] || (( ${#checked[@]} == expected )); }; then
    cp -- "$selected" "$output"
    printf 'Captured %s (%s x %s). Loader log: %s/MelonLoader/Latest.log\n' "$output" "$width" "$height" "$game_dir"
    exit 0
  fi
done

printf 'Preview failed. See %s and %s/MelonLoader/Latest.log\n' "$log" "$game_dir" >&2
exit 1
