"""The GH3 venues: their zone names, display names, and stage package IDs."""

# The GH3 zones that the venues step exports, with the venue names of the GH3 menus.
VENUES = {
    "z_party": "Backyard Bash",
    "z_dive": "Mitch's Moose Lounge",
    "z_video": "Video Shoot",
    "z_artdeco": "Ye Olde Royal Odeon",
    "z_prison": "Shanker's Island",
    "z_wikker": "Desert Rock Tour",
    "z_budokan": "Kaiju Megadome",
    "z_hell": "Lou's Inferno",
    "z_soundcheck": "Soundcheck",
    "z_credits": "Credits",
}


def stage_id(zone: str) -> str:
    """The stage package ID of a GH3 zone (z_dive -> gh3.dive)."""
    return "gh3." + zone.removeprefix("z_")
