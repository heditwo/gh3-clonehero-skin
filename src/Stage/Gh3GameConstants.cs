namespace Gh3Skin;

// GH3 game values that several stage parts read, with GH3's defaults for a bank without them.
internal static class Gh3Health
{
    // GH3: health is rock x 2 (0-2); health_poor_medium and health_medium_good split poor, medium, and good.
    public const double PoorMedium = .66;
    public const double MediumGood = 1.33;
    // GH3: CrowdLevelForSurges, the health above which the crowd surges.
    public const double CrowdSurges = 1.66;
}

// GH3: the slow_tempo, med_tempo, and fast_tempo defaults (beats per minute).
internal static class Gh3Tempo
{
    public const double Slow = 75;
    public const double Medium = 115;
    public const double Fast = 145;
}

// Unity render queues (Geometry, AlphaTest, Transparent).
internal static class RenderQueue
{
    public const int Geometry = 2000;
    public const int AlphaTest = 2450;
    public const int Transparent = 3000;
}
