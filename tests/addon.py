"""Modules of the Blender add-on (blender/chstage_tools) for the tests that run without Blender."""

import importlib
import importlib.util
import sys
import types
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
ADDON = ROOT / "blender" / "chstage_tools"
PACKAGE = "chstage_tools_nobpy"


def addon_module(name):
    """A module of the add-on package without its __init__ (which needs bpy). The first call copies pipeline/package
    into the add-on vendor folder, as blender/build.py does for a release."""
    if PACKAGE not in sys.modules:
        spec = importlib.util.spec_from_file_location("chstage_build", ROOT / "blender" / "build.py")
        build = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(build)
        build.vendor()
        package = types.ModuleType(PACKAGE)
        package.__path__ = [str(ADDON)]
        sys.modules[PACKAGE] = package
    return importlib.import_module(f"{PACKAGE}.{name}")
