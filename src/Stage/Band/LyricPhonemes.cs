using System.IO.Compression;
using System.Text;

namespace Gh3Skin;

// The phonemes of the sung syllables for the lip sync: the words of the chart lyrics from the CMU Pronouncing Dictionary
// (src/Resources/cmudict.br, tools/lipsync-dictionary.py), with letter-to-sound rules for unknown words, divided over the
// sung notes of each word.
internal static class LyricPhonemes
{
    // ARPAbet without stress, in the order of tools/lipsync-dictionary.py.
    public static readonly string[] Phonemes =
    {
        "AA", "AE", "AH", "AO", "AW", "AY", "B", "CH", "D", "DH", "EH", "ER", "EY", "F", "G", "HH", "IH", "IY", "JH", "K", "L", "M", "N",
        "NG", "OW", "OY", "P", "R", "S", "SH", "T", "TH", "UH", "UW", "V", "W", "Y", "Z", "ZH"
    };
    private const string ResourceName = "Gh3Skin.cmudict.br";
    private const int CodeLetters = 26;
    private const int MaximumWordLength = 64;
    private static readonly Lazy<Dictionary> Words = new(LoadDictionary, LazyThreadSafetyMode.ExecutionAndPublication);
    private static readonly Dictionary<string, byte> PhonemeIndex = Phonemes.Select((p, i) => (p, i)).ToDictionary(x => x.p, x => (byte)x.i);

    public static bool IsVowel(byte phoneme) => Phonemes[phoneme] is "AA" or "AE" or "AH" or "AO" or "AW" or "AY" or "EH" or "ER" or "EY" or "IH" or "IY" or "OW" or "OY" or "UH" or "UW";
    public static byte Index(string phoneme) => PhonemeIndex[phoneme];

    // Loads the dictionary now (on a worker thread before the song), so that the first lookup does not wait.
    public static int Preload() => Words.Value.Count;

    private sealed class Dictionary
    {
        private readonly byte[] _text;
        private readonly int[] _lines;
        public int Count => _lines.Length;

        public Dictionary(byte[] text)
        {
            _text = text;
            var lines = new List<int> { 0 };
            for (var i = 0; i < text.Length; i++)
                if (text[i] == '\n' && i + 1 < text.Length) lines.Add(i + 1);
            _lines = lines.ToArray();
        }

        // Binary search over the sorted lines "word<TAB>codes".
        public byte[]? Find(string word)
        {
            var key = Encoding.ASCII.GetBytes(word);
            int low = 0, high = _lines.Length - 1;
            while (low <= high)
            {
                var middle = (low + high) / 2;
                var start = _lines[middle];
                var tab = Array.IndexOf(_text, (byte)'\t', start);
                var order = new ReadOnlySpan<byte>(_text, start, tab - start).SequenceCompareTo(key);
                if (order == 0)
                {
                    var end = Array.IndexOf(_text, (byte)'\n', tab);
                    if (end < 0) end = _text.Length;
                    var result = new byte[end - tab - 1];
                    for (var i = 0; i < result.Length; i++)
                    {
                        var letter = _text[tab + 1 + i];
                        result[i] = (byte)(letter >= 'a' ? letter - 'a' + CodeLetters : letter - 'A');
                    }
                    return result;
                }
                if (order < 0) low = middle + 1;
                else high = middle - 1;
            }
            return null;
        }
    }

    private static Dictionary LoadDictionary()
    {
        using var stream = typeof(LyricPhonemes).Assembly.GetManifestResourceStream(ResourceName);
        if (stream == null) return new Dictionary(Array.Empty<byte>());
        using var brotli = new BrotliStream(stream, CompressionMode.Decompress);
        using var output = new MemoryStream();
        brotli.CopyTo(output);
        return new Dictionary(output.ToArray());
    }

