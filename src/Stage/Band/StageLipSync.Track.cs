namespace Gh3Skin;

// The viseme track of the lip sync: sung notes from the chart, phonemes for each note, and keys (a viseme over a time
// interval). The onset consonants of a syllable come just before its note, the vowel holds for the note (the first
// vowel of a diphthong holds, the glide comes at the end), and the final consonants close the note. The mouth goes to
// rest only between phrases.
internal sealed partial class StageLipSync
{
    internal readonly record struct Key(double Start, double End, Viseme Viseme, float Strength, int Note, bool Vowel);

    // One sung note. `Variation` gives equal syllables slightly different openings when there is no vocal stem.
    internal readonly record struct Note(double Start, double End, string Text, bool Spoken, int Pitch, float Variation)
    {
        // The opening without a vocal stem: it rises over Attack from the note start and a sustained vowel relaxes by
        // SustainRelax between SustainRelaxStart and SustainRelaxStart + SustainRelaxLength.
        public float Level(double time)
        {
            static double Smooth(double x) { x = Math.Clamp(x, 0, 1); return x * x * (3 - 2 * x); }
            var age = time - (Start - Lead);
            var level = Smooth(age / Attack) * (1 - SustainRelax * Smooth((age - SustainRelaxStart) / SustainRelaxLength));
            return (float)Math.Max(level, NoteFloor) * Variation * (Spoken ? SpokenOpen : 1);
        }
    }

    // Sung note lengths are clamped to this range, and a "+" slide joins the previous note when it starts within
    // SlideJoin of its end.
    private const double MinimumNote = 0.05;
    private const double MaximumNote = 4;
    private const double SlideJoin = 0.25;
    // Tuned: without a vocal stem the mouth opens Lead before the note over Attack; a sustained vowel relaxes by 25 %
    // between 0.3 s and 0.8 s; spoken notes open 70 %; equal syllables vary between 0.85 and 1.15 of their opening.
    // NoteFloor keeps a vowel from starting at zero when its key starts before the note.
    private const double Lead = 0.05;
    private const double Attack = 0.1;
    private const double SustainRelax = 0.25;
    private const double SustainRelaxStart = 0.3;
    private const double SustainRelaxLength = 0.5;
    private const float NoteFloor = 0.35f;
    private const float SpokenOpen = 0.7f;
    private const float VariationBase = 0.85f;
    private const float VariationRange = 0.3f;
    private const int VariationSteps = 1000;
    // Tuned: a gap of more than PhraseGap between two notes ends a phrase. When the gap has room for a rest of at least
    // MinimumRest, the mouth rests from RestDelay after a note to RestMargin before the first consonant of the next note.
    private const double PhraseGap = 0.35;
    private const double RestDelay = 0.12;
    private const double RestMargin = 0.05;
    private const double MinimumRest = 0.12;
    // Tuned: a consonant takes 0.07 s (at least 0.025 s). The onset consonants take at most 45 % of the time from the
    // previous note start, and the final consonants at most 35 % of their note. A diphthong glides in the last 35 % of
    // its vowel, at most 0.15 s. The face leads the sound by VisualLead.
    private const double ConsonantSeconds = 0.07;
    private const double MinimumConsonantSeconds = 0.025;
    private const double OnsetShare = 0.45;
    private const double CodaShare = 0.35;
    private const double GlideShare = 0.35;
    private const double GlideMaximum = 0.15;
    private const double VisualLead = 0.04;
    private const double OpenSongTail = 2;
    private const float FullStrength = 1;
    // A note without letters ("*", "...") sings an open "ah".
    private static readonly byte[] DefaultVowel = { LyricPhonemes.Index("AH") };

