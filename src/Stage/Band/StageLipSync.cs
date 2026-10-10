using System.Numerics;

namespace Gh3Skin;

// Lip sync for songs without GH3 facial clips. GH3 moves the singer's face only with song-specific facial clips
// (Band_PlayFacialAnim in the song's performance array). A custom chart has none, so the mod opens the mouth on the sung
// syllables of the chart (PART VOCALS notes and lyrics, or .chart lyric events). The mouth shapes are measured from the
// 570 GH3 singer facial clips (tools/lipsync-shapes.py) and add to the body clip as GH3's applydifference does.
internal sealed partial class StageLipSync
{
    // Vowel: the mouth shape of a character with face blend shapes ('a', 'i', 'u', 'e', 'o'; 'n' for a closed mouth).
    internal readonly record struct Syllable(double On, double Off, float Jaw, float Round, char Vowel = 'a');

    // GH3_SKIN_LIPSYNC=off keeps the face still on charts without GH3 facial clips. A menu option can set this before the song loads.
    public static bool Enabled { get; set; } = SkinEnvironment.IsNotOff(SkinEnvironment.LipSync);
    // Tuned: the mouth opens Lead before the note over Attack, closes over Release after it, and closes Gap before the
    // next syllable; a syllable is at least MinimumSyllable long.
    private const double Lead = 0.05;
    private const double Attack = 0.1;
    private const double Release = 0.12;
    private const double Gap = 0.04;
    private const double MinimumSyllable = 0.06;
    // Sung note lengths are clamped to this range, and a "+" slide joins the previous note when it starts within
    // SlideJoin of its end.
    private const double MinimumNote = 0.05;
    private const double MaximumNote = 4;
    private const double SlideJoin = 0.25;
    // Tuned: spoken notes open the mouth less; equal syllables vary between 0.85 and 1.15 of their opening.
    private const float SpokenJaw = 0.7f;
    private const float VariationBase = 0.85f;
    private const float VariationRange = 0.3f;
    private const int VariationSteps = 1000;
    // Tuned: a sustained vowel relaxes by 25 % between 0.3 s and 0.8 s after its start.
    private const double SustainRelax = 0.25;
    private const double SustainRelaxStart = 0.3;
    private const double SustainRelaxLength = 0.5;
    // Tuned: the closed-mouth shape 'n' has 30 % of the blend shape weight.
    private const float ClosedMouthWeight = 0.3f;
    // Measured: a blink closes in 0.05 s, holds to 0.08 s, and opens by 0.17 s (GH3 facial clips).
    private const double BlinkClose = 0.05;
    private const double BlinkHold = 0.08;
    private const double BlinkOpen = 0.09;
    private const double BlinkEnd = 0.17;
    // Measured: about 6 blinks per minute with long gaps: 2 s plus up to 16 s, weighted toward short gaps.
    private const double MinimumBlinkGap = 2;
    private const double BlinkGapRange = 16;
    private const int BlinkRandomSteps = 1000;
    // Below these the mouth is closed and Apply leaves the pose alone.
    private const float ClosedJaw = 0.01f;
    private const float ClosedRound = 0.001f;
    private const float MinimumTurnDegrees = 1e-4f;
    // Jaw opening in degrees about the jaw Z axis. The GH3 facial clips have p50 7.3, p95 29.1, and max 51.8.
    private const float OpenJaw = 24;
    private const float MaxJaw = 30;
    private const float BlinkAngle = 50;
    private static readonly string[] MouthBones =
    {
        "Bone_Jaw", "Bone_Mouth_L", "Bone_Mouth_R", "Bone_Lip_Lower_Mid", "Bone_Lip_Upper_Mid", "Bone_Cheek_L", "Bone_Cheek_R",
        "Bone_Cheek_Upper_L", "Bone_Cheek_Upper_R", "Bone_Lip_Lower_Corner_L", "Bone_Lip_Lower_Corner_R", "Bone_Lip_Lower_L",
        "Bone_Lip_Lower_R", "Bone_Lip_Upper_Corner_L", "Bone_Lip_Upper_Corner_R", "Bone_Lip_Upper_L", "Bone_Lip_Upper_R", "Bone_Tongue"
    };
    private static readonly string[] LidBones = { "Bone_Eyelid_Upper_L", "Bone_Eyelid_Upper_R" };
    private readonly int[] _mouth;
    private readonly int[] _lids;
    private readonly double[] _blinks;
    public Syllable[] Syllables { get; }

    private StageLipSync(Syllable[] syllables, int[] mouth, int[] lids)
    {
        Syllables = syllables;
        _mouth = mouth;
        _lids = lids;
        _blinks = syllables.Length > 0 ? BlinkTimes(syllables[0].On - 1, syllables[^1].Off + 2, XorShift32.GoldenRatioIncrement) : Array.Empty<double>();
    }

