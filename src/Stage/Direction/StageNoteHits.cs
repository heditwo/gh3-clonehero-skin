using Il2Cpp;

namespace Gh3Skin;

// Finds the notes that the player hit and raises GH3's hitnote_<lane> stage events.

internal sealed class StageNoteHits
{
    private ObjectPublicObInObDoSiDoUIInBoInUnique[] _notes = Array.Empty<ObjectPublicObInObDoSiDoUIInBoInUnique>();
    private bool[] _seen = Array.Empty<bool>();
    private IntPtr _engine;
    private int _first;
    private double _time = double.NaN;
    private static readonly uint[] Events = new[] { "hitnote_green", "hitnote_red", "hitnote_yellow", "hitnote_blue", "hitnote_orange" }.Select(Gh3Checksum.Value).ToArray();

    public void Reset()
    {
        _notes = Array.Empty<ObjectPublicObInObDoSiDoUIInBoInUnique>();
        _seen = Array.Empty<bool>();
        _engine = IntPtr.Zero;
        _time = double.NaN;
        _first = 0;
    }

    public uint[] Update(BasePlayer player, double time)
    {
        if (player.engine == null || player.TryCast<GuitarPlayer>() == null) return Array.Empty<uint>();
        var notes = CloneHeroBindings.Notes(player.engine);
        if (notes == null) return Array.Empty<uint>();
        if (_engine != player.engine.Pointer || _notes.Length != notes.Count)
        {
            Reset();
            _engine = player.engine.Pointer;
            _notes = Enumerable.Range(0, notes.Count).Select(index => notes[index]).ToArray();
            _seen = new bool[_notes.Length];
        }
        if (time < _time)
        {
            Array.Clear(_seen, 0, _seen.Length);
            _first = 0;
            while (_first < _notes.Length && _notes[_first].field_Public_Double_0 < time - 1) _first++;
        }
        var events = new List<uint>();
        for (var index = _first; index < _notes.Length; index++)
        {
            var note = _notes[index];
            if (note.field_Public_Double_0 > time + 1) break;
            var flags = note.field_Public_EnumNPublicSealedvaNoHiMiDyIsWa8vWaUnique_0;
            var hit = (flags & ObjectPublicObInObDoSiDoUIInBoInUnique.EnumNPublicSealedvaNoHiMiDyIsWa8vWaUnique.Hit) != 0;
            if (hit && !_seen[index] && time >= _time)
            {
                var mask = CloneHeroBindings.NoteLanes(note);
                for (var lane = 0; lane < Events.Length; lane++)
                    if ((mask & (2 << lane)) != 0) { events.Add(Events[lane]); BindingTrace.StageNoteEvents++; }
            }
            _seen[index] = hit;
            if (index == _first && (hit || (flags & ObjectPublicObInObDoSiDoUIInBoInUnique.EnumNPublicSealedvaNoHiMiDyIsWa8vWaUnique.Missed) != 0 || note.field_Public_Double_0 < time - 1)) _first++;
        }
        _time = time;
        return events.ToArray();
    }
}
