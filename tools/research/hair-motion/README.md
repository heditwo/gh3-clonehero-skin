# Hair motion against the original GH3

Tools of the 2026-10-10 hair motion check ([PARITY-NOTES.md](../../../docs/PARITY-NOTES.md#hair-dynamics), [VERIFICATION.md](../../../docs/VERIFICATION.md#hair-motion)).

| File | Purpose |
|---|---|
| `gh3_session.py` | One original-GH3 session: patches `qb.pab.xen` and the zone PAKs (`gh3_patch.py`, from the God of Rock check), drives the menus (`gh3_nav.py`), records the window, samples the live Havok bodies (`hkRigidBody` vtable `0x8cae30`) at 300 Hz, and restores the files and the save byte-equal. The paths are those of the test machine |
| `hairlib.py` | GH3 recordings and bench outputs in one frame (bones, GH3 coordinates), Havok cone and plane angles, tips in the head frame |
| `replay.py` | Writes the head path of a recording; compares bench runs with GH3 (tips, frequency bands, cone overshoot) |
| `ring.py`, `evidence.py` | Ringing of the tips; the table and plot of PARITY-NOTES.md |
| `fam.py` | Two solver builds on all profiles in My Curse (bounce, head capsule clearance) |
| `limits.py`, `ragdoll_dump.py` | Live constraint angles against the limits; all bodies and constraints of a ragdoll |
| `body_path.py`, `body_colliders.py` | Body collision experiment (2026-10-10): the paths of all recorded body bones for the bench (`GH3_HAIR_BENCH_BODIES`), and the GH3 ragdoll body capsules as colliders (`GH3_HAIR_BENCH_BODY`) |

Work files go to `HAIR_MOTION_WORK` (default `~/.cache/hair-motion`). Run with `uv run --with numpy [--with matplotlib] python <tool>`.
