namespace Gh3Skin;

internal readonly record struct StageColor(float R, float G, float B)
{
    public static StageColor Mix(StageColor a, StageColor b, double amount)
    {
        var t = (float)Math.Clamp(amount, 0, 1);
        if (t == 0) return a;
        if (t == 1) return b;
        return new(a.R + (b.R - a.R) * t, a.G + (b.G - a.G) * t, a.B + (b.B - a.B) * t);
    }

    public static StageColor Named(string name) => name switch
    {
        "red" => new(1, 0.12f, 0.06f),
        "green" => new(0.12f, 1, 0.35f),
        "blue" or "intro" => new(0.15f, 0.35f, 1),
        "yellow" or "chorus" => new(1, 0.75f, 0.15f),
        "orange" => new(1, 0.3f, 0.05f),
        "purple" or "verse" => new(0.55f, 0.15f, 1),
        "magenta" => new(1, 0.1f, 0.65f),
        "cyan" or "solo" => new(0.1f, 0.85f, 1),
        "blackout" => new(0, 0, 0),
        _ => new(1, 1, 1)
    };
}

internal sealed record StageCue(double Time, string Type, string Value = "", double Duration = 0,
    string Role = "", bool Immediate = false, bool Loop = false, int Count = 1, int DisableArms = 0);
internal readonly record struct StageLightFrame(double Time, StageColor From, StageColor To, double Blend, bool Strobe, bool Lit)
{
    public StageColor Sample(double time) => StageColor.Mix(From, To, Blend <= 0 ? 1 : (time - Time) / Blend);
}

internal sealed class StageTimeline
{
    private readonly StageLightFrame[] _lights;
    public StageCue[] Effects { get; }
    public StageCue[] Cameras { get; }
    public int CueCount { get; }
    public string Source { get; }
    public IReadOnlyDictionary<string, string> ChannelSources { get; init; } = new Dictionary<string, string>();
    public StageCue[] Cues { get; }
    public double[] Beats { get; }
    private readonly Dictionary<string, StageCue[]> _tracks;

    public StageTimeline(IEnumerable<StageCue> cues, string source)
    {
        Source = source;
        var ordered = cues.OrderBy(cue => cue.Time).ToArray();
        if (ordered.Length > 100000 || ordered.Any(cue => !double.IsFinite(cue.Time) || Math.Abs(cue.Time) > 86400 ||
                !double.IsFinite(cue.Duration) || cue.Duration < 0 || cue.Duration > 86400 || cue.Count is < 1 or > 100 || cue.DisableArms is < 0 or > 2))
            throw new InvalidDataException("Invalid stage cue time or count");
        CueCount = ordered.Length;
        Cues = ordered;
        Beats = ordered.Where(cue => cue.Type == "beat").Select(cue => cue.Time).Distinct().ToArray();
        _tracks = ordered.GroupBy(cue => cue.Type + "/" + cue.Role).ToDictionary(g => g.Key, g => g.ToArray());
        Cameras = ordered.Where(cue => cue.Type is "camera" or "gh3-camera").ToArray();
        if (Cameras.Any(cue => cue.Type == "camera" && !StageCameraPath.ValidShot(cue.Value) ||
                cue.Type == "gh3-camera" && (!int.TryParse(cue.Value, out var pitch) || pitch is < 0 or > 127)))
            throw new InvalidDataException("Invalid stage camera shot");
        var effects = new List<StageCue>();
        var lights = new List<StageLightFrame>();
        var mood = "intro";
        string? colorOverride = null;
        var blend = 0.3;
        var snapshot = 0;
        var strobe = false;
        var lit = true;
        var frame = new StageLightFrame(double.NegativeInfinity, StageColor.Named(mood), StageColor.Named(mood), 0, false, true);
        foreach (var original in ordered)
        {
            var cue = original.Type == "gh3-note" && int.TryParse(original.Value, out var note) ? FromGh3Note(original.Time, note) : original;
            if (cue == null) continue;
            if (cue.Type is "pyro" or "sparks" or "fireworks" or "smoke" or "flash")
            {
                effects.Add(cue);
                continue;
            }
            switch (cue.Type)
            {
                case "blend":
                    if (!double.TryParse(cue.Value, System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out blend) || !double.IsFinite(blend) || blend < 0 || blend > 30)
                        throw new InvalidDataException("Invalid light blend time");
                    continue;
                case "lighting":
                    mood = cue.Value;
                    strobe = mood == "strobe";
                    lit = true;
                    snapshot = 0;
                    break;
                case "color": colorOverride = cue.Value == "off" ? null : cue.Value; break;
                case "snapshot": snapshot++; break;
                case "strobe-toggle": lit = !lit; break;
                default: continue;
            }
            var color = StageColor.Named(colorOverride ?? mood);
            if (mood == "blackout") color = StageColor.Named("blackout");
            else if (colorOverride == null && snapshot % 2 == 1)
                color = StageColor.Mix(color, StageColor.Named(mood == "chorus" ? "magenta" : "cyan"), 0.65);
            frame = new StageLightFrame(cue.Time, frame.Sample(cue.Time), color,
                strobe || mood == "blackout" ? 0 : blend, strobe, lit);
            lights.Add(frame);
        }
        _lights = lights.ToArray();
        Effects = effects.ToArray();
    }

