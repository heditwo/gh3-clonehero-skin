using System.Buffers.Binary;
using System.Text.Json.Nodes;

namespace Gh3Skin.Packages;

internal readonly record struct GlbAccessor(double[] Values, int Count, int Columns)
{
    public double this[int row, int column] => Values[row * Columns + column];
}

// glTF 2.0 binary reader of the importer: one embedded buffer, no sparse accessors (PACKAGE-FORMAT.md section 6.1).
// Values are doubles, as in the Python compiler (pipeline/stagepack/gltf.py GlbReader), so the cache has the same numbers.
internal sealed class GlbDocument
{
    private readonly byte[] _data;
    private readonly int _binary;
    private readonly int _binaryLength;
    public JsonObject Json { get; }
    // PACKAGE-FORMAT.md section 6.2: images with the media type image/vnd-gh3.tex hold GH3TEX02 textures.
    public const string TextureExtension = "GH3_texture_bc";

    public GlbDocument(byte[] data)
    {
        if (data.Length < 28 || BinaryPrimitives.ReadUInt32LittleEndian(data) != 0x46546c67 || BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4)) != 2 ||
            BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(8)) != data.Length)
            throw new PackageException("expected a glTF 2.0 GLB");
        JsonObject? json = null;
        _binary = -1;
        for (var cursor = 12; cursor < data.Length;)
        {
            if (cursor + 8 > data.Length) throw new PackageException("invalid GLB chunks");
            var length = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(cursor));
            var kind = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(cursor + 4));
            cursor += 8;
            if (length < 0 || cursor + (long)length > data.Length) throw new PackageException("invalid GLB chunks");
            if (kind == 0x4e4f534a)
            {
                if (json != null) throw new PackageException("invalid GLB chunks");
                json = JsonNode.Parse(data.AsSpan(cursor, length)) as JsonObject;
            }
            else if (kind == 0x004e4942)
            {
                if (_binary >= 0) throw new PackageException("invalid GLB chunks");
                _binary = cursor;
                _binaryLength = length;
            }
            cursor += length;
        }
        Json = json ?? throw new PackageException("GLB has no JSON chunk");
        _data = data;
        if (Json["extensionsRequired"] is JsonArray required && required.Any(e => e?.GetValue<string>() != TextureExtension))
            throw new PackageException("GLB requires glTF extensions");
        var buffers = List("buffers");
        if (buffers.Count != 1 || buffers[0]!["uri"] != null || buffers[0]!["byteLength"]!.GetValue<long>() > _binaryLength)
            throw new PackageException("GLB must contain one embedded buffer");
    }

    public JsonArray List(string name) => Json[name] as JsonArray ?? new JsonArray();
    public JsonObject Item(string name, int index) => List(name)[index] as JsonObject ?? throw new PackageException($"invalid glTF {name} {index}");

    public ReadOnlySpan<byte> View(int index)
    {
        var view = Item("bufferViews", index);
        var start = Int(view, "byteOffset", 0);
        var length = Int(view, "byteLength", -1);
        if (Int(view, "buffer", 0) != 0 || start < 0 || length < 0 || (long)start + length > _binaryLength) throw new PackageException("invalid glTF buffer view");
        return _data.AsSpan(_binary + start, length);
    }

    public static int Int(JsonObject node, string name, int fallback) => node[name] is JsonValue value ? value.GetValue<int>() : fallback;

    public GlbAccessor Accessor(int index, bool normalize = true)
    {
        var accessor = Item("accessors", index);
        if (accessor["sparse"] != null) throw new PackageException("sparse glTF accessors are not permitted");
        var type = accessor["componentType"]!.GetValue<int>();
        var size = type switch { 5120 or 5121 => 1, 5122 or 5123 => 2, 5125 or 5126 => 4, _ => throw new PackageException("unsupported accessor component type") };
        var columns = accessor["type"]!.GetValue<string>() switch
        {
            "SCALAR" => 1, "VEC2" => 2, "VEC3" => 3, "VEC4" => 4, "MAT4" => 16, _ => throw new PackageException("unsupported accessor type"),
        };
        if (accessor["bufferView"] == null)
        {
            // glTF: an accessor without a buffer view holds zeros (Blender writes these for unchanged morph targets).
            var zeros = accessor["count"]!.GetValue<int>();
            if (zeros is < 1 or > 10000000) throw new PackageException("invalid glTF accessor range");
            return new GlbAccessor(new double[zeros * columns], zeros, columns);
        }
        var viewIndex = accessor["bufferView"]!.GetValue<int>();
        var view = Item("bufferViews", viewIndex);
        var data = View(viewIndex);
        var offset = Int(accessor, "byteOffset", 0);
        var stride = Int(view, "byteStride", columns * size);
        var count = accessor["count"]!.GetValue<int>();
        if (count is < 1 or > 10000000 || stride < columns * size || offset < 0 || offset + (long)(count - 1) * stride + columns * size > data.Length)
            throw new PackageException("invalid glTF accessor range");
        var normalized = normalize && accessor["normalized"] is JsonValue flag && flag.GetValue<bool>() && type != 5126;
        var values = new double[count * columns];
        for (var row = 0; row < count; row++)
        {
            var at = data[(offset + row * stride)..];
            for (var c = 0; c < columns; c++)
            {
                double value = type switch
                {
                    5126 => BinaryPrimitives.ReadSingleLittleEndian(at[(c * 4)..]),
                    5121 => at[c],
                    5120 => (sbyte)at[c],
                    5123 => BinaryPrimitives.ReadUInt16LittleEndian(at[(c * 2)..]),
                    5122 => BinaryPrimitives.ReadInt16LittleEndian(at[(c * 2)..]),
                    _ => BinaryPrimitives.ReadUInt32LittleEndian(at[(c * 4)..]),
                };
                if (normalized)
                    value = Math.Max(-1, value / type switch { 5121 => 255.0, 5120 => 127.0, 5123 => 65535.0, 5122 => 32767.0, _ => 4294967295.0 });
                if (!double.IsFinite(value)) throw new PackageException("non-finite glTF accessor value");
                values[row * columns + c] = value;
            }
        }
        return new GlbAccessor(values, count, columns);
    }

    public bool IsIntegerAccessor(int index) => Item("accessors", index)["componentType"]!.GetValue<int>() != 5126;

    // Index of the first texture whose image has this name (texture references '#<name>', PACKAGE-FORMAT.md section 6.6).
    public int? NamedTexture(string name)
    {
        var images = List("images");
        var textures = List("textures");
        for (var i = 0; i < textures.Count; i++)
        {
            var source = textures[i] is JsonObject texture ? Int(texture, "source", -1) : -1;
            if (source >= 0 && source < images.Count && images[source]?["name"]?.GetValue<string>() == name) return i;
        }
        return null;
    }

    public int? NamedImage(string name)
    {
        var images = List("images");
        for (var i = 0; i < images.Count; i++)
            if (images[i] is JsonObject image && image["name"] is JsonValue value && value.TryGetValue<string>(out var text) && text == name) return i;
        return null;
    }

    public byte[] TextureImage(int texture)
    {
        var source = Int(Item("textures", texture), "source", -1);
        var image = Item("images", source);
        if (image["bufferView"] is not JsonValue view) throw new PackageException("textures must be embedded in the GLB");
        return View(view.GetValue<int>()).ToArray();
    }

    public byte[] Image(int image) =>
        Item("images", image)["bufferView"] is JsonValue view ? View(view.GetValue<int>()).ToArray() : throw new PackageException("images must be embedded in the GLB");

    // Scene nodes in depth-first order with world matrices in Unity runtime coordinates (REFLECTION @ world @ REFLECTION).
    public IEnumerable<(int Index, JsonObject Node, Matrix4d World)> Nodes()
    {
        var visited = new HashSet<int>();
        var result = new List<(int, JsonObject, Matrix4d)>();
        void Visit(int index, Matrix4d parent, int depth)
        {
            if (!visited.Add(index) || depth > 128) throw new PackageException("invalid glTF scene hierarchy");
            var node = Item("nodes", index);
            var world = parent * Matrix4d.FromNode(node);
            result.Add((index, node, Matrix4d.Reflect(world)));
            if (node["children"] is JsonArray children)
                foreach (var child in children) Visit(child!.GetValue<int>(), world, depth + 1);
        }
        var scene = Item("scenes", Json["scene"] is JsonValue s ? s.GetValue<int>() : 0);
        foreach (var root in scene["nodes"] as JsonArray ?? new JsonArray()) Visit(root!.GetValue<int>(), Matrix4d.Identity, 0);
        return result;
    }

    public Dictionary<int, int> Parents()
    {
        var parents = new Dictionary<int, int>();
        var nodes = List("nodes");
        for (var i = 0; i < nodes.Count; i++)
            if (nodes[i]?["children"] is JsonArray children)
                foreach (var child in children) parents[child!.GetValue<int>()] = i;
        return parents;
    }
}
