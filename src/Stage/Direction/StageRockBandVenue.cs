using System.Globalization;
using System.Text.RegularExpressions;

namespace Gh3Skin;

internal readonly record struct StageTickCue(long Tick, string Type, string Value, long Length = 0);

// Maps the Rock Band VENUE track (RB1/RB2 notes and text events, RB3 text events) to GH3 camera notes,
// lightshow moods and notes, and effects. The table and the reasons are in docs/CUSTOM-CONTENT.md.
internal static class StageRockBandVenue
{
    // A Rock Band shot that lasts longer than this gets GH3's own 3–8 s cuts inside the selected camera group.
    public const double MaxCameraHold = 8;
    private const double DefaultBlend = Gh3CommandDispatch.DefaultLightBlend;
    private const int StepLimit = 4096;
    private static readonly Regex Lighting = new(@"^lighting\s*\((.*)\)$", RegexOptions.CultureInvariant);
    private static readonly Regex Cut = new(@"^(?:do_(?:directed|optional)_cut\s+)?(directed_\w+)$", RegexOptions.CultureInvariant);
    private static readonly Regex Coop = new(@"^coop_([a-z]+)_(behind|near|far|closeup_hand|closeup_head|closeup)$", RegexOptions.CultureInvariant);

    private sealed record Light(string Mood, string? Color = null, double Blend = DefaultBlend, bool Keyframed = false,
        double StepBeats = 0, bool Toggle = false, double Release = DefaultBlend);

    private static readonly Dictionary<string, Light> Lights = new()
    {
        [""] = new("verse", Keyframed: true),
        ["verse"] = new("verse", Keyframed: true),
        ["chorus"] = new("chorus", Keyframed: true),
        ["dischord"] = new("chorus", "red", Keyframed: true),
        ["manual_cool"] = new("verse", "blue", Keyframed: true),
        ["manual_warm"] = new("verse", "orange", Keyframed: true),
        ["stomp"] = new("strobe", Keyframed: true, Toggle: true),
        ["intro"] = new("intro"),
        ["loop_cool"] = new("verse", "blue", StepBeats: 2),
        ["loop_warm"] = new("verse", "orange", StepBeats: 2),
        ["harmony"] = new("verse", StepBeats: 2),
        ["searchlights"] = new("verse", StepBeats: 1),
        ["sweep"] = new("chorus", StepBeats: 2),
        ["frenzy"] = new("chorus", StepBeats: 1),
        ["bre"] = new("chorus", StepBeats: 1),
        ["silhouettes"] = new("blackout"),
        ["silhouettes_spot"] = new("solo"),
        ["blackout_spot"] = new("blackout"),
        ["blackout_fast"] = new("blackout", Blend: 0.2),
        ["blackout_slow"] = new("blackout", Blend: 2),
        ["flare_fast"] = new("flare", Blend: 0),
        ["flare_slow"] = new("flare", Blend: 0, Release: 1),
        ["strobe_fast"] = new("strobe", StepBeats: 0.125, Toggle: true),
        ["strobe_slow"] = new("strobe", StepBeats: 0.25, Toggle: true),
    };

    private static readonly string[] Moods = { "intro", "verse", "chorus", "solo", "flare", "blackout", "strobe" };
    private static readonly string[] Colors = { "red", "green", "blue", "yellow", "orange", "purple", "magenta", "cyan", "white" };

    public static string Normalize(string text)
    {
        var value = text.Trim();
        if (value.StartsWith('[') && value.EndsWith(']')) value = value[1..^1].Trim();
        return value.ToLowerInvariant();
    }

    // The single lighting value for a chart text event outside the VENUE track: GH3 moods and colors stay as they are.
    public static string LightingValue(string name)
    {
        name = name.Trim().ToLowerInvariant();
        if (Moods.Contains(name) || Colors.Contains(name)) return name;
        return Lights.TryGetValue(name, out var light) ? light.Mood : "verse";
    }

