using System.Globalization;

namespace Gh3Skin;

internal sealed class SkinOptions
{
    public bool KeepStockGems { get; private init; }
    public bool KeepStockFlames { get; private init; }
    public float? DisplayPower { get; private init; }
    public float? DisplayRock { get; private init; }
    public int ForcePowerAtCombo { get; private init; }
    public float CaptureAfter { get; private init; }
    public IReadOnlyList<float> CaptureTimes { get; private init; } = Array.Empty<float>();
    public string CaptureDirectory { get; private init; } = "/tmp/opencode/gh3-matrix-mod";

    public static SkinOptions FromEnvironment()
    {
        var captureAfter = ParseFloat("GH3_SKIN_CAPTURE_AFTER") ?? 0;
        captureAfter = captureAfter > 0 ? captureAfter : 0;
        var captureTimes = (Environment.GetEnvironmentVariable("GH3_SKIN_CAPTURE_TIMES") ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(value => float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var time) ? time : 0)
            .Where(time => time > 0)
            .ToArray();
        var power = captureAfter > 0 ? ParseFloat("GH3_SKIN_PREVIEW_POWER") : null;
        var rock = captureAfter > 0 ? ParseFloat("GH3_SKIN_PREVIEW_ROCK") : null;
        power = ParseFloat("GH3_SKIN_FORCE_POWER") ?? power;
        rock = ParseFloat("GH3_SKIN_FORCE_ROCK") ?? rock;

        return new SkinOptions
        {
            KeepStockGems = Environment.GetEnvironmentVariable("GH3_SKIN_KEEP_STOCK_GEMS") == "1",
            KeepStockFlames = Environment.GetEnvironmentVariable("GH3_SKIN_KEEP_STOCK_FLAMES") == "1",
            DisplayPower = power.HasValue ? Math.Clamp(power.Value, 0, 1) : null,
            DisplayRock = rock.HasValue ? Math.Clamp(rock.Value, 0, 1) : null,
            ForcePowerAtCombo = int.TryParse(Environment.GetEnvironmentVariable("GH3_SKIN_FORCE_POWER_AT_COMBO"), out var combo)
                ? Math.Max(0, combo) : 0,
            CaptureAfter = captureAfter,
            CaptureTimes = captureTimes,
            CaptureDirectory = Environment.GetEnvironmentVariable("GH3_SKIN_CAPTURE_DIR") ?? "/tmp/opencode/gh3-matrix-mod"
        };
    }

    private static float? ParseFloat(string name) =>
        float.TryParse(Environment.GetEnvironmentVariable(name), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value : null;
}
