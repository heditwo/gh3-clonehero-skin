using System.Buffers.Binary;
using System.Text.Json.Nodes;

namespace Gh3Skin.Packages;

// One glTF primitive in Unity runtime coordinates (pipeline/stagepack/gltf.py GlbReader.primitive).
internal sealed class PrimitiveData
{
    public int Count;
    public double[] Position = Array.Empty<double>(), Normal = Array.Empty<double>();
    public double[] Uv = Array.Empty<double>(), Lightmap = Array.Empty<double>(), Layer = Array.Empty<double>(), Mask = Array.Empty<double>();
    public byte[] Color = Array.Empty<byte>();
    public double[] Gh3Uv = Array.Empty<double>(), Gh3Binormal = Array.Empty<double>(), Gh3Position1 = Array.Empty<double>(), Gh3Binormal1 = Array.Empty<double>();
    public int[] Faces = Array.Empty<int>();
}

internal sealed record MeshPart(string Material, int Count, byte[] Vertices, int[] Faces);

internal static class StageGeometry
{
    public const int StaticStride = 144;
    public const int SkinnedStride = 128;
    private static readonly string[] Gh3Streams = { "_GH3_UV0", "_GH3_UV1", "_GH3_UV2", "_GH3_UV3", "_GH3_UV4", "_GH3_UV5", "_GH3_BINORMAL" };

    private static void Apply(double[] rows, int count, double[] m, double[]? translation, double[] output)
    {
        for (var i = 0; i < count; i++)
        {
            double x = rows[i * 3], y = rows[i * 3 + 1], z = rows[i * 3 + 2];
            for (var r = 0; r < 3; r++)
                output[i * 3 + r] = x * m[r * 3] + y * m[r * 3 + 1] + z * m[r * 3 + 2] + (translation?[r] ?? 0);
        }
    }

