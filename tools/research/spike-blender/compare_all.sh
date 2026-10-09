#!/usr/bin/env bash
# Compare every round-trip GLB with its original; writes results.jsonl in the scratch folder.
set -euo pipefail
export MISE_QUIET=1
root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../../.." && pwd)"
scratch="${1:-/tmp/opencode/spike-blender}"
: > "$scratch/results.jsonl"
for out in "$scratch"/out/*.glb; do
  name="$(basename "$out")"
  PYTHONPATH="$root" uv run --with pillow --with numpy --with scipy python "$root/tools/research/spike-blender/compare.py" \
    "$scratch/orig/${name%%.*}.glb" "$out" >> "$scratch/results.jsonl"
done
