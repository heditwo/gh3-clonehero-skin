"""Export the original GH3 Tesla material and noise texture."""

from hashlib import sha256
from io import BytesIO

from PIL import Image

from pipeline.gh3 import scene
from pipeline.gh3.data import Source
from pipeline.gh3.formats import key
from pipeline.stagepack.pack import load_json, write_json

# The Tesla coil material of global.pak, its GH3 template, and its noise texture.
TESLA_MATERIAL = "01488227"
TESLA_TEMPLATE = "d7287426"
TESLA_TEXTURE = "e936482c"


def convert(data, output):
    entries = Source(data).load("ZONES/global.pak.xen")
    materials = []
    for kind, _name, blob in entries:
        if kind != key(".scn"):
            continue
        material = scene.scene(blob)[0].get(TESLA_MATERIAL)
        if material is not None:
            materials.append(material)
    if len(materials) != 1 or materials[0]["template"] != TESLA_TEMPLATE or materials[0]["textures"] != [TESLA_TEXTURE]:
        raise ValueError("The source Tesla material is unknown")
    images = []
    for kind, _name, blob in entries:
        if kind != key(".tex"):
            continue
        images.extend(image for name, image in scene.texture_entries(blob) if f"{name:08x}" == TESLA_TEXTURE)
    if len(images) != 1:
        raise ValueError("The source Tesla noise texture is missing or duplicated")
    output.mkdir(parents=True, exist_ok=True)
    image = Image.open(BytesIO(images[0])).convert("RGBA")
    texture = output / "tesla-noise.png"
    image.save(texture)
    write_json(
        output / "tesla-material.json",
        {
            "material": TESLA_MATERIAL,
            "shader": TESLA_TEMPLATE,
            "texture": TESLA_TEXTURE,
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
