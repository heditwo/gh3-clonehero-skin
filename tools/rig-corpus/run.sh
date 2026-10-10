#!/usr/bin/env bash
# Run the rig corpus through the add-on flow headless (docs/BLENDER-ADDON.md section 2.13).
# Usage: bash tools/rig-corpus/run.sh OUTPUT_DIR [--only id,id] [--check] [corpus.py options: --export --render --save --draft]
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
# One Blender process for each model: a crash or a leak of one model does not stop the others.
only=""
check=""
args=()
while (( $# )); do
  case "$1" in
    --only) only="$2"; shift 2 ;;
    --check) check="--check"; shift ;;
    *) args+=("$1"); shift ;;
  esac
done
ids="$(python3 -c "import json, sys; ids = [m['id'] for m in json.load(open(sys.argv[1]))['models']]; want = [x for x in sys.argv[2].split(',') if x]; print(' '.join(i for i in ids if not want or i in want))" "$here/corpus.json" "$only")"
enable="import bpy, os; bpy.ops.preferences.addon_enable(module='bl_ext.user_default.chstage_tools'); os.path.isdir('$user/extensions/user_default/mmd_tools') and bpy.ops.preferences.addon_enable(module='bl_ext.user_default.mmd_tools')"
: > "$output/corpus.log"
for id in $ids; do
  BLENDER_USER_RESOURCES="$user" "$blender" --background --python-exit-code 1 --python-expr "$enable" \
    --python "$here/corpus.py" -- --models "$models" --out "$output/models/$id" --library "$library" --characters "$characters" --only "$id" "${args[@]}" \
    >> "$output/corpus.log" 2>&1 || printf 'CORPUS %s {"id": "%s", "failed": "Blender stopped (see corpus.log)"}\n' "$id" "$id" >> "$output/corpus.log"
done
python3 "$here/summary.py" "$output" $check
