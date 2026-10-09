#!/usr/bin/env bash
# Round trip every probe GLB through Blender with each import/export variant. Run under the heavy lock.
set -euo pipefail
export MISE_QUIET=1
here="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
scratch="${1:-/tmp/opencode/spike-blender}"
mkdir -p "$scratch/out" "$scratch/logs"
blender=(blender-clean --background --factory-startup)

for source in "$scratch"/orig/*.glb; do
  name="$(basename "$source" .glb)"
  for variant in default:default temperance:default fortune:default nobind:default merge:default \
                 default:keep default:defbones default:posed-frame default:flatten default:zup fps30:default fps60:default fps30:keep; do
    import="${variant%%:*}"; export="${variant##*:}"
    out="$scratch/out/$name.$import-$export.glb"
    "${blender[@]}" --python "$here/roundtrip.py" -- import "$source" "$out" "$import" "$export" > "$scratch/logs/$name.$import-$export.log" 2>&1
  done
  for export in default keep; do
    blend="$scratch/out/$name.blend-$export.blend"
    "${blender[@]}" --python "$here/roundtrip.py" -- import "$source" "$blend" default "blend:$export" > "$scratch/logs/$name.blend-$export.save.log" 2>&1
    "${blender[@]}" "$blend" --python "$here/roundtrip.py" -- export "$scratch/out/$name.blend-$export.glb" "$export" > "$scratch/logs/$name.blend-$export.log" 2>&1
  done
done
ls -la "$scratch/out"
