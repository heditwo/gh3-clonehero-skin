#!/usr/bin/env bash
# Measure the load times of the user flows (docs/VERIFICATION.md, load times) on a test game with GH3_SKIN_TIMING=1.
# Usage: scripts/measure-load.sh <output folder> [runs] [flow ...]
#   Flows: warm-menu, warm-song, cold-menu, cold-song, picker, reload (the same stage again), reload-random (memory over
#   9 songs on random stages). Default: all.
#   GH3_CLONE_HERO_DIR selects the game. The cold flows delete UserData/GH3Skin/Cache of that game.
#   The song flows use GH3_SKIN_PREVIEW_SONG (default: the native My Curse chart) and GH3_SKIN_STAGE (default gh3.dive).
# Each run takes the record lock. Summarize the results with tools/summarize-load.py <output folder>.
# Repeatable captures: script seed 1 unless the caller sets one (an empty value gives a random seed).
export GH3_SKIN_SEED="${GH3_SKIN_SEED-1}"
set -euo pipefail
export MISE_QUIET=1

project_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
game_dir="$(realpath "${GH3_CLONE_HERO_DIR:-$HOME/games/clonehero-gh3-melon}")"
output="${1:?output folder}"
runs="${2:-3}"
shift $(( $# < 2 ? $# : 2 ))
flows=("$@")
(( ${#flows[@]} )) || flows=(warm-menu warm-song cold-menu cold-song picker reload reload-random)
song="${GH3_SKIN_PREVIEW_SONG:-$HOME/.cache/gh3ref/charts/mycurse-native}"
stage="${GH3_SKIN_STAGE:-gh3.dive}"
cache="$game_dir/UserData/GH3Skin/Cache"
mkdir -p "$output"

measure() {
  local name="$1"
  shift
  for attempt in 1 2 3; do
    if flock /tmp/opencode/gh3-record.lock uv run --with python-xlib --with pillow python "$project_dir/tools/measure-load.py" \
      --game "$game_dir" --out "$output/$name" --timeout 300 "$@"; then
      return 0
    fi
    printf 'Run %s failed (attempt %s)\n' "$name" "$attempt" >&2
  done
  return 1
}

for flow in "${flows[@]}"; do
  for ((run = 1; run <= runs; run++)); do
    case "$flow" in
      warm-menu) measure "$flow-$run" "wait:frame 30 of scene Main Menu" "seen:package imports finished" sleep:3 ;;
      cold-menu) rm -rf "$cache"; measure "$flow-$run" "wait:frame 30 of scene Main Menu" "seen:package imports finished" sleep:2 ;;
      warm-song) measure "$flow-$run" --song "$song" --env "GH3_SKIN_STAGE=$stage" "wait:song time 0" sleep:1 ;;
      cold-song) rm -rf "$cache"; measure "$flow-$run" --song "$song" --env "GH3_SKIN_STAGE=$stage" "wait:timing.*stage loaded" sleep:1 ;;
      picker) measure "$flow-$run" --env "GH3_SKIN_PICKER_TEST=ready;wait 1;open;wait 2;close;quit" "seen:picker preview of" sleep:3 ;;
      reload) measure "$flow-$run" --song "$song" --env "GH3_SKIN_STAGE=$stage" --env GH3_SKIN_TIMING_RELOADS=4 \
        "wait:song time 0" "wait:song time 0" "wait:song time 0" "wait:song time 0" "wait:song time 0" sleep:1 ;;
      reload-random) measure "$flow-$run" --song "$song" --env GH3_SKIN_STAGE=random --env GH3_SKIN_TIMING_RELOADS=8 \
        "wait:song time 0" "wait:song time 0" "wait:song time 0" "wait:song time 0" "wait:song time 0" \
        "wait:song time 0" "wait:song time 0" "wait:song time 0" "wait:song time 0" sleep:1 ;;
      *) printf 'Unknown flow %s\n' "$flow" >&2; exit 2 ;;
    esac
  done
done
