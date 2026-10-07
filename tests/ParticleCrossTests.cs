using System.Numerics;
using System.Text.Json;
using Gh3Skin;

internal static class ParticleCrossTests
{
    public static void Run()
    {
        if (Environment.GetEnvironmentVariable("GH3_NATIVE_PARTICLE_CROSS_ORACLE") is not { } path) return;
        using var oracle = JsonDocument.Parse(File.ReadAllText(path));
        foreach (var row in oracle.RootElement.GetProperty("rows").EnumerateArray())
        {
            var size = Vector(row, "startSize") + (Vector(row, "endSize") - Vector(row, "startSize")) * (row.GetProperty("age").GetSingle() / row.GetProperty("life").GetSingle());
            var basis = StageParticleGeometry.CrossBasis(size, Vector(row, "velocity"), Vector(row, "right"), Vector(row, "up"), row.GetProperty("align").GetBoolean(),
                row.GetProperty("rotate").GetBoolean() ? row.GetProperty("angle").GetSingle() : 0, row.GetProperty("type").GetString() == "x",
                row.GetProperty("rotate3d").GetBoolean(), row.GetProperty("randomIndex").GetInt32(), row.GetProperty("colorMix").GetSingle(), Vector(row, "alignTo"));
            var layout = row.GetProperty("layout").GetString();
            var uvBounds = StageParticleGeometry.TextureBounds(layout is "Layout1x2" or "Layout2x2" ? 2 : 1, layout is "Layout2x1" or "Layout2x2" ? 2 : 1,
                row.GetProperty("frame").GetInt32() + (int)(row.GetProperty("age").GetSingle() * row.GetProperty("fps").GetSingle()));
            var quads = basis.Visible ? StageParticleGeometry.Cross(Vector(row, "position"), basis.Right, basis.Up, basis.Depth,
                row.GetProperty("type").GetString() == "x", uvBounds).ToArray() : Array.Empty<StageParticleQuad>();
            if (row.GetProperty("type").GetString() == "flat")
            {
                var flatBasis = row.GetProperty("align").GetBoolean() ? StageParticleGeometry.Basis(new(size.X, size.Y), Vector(row, "velocity"),
                    Vector(row, "right"), Vector(row, "up"), true, row.GetProperty("pathTime").GetSingle(), row.GetProperty("angle").GetSingle()) : new StageParticleBasis(basis.Right, basis.Up);
                var position = Vector(row, "position");
                quads = flatBasis.Visible ? new[] { new StageParticleQuad(position - flatBasis.Right - flatBasis.Up, position + flatBasis.Right - flatBasis.Up,
                    position + flatBasis.Right + flatBasis.Up, position - flatBasis.Right + flatBasis.Up, new(uvBounds.X, uvBounds.W, uvBounds.Z, uvBounds.Y)) } : Array.Empty<StageParticleQuad>();
            }
            var vertices = row.GetProperty("vertices").EnumerateArray().ToArray();
            var unitAxes = StageParticleGeometry.CrossBasis(new(2, 2, 2), Vector(row, "velocity"), Vector(row, "right"), Vector(row, "up"), row.GetProperty("align").GetBoolean(),
                row.GetProperty("rotate").GetBoolean() ? row.GetProperty("angle").GetSingle() : 0, row.GetProperty("type").GetString() == "x",
                row.GetProperty("rotate3d").GetBoolean(), row.GetProperty("randomIndex").GetInt32(), row.GetProperty("colorMix").GetSingle(), Vector(row, "alignTo"));
            Check(quads.Length * 6 == vertices.Length, "Crossed particle vertex count differs");
            for (var index = 0; index < quads.Length; index++)
            {
                var quad = quads[index];
                var lighting = row.GetProperty("type").GetString() == "flat" ? StageParticleGeometry.FlatLighting(Vector(row, "velocity"), Vector(row, "right"), Vector(row, "up"),
                    row.GetProperty("align").GetBoolean(), row.GetProperty("pathTime").GetSingle(), row.GetProperty("rotate").GetBoolean() ? row.GetProperty("angle").GetSingle() : 0,
                    row.GetProperty("rotate3d").GetBoolean(), row.GetProperty("randomIndex").GetInt32(), row.GetProperty("colorMix").GetSingle(), Vector(row, "alignTo")) :
                    StageParticleGeometry.CrossLighting(unitAxes, row.GetProperty("type").GetString() == "x", index);
                var positions = new[] { quad.A, quad.B, quad.C, quad.A, quad.C, quad.D };
                var uv = new[] { new Vector2(quad.Uv.Z, quad.Uv.Y), new Vector2(quad.Uv.X, quad.Uv.Y), new Vector2(quad.Uv.X, quad.Uv.W),
                    new Vector2(quad.Uv.Z, quad.Uv.Y), new Vector2(quad.Uv.X, quad.Uv.W), new Vector2(quad.Uv.Z, quad.Uv.W) };
                for (var vertex = 0; vertex < 6; vertex++)
                {
                    var expected = vertices[index * 6 + vertex];
                    var tangent = expected.GetProperty("tangentFrame").EnumerateArray().Select(value => value.GetSingle()).ToArray();
                    var axes = new[] { lighting.Right, lighting.Up, lighting.Depth };
                    for (var axis = 0; axis < 3; axis++)
                        Check(Vector3.Distance(axes[axis], new(tangent[axis * 4], tangent[axis * 4 + 1], tangent[axis * 4 + 2])) < .00001f,
                            "Crossed particle lighting basis differs: " + row);
                    Check(Math.Abs(size.X - expected.GetProperty("softEdgeWidth").GetSingle()) < .000001f, "Native crossed-particle soft-edge width differs");
                    Check(Vector3.Distance(positions[vertex], Vector(expected, "position")) < .00001f, "Crossed particle vertex differs: " + row);
                    var values = expected.GetProperty("uv").EnumerateArray().Select(value => value.GetSingle()).ToArray();
                    Check(Vector2.Distance(uv[vertex], new(values[0], values[1])) < .000001f, "Crossed particle texture coordinates differ");
                }
            }
        }
        Console.WriteLine("Native crossed particle geometry checks passed.");
    }

    private static Vector3 Vector(JsonElement row, string name) { var values = row.GetProperty(name).EnumerateArray().Select(value => value.GetSingle()).ToArray(); return new(values[0], values[1], values[2]); }
    private static void Check(bool success, string message) { if (!success) throw new Exception(message); }
}
