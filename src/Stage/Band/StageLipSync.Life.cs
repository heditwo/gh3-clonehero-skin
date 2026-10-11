using System.Numerics;

namespace Gh3Skin;

// The face around the mouth while the singer sings: the brows rise on long and high notes, the eyes narrow and the brows
// lower on loud singing (with a vocal stem), the eyes look around with saccades, and the head nods a little at phrase
// starts and accents. The brow and squint poses and the eye motion are measured from the GH3 singer facial clips
// (tools/lipsync-shapes.py); GH3 does not move the head with the voice, so the nods are tuned. The head and the eyes
// also move a character on its own skeleton (the retarget maps them).
internal sealed partial class StageLipSync
{
    private static readonly string[] LifeBones =
    {
        "Bone_Head", "Bone_Eye_L", "Bone_Eye_R", "Bone_Brow_L", "Bone_Brow_R", "Bone_Brow_Mid", "Bone_Brow_Mid_L", "Bone_Brow_Mid_R",
        "Bone_Eyelid_Lower_L", "Bone_Eyelid_Lower_R", "Bone_Cheek_Upper_L", "Bone_Cheek_Upper_R"
    };
    private const int HeadBone = 0;
    private const int FirstEye = 1;
    private const int Eyes = 2;
    private const int FirstBrow = 3;
    private const int Brows = 5;
    private const int FirstSquint = 8;
    private const int SquintBones = 4;
    private readonly Life _life;

    public float BrowAt(double time) => _life.Brow(time, Loudness(time));
    public float SquintAt(double time) => _life.Squint(Loudness(time));

    private sealed class Life
    {
        // Tuned: the brows rise fully on a note of LongNote + LongRange seconds, or on a note PitchRange semitones above
        // the 70th percentile pitch of the song; they rise over 0.15 s and fall over 0.5 s. A slow small motion
        // (BrowIdle, two incommensurate frequencies) keeps them alive.
        private const double LongNote = 0.5;
        private const double LongRange = 1;
        private const double HighPercentile = 0.7;
        private const double MinimumPitchRange = 3;
        private const double PitchRangePercentile = 0.95;
        private const double BrowRise = 0.15;
        private const double BrowFall = 0.5;
        private const float BrowIdle = 0.12f;
        private const double BrowIdleFrequencyA = 0.23;
        private const double BrowIdleFrequencyB = 0.37;
        private const int TrackRate = 30;
        // Tuned: belting (stem loudness above BeltStart, full at BeltStart + BeltRange) narrows the eyes and lowers the brows.
        private const float BeltStart = 0.9f;
        private const float BeltRange = 0.4f;
        private const float BeltFrown = 0.6f;
        private const float SquintMaximum = 0.8f;
        // Measured: saccades come every 0.7 s (p10), 1.83 s (p50), and 3.03 s (p90); the eyes look between -19.6 and 11.8
        // degrees left and right and +-7 degrees up and down (p5 and p95 of the GH3 facial clips). Tuned: a saccade takes
        // 0.05 s; half of them go back to the centre, and the range is 40 % of the GH3 range (large anime eyes show a
        // small turn clearly).
        private static readonly double[] SaccadeIntervals = { 0.4, 0.7, 1.83, 3.03, 4 };
        private const float LookLeft = -19.6f;
        private const float LookRight = 11.8f;
        private const float LookVertical = 7;
        private const float LookScale = 0.4f;
        private const double SaccadeSeconds = 0.05;
        private const int RandomSteps = 10000;
        private const double CentreChance = 0.5;
        private const double LifeMargin = 5;
        // Tuned: a nod turns the head down by NodDegrees (PhraseNodDegrees at a phrase start) over 0.12 s and back over
        // 0.4 s; an accent is a note longer than AccentNote.
        private const float NodDegrees = 1.8f;
        private const float PhraseNodDegrees = 3;
        private const double NodDown = 0.12;
        private const double NodBack = 0.4;
        private const double AccentNote = 0.5;
        private const double PhraseGapSeconds = PhraseGap;

        private readonly int[] _bones;
        private readonly float[] _brow;
        private readonly double _trackStart;
        private readonly (double Time, float Yaw, float Pitch)[] _looks;
        private readonly (double Time, float Degrees)[] _nods;

        public Life(Note[] notes, int[] bones)
        {
            _bones = bones;
            if (notes.Length == 0)
            {
                _brow = Array.Empty<float>();
                _looks = Array.Empty<(double, float, float)>();
                _nods = Array.Empty<(double, float)>();
                return;
            }
            _trackStart = notes[0].Start - LifeMargin;
            _brow = BrowTrack(notes, _trackStart);
            _looks = Looks(_trackStart, notes[^1].End + LifeMargin);
            _nods = Nods(notes);
        }

        private static float[] BrowTrack(Note[] notes, double start)
        {
            var pitches = notes.Where(n => n.Pitch > 0).Select(n => (double)n.Pitch).OrderBy(p => p).ToArray();
            var high = pitches.Length == 0 ? double.MaxValue : pitches[(int)((pitches.Length - 1) * HighPercentile)];
            var range = pitches.Length == 0 ? 1 : Math.Max(MinimumPitchRange, pitches[(int)((pitches.Length - 1) * PitchRangePercentile)] - high);
            var length = (int)((notes[^1].End + LifeMargin - start) * TrackRate);
            var track = new float[Math.Clamp(length, 0, StageLimits.MaximumSeconds * TrackRate)];
            var rise = 1 - Math.Exp(-1 / (BrowRise * TrackRate));
            var fall = 1 - Math.Exp(-1 / (BrowFall * TrackRate));
            double level = 0;
            var index = 0;
            for (var frame = 0; frame < track.Length; frame++)
            {
                var time = start + frame / (double)TrackRate;
                while (index < notes.Length && notes[index].End < time) index++;
                double target = 0;
                if (index < notes.Length && notes[index].Start <= time)
                {
                    var note = notes[index];
                    var longNote = Math.Clamp((note.End - note.Start - LongNote) / LongRange, 0, 1);
                    var highNote = note.Pitch > 0 ? Math.Clamp((note.Pitch - high) / range, 0, 1) : 0;
                    target = Math.Min(1, longNote + highNote);
                }
                level += (target - level) * (target > level ? rise : fall);
                track[frame] = (float)level;
            }
            return track;
        }

