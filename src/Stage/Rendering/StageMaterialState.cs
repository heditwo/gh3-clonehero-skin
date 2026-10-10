using System.Numerics;

namespace Gh3Skin;

internal readonly record struct StageMaterialTarget(uint Object, uint Material, uint Parameter);

internal sealed class StageMaterialState
{
    private readonly StageScriptBank _bank;
    private readonly Gh3ScriptRuntime _runtime;
    private readonly Func<StageMaterialTarget, Vector4?>? _original;
    private readonly Action<string> _gap;
    private readonly Dictionary<StageMaterialTarget, Vector4> _properties = new();
    public IReadOnlyDictionary<StageMaterialTarget, Vector4> Properties => _properties;
    public static uint ColorParameter => Gh3Checksum.Value("m_psPass0MaterialColor");
    public static uint FpsParameter => Gh3Checksum.Value("m_FPS");

    public StageMaterialState(StageScriptBank bank, Gh3ScriptRuntime runtime, Action<string> gap, Func<StageMaterialTarget, Vector4?>? original = null)
    {
        _bank = bank; _runtime = runtime; _gap = gap; _original = original;
    }

    public Gh3ScriptCallResult? Execute(uint command, Gh3QbValue parameters)
    {
        if (command != Gh3Checksum.Value("UpdateMaterialProperty")) return null;
        var material = parameters.Field("material")?.Integer ?? throw new InvalidDataException("Material identity is missing");
        var parameter = parameters.Field("parameter")?.Integer ?? throw new InvalidDataException("Material parameter is missing");
        var source = parameters.Field("value") ?? throw new InvalidDataException("Material values are missing");
        Vector4 value;
        if (source.Type == Gh3QbType.Array)
        {
            var values = source.Items.Select(Gh3ScriptRuntime.Numeric).ToArray();
            if (values.Length is not (1 or 3 or 4) || values.Any(item => !double.IsFinite(item))) throw new InvalidDataException("Invalid material color array");
            const float scale = 1f / 255;
            value = new((float)values[0] * scale, (float)values[values.Length == 1 ? 0 : 1] * scale,
                (float)values[values.Length == 1 ? 0 : 2] * scale, values.Length == 4 ? (float)values[3] * scale : 1);
        }
        else if (source.Type == Gh3QbType.Vector && source.Data is float[] { Length: 3 } vector && vector.All(float.IsFinite)) value = new(vector[0], vector[1], vector[2], 0);
        else if (source.Type is Gh3QbType.Integer or Gh3QbType.Float && double.IsFinite(Gh3ScriptRuntime.Numeric(source))) value = new((float)Gh3ScriptRuntime.Numeric(source), 0, 0, 0);
        else throw new InvalidDataException("Invalid material value");
        var targets = parameters.Field("object") is { } obj ? new[] { obj.Integer } :
            parameters.Field("prefix") is { } prefix ? _bank.PrefixValues.GetValueOrDefault(prefix.Integer, Array.Empty<uint>()) : throw new InvalidDataException("Material target is missing");
        var seconds = parameters.Field("time") is { Type: 1 or 2 } duration ? Math.Truncate(Gh3ScriptRuntime.Numeric(duration)) : 0;
        if (!double.IsFinite(seconds) || Math.Abs(seconds) > 86400) throw new InvalidDataException("Invalid material hold time");
        var first = new StageMaterialTarget(targets.FirstOrDefault(), material, parameter);
        var previous = _properties.TryGetValue(first, out var current) ? current : _original?.Invoke(first);
        foreach (var target in targets)
        {
            var key = new StageMaterialTarget(target, material, parameter);
            _properties[key] = value;
        }
        if (seconds > 0 && targets.Length > 0)
            _runtime.Schedule(_runtime.Time + seconds, () =>
            {
                foreach (var target in targets)
                {
                    var key = new StageMaterialTarget(target, material, parameter);
                    if (previous is { } restore) _properties[key] = restore;
                    else _properties.Remove(key);
                }
            });
        if (parameter != ColorParameter && parameter != FpsParameter) _gap("Material parameter rendering is incomplete: " + Gh3QbText.Key(parameter));
        return new(true);
    }
}
