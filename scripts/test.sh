#!/usr/bin/env bash
# Run every automated check: the C# logic tests, the Python lint, format, and type checks, the Python tests, and shell syntax checks.
set -euo pipefail
export MISE_QUIET=1

project_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$project_dir"

dotnet run --project tests/SkinLogic.Tests.csproj -c Release --nologo
# The research and measuring tools link source files of the mod; build them so that a refactor cannot break them unnoticed.
for tool in tools/ClipBench tools/PackageImport tools/Gh3SongTool; do dotnet build "$tool" -c Release --nologo -v quiet; done
uvx ruff@0.17.0 check --quiet .
uvx ruff@0.17.0 format --check --quiet .
uvx --python 3.11 --with numpy mypy@2.4.0 --no-error-summary
uv run --with pillow --with numpy --with 'shapely>=2.1' --with jsonschema --with brotli python -m unittest discover -s tests -p 'test_*.py'
for script in install.sh install-linux.sh run-gh3-skin.sh scripts/*.sh blender/*.sh tools/windows/*.sh; do bash -n "$script"; done
sh -n tools/direction-install/install-direction.sh
printf 'All checks passed\n'
