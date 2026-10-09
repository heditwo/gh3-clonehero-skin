#!/usr/bin/env bash
# Build the add-on and run the headless Blender test with each Blender given (default: blender-clean).
# Usage: bash blender/test.sh [OUTPUT_DIR] [BLENDER ...]
# The test installs the extension into a temporary Blender user folder, never into the user's Blender setup.
set -euo pipefail
export MISE_QUIET=1
here="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
output="${1:-${TMPDIR:-/tmp}/chstage-addon-test}"
shift || true
blenders=("$@")
(( ${#blenders[@]} )) || blenders=(blender-clean)
rig="${CHSTAGE_TEST_RIG:-}"

archive="$(python3 "$here/build.py" --output "$output/dist")"
status=0
for blender in "${blenders[@]}"; do
  name="$(basename "$blender")"
  run="$output/$name"
  rm -rf "$run"; mkdir -p "$run/user"
  BLENDER_USER_RESOURCES="$run/user" "$blender" --background --factory-startup --offline-mode --python-exit-code 1 \
    --python "$here/tests/blender_test.py" -- "$archive" "$run" $rig > "$run/blender.log" 2>&1 || status=1
  grep -E '^CHSTAGE-TEST (FAIL|RESULT)' "$run/blender.log" || { printf '%s: no result, see %s\n' "$name" "$run/blender.log"; status=1; }
done
exit "$status"
