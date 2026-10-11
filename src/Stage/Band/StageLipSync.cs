using System.Numerics;

namespace Gh3Skin;

// Lip sync for songs without GH3 facial clips. GH3 moves the singer's face only with song-specific facial clips
// (Band_PlayFacialAnim in the song's performance array). A custom chart has none, so the mod makes the face from the
// chart: the lyrics give phonemes (LyricPhonemes), the phonemes give visemes on a time track (StageLipSync.Track.cs),
// and the visemes blend with coarticulation. The vocal stem, when the chart folder has one, gives the loudness
// (VocalEnvelope). The viseme poses are GH3 mouth poses measured from the 570 GH3 singer facial clips
// (tools/lipsync-shapes.py); they add to the body clip as GH3's applydifference does. StageLipSync.Life.cs adds the
// brows, the eyes, and small head nods.
internal sealed partial class StageLipSync
{
    internal enum Viseme : byte { Rest, AA, AH, EH, IY, OW, UW, ER, MBP, FV, TH, L, WR, SZ, SH, KG }
    private const int VisemeCount = 16;
    private const int ShapedVisemes = VisemeCount - 1;
    private const int BoneValues = 6;

    // GH3_SKIN_LIPSYNC=off keeps the face still on charts without GH3 facial clips. A menu option can set this before the song loads.
    public static bool Enabled { get; set; } = SkinEnvironment.IsNotOff(SkinEnvironment.LipSync);
    // Measured: a blink closes in 0.05 s, holds to 0.08 s, and opens by 0.17 s (GH3 facial clips).
    private const double BlinkClose = 0.05;
    private const double BlinkHold = 0.08;
    private const double BlinkOpen = 0.09;
    private const double BlinkEnd = 0.17;
    // Measured: about 6 blinks per minute with long gaps: 2 s plus up to 16 s, weighted toward short gaps.
    private const double MinimumBlinkGap = 2;
    private const double BlinkGapRange = 16;
    private const int BlinkRandomSteps = 1000;
    private const float BlinkAngle = 50;
    private const float MinimumTurnDegrees = 1e-4f;
    private const float MinimumWeight = 1e-3f;
    // Tuned: the dominance of a key falls as exp(-distance / time) outside its interval: vowels and the rest pose
    // spread wider than consonants, so a consonant between two vowels is a partial closure. Keys further away than
    // Horizon have no effect. A small rest dominance everywhere closes the mouth far from any key.
    private const double VowelTime = 0.06;
    private const double ConsonantTime = 0.03;
    private const double RestTime = 0.08;
    private const double Horizon = 0.4;
    private const float BackgroundRest = 0.02f;
    // Tuned: m, b, and p close the lips fully: the closure replaces the mouth over the key and falls off as a Gaussian.
    private const double ClosureTime = 0.03;
    // Tuned: the mouth opening follows the vocal stem loudness: 25 % at silence, the full viseme at the normal loud level,
    // up to 140 % when louder. The curve exponent opens quiet singing more than a linear map. The face shows the sound
    // 0.04 s early.
    private const float GainFloor = 0.25f;
    private const float GainCeiling = 1.4f;
    private const float GainCurve = 0.6f;
    private const double EnvelopeLead = 0.04;
    private static readonly string[] MouthBones =
    {
        "Bone_Jaw", "Bone_Mouth_L", "Bone_Mouth_R", "Bone_Lip_Lower_Mid", "Bone_Lip_Upper_Mid", "Bone_Cheek_L", "Bone_Cheek_R",
        "Bone_Cheek_Upper_L", "Bone_Cheek_Upper_R", "Bone_Lip_Lower_Corner_L", "Bone_Lip_Lower_Corner_R", "Bone_Lip_Lower_L",
        "Bone_Lip_Lower_R", "Bone_Lip_Upper_Corner_L", "Bone_Lip_Upper_Corner_R", "Bone_Lip_Upper_L", "Bone_Lip_Upper_R", "Bone_Tongue"
    };
    private const int LowerLipMiddle = 3, UpperLipMiddle = 4;
    private static readonly string[] LidBones = { "Bone_Eyelid_Upper_L", "Bone_Eyelid_Upper_R" };
    // Tuned: the dominance of each viseme. The lip consonants (f, v, w, r, sh) show strongly between two vowels; the
    // tongue consonants (t, d, n, s, z, l, k, g, th) change the lips little, so the jaw stays open in legato singing.
    private static readonly float[] Dominance = { 1, 1, 1, 1, 1, 1, 1, 1, 0, 1.4f, 0.5f, 0.3f, 1.2f, 0.4f, 1, 0.3f };
    // Tuned: the face blend shapes a, i, u, e, o of each viseme (VRM and MMD characters have only these five mouth shapes).
    private static readonly float[] VowelShapes =
    {
        0, 0, 0, 0, 0, // Rest
        1, 0, 0, 0, 0, // AA
        0.6f, 0, 0, 0.1f, 0.1f, // AH
        0.25f, 0, 0, 0.75f, 0, // EH
        0, 0.85f, 0, 0.1f, 0, // IY
        0.1f, 0, 0, 0, 0.85f, // OW
        0, 0, 0.9f, 0, 0.1f, // UW
        0, 0, 0.35f, 0.1f, 0.3f, // ER
        0, 0, 0, 0, 0, // MBP
        0, 0.25f, 0, 0.05f, 0, // FV
        0.1f, 0.1f, 0, 0.25f, 0, // TH
        0.3f, 0, 0, 0.2f, 0, // L
        0, 0, 0.7f, 0, 0.1f, // WR
        0, 0.35f, 0, 0.05f, 0, // SZ
        0, 0.1f, 0.5f, 0, 0.1f, // SH
        0.25f, 0, 0, 0.15f, 0, // KG
    };
    private const int VowelShapeCount = 5;
    // CharacterSkeleton.FaceSlots: blink, a, i, u, e, o, pp, ff, th, dd, kk, ch, ss, nn, rr, browUp, browDown, squint.
    private const int FirstVowelSlot = 1;
    private const int PpSlot = 6, FfSlot = 7, ThSlot = 8, DdSlot = 9, KkSlot = 10, ChSlot = 11, SsSlot = 12, NnSlot = 13, RrSlot = 14;
    private const int BrowUpSlot = 15, BrowDownSlot = 16, SquintSlot = 17;
    // The viseme slots that can show each viseme, in order of preference (empty: the vowel mix).
    private static readonly int[][] ConsonantSlots =
    {
        Array.Empty<int>(), Array.Empty<int>(), Array.Empty<int>(), Array.Empty<int>(), Array.Empty<int>(), Array.Empty<int>(), Array.Empty<int>(),
        Array.Empty<int>(), new[] { PpSlot }, new[] { FfSlot }, new[] { ThSlot }, new[] { NnSlot, DdSlot }, new[] { RrSlot }, new[] { SsSlot, DdSlot, NnSlot },
        new[] { ChSlot }, new[] { KkSlot },
    };
    // Tuned: the mouth shape keys of a character are a normal open mouth at 100 %; the normal loud level gives 70 % of a
    // viseme pose (GainFloor and GainCurve), so the shapes are 1.4 times the viseme weight.
    private const float ShapeGain = 1.4f;
    // Tuned: a squint shape key of a character often closes the eyes far; the face uses 60 % of it at most.
    private const float SquintShape = 0.6f;

