namespace Gh3Skin;

internal static class GameCompatibility
{
    public const string Version = "1.1.0.6142";
    public static readonly string[] Platforms = { "LinuxPlayer", "WindowsPlayer" };

    public static bool Supports(string version) =>
        version is Version or "v1.1.0.6142" or "1.1.0.6142-final" or "v1.1.0.6142-final";

    public static string? Problem(string version, string platform)
    {
        if (!Supports(version))
            return $"GH3 skin requires Clone Hero {Version}; found {version}. The skin is off.";
        if (!Platforms.Contains(platform))
            return $"GH3 skin supports Clone Hero {Version} on Linux and Windows; found {platform}. The skin is off.";
        return null;
    }
}
