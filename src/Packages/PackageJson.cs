using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Gh3Skin.Packages;

internal static class PackageJson
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    // One options object for the runtime JSON files: System.Text.Json builds its type metadata once for each options
    // object, so a new object for each file made every read pay that cost again.
    public static readonly JsonSerializerOptions CaseInsensitive = new() { PropertyNameCaseInsensitive = true };

    public static JsonNode Parse(byte[] data) => JsonNode.Parse(data) ?? throw new PackageException("empty JSON file");
    public static JsonObject Object(byte[] data) => Parse(data) as JsonObject ?? throw new PackageException("expected a JSON object");

    public static JsonNode? Clone(JsonNode? node) => node == null ? null : JsonNode.Parse(node.ToJsonString());

    public static void Write(string path, JsonNode node)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, node.ToJsonString(Indented) + "\n");
    }

    public static bool IsNumber(JsonNode? node) => node is JsonValue value && (value.TryGetValue<JsonElement>(out var element) ?
        element.ValueKind == JsonValueKind.Number : value.TryGetValue<double>(out _) || value.TryGetValue<int>(out _) || value.TryGetValue<long>(out _) || value.TryGetValue<float>(out _));

    public static double Number(JsonNode? node, string what)
    {
        if (node is JsonValue value)
        {
            if (value.TryGetValue<JsonElement>(out var element) && element.ValueKind == JsonValueKind.Number) return element.GetDouble();
            if (value.TryGetValue<double>(out var d)) return d;
            if (value.TryGetValue<float>(out var f)) return f;
            if (value.TryGetValue<int>(out var i)) return i;
            if (value.TryGetValue<long>(out var l)) return l;
        }
        throw new PackageException($"{what} must be a number");
    }

    public static double Number(JsonNode? node, double minimum, double maximum, string what)
    {
        var value = Number(node, what);
        if (!double.IsFinite(value) || value < minimum || value > maximum) throw new PackageException($"{what} must be a number from {minimum} to {maximum}");
        return value;
    }

    // An integer in the JSON text (Python's `type(v) == int`), not 1.0.
    public static bool IsInteger(JsonNode? node)
    {
        if (node is not JsonValue value) return false;
        if (value.TryGetValue<JsonElement>(out var element))
            return element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out _) && !element.GetRawText().Contains('.') && !element.GetRawText().Contains('e') && !element.GetRawText().Contains('E');
        return value.TryGetValue<int>(out _) || value.TryGetValue<long>(out _);
    }

    public static long Integer(JsonNode? node, long minimum, long maximum, string what)
    {
        if (!IsInteger(node)) throw new PackageException($"{what} must be an integer");
        var json = (JsonValue)node!;
        var value = json.TryGetValue<JsonElement>(out var element) ? element.GetInt64() : json.TryGetValue<int>(out var small) ? small : json.GetValue<long>();
        if (value < minimum || value > maximum) throw new PackageException($"{what} must be an integer from {minimum} to {maximum}");
        return value;
    }

    public static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    public static bool? Bool(JsonNode? node) => node is JsonValue value && value.TryGetValue<bool>(out var flag) ? flag : null;

    public static double[] Vector(JsonNode? node, int size, string what)
    {
        if (node is not JsonArray array || array.Count != size) throw new PackageException($"{what} must be a finite {size}-element vector");
        var result = new double[size];
        for (var i = 0; i < size; i++)
        {
            result[i] = Number(array[i], what);
            if (!double.IsFinite(result[i])) throw new PackageException($"{what} must be a finite {size}-element vector");
        }
        return result;
    }

    public static double[] Rotation(JsonNode? node, string what)
    {
        var q = Vector(node, 4, what);
        if (Math.Abs(q.Sum(v => v * v) - 1) > 0.001) throw new PackageException($"{what} must have unit length");
        return q;
    }

    public static void Fields(JsonNode? node, string what, string[] allowed, params string[] required)
    {
        if (node is not JsonObject value) throw new PackageException($"{what} must be an object");
        foreach (var (name, _) in value)
            if (!allowed.Contains(name)) throw new PackageException($"{what} has the unknown field '{name}'");
        foreach (var name in required)
            if (!value.ContainsKey(name)) throw new PackageException($"{what} needs the field '{name}'");
    }

    public static JsonArray Array(IEnumerable<double> values) => new(values.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray());

    // Python repr() of a float, for checksums of text that the Python compiler builds with str(list).
    public static string PythonRepr(double value)
    {
        if (double.IsNaN(value)) return "nan";
        if (double.IsInfinity(value)) return value > 0 ? "inf" : "-inf";
        if (value == 0) return 1 / value < 0 ? "-0.0" : "0.0";
        var text = value.ToString("R", CultureInfo.InvariantCulture);
        var exponent = (int)Math.Floor(Math.Log10(Math.Abs(value)));
        if (exponent is >= -4 and < 16 && text.Contains('E'))
            text = decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d.ToString(CultureInfo.InvariantCulture) : text;
        if (text.Contains('E'))
        {
            var parts = text.Split('E');
            var e = int.Parse(parts[1], CultureInfo.InvariantCulture);
            return parts[0] + "e" + (e < 0 ? "-" : "+") + Math.Abs(e).ToString("00", CultureInfo.InvariantCulture);
        }
        return text.Contains('.') ? text : text + ".0";
    }

    public static string PythonList(IEnumerable<double> values) => "[" + string.Join(", ", values.Select(PythonRepr)) + "]";
}
