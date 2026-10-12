"""The GH3 venues: their zone names and stage package IDs. The display names come from GH3 (pipeline.gh3.levels)."""

# The GH3 zones that the venues step exports (the rows of LevelZones without z_viewer, the model viewer).
VENUES = (
    "z_party",
    "z_dive",
    "z_video",
    "z_artdeco",
    "z_prison",
    "z_wikker",
    "z_budokan",
    "z_hell",
    "z_soundcheck",
    "z_credits",
)


def stage_id(zone: str) -> str:
    """The stage package ID of a GH3 zone (z_dive -> gh3.dive)."""
    return "gh3." + zone.removeprefix("z_")
