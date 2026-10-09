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
    public Gh3Song? NativeSong { get; init; }
    public string SourceSong { get; init; } = "";
    public string Countoff { get; init; } = "";
    private readonly string[] _commandDiagnostics;
    private IReadOnlyList<string> _diagnostics = Array.Empty<string>();
    public IReadOnlyList<string> Diagnostics
    {
        get => _commandDiagnostics.Concat(_diagnostics).Distinct().ToArray();
        init => _diagnostics = value;
    }
    public IReadOnlyDictionary<string, string> ChannelSources { get; init; } = new Dictionary<string, string>();
    public StageCue[] Cues { get; }
    public StageCue[] PlaybackCues { get; }
    public double[] Beats { get; }
    // The earliest time that the stage plays: song time 0, or an earlier cue such as GH3's pre-song transition.
    public double Start => Math.Min(0, Cues.Length > 0 ? Cues[0].Time : 0);
    private readonly Dictionary<string, StageCue[]> _tracks;

    public StageTimeline(IEnumerable<StageCue> cues, string source)
    {
        Source = source;
        var authored = cues.OrderBy(cue => cue.Time).ToArray();
        var diagnostics = new Dictionary<string, int>();
        void Report(string message) => diagnostics[message] = diagnostics.GetValueOrDefault(message) + 1;
        var playback = new List<StageCue>();
        foreach (var cue in authored)
        {
            if (cue.Type != "gh3-script")
            {
                playback.Add(cue);
                if (AudioNote(cue) is { } audio)
                {
                    playback.Add(audio);
                    continue;
                }
                if (cue.Type is "vocal" or "crowd" or "gh3-scripts-note" or "gh3-triggers-note" ||
                    cue.Type == "gh3-note" && (!int.TryParse(cue.Value, out var light) || FromGh3Note(cue.Time, light) == null) ||
                    cue.Type == "fret" && (!int.TryParse(cue.Value, out var fret) || fret is not (>= 101 and <= 110) and not (>= 118 and <= 127)) ||
                    cue.Type == "drum" && (!int.TryParse(cue.Value, out var drum) || drum is not (>= 36 and <= 45) and not (>= 48 and <= 57)))
                    Report($"GH3 {cue.Type} note {cue.Value}: no playback handler");
                continue;
            }
            var call = Gh3ScriptEvent.Decode(cue.Value);
            var result = Gh3CommandDispatch.Resolve(call);
            if (result.Status != "implemented")
                Report($"GH3 {call.Track} {result.Name}: {result.Status}; {string.Join(" ", result.Gaps)}");
            if (result.Type.Length > 0)
                playback.Add(new(cue.Time, result.Type, result.Value, Role: result.Role, Immediate: result.Immediate,
                    Loop: result.Loop, Count: result.Count, DisableArms: result.DisableArms));
        }
        _commandDiagnostics = diagnostics.Select(item => $"{item.Key} ({item.Value} events; source data retained).").ToArray();
        var ordered = playback.ToArray();
        if (authored.Length > 100000 || authored.Concat(ordered).Any(cue => !double.IsFinite(cue.Time) || Math.Abs(cue.Time) > 86400 ||
                !double.IsFinite(cue.Duration) || cue.Duration < 0 || cue.Duration > 86400 || cue.Count is < 1 or > 100 ||
                cue.DisableArms is < 0 or > 2 || cue.Velocity is < 0 or > 127))
            throw new InvalidDataException("Invalid stage cue time or count");
        CueCount = authored.Length;
        Cues = authored;
        PlaybackCues = ordered;
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
        var blend = Gh3CommandDispatch.DefaultLightBlend;
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

    public static StageCue? AudioNote(StageCue cue)
    {
        if (!int.TryParse(cue.Value, out var note)) return null;
        string command;
        var parameters = new List<Gh3QbField>();
        if (cue.Type == "drum" && note == 70) command = "Countoff_NoteMap";
        else if (cue.Type == "crowd" && note is >= 72 and <= 80)
        {
            command = "GH3_Crowd_Event_Listener";
            parameters.Add(new(Gh3Checksum.Value("event_type"), new(13, -1, Gh3Checksum.Value(new[] { "applause", "surge_fast", "surge_slow" }[(note - 72) % 3]))));
            if (note is <= 74 or >= 78) parameters.Add(new(Gh3Checksum.Value("override_state"), new(1, -1, 1u)));
        }
        else return null;
        parameters.Add(new(Gh3Checksum.Value("velocity"), new(1, -1, (uint)(cue.Velocity ?? 100))));
        parameters.Add(new(Gh3Checksum.Value("length"), new(1, -1, (uint)Math.Round(cue.Duration * 1000))));
        var call = new Gh3ScriptEvent(cue.Type == "drum" ? "drums" : "crowd", new(10, -1, new[] {
            new Gh3QbField(Gh3Checksum.Value("time"), new(1, -1, unchecked((uint)(int)Math.Round(cue.Time * 1000)))),
            new Gh3QbField(Gh3Checksum.Value("scr"), new(13, -1, Gh3Checksum.Value(command))),
            new Gh3QbField(Gh3Checksum.Value("params"), new(10, -1, parameters)) }));
        return new(cue.Time, "gh3-audio", call.Encode());
    }

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
        >= 39 and <= 53 => new(time, "blend", new[] { "0.15", "0", "0.05", "0.1", "0.15", "0.2", "0.25", "0.3", "0.4", "0.5", "0.6", "0.7", "0.8", "0.9", "1" }[note - 39]),
        _ => null
    };

    public static StageCue? FromText(double time, string text)
    {
        var exact = text.Trim();
        if (exact.StartsWith('[') && exact.EndsWith(']')) exact = exact[1..^1].Trim();
        if (exact.StartsWith("gh3 ", StringComparison.OrdinalIgnoreCase))
            return new(time, "gh3-script", Gh3ScriptEvent.Decode(exact[4..]).Encode());
        var value = text.Trim().Trim('[', ']').Trim().ToLowerInvariant();
        if (value is "bonusfx" or "bonusfx_optional") return new(time, "pyro");
        if (value.StartsWith("lighting (") && value.EndsWith(")"))
            return new(time, "lighting", StageRockBandVenue.LightingValue(value[10..^1]));
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
