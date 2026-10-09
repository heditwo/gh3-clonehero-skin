using System.Text.Json;

namespace Gh3Skin;

internal sealed class HudTuning
{
    public float ScoreOffsetX { get; private init; }
    public float ScoreOffsetY { get; private init; }
    public float RockScale { get; private init; }
    public float RockVerticalScale { get; private init; }
    public float RockOffsetX { get; private init; }
    public float RockOffsetY { get; private init; }
    public float[] RollAlphas { get; private init; } = Array.Empty<float>();
    public float[] RollOffsets { get; private init; } = Array.Empty<float>();

    public static HudTuning Load(string file)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(file));
        var settings = document.RootElement;
        var tuning = new HudTuning
        {
            ScoreOffsetX = settings.GetProperty("scoreOffsetX").GetSingle(),
            ScoreOffsetY = settings.GetProperty("scoreOffsetY").GetSingle(),
            RockScale = settings.GetProperty("rockScale").GetSingle(),
            RockVerticalScale = settings.GetProperty("rockVerticalScale").GetSingle(),
            RockOffsetX = settings.GetProperty("rockOffsetX").GetSingle(),
            RockOffsetY = settings.GetProperty("rockOffsetY").GetSingle(),
            RollAlphas = settings.GetProperty("rollAlpha").EnumerateArray().Select(value => value.GetSingle()).ToArray(),
            RollOffsets = settings.GetProperty("rollOffset720").EnumerateArray().Select(value => value.GetSingle()).ToArray()
        };
        if (tuning.RollAlphas.Length < 2 || tuning.RollAlphas.Length != tuning.RollOffsets.Length)
            throw new InvalidDataException($"Invalid HUD tuning: {file}");
        return tuning;
    }
}
