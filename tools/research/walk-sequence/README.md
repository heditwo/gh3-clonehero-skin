# Walk sequence against the original GH3

Tools of the 2026-10-12 walk check ([PARITY-NOTES.md](../../../docs/PARITY-NOTES.md#walk-clips), [STATUS.md](../../../docs/STATUS.md#walk-sequence)). Record GH3 with `../hair-motion/gh3_session.py` (`LEVEL=z_dive PERFORMANCE=1 CAM=0`, and in `SAVE_IN` a save made with the same patch: `PREP=1 NAV_ONLY=1 SAVE_OUT=<folder>`), and the mod with `GH3_SKIN_TRACE=1` (the `[trace] walk` lines).

| File | Purpose |
|---|---|
| `extract.py` | Keeps the leg and spine bodies of a large `bodies.npz` (reads the array in parts) |
| `gh3walk.py` | The pelvis path and the leg and spine joint rotations of the walking guitarist |
| `gh3root.py` | The GH3 object root (position, yaw) and the best clip at song times: the pelvis world pose times the inverse of the pelvis model pose of the best clip frame |
| `cmpwalk.py` | One walk, GH3 against the mod: time offset, pelvis path, joint angles, and heading for each phase (before, each walk clip, after) |

Run with `uv run --with numpy python <tool>` from the repository root.