        private static (double, float, float)[] Looks(double start, double end)
        {
            var looks = new List<(double, float, float)>();
            var seed = XorShift32.GoldenRatioIncrement;
            double Random() => XorShift32.Next(ref seed) % RandomSteps / (double)RandomSteps;
            for (var time = start; time < end && looks.Count < StageLimits.MaximumCount;)
            {
                var centre = Random() < CentreChance;
                var yaw = centre ? 0 : (float)(LookLeft + (LookRight - LookLeft) * Random()) * LookScale;
                var pitch = centre ? 0 : (float)((Random() * 2 - 1) * LookVertical) * LookScale;
                looks.Add((time, yaw, pitch));
                var at = Random() * (SaccadeIntervals.Length - 1);
                var step = (int)at;
                time += SaccadeIntervals[step] + (SaccadeIntervals[step + 1] - SaccadeIntervals[step]) * (at - step);
            }
            return looks.ToArray();
        }

        private static (double, float)[] Nods(Note[] notes)
        {
            var nods = new List<(double, float)>();
            for (var i = 0; i < notes.Length; i++)
            {
                var phraseStart = i == 0 || notes[i].Start - notes[i - 1].End > PhraseGapSeconds;
                if (phraseStart) nods.Add((notes[i].Start - Lead, PhraseNodDegrees));
                else if (notes[i].End - notes[i].Start > AccentNote) nods.Add((notes[i].Start - Lead, NodDegrees));
            }
            return nods.ToArray();
        }

        public float Brow(double time, float loudness)
        {
            var position = (time - _trackStart) * TrackRate;
            float raise = 0;
            if (position >= 0 && position < _brow.Length - 1)
            {
                var index = (int)position;
                raise = _brow[index] + (_brow[index + 1] - _brow[index]) * (float)(position - index);
                // The idle motion only lifts the brows: a lowered brow reads as a frown on a face at rest.
                raise += BrowIdle * (float)Math.Max(0, Math.Sin(time * BrowIdleFrequencyA * 2 * Math.PI) * Math.Sin(time * BrowIdleFrequencyB * 2 * Math.PI));
            }
            return raise - BeltFrown * Belt(loudness);
        }

        public float Squint(float loudness) => SquintMaximum * Belt(loudness);

        private static float Belt(float loudness) => loudness < 0 ? 0 : Math.Clamp((loudness - BeltStart) / BeltRange, 0, 1);

        public (float Yaw, float Pitch) Look(double time)
        {
            var index = StageTimeline.UpperBound(_looks, time, l => l.Time) - 1;
            if (index < 0) return (0, 0);
            var current = _looks[index];
            var previous = index > 0 ? _looks[index - 1] : (current.Time, 0f, 0f);
            var t = (float)Math.Clamp((time - current.Time) / SaccadeSeconds, 0, 1);
            t = t * t * (3 - 2 * t);
            return (previous.Item2 + (current.Yaw - previous.Item2) * t, previous.Item3 + (current.Pitch - previous.Item3) * t);
        }

        public float Nod(double time)
        {
            float degrees = 0;
            for (var i = StageTimeline.UpperBound(_nods, time, n => n.Time) - 1; i >= 0 && time - _nods[i].Time < NodDown + NodBack; i--)
            {
                var age = time - _nods[i].Time;
                var x = age < NodDown ? age / NodDown : 1 - (age - NodDown) / NodBack;
                x = Math.Clamp(x, 0, 1);
                degrees += _nods[i].Degrees * (float)(x * x * (3 - 2 * x));
            }
            return degrees;
        }

        public void Apply(BandPose[] pose, double time, float loudness)
        {
            if (_bones.Length == 0) return;
            var nod = Nod(time);
            if (_bones[HeadBone] >= 0 && nod > 0) pose[_bones[HeadBone]] = Add(pose[_bones[HeadBone]], new Vector3(0, 0, nod), Vector3.Zero);
            var (yaw, pitch) = Look(time);
            for (var i = FirstEye; i < FirstEye + Eyes; i++)
                if (_bones[i] >= 0) pose[_bones[i]] = Add(pose[_bones[i]], new Vector3(yaw, 0, pitch), Vector3.Zero);
            var brow = Brow(time, loudness);
            var table = brow >= 0 ? BrowRaisePose : BrowLowerPose;
            Layer(pose, FirstBrow, Brows, table, Math.Abs(brow));
            Layer(pose, FirstSquint, SquintBones, SquintPose, Squint(loudness));
        }

        private void Layer(BandPose[] pose, int first, int count, float[] table, float weight)
        {
            if (weight < MinimumWeight) return;
            for (var i = 0; i < count; i++)
            {
                var bone = _bones[first + i];
                if (bone < 0) continue;
                var k = BoneValues * i;
                pose[bone] = Add(pose[bone], new Vector3(table[k], table[k + 1], table[k + 2]) * weight,
                    new Vector3(table[k + 3], table[k + 4], table[k + 5]) * weight);
            }
        }
    }
}
