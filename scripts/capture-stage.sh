#!/usr/bin/env bash
# Capture timed Clone Hero frames of one stage pack without a desktop session.
# Usage: scripts/capture-stage.sh <stage-id> <output-dir> [song-dir] [times] [seconds]
set -euo pipefail
export MISE_QUIET=1

project="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
stage="${1:?stage id, for example gh3.party}"
output="$(realpath -m "${2:?output directory}")"
song="$(realpath "${3:-$project/charts/GH3 Stage Preview}")"
times="${4:-4,8,12,16}"
seconds="${5:-30}"
game="${GH3_CLONE_HERO_DIR:-/tmp/opencode/gh3-crowd-runtime/game}"
xvfb="${GH3_XVFB:-/tmp/opencode/gh3-stage-research/xvfb/usr/bin/Xvfb}"
audio="$project/assets/event-audit/behavior-work/with-runtime-audio.sh"
display="${GH3_CAPTURE_DISPLAY:-:18294}"
width="${GH3_SKIN_PREVIEW_WIDTH:-1280}"
height="${GH3_SKIN_PREVIEW_HEIGHT:-720}"

if [[ -e "$output" ]] && compgen -G "$output/frame-*.png" > /dev/null; then
  printf 'Output already has frames: %s\n' "$output" >&2
  exit 1
fi
mkdir -p "$output"
cp "$project/src/bin/Release/net6.0/Gh3Skin.dll" "$game/Mods/Gh3Skin.dll"
rm -f "$game/Mods/BehaviorProbe.dll"
while IFS= read -r -d '' file; do
  target="$game/Mods/GH3Skin/assets/${file#"$project/assets/"}"
  if [[ ! -e "$target" ]]; then
    mkdir -p "$(dirname "$target")"
    ln -sfn "$file" "$target"
  fi
done < <(find "$project/assets/libraries/gh3-band-v1" "$project/assets/stages/$stage" "$project/assets/stages/events" -type f -print0)

"$xvfb" "$display" -screen 0 "${width}x${height}x24" -nolisten tcp > "$output/xvfb.log" 2>&1 &
server=$!
cleanup() {
  pkill -KILL -f "clonehero.*--song $song" 2> /dev/null || true
  kill "$server" 2> /dev/null || true
  wait "$server" 2> /dev/null || true
}
trap cleanup EXIT
sleep 2

sha256sum "$game/Mods/Gh3Skin.dll" "$project/assets/libraries/gh3-band-v1/render/"*.bundle > "$output/build.sha256"
# MelonLoader downloads debug symbols over HTTPS; its socket threads have crashed the CLR. Set GH3_CAPTURE_NETWORK=1 to allow network access.
isolate=(unshare -rn)
[[ -n "${GH3_CAPTURE_NETWORK:-}" ]] && isolate=()
set +e
# Xvfb has no GPU access; use record-preview.sh from a desktop session for full-speed video.
env LIBGL_ALWAYS_SOFTWARE=1 LP_NUM_THREADS="${GH3_CAPTURE_THREADS:-8}" DISPLAY="$display" WAYLAND_DISPLAY='' \
  XDG_CONFIG_HOME="$game/../config" GH3_CLONE_HERO_DIR="$game" GH3_SKIN_PREVIEW_SONG="$song" \
  GH3_SKIN_PREVIEW_WIDTH="$width" GH3_SKIN_PREVIEW_HEIGHT="$height" GH3_SKIN_STAGE="$stage" \
  GH3_SKIN_CAMERA_SHOT="${GH3_SKIN_CAMERA_SHOT:-}" GH3_SKIN_CAPTURE_TIMES="$times" GH3_SKIN_CAPTURE_DIR="$output" GH3_SKIN_CAPTURE_CLOCK="${GH3_SKIN_CAPTURE_CLOCK:-song}" \
  timeout -k 5 "$seconds" "${isolate[@]}" dbus-run-session -- bash "$audio" bash "$project/run-gh3-skin.sh" --preview \
  --melonloader.agfoffline -force-glcore -logFile "$output/unity.log" > "$output/player.log" 2>&1
status=$?
set -e
printf 'exit %s\n' "$status" >> "$output/player.log"
grep -E 'GH3 (stage|authored|crowd)|Exception|Error' "$game/MelonLoader/Latest.log" 2> /dev/null | tail -20 > "$output/melon-summary.log" || true
ls "$output"/frame-*.png 2> /dev/null || printf 'No frames captured. See %s\n' "$output" >&2
