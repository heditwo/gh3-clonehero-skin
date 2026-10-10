using System.Numerics;

namespace Gh3Skin;

internal sealed class StageTextureAnimation
{
    public int Columns { get; set; } = 1;
    public int Rows { get; set; } = 1;
    public float Fps { get; set; }
    public float Offset { get; set; }

    public void Validate()
    {
        if (Columns is < 1 or > 256 || Rows is < 1 or > 256 || !float.IsFinite(Fps) || Math.Abs(Fps) > StageLimits.MaximumMagnitude || !float.IsFinite(Offset) || Math.Abs(Offset) > StageLimits.MaximumMagnitude)
            throw new InvalidDataException("Invalid texture animation");
    }

    public Vector4 Transform(double time, float? fps = null)
    {
        var frame = (float)time * (fps ?? Fps) + Offset;
        var count = Columns * Rows;
        frame %= count;
        var row = MathF.Floor(frame / Columns);
        var column = MathF.Floor(frame - row * Columns);
        return new(1f / Columns, 1f / Rows, column / Columns, (Rows - row - 1) / Rows);
    }
}
