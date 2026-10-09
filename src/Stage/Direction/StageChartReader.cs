using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Gh3Skin;

internal sealed class StageEventFile
{
    public int Version { get; set; }
    public string Library { get; set; } = "";
    public string OriginalSongLookup { get; set; } = "auto";
    public string[] ClearChannels { get; set; } = Array.Empty<string>();
    public double OffsetSeconds { get; set; }
    public StageCue[] Events { get; set; } = Array.Empty<StageCue>();
    public string SourceSong { get; set; } = "";
    public string Countoff { get; set; } = "";
}

internal sealed record StageChartData(StageTimeline Timeline, StageCue[] Direction, StageCue[] Strums, StageCue[] Timing);

internal sealed class StageEventIndex
{
    public int Version { get; set; }
    public StageSongEntry[] Songs { get; set; } = Array.Empty<StageSongEntry>();
}

internal sealed class StageSongEntry
{
    public string Title { get; set; } = "";
    public string Artist { get; set; } = "";
    public string File { get; set; } = "";
}

internal static class StageChartReader
{
    private sealed record TickCue(long Tick, string Text, int Note = -1, string Type = "", string Role = "", long Length = 0, int? Velocity = null);
    private sealed record Tempo(long Tick, double Microseconds);
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public static StageTimeline Load(string songDirectory, string chartPath, string? library = null, string title = "", string artist = "",
        StagePerformance? performance = null)
    {
        var native = Directory.Exists(songDirectory) ? Directory.EnumerateFiles(songDirectory)
            .Where(path => path.EndsWith("_song.pak.xen", StringComparison.OrdinalIgnoreCase)).ToArray() : Array.Empty<string>();
        if (native.Length > 1) throw new InvalidDataException("The song folder contains more than one native GH3 song PAK.");
        if (native.Length == 1) return StageNativeSong.Load(native[0]);
        var sidecar = Path.Combine(songDirectory, "stage-events.json");
        var file = File.Exists(sidecar) ? ReadEventFile(sidecar) : null;
        if (file?.Version == 1) return EventTimeline(file, sidecar);
        var chart = new StageChartData(new StageTimeline(Array.Empty<StageCue>(), "no venue cues"),
            Array.Empty<StageCue>(), Array.Empty<StageCue>(), Array.Empty<StageCue>());
        if (File.Exists(chartPath))
        {
            CheckSize(chartPath);
            chart = Path.GetExtension(chartPath).ToLowerInvariant() switch
            {
                ".chart" => ParseChart(File.ReadAllText(chartPath)),
                ".mid" or ".midi" => ParseMidi(File.ReadAllBytes(chartPath)),
                _ => chart
            };
        }
        var original = file?.OriginalSongLookup == "off" ? null : ReadOriginal(library, title, artist);
        if (file != null)
            return StageSongDirection.Resolve(file, EventTimeline(file, sidecar), chart, original, performance);
        var timeline = chart.Timeline;
        if (original == null) return timeline;
        var venue = timeline.Cues.Where(c => c.Type is not ("strum" or "beat" or "tempo")).ToArray();
        if (venue.Length == 0) return original;
        static string Channel(StageCue cue) => StageSongDirection.Channel(cue) ?? cue.Type + "/" + cue.Role;
        var overridden = venue.Select(Channel).ToHashSet();
        return new StageTimeline(original.Cues.Where(c => !overridden.Contains(Channel(c))).Concat(venue), original.Source + " + " + timeline.Source)
            { SourceSong = original.SourceSong, Countoff = original.Countoff };
    }

