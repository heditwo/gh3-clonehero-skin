"""Export the original GH3 Tesla material and noise texture."""

from hashlib import sha256
from io import BytesIO

from PIL import Image

from pipeline.gh3 import scene
from pipeline.gh3.direction import Source
from pipeline.gh3.formats import key, values, word
from pipeline.stagepack.pack import load_json, write_json


def convert(data, output):
    entries = Source(data).load("ZONES/global.pak.xen")
    materials = []
    for kind, name, blob in entries:
        if kind != key(".scn"):
            continue
        material = scene.scene(blob)[0].get("01488227")
        if material is not None:
            materials.append(material)
    if len(materials) != 1 or materials[0]["template"] != "d7287426" or materials[0]["textures"] != ["e936482c"]:
        raise ValueError("The source Tesla material is unknown")
    images = []
    for kind, name, blob in entries:
        if kind != key(".tex"):
            continue
        for index in range(values(blob, 6, "H")[0]):
            header = word(blob, 8) + index * 40
            if word(blob, header + 4) != 0xE936482C:
                continue
            start, size = values(blob, header + 28, "II")
            images.append(blob[start : start + size])
    if len(images) != 1:
        raise ValueError("The source Tesla noise texture is missing or duplicated")
    output.mkdir(parents=True, exist_ok=True)
    image = Image.open(BytesIO(images[0])).convert("RGBA")
    texture = output / "tesla-noise.png"
    image.save(texture)
    write_json(
        output / "tesla-material.json",
        {
            "material": "01488227",
            "shader": "d7287426",
            "texture": "e936482c",
            "sourceSha256": sha256(images[0]).hexdigest(),
            "imageSha256": sha256(texture.read_bytes()).hexdigest(),
            "size": list(image.size),
        },
    )
    library = output.parent / "library.json"
    if library.is_file():
        manifest = load_json(library)
        for path in (texture, output / "tesla-material.json"):
            manifest["files"][str(path.relative_to(library.parent))] = sha256(path.read_bytes()).hexdigest()
        write_json(library, manifest)
    print(f"Tesla noise texture: {image.size[0]} x {image.size[1]} pixels, {texture}")
