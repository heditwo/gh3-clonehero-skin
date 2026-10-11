#!/usr/bin/env bash
# Run every automated check: the C# logic tests, the Python lint and format check, the Python tests, and shell syntax checks.
set -euo pipefail
export MISE_QUIET=1

project_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$project_dir"

dotnet run --project tests/SkinLogic.Tests.csproj -c Release --nologo
uvx ruff@0.17.0 check --quiet .
uvx ruff@0.17.0 format --check --quiet .
uv run --with pillow --with numpy --with 'shapely>=2.1' --with jsonschema --with brotli python -m unittest discover -s tests -p 'test_*.py'
for script in install.sh install-linux.sh run-gh3-skin.sh scripts/*.sh blender/*.sh tools/windows/*.sh; do bash -n "$script"; done
printf 'All checks passed\n'