    private readonly Key[] _keys;
    private readonly double[] _reach;
    private readonly Note[] _notes;
    private readonly int[] _mouth;
    private readonly int[] _lids;
    private readonly double[] _blinks;
    private readonly float[] _weights = new float[VisemeCount];
    private readonly float[] _delta = new float[MouthBones.Length * BoneValues];
    private Task<VocalEnvelope?>? _pending;
    private VocalEnvelope? _envelope;

    public int NoteCount => _notes.Length;
    public int KeyCount => _keys.Length;
    public int DictionaryWords { get; }
    public int RuleWords { get; }
    public VocalEnvelope? Envelope => _envelope;

    private StageLipSync(Note[] notes, Key[] keys, int[] mouth, int[] lids, int[] life, (int Dictionary, int Rules) words)
    {
        _notes = notes;
        _keys = keys;
        _reach = new double[keys.Length];
        for (var i = 0; i < keys.Length; i++) _reach[i] = Math.Max(i > 0 ? _reach[i - 1] : double.MinValue, keys[i].End);
        _mouth = mouth;
        _lids = lids;
        (DictionaryWords, RuleWords) = words;
        _blinks = notes.Length > 0 ? BlinkTimes(notes[0].Start - 1, notes[^1].End + 2, XorShift32.GoldenRatioIncrement) : Array.Empty<double>();
        _pending = VocalEnvelope.Current;
        _life = new Life(notes, life);
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
        var notes = Notes(timeline.Track("sing", role));
        if (notes.Length == 0) return null;
        int Find(string name) => Array.IndexOf(bones, Gh3Checksum.Of(name));
        var mouth = MouthBones.Select(Find).ToArray();
        if (mouth[0] < 0) return null;
        var (keys, words) = Keys(notes);
        return new StageLipSync(notes, keys, mouth, LidBones.Select(Find).ToArray(), LifeBones.Select(Find).ToArray(), words);
    }

