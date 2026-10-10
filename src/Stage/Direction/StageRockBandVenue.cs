using System.Globalization;
using System.Text.RegularExpressions;

namespace Gh3Skin;

// Delay: seconds after Tick (a flare pulse returns a fixed time after its peak, independent of the tempo).
internal readonly record struct StageTickCue(long Tick, string Type, string Value, long Length = 0, double Delay = 0);

// Maps the Rock Band VENUE track (RB1/RB2 notes and text events, RB3 text events) to GH3 camera notes,
// lightshow moods and notes, and effects. The table and the reasons are in docs/CUSTOM-CONTENT.md.
internal static class StageRockBandVenue
{
    // A Rock Band shot that lasts longer than this gets GH3's own 3–8 s cuts inside the selected camera group.
    public const double MaxCameraHold = 8;
    private const double DefaultBlend = Gh3CommandDispatch.DefaultLightBlend;
    private const int StepLimit = 4096;
    private const string NextSnapshotNote = "58"; // GH3: Gh3LightshowNote.NextSnapshot
    private const string StrobeToggleNote = "60"; // GH3: Gh3LightshowNote.StrobeToggle
    private static readonly Regex Lighting = new(@"^lighting\s*\((.*)\)$", RegexOptions.CultureInvariant);
    private static readonly Regex Cut = new(@"^(?:do_(?:directed|optional)_cut\s+)?(directed_\w+)$", RegexOptions.CultureInvariant);
    private static readonly Regex Coop = new(@"^coop_([a-z]+)_(behind|near|far|closeup_hand|closeup_head|closeup)$", RegexOptions.CultureInvariant);

    private sealed record Light(string Mood, string? Color = null, double Blend = DefaultBlend, bool Keyframed = false,
        double StepBeats = 0, bool Toggle = false, double Release = DefaultBlend, bool Pulse = false);
    // Origin: the tick where the cue is fully reached and its steps start; Fade: the blend in seconds into this cue.
    private sealed record Entry(long Tick, string Name, Light Light, double Delay = 0, double? Fade = null, long Origin = -1);

    // A flare rises over GH3's default blend and the light turns back before the peak, as in GH3's own pulses (flare note,
    // then a mood note a moment later). The peak is a fraction of the flare snapshot, not a white-out.
    private const double PulseRise = DefaultBlend;
    // WCAG 2.3.1: at most three flashes in any one second.
    private static double MinPulseGap(bool reduce) => reduce ? 1 : WcagFlashGap;
    private const double WcagFlashGap = 0.34;
    // Tuned: the pulse peak as a fraction of the flare snapshot (reduced: 0.2), and the shortest strobe toggle gap.
    private static double PulsePeak(bool reduce) => reduce ? ReducedPulsePeak : NormalPulsePeak;
    private const double NormalPulsePeak = 0.4;
    private const double ReducedPulsePeak = 0.2;
    private const double MinToggleGap = 0.2;
    // Player option (PlayerChoices.ReduceFlashing): weaker and rarer flare pulses, and slower strobes.
    public static bool ReduceFlashing { get; set; }

