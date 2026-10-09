namespace Gh3Skin;

internal sealed record StageMoviePlayback(string Name, StageMovieClip Clip, double Start, double Rate, int First, int LoopStart, int LoopEnd, bool Loop, bool Hold)
{
    public int Frame(double time)
    {
        var frame = First + (long)Math.Floor(Math.Max(0, time - Start) * Rate + 1e-9);
        if (Loop && frame > LoopEnd) frame = LoopStart + (frame - LoopEnd - 1) % (LoopEnd - LoopStart + 1);
        return frame >= Clip.Frames ? Hold ? Clip.Frames - 1 : -1 : (int)frame;
    }
}

internal sealed class StageMovieState : IDisposable
{
    private readonly StageScriptBank _bank;
    private readonly Dictionary<string, StageMovieClip> _clips = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, StageMoviePlayback> _slots = new();
    private readonly Action<string> _gap;
    public IReadOnlyDictionary<int, StageMoviePlayback> Slots => _slots;

    public StageMovieState(StageScriptBank bank, Action<string> gap) { _bank = bank; _gap = gap; }

    public Gh3ScriptCallResult? Execute(uint command, Gh3QbValue parameters, Gh3ScriptRuntime runtime)
    {
        bool Is(string name) => command == Gh3Checksum.Value(name);
        if (!Is("PlayMovie") && !Is("KillMovie")) return null;
        if (parameters.Field("textureSlot") is not { } slotValue) return new(false);
        var slot = checked((int)Gh3ScriptRuntime.Numeric(slotValue));
        if (slot is < 0 or > 31) throw new InvalidDataException("Invalid movie texture slot");
        if (Is("KillMovie")) { _slots.Remove(slot); return new(true); }
        var name = parameters.Field("movie")?.Data as string ?? throw new InvalidDataException("Movie name is missing");
        if (!_clips.TryGetValue(name, out var clip))
        {
            if (!_bank.MoviePaths.TryGetValue(name, out var path)) throw new InvalidDataException("Stage movie is missing: " + name);
            _clips.Add(name, clip = new(path));
        }
        int Frame(string field, int fallback)
        {
            var value = parameters.Field(field) is { } item ? checked((int)Gh3ScriptRuntime.Numeric(item)) : fallback;
            if (value == -1) value = fallback;
            if (value < 1 || value > clip.Frames) throw new InvalidDataException("Movie frame is outside its clip");
            return value - 1;
        }
        var first = Frame("start_frame", 1); var loopStart = Frame("loop_start", 1); var loopEnd = Frame("loop_end", clip.Frames);
        if (loopEnd < loopStart) throw new InvalidDataException("Movie loop is reversed");
        var rate = parameters.Field("frameRate") is { } fps ? Gh3ScriptRuntime.Numeric(fps) : clip.FrameRate;
        if (rate == -1) rate = clip.FrameRate;
        if (!double.IsFinite(rate) || rate <= 0 || rate > 240) throw new InvalidDataException("Invalid movie frame rate");
        var playback = new StageMoviePlayback(name, clip, runtime.Time, rate, first, loopStart, loopEnd, !parameters.Flag("no_looping"), !parameters.Flag("no_hold"));
        _slots[slot] = playback;
        foreach (var field in parameters.Fields)
            if (field.Name != 0 && field.Name != Gh3Checksum.Value("movie") && field.Name != Gh3Checksum.Value("textureSlot") && field.Name != Gh3Checksum.Value("texturePri") &&
                field.Name != Gh3Checksum.Value("viewport") && field.Name != Gh3Checksum.Value("frameRate") && field.Name != Gh3Checksum.Value("start_frame") &&
                field.Name != Gh3Checksum.Value("loop_start") && field.Name != Gh3Checksum.Value("loop_end"))
                _gap("Movie parameter playback is incomplete: " + Gh3QbText.Key(field.Name));
        return new(true, Waiting: parameters.Flag("noWait") ? null : () => _slots.TryGetValue(slot, out var current) && ReferenceEquals(current, playback) &&
            (playback.Loop || runtime.Time < playback.Start + (clip.Frames - first) / rate));
    }

    public void Dispose() { foreach (var clip in _clips.Values) clip.Dispose(); _clips.Clear(); _slots.Clear(); }
}
