# GH3 Gameplay Skin: examples for creators

This archive has the example stage and the example character of the GH3 Gameplay Skin, a mod for Clone Hero. Use them to learn the CH Stage Tools add-on for Blender. The [creator guide](https://github.com/heditwo/gh3-clonehero-skin/blob/main/docs/CREATOR-GUIDE.md) tells you how to make them.

| File | Contents |
| --- | --- |
| `example-club.blend` | The source of the example stage: a small club with a wood stage, brick walls, speakers, a truss, a neon star, two cameras, and two lights with the moods `intro`, `blackout`, and `chorus` |
| `example.club.chstage` | The stage package (ID `example.club`). To play it, put it into `Custom/Stages` |
| `example-guitarist.blend` | The source of the example character: a body from Blender primitives and seven hair cards on the GH3 guitarist rig |
| `example.guitarist.chchar` | The character package (ID `example.guitarist`). To play it, put it into `Custom/Characters` |

## Open the examples

1. Install the add-on `chstage_tools-<version>.zip` of the same release in Blender 4.2 or later (Edit > Preferences > Get Extensions > Install from Disk).
2. Open `example-club.blend` or `example-guitarist.blend`.
3. For the character: in the add-on preferences, set **GH3 library (.chlib)** to `Mods/GH3Skin/Packages/gh3-band-v1.chlib` of your game folder. Then click the reload button in **CH Character > Weights and Hair**. The add-on loads the GH3 body and the guitar as helpers. The `.blend` file does not contain them.

## License

The project made all geometry and textures of these files from Blender primitives and generated patterns. The files are CC0-1.0 (public domain): you can use them for all purposes. The example character uses the bone names and the bind pose of the GH3 guitarist rig, because the package format requires the fixed GH3 rigs.