    public static PrimitiveData Primitive(GlbDocument glb, JsonObject primitive, Matrix4d world)
    {
        if (GlbDocument.Int(primitive, "mode", 4) != 4) throw new PackageException("triangulate stage meshes before export");
        if (primitive["targets"] is JsonArray { Count: > 0 }) throw new PackageException("bake morph targets before stage import");
        var attributes = primitive["attributes"] as JsonObject ?? throw new PackageException("primitive has no attributes");
        int? Attribute(string name) => attributes[name] is JsonValue value ? value.GetValue<int>() : null;
        if (Attribute("NORMAL") == null) throw new PackageException("export stage vertex normals");
        var positions = glb.Accessor(Attribute("POSITION") ?? throw new PackageException("primitive has no POSITION"));
        var normals = glb.Accessor(Attribute("NORMAL")!.Value);
        var count = positions.Count;
        if (positions.Columns != 3 || normals.Columns != 3 || normals.Count != count) throw new PackageException("invalid glTF vertex attributes");
        var transform = world * Matrix4d.Reflection;
        var linear = transform.Upper3();
        var translation = transform.Translation;
        var inverse = transform.Inverse3();
        var data = new PrimitiveData { Count = count, Position = new double[count * 3], Normal = new double[count * 3] };
        Apply(positions.Values, count, linear, translation, data.Position);
        // normals @ inv(T): row vector times the inverse, i.e. the transposed inverse applied to the normal.
        var inverseTransposed = new double[9];
        for (var r = 0; r < 3; r++) for (var c = 0; c < 3; c++) inverseTransposed[r * 3 + c] = inverse[c * 3 + r];
        Apply(normals.Values, count, inverseTransposed, null, data.Normal);
        for (var i = 0; i < count; i++)
        {
            var length = Math.Sqrt(data.Normal[i * 3] * data.Normal[i * 3] + data.Normal[i * 3 + 1] * data.Normal[i * 3 + 1] + data.Normal[i * 3 + 2] * data.Normal[i * 3 + 2]);
            length = Math.Max(length, 1e-10);
            for (var c = 0; c < 3; c++) data.Normal[i * 3 + c] /= length;
        }
        // numpy keeps float32 accessors in float32, so `1 - v` and the colour scale round in single precision there.
        (double[] Values, bool Single) Channel(int index)
        {
            if (Attribute($"TEXCOORD_{index}") is not { } accessor) return (new double[count * 2], false);
            var values = glb.Accessor(accessor);
            if (values.Columns != 2 || values.Count != count) throw new PackageException("invalid glTF UV count");
            return (values.Values, !glb.IsIntegerAccessor(accessor));
        }
        var uv = Channel(0);
        var lightmap = Channel(1);
        var layer = Channel(2);
        var mask = Channel(3);
        var color = new double[count * 4];
        var singleColor = false;
        if (Attribute("COLOR_0") is { } colorAccessor)
        {
            var values = glb.Accessor(colorAccessor);
            if (values.Count != count || values.Columns is not (3 or 4)) throw new PackageException("invalid glTF color count");
            singleColor = !glb.IsIntegerAccessor(colorAccessor) && values.Columns == 4;
            for (var i = 0; i < count; i++)
                for (var c = 0; c < 4; c++) color[i * 4 + c] = c < values.Columns ? values[i, c] : 1;
        }
        else Array.Fill(color, 1.0);
        int[] indices;
        if (primitive["indices"] is JsonValue indexAccessor)
        {
            var index = indexAccessor.GetValue<int>();
            if (!glb.IsIntegerAccessor(index)) throw new PackageException("invalid glTF triangles");
            indices = glb.Accessor(index, normalize: false).Values.Select(v => (int)v).ToArray();
        }
        else indices = Enumerable.Range(0, count).ToArray();
        if (indices.Length % 3 != 0 || indices.Any(i => i < 0 || i >= count)) throw new PackageException("invalid glTF triangles");
        if (transform.Determinant3() < 0)
            for (var i = 0; i < indices.Length; i += 3) (indices[i + 1], indices[i + 2]) = (indices[i + 2], indices[i + 1]);
        data.Faces = indices;
        var materials = glb.List("materials");
        var material = primitive["material"] is JsonValue m ? materials[m.GetValue<int>()] as JsonObject : null;
        // A GH3 template applies its own colour constant; its vertex colours stay as GH3 stores them.
        if (material?["extras"]?["gh3Shader"] == null)
        {
            singleColor = false;
            if (material?["pbrMetallicRoughness"]?["baseColorFactor"] is JsonArray factor)
                for (var i = 0; i < count; i++)
                    for (var c = 0; c < 4; c++) color[i * 4 + c] *= PackageJson.Number(factor[c], "baseColorFactor");
        }
        data.Gh3Uv = new double[count * 12];
        data.Gh3Binormal = new double[count * 3];
        data.Gh3Binormal1 = new double[count * 3];
        data.Gh3Position1 = new double[count * 3];
        if (Gh3Streams.All(name => Attribute(name) != null))
        {
            for (var set = 0; set < 6; set++)
            {
                var channel = glb.Accessor(Attribute($"_GH3_UV{set}")!.Value);
                if (channel.Count != count || channel.Columns != 2) throw new PackageException("invalid GH3 UV stream");
                for (var i = 0; i < count; i++)
                {
                    data.Gh3Uv[i * 12 + set * 2] = channel[i, 0];
                    data.Gh3Uv[i * 12 + set * 2 + 1] = channel[i, 1];
                }
            }
            var binormals = glb.Accessor(Attribute("_GH3_BINORMAL")!.Value);
            var hasPivots = Attribute("_GH3_POSITION1") != null;
            var binormals1 = Attribute("_GH3_BINORMAL1") is { } b1 ? glb.Accessor(b1).Values : new double[count * 3];
            if (binormals.Count != count || binormals.Columns != 3 || binormals1.Length != count * 3) throw new PackageException("invalid GH3 vertex stream");
            Apply(binormals.Values, count, linear, null, data.Gh3Binormal);
            Apply(binormals1, count, linear, null, data.Gh3Binormal1);
            if (hasPivots)
            {
                var pivots = glb.Accessor(Attribute("_GH3_POSITION1")!.Value);
                if (pivots.Count != count || pivots.Columns != 3) throw new PackageException("invalid GH3 vertex stream");
                Apply(pivots.Values, count, linear, translation, data.Gh3Position1);
            }
            else Array.Copy(data.Position, data.Gh3Position1, data.Position.Length);
        }
        data.Uv = Flip(uv);
        data.Lightmap = Flip(lightmap);
        data.Layer = Flip(layer);
        data.Mask = Flip(mask);
        data.Color = new byte[count * 4];
        for (var i = 0; i < color.Length; i++)
            data.Color[i] = singleColor ? (byte)MathF.Round(Math.Clamp((float)color[i], 0f, 1f) * 255f) : (byte)Math.Round(Math.Clamp(color[i], 0, 1) * 255);
        return data;
    }

    private static double[] Flip((double[] Values, bool Single) uv)
    {
        var result = (double[])uv.Values.Clone();
        for (var i = 1; i < result.Length; i += 2) result[i] = uv.Single ? 1f - (float)uv.Values[i] : 1 - uv.Values[i];
        return result;
    }

