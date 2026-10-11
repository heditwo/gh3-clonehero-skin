"""GH3 skinned models (.skin and .tex in model PAKs) as the band library, the characters, and the instruments export them."""

from pipeline.gh3 import scene
from pipeline.gh3.formats import key


def read_models(source, paks, texture_directory, collision_error):
    """(materials, meshes) of GH3 model PAKs; writes their textures to texture_directory as <checksum>.png.

    Each material gets its diffuse texture; cube map samplers wrap in U and clamp in V. Two PAKs that give one material
    name different data raise ValueError(collision_error).
    """
    materials, meshes, available, cubes = {}, [], set(), set()
    for pak in paks:
        for kind, _, data in source.load(pak):
            if kind == key(".skin"):
                found, parts = scene.scene(data, skinned=True)
                if any(materials[name] != found[name] for name in materials.keys() & found.keys()):
                    raise ValueError(collision_error)
                materials.update(found)
                meshes.extend(parts)
            elif kind == key(".tex"):
                cubes.update(scene.cube_names(data))
                for name, image in scene.textures(data):
                    image.save(texture_directory / f"{name}.png")
                    available.add(name)
    for material in materials.values():
        material["texture"] = scene.diffuse_texture(material, available)
    scene.mark_cube_samplers(materials, cubes)
    return materials, meshes
