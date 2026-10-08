namespace Gh3Skin;

internal enum SustainVariant
{
    Green,
    Red,
    Yellow,
    Blue,
    Orange,
    Star,
    Dead
}

internal static class SustainState
{
    public const string SpritePrefix = "spr_sustain_strip6_";

    // Clone Hero's sustain atlas: frame 0 is held, frames 1-4 are unheld, and frame 5 is a dropped sustain.
    public static bool TryFrame(string sprite, out int frame)
    {
        frame = -1;
        return sprite.Length == SpritePrefix.Length + 1 && sprite.StartsWith(SpritePrefix, StringComparison.Ordinal) &&
            (frame = sprite[^1] - '0') is >= 0 and <= 5;
    }

    // GH3 swaps the whole bar material: MakeDeadWhammy, MakeStarWhammy, or the lane's MakeNormalWhammy.
    public static SustainVariant Variant(int frame, int lane, bool starPowerActive) =>
        frame == 5 ? SustainVariant.Dead :
        starPowerActive ? SustainVariant.Star :
        (SustainVariant)Math.Clamp(lane, 0, 4);

    // GH3 SetNewWhammyValue (GH3.exe 0x41de60) shifts the player's wibble array by whammy_wibble_speed (2) entries each
    // 60 Hz frame and writes the newest value into entry 0. The WhammyBar mesh (0x601d30) gives each row of 8 highway
    // lines the entry index 128 - row, so entry 0 is at the strike line (line 1024) and entry 128 at the far end (line 0).
    public const float WibbleEntries = 128;
    public const float WibbleEntriesPerSecond = 2 * 60f;

    // control_whammy_pitchshift: whammy_scale = (len * 0.5 + 0.5) * 2 = len + 1. The WhammyBar vertex shader multiplies
    // each row's horizontal offset by its wibble entry, so a full deflection doubles the bar width at that row.
    public static float WhammyWidth(float deflection) => 1 + Math.Clamp(deflection, 0, 1);

    // Clone Hero's whammy texture has one row per channel; channel 0 is the open note, so lanes start at row 1.
    public static int WhammyChannel(int lane) => lane + 1;
}