    public static List<StageTickCue> Decode(IEnumerable<(long Tick, int Note, long Length)> notes, IEnumerable<(long Tick, string Text)> texts,
        int resolution, long end, out string? diagnostic, out List<(long Tick, string Text)> unhandled)
    {
        var ignored = new SortedDictionary<string, int>();
        var unknown = new SortedSet<string>();
        void Ignore(string reason) => ignored[reason] = ignored.GetValueOrDefault(reason) + 1;
        var cameras = new SortedDictionary<long, List<(int Priority, int Note)>>();
        void Camera(long tick, int priority, int note)
        {
            if (!cameras.TryGetValue(tick, out var list)) cameras[tick] = list = new();
            list.Add((priority, note));
        }
        var lights = new List<(long Tick, string Name, Light Light)>();
        var keyframes = new List<(long Tick, string Kind)>();
        var effects = new List<StageTickCue>();
        unhandled = new();

        foreach (var group in notes.GroupBy(n => n.Tick))
        {
            var pitches = group.Select(n => n.Note).ToHashSet();
            foreach (var note in pitches)
            {
                if (note is >= 96 and <= 110) Ignore("post-processing");
                else if (note is >= 85 and <= 87) Ignore("sing-along");
                else if (note is >= 37 and <= 41) Ignore("spotlight");
                else if (note is >= 48 and <= 50) keyframes.Add((group.Key, note == 48 ? "next" : note == 49 ? "prev" : "first"));
                else if (note is not (>= 60 and <= 64 or >= 70 and <= 73)) Ignore("other note");
            }
            if (NoteCut(pitches) is { } cut) Camera(group.Key, 1, cut);
        }

        foreach (var (tick, raw) in texts)
        {
            var text = Normalize(raw);
            if (text.EndsWith(".pp", StringComparison.Ordinal)) Ignore("post-processing");
            else if (Lighting.Match(text) is { Success: true } lighting) Add(lighting.Groups[1].Value.Trim());
            else if (text is "verse" or "chorus") Add(text);
            else if (text is "first" or "next" or "prev") keyframes.Add((tick, text));
            else if (text is "bonusfx" or "bonusfx_optional") effects.Add(new(tick, "pyro", ""));
            else if (text == "fogon") effects.Add(new(tick, "smoke", ""));
            else if (text == "fogoff") Ignore("fog off");
            else if (Cut.Match(text) is { Success: true } directed)
            {
                var note = Directed(directed.Groups[1].Value);
                if (note == null) unknown.Add(text);
                Camera(tick, 0, note ?? 79);
            }
            else if (Coop.Match(text) is { Success: true } coop && CoopCut(coop.Groups[1].Value, coop.Groups[2].Value) is { } standard)
                Camera(tick, standard.Keys ? 3 : coop.Groups[2].Value == "behind" ? 2 : 1, standard.Note);
            else
            {
                if (!text.StartsWith("stage ", StringComparison.Ordinal) && !text.StartsWith("gh3 ", StringComparison.Ordinal)) unknown.Add(text);
                unhandled.Add((tick, raw));
            }

            void Add(string name)
            {
                if (Moods.Contains(name) || Colors.Contains(name)) lights.Add((tick, name, new Light(name, Keyframed: name is "verse" or "chorus")));
                else if (Lights.TryGetValue(name, out var light)) lights.Add((tick, name, light));
                else
                {
                    unknown.Add($"lighting ({name})");
                    lights.Add((tick, name, Lights["verse"]));
                }
            }
        }

        var output = new List<StageTickCue>();
        var cutTicks = cameras.Keys.ToArray();
        var stacked = 0;
        for (var i = 0; i < cutTicks.Length; i++)
        {
            var best = cameras[cutTicks[i]].Min(c => c.Priority);
            var candidates = cameras[cutTicks[i]].Where(c => c.Priority == best).ToArray();
            // Magma writes stacked alternatives in name order, so "first" would nearly always be the behind cut; rotate instead.
            var choice = candidates.Length == 1 ? candidates[0] : candidates[stacked++ % candidates.Length];
            output.Add(new(cutTicks[i], "gh3-camera", choice.Note.ToString(CultureInfo.InvariantCulture),
                i + 1 < cutTicks.Length ? cutTicks[i + 1] - cutTicks[i] : 0));
        }

        // One lighting cue per tick: the last one in the track, but an intro transition yields to a real cue at the same time.
        lights = lights.GroupBy(l => l.Tick).OrderBy(g => g.Key)
            .Select(g => g.Where(l => l.Light.Mood != "intro").DefaultIfEmpty(g.Last()).Last()).ToList();
        // Rock Band blends when a cue repeats: from the second cue to the next different cue. The next cue starts at the
        // repeat and blends over the span. Length marks that span on the blend cue; the reader converts it to seconds.
        var blends = new Dictionary<int, long>();
        for (var i = 1; i < lights.Count; i++)
        {
            if (lights[i].Name != lights[i - 1].Name) continue;
            var from = lights[i].Tick;
            var j = i;
            while (j < lights.Count && lights[j].Name == lights[i - 1].Name) j++;
            lights.RemoveRange(i, j - i);
            if (i < lights.Count)
            {
                blends[i] = lights[i].Tick - from;
                lights[i] = lights[i] with { Tick = from };
            }
        }
        keyframes = keyframes.OrderBy(k => k.Tick).ToList();
        string? color = null;
        var blend = DefaultBlend;
        var release = DefaultBlend;
        var keyframe = 0;
        var lightCount = 0;
        for (var i = 0; i < lights.Count; i++)
        {
            var (tick, _, light) = lights[i];
            var next = i + 1 < lights.Count ? lights[i + 1].Tick : end;
            for (; keyframe < keyframes.Count && keyframes[keyframe].Tick < tick; keyframe++)
                Ignore(i == 0 ? "keyframe before lighting" : "keyframe in automatic lighting");
            var passthrough = Colors.Contains(light.Mood);
            var targetColor = passthrough ? light.Mood : light.Color;
            var recolor = !passthrough && targetColor != color;
            if (recolor) output.Add(new(tick, "color", targetColor ?? "off"));
            color = targetColor;
            var origin = tick;
            if (blends.TryGetValue(i, out var span))
            {
                origin = tick + span;
                output.Add(new(tick, "blend", "", span));
                output.Add(new(origin, "blend", light.Blend.ToString(CultureInfo.InvariantCulture)));
                blend = light.Blend;
            }
            else
            {
                var targetBlend = release != DefaultBlend ? release : light.Blend;
                if (targetBlend != blend) output.Add(new(tick, "blend", targetBlend.ToString(CultureInfo.InvariantCulture)));
                blend = targetBlend;
            }
            release = light.Release;
            output.Add(new(tick, "lighting", light.Mood));
            lightCount++;
            // GH3 keeps the running snapshot when the mood does not change, so a new override color needs a snapshot step.
            if (recolor && i > 0 && lights[i - 1].Light.Mood == light.Mood) output.Add(new(tick, "gh3-note", "58"));
            var stepped = new HashSet<long> { tick };
            for (; keyframe < keyframes.Count && keyframes[keyframe].Tick < next; keyframe++)
            {
                var (at, kind) = keyframes[keyframe];
                if (!light.Keyframed) Ignore("keyframe in automatic lighting");
                else if (light.Toggle && kind != "next") Ignore("stomp keyframe other than next");
                else if (light.Toggle) output.Add(new(at, "gh3-note", "60"));
                else if (stepped.Add(at)) output.Add(new(at, "gh3-note", "58"));
            }
            if (light.StepBeats > 0)
            {
                var step = Math.Max(1, (long)Math.Round(light.StepBeats * resolution));
                var count = 0;
                for (var at = origin + step; at < next && count < StepLimit; at += step, count++)
                    output.Add(new(at, "gh3-note", light.Toggle ? "60" : "58"));
            }
        }
        for (; keyframe < keyframes.Count; keyframe++) Ignore(lights.Count == 0 ? "keyframe before lighting" : "keyframe in automatic lighting");
        output.AddRange(effects);

        var parts = new List<string>();
        if (output.Count > 0 || ignored.Count > 0 || unknown.Count > 0)
        {
            parts.Add($"Rock Band VENUE: {cutTicks.Length} camera cuts, {lightCount} lighting cues, {effects.Count} effects mapped");
            if (blends.Count > 0) parts.Add($"{blends.Count} repeated lighting cues blend into the next cue");
            if (ignored.Count > 0) parts.Add("ignored " + string.Join(", ", ignored.Select(pair => $"{pair.Value} {pair.Key}")));
            if (unknown.Count > 0) parts.Add("unknown " + string.Join(", ", unknown.Take(8)) + (unknown.Count > 8 ? ", ..." : ""));
        }
        diagnostic = parts.Count > 0 ? string.Join("; ", parts) + "." : null;
        return output;
    }

