#!/usr/bin/env bash
# Run the rig corpus through the add-on flow headless (docs/BLENDER-ADDON.md section 2.13).
# Usage: bash tools/rig-corpus/run.sh OUTPUT_DIR [corpus.py options, for example --only xbot,kaykit --export --render]
# RIG_CORPUS_DIR: the downloaded models (tools/rig-corpus/fetch.sh; default ~/.cache/rig-corpus). Models that are not
# there are skipped. MMD_TOOLS_DIR: an MMD Tools extension folder for the PMX model (else that model fails to import).
# BLENDER: the Blender command (default blender-clean). CHSTAGE_TEST_LIBRARY, CHSTAGE_TEST_CHARACTERS: the libraries.
set -euo pipefail
export MISE_QUIET=1
here="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
root="$(cd -- "$here/../.." && pwd)"
output="$(realpath -m "${1:?output folder}")"
shift
models="${RIG_CORPUS_DIR:-$HOME/.cache/rig-corpus}"
library="${CHSTAGE_TEST_LIBRARY:-$root/assets/packages/gh3-band-v1.chlib}"
characters="${CHSTAGE_TEST_CHARACTERS:-$root/assets/packages/gh3-characters-v1.chlib}"
blender="${BLENDER:-blender-clean}"
if [[ ! -d "$models" || ! -e "$library" || ! -e "$characters" ]]; then
  printf 'rig corpus: skipped (models %s or libraries not found)\n' "$models"
  exit 0
fi
mkdir -p "$output"
archive="$(python3 "$root/blender/build.py" --output "$output/dist")"
user="$output/user"
rm -rf "$user"; mkdir -p "$user/extensions/user_default"
python3 -c "import sys, zipfile; zipfile.ZipFile(sys.argv[1]).extractall(sys.argv[2])" "$archive" "$user/extensions/user_default/chstage_tools"
[[ -n "${MMD_TOOLS_DIR:-}" && -d "${MMD_TOOLS_DIR:-}" ]] && cp -r "$MMD_TOOLS_DIR" "$user/extensions/user_default/mmd_tools"
BLENDER_USER_RESOURCES="$user" "$blender" --background --python-exit-code 1 \
  --python-expr "import bpy; bpy.ops.preferences.addon_enable(module='bl_ext.user_default.chstage_tools'); [bpy.ops.preferences.addon_enable(module='bl_ext.user_default.mmd_tools') for _ in [0] if __import__('os').path.isdir('$user/extensions/user_default/mmd_tools')]" \
  --python "$here/corpus.py" -- --models "$models" --out "$output" --library "$library" --characters "$characters" "$@" > "$output/corpus.log" 2>&1 || { tail -30 "$output/corpus.log"; exit 1; }
grep '^CORPUS SUMMARY' "$output/corpus.log"
