using System.Globalization;
using System.Text.Json;

namespace Gh3Skin;

internal static class StageSongDirection
{
    public static string? Channel(StageCue cue) => cue.Type switch
    {
        "camera" or "gh3-camera" => "camera",
        "gh3-script" => ScriptChannel(cue),
        "gh3-audio" => AudioChannel(cue),
        "crowd-lighters" or "crowd-animation" or "crowd-hand" => "crowd-control",
        "stage-diver" => "stage-diver/" + cue.Role,
        "actor-play" or "actor-hide" or "actor-show" => "actor/" + cue.Role,
        "vocal" or "crowd" or "gh3-scripts-note" or "gh3-triggers-note" => cue.Type,
        "stance" or "perform" or "clip" => "body/" + cue.Role,
        "walk" or "face" or "arms" or "strum-style" or "strum" => cue.Type + "/" + cue.Role,
        "fret" when int.TryParse(cue.Value, out var note) => note switch
        {
            >= 118 and <= 127 => "fret/guitarist",
            >= 101 and <= 110 => "fret/bassist",
            _ => "fret"
        },
        "drum" => "drum",
        "lighting" or "color" or "blend" or "snapshot" or "strobe-toggle" => "lighting",
        "gh3-note" when int.TryParse(cue.Value, out var note) && StageTimeline.FromGh3Note(cue.Time, note) is { } mapped => Channel(mapped),
        "gh3-note" => "lighting",
        "pyro" or "sparks" or "fireworks" or "smoke" or "flash" => cue.Type,
        "tempo" or "beat" => "timing",
        _ => null
    };

    private static string ScriptChannel(StageCue cue)
    {
        var call = Gh3ScriptEvent.Decode(cue.Value);
        var result = Gh3CommandDispatch.Resolve(call);
        return result.Type.Length > 0 ? Channel(new(cue.Time, result.Type, result.Value, Role: result.Role)) ?? "gh3-script/" + call.Track :
            $"gh3-script/{call.Track}/{call.Command:x8}";
    }

    private static string AudioChannel(StageCue cue)
    {
        var call = Gh3ScriptEvent.Decode(cue.Value);
        return call.Command == Gh3Checksum.Value("Countoff_NoteMap") || call.Command == Gh3Checksum.Value("GH_SFX_Countoff_Logic") ? "drum" : "audio";
    }

