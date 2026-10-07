using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Gh3Skin;

internal static class Gh3QbText
{
    public static string Encode(Gh3QbValue value, Func<uint, string>? name = null)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) Write(writer, value, name);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public static Gh3QbValue Decode(string text)
    {
        if (text.Length > 4 * 1024 * 1024) throw new InvalidDataException("The typed GH3 value is too large.");
        try
        {
            using var document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 256 });
            var count = 0;
            return Read(document.RootElement, 0, ref count);
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException or OverflowException)
        {
            throw new InvalidDataException("Invalid typed GH3 value.", error);
        }
    }

    public static string Key(uint value) => $"0x{value:x8}";

    public static uint Key(string value) => string.IsNullOrEmpty(value) ? throw new InvalidDataException("Missing GH3 key.") : value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
        ? uint.Parse(value.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture)
        : Gh3Checksum.Value(value);

    private static void Write(Utf8JsonWriter writer, Gh3QbValue value, Func<uint, string>? name)
    {
        writer.WriteStartArray();
        writer.WriteNumberValue(value.Type);
        switch (value.Data)
        {
            case uint integer when value.Type != 1: writer.WriteStringValue(name?.Invoke(integer) ?? Key(integer)); break;
            case uint integer: writer.WriteNumberValue(integer); break;
            case float number: writer.WriteNumberValue(number); break;
            case string text: writer.WriteStringValue(text); break;
            case float[] vector:
                writer.WriteStartArray();
                foreach (var coordinate in vector) writer.WriteNumberValue(coordinate);
                writer.WriteEndArray();
                break;
            case ReadOnlyMemory<byte> script: writer.WriteBase64StringValue(script.Span); break;
            case IReadOnlyList<Gh3QbField> fields:
                writer.WriteStartArray();
                foreach (var field in fields)
                {
                    writer.WriteStartArray();
                    writer.WriteStringValue(name?.Invoke(field.Name) ?? Key(field.Name));
                    Write(writer, field.Value, name);
                    writer.WriteEndArray();
                }
                writer.WriteEndArray();
                break;
            case IReadOnlyList<Gh3QbValue> items:
                writer.WriteStartArray();
                foreach (var item in items) Write(writer, item, name);
                writer.WriteEndArray();
                break;
            default: throw new InvalidDataException("Unsupported typed GH3 value.");
        }
        writer.WriteEndArray();
    }

    private static Gh3QbValue Read(JsonElement node, int depth, ref int count)
    {
        if (depth > 64 || ++count > 100000 || node.GetArrayLength() != 2)
            throw new InvalidDataException("Invalid typed GH3 value size or depth.");
        var type = node[0].GetUInt32();
        var value = node[1];
        object data;
        switch (type)
        {
            case 1: data = value.GetUInt32(); break;
            case 13: case 26: case 28: data = Key(value.GetString()!); break;
            case 2:
                var number = value.GetSingle();
                if (!float.IsFinite(number)) throw new InvalidDataException("Non-finite typed GH3 number.");
                data = number;
                break;
            case 3: case 4: data = value.GetString() ?? throw new InvalidDataException("Missing GH3 string."); break;
            case 5: case 6:
                var vector = value.EnumerateArray().Select(item => item.GetSingle()).ToArray();
                if (vector.Length != (type == 5 ? 2 : 3) || !vector.All(float.IsFinite))
                    throw new InvalidDataException("Invalid typed GH3 vector.");
                data = vector;
                break;
            case 7: data = new ReadOnlyMemory<byte>(value.GetBytesFromBase64()); break;
            case 10:
                var fields = new List<Gh3QbField>();
                foreach (var field in value.EnumerateArray())
                {
                    if (field.GetArrayLength() != 2) throw new InvalidDataException("Invalid typed GH3 field.");
                    fields.Add(new(Key(field[0].GetString()!), Read(field[1], depth + 1, ref count)));
                }
                data = fields.AsReadOnly();
                break;
            case 12:
                var items = new List<Gh3QbValue>();
                foreach (var item in value.EnumerateArray()) items.Add(Read(item, depth + 1, ref count));
                data = items.AsReadOnly();
                break;
            default: throw new InvalidDataException($"Unsupported typed GH3 value type {type}.");
        }
        return new(type, -1, data);
    }
}