    private static StageTimeline? ReadOriginal(string? library, string title, string artist)
    {
        if (library == null || title.Length == 0 || artist.Length == 0) return null;
        var indexPath = Path.Combine(library, "index.json");
        if (!File.Exists(indexPath)) return null;
        CheckSize(indexPath);
        var index = JsonSerializer.Deserialize<StageEventIndex>(File.ReadAllText(indexPath), JsonOptions);
        if (index?.Version != 1 || index.Songs == null) throw new InvalidDataException("Invalid GH3 cue library index");
        var matches = index.Songs.Where(song => Normalize(song.Title) == Normalize(title) && Normalize(song.Artist) == Normalize(artist)).ToArray();
        if (matches.Length != 1) return null;
        var filename = matches[0].File;
        if (filename.Length == 0 || Path.GetFileName(filename) != filename || !filename.EndsWith(".json", StringComparison.Ordinal))
            throw new InvalidDataException("Invalid GH3 cue library filename");
        var path = Path.Combine(library, filename);
        var file = ReadEventFile(path);
        if (file.Version != 1) throw new InvalidDataException("The original song library requires version 1 event files");
        return EventTimeline(file, path);
    }

    private static string Normalize(string value) => new(value.Normalize(NormalizationForm.FormD)
        .Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private static StageEventFile ReadEventFile(string path)
    {
        CheckSize(path);
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var file = document.RootElement.Deserialize<StageEventFile>(JsonOptions);
            ValidateEventFile(file);
            if (file!.Version == 2) StageSongDirection.Validate(file, document.RootElement);
            return file;
        }
        catch (Exception error) when (error is JsonException or InvalidDataException)
        {
            throw new InvalidDataException($"{path}: {error.Message}", error);
        }
    }

    private static void ValidateEventFile(StageEventFile? file)
    {
        if (file == null || file.Version is not (1 or 2) || !double.IsFinite(file.OffsetSeconds) || file.Events == null ||
            file.SourceSong == null || file.SourceSong.Length > 128 || file.Countoff == null || file.Countoff.Length > 128 ||
            file.Events.Length > 100000 || file.Events.Any(cue => cue == null))
            throw new InvalidDataException("Invalid stage event file");
        var types = new HashSet<string> { "pyro", "sparks", "fireworks", "smoke", "flash", "lighting", "color", "blend", "snapshot", "strobe-toggle", "gh3-note", "camera",
            "gh3-camera", "gh3-script", "gh3-scripts-note", "gh3-triggers-note", "fret", "drum", "vocal", "crowd", "stance", "perform", "face", "clip", "walk", "strum-style", "arms", "strum", "beat", "tempo",
            "stage-diver", "actor-play", "actor-hide", "actor-show", "crowd-lighters", "crowd-animation", "crowd-hand", "gh3-audio" };
        if (file.Events.Any(cue => !types.Contains(cue.Type) || cue.Value == null || cue.Role == null ||
                cue.Type == "gh3-note" && (!int.TryParse(cue.Value, out var note) || note < 0 || note > 127) ||
                cue.Type == "arms" && cue.Value is not ("0" or "1" or "2")))
        {
            throw new InvalidDataException("Invalid stage cue type or value");
        }
    }

    private static StageTimeline EventTimeline(StageEventFile file, string path)
    {
        try { return new(file.Events.Select(cue => cue with { Time = cue.Time + file.OffsetSeconds }), Path.GetFileName(path)) { SourceSong = file.SourceSong, Countoff = file.Countoff }; }
        catch (InvalidDataException error) { throw new InvalidDataException($"{path}: {error.Message}", error); }
    }

    private static void CheckSize(string path)
    {
        if (new FileInfo(path).Length > 32 * 1024 * 1024) throw new InvalidDataException("Stage chart is too large");
    }

    public static StageTimeline ReadChart(string text) => ParseChart(text).Timeline;

