#!/usr/bin/env bash
# Round trip of venue packages through the add-on (Open .chstage, Export .chstage, compare), one Blender process each.
# Usage: bash blender/roundtrip.sh OUTPUT_DIR "BLENDER ..." PACKAGE ...
# Default packages: assets/packages/*.chstage. Writes OUTPUT_DIR/<blender>/<id>.json and OUTPUT_DIR/summary.md.
set -euo pipefail
export MISE_QUIET=1
here="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
output="${1:?output folder}"
read -r -a blenders <<< "${2:-blender-clean}"
shift 2 || shift $#
packages=("$@")
(( ${#packages[@]} )) || packages=("$here"/../assets/packages/*.chstage)
archive="$(python3 "$here/build.py" --output "$output/dist")"
status=0
for blender in "${blenders[@]}"; do
  name="$(basename "$blender")"
  rm -rf "$output/$name/user"; mkdir -p "$output/$name/user"
  for package in "${packages[@]}"; do
    id="$(basename "$package" .chstage)"
    BLENDER_USER_RESOURCES="$output/$name/user" "$blender" --background --factory-startup --offline-mode --python-exit-code 1 \
      --python "$here/tests/roundtrip.py" -- "$archive" "$package" "$output/$name" > "$output/$name/$id.log" 2>&1 || status=1
    grep -E '^CHSTAGE-ROUNDTRIP' "$output/$name/$id.log" || { printf '%s %s: no result, see %s\n' "$name" "$id" "$output/$name/$id.log"; status=1; }
  done
done
python3 "$here/tests/roundtrip_summary.py" "$output" > "$output/summary.md"
cat "$output/summary.md"
exit "$status"
