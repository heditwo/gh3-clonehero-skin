using System.Numerics;

namespace Gh3Skin;

// One light node of a level's light array, as GH3's light setup reads it (tests/LightNativeTests.cs compares it with the
// native oracle). Field checksums without a known name stay as hex values.
internal sealed class StageLightNative
{
    // GH3: cos of half the cone angle: degrees * pi/360 as float32.
    private const float HalfAngleRadiansPerDegree = .008726646192371845f;
    // GH3: guards the division of the cone and distance falloff.
    private const float FalloffEpsilon = .0001f;
    public uint Id { get; }
    public uint Group { get; }
    public int Type { get; }
    public uint Flags { get; }
    public Vector4 Color { get; }
    public Vector4 Position { get; }
    public Vector4 Direction { get; }
    public Vector4 Attenuation { get; }
    public Vector4 Cone { get; }
    public float Radius { get; }
    public float Intensity { get; }
    public float Specular { get; }
    public float SpecularPower { get; }
    public Vector3 BoundsPosition { get; }
    public Vector3 BoundsExtent { get; }
    public Vector4[] Harmonics { get; }

    public StageLightNative(Gh3QbValue source)
    {
        float Number(string name, float fallback) => source.Field(name) is { Type: Gh3QbType.Integer or Gh3QbType.Float } value ? (float)Gh3ScriptRuntime.Numeric(value) : fallback;
        Vector3 Vector(uint name, Vector3 fallback) => source.Field(name)?.Data is float[] { Length: 3 } values ? new(values[0], values[1], values[2]) : fallback;
        Id = source.Field("Name")?.Integer ?? 0;
        Group = source.Field("LightGroup")?.Integer ?? Gh3Keys.DefaultGroup;
        // GH3: the light types point, spot, an unnamed one (0x1e2ee0ec, with spherical harmonics), and ambient select the
        // shader light types 2-5.
        Type = source.Field("Type")?.Integer switch { Gh3Keys.Point => 2, Gh3Keys.Spot => 3, 0x1e2ee0ec => 4, Gh3Keys.Ambient => 5, _ => 1 };
        // GH3: the light flag bits.
        Flags = (source.Flag("CreatedAtStart") ? 2u : 0u) | (source.Flag("Global") ? 1u : 0u) |
            (source.Fields.Any(field => field.Name == 0 && field.Value.Integer == Gh3Keys.Dynamic) ? 4u : 0u) | (source.Flag("Shadow") ? 8u : 0u);
        Color = new(Vector(Gh3Checksum.Value("LightColor"), Vector3.Zero), source.Field("LightColor")?.Data is float[] { Length: 3 } ? 1 : 0);
        Position = new(Vector(Gh3Checksum.Value("Pos"), Vector3.Zero), 1);
        var values = source.Field("RotationQuat")?.Items.Select(value => (float)Gh3ScriptRuntime.Numeric(value)).ToArray();
        var rotation = values is { Length: 4 } ? new Quaternion(values[0], values[1], values[2], values[3]) : Quaternion.Identity;
        var square = ((rotation.X * rotation.X + rotation.Y * rotation.Y) + rotation.Z * rotation.Z) + rotation.W * rotation.W;
        var scale = square > 0 ? 2 / square : 0;
        var x = rotation.X * scale;
        var y = rotation.Y * scale;
        var z = rotation.Z * scale;
        Direction = new(-(rotation.X * y - rotation.W * z), -(1 - (rotation.Z * z + rotation.X * x)), -(rotation.Y * z + rotation.W * x), 0);
        // GH3: the attenuation defaults, and the far constant of lights without distance falloff.
        Radius = Number("AttenEnd", 15);
        // GH3: see above.
        Attenuation = Type is 2 or 3 ? DistanceAttenuation(Number("AttenStart", 7.5f), Radius) : new(0, -2000, 0, 0);
        if (Type is 2 or 3)
        {
            var inner = (float)Math.Cos(Number("Hotspot", 0) * HalfAngleRadiansPerDegree);
            // GH3: the default cone field of 45 degrees.
            var outer = (float)Math.Cos(Number("Field", 45) * HalfAngleRadiansPerDegree);
            Cone = new(inner, outer, 1 / (inner - outer + FalloffEpsilon), 1);
        }
        else Cone = Vector4.One;
        Intensity = Number("Intensity", 1);
        Specular = Number("SpecularIntensity", 1);
        // GH3: the specular power comes from the shadowintensity field, else from an unnamed field.
        SpecularPower = (source.Field(Gh3Keys.ShadowIntensity) ?? source.Field(0xe8363d4b)) is { } power ? (float)Gh3ScriptRuntime.Numeric(power) : 1;
        var position = new Vector3(Position.X, Position.Y, Position.Z);
        var direction = new Vector3(Direction.X, Direction.Y, Direction.Z);
        // GH3: the unnamed bounds position and extent fields; omitted, the bounds cover the light's range.
        BoundsPosition = Vector(0x4c1d24af, Type is 1 or 3 ? position + direction * (Radius * .5f) : position);
        // GH3: see above.
        BoundsExtent = Vector(0x0dd3f28f, new Vector3(Type is 1 or 3 ? Radius * .5f : Radius));
        // GH3: the nine unnamed spherical-harmonic fields of an ambient light (Type 4).
        var keys = new uint[] { 0x8ee38f1a, 0xf9e4bf8c, 0x60edee36, 0x17eadea0, 0x898e4b03, 0xfe897b95, 0x67802a2f, 0x10871ab9, 0x80380728 };
        Harmonics = keys.Select(key => Type == 4 && source.Field(key) is { Type: Gh3QbType.Array } field && field.Items.Count >= 3 ?
            new Vector4((float)Gh3ScriptRuntime.Numeric(field.Items[0]), (float)Gh3ScriptRuntime.Numeric(field.Items[1]), (float)Gh3ScriptRuntime.Numeric(field.Items[2]), 0) : Vector4.Zero).ToArray();
    }

    public static Vector4 DistanceAttenuation(float near, float far)
    {
        var square = far * far;
        return new(square, 1 / (square - near * near + FalloffEpsilon), 0, 1);
    }
}