    public StageCue[] Track(string type, string role = "") => _tracks.GetValueOrDefault(type + "/" + role, Array.Empty<StageCue>());

    public static int UpperBound<T>(IReadOnlyList<T> values, double time, Func<T, double> getTime)
    {
        var lo = 0;
        var hi = values.Count;
        while (lo < hi)
        {
            var mid = lo + (hi - lo) / 2;
            if (getTime(values[mid]) <= time) lo = mid + 1;
            else hi = mid;
        }
        return lo;
    }

    public double TempoAt(double time)
    {
        if (Beats.Length < 2)
        {
            var tempos = Track("tempo");
            var at = UpperBound(tempos, time, c => c.Time) - 1;
            return at >= 0 && double.TryParse(tempos[at].Value, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var bpm) && double.IsFinite(bpm) ? Math.Clamp(bpm, 20, 400) : 115;
        }
        var index = Math.Clamp(UpperBound(Beats, time, t => t) - 1, 0, Beats.Length - 2);
        return Math.Clamp(60 / (Beats[index + 1] - Beats[index]), 20, 400);
    }

    public StageColor Lighting(double time)
    {
        var lo = 0;
        var hi = _lights.Length;
        while (lo < hi)
        {
            var mid = lo + (hi - lo) / 2;
            if (_lights[mid].Time <= time) lo = mid + 1;
            else hi = mid;
        }
        if (lo == 0) return StageColor.Named("intro");
        var frame = _lights[lo - 1];
        if (frame.Strobe && (!frame.Lit || (time - frame.Time) % 0.16 >= 0.08)) return new(0, 0, 0);
        return frame.Sample(time);
    }

    public StageCue? CameraAt(double time)
    {
        var lo = 0;
        var hi = Cameras.Length;
        while (lo < hi)
        {
            var mid = lo + (hi - lo) / 2;
            if (Cameras[mid].Time <= time) lo = mid + 1;
            else hi = mid;
        }
        return lo == 0 ? null : Cameras[lo - 1];
    }

    public int FirstEffectAfter(double time)
    {
        var lo = 0;
        var hi = Effects.Length;
        while (lo < hi)
        {
            var mid = lo + (hi - lo) / 2;
            if (Effects[mid].Time < time) lo = mid + 1;
            else hi = mid;
        }
        return lo;
    }

    public static StageCue? FromGh3Note(double time, int note) => note switch
    {
        56 => new(time, "pyro"),
        57 or 58 => new(time, "snapshot"),
        60 => new(time, "strobe-toggle"),
        >= 61 and <= 69 => new(time, "color", new[] { "purple", "orange", "off", "magenta", "white", "yellow", "blue", "red", "green" }[note - 61]),
        >= 70 and <= 76 => new(time, "lighting", new[] { "strobe", "flare", "blackout", "solo", "chorus", "verse", "intro" }[note - 70]),
        >= 39 and <= 53 => new(time, "blend", new[] { "0.3", "0", "0.05", "0.1", "0.15", "0.2", "0.25", "0.3", "0.4", "0.5", "0.6", "0.7", "0.8", "0.9", "1" }[note - 39]),
        _ => null
    };

    public static StageCue? FromText(double time, string text)
    {
        var value = text.Trim().Trim('[', ']').Trim().ToLowerInvariant();
        if (value is "bonusfx" or "bonusfx_optional") return new(time, "pyro");
        if (value.StartsWith("lighting (") && value.EndsWith(")"))
            return new(time, "lighting", value[10..^1]);
        if (!value.StartsWith("stage ")) return null;
        var parts = value[6..].Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return null;
        if (parts.Length == 2 && parts[0] is "stance" or "perform" or "clip" or "face" or "walk" or "arms" or "strum-style" or "strum")
        {
            var arguments = parts[1].Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            if (arguments.Length == 2 && arguments[0] is "guitarist" or "bassist" or "singer" or "drummer")
                return new(time, parts[0], arguments[1], Role: arguments[0], Immediate: true);
        }
        return parts[0] switch
        {
            "pyro" or "sparks" or "fireworks" or "smoke" or "flash" => new(time, parts[0]),
            "blackout" or "strobe" => new(time, "lighting", parts[0]),
            "lights" when parts.Length == 2 => new(time, "lighting", parts[1]),
            "camera" when parts.Length == 2 => new(time, "camera", parts[1]),
            "fret" or "drum" when parts.Length == 2 => new(time, parts[0], parts[1]),
            _ => null
        };
    }
}