    // The sung syllables without bones, for the face blend shapes of a character with its own skeleton (the GH3 rig of
    // the role may have no jaw).
    public static StageLipSync? Shapes(StageTimeline timeline, string role)
    {
        if (!Enabled || role != "singer" || timeline.Track("face", role).Length > 0) return null;
        var notes = Notes(timeline.Track("sing", role));
        if (notes.Length == 0) return null;
        var (keys, words) = Keys(notes);
        return new StageLipSync(notes, keys, Array.Empty<int>(), Array.Empty<int>(), Array.Empty<int>(), words);
    }

    public static float BlinkAt(double[] blinks, double time)
    {
        var index = StageTimeline.UpperBound(blinks, time, t => t) - 1;
        if (index < 0) return 0;
        var age = time - blinks[index];
        return (float)(age < BlinkClose ? age / BlinkClose : age < BlinkHold ? 1 : age < BlinkEnd ? 1 - (age - BlinkHold) / BlinkOpen : 0);
    }

    public float Blink(double time) => BlinkAt(_blinks, time);

    // The loudness of the vocal stem at a song time, or -1 while there is no envelope (no stem, or still decoding).
    public float Loudness(double time)
    {
        if (_envelope == null && _pending != null && _pending.IsCompleted)
        {
            _envelope = _pending.IsCompletedSuccessfully ? _pending.Result : null;
            _pending = null;
        }
        return _envelope == null ? -1 : _envelope.At(time + EnvelopeLead);
    }

    // The weight of each viseme at a time (Rest first), and the closure of the lips (0-1) for m, b, and p.
    public float Weights(double time, float[] weights)
    {
        Array.Clear(weights, 0, weights.Length);
        var loudness = Loudness(time);
        var total = BackgroundRest;
        weights[(int)Viseme.Rest] = BackgroundRest;
        float closure = 0;
        var first = StageTimeline.UpperBound(_keys, time + Horizon, k => k.Start) - 1;
        for (var i = first; i >= 0 && _reach[i] >= time - Horizon; i--)
        {
            var key = _keys[i];
            var distance = time < key.Start ? key.Start - time : time > key.End ? time - key.End : 0;
            if (distance > Horizon) continue;
            if (key.Viseme == Viseme.MBP)
            {
                var x = distance / ClosureTime;
                closure = Math.Max(closure, (float)Math.Exp(-x * x) * key.Strength);
                continue;
            }
            var vowel = key.Vowel;
            var spread = key.Viseme == Viseme.Rest ? RestTime : vowel ? VowelTime : ConsonantTime;
            var dominance = Dominance[(int)key.Viseme] * (float)Math.Exp(-distance / spread);
            total += dominance;
            weights[(int)key.Viseme] += dominance * (vowel ? key.Strength * Gain(key, time, loudness) : key.Strength);
        }
        for (var v = 0; v < weights.Length; v++) weights[v] /= total;
        return closure;
    }