    private static void Floats(Span<byte> target, double[] source, int start, int count)
    {
        for (var i = 0; i < count; i++) BinaryPrimitives.WriteSingleLittleEndian(target[(i * 4)..], (float)source[start + i]);
    }

    public static byte[] PackStatic(PrimitiveData data)
    {
        var packed = new byte[data.Count * StaticStride];
        for (var v = 0; v < data.Count; v++)
        {
            var row = packed.AsSpan(v * StaticStride, StaticStride);
            Floats(row, data.Position, v * 3, 3);
            Floats(row[12..], data.Normal, v * 3, 3);
            Floats(row[24..], data.Uv, v * 2, 2);
            data.Color.AsSpan(v * 4, 4).CopyTo(row[32..]);
            Floats(row[36..], data.Lightmap, v * 2, 2);
            Floats(row[44..], data.Layer, v * 2, 2);
            Floats(row[52..], data.Mask, v * 2, 2);
            Floats(row[60..], data.Gh3Uv, v * 12, 12);
            Floats(row[108..], data.Gh3Binormal, v * 3, 3);
            Floats(row[120..], data.Gh3Position1, v * 3, 3);
            Floats(row[132..], data.Gh3Binormal1, v * 3, 3);
        }
        return packed;
    }

    // GH3BND02 vertex: 56-byte skinned vertex, then GH3 UV sets, binormal 0, tangent 0.
    public static byte[] PackSkinned(PrimitiveData data, int[] joints, double[] weights, double[]? tangents)
    {
        var packed = new byte[data.Count * SkinnedStride];
        for (var v = 0; v < data.Count; v++)
        {
            var row = packed.AsSpan(v * SkinnedStride, SkinnedStride);
            Floats(row, data.Position, v * 3, 3);
            Floats(row[12..], data.Normal, v * 3, 3);
            Floats(row[24..], data.Uv, v * 2, 2);
            data.Color.AsSpan(v * 4, 4).CopyTo(row[32..]);
            for (var i = 0; i < 4; i++) row[36 + i] = (byte)joints[v * 4 + i];
            Floats(row[40..], weights, v * 4, 4);
            Floats(row[56..], data.Gh3Uv, v * 12, 12);
            Floats(row[104..], data.Gh3Binormal, v * 3, 3);
            if (tangents != null) Floats(row[116..], tangents, v * 3, 3);
        }
        return packed;
    }

    // pipeline/stagepack/pack.py split_mesh: at most 20,000 faces for each part, with the used vertices in index order.
    public static IEnumerable<MeshPart> Split(string material, byte[] packed, int stride, int[] faces)
    {
        for (var start = 0; start < faces.Length; start += 60000)
        {
            var length = Math.Min(60000, faces.Length - start);
            var used = new SortedSet<int>();
            for (var i = 0; i < length; i++) used.Add(faces[start + i]);
            var order = used.ToArray();
            var remap = new Dictionary<int, int>(order.Length);
            for (var i = 0; i < order.Length; i++) remap[order[i]] = i;
            var vertices = new byte[order.Length * stride];
            for (var i = 0; i < order.Length; i++) Buffer.BlockCopy(packed, order[i] * stride, vertices, i * stride, stride);
            var section = new int[length];
            for (var i = 0; i < length; i++) section[i] = remap[faces[start + i]];
            yield return new MeshPart(material, order.Length, vertices, section);
        }
    }

    // The source vertex indices of each part of Split, in the order of the part vertices.
    public static IEnumerable<int[]> SplitOrders(int[] faces)
    {
        for (var start = 0; start < faces.Length; start += 60000)
        {
            var used = new SortedSet<int>();
            for (var i = 0; i < Math.Min(60000, faces.Length - start); i++) used.Add(faces[start + i]);
            yield return used.ToArray();
        }
    }

    public static void WriteMeshes(string path, IReadOnlyList<MeshPart> meshes, bool skinned)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = new BufferedStream(File.Create(path), 1 << 20);
        using var writer = new BinaryWriter(stream);
        writer.Write(System.Text.Encoding.ASCII.GetBytes(skinned ? "GH3BND02" : "GH3STG04"));
        writer.Write(meshes.Count);
        foreach (var mesh in meshes)
        {
            writer.Write(Convert.ToUInt32(mesh.Material, 16));
            writer.Write(mesh.Count);
            writer.Write(mesh.Faces.Length);
            writer.Write(mesh.Vertices);
            foreach (var index in mesh.Faces) writer.Write(index);
        }
    }
}