    public static void Validate(StageEventFile file, JsonElement document)
    {
        if (file.Library != "gh3-band-v1") throw new InvalidDataException("library must be gh3-band-v1");
        if (file.OriginalSongLookup is not ("auto" or "off")) throw new InvalidDataException("originalSongLookup must be auto or off");
        if (file.ClearChannels == null || file.ClearChannels.Length > 64 ||
            file.ClearChannels.Any(channel => !Clearable(channel)) || file.ClearChannels.Distinct().Count() != file.ClearChannels.Length)
            throw new InvalidDataException("clearChannels must contain unique supported channels; timing cannot be cleared");
        var events = document.EnumerateObject().FirstOrDefault(p => p.Name.Equals("events", StringComparison.OrdinalIgnoreCase)).Value;
        if (events.ValueKind != JsonValueKind.Array) throw new InvalidDataException("events must be an array");
        var index = 0;
        foreach (var entry in events.EnumerateArray())
        {
            if (!entry.EnumerateObject().Any(p => p.Name.Equals("time", StringComparison.OrdinalIgnoreCase)) ||
                !entry.EnumerateObject().Any(p => p.Name.Equals("type", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException($"events[{index}] requires time and type");
            ValidateCue(file.Events[index], $"events[{index}]");
            index++;
        }
        var conflict = file.Events.Select(Channel).FirstOrDefault(channel => file.ClearChannels.Contains(channel));
        if (conflict != null) throw new InvalidDataException($"events and clearChannels both supply {conflict}");
    }

    private static bool Clearable(string? channel)
    {
        if (channel is "camera" or "lighting" or "drum" or "pyro" or "sparks" or "fireworks" or "smoke" or "flash" or "crowd-control" or "audio") return true;
        var parts = channel?.Split('/');
        return parts?.Length == 2 && parts[0] switch
        {
            "body" or "face" => Performer(parts[1]),
            "stage-diver" => int.TryParse(parts[1], out var index) && index is >= 1 and <= 128,
            "actor" => parts[1].Length > 0 && parts[1].Length <= 128,
            "walk" or "arms" or "strum-style" or "strum" or "fret" => Instrument(parts[1]),
            _ => false
        };
    }

    private static bool Performer(string role) => role is "guitarist" or "bassist" or "singer" or "drummer";
    private static bool Instrument(string role) => role is "guitarist" or "bassist";

    private static void ValidateCue(StageCue cue, string field)
    {
        var roleValid = cue.Type switch
        {
            "stance" or "perform" or "clip" or "face" => Performer(cue.Role),
            "walk" or "arms" or "strum-style" or "strum" => Instrument(cue.Role),
            "crowd-animation" or "crowd-hand" => cue.Role.Length == 0 || cue.Role.Length == 6 && cue.Role.StartsWith("crowd", StringComparison.Ordinal) && cue.Role[5] is >= '1' and <= '8',
            "stage-diver" => int.TryParse(cue.Role, out var index) && index is >= 1 and <= 128,
            "actor-play" or "actor-hide" or "actor-show" => cue.Role.Length is > 0 and <= 128,
            _ => cue.Role.Length == 0
        };
        if (!roleValid) throw new InvalidDataException($"{field}.role is invalid for {cue.Type}");
        if (Channel(cue) == null) throw new InvalidDataException($"{field} has no supported channel: {cue.Type} {cue.Value}");
        var valueValid = cue.Type switch
        {
            "pyro" or "sparks" or "fireworks" or "smoke" or "flash" or "snapshot" or "strobe-toggle" or "beat" => true,
            "arms" => cue.Value is "0" or "1" or "2",
            "strum" => int.TryParse(cue.Value, out var mask) && mask is >= 0 and <= 31,
            "drum" or "fret" or "vocal" or "crowd" or "gh3-note" or "gh3-scripts-note" or "gh3-triggers-note" =>
                int.TryParse(cue.Value, out var note) && note is >= 0 and <= 127,
            "tempo" => double.TryParse(cue.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var bpm) && double.IsFinite(bpm) && bpm > 0,
            "clip" or "face" => cue.Value.Length == 8 && cue.Value.All(Uri.IsHexDigit),
            "crowd-lighters" => cue.Value is "start" or "stop" or "on" or "off",
            "crowd-animation" => cue.Value is "idle" or "special" or "starpower",
            "crowd-hand" => StageCrowdState.ValidHand(cue.Value),
            "stage-diver" => cue.Value is "jump" or "hide",
            "actor-hide" or "actor-show" => true,
            _ => !string.IsNullOrWhiteSpace(cue.Value)
        };
        if (!valueValid) throw new InvalidDataException($"{field}.value is invalid for {cue.Type}");
    }

    public static StageTimeline Resolve(StageEventFile file, StageTimeline authored, StageChartData chart, StageTimeline? original,
        StagePerformance? performance = null)
    {
        var selected = file.ClearChannels.ToDictionary(channel => channel, _ => authored.Source + " (cleared)");
        var cues = new List<StageCue>();
        void Select(IEnumerable<StageCue> events, string source, bool validate = false)
        {
            var mapped = events.Select(cue => (Cue: cue, Channel: Channel(cue))).Where(item => item.Channel != null).ToArray();
            var channels = mapped.Select(item => item.Channel!).Where(channel => !selected.ContainsKey(channel)).ToHashSet();
            foreach (var item in mapped.Where(item => channels.Contains(item.Channel!)))
            {
                if (validate)
                {
                    ValidateCue(item.Cue, source);
                    ValidateReference(item.Cue, performance, source);
                }
                cues.Add(item.Cue);
            }
            foreach (var channel in channels) selected.Add(channel, source);
        }
        foreach (var cue in authored.Cues) ValidateReference(cue, performance, authored.Source);
        Select(chart.Timing, chart.Timeline.Source + " tempo map");
        Select(authored.Cues, authored.Source);
        Select(chart.Direction, chart.Timeline.Source, validate: true);
        Select(chart.Strums, chart.Timeline.Source + " Expert notes");
        if (original != null) Select(original.Cues, original.Source);
        var source = authored.Source + " v2 [" + string.Join("; ", selected.OrderBy(pair => pair.Key)
            .Select(pair => pair.Key + "=" + pair.Value)) + "]";
        return new StageTimeline(cues, source) { ChannelSources = selected,
            SourceSong = authored.SourceSong.Length > 0 ? authored.SourceSong : original?.SourceSong ?? "",
            Countoff = authored.Countoff.Length > 0 ? authored.Countoff : original?.Countoff ?? "" };
    }

    private static void ValidateReference(StageCue cue, StagePerformance? performance, string source)
    {
        if (performance == null || cue.Type is not ("stance" or "perform" or "clip" or "face" or "strum-style")) return;
        if (!performance.Members.TryGetValue(cue.Role, out var member))
            throw new InvalidDataException($"{source}: no performance profile for {cue.Role}");
        var valid = cue.Type switch
        {
            "stance" => member.Groups.Keys.Any(key => key.Split('/') is { Length: >= 3 } parts && parts[0] == cue.Value &&
                (parts[1] == "transitions" || BodyGroup(parts))),
            "perform" => member.Groups.Keys.Any(key => key.Split('/') is { } parts && BodyGroup(parts) && parts[1] == cue.Value),
            "clip" or "face" => member.Clips.ContainsKey(cue.Value),
            "strum-style" => member.Layers.Keys.Any(key => key.StartsWith("strum/" + cue.Value + "/", StringComparison.Ordinal)),
            _ => true
        };
        if (!valid) throw new InvalidDataException($"{source}: unresolved {cue.Type} '{cue.Value}' for {cue.Role}");
    }

    private static bool BodyGroup(string[] parts) => parts.Length == 4 && parts[2] is "bad" or "normal" or "good";
}