    // The letters and apostrophes of a lyric syllable in lower case. Rock Band marks: '-' and '=' join the next
    // syllable, '#' and '^' mark spoken notes, '$' hides a harmony, '§' joins two syllables in one note.
    public static string Clean(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var c in text.ToLowerInvariant())
            if (c is >= 'a' and <= 'z' or '\'') builder.Append(c);
            else if (c is ' ' or '§' or '_') builder.Append(' ');
        return builder.ToString().Trim();
    }

    public static bool JoinsNext(string text)
    {
        var trimmed = text.Trim().TrimEnd('#', '^', '$', '*', '%', '/');
        return trimmed.EndsWith('-') || trimmed.EndsWith('=');
    }

    // The phonemes of one word: the dictionary, then the word without a final apostrophe ("lovin'" as "loving"), then the
    // letter-to-sound rules.
    public static byte[] Word(string word) => Known(word) ?? LetterToSound.Convert(word.Replace("'", ""));

    public static bool IsKnown(string text) => text.Split(' ', StringSplitOptions.RemoveEmptyEntries).All(word => Known(word) != null);

    private static byte[]? Known(string word)
    {
        word = word.Trim('\'');
        if (word.Length == 0) return Array.Empty<byte>();
        if (word.Length > MaximumWordLength) word = word[..MaximumWordLength];
        var dictionary = Words.Value;
        return dictionary.Find(word) ?? (word.EndsWith("in'", StringComparison.Ordinal) || word.EndsWith("in", StringComparison.Ordinal) ?
            dictionary.Find(word.TrimEnd('\'') + "g") : null) ?? dictionary.Find(word.Replace("'", ""));
    }

    // The phonemes of each sung note of one word. The notes carry the syllables of the chart ("los-", "ing"). Each note
    // gets one vowel; a single consonant between two vowels starts the next syllable, and of two or more the first ends
    // the syllable. Extra notes repeat the last vowel; extra vowels go to the last note. A word that the dictionary does
    // not have is read syllable by syllable, and a syllable that starts with a vowel letter and is not a word continues
    // the vowel before it (a held vowel written as more syllables: "yeah-" "eah").
    public static byte[][] Divide(IReadOnlyList<string> syllables)
    {
        var count = syllables.Count;
        var words = string.Concat(syllables.Select(Clean)).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (count > 1 && words.Length == 1 && Known(words[0]) == null) return BySyllable(syllables);
        return Split(words.SelectMany(Word).ToArray(), count);
    }

    private static byte[][] BySyllable(IReadOnlyList<string> syllables)
    {
        var result = new byte[syllables.Count][];
        byte[] lastVowel = Array.Empty<byte>();
        for (var i = 0; i < result.Length; i++)
        {
            var text = Clean(syllables[i]).Replace(" ", "");
            var known = Known(text);
            result[i] = known ?? (i > 0 && text.Length > 0 && "aeiouy".Contains(text[0]) && lastVowel.Length > 0 ? lastVowel : Word(text));
            var vowel = result[i].LastOrDefault(IsVowel);
            if (result[i].Any(IsVowel)) lastVowel = new[] { vowel };
        }
        return result;
    }

    private static byte[][] Split(byte[] phonemes, int count)
    {
        var result = new byte[count][];
        var vowels = Enumerable.Range(0, phonemes.Length).Where(i => IsVowel(phonemes[i])).ToArray();
        if (count == 1 || vowels.Length == 0)
        {
            result[0] = phonemes;
            for (var i = 1; i < count; i++) result[i] = Array.Empty<byte>();
            return result;
        }
        var cuts = new int[count + 1];
        cuts[count] = phonemes.Length;
        for (var note = 1; note < count; note++)
        {
            if (note >= vowels.Length) { cuts[note] = phonemes.Length; continue; }
            var previous = vowels[note - 1];
            var next = vowels[note];
            var consonants = next - previous - 1;
            cuts[note] = consonants <= 1 ? previous + 1 : previous + 2;
        }
        var lastVowel = new[] { phonemes[vowels[^1]] };
        for (var note = 0; note < count; note++)
            result[note] = cuts[note] < cuts[note + 1] ? phonemes[cuts[note]..cuts[note + 1]] : note >= vowels.Length ? lastVowel : Array.Empty<byte>();
        return result;
    }

    // English letter-to-sound rules for words that the dictionary does not have (names, sung sounds, typos): the
    // common spellings of each sound, longest first, and the silent final e that makes the vowel before it long.
    private static class LetterToSound
    {
        private static readonly (string Letters, string Sound)[] Rules =
        {
            ("tch", "CH"), ("igh", "AY"), ("ough", "AO"), ("augh", "AO"), ("eigh", "EY"), ("sch", "S K"),
            ("ch", "CH"), ("sh", "SH"), ("th", "TH"), ("ph", "F"), ("ng", "NG"), ("ck", "K"), ("qu", "K W"), ("wh", "W"), ("gh", ""),
            ("ee", "IY"), ("ea", "IY"), ("oo", "UW"), ("ou", "AW"), ("ow", "OW"), ("oi", "OY"), ("oy", "OY"), ("ai", "EY"),
            ("ay", "EY"), ("au", "AO"), ("aw", "AO"), ("ie", "IY"), ("ue", "UW"), ("ew", "UW"), ("oa", "OW"), ("ei", "EY"), ("ey", "EY"),
            ("a", "AE"), ("e", "EH"), ("i", "IH"), ("o", "AA"), ("u", "AH"), ("b", "B"), ("c", "K"), ("d", "D"), ("f", "F"), ("g", "G"),
            ("h", "HH"), ("j", "JH"), ("k", "K"), ("l", "L"), ("m", "M"), ("n", "N"), ("p", "P"), ("q", "K"), ("r", "R"), ("s", "S"),
            ("t", "T"), ("v", "V"), ("w", "W"), ("x", "K S"), ("z", "Z"),
        };
        private static readonly Dictionary<char, string> LongVowels = new() { ['a'] = "EY", ['e'] = "IY", ['i'] = "AY", ['o'] = "OW", ['u'] = "UW" };
        private const string VowelLetters = "aeiou";

        public static byte[] Convert(string word)
        {
            var sounds = new List<string>();
            var silentE = word.Length > 2 && word[^1] == 'e' && !VowelLetters.Contains(word[^2]) && VowelLetters.Contains(word[^3]);
            var length = silentE ? word.Length - 1 : word.Length;
            for (var i = 0; i < length;)
            {
                var c = word[i];
                if (silentE && i == length - 2 && LongVowels.TryGetValue(c, out var vowel)) { sounds.Add(vowel); i++; continue; }
                if (c == 'c' && i + 1 < length && word[i + 1] is 'e' or 'i' or 'y') { sounds.Add("S"); i++; continue; }
                if (c == 'y') { sounds.Add(i == 0 ? "Y" : i == length - 1 && length <= 3 ? "AY" : "IY"); i++; continue; }
                if (i + 1 < length && c == word[i + 1] && !VowelLetters.Contains(c)) { i++; continue; }
                var rule = Rules.First(r => string.CompareOrdinal(word, i, r.Letters, 0, r.Letters.Length) == 0 && i + r.Letters.Length <= length ||
                    r.Letters.Length == 1 && r.Letters[0] == c);
                if (rule.Sound.Length > 0) sounds.AddRange(rule.Sound.Split(' '));
                i += rule.Letters.Length;
            }
            return sounds.Select(Index).ToArray();
        }
    }
}