    // Rock Band lighting cue to GH3 mood and color, with blends and step rates (Tuned: the J rows of
    // docs/CUSTOM-CONTENT.md#lighting).
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
        ["flare_fast"] = new("flare", Release: 0.3, Pulse: true),
        ["flare_slow"] = new("flare", Release: 1, Pulse: true),
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
        int resolution, long end, out string? diagnostic, out List<(long Tick, string Text)> unhandled, Func<long, double>? seconds = null)
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
                if (note is >= RockBandVenueNote.PostProcessingFirst and <= RockBandVenueNote.PostProcessingLast) Ignore("post-processing");
                else if (note is >= RockBandVenueNote.SingAlongFirst and <= RockBandVenueNote.SingAlongLast) Ignore("sing-along");
                else if (note is >= RockBandVenueNote.SpotlightFirst and <= RockBandVenueNote.SpotlightLast) Ignore("spotlight");
                else if (note is >= RockBandVenueNote.KeyframeNext and <= RockBandVenueNote.KeyframeFirst) keyframes.Add((group.Key, note == RockBandVenueNote.KeyframeNext ? "next" : note == RockBandVenueNote.KeyframePrevious ? "prev" : "first"));
                else if (note is not (>= RockBandVenueNote.CameraRandom and <= RockBandVenueNote.FocusVocalist or >= RockBandVenueNote.ConstraintFirst and <= RockBandVenueNote.NoCloseups)) Ignore("other note");
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
                Camera(tick, 0, note ?? Gh3CameraNote.Any);
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
        seconds ??= tick => tick * 0.5 / resolution;
        var entries = lights.GroupBy(l => l.Tick).OrderBy(g => g.Key)
            .Select(g => g.Where(l => l.Light.Mood != "intro").DefaultIfEmpty(g.Last()).Last())
            .Select(l => new Entry(l.Tick, l.Name, l.Light)).ToList();
        // Rock Band blends when a cue repeats: from the second cue to the next different cue. The next cue starts at the
        // repeat and blends over the span; its snapshot steps start at its own time.
        var repeats = 0;
        for (var i = 1; i < entries.Count; i++)
        {
            if (entries[i].Name != entries[i - 1].Name) continue;
            var from = entries[i].Tick;
            var j = i;
            while (j < entries.Count && entries[j].Name == entries[i - 1].Name) j++;
            entries.RemoveRange(i, j - i);
            if (i < entries.Count)
            {
                repeats++;
                entries[i] = entries[i] with { Tick = from, Origin = entries[i].Tick, Fade = seconds(entries[i].Tick) - seconds(from) };
            }
        }
        var (pulses, dropped) = Pulses(entries, seconds);
        keyframes = keyframes.OrderBy(k => k.Tick).ToList();
        string? color = null;
        var blend = DefaultBlend;
        var release = DefaultBlend;
        var keyframe = 0;
        var lightCount = 0;
        var lastToggle = double.NegativeInfinity;
        void Toggle(long at)
        {
            // Reduce flashing: at most 2.5 strobe flashes (on and off) per second.
            if (ReduceFlashing && seconds(at) - lastToggle < MinToggleGap) return;
            lastToggle = seconds(at);
            output.Add(new(at, "gh3-note", StrobeToggleNote));
        }
        for (var i = 0; i < entries.Count; i++)
        {
            var (tick, _, light, delay, fade, start) = entries[i];
            var origin = start >= 0 ? start : tick;
            var next = i + 1 < entries.Count ? entries[i + 1].Tick : end;
            for (; keyframe < keyframes.Count && keyframes[keyframe].Tick < tick; keyframe++)
                Ignore(i == 0 ? "keyframe before lighting" : "keyframe in automatic lighting");
            var passthrough = Colors.Contains(light.Mood);
            var targetColor = passthrough ? light.Mood : light.Color;
            var recolor = !passthrough && targetColor != color;
            if (recolor) output.Add(new(tick, "color", targetColor ?? "off", Delay: delay));
            color = targetColor;
            if (fade is { } span)
            {
                output.Add(new(tick, "blend", span.ToString("0.###", CultureInfo.InvariantCulture), Delay: delay));
                blend = span;
                if (origin != tick)
                {
                    output.Add(new(origin, "blend", light.Blend.ToString(CultureInfo.InvariantCulture)));
                    blend = light.Blend;
                }
            }
            else
            {
                var targetBlend = release != DefaultBlend ? release : light.Blend;
                if (targetBlend != blend) output.Add(new(tick, "blend", targetBlend.ToString(CultureInfo.InvariantCulture), Delay: delay));
                blend = targetBlend;
            }
            release = light.Release;
            output.Add(new(tick, "lighting", light.Mood, Delay: delay));
            lightCount++;
            // GH3 keeps the running snapshot when the mood does not change, so a new override color needs a snapshot step.
            if (recolor && i > 0 && entries[i - 1].Light.Mood == light.Mood) output.Add(new(tick, "gh3-note", NextSnapshotNote, Delay: delay));
            var stepped = new HashSet<long> { tick, origin };
            for (; keyframe < keyframes.Count && keyframes[keyframe].Tick < next; keyframe++)
            {
                var (at, kind) = keyframes[keyframe];
                if (!light.Keyframed) Ignore("keyframe in automatic lighting");
                else if (light.Toggle && kind != "next") Ignore("stomp keyframe other than next");
                else if (light.Toggle) Toggle(at);
                else if (stepped.Add(at)) output.Add(new(at, "gh3-note", NextSnapshotNote));
            }
            if (light.StepBeats > 0)
            {
                var step = Math.Max(1, (long)Math.Round(light.StepBeats * resolution));
                var count = 0;
                for (var at = origin + step; at < next && count < StepLimit; at += step, count++)
                    if (light.Toggle) Toggle(at);
                    else output.Add(new(at, "gh3-note", NextSnapshotNote));
            }
        }
        for (; keyframe < keyframes.Count; keyframe++) Ignore(lights.Count == 0 ? "keyframe before lighting" : "keyframe in automatic lighting");
        output.AddRange(effects);

        var parts = new List<string>();
        if (output.Count > 0 || ignored.Count > 0 || unknown.Count > 0)
        {
            parts.Add($"Rock Band VENUE: {cutTicks.Length} camera cuts, {lightCount} lighting cues, {effects.Count} effects mapped");
            if (repeats > 0) parts.Add($"{repeats} repeated lighting cues blend into the next cue");
            if (pulses > 0) parts.Add($"{pulses} flares play as {PulsePeak(ReduceFlashing) * 100:0}% pulses" +
                (dropped > 0 ? $", {dropped} dropped to keep flares {MinPulseGap(ReduceFlashing):0.##} s apart" : "") +
                (ReduceFlashing ? " (reduce flashing)" : ""));
            if (ignored.Count > 0) parts.Add("ignored " + string.Join(", ", ignored.Select(pair => $"{pair.Value} {pair.Key}")));
            if (unknown.Count > 0) parts.Add("unknown " + string.Join(", ", unknown.Take(8)) + (unknown.Count > 8 ? ", ..." : ""));
        }
        diagnostic = parts.Count > 0 ? string.Join("; ", parts) + "." : null;
        return output;
    }

    // Shapes each flare into a pulse: the next cue (or, when it is far, the cue before the flare) starts at the pulse peak and
    // fades in, so it is reached at its own time. Flares closer than the minimum gap are dropped.
    private static (int Pulses, int Dropped) Pulses(List<Entry> entries, Func<long, double> seconds)
    {
        var hold = PulseRise * PulsePeak(ReduceFlashing);
        var last = double.NegativeInfinity;
        var pulses = 0;
        var dropped = 0;
        for (var i = 0; i < entries.Count; i++)
        {
            var flare = entries[i];
            if (!flare.Light.Pulse) continue;
            var time = seconds(flare.Tick) + flare.Delay;
            if (time - last < MinPulseGap(ReduceFlashing))
            {
                entries.RemoveAt(i--);
                dropped++;
                continue;
            }
            last = time;
            pulses++;
            if (i + 1 < entries.Count && seconds(entries[i + 1].Tick) + entries[i + 1].Delay - time <= hold) continue;
            if (i + 1 < entries.Count && (entries[i + 1].Fade != null ||
                seconds(entries[i + 1].Tick) + entries[i + 1].Delay - time <= hold + flare.Light.Release))
            {
                var next = entries[i + 1];
                var reached = next.Origin >= 0 ? next.Origin : next.Tick;
                entries[i + 1] = next with { Tick = flare.Tick, Delay = hold, Origin = reached, Fade = Math.Max(0, seconds(reached) - time - hold) };
                continue;
            }
            var before = entries.Take(i).LastOrDefault(entry => !entry.Light.Pulse) ?? new Entry(0, "intro", Lights["intro"]);
            entries.Insert(i + 1, before with { Tick = flare.Tick, Delay = hold, Fade = flare.Light.Release, Origin = -1 });
            i++;
        }
        return (pulses, dropped);
    }

    // RB1/RB2 camera notes: 60 random, 61–64 focus on bass, drums, guitar, vocals; 70–73 constraints.
    private static int? NoteCut(HashSet<int> pitches)
    {
        var focus = new[] { RockBandVenueNote.FocusBassist, RockBandVenueNote.FocusDrummer, RockBandVenueNote.FocusGuitarist, RockBandVenueNote.FocusVocalist }.Where(pitches.Contains).ToArray();
        if (focus.Length == 0 && !pitches.Contains(RockBandVenueNote.CameraRandom)) return null;
        var close = pitches.Contains(RockBandVenueNote.OnlyCloseups);
        if (focus.Length > 1) return Gh3CameraNote.Mid;
        if (focus.Length == 1)
            return focus[0] switch
            {
                RockBandVenueNote.FocusBassist => Gh3CameraNote.Bassist, RockBandVenueNote.FocusDrummer => Gh3CameraNote.Drummer,
                RockBandVenueNote.FocusGuitarist => close ? Gh3CameraNote.GuitarCloseup : Gh3CameraNote.Closeup, _ => close ? Gh3CameraNote.SingerCloseup : Gh3CameraNote.Singer
            };
        return pitches.Contains(RockBandVenueNote.OnlyFar) ? Gh3CameraNote.Wide : close ? Gh3CameraNote.Closeup : pitches.Contains(RockBandVenueNote.NoCloseups) ? Gh3CameraNote.Mid : Gh3CameraNote.Any;
    }

    // RB3 standard cuts. Keys has no GH3 performer, so it leaves the shot; the priority prefers cuts without keys.
    private static (int Note, bool Keys)? CoopCut(string who, string shot)
    {
        var band = who is "front" or "all";
        if (!band && who.Any(c => c is not ('g' or 'b' or 'd' or 'v' or 'k'))) return null;
        var keys = !band && who.Contains('k');
        var members = band ? who : who.Replace("k", "");
        if (shot == "behind") return (Gh3CameraNote.StageRear, keys);
        if (shot == "far") return (Gh3CameraNote.Wide, keys);
        var note = members switch
        {
            "g" => shot == "closeup_hand" ? Gh3CameraNote.GuitarCloseup : Gh3CameraNote.Closeup,
            "b" => Gh3CameraNote.Bassist,
            "d" => Gh3CameraNote.Drummer,
            "v" => shot is "closeup" or "closeup_head" ? Gh3CameraNote.SingerCloseup : Gh3CameraNote.Singer,
            "gv" => Gh3CameraNote.Pan,
            _ => Gh3CameraNote.Mid
        };
        return (note, keys);
    }

    private static int? Directed(string cut) => cut switch
    {
        "directed_all" => Gh3CameraNote.Mid,
        "directed_all_cam" => Gh3CameraNote.ZoomFast,
        "directed_all_lt" => Gh3CameraNote.ZoomOut,
        "directed_all_yeah" or "directed_brej" or "directed_crowd" => Gh3CameraNote.Wide,
        "directed_bre" => Gh3CameraNote.OrbitStage,
        "directed_guitar" or "directed_guitar_np" or "directed_crowd_g" => Gh3CameraNote.Closeup,
        "directed_guitar_cam" or "directed_guitar_cam_pr" or "directed_guitar_cam_pt" => Gh3CameraNote.ZoomFast,
        "directed_guitar_cls" => Gh3CameraNote.GuitarCloseup,
        "directed_bass" or "directed_bass_np" or "directed_bass_cam" or "directed_bass_cls" or "directed_crowd_b" => Gh3CameraNote.Bassist,
        "directed_drums_lt" => Gh3CameraNote.OrbitDrummer,
        "directed_drums" or "directed_drums_np" or "directed_drums_pnt" or "directed_drums_kd" or "directed_duo_drums" => Gh3CameraNote.Drummer,
        "directed_vocal" or "directed_vocals" or "directed_vocal_np" or "directed_vocals_np" or "directed_vocal_cam" or "directed_vocals_cam"
            or "directed_vocals_cam_pr" or "directed_vocals_cam_pt" or "directed_duo_kv" => Gh3CameraNote.Singer,
        "directed_vocal_cls" or "directed_vocals_cls" => Gh3CameraNote.SingerCloseup,
        "directed_stagedive" or "directed_crowdsurf" => Gh3CameraNote.StageDive,
        "directed_keys" or "directed_keys_np" or "directed_keys_cam" or "directed_duo_bass" or "directed_duo_gb" => Gh3CameraNote.Mid,
        "directed_duo_guitar" => Gh3CameraNote.Pan,
        "directed_duo_kg" => Gh3CameraNote.Closeup,
        "directed_duo_kb" => Gh3CameraNote.Bassist,
        _ => null
    };
}
