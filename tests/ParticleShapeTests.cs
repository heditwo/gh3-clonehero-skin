using System.Numerics;
using System.Text.Json;
using Gh3Skin;

internal static class ParticleShapeTests
{
    public static void Run()
    {
        if (Environment.GetEnvironmentVariable("GH3_NATIVE_PARTICLE_BIRTH_ORACLE") is not { } path) return;
        using var oracle = JsonDocument.Parse(File.ReadAllText(path));
        var defaults = oracle.RootElement.GetProperty("defaults");
        var defaultDefinition = new StageParticleDefinition(Gh3ScriptRuntime.Structure(new[] { new Gh3QbField(Gh3Checksum.Value("QuickMaterial"),
            Gh3ScriptRuntime.Structure(new[] { new Gh3QbField(Gh3Checksum.Value("DiffuseTexture"), Gh3ScriptRuntime.Key("texture")) })) }));
        Check(defaultDefinition.Maximum == defaults.GetProperty("maximum").GetInt32() && defaultDefinition.Follow == defaults.GetProperty("follow").GetSingle() &&
            defaultDefinition.BounceCoefficient == defaults.GetProperty("bounceCoefficient").GetSingle(), "Native particle scalar defaults differ");
        Check(defaultDefinition.Life == new Vector2(Values(defaults.GetProperty("life"))[0], Values(defaults.GetProperty("life"))[1]) &&
            defaultDefinition.Width == Vector2.Zero && defaultDefinition.Height == Vector2.Zero, "Native particle range defaults differ");
        var defaultShape = defaultDefinition.CreateShape();
        Check(defaultShape.Elevation == new Vector3(0, MathF.PI, MathF.PI) && defaultShape.Sweep == defaultShape.Elevation, "Native particle spread defaults differ");
        var initialization = oracle.RootElement.GetProperty("initialization");
        var initialRandom = new Gh3Random(12345);
        Check(new[] { initialRandom.Next(64), initialRandom.Next(2), initialRandom.Next(65536) }.SequenceEqual(initialization.GetProperty("draws").EnumerateArray().Select(value => value.GetInt32())), "Native particle initialization random draws differ");
        var shape = new StageParticleShape();
        foreach (var row in oracle.RootElement.GetProperty("controls").EnumerateArray())
        {
            var name = row.GetProperty("command").GetString()!;
            var parameters = Gh3ScriptRuntime.Structure(row.GetProperty("parameters").EnumerateObject().Select(field => new Gh3QbField(Gh3Checksum.Value(field.Name),
                field.Value.ValueKind == JsonValueKind.Array ? new(field.Value.GetArrayLength() == 2 ? 5u : 6u, -1, Values(field.Value)) : Gh3ScriptRuntime.Number(field.Value.GetDouble()))));
            Check(shape.Execute(Gh3Checksum.Value(name), parameters), name + " is missing");
            var after = row.GetProperty("after");
            Compare(shape.Elevation, after, "elevation", name);
            Compare(shape.Sweep, after, "sweep", name);
            Compare(shape.Target, after, "target", name);
            Compare(shape.Angle, after, "angle", name);
            CompareMatrix(shape.TargetBasis, after.GetProperty("targetBasis"), name);
            CompareMatrix(shape.AngleBasis, after.GetProperty("angleBasis"), name);
            Check(shape.RandomAngle == after.GetProperty("randomAngle").GetBoolean() && shape.Circular == after.GetProperty("circular").GetBoolean(), "Native particle flags differ: " + name);
        }
        foreach (var row in oracle.RootElement.GetProperty("spreads").EnumerateArray())
        {
            shape.SourceSpread(row.GetProperty("source").GetSingle());
            Compare(shape.Elevation, row, "elevation", "Source AngleSpread");
            Compare(shape.Sweep, row, "sweep", "Source AngleSpread");
        }
        foreach (var row in oracle.RootElement.GetProperty("births").EnumerateArray())
        {
            var random = new Gh3Random(12345);
            var draws = new List<int>();
            int Next(int limit) { var value = random.Next(limit); draws.Add(value); return value; }
            shape = new() { Radiate = row.GetProperty("radiate").GetBoolean(), Circular = row.GetProperty("circular").GetBoolean(),
                TargetPosition = row.GetProperty("targetPosition").ValueKind == JsonValueKind.Null ? null : row.GetProperty("targetPosition").GetSingle() };
            shape.SetTarget(Vector(row.GetProperty("target")));
            shape.SetAngle(Vector(row.GetProperty("angle")));
            shape.SetElevation(-.125f, .25f);
            shape.SetSweep(0, 2);
            var pathDelta = row.GetProperty("previous").ValueKind == JsonValueKind.Null ? Vector3.Zero : Vector(row.GetProperty("previous")) - new Vector3(1, 2, 3);
            foreach (var particle in row.GetProperty("particles").EnumerateArray())
            {
                var birth = shape.Emit(new(2, 3, 4), new(2, 5), new(1, 3), Next, Vector(row.GetProperty("emitterVelocity")), row.GetProperty("velocityWeight").GetSingle(), pathDelta != Vector3.Zero);
                var context = $"radiate={shape.Radiate}, circular={shape.Circular}, target={shape.Target}, angle={shape.Angle}";
                Compare(new Vector3(1, 2, 3) + (new Vector3(birth.Position.X, birth.Position.Y, -birth.Position.Z) + pathDelta * birth.PathFraction), particle, "position", context);
                Compare(new(birth.Velocity.X, birth.Velocity.Y, -birth.Velocity.Z), particle, "velocity", context);
                Check(Math.Abs(birth.Life - particle.GetProperty("life").GetSingle()) < .000001 && birth.Rotation == particle.GetProperty("rotation").GetSingle() && birth.ColorMix == particle.GetProperty("color").GetSingle(),
                    "Native birth lifetime, angle, or color random differs: " + context);
            }
            Check(draws.SequenceEqual(row.GetProperty("draws").EnumerateArray().Select(value => value.GetInt32())), "Native birth random draw order differs");
            Check(new[] { (uint)random.Seed1, (uint)random.Seed2, (uint)random.Seed3 }.SequenceEqual(row.GetProperty("seeds").EnumerateArray().Select(value => value.GetUInt32())), "Native birth final random states differ");
        }
        foreach (var row in oracle.RootElement.GetProperty("sourceRanges").EnumerateArray())
        {
            var fields = defaultDefinition.Source.Fields.Concat(row.GetProperty("fields").EnumerateArray().Select(field => new Gh3QbField(Gh3Checksum.Value(field[0].GetString()!),
                field[1].ValueKind == JsonValueKind.Array ? new(5, -1, Values(field[1])) : Gh3ScriptRuntime.Number(field[1].GetDouble()))));
            var parsed = new StageParticleDefinition(Gh3ScriptRuntime.Structure(fields)).CreateShape();
            Compare(parsed.Elevation, row, "elevation", "Source spread field order");
            Compare(parsed.Sweep, row, "sweep", "Source spread field order");
        }
        foreach (var row in oracle.RootElement.GetProperty("capacities").EnumerateArray())
        {
            Gh3QbValue Number(string name) => Gh3ScriptRuntime.Number(row.GetProperty(name).GetDouble());
            Gh3QbField Field(string name, Gh3QbValue value) => new(Gh3Checksum.Value(name), value);
            var definition = new StageParticleDefinition(Gh3ScriptRuntime.Structure(new[] {
                Field("QuickMaterial", Gh3ScriptRuntime.Structure(new[] { Field("DiffuseTexture", Gh3ScriptRuntime.Key("texture")) })),
                Field("Max", Number("maximum")), Field("EmitNum", Number("count")), Field("Emit_Rate", Number("rate")), Field("LifeRange", new(5, -1, Values(row.GetProperty("life"))))
            }));
            Check(definition.Maximum == row.GetProperty("capacity").GetInt32(), "Native particle capacity calculation differs");
        }
        foreach (var row in oracle.RootElement.GetProperty("rotations").EnumerateArray())
        {
            var random = new Gh3Random(12345);
            var speeds = Values(row.GetProperty("speed"));
            foreach (var value in Values(row.GetProperty("values")))
            {
                var actual = (random.Next(10000) * (speeds[1] - speeds[0]) * .0001f + speeds[0]) * (2 * MathF.PI);
                Check(Math.Abs(actual - value) < .000001f, "Native particle rotation-speed units or initialization differs");
            }
        }
        Console.WriteLine("Native particle direction controls, source spread, birth geometry, and random draws passed.");
    }

    private static float[] Values(JsonElement value) => value.EnumerateArray().Select(value => value.GetSingle()).ToArray();
    private static Vector3 Vector(JsonElement value) { var values = Values(value); return new(values[0], values[1], values[2]); }
    private static void Compare(Vector3 actual, JsonElement row, string field, string context) =>
        Check(Vector3.Distance(actual, Vector(row.GetProperty(field))) < .00001f, $"Native {field} differs ({context}): {actual}; expected {row.GetProperty(field)}");
    private static void CompareMatrix(Matrix4x4 actual, JsonElement expected, string context)
    {
        var values = new[] { actual.M11, actual.M12, actual.M13, actual.M14, actual.M21, actual.M22, actual.M23, actual.M24,
            actual.M31, actual.M32, actual.M33, actual.M34, actual.M41, actual.M42, actual.M43, actual.M44 };
        Check(values.Zip(Values(expected)).All(pair => Math.Abs(pair.First - pair.Second) < .000001f), $"Native direction matrix differs: {context}: {actual}; expected {expected}");
    }
    private static void Check(bool success, string message) { if (!success) throw new Exception(message); }
}
