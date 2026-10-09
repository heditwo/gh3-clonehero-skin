# Examples

Example content for creators. [docs/CREATOR-GUIDE.md](../docs/CREATOR-GUIDE.md) tells you how to make the same things.

| File | Contents |
| --- | --- |
| `example-club.blend` | Source of the example stage: a small club with a wood stage, brick walls, speakers, a cutout truss, a neon star, two cameras, and two lights with the moods `intro`, `blackout`, and `chorus` |
| `example.club.chstage` | The stage package (ID `example.club`). Put it into `Custom/Stages` |
| `example-guitarist.blend` | Source of the example character: a body from Blender primitives and seven hair cards on the GH3 guitarist rig |
| `example.guitarist.chchar` | The character package (ID `example.guitarist`, profile `gh3-guitarist-axel-v1`). Put it into `Custom/Characters` |

All geometry and textures come from `build_examples.py` (Blender primitives and generated patterns). The manifests give the license `CC0-1.0`. The character uses the GH3 guitarist rig (bone names and bind pose), because the package format requires the fixed GH3 rigs.

## Open the examples

1. Install the CH Stage Tools add-on ([CREATOR-GUIDE.md section 2](../docs/CREATOR-GUIDE.md#2-install-the-add-on)).
2. Open `example-club.blend` or `example-guitarist.blend` in Blender 4.2 or later.
3. For the character: set **GH3 library (.chlib)** in the add-on preferences, then click the reload button in **CH Character > Weights and Hair**. The add-on loads the GH3 body and the guitar as helpers. The `.blend` file does not contain them.

## Make the examples again

```sh
flock /tmp/opencode/gh3-heavy.lock bash examples/build.sh ~/.cache/blender/blender-4.2-clean
```

`build.sh` builds the add-on, runs `build_examples.py` in Blender (one process for the stage, one for the character), and validates the packages with the GH3 library (`assets/packages/gh3-band-v1.chlib`). Use Blender 4.2: a `.blend` file from a newer Blender does not open in Blender 4.2.

`check_examples.py` opens each `.blend` file, exports it, and compares the export with the package (the preview image is not compared, because each Blender version renders it a little differently):

```sh
blender --background --factory-startup --python-exit-code 1 --python examples/check_examples.py -- blender/dist/chstage_tools-0.1.0.zip assets/packages/gh3-band-v1.chlib /tmp/check
```

`tests/test_examples.py` (part of `scripts/test.sh`) validates the packages without Blender.
