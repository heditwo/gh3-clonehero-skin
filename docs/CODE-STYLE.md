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

## C#

The C# code is the mod (`src/`), the logic tests (`tests/*.cs`), and the C# tools (`tools/PackageImport`, `tools/ClipBench`, and `tools/Gh3SongTool` link mod files; `scripts/test.sh` builds them). The GH3 song PAK reader is in `tools/Gh3SongTool`, not in `src/`: the mod does not read GH3 files.

### Files and modules

- One main type for each file. The file has the name of the type. Small records and enums that only this type uses can stay in its file.
- A large type is split into partial files along its parts: `<Type>.<Part>.cs`, for example `Gh3ScriptRuntime.Builtins.cs` and `StageCompiler.Materials.cs`. A file of more than about 500 lines is a candidate for a split.
- Each file starts with a comment of one or two lines that says what the file is for.
- The modules and their boundaries are in [ARCHITECTURE.md](ARCHITECTURE.md#runtime-modules). Keep these boundaries:
  - `src/Gh3` and `src/Packages` (except `PackageRuntime`) do not use Unity. The tests and the tools link them.
  - The state classes of `src/Stage` and `src/Hud` that the tests link (see `tests/SkinLogic.Tests.csproj`) do not use Unity.
  - Only `CloneHeroBindings` calls obfuscated Clone Hero members.
- When you add a file that a linked file needs, add it to `tests/SkinLogic.Tests.csproj` and, for `src/Packages`, `src/Stage/Band/StageHair*.cs`, and their helpers, to `tools/PackageImport/PackageImport.csproj`.

### Names

- Types, methods, properties, and constants: PascalCase. Private fields: `_camelCase`. Locals and parameters: camelCase.
- Write words in full: `Maximum`, `Minimum`, `Position`, `Rotation`. Common short forms are permitted: `Id`, `Uv`, `Fps`, `Ik`.
- Prefix GH3 names with `Gh3` when a plain name is not clear (`Gh3QbType`, `Gh3ScriptOp`, `Gh3Keys`). Stage parts start with `Stage`.
- A name says what the value is, not where it comes from: `PulseSeconds`, not `Value27`.

### C# constants

Do not write a number with a meaning directly in the code. Give it a name, or put a source note on it. The literal guard (`tests/LiteralGuardTests.cs`) checks this rule in `src/Stage`, `src/Hud`, `src/Gameplay`, and `src/Gh3`.

- A number is trivial and needs no name when it is an integer from 0 to 16, a half or a quarter, a power of two to 256, 24, 60, 90, 100, 180, 255, 360, 1000, or `1e-6`, and also `0xff`, `0xffff`, `0xffffffff`.
- Put a constant in a `const` or `static readonly` field near its use. Put constants that several files use in a constants file (`*Constants.cs`, for example `Gh3QbConstants.cs`, `Gh3NoteConstants.cs`, `StageParticleConstants.cs`, `Gh3GameConstants.cs`) or in a shared class (`StageLimits`, `StageTolerance`, `HudCanvas`, `RenderQueue`).
- Keep the type of the literal. A `const int` replaces an integer literal, a `const float` a `float` literal (`2f`), a `const double` a `double` literal. A changed type can change the arithmetic.
- Classify each constant with a marker at the start of its comment:

| Class | Marker | Use |
| --- | --- | --- |
| (a) GH3 data | `GH3:` | A value from the GH3 binary, a QB script, or GH3 data files. Give the source: a GH3.exe address (`GH3.exe 0x477120`), a script and field (`guitar_hud_2d.qb, num_a9 at (222, 70)`), or a section of [PARITY-NOTES.md](PARITY-NOTES.md) |
| (b) Measured | `Measured:` | A value measured from GH3: recordings, memory, or extracted data. Give the method or the tool (`tools/lipsync-shapes.py`) |
| (c) Tuned | `Tuned:` | A value that we chose or calibrated by eye. Say what it controls. If a GH3 source can exist, say "GH3 source not found" |
| (d) Engineering | none | Limits against corrupt data, file layouts of our own formats, tolerances, capacities. Give a name that says what it is |

- A marker covers its statement: the comment on the same line, or the comment block directly above the statement. A comment above a group of constants covers the group.
- Name GH3 checksums. `Gh3Keys` holds checksums of known names as constants (a test checks each one against its name). A checksum without a known name stays as a hex literal with a `GH3:` note, for example `0x1e2ee0ec`.
- Data tables of measured or GH3 values stay in `static readonly` arrays with their source note above the table. Large tables go to their own partial file (`StageLipSync.Shapes.cs`).

To see the literals that the guard rejects, or to check other folders:

```bash
MISE_QUIET=1 dotnet build tests/SkinLogic.Tests.csproj -c Release --nologo
GH3_LITERAL_REPORT=1 dotnet tests/bin/Release/net10.0/SkinLogic.Tests.dll
GH3_LITERAL_REPORT=1 GH3_LITERAL_FOLDERS=src/Packages,src/Menu,src dotnet tests/bin/Release/net10.0/SkinLogic.Tests.dll
```

`tests/fixtures/literal-allowlist.txt` lists the literals that the guard accepts without a name or a marker. Keep it empty if possible.

### Comments

- Write a comment only for the reason ("why") or for a source. Do not write a comment that tells what the next line does.
- Keep the GH3 source citations, and make them exact (address, script, field).
- Write comments in ASD-STE100 Simplified Technical English: short sentences, active voice, one meaning for each word.
- Do not leave commented-out code, old TODO notes, or the history of a change in comments. The Git history keeps them.

### Data, not code

The mod does not choose behaviour by a stage ID, a GH3 zone name, a character or profile name, a song title, an ID prefix, or a GH3 bone name (PACKAGING-PLAN decision 16). Put the value that the code needs into the package data, and let the pipeline write it from the GH3 data:

| The code needs | Read it from |
| --- | --- |
| The small-stage intro and win animations | `placements.json` `smallVenue` (`StagePack.SmallVenue`) |
| The GH3 zone name of a venue | `scripts.json` `zone` (`StagePack.Zone`) |
| The band library | The required library with a catalog (`PackageManager.BandLibrary`) |
| The fixed profiles of the band roles | `catalog.json` `profile` (`LibraryRigs`) |
| The instrument, microphone, mouth, drum stick, and throne bones | The rig points (`RigPoints`) of the rig that plays |
| The IK chains and the twist bones | The rig data `ik` and `constraints` (`IkChainDefinition`, `RigConstraint`) |
| The humanoid and face bones of a GH3 rig | `HumanoidRoles` (the one place for GH3 bone names) |

`tests/DataGuardTests.cs` fails when a string literal in `src/` is a GH3 stage ID (`gh3.<name>`), a GH3 zone (`z_<name>`), a GH3 character name, or a GH3 bone name (`Bone_<name>`). Its allowlist has two files: `src/Packages/HumanoidRoles.cs` (bone names) and `src/Packages/PlayerChoices.cs` (`FallbackStage`, the default stage of a setting). Do not add to the allowlist; change the data. Band roles (`guitarist`, `bassist`, `singer`, `drummer`) are not names of content; code can use them. In the pipeline, a rule for a single GH3 character or venue comes from the GH3 data (for example the `IsWinPort` branch of `create_guitarist`), or it is a documented exception in [PARITY-NOTES.md](PARITY-NOTES.md#data-rules).

### Environment variables

`SkinEnvironment` lists every environment variable that the mod reads, with a name for each one. Read variables only through `SkinEnvironment.Get`, `IsOne` ("1" turns a test switch on), and `IsNotOff` ("off" or "0" turns a feature off). Document a new variable in [VERIFICATION.md](VERIFICATION.md#runtime-variables).

### Il2Cpp rules

The rules of [ARCHITECTURE.md](ARCHITECTURE.md#il2cpp-constraints) apply. Code that follows them can look unusual; do not "simplify" it:

- In per-frame code, do not use Unity struct constructors or operators. Use `System.Numerics` and field initializers (`new Vector4 { x = ... }`). A comment at the place says so where it is not clear.
- Do not allocate in per-frame code. Keep work arrays in fields; use `static readonly` tables, not `new[] { ... }` in a method that runs each frame.
- A `static readonly` field of a Unity struct type (for example `Vector2`, `Rect`) runs Unity code when the type initializes. Use `const float` fields for the components instead.
- Background threads use only managed code: no Unity and no Il2Cpp calls.

### Random numbers

- GH3's own generator is `Gh3Random` (GH3.exe `Rnd`); use it where GH3 draws a value.
- For the mod's own repeatable choices use `XorShift32` (stateful) or `PcgHash` (stateless). Do not copy the algorithms into other files.

### Tests and checks

- `bash scripts/test.sh` runs all checks. The C# build has no warnings (`dotnet build src/Gh3Skin.csproj -c Release`).
- A refactor does not change behavior. To show it, compare the method IL of the old and the new `Gh3Skin.dll` (an IL disassembler such as ICSharpCode.Decompiler), the hair bench output (`GH3_HAIR_BENCH`), the package cache of `tools/PackageImport`, and captures of the same song times ([VERIFICATION.md](VERIFICATION.md#compare-two-builds)).