    private float Gain(Key key, double time, float loudness)
    {
        if (loudness >= 0) return GainFloor + (1 - GainFloor) * MathF.Pow(Math.Min(loudness, GainCeiling), GainCurve);
        return _notes[key.Note].Level(time);
    }

    // Blend shape weights (0-1) of the face slots of a character (CharacterSkeleton.FaceSlots order; slot 0, blink, is
    // not changed). A viseme uses its own slot (a VRChat viseme) when the character has it, else a mix of the vowel
    // shapes a, i, u, e, o. `present`: the slots that the character has.
    public void Face(double time, float[] slots, bool[] present)
    {
        Array.Clear(slots, 1, slots.Length - 1);
        var closure = Weights(time, _weights);
        for (var v = 1; v < VisemeCount; v++)
        {
            if (_weights[v] < MinimumWeight) continue;
            var own = Array.Find(ConsonantSlots[v], slot => slot < present.Length && present[slot]);
            if (own > 0) { slots[own] += _weights[v] * ShapeGain; continue; }
            for (var s = 0; s < VowelShapeCount; s++) slots[FirstVowelSlot + s] += _weights[v] * ShapeGain * VowelShapes[v * VowelShapeCount + s];
        }
        for (var s = 1; s < slots.Length; s++) slots[s] = Math.Clamp(slots[s] * (1 - closure), 0, 1);
        if (present.Length > PpSlot && present[PpSlot]) slots[PpSlot] = Math.Max(slots[PpSlot], closure);
        if (slots.Length > SquintSlot)
        {
            var brow = BrowAt(time);
            slots[BrowUpSlot] = Math.Clamp(brow, 0, 1);
            slots[BrowDownSlot] = Math.Clamp(-brow, 0, 1);
            slots[SquintSlot] = SquintShape * SquintAt(time);
        }
    }

    // The jaw opening in degrees (for checks and the trace).
    public float Jaw(double time)
    {
        Mouth(time);
        return _delta[2];
    }

    // The change of the lip gap from the bind pose in metres (the upper minus the lower lip middle, head up axis; for
    // checks: negative or near zero is a closed mouth).
    public float LipGap(double time)
    {
        Mouth(time);
        return _delta[UpperLipMiddle * BoneValues + BoneValues / 2] - _delta[LowerLipMiddle * BoneValues + BoneValues / 2];
    }

    private void Mouth(double time)
    {
        var closure = Weights(time, _weights);
        Array.Clear(_delta, 0, _delta.Length);
        for (var v = 1; v < VisemeCount; v++)
        {
            var weight = _weights[v] * (1 - closure) + (v == (int)Viseme.MBP ? closure : 0);
            if (weight < MinimumWeight) continue;
            var offset = (v - 1) * _delta.Length;
            for (var i = 0; i < _delta.Length; i++) _delta[i] += weight * VisemePoses[offset + i];
        }
    }

    public void Apply(BandPose[] pose, double time)
    {
        Mouth(time);
        for (var i = 0; i < _mouth.Length; i++)
        {
            var bone = _mouth[i];
            if (bone < 0) continue;
            var k = BoneValues * i;
            pose[bone] = Add(pose[bone], new Vector3(_delta[k], _delta[k + 1], _delta[k + 2]), new Vector3(_delta[k + 3], _delta[k + 4], _delta[k + 5]));
        }
        var blink = Blink(time);
        if (blink > 0)
            foreach (var bone in _lids)
                if (bone >= 0) pose[bone] = Add(pose[bone], new Vector3(0, 0, BlinkAngle * blink), Vector3.Zero);
        _life.Apply(pose, time, Loudness(time));
    }

    private static BandPose Add(BandPose current, Vector3 degrees, Vector3 offset)
    {
        var angle = degrees.Length();
        if (angle < MinimumTurnDegrees) return new(current.Position + offset, current.Rotation);
        var turn = Quaternion.CreateFromAxisAngle(degrees / angle, angle * MathF.PI / 180);
        return new(current.Position + offset, Quaternion.Normalize(current.Rotation * turn));
    }
}
