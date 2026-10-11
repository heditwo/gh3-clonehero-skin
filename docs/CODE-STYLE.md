# Code style

This document gives the rules for the code of the project. Follow them for new code and for changes. Each language has its own section.

## Python

The Python code is the asset pipeline (`pipeline/`), the Blender add-on (`blender/chstage_tools/`, `blender/build.py`), the Python tests (`tests/*.py`, `blender/tests/`), and the tools (`tools/`). The pipeline runs on Python 3.12 (`uv run`); the add-on and the package format code that it bundles must also run on Python 3.11 (Blender 4.2).

### Tools

- `ruff.toml` has the settings. `scripts/test.sh` runs `ruff check` and `ruff format --check`. Before a commit, run:

  ```bash
  uvx ruff@0.17.0 format .
  uvx ruff@0.17.0 check --fix .
  ```

- The rule sets: pyflakes, pycodestyle, isort, pyupgrade (Python 3.11), bugbear, pep8-naming, flake8-simplify, and a complexity limit of 20. `ruff.toml` gives the reason for each rule that is off.
- `mypy.ini` lists the package format modules that `mypy` checks (Python 3.11); `scripts/test.sh` runs it. Public functions of these modules have type hints; give new public functions type hints too.
- There is no `pyproject.toml`: in a folder with one, `uv run` makes a project environment.
- The formatter sets the layout (120 columns). Do not align code by hand.

### Package layout

| Folder | Contents | Rules |
| --- | --- | --- |
| `pipeline/gh3/` | Readers of the GH3 PC files: PAK and QB, scenes and textures, skeletons and clips, Havok ragdolls, Direct3D 9 shaders, the material library | Bounded readers: they check sizes and raise `ValueError` for bad data. No output files |
| `pipeline/library/`, `pipeline/characters.py`, `pipeline/character_library.py`, `pipeline/instruments.py`, `pipeline/venues/`, `pipeline/events.py` | Export steps from the GH3 data to the runtime files ([PIPELINE.md](PIPELINE.md)) | One command module for each step (`python -m pipeline.<step> --data DATA`) |
| `pipeline/stagepack/` | The public stage-pack format and its compiler | |
| `pipeline/package/` | Package format v1: build, validate, GLB, textures, clips, characters, skeletons | Modules in `VALIDATOR_FILES` of `blender/build.py` use relative imports only and need only numpy: the add-on bundles them |
| `pipeline/render/`, `pipeline/hud/` | Shader bundles; HUD textures | |
| `blender/chstage_tools/` | The add-on | Modules without `bpy` are tested without Blender through `tests/addon.py`. Keep the public functions that `tools/rig-corpus` and the build scripts of the test characters call (`skeleton_tools`, `chain_tools`, `character_tools`) |
| `tools/` | Operational tools (captures, load times, audits, the rig corpus) | |
| `tools/research/` | Finished investigations, kept as evidence | Not part of the pipeline. Each tool has one line in its README |

One implementation for each format: GLB split and join (`package/glb.py`), the runtime files GH3BND02, GH3ANM01, GH3CLP01, and GH3VID01 (`package/runtime.py`), GH3TEX02 (`package/texture.py`), rotation conversions (`package/rotations.py`), the library IDs and profiles (`package/profiles.py`), and the GH3 `DATA` loader (`gh3/data.py`). Use them; do not copy them into the add-on or a tool.

### Names

- Modules, functions, and variables: `snake_case`. Classes: `CapWords`. Constants: `UPPER_CASE` at module or class level.
- Blender classes keep the Blender form `CHSTAGE_OT_name`, `CHSTAGE_PT_name`.
- A name says what the value is: `MATERIAL_ALPHA_REF`, not `OFFSET_232`. Words in full; common short forms are permitted (`glb`, `uv`, `fps`, `ik`, `rgba`).
- Private helpers start with `_`. A name that a tool, a test, or the add-on uses is public; rename it in all places in the same commit (`tests/test_pipeline_imports.py` finds missed imports).

### Constants

Do not write a number with a meaning in a function. Give it a name at the top of the module, and say where it comes from:

| Kind | Example | Source note |
| --- | --- | --- |
| GH3 binary format | `MATERIAL_TEMPLATE = 160`, `CLIP_FLAG_PARTIAL = 0x80000` | The record layout in a comment above the group; the layouts were found by reading the GH3 PC files |
| GH3 script value | `DEFAULT_FIELD_OF_VIEW = 72`, `ANIMATION_CONSTANTS` | The QB name or file (`guitar_animation_data.qb`), or a [PARITY-NOTES.md](PARITY-NOTES.md) section |
| External specification | `DDS_CAPS2_CUBEMAP = 0x200`, `TYPE_TRANSFORM = 18` | The specification (DDS reference, Havok 4.6 `hkClassMember::Type`, d3d9types.h, glTF 2.0) |
| Measured from GH3 | `HIGHWAY_SIZE = (512, 1024)` | The measurement and where it is recorded |
| Tuned | `PRESETS`, `ELBOW_POLE_SIDE` | Say "tuned" and what was tuned against (a bench, an image) |
| Engineering limit | `MAX_QB_DEPTH = 12`, `SINGULAR = 1e-9` | Why the value is safe |

`tests/test_magic_numbers.py` checks `pipeline/gh3`, `pipeline/library`, and `pipeline/package`: a number in a function body must be a named constant, except -1, 0, 1, 2, 3, 4, 0.5, and the base of `int()`. `tests/fixtures/magic-numbers.json` has the count of the unnamed numbers that are still in each file (most in the humanoid finder and the retarget). A file can go down, never up. After you name numbers, run `python -m tests.test_magic_numbers --update`.

### Functions

- A function does one step. The complexity limit is 20. The longer functions of today have `# noqa: C901`; when you change one, split it.
- Public functions have a docstring of one or two lines that says what they return, with the units and the coordinate frame when they are not clear.
- Prefer a table (a dict or a tuple of rows at module level) to a long `if`/`elif` chain of constants.
- A behavior change and a refactor go in different commits. A refactor of an exporter must give byte-equal outputs; check with the regeneration and a comparison of the files ([VERIFICATION.md](VERIFICATION.md#python-refactors)).

### Comments and docstrings

- Each module starts with a docstring of one or two lines that says what the module is for. Add the format layout or the coordinate frame after it when the module needs it.
- Comments say why, not what. Keep the GH3 citations (script names, addresses, PARITY-NOTES sections).
- Write comments and docstrings in ASD-STE100 Simplified Technical English, as the documentation.

### Command lines

- Run the commands as modules: `python -m pipeline.<module>`. Parse the arguments in `main()` with `argparse`, and use `description=__doc__.splitlines()[0]`.
- Use the same argument names: `--data` (the GH3 `DATA` folder), `--output`, `--library`, `--character-library`, `--only`, `--json`.
