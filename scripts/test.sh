#!/usr/bin/env bash
# Run every automated check: the C# logic tests, the Python pipeline tests, and shell syntax checks.
set -euo pipefail
export MISE_QUIET=1

project_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$project_dir"

dotnet run --project tests/SkinLogic.Tests.csproj -c Release --nologo
uv run --with pillow --with numpy --with 'shapely>=2.1' python -m unittest discover -s tests -p 'test_*.py'
for script in install.sh run-gh3-skin.sh scripts/*.sh; do bash -n "$script"; done
printf 'All checks passed\n'
