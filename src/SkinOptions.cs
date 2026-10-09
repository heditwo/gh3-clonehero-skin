using System.Globalization;

namespace Gh3Skin;

internal sealed class SkinOptions
{
    public bool KeepStockGems { get; private init; }
    public bool KeepStockFlames { get; private init; }
    public float? DisplayPower { get; private init; }
    public float? DisplayRock { get; private init; }
    public int ForcePowerAtCombo { get; private init; }
    public int ForceCombo { get; private init; }
    public int ForceComboBreak { get; private init; }
    public float ForceWhammyRate { get; private init; }
    public bool ForceStarPowerActive { get; private init; }
    public float CaptureAfter { get; private init; }
    public IReadOnlyList<float> CaptureTimes { get; private init; } = Array.Empty<float>();
    public string CaptureDirectory { get; private init; } = "";
    public string ScreenshotPath { get; private init; } = "";
    public bool CaptureSongClock { get; private init; }

    public static SkinOptions FromEnvironment()
    {
        var captureAfter = ParseFloat("GH3_SKIN_CAPTURE_AFTER") ?? 0;
        captureAfter = captureAfter > 0 ? captureAfter : 0;
        var songClock = Environment.GetEnvironmentVariable("GH3_SKIN_CAPTURE_CLOCK") == "song";
        // On the song clock, negative times capture GH3's intro (the 8 s before the song).
        var captureTimes = (Environment.GetEnvironmentVariable("GH3_SKIN_CAPTURE_TIMES") ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(value => float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var time) ? time : 0)
            .Where(time => float.IsFinite(time) && (time > 0 || songClock && time < 0))
            .Distinct()
            .OrderBy(time => time)
            .ToArray();
        var capture = captureAfter > 0 || captureTimes.Length > 0;
        var power = capture ? ParseFloat("GH3_SKIN_PREVIEW_POWER") : null;
        var rock = capture ? ParseFloat("GH3_SKIN_PREVIEW_ROCK") : null;
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
            ForceCombo = ParseInt("GH3_SKIN_FORCE_COMBO"),
            ForceComboBreak = ParseInt("GH3_SKIN_FORCE_COMBO_BREAK"),
            ForceWhammyRate = Math.Max(0, ParseFloat("GH3_SKIN_FORCE_WHAMMY") ?? 0),
            ForceStarPowerActive = Environment.GetEnvironmentVariable("GH3_SKIN_FORCE_STAR_POWER_ACTIVE") == "1",
            CaptureAfter = captureAfter,
            CaptureTimes = captureTimes,
            CaptureSongClock = songClock,
            CaptureDirectory = Path.GetFullPath(Environment.GetEnvironmentVariable("GH3_SKIN_CAPTURE_DIR") ??
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Captures", "GH3Skin")),
            ScreenshotPath = Path.GetFullPath(Environment.GetEnvironmentVariable("GH3_SKIN_SCREENSHOT") ??
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Captures", "GH3Skin", "preview.png"))
        };
    }

    private static int ParseInt(string name) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? Math.Max(0, value) : 0;

    private static float? ParseFloat(string name) =>
        float.TryParse(Environment.GetEnvironmentVariable(name), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && float.IsFinite(value)
            ? value : null;
}

// Test override for the displayed note streak: GH3_SKIN_FORCE_COMBO adds a fixed amount to a running streak, and
// GH3_SKIN_FORCE_COMBO_BREAK breaks the shown streak once it reaches that value, as if a note was missed.
internal sealed class ComboOverride
{
    private readonly int _offset;
    private readonly int _breakAt;
    private int _brokenAt = -1;

    public ComboOverride(int offset, int breakAt)
    {
        _offset = offset;
        _breakAt = breakAt;
    }

    public void Reset() => _brokenAt = -1;

    public int Apply(int combo)
    {
        if (_offset <= 0) return combo;
        if (_brokenAt >= 0)
        {
            if (combo < _brokenAt) _brokenAt = 0;
            return combo - _brokenAt;
        }
        if (combo == 0) return 0;
        var shown = combo + _offset;
        if (_breakAt <= 0 || shown < _breakAt) return shown;
        _brokenAt = combo;
        return 0;
    }
}
