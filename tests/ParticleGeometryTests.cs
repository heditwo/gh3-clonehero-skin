using System.Numerics;
using System.Text.Json;
using Gh3Skin;

internal static class ParticleGeometryTests
{
    public static void Run()
    {
        if (Environment.GetEnvironmentVariable("GH3_NATIVE_PARTICLE_GEOMETRY_ORACLE") is not { } path) return;
        using var oracle = JsonDocument.Parse(File.ReadAllText(path));
        foreach (var row in oracle.RootElement.GetProperty("rows").EnumerateArray())
        {
            var layout = row.GetProperty("layout").GetString()!;
            var columns = layout is "Layout2x2" or "Layout1x2" ? 2 : 1;
            var rows = layout is "Layout2x2" or "Layout2x1" ? 2 : 1;
            var age = row.GetProperty("age").GetSingle();
            var phase = age / row.GetProperty("life").GetSingle();
            var start = Values(row, "startSize");
            var end = Values(row, "endSize");
            var size = new Vector2(start[0] + (end[0] - start[0]) * phase, start[1] + (end[1] - start[1]) * phase);
            var align = row.GetProperty("pathTime").ValueKind != JsonValueKind.Null;
            var basis = StageParticleGeometry.Basis(size, Vector(row, "velocity"), Vector(row, "right"), Vector(row, "up"),
                align, align ? row.GetProperty("pathTime").GetSingle() : 0, row.GetProperty("rotate").GetBoolean() ? row.GetProperty("angle").GetSingle() : 0);
            var position = Vector(row, "position");
            var corners = new[] { position - basis.Right - basis.Up, position + basis.Right - basis.Up,
                position + basis.Right + basis.Up, position - basis.Right + basis.Up };
            var bounds = StageParticleGeometry.TextureBounds(columns, rows, row.GetProperty("frame").GetInt32() + (int)(age * row.GetProperty("fps").GetSingle()));
            var uv = new[] { new Vector2(bounds.Z, bounds.W), new Vector2(bounds.X, bounds.W), new Vector2(bounds.X, bounds.Y), new Vector2(bounds.Z, bounds.Y) };
            var indices = new[] { 0, 1, 2, 0, 2, 3 };
            var vertices = row.GetProperty("vertices").EnumerateArray().ToArray();
            Check(basis.Visible == (vertices.Length > 0), $"Native particle visibility differs: path={row.GetProperty("pathTime")}, velocity={row.GetProperty("velocity")}");
            if (!basis.Visible) continue;
            var lightAxes = StageParticleGeometry.FlatLighting(Vector(row, "velocity"), Vector(row, "right"), Vector(row, "up"),
                align, align ? row.GetProperty("pathTime").GetSingle() : 0, row.GetProperty("rotate").GetBoolean() ? row.GetProperty("angle").GetSingle() : 0);
            for (var i = 0; i < indices.Length; i++)
            {
                var context = $"layout={layout}, age={age}, path={row.GetProperty("pathTime")}, rotate={row.GetProperty("rotate")}, vertex={i}";
                Check(Vector3.Distance(corners[indices[i]], Vector(vertices[i], "position")) < .00001f,
                    $"Native particle vertex differs ({context}): {corners[indices[i]]}; expected {vertices[i].GetProperty("position")}");
                var expectedUv = Values(vertices[i], "uv");
                var tangent = Values(vertices[i], "tangentFrame");
                var axes = new[] { lightAxes.Right, lightAxes.Up, lightAxes.Depth };
                for (var axis = 0; axis < 3; axis++)
                    Check(Vector3.Distance(axes[axis], new(tangent[axis * 4], tangent[axis * 4 + 1], tangent[axis * 4 + 2])) < .00001f,
                        "Native particle lighting basis differs: " + context);
                Check(Math.Abs(size.X - vertices[i].GetProperty("softEdgeWidth").GetSingle()) < .000001f, "Native particle soft-edge width differs");
                Check(uv[indices[i]] == new Vector2(expectedUv[0], expectedUv[1]), "Native particle texture coordinates differ: " + context);
            }
        }
        Console.WriteLine("Native particle vertex, path alignment, rotation table, and texture-frame checks passed.");
    }

    private static float[] Values(JsonElement row, string field) => row.GetProperty(field).EnumerateArray().Select(value => value.GetSingle()).ToArray();
    private static Vector3 Vector(JsonElement row, string field) { var values = Values(row, field); return new(values[0], values[1], values[2]); }
    private static void Check(bool success, string message) { if (!success) throw new Exception(message); }
}