    // ARPAbet phoneme (LyricPhonemes.Phonemes order) to viseme, and the glide viseme of a diphthong.
    private static readonly Viseme[] PhonemeVisemes =
    {
        Viseme.AA, Viseme.EH, Viseme.AH, Viseme.OW, Viseme.AA, Viseme.AA, Viseme.MBP, Viseme.SH, Viseme.SZ, Viseme.TH, Viseme.EH, Viseme.ER,
        Viseme.EH, Viseme.FV, Viseme.KG, Viseme.KG, Viseme.IY, Viseme.IY, Viseme.SH, Viseme.KG, Viseme.L, Viseme.MBP, Viseme.SZ, Viseme.KG,
        Viseme.OW, Viseme.OW, Viseme.MBP, Viseme.WR, Viseme.SZ, Viseme.SH, Viseme.SZ, Viseme.TH, Viseme.UW, Viseme.UW, Viseme.FV, Viseme.WR,
        Viseme.IY, Viseme.SZ, Viseme.SH
    };
    private static readonly Dictionary<string, Viseme> Glides = new()
    {
        ["AW"] = Viseme.UW, ["AY"] = Viseme.IY, ["EY"] = Viseme.IY, ["OW"] = Viseme.UW, ["OY"] = Viseme.IY,
    };
    // Tuned: the strength of the consonant visemes: h takes the shape of the vowel after it, so it is weak.
    private const float WeakConsonant = 0.3f;

    // One note per sung note. "+" continues the previous note (a slide); "#" and "^" mark spoken notes.
    public static Note[] Notes(IReadOnlyList<StageCue> cues)
    {
        var notes = new List<Note>();
        foreach (var cue in cues.OrderBy(c => c.Time))
        {
            var text = cue.Value.Trim();
            var end = cue.Time + Math.Clamp(cue.Duration, MinimumNote, MaximumNote);
            if (text.TrimEnd('-', '=') == "+")
            {
                if (notes.Count > 0 && cue.Time - notes[^1].End < SlideJoin) notes[^1] = notes[^1] with { End = Math.Max(notes[^1].End, end) };
                continue;
            }
            if (notes.Count > 0 && cue.Time < notes[^1].End) notes[^1] = notes[^1] with { End = Math.Max(notes[^1].Start + MinimumNote, cue.Time) };
            var hash = Gh3Checksum.Value(text + notes.Count);
            var variation = VariationBase + VariationRange * (hash % VariationSteps) / (float)VariationSteps;
            notes.Add(new Note(cue.Time, end, text, text.IndexOfAny(new[] { '#', '^' }) >= 0, cue.Pitch ?? 0, variation));
        }
        return notes.ToArray();
    }

    // The phonemes of each note: the notes of one word are read together.
    public static (byte[][] Phonemes, int Dictionary, int Rules) NotePhonemes(Note[] notes)
    {
        var result = new byte[notes.Length][];
        int dictionary = 0, rules = 0;
        for (var start = 0; start < notes.Length;)
        {
            var end = start;
            while (end + 1 < notes.Length && LyricPhonemes.JoinsNext(notes[end].Text)) end++;
            var syllables = notes[start..(end + 1)].Select(n => n.Text).ToArray();
            var parts = LyricPhonemes.Divide(syllables);
            if (LyricPhonemes.IsKnown(string.Concat(syllables.Select(LyricPhonemes.Clean)))) dictionary++;
            else rules++;
            for (var i = 0; i < parts.Length; i++) result[start + i] = parts[i].Length > 0 ? parts[i] : DefaultVowel;
            start = end + 1;
        }
        return (result, dictionary, rules);
    }