    // The GH3 facial clips show about 6 blinks per minute (eyelid closed by more than 20 degrees), with long gaps.
    public static double[] BlinkTimes(double start, double end, uint seed)
    {
        var blinks = new List<double>();
        for (var time = start; time < end;)
        {
            blinks.Add(time);
            XorShift32.Next(ref seed);
            var random = (seed % BlinkRandomSteps) / (double)BlinkRandomSteps;
            time += MinimumBlinkGap + BlinkGapRange * random * random;
        }
        return blinks.ToArray();
    }

    public static StageLipSync? Create(StageTimeline timeline, string role, string[] bones)
    {
        if (!Enabled || role != "singer" || timeline.Track("face", role).Length > 0) return null;
        var syllables = Build(timeline.Track("sing", role));
        if (syllables.Length == 0) return null;
        int Find(string name) => Array.IndexOf(bones, Gh3Checksum.Of(name));
        var mouth = MouthBones.Select(Find).ToArray();
        return mouth[0] < 0 ? null : new StageLipSync(syllables, mouth, LidBones.Select(Find).ToArray());
    }

    // The sung syllables without bones, for the face blend shapes of a character with its own skeleton (the GH3 rig of
    // the role may have no jaw).
    public static StageLipSync? Shapes(StageTimeline timeline, string role)
    {
        if (!Enabled || role != "singer" || timeline.Track("face", role).Length > 0) return null;
        var syllables = Build(timeline.Track("sing", role));
        return syllables.Length == 0 ? null : new StageLipSync(syllables, Array.Empty<int>(), Array.Empty<int>());
    }

    // One syllable per sung note. "+" continues the previous syllable (a slide); "#" and "^" mark spoken notes.
    public static Syllable[] Build(IReadOnlyList<StageCue> cues)
    {
        var notes = new List<(double Start, double End, float Jaw, float Round, char Vowel)>();
        foreach (var cue in cues.OrderBy(c => c.Time))
        {
            var text = cue.Value.Trim();
            var end = cue.Time + Math.Clamp(cue.Duration, MinimumNote, MaximumNote);
            if (text.TrimEnd('-', '=') == "+")
            {
                if (notes.Count > 0 && cue.Time - notes[^1].End < SlideJoin) notes[^1] = notes[^1] with { End = Math.Max(notes[^1].End, end) };
                continue;
            }
            var (jaw, round) = Vowel(text);
            if (text.IndexOfAny(new[] { '#', '^' }) >= 0) jaw *= SpokenJaw;
            // Small deterministic variation, so that equal syllables do not look mechanical.
            var hash = Gh3Checksum.Value(text + notes.Count);
            jaw *= VariationBase + VariationRange * (hash % VariationSteps) / (float)VariationSteps;
            notes.Add((cue.Time, end, Math.Min(jaw, MaxJaw), round, Letter(text)));
        }
        var result = new Syllable[notes.Count];
        for (var i = 0; i < notes.Count; i++)
        {
            var on = notes[i].Start - Lead;
            var off = notes[i].End;
            // Close the mouth a little before the next syllable, so that consecutive syllables show a consonant.
            if (i + 1 < notes.Count) off = Math.Min(off, notes[i + 1].Start - Lead - Gap);
            result[i] = new(on, Math.Max(off, on + MinimumSyllable), notes[i].Jaw, notes[i].Round, notes[i].Vowel);
        }
        return result;
    }

    // The mouth shape of the first vowel, with the rules of Vowel: "ow" and "aw" round to 'o'.
    public static char Letter(string text)
    {
        var letters = new string(text.ToLowerInvariant().Where(char.IsLetter).ToArray());
        for (var i = 0; i < letters.Length; i++)
        {
            var next = i + 1 < letters.Length ? letters[i + 1] : ' ';
            switch (letters[i])
            {
                case 'a': return next is 'w' or 'u' ? 'o' : 'a';
                case 'o' when next is 'w' or 'u' && !letters.StartsWith("you"): return 'a';
                case 'o': return 'o';
                case 'u': return 'u';
                case 'e': return 'e';
                case 'i': return 'i';
                case 'y' when i > 0: return 'i';
            }
        }
        return 'n';
    }

    // Blend shape weights (0-1) of the mouth shapes a, i, u, e, o: the envelope of each syllable, scaled by its jaw
    // opening against the normal opening.
    public void Visemes(double time, float[] weights)
    {
        Array.Clear(weights, 0, weights.Length);
        var index = StageTimeline.UpperBound(Syllables, time, s => s.On) - 1;
        for (var i = index; i >= 0 && i > index - 4; i--)
        {
            var syllable = Syllables[i];
            var level = Envelope(syllable, time) * Math.Min(1, syllable.Jaw / OpenJaw);
            var slot = syllable.Vowel switch { 'a' => 0, 'i' => 1, 'u' => 2, 'e' => 3, 'o' => 4, _ => 0 };
            weights[slot] += syllable.Vowel == 'n' ? level * ClosedMouthWeight : level;
        }
        for (var i = 0; i < weights.Length; i++) weights[i] = Math.Min(1, weights[i]);
    }