    // RB1/RB2 camera notes: 60 random, 61–64 focus on bass, drums, guitar, vocals; 70–73 constraints.
    private static int? NoteCut(HashSet<int> pitches)
    {
        var focus = new[] { 61, 62, 63, 64 }.Where(pitches.Contains).ToArray();
        if (focus.Length == 0 && !pitches.Contains(60)) return null;
        var close = pitches.Contains(72);
        if (focus.Length > 1) return 85;
        if (focus.Length == 1)
            return focus[0] switch { 61 => 97, 62 => 89, 63 => close ? 95 : 84, _ => close ? 104 : 88 };
        return pitches.Contains(71) ? 87 : close ? 84 : pitches.Contains(73) ? 85 : 79;
    }

    // RB3 standard cuts. Keys has no GH3 performer, so it leaves the shot; the priority prefers cuts without keys.
    private static (int Note, bool Keys)? CoopCut(string who, string shot)
    {
        var band = who is "front" or "all";
        if (!band && who.Any(c => c is not ('g' or 'b' or 'd' or 'v' or 'k'))) return null;
        var keys = !band && who.Contains('k');
        var members = band ? who : who.Replace("k", "");
        if (shot == "behind") return (102, keys);
        if (shot == "far") return (87, keys);
        var note = members switch
        {
            "g" => shot == "closeup_hand" ? 95 : 84,
            "b" => 97,
            "d" => 89,
            "v" => shot is "closeup" or "closeup_head" ? 104 : 88,
            "gv" => 114,
            _ => 85
        };
        return (note, keys);
    }

