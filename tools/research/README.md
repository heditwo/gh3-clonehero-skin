# Research tools

Probe and analysis tools of finished investigations, kept as evidence for the reports in [docs/spike/](../../docs/spike/)
and for the measured values that the code cites. They are not part of the pipeline: they can need extra packages, game
copies, or files of the test machine. The in-game probes (`GH3_SKIN_SPIKE`) that some of them used were removed in
step 6. `tests/test_pipeline_imports.py` checks that their `pipeline` imports still exist.

| Tool | Purpose |
| --- | --- |
| [`hair-motion/`](hair-motion/README.md) | Hair motion against the original GH3: GH3 sessions with Havok sampling, the hair bench comparisons, and the custom chain naturalness measures |
| `spike-blender/run.sh`, `roundtrip.py` | Round trip of GH3 rig GLBs through Blender with each import and export variant ([docs/spike/blender.md](../../docs/spike/blender.md)) |
| `spike-blender/compare.py`, `compare_all.sh` | Measured differences between an original rig GLB and its Blender round trip |
| `spike-blender/accept.py` | Runs the stage-pack actor compiler on GLBs |
| `spike-blender/band_glb.py` | Writes a library band member as a skinned GLB with the original rig |
| `spike-clips/analyze.py`, `summarize.py` | Size and error of quantized band clip encodings, and their Markdown tables (the q48 decision) |
| `spike-clips/stats.py` | Counts of the GH3CLP01 clip library |
| `spike-d3d11/*.py` | Direct3D 11 shader probe: shader platforms of Unity files and bundles, DXBC compile and chunks, metadata and interop type comparisons |
| `spike-macos/*.py`, `InteropGen/` | macOS probe: Mach-O slices and signatures, Metal shader blobs, texture formats, interop and method signature comparisons |
| `spike-skinned/character_glb.py`, `compare_renders.py` | Skinned GLB probe: a band member as a skinned GLB, and the pixel statistics of the comparison renders |
| `spike-textures/encode_textures.py`, `csharp-encoder/`, `run-texture-spike.sh` | Texture probe: BC1/BC3/BC5/BC7 encode times and PSNR, the C# encoder, and the in-game run |
