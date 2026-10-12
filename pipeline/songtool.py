"""Build and locate tools/Gh3SongTool, the C# reader of GH3 song PAKs that shares the mod's QB, script, and event code."""

import os
import subprocess
from functools import lru_cache

from pipeline.paths import ROOT

PROJECT = ROOT / "tools/Gh3SongTool"


@lru_cache(maxsize=1)
def path():
    subprocess.run(
        ["dotnet", "build", str(PROJECT), "-c", "Release", "--nologo", "-v:q"],
        check=True,
        stdout=subprocess.DEVNULL,
        env={**os.environ, "MISE_QUIET": "1"},
    )
    return PROJECT / "bin/Release/net10.0/Gh3SongTool.dll"