    public static (Key[] Keys, (int, int) Words) Keys(Note[] notes)
    {
        var (phonemes, dictionary, rules) = NotePhonemes(notes);
        var keys = new List<Key>();
        for (var i = 0; i < notes.Length; i++)
        {
            var note = notes[i];
            var start = note.Start - VisualLead;
            var end = note.End - VisualLead;
            var phraseStart = i == 0 || note.Start - notes[i - 1].End > PhraseGap;
            var sounds = phonemes[i];
            var firstVowel = Array.FindIndex(sounds, LyricPhonemes.IsVowel);
            var lastVowel = Array.FindLastIndex(sounds, LyricPhonemes.IsVowel);
            if (firstVowel < 0) { firstVowel = sounds.Length; lastVowel = sounds.Length - 1; }
            var onset = sounds[..firstVowel];
            var nucleus = firstVowel <= lastVowel ? sounds[firstVowel..(lastVowel + 1)] : Array.Empty<byte>();
            var coda = sounds[(lastVowel + 1)..];
            if (onset.Length > 0)
            {
                var room = phraseStart ? onset.Length * ConsonantSeconds : OnsetShare * (note.Start - notes[i - 1].Start);
                var each = Math.Clamp(room / onset.Length, MinimumConsonantSeconds, ConsonantSeconds);
                for (var j = 0; j < onset.Length; j++)
                    keys.Add(Consonant(onset[j], start - each * (onset.Length - j), start - each * (onset.Length - j - 1), i));
            }
            var codaEach = coda.Length == 0 ? 0 : Math.Clamp(CodaShare * (end - start) / coda.Length, MinimumConsonantSeconds, ConsonantSeconds);
            var nucleusEnd = Math.Max(start + MinimumConsonantSeconds, end - codaEach * coda.Length);
            AddNucleus(keys, nucleus, start, nucleusEnd, i);
            for (var j = 0; j < coda.Length; j++)
                keys.Add(Consonant(coda[j], nucleusEnd + codaEach * j, nucleusEnd + codaEach * (j + 1), i));
            var next = i + 1 < notes.Length ? notes[i + 1].Start - VisualLead - onsetSpan(phonemes[i + 1]) - RestMargin : end + Horizon + OpenSongTail;
            if (next > end + RestDelay + MinimumRest) keys.Add(new Key(end + RestDelay, next, Viseme.Rest, FullStrength, i, false));
        }
        return (keys.OrderBy(k => k.Start).ToArray(), (dictionary, rules));

        static double onsetSpan(byte[] sounds) => Math.Max(0, Array.FindIndex(sounds, LyricPhonemes.IsVowel)) * ConsonantSeconds;
    }

    private static Key Consonant(byte phoneme, double start, double end, int note)
    {
        var viseme = PhonemeVisemes[phoneme];
        var strength = LyricPhonemes.Phonemes[phoneme] == "HH" ? WeakConsonant : FullStrength;
        return new Key(start, end, viseme, strength, note, false);
    }

    // The vowels share the note; a consonant inside the nucleus (from two vowels in one note) takes one consonant time.
    private static void AddNucleus(List<Key> keys, byte[] nucleus, double start, double end, int note)
    {
        if (nucleus.Length == 0)
        {
            keys.Add(new Key(start, end, Viseme.AH, FullStrength, note, true));
            return;
        }
        var vowels = nucleus.Count(LyricPhonemes.IsVowel);
        var inner = nucleus.Length - vowels;
        var consonant = Math.Min(ConsonantSeconds, (end - start) * CodaShare / Math.Max(1, inner));
        var each = (end - start - inner * consonant) / vowels;
        var time = start;
        foreach (var phoneme in nucleus)
        {
            if (!LyricPhonemes.IsVowel(phoneme))
            {
                keys.Add(Consonant(phoneme, time, time + consonant, note));
                time += consonant;
                continue;
            }
            var viseme = PhonemeVisemes[phoneme];
            if (Glides.TryGetValue(LyricPhonemes.Phonemes[phoneme], out var glide))
            {
                var glideLength = Math.Min(GlideMaximum, each * GlideShare);
                keys.Add(new Key(time, time + each - glideLength, viseme, FullStrength, note, true));
                keys.Add(new Key(time + each - glideLength, time + each, glide, FullStrength, note, true));
            }
            else keys.Add(new Key(time, time + each, viseme, FullStrength, note, true));
            time += each;
        }
    }
}
