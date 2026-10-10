"""Default locations: the repository, its generated assets, and the owner's GH3 and Clone Hero copies.

assets/README.md lists the folders under assets/. The mod reads the same layout from Mods/GH3Skin/assets
(src/SkinAssetPaths.cs)."""

from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
ASSETS = ROOT / "assets"
GEMS = ASSETS / "gems"
SUSTAINS = ASSETS / "sustains"
HIGHWAY = ASSETS / "highway"
BEAT_LINES = HIGHWAY / "beat-lines"
STAR_POWER_HIGHWAY = HIGHWAY / "star-power"
HUD = ASSETS / "hud"
ROCK_METER = HUD / "rock-meter"
SCORE = HUD / "score"
COUNTER = HUD / "counter"
MESSAGES = HUD / "messages"
YOU_ROCK = HUD / "you-rock"
READY_ANIMATION = HUD / "ready"
FONTS = ASSETS / "fonts"
HIT_EFFECTS = ASSETS / "hit-effects"
STAGES = ASSETS / "stages"
STAGE_PACKS = ASSETS / "stage-packs"
LIBRARY = ASSETS / "libraries/gh3-band-v1"
CHARACTER_LIBRARY = ASSETS / "libraries/gh3-characters-v1"
PACKAGES = ASSETS / "packages"
CHARACTERS = ASSETS / "characters"
INSTRUMENTS = ASSETS / "instruments"
GH3_DATA = Path.home() / "games/gh3-test-tools/game/DATA"
GAME = Path.home() / "games/clonehero-gh3-melon"
WINDOWS_GAME = Path.home() / "games/clonehero-win/game"
GH3_SKIN_ASSETS = Path.home() / "games/gh3-skin-assets"
