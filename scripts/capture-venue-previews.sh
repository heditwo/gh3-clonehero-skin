#!/usr/bin/env bash
# Make the venue preview images (assets/previews/<id>.png, 512 x 288) from in-game captures of GH3's intro, before the
# highway and the HUD come in. Each capture is one game run under the record lock. Then run the `packages` step of
# scripts/regenerate.sh (or python -m pipeline.package.build --no-library) to put the images into the packages.
# Usage: GH3_CLONE_HERO_DIR=<game copy> scripts/capture-venue-previews.sh [venue-id ...]
set -euo pipefail
export MISE_QUIET=1

project="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
song="${GH3_SKIN_PREVIEW_SONG:-$HOME/.cache/gh3ref/charts/mycurse-native}"
work="${GH3_PREVIEW_WORK:-$HOME/.cache/gh3ref/venue-previews}"
venues=("$@")
(( ${#venues[@]} )) || venues=(gh3.artdeco gh3.budokan gh3.credits gh3.dive gh3.hell gh3.party gh3.prison gh3.soundcheck gh3.video gh3.wikker example.studio)
# Song times of the four frames; the chosen frame shows the venue best (the intro cameras differ for each venue).
times="-7,-5.5,-4,-3.1"
declare -A frame=([gh3.artdeco]=00 [gh3.dive]=00)
mkdir -p "$project/assets/previews"
for venue in "${venues[@]}"; do
  output="$work/$venue"
  for attempt in 1 2 3; do
    rm -rf -- "$output"
    mkdir -p "$output"
    flock /tmp/opencode/gh3-record.lock env GH3_SKIN_STAGE="$venue" GH3_SKIN_PREVIEW_SONG="$song" GH3_SKIN_PREVIEW_WIDTH=1600 \
      GH3_SKIN_PREVIEW_HEIGHT=900 GH3_SKIN_CAPTURE_CLOCK=song GH3_SKIN_CAPTURE_TIMES="$times" GH3_SKIN_CAPTURE_DIR="$output" \
      GH3_SKIN_SCREENSHOT="$output.png" bash "$project/scripts/capture-preview.sh" > "$output.log" 2>&1 || true
    [[ -f "$output/frame-03.png" ]] && break
  done
  source="$output/frame-${frame[$venue]:-02}.png"
  [[ -f "$source" ]] || { printf 'No capture for %s. See %s.log\n' "$venue" "$output" >&2; exit 1; }
  magick "$source" -filter Lanczos -resize '512x288!' -strip -dither FloydSteinberg -colors 256 \
    -define png:compression-level=9 "PNG8:$project/assets/previews/$venue.png"
  printf '%s: %s\n' "$venue" "$project/assets/previews/$venue.png"
done