    private static StageChartData ParseChart(string text)
    {
        var section = "";
        var resolution = 192;
        var offset = 0.0;
        var tempos = new List<Tempo>();
        var cues = new List<TickCue>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith('[') && line.EndsWith(']')) { section = line[1..^1]; continue; }
            var split = line.IndexOf('=');
            if (split < 0) continue;
            var left = line[..split].Trim();
            var right = line[(split + 1)..].Trim();
            if (section == "Song")
            {
                if (left == "Resolution") resolution = int.Parse(right, CultureInfo.InvariantCulture);
                if (left == "Offset") offset = double.Parse(right, CultureInfo.InvariantCulture);
                continue;
            }
            if (!long.TryParse(left, out var tick) || tick < 0) continue;
            if (section == "SyncTrack" && right.StartsWith("B "))
            {
                var bpm = double.Parse(right[2..], CultureInfo.InvariantCulture);
                tempos.Add(new Tempo(tick, 60000000000 / bpm));
            }
            else if (right.StartsWith("E "))
            {
                var value = right[2..].Trim();
                if (value.StartsWith('"') && value.EndsWith('"') && value.Length >= 2) value = value[1..^1];
                if (section == "Events" || DirectionTrack(section).Length > 0 || Gh3Text(value)) cues.Add(new TickCue(tick, value));
            }
            else if (DirectionTrack(section) is { Length: > 0 } direction && right.StartsWith("N "))
            {
                var fields = right.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (fields.Length != 3 || !int.TryParse(fields[1], out var note) || note is < 0 or > 127 ||
                    !long.TryParse(fields[2], out var length) || length < 0)
                    throw new InvalidDataException($"Invalid {section} direction note at tick {tick}");
                cues.Add(new TickCue(tick, "", note, direction, Length: length));
            }
            else if (section is "ExpertSingle" or "ExpertDoubleBass" or "ExpertDoubleRhythm" && right.StartsWith("N "))
            {
                var fields = right.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (fields.Length == 3 && int.TryParse(fields[1], out var lane) && lane is >= 0 and < 5 &&
                    long.TryParse(fields[2], out var length) && length >= 0)
                    cues.Add(new TickCue(tick, "", 1 << lane, "strum", section == "ExpertSingle" ? "guitarist" : "bassist", length));
            }
        }
        return ConvertTicks(cues, tempos, resolution, offset, "notes.chart");
    }

    public static StageTimeline ReadMidi(byte[] data) => ParseMidi(data).Timeline;

    private static string DirectionTrack(string name)
    {
        name = name.Trim().ToUpperInvariant();
        if (name == "GH3 VENUE") return "gh3-note";
        if (name.StartsWith("GH3 ", StringComparison.Ordinal)) name = name[4..];
        return name switch
        {
            "LIGHTSHOW" => "gh3-note",
            "CAMERAS" => "gh3-camera",
            "ANIMS" or "ANIM" => "fret",
            "DRUMS" => "drum",
            "PERFORMANCE" => "vocal",
            "CROWD" => "crowd",
            "SCRIPTS" => "gh3-scripts-note",
            "TRIGGERS" => "gh3-triggers-note",
            _ => ""
        };
    }

    private static StageChartData ParseMidi(byte[] data)
    {
        var cursor = new MidiCursor(data, 0, data.Length);
        if (cursor.Ascii(4) != "MThd") throw new InvalidDataException("Missing MIDI header");
        var headerSize = cursor.UInt32();
        if (headerSize < 6 || headerSize > data.Length - 8) throw new InvalidDataException("Invalid MIDI header size");
        var format = cursor.UInt16();
        var trackCount = cursor.UInt16();
        var resolution = cursor.UInt16();
        if (format > 1 || (resolution & 0x8000) != 0) throw new InvalidDataException("The venue reader requires MIDI format 0/1 with PPQN timing");
        cursor.Skip((int)headerSize - 6);
        var tempos = new List<Tempo>();
        var cues = new List<TickCue>();
        for (var track = 0; track < trackCount; track++)
        {
            if (cursor.Ascii(4) != "MTrk") throw new InvalidDataException("Missing MIDI track");
            var size = cursor.UInt32();
            if (size > int.MaxValue) throw new InvalidDataException("Invalid MIDI track size");
            var reader = cursor.Chunk((int)size);
            var trackName = "";
            var trackCues = new List<TickCue>();
            var active = new Dictionary<int, Queue<int>>();
            long tick = 0;
            byte running = 0;
            while (!reader.End)
            {
                tick = checked(tick + reader.Variable());
                var status = reader.Peek();
                if ((status & 0x80) != 0) reader.Byte();
                else
                {
                    if (running == 0) throw new InvalidDataException("MIDI running status has no status byte");
                    status = running;
                }
                if (status == 0xFF)
                {
                    running = 0;
                    var type = reader.Byte();
                    var payload = reader.Bytes(reader.Variable());
                    if (type == 0x03) trackName = Encoding.UTF8.GetString(payload).Trim().ToUpperInvariant();
                    else if (type is 0x01 or 0x06) trackCues.Add(new TickCue(tick, Encoding.UTF8.GetString(payload)));
                    else if (type == 0x51)
                    {
                        if (payload.Length != 3) throw new InvalidDataException("Invalid MIDI tempo");
                        tempos.Add(new Tempo(tick, payload[0] * 65536 + payload[1] * 256 + payload[2]));
                    }
                    else if (type == 0x2F) break;
                }
                else if (status is 0xF0 or 0xF7)
                {
                    running = 0;
                    reader.Skip(reader.Variable());
                }
                else if (status >= 0x80 && status < 0xF0)
                {
                    running = status;
                    var first = reader.Byte();
                    var second = (status & 0xF0) is 0xC0 or 0xD0 ? 0 : reader.Byte();
                    if (first > 127 || second > 127) throw new InvalidDataException("Invalid MIDI data byte");
                    var noteKey = (status & 15) * 128 + first;
                    if ((status & 0xF0) == 0x90 && second > 0)
                    {
                        if (!active.TryGetValue(noteKey, out var starts)) active[noteKey] = starts = new();
                        starts.Enqueue(trackCues.Count);
                        trackCues.Add(new TickCue(tick, "", first, Velocity: second));
                    }
                    else if ((status & 0xF0) == 0x80 || (status & 0xF0) == 0x90 && second == 0)
                    {
                        if (active.TryGetValue(noteKey, out var starts) && starts.Count > 0)
                        {
                            var start = starts.Dequeue();
                            trackCues[start] = trackCues[start] with { Length = tick - trackCues[start].Tick };
                        }
                    }
                }
                else throw new InvalidDataException("Unsupported MIDI status");
            }
            if (trackName is "VENUE" or "EVENTS") cues.AddRange(trackCues.Where(cue => cue.Note < 0));
            var direction = DirectionTrack(trackName);
            if (direction.Length > 0) cues.AddRange(trackCues.Select(c => c with { Type = direction }));
            else if (trackName is not ("VENUE" or "EVENTS")) cues.AddRange(trackCues.Where(c => c.Note < 0 && Gh3Text(c.Text)));
            if (trackName is "PART GUITAR" or "PART BASS" or "PART RHYTHM")
                cues.AddRange(trackCues.Where(c => c.Note is >= 96 and <= 100).Select(c => c with
                {
                    Type = "strum", Role = trackName == "PART GUITAR" ? "guitarist" : "bassist", Note = 1 << (c.Note - 96)
                }));
        }
        return ConvertTicks(cues, tempos, resolution, 0, "notes.mid");
    }

    private static bool Gh3Text(string text) => text.Trim().TrimStart('[').TrimStart().StartsWith("gh3 ", StringComparison.OrdinalIgnoreCase);

    private static StageChartData ConvertTicks(List<TickCue> cues, List<Tempo> tempos, int resolution, double offset, string source)
    {
        if (resolution <= 0 || !double.IsFinite(offset) || tempos.Any(t => !double.IsFinite(t.Microseconds) || t.Microseconds <= 0))
            throw new InvalidDataException("Invalid chart timing");
        var changes = tempos.OrderBy(t => t.Tick).ToArray();
        var index = 0;
        long lastTick = 0;
        var seconds = offset;
        var rate = 500000.0;
        var output = new List<StageCue>();
        var noteStrums = new List<StageCue>();
        double Seconds(long tick)
        {
            long previous = 0;
            var elapsed = offset;
            var tempo = 500000.0;
            foreach (var change in changes)
            {
                if (change.Tick > tick) break;
                elapsed += (change.Tick - previous) * tempo / (resolution * 1000000.0);
                previous = change.Tick;
                tempo = change.Microseconds;
            }
            return elapsed + (tick - previous) * tempo / (resolution * 1000000.0);
        }
        var timing = changes.Select(tempo => new StageCue(Seconds(tempo.Tick), "tempo",
            (60000000 / tempo.Microseconds).ToString(CultureInfo.InvariantCulture))).ToArray();
        foreach (var cue in cues.OrderBy(c => c.Tick))
        {
            while (index < changes.Length && changes[index].Tick <= cue.Tick)
            {
                var change = changes[index++];
                seconds += (change.Tick - lastTick) * rate / (resolution * 1000000.0);
                lastTick = change.Tick;
                rate = change.Microseconds;
            }
            var time = seconds + (cue.Tick - lastTick) * rate / (resolution * 1000000.0);
            var converted = cue.Type.Length > 0 && cue.Note >= 0 ? new StageCue(time, cue.Type, cue.Note.ToString(CultureInfo.InvariantCulture),
                Seconds(checked(cue.Tick + cue.Length)) - time, cue.Role, Velocity: cue.Velocity) :
                cue.Note >= 0 ? StageTimeline.FromGh3Note(time, cue.Note) : StageTimeline.FromText(time, cue.Text);
            if (converted != null) (cue.Type == "strum" ? noteStrums : output).Add(converted);
        }
        var chords = noteStrums.GroupBy(c => (c.Time, c.Role)).Select(g =>
            new StageCue(g.Key.Time, "strum", g.Aggregate(0, (mask, c) => mask | int.Parse(c.Value)).ToString(), g.Max(c => c.Duration), g.Key.Role)).ToArray();
        var timeline = new StageTimeline(timing.Concat(output).Concat(chords), source);
        if (changes.Length > 0 && changes[0].Tick > 0)
            timing = new[] { new StageCue(offset, "tempo", "120") }.Concat(timing).ToArray();
        return new(timeline, output.ToArray(), chords, timing);
    }

    private sealed class MidiCursor
    {
        private readonly byte[] _data;
        private readonly int _end;
        private int _position;
        public MidiCursor(byte[] data, int start, int end) { _data = data; _position = start; _end = end; }
        public bool End => _position >= _end;
        public byte Peek() => !End ? _data[_position] : throw new InvalidDataException("Truncated MIDI");
        public byte Byte() { var result = Peek(); _position++; return result; }
        public void Skip(int count)
        {
            if (count < 0 || count > _end - _position) throw new InvalidDataException("Truncated MIDI");
            _position += count;
        }
        public byte[] Bytes(int count) { var start = _position; Skip(count); return _data[start.._position]; }
        public string Ascii(int count) => Encoding.ASCII.GetString(Bytes(count));
        public ushort UInt16() => BinaryPrimitives.ReadUInt16BigEndian(Bytes(2));
        public uint UInt32() => BinaryPrimitives.ReadUInt32BigEndian(Bytes(4));
        public MidiCursor Chunk(int count) { var start = _position; Skip(count); return new(_data, start, _position); }
        public int Variable()
        {
            var result = 0;
            for (var i = 0; i < 4; i++)
            {
                var value = Byte();
                result = (result << 7) | (value & 127);
                if (value < 128) return result;
            }
            throw new InvalidDataException("Invalid MIDI variable length value");
        }
    }
}
