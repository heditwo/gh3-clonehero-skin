"""Default locations: the repository, its generated assets, and the owner's GH3 and Clone Hero copies."""

from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
ASSETS = ROOT/"assets"
STAGES = ASSETS/"stages"
STAGE_PACKS = ASSETS/"stage-packs"
LIBRARY = ASSETS/"libraries/gh3-band-v1"
GH3_DATA = Path.home()/"games/gh3-test-tools/game/DATA"
GAME = Path.home()/"games/clonehero-gh3-melon"
GH3_SKIN_ASSETS = Path.home()/"games/gh3-skin-assets"
