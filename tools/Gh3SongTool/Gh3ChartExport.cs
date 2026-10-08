using System.Globalization;
using System.Text;
using Gh3Skin;

// Writes a Clone Hero .chart from a native GH3 song: one beat per fretbar, notes, sustains, forced flips, and star power.
internal static class Gh3ChartExport
{
    private const int Resolution = 192;

    public static string Write(Gh3Song song, string difficulty)
    {
        var bars = song.Array("fretbars").Select(value => (double)value.Integer).ToArray();
        if (bars.Length < 2) throw new InvalidDataException("The song has no fretbars.");
        double Tick(double time)
        {
            var i = Array.BinarySearch(bars, time);
            if (i < 0) i = Math.Clamp(~i - 1, 0, bars.Length - 2);
            i = Math.Min(i, bars.Length - 2);
            return (i + (time - bars[i]) / (bars[i + 1] - bars[i])) * Resolution;
        }
        double BeatLength(double time)
        {
            var i = Math.Clamp(Array.BinarySearch(bars, time) is var found && found < 0 ? ~found - 1 : found, 0, bars.Length - 2);
            return bars[i + 1] - bars[i];
        }
        var text = new StringBuilder();
        text.AppendLine("[Song]\n{\n  Name = \"" + song.Id + "\"\n  Offset = 0\n  Resolution = " + Resolution + "\n}");
        text.AppendLine("[SyncTrack]\n{");
        var events = new List<(long Tick, string Text)>();
        if (bars[0] > 0)
        {
            // Lead-in before the first fretbar keeps the first beat's tempo, starting at song time 0.
            events.Add((0, $"B {Math.Round(60000000.0 / (bars[1] - bars[0]))}"));
        }
        foreach (var row in song.Array("timesig"))
        {
            var values = row.Items.Select(item => item.Integer).ToArray();
            events.Add(((long)Math.Round(Tick(values[0]) + Offset(bars)), $"TS {values[1]}"));
        }
        for (var i = 0; i < bars.Length - 1; i++)
            events.Add(((long)Math.Round(i * Resolution + Offset(bars)), $"B {Math.Round(60000000.0 / (bars[i + 1] - bars[i]))}"));
        foreach (var (tick, value) in events.OrderBy(item => item.Tick).ThenBy(item => item.Text.StartsWith("TS") ? 0 : 1))
            text.AppendLine($"  {tick} = {value}");
        text.AppendLine("}\n[Events]\n{\n}");
        var section = char.ToUpperInvariant(difficulty[0]) + difficulty[1..] + "Single";
        text.AppendLine($"[{section}]\n{{");
        var lines = new List<(long Tick, string Text)>();
        var notes = song.Array("song_" + difficulty).Select(value => value.Integer).ToArray();
        if (notes.Length == 0 || notes.Length % 3 != 0) throw new InvalidDataException($"The song has no {difficulty} notes.");
        for (var i = 0; i < notes.Length; i += 3)
        {
            var (time, length, mask) = (notes[i], notes[i + 1], notes[i + 2]);
            var tick = (long)Math.Round(Tick(time) + Offset(bars));
            var sustain = length >= BeatLength(time) / 2 ? (long)Math.Round(Tick(time + length) - Tick(time)) : 0;
            for (var fret = 0; fret < 5; fret++)
                if ((mask & (1u << fret)) != 0) lines.Add((tick, $"N {fret} {sustain}"));
            if ((mask & 0x20) != 0) lines.Add((tick, "N 5 0"));
        }
        foreach (var row in song.Array(difficulty + "_star"))
        {
            var values = row.Items.Select(item => item.Integer).ToArray();
            var start = (long)Math.Round(Tick(values[0]) + Offset(bars));
            lines.Add((start, $"S 2 {(long)Math.Round(Tick(values[0] + values[1]) - Tick(values[0]))}"));
        }
        foreach (var (tick, value) in lines.OrderBy(item => item.Tick))
            text.AppendLine($"  {tick} = {value}");
        text.AppendLine("}");
        return text.ToString();
    }

    // Shifts ticks so that song time 0 is tick 0 when the first fretbar is later than 0.
    private static double Offset(double[] bars) => bars[0] > 0 ? bars[0] / (bars[1] - bars[0]) * Resolution : 0;
}
