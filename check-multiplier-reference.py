import argparse
import json
from pathlib import Path

import numpy as np
from PIL import Image
from scipy.ndimage import map_coordinates

parser = argparse.ArgumentParser(description="Compare aligned GH3 and Clone Hero multiplier captures without image resampling.")
parser.add_argument("reference", type=Path)
parser.add_argument("capture", type=Path)
parser.add_argument("--output", type=Path, required=True)
args = parser.parse_args()
assets = Path(__file__).parent / "assets"
keys = ["1a", "2a", "3a", "4a", "2b", "4b", "6b", "8b"]
reports = []


def alpha(texture, vertices, xx, yy):
    basis = np.column_stack((vertices[3] - vertices[0], vertices[1] - vertices[0]))
    uv = np.linalg.solve(basis, np.array([xx.ravel() + .5 - vertices[0, 0], yy.ravel() + .5 - vertices[0, 1]]))
    rgba = np.asarray(Image.open(texture).convert("RGBA"))
    coords = [uv[1] * rgba.shape[0] - .5, uv[0] * rgba.shape[1] - .5]
    return map_coordinates(rgba[:, :, 3].astype(float) / 255, coords, order=1, mode="constant", cval=0).reshape(xx.shape)


for index, key in enumerate(keys):
    reference = np.asarray(Image.open(args.reference / f"state-{index}.png").convert("RGB"), dtype=np.int16)
    capture = np.asarray(Image.open(args.capture / f"state-{index}.png").convert("RGB"), dtype=np.int16)
    if capture.shape != reference.shape:
        raise ValueError(f"Different capture sizes for {key}")
    scale = reference.shape[0] / 900
    body_vertices = np.array([[262.5, 568.75], [262.052001953125, 792.7495727539062],
                              [486.05157470703125, 793.1975708007812], [486.49957275390625, 569.197998046875]]) * scale + .5
    multiplier_vertices = np.array([[323.6712341308594, 647.5611572265625], [323.4472351074219, 759.5609130859375],
                                    [435.447021484375, 759.784912109375], [435.6710205078125, 647.78515625]]) * scale + .5
    left, top = np.floor(multiplier_vertices.min(axis=0)).astype(int)
    right, bottom = np.ceil(multiplier_vertices.max(axis=0)).astype(int)
    yy, xx = np.mgrid[top:bottom, left:right]
    body_alpha = alpha(assets / "HUD_score_body.dds", body_vertices, xx, yy)
    multiplier_alpha = alpha(assets / f"HUD_score_nixie_{key}.dds", multiplier_vertices, xx, yy)
    mask = (body_alpha < 1 - 1e-6) & (body_alpha + multiplier_alpha * (1 - body_alpha) > 1 - 1e-6)
    if not mask.any():
        raise ValueError(f"Empty comparison mask for {key}")
    error = np.abs(capture[top:bottom, left:right] - reference[top:bottom, left:right])[mask]
    reports.append({"state": key, "pixels": int(mask.sum()), "different_pixels": int(np.any(error != 0, axis=1).sum()),
                    "different_channels": int(np.count_nonzero(error)), "maximum_channel_error": int(error.max()),
                    "mean_channel_error": float(error.mean()), "equal_channel_percent": float((error == 0).mean() * 100)})

result = {"pixel_identical": all(row["different_pixels"] == 0 for row in reports), "states": reports}
args.output.parent.mkdir(parents=True, exist_ok=True)
args.output.write_text(json.dumps(result, indent=2) + "\n")
print(json.dumps(result, indent=2))
raise SystemExit(0 if result["pixel_identical"] else 1)
