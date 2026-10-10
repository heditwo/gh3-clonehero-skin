using System.Numerics;

namespace Gh3Skin;

internal sealed record StageOutlineSettings(float Min, float Max, float Rate, float ZScale, float FillDistance, float BlendMode);
internal readonly record struct StageOutlineObject(bool Enabled, int Color);
internal readonly record struct StageScreenTarget(uint Viewport, uint Name);
internal sealed record StageScreenEffect(double Start, double Duration, Gh3QbValue From, Gh3QbValue To)
{
    public Gh3QbValue Sample(double time)
    {
        var amount = Duration <= 0 ? 1 : Math.Clamp((time - Start) / Duration, 0, 1);
        Gh3QbValue Blend(Gh3QbValue previous, Gh3QbValue next, bool hue = false)
        {
            if (previous.Type == Gh3QbType.Array && next.Type == Gh3QbType.Array && previous.Items.Count == next.Items.Count)
                return new(Gh3QbType.Array, -1, previous.Items.Zip(next.Items, (a, b) => Blend(a, b)).ToArray());
            if (previous.Type is not (Gh3QbType.Integer or Gh3QbType.Float) || next.Type is not (Gh3QbType.Integer or Gh3QbType.Float)) return next;
            var from = Gh3ScriptRuntime.Numeric(previous);
            var to = Gh3ScriptRuntime.Numeric(next);
            if (hue) { if (from - to > .5) from--; else if (from - to < -.5) from++; }
            var value = from + (to - from) * amount;
            if (hue) { if (value < 0) value++; else if (value > 1) value--; }
            return Gh3ScriptRuntime.Number(value);
        }
        return Gh3ScriptRuntime.Structure(To.Fields.Select(field => field.Name != Gh3Checksum.Value("ON") && From.Field(field.Name) is { } previous ?
            new Gh3QbField(field.Name, Blend(previous, field.Value, field.Name == Gh3Checksum.Value("hue"))) : field));
    }
}

internal sealed class StageRenderControlState
{
    private readonly StageLighting? _lighting;
    private readonly Func<uint, bool> _exists;
    private readonly Action<string> _gap;
    private readonly Dictionary<int, Vector4> _outlineColors = new();
    private readonly Dictionary<uint, StageOutlineObject> _outlines = new();
    private readonly Dictionary<StageScreenTarget, StageScreenEffect> _screenEffects = new();
    public StageOutlineSettings Outline { get; private set; } = new(0, 0, 0, 0, 40, 0);
    public IReadOnlyDictionary<int, Vector4> OutlineColors => _outlineColors;
    public IReadOnlyDictionary<uint, StageOutlineObject> Outlines => _outlines;
    public IReadOnlyDictionary<StageScreenTarget, StageScreenEffect> ScreenEffects => _screenEffects;
    public Vector3? ShadowDirection { get; private set; }

    public StageRenderControlState(StageLighting? lighting, Func<uint, bool> exists, Action<string> gap)
    {
        _lighting = lighting; _exists = exists; _gap = gap;
    }

    public void RemoveObject(uint id) => _outlines.Remove(id);

    public Gh3ScriptCallResult? Execute(Gh3ScriptContext context, uint command, Gh3QbValue parameters, double time)
    {
        bool Is(string name) => command == Gh3Checksum.Value(name);
        float Number(string name, float fallback) => parameters.Field(name) is { Type: 1 or 2 } value && double.IsFinite(Gh3ScriptRuntime.Numeric(value)) ?
            (float)Gh3ScriptRuntime.Numeric(value) : fallback;
        if (Is("SetShadowDirFromLight"))
        {
            var name = parameters.Field("Name")?.Integer ?? context.Object;
            var light = _lighting?.Bank.Lights.FirstOrDefault(light => light.Object.Length > 0 && Gh3QbText.Key(light.Object) == name);
            if (light == null || !_exists(name)) return new(false);
            var direction = light.Forward;
            if (parameters.Field("pos") is { Type: 6, Data: float[] { Length: 3 } position })
            {
                direction = new Vector3(position[0], position[1], -position[2]) - light.Location;
                if (direction.LengthSquared() > .000001f) direction = Vector3.Normalize(direction);
            }
            ShadowDirection = direction;
            _gap("Source shadow-map rendering is incomplete");
            return new(true);
        }
        if (Is("SetOutlineShaderParams"))
        {
            Outline = new(Number("min", 0), Number("max", 0), Number("rate", 0), Number("zscale", 0), Number("filldist", 40), Number("blendmode", 0));
            _gap("Source outline shader rendering is incomplete");
            return new(true);
        }
        if (Is("SetOutlineShaderColor"))
        {
            var index = checked((int)Number("index", 0));
            if (index is < 0 or > 255) throw new InvalidDataException("Invalid outline color index");
            _outlineColors[index] = new(Number("red", 0) / 255, Number("green", 0) / 255, Number("blue", 0) / 255, Number("alpha", 0) / 255);
            return new(true);
        }
        if (Is("Obj_SetOutlineFlag") || Is("Obj_SetOutlineColorIndex"))
        {
            if (!_exists(context.Object)) return new(false);
            var previous = _outlines.GetValueOrDefault(context.Object);
            var index = checked((int)Number("index", 0));
            if (index is < 0 or > 255) throw new InvalidDataException("Invalid outline color index");
            _outlines[context.Object] = Is("Obj_SetOutlineFlag") ? previous with { Enabled = parameters.Flag("on") } : previous with { Color = index };
            _gap("Source outline shader rendering is incomplete");
            return new(true);
        }
        if (Is("ViewportExists")) return new(parameters.Field("id")?.Integer is { } viewport && (viewport == Gh3Checksum.Value("bg_viewport") || viewport == Gh3Checksum.Value("ui")));
        if (Is("ScreenFX_FXInstanceExists") || Is("ScreenFX_AddFXInstance") || Is("ScreenFX_UpdateFXInstanceParams") || Is("ScreenFX_RemoveFXInstance"))
        {
            var viewport = parameters.Field("viewport")?.Integer ?? throw new InvalidDataException("Screen effect viewport is missing");
            var name = parameters.Field("name")?.Integer ?? throw new InvalidDataException("Screen effect name is missing");
            var target = new StageScreenTarget(viewport, name);
            var exists = _screenEffects.TryGetValue(target, out var previous);
            if (Is("ScreenFX_FXInstanceExists")) return new(exists);
            if (Is("ScreenFX_RemoveFXInstance")) return new(_screenEffects.Remove(target));
            if (Is("ScreenFX_UpdateFXInstanceParams") && !exists) return new(false);
            var duration = Number("time", 0);
            if (duration is < 0 or > 86400) throw new InvalidDataException("Invalid screen effect duration");
            var from = previous?.Sample(time) ?? (parameters.Field("Type")?.Integer == StageScreenColor.Type ? StageScreenColor.Defaults : parameters);
            var to = Gh3ScriptRuntime.Structure(from.Fields.Where(field => parameters.Field(field.Name) == null).Concat(parameters.Fields));
            _screenEffects[target] = new(time, Math.Truncate(duration * 1000) / 1000, from, to);
            if (to.Field("Type")?.Integer != StageScreenColor.Type) _gap("Source screen-effect type is not supported: " + Gh3QbText.Encode(to.Field("Type") ?? Gh3ScriptRuntime.Number(0)));
            return new(true);
        }
        return null;
    }
}
