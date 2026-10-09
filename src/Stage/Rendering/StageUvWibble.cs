using System.Numerics;

namespace Gh3Skin;

// GH3 Wibble_Simple vertex shader: the diffuse UV turns about (c9, c10) at c8 rad/s, then scrolls by
// c0·t + c1·sin(c2·t + c3) in U and c4·t + c5·sin(c6·t + c7) in V. UVs are in GH3's top-down V convention.
internal sealed class StageUvWibble
{
    private readonly float[] _c;

    public StageUvWibble(float[] constants)
    {
        if (constants.Length != 11 || !constants.All(float.IsFinite)) throw new InvalidDataException("Invalid GH3 UV wibble");
        _c = constants;
    }

    public float PivotU => _c[9];
    public float PivotV => _c[10];

    // Returns (cos, sin, U offset, V offset). GH3 wraps the scroll to ±8 to keep shader precision.
    public Vector4 Transform(double time)
    {
        var t = (float)time;
        var u = (_c[0] * t + _c[1] * MathF.Sin(_c[2] * t + _c[3])) % 8;
        var v = (_c[4] * t + _c[5] * MathF.Sin(_c[6] * t + _c[7])) % 8;
        var angle = _c[8] * t % (2 * MathF.PI);
        return new(MathF.Cos(angle), MathF.Sin(angle), u, v);
    }
}
