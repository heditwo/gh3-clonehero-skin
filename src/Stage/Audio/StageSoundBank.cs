using System.Text.Json;

namespace Gh3Skin;

// The venue sound bank (samples and sound definitions) and the sound event choice rules.

internal sealed class StageSoundSample
{
    public int Rate { get; set; }
    public int Channels { get; set; }
    public int Frames { get; set; }
    public int LoopStart { get; set; }
    public int LoopEnd { get; set; }
    public bool Loop { get; set; }
    public double Duration => (double)Frames / Rate;
}

internal sealed class StageSoundBank
{    private const int MaximumEntries = 10000;
    // Sample rates 8-192 kHz; at most five minutes at 192 kHz.
    private const int MinimumRate = 8000, MaximumRate = 192000;
    private const int MaximumFrames = 57600000;

    public int Version { get; set; }
    public Dictionary<string, JsonElement> Definitions { get; set; } = new();
    public Dictionary<string, StageSoundSample> Samples { get; set; } = new();
    public Dictionary<string, double> Constants { get; set; } = new();
    private readonly Dictionary<uint, Gh3QbValue> _values = new();
    private readonly Dictionary<uint, uint> _parents = new();
    public IReadOnlyDictionary<uint, Gh3QbValue> RuntimeValues => _values;

    public static StageSoundBank Load(string path)
    {
        var bank = JsonSerializer.Deserialize<StageSoundBank>(File.ReadAllText(path), Packages.PackageJson.CaseInsensitive);
        if (bank?.Version != 1 || bank.Samples.Count > MaximumEntries || bank.Definitions.Count > MaximumEntries ||
            bank.Samples.Any(pair => pair.Key.Length != 8 || !pair.Key.All(Uri.IsHexDigit) || pair.Value is not { Rate: >= MinimumRate and <= MaximumRate, Channels: 1 or 2, Frames: > 0 and <= MaximumFrames } ||
                pair.Value.LoopStart < 0 || pair.Value.LoopEnd <= pair.Value.LoopStart || pair.Value.LoopEnd > pair.Value.Frames))
            throw new InvalidDataException("Invalid stage sound library");
        foreach (var pair in bank.Definitions) bank._values.Add(Convert.ToUInt32(pair.Key, 16), Gh3QbText.Decode(pair.Value.GetRawText()));
        void Parents(Gh3QbValue value, uint parent)
        {
            foreach (var field in value.Fields)
                if (field.Value.Type == Gh3QbType.Structure)
                {
                    bank._parents.Add(field.Name, parent);
                    Parents(field.Value, field.Name);
                }
                else if (field.Name == Gh3Checksum.Value("LeafNodes"))
                    foreach (var leaf in field.Value.Items) bank._parents.Add(leaf.Integer, parent);
        }
        if (bank.Value("BussTree") is { } tree) Parents(tree, 0);
        return bank;
    }

    public Gh3QbValue? Value(string name) => Value(Gh3Checksum.Value(name));
    public Gh3QbValue? Value(uint key) => _values.GetValueOrDefault(key);
    public StageSoundSample? Sample(uint key) => Samples.GetValueOrDefault(key.ToString("x8"));
    public IEnumerable<uint> Busses(uint key)
    {
        for (var depth = 0; key != 0 && depth < 32; depth++)
        {
            yield return key;
            key = _parents.GetValueOrDefault(key);
        }
    }
    public static double Number(Gh3QbValue? value, double fallback) => value == null ? fallback : value.Type == Gh3QbType.Integer ? unchecked((int)value.Integer) : value.Number;
}

internal sealed class StageSoundVoice
{
    public long Id { get; init; }
    public long Instance { get; init; }
    public uint Event { get; init; }
    public uint Sample { get; init; }
    public uint Buss { get; init; }
    public double Start { get; init; }
    public double End { get; set; }
    public float Gain { get; set; }
    public float Pitch { get; init; } = 1;
    public float Pan { get; init; }
    public bool Loop { get; init; }
}

internal sealed class StageSoundEvents
{
    private readonly StageSoundBank _bank;
    private readonly List<StageSoundVoice> _voices = new();
    private readonly Dictionary<uint, double> _last = new();
    private readonly Dictionary<uint, Queue<int>> _choices = new();
    private long _voice;
    private long _instance;
    private uint _seed = Gh3ScriptRuntime.ScriptRandomSeed;
    public IReadOnlyList<StageSoundVoice> Voices => _voices;

    public StageSoundEvents(StageSoundBank bank) => _bank = bank;

