namespace Gh3Skin;

// The skin folders in Mods/GH3Skin/assets. They mirror assets/ in the repository (see assets/README.md).
internal static class SkinAssetPaths
{
    public static string Gems(string root, string lane) => Path.Combine(root, "gems", lane);
    public static string Sustains(string root) => Path.Combine(root, "sustains");
    public static string Highway(string root) => Path.Combine(root, "highway");
    public static string BeatLines(string root) => Path.Combine(root, "highway", "beat-lines");
    public static string StarPowerHighway(string root) => Path.Combine(root, "highway", "star-power");
    public static string Hud(string root) => Path.Combine(root, "hud");
    public static string ReadyAnimation(string root) => Path.Combine(root, "hud", "ready");
    public static string Fonts(string root) => Path.Combine(root, "fonts");
    public static string HitEffects(string root) => Path.Combine(root, "hit-effects");
    public static string YouRockFlames(string root) => Path.Combine(root, "hud", "you-rock", "you-rock-flames.png");
    public static string HudTuning(string root) => Path.Combine(root, "hud", "counter-tuning.json");

    public static string HudTextureFolder(string root, string name) =>
        name.StartsWith("HUD_rock_", StringComparison.Ordinal) ? Path.Combine(root, "hud", "rock-meter")
        : name.StartsWith("HUD_score_", StringComparison.Ordinal) ? Path.Combine(root, "hud", "score")
        : name.StartsWith("HUD_counter_", StringComparison.Ordinal) ? Path.Combine(root, "hud", "counter")
        : name == "hud-glowburst" ? Path.Combine(root, "hud", "messages")
        : name.StartsWith("gh3-", StringComparison.Ordinal) ? HitEffects(root)
        : throw new ArgumentException($"No asset folder for {name}");
}
