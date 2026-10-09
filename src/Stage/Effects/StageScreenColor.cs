using System.Numerics;

namespace Gh3Skin;

internal static class StageScreenColor
{
    public static uint Type => Gh3Checksum.Value("bright_sat");
    public static Gh3QbValue Defaults => Gh3ScriptRuntime.Structure(new[] {
        Field("ON", Gh3ScriptRuntime.Number(1)), Field("brightness", Gh3ScriptRuntime.Number(1)),
        Field("contrast", Gh3ScriptRuntime.Number(1)), Field("saturation", Gh3ScriptRuntime.Number(1)), Field("hue", Gh3ScriptRuntime.Number(0)),
        Field("red_mix", Mix(1, 0, 0)), Field("green_mix", Mix(0, 1, 0)), Field("blue_mix", Mix(0, 0, 1))
    });

    private static Gh3QbField Field(string name, Gh3QbValue value) => new(Gh3Checksum.Value(name), value);
    private static Gh3QbValue Mix(params int[] channels) => new(12, -1, channels.Select(value => Gh3ScriptRuntime.Number(value)).ToArray());

    public static Matrix4x4 Matrix(Gh3QbValue parameters)
    {
        float Number(string name, float fallback) => parameters.Field(name) is { Type: 1 or 2 } value ? (float)Gh3ScriptRuntime.Numeric(value) : fallback;
        if (Number("ON", 1) <= 0) return Matrix4x4.Identity;
        Vector3 Channels(string name, Vector3 fallback)
        {
            if (parameters.Field(name) is not { } value) return fallback;
            if (value.Type != 12 || value.Items.Count != 3 || value.Items.Any(channel => channel.Type is not (1 or 2)))
                throw new InvalidDataException("A screen color mix needs three numeric channels");
            return new((float)Gh3ScriptRuntime.Numeric(value.Items[0]), (float)Gh3ScriptRuntime.Numeric(value.Items[1]), (float)Gh3ScriptRuntime.Numeric(value.Items[2]));
        }
        var red = Channels("red_mix", Vector3.UnitX);
        var green = Channels("green_mix", Vector3.UnitY);
        var blue = Channels("blue_mix", Vector3.UnitZ);
        var mix = new Matrix4x4(red.X, green.X, blue.X, 0, red.Y, green.Y, blue.Y, 0, red.Z, green.Z, blue.Z, 0, 0, 0, 0, 1);
        var amount = Number("saturation", 1);
        var weights = new Vector3(.3086f, .6094f, .082f) * (1 - amount);
        var saturation = new Matrix4x4(weights.X + amount, weights.Y, weights.Z, 0,
            weights.X, weights.Y + amount, weights.Z, 0, weights.X, weights.Y, weights.Z + amount, 0, 0, 0, 0, 1);
        var rotation = Matrix4x4.Transpose(Matrix4x4.CreateFromAxisAngle(Vector3.Normalize(Vector3.One), Number("hue", 0) * (2 * MathF.PI)));
        var contrast = Number("contrast", 1);
        var bias = (1 - contrast) * .5f;
        var centered = new Matrix4x4(contrast, 0, 0, bias, 0, contrast, 0, bias, 0, 0, contrast, bias, 0, 0, 0, 1);
        return mix * Matrix4x4.CreateScale(Number("brightness", 1)) * saturation * rotation * centered;
    }
}