    public void Play(uint key, double time, float gain = 1, uint? buss = null, bool loop = false, Gh3QbValue? options = null)
    {
        var settings = _bank.Value(Gh3Checksum.Append(key, "_struct"));
        if (_last.TryGetValue(key, out var last) && time - last < StageSoundBank.Number(settings?.Field("NoRepeatFor"), 0)) return;
        var instances = _voices.Where(voice => voice.Event == key && voice.End > time).GroupBy(voice => voice.Instance).OrderBy(group => group.Key).ToArray();
        var limit = StageSoundBank.Number(settings?.Field("InstanceLimit"), 64);
        if (instances.Length >= limit)
        {
            if (settings?.Field("InstanceManagement")?.Integer == Gh3Checksum.Value("ignore")) return;
            foreach (var voice in instances[0]) voice.End = time;
        }
        _last[key] = time;
        var instance = _instance++;
        var bus = buss ?? settings?.Field("Default_Buss")?.Integer ?? Gh3Checksum.Value("Default");
        void Voice(uint sample, Gh3QbValue? parameters)
        {
            var data = _bank.Sample(sample) ?? throw new InvalidDataException($"Missing stage sound sample {sample:x8}");
            // GH3: pitch and volume are percentages; their random draws use the sample checksum xor 0x100 and 0x200.
            var pitch = (float)RandomNumber(parameters?.Field("RandomPitch"), sample ^ 0x100, StageSoundBank.Number(parameters?.Field("pitch"), 100)) / 100;
            // GH3: see above.
            var volume = (float)RandomNumber(parameters?.Field("RandomVol"), sample ^ 0x200, StageSoundBank.Number(parameters?.Field("vol"), 100)) / 100;
            var loops = StageSoundBank.Number(parameters?.Field("num_loops"), 0);
            var looping = loop || data.Loop || loops < 0;
            if (!float.IsFinite(pitch) || pitch <= 0 || !float.IsFinite(volume) || volume < 0) throw new InvalidDataException("Invalid stage sound pitch or volume");
            _voices.Add(new() { Id = _voice++, Instance = instance, Event = key, Sample = sample, Buss = bus, Start = time,
                End = looping ? double.PositiveInfinity : time + data.Duration * Math.Max(1, loops + 1) / pitch,
                Gain = volume * gain, Pitch = pitch, Pan = (float)Math.Clamp(StageSoundBank.Number(parameters?.Field("pan1x"), 0), -1, 1), Loop = looping || loops > 0 });
        }
        if (_bank.Sample(key) != null) { Voice(key, options); return; }
        var container = _bank.Value(Gh3Checksum.Append(key, "_container")) ?? throw new InvalidDataException($"Missing stage sound event {key:x8}");
        var sounds = container.Field("Sounds")?.Fields.Select(field => field.Value).Where(value => value.Type == Gh3QbType.Structure).ToArray() ?? Array.Empty<Gh3QbValue>();
        if (sounds.Length == 0) return;
        var mode = container.Field("Randomness")?.Integer ?? Gh3Checksum.Value("None");
        var selected = mode == Gh3Checksum.Value("None") ? sounds : new[] { sounds[Choose(key, sounds.Length, mode)] };
        foreach (var sound in selected)
            foreach (var field in sound.Fields.Where(field => field.Name == 0 && field.Value.Type == Gh3QbType.Checksum)) Voice(field.Value.Integer, sound);
    }

    private double RandomNumber(Gh3QbValue? value, uint key, double fallback)
    {
        var values = value?.Field("vals")?.Items;
        return values == null || values.Count == 0 ? fallback : StageSoundBank.Number(values[Choose(key, values.Count, value!.Field("Type")?.Integer ?? 0)], fallback);
    }

    private int Choose(uint key, int count, uint mode)
    {
        var history = _choices.GetValueOrDefault(key);
        if (history == null) _choices.Add(key, history = new());
        var avoid = mode == Gh3Checksum.Value("RandomNoRepeatLastTwoType") ? 2 : mode == Gh3Checksum.Value("RandomNoRepeatType") ? 1 : 0;
        var candidates = Enumerable.Range(0, count).Where(index => !history.TakeLast(Math.Min(avoid, count - 1)).Contains(index)).ToArray();
        XorShift32.Next(ref _seed);
        var choice = candidates[_seed % candidates.Length];
        history.Enqueue(choice);
        while (history.Count > 2) history.Dequeue();
        return choice;
    }

    public void Stop(uint key, double time)
    {
        foreach (var voice in _voices.Where(voice => voice.Event == key && voice.Start <= time && voice.End > time)) voice.End = time;
    }

    public void Prune(double time) => _voices.RemoveAll(voice => voice.End < time);
    public bool Playing(uint key, double time) => _voices.Any(voice => voice.Event == key && voice.Start <= time && voice.End > time);
    public void Volume(uint key, double time, float gain)
    {
        foreach (var voice in _voices.Where(voice => voice.Event == key && voice.Start <= time && voice.End > time)) voice.Gain = gain;
    }
}
