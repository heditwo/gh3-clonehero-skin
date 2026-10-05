namespace Gh3Skin;

internal static class GameCompatibility
{
    public const string Version = "1.1.0.6142";

    public static bool Supports(string version) =>
        version is Version or "v1.1.0.6142" or "1.1.0.6142-final" or "v1.1.0.6142-final";
}
