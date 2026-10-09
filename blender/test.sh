#!/usr/bin/env bash
# Build the add-on and run the headless Blender test with each Blender given (default: blender-clean).
# Usage: bash blender/test.sh [OUTPUT_DIR] [BLENDER ...]
# The test installs the extension into a temporary Blender user folder, never into the user's Blender setup.
# CHSTAGE_TEST_VENUE: a venue package for the round-trip check (default: assets/packages/gh3.credits.chstage if present).
# CHSTAGE_TEST_RIG: a GH3 rig GLB for the import fix check.
# CHSTAGE_TEST_LIBRARY: the library package for the character test (default: assets/packages/gh3-band-v1.chlib if
# present; an empty value turns the character test off).
set -euo pipefail
export MISE_QUIET=1
here="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
output="${1:-${TMPDIR:-/tmp}/chstage-addon-test}"
shift || true
blenders=("$@")
(( ${#blenders[@]} )) || blenders=(blender-clean)
rig="${CHSTAGE_TEST_RIG:-}"
venue="${CHSTAGE_TEST_VENUE-$here/../assets/packages/gh3.credits.chstage}"
[[ -f "$venue" ]] || venue=""
library="${CHSTAGE_TEST_LIBRARY-$here/../assets/packages/gh3-band-v1.chlib}"
[[ -e "$library" ]] || library=""

archive="$(python3 "$here/build.py" --output "$output/dist")"
status=0
for blender in "${blenders[@]}"; do
  name="$(basename "$blender")"
  run="$output/$name"
  rm -rf "$run"; mkdir -p "$run/user"
  BLENDER_USER_RESOURCES="$run/user" "$blender" --background --factory-startup --offline-mode --python-exit-code 1 \
    --python "$here/tests/blender_test.py" -- "$archive" "$run" "$rig" "$venue" > "$run/blender.log" 2>&1 || status=1
  grep -E '^CHSTAGE-TEST (FAIL|RESULT)' "$run/blender.log" || { printf '%s: no result, see %s\n' "$name" "$run/blender.log"; status=1; }
  [[ -n "$library" ]] || continue
  rm -rf "$run/user"; mkdir -p "$run/user" "$run/characters"
  BLENDER_USER_RESOURCES="$run/user" "$blender" --background --factory-startup --offline-mode --python-exit-code 1 \
    --python "$here/tests/character_test.py" -- "$archive" "$run/characters" "$library" > "$run/characters.log" 2>&1 || status=1
  grep -E '^CHSTAGE-TEST (FAIL|RESULT)' "$run/characters.log" || { printf '%s: no character result, see %s\n' "$name" "$run/characters.log"; status=1; }
done
exit "$status"