    public static float BlinkAt(double[] blinks, double time)
    {
        var index = StageTimeline.UpperBound(blinks, time, t => t) - 1;
        if (index < 0) return 0;
        var age = time - blinks[index];
        return (float)(age < BlinkClose ? age / BlinkClose : age < BlinkHold ? 1 : age < BlinkEnd ? 1 - (age - BlinkHold) / BlinkOpen : 0);
    }

    // Tuned: jaw degrees and mouth width for the first vowel of the syllable: "a" opens, "o" and "u" round, "e" and "i" widen.
    public static (float Jaw, float Round) Vowel(string text)
    {
        var letters = new string(text.ToLowerInvariant().Where(char.IsLetter).ToArray());
        for (var i = 0; i < letters.Length; i++)
        {
            var next = i + 1 < letters.Length ? letters[i + 1] : ' ';
            switch (letters[i])
            {
                case 'a': return (OpenJaw, next is 'w' or 'u' ? 0.3f : -0.1f); // Tuned: by eye
                case 'o' when next is 'w' or 'u' && !letters.StartsWith("you"): return (OpenJaw * 0.9f, 0.2f); // Tuned: by eye
                case 'o': return (OpenJaw * 0.8f, 0.7f); // Tuned: by eye
                case 'u': return (OpenJaw * 0.65f, 0.8f); // Tuned: by eye
                case 'e' or 'i': return (OpenJaw * 0.6f, -0.6f); // Tuned: by eye
                case 'y' when i > 0: return (OpenJaw * 0.75f, -0.3f); // Tuned: by eye
            }
        }
        return (OpenJaw * 0.3f, 0.3f); // Tuned: by eye
    }

    public (float Jaw, float Round) Mouth(double time)
    {
        var index = StageTimeline.UpperBound(Syllables, time, s => s.On) - 1;
        float jaw = 0, round = 0;
        for (var i = index; i >= 0 && i > index - 4; i--)
        {
            var weight = Envelope(Syllables[i], time);
            jaw += weight * Syllables[i].Jaw;
            round += weight * Syllables[i].Round;
        }
        return (Math.Min(jaw, MaxJaw), Math.Clamp(round, -1, 1));
    }

    // Opens over Attack, relaxes a sustained vowel to 75 %, and closes over Release after the syllable.
    public static float Envelope(Syllable syllable, double time)
    {
        if (time <= syllable.On || time >= syllable.Off + Release) return 0;
        static double Smooth(double x) { x = Math.Clamp(x, 0, 1); return x * x * (3 - 2 * x); }
        double Level(double t) => Smooth((t - syllable.On) / Attack) * (1 - SustainRelax * Smooth((t - syllable.On - SustainRelaxStart) / SustainRelaxLength));
        return (float)(time < syllable.Off ? Level(time) : Level(syllable.Off) * (1 - Smooth((time - syllable.Off) / Release)));
    }

    public float Blink(double time) => BlinkAt(_blinks, time);

    public void Apply(BandPose[] pose, double time)
    {
        var (jaw, round) = Mouth(time);
        if (jaw > ClosedJaw || Math.Abs(round) > ClosedRound)
            for (var i = 0; i < _mouth.Length; i++)
            {
                var bone = _mouth[i];
                if (bone < 0) continue;
                var k = 6 * i;
                var rotation = new Vector3(Open[k] * jaw + Round[k] * round, Open[k + 1] * jaw + Round[k + 1] * round, Open[k + 2] * jaw + Round[k + 2] * round);
                var offset = new Vector3(Open[k + 3] * jaw + Round[k + 3] * round, Open[k + 4] * jaw + Round[k + 4] * round, Open[k + 5] * jaw + Round[k + 5] * round);
                pose[bone] = Add(pose[bone], rotation, offset);
            }
        var blink = Blink(time);
        if (blink > 0)
            foreach (var bone in _lids)
                if (bone >= 0) pose[bone] = Add(pose[bone], new Vector3(0, 0, BlinkAngle * blink), Vector3.Zero);
    }

    private static BandPose Add(BandPose current, Vector3 degrees, Vector3 offset)
    {
        var angle = degrees.Length();
        if (angle < MinimumTurnDegrees) return new(current.Position + offset, current.Rotation);
        var turn = Quaternion.CreateFromAxisAngle(degrees / angle, angle * MathF.PI / 180);
        return new(current.Position + offset, Quaternion.Normalize(current.Rotation * turn));
    }

}
