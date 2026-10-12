using System.Runtime.InteropServices;

namespace Gh3Skin;

// Plays the stage sound voices (crowd, count-off, venue sounds) through BASS on the song clock.

internal sealed class StageAudio : IDisposable
{    // The RIFF size counts the 36 header bytes after it ("WAVE", the 24-byte fmt chunk, the data chunk header).
    private const int WaveHeaderAfterRiffSize = 36;
    // A voice more than 50 ms off the song clock is moved back to its place.
    private const double MaximumDrift = .05;
    // The count-off of a song whose direction file names none (the GH3 count-off of most songs, songlist.qb).
    private const string DefaultCountoff = "sticks_normal";

    private sealed record Wave(IntPtr Data, int Length);
    private readonly string _directory;
    private readonly StageSoundBank _bank;
    private readonly StageAudioState _state;
    private readonly Dictionary<uint, Wave> _clips = new();
    private readonly Dictionary<long, uint> _sources = new();
    private readonly HashSet<string> _reported = new();
    private int _revision;
    private bool _paused;

    private StageAudio(string directory, StageSoundBank bank, StageAudioState state)
    {
        _directory = directory;
        _bank = bank;
        _state = state;
    }

    public static StageAudio? Load(string directory, StageTimeline timeline, string zone, bool training = false)
    {
        var path = Path.Combine(directory, "audio.json");
        if (!File.Exists(path)) return null;
        var bank = StageSoundBank.Load(path);
        var countoff = timeline.Countoff.Length > 0 ? timeline.Countoff : DefaultCountoff;
        return new(directory, bank, new StageAudioState(bank, timeline, zone, countoff) { Training = training });
    }

    public void Update(double time, float rock, bool paused)
    {
        _state.Update(time, rock);
        if (_state.Revision != _revision)
        {
            Clear();
            _revision = _state.Revision;
        }
        var active = _state.Voices.Where(voice => voice.Start <= time && voice.End > time).ToDictionary(voice => voice.Id);
        foreach (var id in _sources.Keys.Where(id => !active.ContainsKey(id)).ToArray())
        {
            StageAudioBackend.Free(_sources[id]);
            _sources.Remove(id);
        }
        foreach (var voice in active.Values)
        {
            var data = _bank.Sample(voice.Sample)!;
            var sample = (time - voice.Start) * voice.Pitch * data.Rate;
            if (voice.Loop && sample >= data.LoopEnd) sample = data.LoopStart + (sample - data.LoopStart) % (data.LoopEnd - data.LoopStart);
            var frame = (int)Math.Clamp(sample, 0, data.Frames - 1);
            if (!_sources.TryGetValue(voice.Id, out var source))
            {
                var clip = Clip(voice.Sample);
                source = StageAudioBackend.Create(clip.Data, clip.Length, voice.Loop);
                _sources.Add(voice.Id, source);
                StageAudioBackend.Pitch(source, data.Rate * voice.Pitch);
                StageAudioBackend.Pan(source, voice.Pan);
                StageAudioBackend.Volume(source, _state.Gain(voice, time));
                StageAudioBackend.Frame(source, data.Channels, frame);
                if (!paused) StageAudioBackend.Play(source);
            }
            else
            {
                if (paused && !_paused) StageAudioBackend.Pause(source);
                else if (!paused && _paused) StageAudioBackend.Play(source);
                var position = StageAudioBackend.Frame(source, data.Channels);
                if (Math.Abs(position - frame) > data.Rate * MaximumDrift || voice.Loop && position >= data.LoopEnd)
                    StageAudioBackend.Frame(source, data.Channels, frame);
                StageAudioBackend.Volume(source, _state.Gain(voice, time));
            }
        }
        _paused = paused;
        foreach (var diagnostic in _state.Diagnostics)
            if (_reported.Add(diagnostic)) MelonLoader.MelonLogger.Warning("GH3 stage audio: " + diagnostic);
    }

    public string Voices => $"{_sources.Values.Count(source => StageAudioBackend.State(source) == 1)}/{_sources.Count}";

    private Wave Clip(uint key)
    {
        if (_clips.TryGetValue(key, out var cached)) return cached;
        var sample = _bank.Sample(key)!;
        using var reader = new BinaryReader(File.OpenRead(Path.Combine(_directory, key.ToString("x8") + ".pcm")));
        var size = checked(sample.Frames * sample.Channels * 2);
        var pcm = reader.ReadBytes(size);
        if (pcm.Length != size || reader.BaseStream.ReadByte() != -1) throw new InvalidDataException("Invalid stage sound sample length");
        using var buffer = new MemoryStream();
        using var writer = new BinaryWriter(buffer);
        writer.Write(System.Text.Encoding.ASCII.GetBytes("RIFF")); writer.Write(WaveHeaderAfterRiffSize + size); writer.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt "));
        writer.Write(16); writer.Write((short)1); writer.Write((short)sample.Channels);
        writer.Write(sample.Rate); writer.Write(sample.Rate * sample.Channels * 2);
        writer.Write((short)(sample.Channels * 2)); writer.Write((short)16);
        writer.Write(System.Text.Encoding.ASCII.GetBytes("data")); writer.Write(size); writer.Write(pcm);
        var bytes = buffer.ToArray();
        var memory = Marshal.AllocHGlobal(bytes.Length);
        Marshal.Copy(bytes, 0, memory, bytes.Length);
        var wave = new Wave(memory, bytes.Length);
        _clips.Add(key, wave);
        return wave;
    }

    private void Clear()
    {
        foreach (var source in _sources.Values) StageAudioBackend.Release(source);
        _sources.Clear();
    }

    public void Dispose()
    {
        Clear();
        foreach (var clip in _clips.Values) Marshal.FreeHGlobal(clip.Data);
        _clips.Clear();
    }
}