    private static int? Directed(string cut) => cut switch
    {
        "directed_all" => 85,
        "directed_all_cam" => 112,
        "directed_all_lt" => 113,
        "directed_all_yeah" or "directed_brej" or "directed_crowd" => 87,
        "directed_bre" => 82,
        "directed_guitar" or "directed_guitar_np" or "directed_crowd_g" => 84,
        "directed_guitar_cam" or "directed_guitar_cam_pr" or "directed_guitar_cam_pt" => 112,
        "directed_guitar_cls" => 95,
        "directed_bass" or "directed_bass_np" or "directed_bass_cam" or "directed_bass_cls" or "directed_crowd_b" => 97,
        "directed_drums_lt" => 92,
        "directed_drums" or "directed_drums_np" or "directed_drums_pnt" or "directed_drums_kd" or "directed_duo_drums" => 89,
        "directed_vocal" or "directed_vocals" or "directed_vocal_np" or "directed_vocals_np" or "directed_vocal_cam" or "directed_vocals_cam"
            or "directed_vocals_cam_pr" or "directed_vocals_cam_pt" or "directed_duo_kv" => 88,
        "directed_vocal_cls" or "directed_vocals_cls" => 104,
        "directed_stagedive" or "directed_crowdsurf" => 117,
        "directed_keys" or "directed_keys_np" or "directed_keys_cam" or "directed_duo_bass" or "directed_duo_gb" => 85,
        "directed_duo_guitar" => 114,
        "directed_duo_kg" => 84,
        "directed_duo_kb" => 97,
        _ => null
    };
}

// Cues for charts without direction: a light mood and a camera cut at each section, and a lightshow step on each bar.
internal static class StageGeneratedDirection
{
    private const int StepLimit = 20000;

    public static List<StageTickCue> FromSections(IReadOnlyList<(long Tick, string Name)> sections, int resolution, long end)
    {
        var output = new List<StageTickCue>();
        if (sections.Count == 0) output.Add(new(0, "lighting", "verse"));
        foreach (var (tick, name) in sections)
        {
            var mood = Mood(name);
            output.Add(new(tick, "lighting", mood));
            output.Add(new(tick, "gh3-camera", mood == "solo" ? "81" : "79"));
        }
        var starts = sections.Select(s => s.Tick).ToHashSet();
        var bar = 4L * resolution;
        var count = 0;
        for (var at = (sections.Count > 0 ? sections[0].Tick : 0) + bar; at < end && count < StepLimit; at += bar, count++)
            if (!starts.Contains(at)) output.Add(new(at, "gh3-note", "58"));
        return output;
    }

    public static string? Section(string text)
    {
        var value = StageRockBandVenue.Normalize(text);
        if (value.StartsWith("section ", StringComparison.Ordinal)) return value[8..].Trim();
        if (value.StartsWith("section_", StringComparison.Ordinal)) return value[8..].Trim();
        if (value.StartsWith("prc_", StringComparison.Ordinal)) return value[4..].Trim();
        return null;
    }

    public static string Mood(string section)
    {
        var name = section.Replace('_', ' ').Replace('-', ' ');
        if (name.Contains("solo")) return "solo";
        if (name.Contains("chorus") && !name.Contains("pre chorus") && !name.Contains("prechorus")) return "chorus";
        if (name.StartsWith("intro", StringComparison.Ordinal)) return "intro";
        return "verse";
    }
}
