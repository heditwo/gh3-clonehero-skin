namespace Gh3Skin;

internal readonly record struct GameplayState(int Score, int Combo, float Rock, float StarPower,
    int Multiplier = 1, bool StarPowerActive = false);

internal static class HudState
{
    public const float RockLowThreshold = 1f / 3;
    public const float RockHighThreshold = 2f / 3;
    public const float RockRedBoundaryAngle = 19.5f;
    public const float RockGreenBoundaryAngle = -18.5f;

    public static string MultiplierTexture(int multiplier, bool active) => active
        ? $"HUD_score_nixie_{Math.Clamp(multiplier / 2, 1, 4) * 2}b"
        : $"HUD_score_nixie_{Math.Clamp(multiplier, 1, 4)}a";

    public static int BaseMultiplier(int multiplier, bool active) =>
        Math.Clamp(active ? multiplier / 2 : multiplier, 1, 4);

    public static float UnitValue(float value) => float.IsFinite(value) ? Math.Clamp(value, 0, 1) : 0;

    public static float RockNeedleAngle(float rock)
    {
        rock = UnitValue(rock);
        if (rock <= RockLowThreshold)
            return RockRedBoundaryAngle + (43 - RockRedBoundaryAngle) * ((RockLowThreshold - rock) / RockLowThreshold);
        if (rock <= 0.5f)
            return RockRedBoundaryAngle * ((0.5f - rock) / (0.5f - RockLowThreshold));
        if (rock <= RockHighThreshold)
            return RockGreenBoundaryAngle * ((rock - 0.5f) / (RockHighThreshold - 0.5f));
        return RockGreenBoundaryAngle + (-43 - RockGreenBoundaryAngle) * ((rock - RockHighThreshold) / (1 - RockHighThreshold));
    }

    public static float StarPowerBulbFill(float power, int bulb) => Math.Clamp(UnitValue(power) * 6 - bulb, 0, 1);
}
