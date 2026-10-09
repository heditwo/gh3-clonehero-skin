"""Convert the owner's original GH3 hand flames and sparks."""

import json
from hashlib import sha256
from io import BytesIO

from PIL import Image

from pipeline.gh3.formats import find_struct, key


def color(value):
    return [((value >> shift) & 255) / 255 for shift in (24, 16, 8, 0)]


def convert(source, output):
    scripts = {name: blob for kind, name, blob in source.load("PAK/qb.pak.xen") if kind == key(".qb")}
    images = {name: blob for kind, name, blob in source.load("ZONES/global.pak.xen") if kind == key(".img")}
    output.mkdir(parents=True, exist_ok=True)
    emitters = []
    for name, script_id, texture_id, file in [
        ("GP_4X_FingerFlames01", 0x16ecb37e, 0x1bd73987, "flames.png"),
        ("GP_4X_FingerSparks01", 0x017be24b, 0xfefe220b, "sparks.png"),
    ]:
        data = find_struct(scripts[script_id], name)
        material = data[key("QuickMaterial")]
        if material[key("DiffuseTexture")] != texture_id or material[key("BlendMode")] != key("add"):
            raise ValueError(f"Unexpected hand-fire material: {name}")
        image = images[texture_id]
        start = image.find(b"DDS ")
        if start < 0:
            raise ValueError(f"Missing hand-fire DDS: {name}")
        Image.open(BytesIO(image[start:])).convert("RGBA").save(output / file)
        emitters.append({
            "name": name, "texture": file, "textureId": f"{texture_id:08x}",
            "scriptSha256": sha256(scripts[script_id]).hexdigest(), "imageSha256": sha256(image).hexdigest(),
            "rate": data[key("Emit_Rate")], "life": data[key("LifeRange")],
            "speed": data[key("SpeedRange")], "size": data[key("SizeRange")],
            "spread": data[key("AngleSpread")], "range": data[key("EmitRangeDims")],
            "force": data[key("Force")], "direction": data[key("Emit_Target")],
            "follow": data[key("FollowEmitter")], "color": color(data[key("Color")]),
            "colors": [color(value) for value in data[key("ParticleColor")]],
            "atlas": 2 if material.get(key("TextureLayout")) == key("Layout2x2") else 1,
            "fps": material[key("QuadAnimationFPS")],
        })
    definition = {"version": 1, "bones": [f"{key('Bone_Hand_Middle_Mid_' + side):08x}" for side in ("L", "R")], "emitters": emitters}
    (output / "hand-fire.json").write_text(json.dumps(definition, indent=2) + "\n")
    print(f"Exported original hand flames and sparks to {output}")
