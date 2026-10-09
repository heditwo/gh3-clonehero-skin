using System.Text.Json;

namespace Gh3Skin;

// Spike: minimal glTF 2.0 binary reader without Unity types (one embedded buffer, no sparse accessors).
internal sealed class GlbFile : IDisposable
{
    private readonly JsonDocument _document;
    private readonly byte[] _data;
    private readonly int _binary;
    private readonly int _binaryLength;
    public JsonElement Root => _document.RootElement;

    private GlbFile(JsonDocument document, byte[] data, int binary, int binaryLength)
    {
        _document = document;
        _data = data;
        _binary = binary;
        _binaryLength = binaryLength;
    }

    public static GlbFile Parse(byte[] data)
    {
        if (data.Length < 28 || BitConverter.ToUInt32(data, 0) != 0x46546c67 || BitConverter.ToUInt32(data, 4) != 2 ||
            BitConverter.ToUInt32(data, 8) != data.Length)
            throw new InvalidDataException("Expected a glTF 2.0 GLB");
        JsonDocument? json = null;
        int binary = -1, binaryLength = 0;
        for (var cursor = 12; cursor < data.Length;)
        {
            var length = BitConverter.ToInt32(data, cursor);
            var kind = BitConverter.ToUInt32(data, cursor + 4);
            cursor += 8;
            if (length < 0 || cursor + length > data.Length) throw new InvalidDataException("Invalid GLB chunk");
            if (kind == 0x4e4f534a && json == null) json = JsonDocument.Parse(new ReadOnlyMemory<byte>(data, cursor, length));
            else if (kind == 0x004e4942 && binary < 0) { binary = cursor; binaryLength = length; }
            cursor += length;
        }
        if (json == null) throw new InvalidDataException("GLB has no JSON chunk");
        return new GlbFile(json, data, binary, binaryLength);
    }

    public JsonElement Array(string name, int index) => Root.GetProperty(name)[index];

    private (int Start, int Length) ViewRange(int index)
    {
        var view = Array("bufferViews", index);
        var offset = view.TryGetProperty("byteOffset", out var o) ? o.GetInt32() : 0;
        var length = view.GetProperty("byteLength").GetInt32();
        if (_binary < 0 || offset < 0 || length < 0 || offset + length > _binaryLength) throw new InvalidDataException("Invalid GLB buffer view");
        return (_binary + offset, length);
    }

    public ReadOnlySpan<byte> View(int index)
    {
        var (start, length) = ViewRange(index);
        return new ReadOnlySpan<byte>(_data, start, length);
    }

    private (int Start, int Count, int Components, int Type, int Stride, bool Normalized) Accessor(int index)
    {
        var accessor = Array("accessors", index);
        if (accessor.TryGetProperty("sparse", out _)) throw new InvalidDataException("Sparse accessors are not supported");
        var components = accessor.GetProperty("type").GetString() switch
        {
            "SCALAR" => 1, "VEC2" => 2, "VEC3" => 3, "VEC4" => 4, "MAT4" => 16,
            _ => throw new InvalidDataException("Unsupported accessor type"),
        };
        var type = accessor.GetProperty("componentType").GetInt32();
        var size = type switch { 5120 or 5121 => 1, 5122 or 5123 => 2, 5125 or 5126 => 4, _ => throw new InvalidDataException("Unsupported component type") };
        var count = accessor.GetProperty("count").GetInt32();
        var viewIndex = accessor.GetProperty("bufferView").GetInt32();
        var (start, length) = ViewRange(viewIndex);
        var offset = accessor.TryGetProperty("byteOffset", out var o) ? o.GetInt32() : 0;
        var stride = Array("bufferViews", viewIndex).TryGetProperty("byteStride", out var s) ? s.GetInt32() : components * size;
        if (count < 1 || stride < components * size || offset < 0 || offset + (long)(count - 1) * stride + components * size > length)
            throw new InvalidDataException("Invalid accessor range");
        var normalized = accessor.TryGetProperty("normalized", out var n) && n.GetBoolean();
        return (start + offset, count, components, type, stride, normalized);
    }

    public float[] Floats(int index, int components)
    {
        var (start, count, actual, type, stride, normalized) = Accessor(index);
        if (actual != components) throw new InvalidDataException("Unexpected accessor width");
        var data = new ReadOnlySpan<byte>(_data, start, _data.Length - start);
        var result = new float[count * components];
        if (type == 5126 && stride == components * 4)
        {
            System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(data[..(count * stride)]).CopyTo(result);
        }
        else
        {
            for (var i = 0; i < count; i++)
            for (var c = 0; c < components; c++)
            {
                var at = data[(i * stride)..];
                result[i * components + c] = type switch
                {
                    5126 => BitConverter.ToSingle(at[(c * 4)..]),
                    5121 => normalized ? at[c] / 255f : at[c],
                    5123 => normalized ? BitConverter.ToUInt16(at[(c * 2)..]) / 65535f : BitConverter.ToUInt16(at[(c * 2)..]),
                    5120 => normalized ? Math.Max(-1, (sbyte)at[c] / 127f) : (sbyte)at[c],
                    5122 => normalized ? Math.Max(-1, BitConverter.ToInt16(at[(c * 2)..]) / 32767f) : BitConverter.ToInt16(at[(c * 2)..]),
                    _ => throw new InvalidDataException("Unsupported float accessor"),
                };
            }
        }
        foreach (var value in result) if (!float.IsFinite(value)) throw new InvalidDataException("Non-finite accessor value");
        return result;
    }

    public int[] Integers(int index, int components)
    {
        var (start, count, actual, type, stride, _) = Accessor(index);
        if (actual != components) throw new InvalidDataException("Unexpected accessor width");
        var data = new ReadOnlySpan<byte>(_data, start, _data.Length - start);
        var result = new int[count * components];
        for (var i = 0; i < count; i++)
        for (var c = 0; c < components; c++)
        {
            var at = data[(i * stride)..];
            result[i * components + c] = type switch
            {
                5121 => at[c],
                5123 => BitConverter.ToUInt16(at[(c * 2)..]),
                5125 => checked((int)BitConverter.ToUInt32(at[(c * 4)..])),
                _ => throw new InvalidDataException("Unsupported integer accessor"),
            };
        }
        return result;
    }

    public byte[] Image(int image)
    {
        var row = Array("images", image);
        if (!row.TryGetProperty("bufferView", out var view)) throw new InvalidDataException("Images must be embedded");
        return View(view.GetInt32()).ToArray();
    }

    public void Dispose() => _document.Dispose();
}
