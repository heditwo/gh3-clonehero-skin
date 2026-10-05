#!/usr/bin/env bash
set -euo pipefail
export MISE_QUIET=1

project_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
chart="$project_dir/charts/GH3 Gem Matrix"
frames="${GH3_GEM_MATRIX_FRAMES:-/tmp/opencode/gh3-matrix-compare}"
mkdir -p "$frames"
frames="$(mktemp -d "$frames/run.XXXXXX")"
times="3.2,5.2,7.2,9.2,11.2,13.2,15.2,17.2,19.2"
mkdir -p "$frames/stock" "$frames/gh3"

for mode in stock gh3; do
  keep_stock=0
  [[ "$mode" == stock ]] && keep_stock=1
  GH3_SKIN_PREVIEW_SONG="$chart" GH3_SKIN_KEEP_STOCK_GEMS="$keep_stock" \
    GH3_SKIN_PREVIEW_WIDTH=1600 GH3_SKIN_PREVIEW_HEIGHT=900 \
    GH3_SKIN_CAPTURE_TIMES="$times" GH3_SKIN_CAPTURE_DIR="$frames/$mode" \
    GH3_SKIN_SCREENSHOT="$frames/$mode/preview.png" "$project_dir/capture-preview.sh"
done

uv run --with pillow python - "$frames" "$project_dir/preview-gem-matrix.png" <<'PY'
import sys
from pathlib import Path
from PIL import Image, ImageDraw

frames, output = Path(sys.argv[1]), Path(sys.argv[2])
labels = (
    "Regular", "Forced HOPO", "Tap", "Open", "Open HOPO",
    "Open tap", "Natural HOPO", "Regular chord", "Tap sequence",
)
comparison = Image.new("RGB", (1360, 340 * len(labels)), "#151515")
draw = ImageDraw.Draw(comparison)
for index, label in enumerate(labels):
    for column, folder in enumerate(("stock", "gh3")):
        image = Image.open(frames / folder / f"frame-{index:02d}.png").convert("RGB")
        if image.size != (1600, 900):
            raise ValueError(f"Expected a 1600 x 900 capture: {folder} frame {index}")
        comparison.paste(image.crop((460, 440, 1140, 760)), (680 * column, 340 * index + 20))
    draw.text((5, 340 * index + 3), f"{label} / Clone Hero", fill="white")
    draw.text((685, 340 * index + 3), "GH3 skin", fill="white")
comparison.save(output)
print(f"Saved {output}")
print(f"Source frames: {frames}")
PY
