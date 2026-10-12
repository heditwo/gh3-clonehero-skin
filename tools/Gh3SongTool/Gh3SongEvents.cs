using System.Globalization;

namespace Gh3Skin;

// The GH3 direction of a song PAK as stage cues, for the original song library (pipeline.events).

internal static class Gh3SongEvents
{
    public static StageCue[] Read(Gh3Song song)
    {
        var cues = new List<StageCue>();
        foreach (var (section, type) in new[] { ("lightshow", "gh3-note"), ("cameras", "gh3-camera"),
            ("anim", "fret"), ("drums", "drum"), ("performance", "vocal"), ("crowd", "crowd"),
            ("scripts", "gh3-scripts-note"), ("triggers", "gh3-triggers-note") })
            foreach (var note in song.Notes(section))
                cues.Add(new(note.Time / 1000.0, type, note.Note.ToString(CultureInfo.InvariantCulture), note.Length / 1000.0,
                    Velocity: note.Velocity is { } velocity ? (int)velocity : null));
        foreach (var call in song.ScriptEvents()) cues.Add(new(call.Time / 1000.0, "gh3-script", call.Encode()));
        foreach (var (role, section) in new[] { ("guitarist", "song_Expert"), ("bassist", "song_rhythm_Expert") })
        {
            var notes = song.Array(section);
            if (notes.Count % 3 != 0) throw new InvalidDataException("Invalid instrument note array.");
            for (var i = 0; i < notes.Count; i += 3)
                cues.Add(new(notes[i].Integer / 1000.0, "strum", (notes[i + 2].Integer & Gh3TrackNote.LaneMask).ToString(CultureInfo.InvariantCulture),
                    notes[i + 1].Integer / 1000.0, role));
        }
        foreach (var beat in song.Array("fretbars")) cues.Add(new(beat.Integer / 1000.0, "beat"));
        return cues.OrderBy(cue => cue.Time).ToArray();
    }
}
