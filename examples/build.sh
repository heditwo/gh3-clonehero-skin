#!/usr/bin/env bash
# Build the example stage and character (.blend and package) with the add-on, then validate the packages.
# Usage: bash examples/build.sh [BLENDER] [LIBRARY]
# Use Blender 4.2 (the add-on minimum) so that every supported Blender version opens the .blend files.
# The add-on is installed into a temporary Blender user folder, never into the user's Blender setup.
set -euo pipefail
export MISE_QUIET=1
here="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
root="$(dirname -- "$here")"
blender="${1:-blender}"
library="${2:-$root/assets/packages/gh3-band-v1.chlib}"
work="$(mktemp -d "${TMPDIR:-/tmp}/chstage-examples.XXXXXX")"
trap 'rm -rf "$work"' EXIT

archive="$(python3 "$root/blender/build.py" --output "$work/dist")"
for kind in stage character; do
  rm -rf "$work/user"; mkdir -p "$work/user"
  BLENDER_USER_RESOURCES="$work/user" "$blender" --background --factory-startup --offline-mode --python-exit-code 1 \
    --python "$here/build_examples.py" -- "$archive" "$library" "$here" "$kind" > "$work/$kind.log" 2>&1 \
    || { cat "$work/$kind.log"; exit 1; }
  grep -E '^EXAMPLES' "$work/$kind.log"
done
rm -f "$here"/*.blend1
cd "$root"
uv run --with numpy --with jsonschema --with brotli python -m pipeline.package.validate examples/example.club.chstage --library "$library"
uv run --with numpy --with jsonschema --with brotli python -m pipeline.package.validate examples/example.guitarist.chchar --library "$library"
