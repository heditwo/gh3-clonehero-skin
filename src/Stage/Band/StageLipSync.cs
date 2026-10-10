using System.Numerics;

namespace Gh3Skin;

// Lip sync for songs without GH3 facial clips. GH3 moves the singer's face only with song-specific facial clips
// (Band_PlayFacialAnim in the song's performance array). A custom chart has none, so the mod opens the mouth on the sung
// syllables of the chart (PART VOCALS notes and lyrics, or .chart lyric events). The mouth shapes are measured from the
// 570 GH3 singer facial clips (tools/lipsync-shapes.py) and add to the body clip as GH3's applydifference does.
internal sealed class StageLipSync
{
    // Vowel: the mouth shape of a character with face blend shapes ('a', 'i', 'u', 'e', 'o'; 'n' for a closed mouth).
    internal readonly record struct Syllable(double On, double Off, float Jaw, float Round, char Vowel = 'a');

    // GH3_SKIN_LIPSYNC=off keeps the face still on charts without GH3 facial clips. A menu option can set this before the song loads.
    public static bool Enabled { get; set; } = Environment.GetEnvironmentVariable("GH3_SKIN_LIPSYNC") is not ("off" or "0");
    private const double Lead = 0.05;
    private const double Attack = 0.1;
    private const double Release = 0.12;
    private const double Gap = 0.04;
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
            var random = (seed % 1000) / 1000.0;
            time += 2 + 16 * random * random;
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
            var end = cue.Time + Math.Clamp(cue.Duration, 0.05, 4);
            if (text.TrimEnd('-', '=') == "+")
            {
                if (notes.Count > 0 && cue.Time - notes[^1].End < 0.25) notes[^1] = notes[^1] with { End = Math.Max(notes[^1].End, end) };
                continue;
            }
            var (jaw, round) = Vowel(text);
            if (text.IndexOfAny(new[] { '#', '^' }) >= 0) jaw *= 0.7f;
            // Small deterministic variation, so that equal syllables do not look mechanical.
            var hash = Gh3Checksum.Value(text + notes.Count);
            jaw *= 0.85f + 0.3f * (hash % 1000) / 1000f;
            notes.Add((cue.Time, end, Math.Min(jaw, MaxJaw), round, Letter(text)));
        }
        var result = new Syllable[notes.Count];
        for (var i = 0; i < notes.Count; i++)
        {
            var on = notes[i].Start - Lead;
            var off = notes[i].End;
            // Close the mouth a little before the next syllable, so that consecutive syllables show a consonant.
            if (i + 1 < notes.Count) off = Math.Min(off, notes[i + 1].Start - Lead - Gap);
            result[i] = new(on, Math.Max(off, on + 0.06), notes[i].Jaw, notes[i].Round, notes[i].Vowel);
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
            weights[slot] += syllable.Vowel == 'n' ? level * 0.3f : level;
        }
        for (var i = 0; i < weights.Length; i++) weights[i] = Math.Min(1, weights[i]);
    }

    public static float BlinkAt(double[] blinks, double time)
    {
        var index = StageTimeline.UpperBound(blinks, time, t => t) - 1;
        if (index < 0) return 0;
        var age = time - blinks[index];
        return (float)(age < 0.05 ? age / 0.05 : age < 0.08 ? 1 : age < 0.17 ? 1 - (age - 0.08) / 0.09 : 0);
    }

    // Jaw degrees and mouth width for the first vowel of the syllable: "a" opens, "o" and "u" round, "e" and "i" widen.
    public static (float Jaw, float Round) Vowel(string text)
    {
        var letters = new string(text.ToLowerInvariant().Where(char.IsLetter).ToArray());
        for (var i = 0; i < letters.Length; i++)
        {
            var next = i + 1 < letters.Length ? letters[i + 1] : ' ';
            switch (letters[i])
            {
                case 'a': return (OpenJaw, next is 'w' or 'u' ? 0.3f : -0.1f);
                case 'o' when next is 'w' or 'u' && !letters.StartsWith("you"): return (OpenJaw * 0.9f, 0.2f);
                case 'o': return (OpenJaw * 0.8f, 0.7f);
                case 'u': return (OpenJaw * 0.65f, 0.8f);
                case 'e' or 'i': return (OpenJaw * 0.6f, -0.6f);
                case 'y' when i > 0: return (OpenJaw * 0.75f, -0.3f);
            }
        }
        return (OpenJaw * 0.3f, 0.3f);
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
        double Level(double t) => Smooth((t - syllable.On) / Attack) * (1 - 0.25 * Smooth((t - syllable.On - 0.3) / 0.5));
        return (float)(time < syllable.Off ? Level(time) : Level(syllable.Off) * (1 - Smooth((time - syllable.Off) / Release)));
    }

    public float Blink(double time) => BlinkAt(_blinks, time);

    public void Apply(BandPose[] pose, double time)
    {
        var (jaw, round) = Mouth(time);
        if (jaw > 0.01f || Math.Abs(round) > 0.001f)
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
        if (angle < 1e-4f) return new(current.Position + offset, current.Rotation);
        var turn = Quaternion.CreateFromAxisAngle(degrees / angle, angle * MathF.PI / 180);
        return new(current.Position + offset, Quaternion.Normalize(current.Rotation * turn));
    }

    // Per bone of MouthBones: rotation vector (degrees, local axes, applied after the current rotation) and translation.
    // Open is per degree of jaw opening; Round is the mouth-width component at its 95th percentile (positive: corners in).
    private static readonly float[] Open =
    {
        -0.0212f, -0.0132f, 1.0000f, -0.000013f, -0.000194f, -0.000003f, // Bone_Jaw
        0.0000f, 0.0000f, 0.0000f, -0.000301f, -0.000526f, -0.000060f, // Bone_Mouth_L
        0.0000f, 0.0000f, 0.0000f, -0.000333f, -0.000517f, 0.000013f, // Bone_Mouth_R
        -0.0369f, 0.0062f, 0.7595f, -0.001186f, -0.000729f, -0.000038f, // Bone_Lip_Lower_Mid
        -0.0142f, -0.0114f, 0.0930f, 0.000021f, -0.000076f, -0.000014f, // Bone_Lip_Upper_Mid
        0.1058f, -0.0220f, 0.2523f, -0.000208f, -0.000345f, 0.000006f, // Bone_Cheek_L
        -0.1217f, -0.0348f, 0.2889f, -0.000220f, -0.000290f, -0.000042f, // Bone_Cheek_R
        -0.0671f, 0.1612f, 0.1667f, -0.000080f, -0.000059f, -0.000029f, // Bone_Cheek_Upper_L
        0.0479f, -0.1529f, 0.1516f, -0.000080f, -0.000038f, 0.000004f, // Bone_Cheek_Upper_R
        -0.1645f, -0.6612f, 0.9704f, -0.000817f, -0.000718f, -0.000138f, // Bone_Lip_Lower_Corner_L
        0.1572f, 0.6176f, 0.9351f, -0.000873f, -0.000734f, 0.000090f, // Bone_Lip_Lower_Corner_R
        -0.1169f, -0.2973f, 0.6228f, -0.001074f, -0.000753f, -0.000147f, // Bone_Lip_Lower_L
        0.1169f, 0.1704f, 0.6455f, -0.001114f, -0.000751f, 0.000085f, // Bone_Lip_Lower_R
        -0.3833f, 0.2567f, 0.1961f, -0.000084f, -0.000299f, -0.000116f, // Bone_Lip_Upper_Corner_L
        0.3712f, -0.3017f, 0.1461f, -0.000102f, -0.000292f, 0.000083f, // Bone_Lip_Upper_Corner_R
        -0.1510f, 0.1775f, 0.1904f, -0.000017f, -0.000153f, -0.000113f, // Bone_Lip_Upper_L
        0.1309f, -0.1728f, 0.1321f, -0.000019f, -0.000147f, 0.000089f, // Bone_Lip_Upper_R
        0.0000f, 0.0000f, 0.0059f, -0.001136f, -0.000702f, -0.000037f, // Bone_Tongue
    };
    private static readonly float[] Round =
    {
        -0.0300f, 0.3901f, 0.0000f, -0.001010f, 0.000457f, 0.000080f, // Bone_Jaw
        0.0000f, 0.0000f, 0.0264f, -0.007394f, 0.009765f, 0.007340f, // Bone_Mouth_L
        0.0000f, 0.0000f, 0.0264f, -0.006049f, 0.009544f, -0.006509f, // Bone_Mouth_R
        1.3631f, -1.3606f, 3.2620f, -0.001582f, 0.002455f, 0.001141f, // Bone_Lip_Lower_Mid
        1.4956f, -0.1247f, -7.0595f, -0.003704f, 0.004910f, -0.000090f, // Bone_Lip_Upper_Mid
        5.9089f, -5.8067f, 0.3735f, -0.003361f, 0.001111f, 0.007091f, // Bone_Cheek_L
        -5.4739f, 5.4876f, -0.2291f, -0.002242f, 0.000393f, -0.006477f, // Bone_Cheek_R
        2.9678f, 1.2319f, 3.9205f, -0.004757f, -0.002127f, 0.004842f, // Bone_Cheek_Upper_L
        -2.2896f, -1.9410f, 3.8716f, -0.004123f, -0.002483f, -0.004083f, // Bone_Cheek_Upper_R
        9.3118f, -1.2824f, 13.6397f, -0.003824f, 0.008954f, 0.006762f, // Bone_Lip_Lower_Corner_L
        -7.5532f, 4.0189f, 13.8752f, -0.002521f, 0.008009f, -0.005381f, // Bone_Lip_Lower_Corner_R
        7.3408f, 1.4775f, 14.6518f, -0.002427f, 0.004845f, 0.004639f, // Bone_Lip_Lower_L
        -4.6538f, 0.9963f, 14.0494f, -0.001808f, 0.004143f, -0.002791f, // Bone_Lip_Lower_R
        -1.4339f, 11.8701f, -12.3186f, -0.007167f, 0.008488f, 0.006642f, // Bone_Lip_Upper_Corner_L
        0.8764f, -10.2196f, -9.6503f, -0.007264f, 0.008731f, -0.006493f, // Bone_Lip_Upper_Corner_R
        -2.7327f, 5.4831f, -6.0514f, -0.005255f, 0.005688f, 0.003536f, // Bone_Lip_Upper_L
        0.6051f, -7.6622f, -5.2251f, -0.005758f, 0.006087f, -0.003554f, // Bone_Lip_Upper_R
        0.0000f, -0.0078f, -0.0206f, -0.001445f, 0.002521f, 0.001112f, // Bone_Tongue
    };
}
