using UnityEngine;
using Gh3Skin.Packages;

namespace Gh3Skin;

// A retargeted character on stage (PACKAGE-FORMAT.md section 9.6): the GH3 rig of the animation set plays the GH3
// performance without a mesh; CharacterRetarget moves the character's skeleton (its own skeleton, or its GH3 rig in another
// role) from it each frame. The instrument stays on the GH3 rig, moved and scaled to fit the character. Camera and
// hand-fire anchors use a frame on the mapped character bone that turns as the GH3 bone turns.
internal sealed class StageRetarget
{
    public CharacterRetarget Rig { get; }
    public BandAnimation Source { get; }
    public Transform[] SourceBones { get; }
    public BandPose[] SourcePose { get; }
    public CharacterFace? Face { get; set; }
    // A GH3 rig as the character's skeleton: the GH3 twist and split bone constraints on its pose (StageGuitarRig).
    public StageGuitarRig? Constraints { get; init; }
    private readonly Transform?[] _anchors;
    private bool _scaled;

    public StageRetarget(CharacterRetarget rig, BandAnimation source, Transform[] sourceBones, Transform[] targetBones, Transform root, int layer)
    {
        Rig = rig;
        Source = source;
        SourceBones = sourceBones;
        SourcePose = (BandPose[])source.BindPose.Clone();
        _anchors = new Transform?[sourceBones.Length];
        for (var bone = 0; bone < targetBones.Length; bone++)
        {
            var gh3 = rig.MappedSource(bone);
            if (gh3 < 0 || _anchors[gh3] != null) continue;
            var frame = new GameObject("GH3 Frame " + bone) { layer = layer }.transform;
            frame.SetParent(targetBones[bone], false);
            var inverse = System.Numerics.Quaternion.Inverse(rig.Correction(bone));
            frame.localRotation = new Quaternion(inverse.X, inverse.Y, inverse.Z, inverse.W);
            _anchors[gh3] = frame;
        }
        var attached = new HashSet<int>(rig.AttachmentBones);
        for (var bone = 0; bone < sourceBones.Length; bone++)
        {
            if (_anchors[bone] != null) continue;
            if (attached.Contains(bone)) { _anchors[bone] = sourceBones[bone]; continue; }
            // A GH3 bone without a character bone (the face, the twist bones) uses the frame of its nearest mapped parent.
            for (var parent = source.Parents[bone]; parent >= 0; parent = source.Parents[parent])
                if (_anchors[parent] != null && !attached.Contains(parent)) { _anchors[bone] = _anchors[parent]; break; }
            _anchors[bone] ??= root;
        }
    }

    public Transform Anchor(int sourceBone) => _anchors[sourceBone]!;

    // The GH3 bones of the moved instrument (the rest of the GH3 rig has no mesh).
    public void ApplySource()
    {
        var pose = Rig.SourceOut;
        foreach (var bone in Rig.AttachmentBones)
        {
            var p = pose[bone];
            SourceBones[bone].localPosition = new Vector3(p.Position.X, p.Position.Y, p.Position.Z);
            SourceBones[bone].localRotation = new Quaternion(p.Rotation.X, p.Rotation.Y, p.Rotation.Z, p.Rotation.W);
        }
        if (!_scaled)
        {
            _scaled = true;
            foreach (var (bone, scale) in Rig.Scales)
                SourceBones[bone].localScale = new Vector3(scale, scale, scale);
        }
    }
}

// Face blend shapes of a character with its own skeleton (character.json face): blink with the GH3 blink timing, the
// mouth, viseme, brow, and squint shapes of the lip sync of the singer (StageLipSync), or the mouth and the eyelids of
// the GH3 facial clips when the song has them.
internal sealed class CharacterFace
{
    // Blink times cover the song plus 30 s, at least 10 minutes.
    private const int MinimumBlinkSeconds = 600;
    private const int BlinkTail = 30;
    // Tuned: the GH3 jaw turn (degrees) of a fully open mouth.
    private const int OpenJawDegrees = 24;
    // Measured (570 GH3 singer facial clips, the upper eyelid turn from the bind pose): the open lid is at 9.3° (median)
    // and GH3 holds it at 10-20° for seconds as an expression; the blinks start above 20° (80th percentile) and reach
    // 26° (median peak of the blinks); held closed eyes are at 31.5° (median). A blink shape closes the eyes fully, so
    // only the turn past 20° closes it: a lowered GH3 lid does not give half-closed eyes.
    private const int LidStartDegrees = 20;
    private const int LidRangeDegrees = 6;
    // Blend shape weights that change less than this are not set again.
    private const float WeightChange = 0.002f;
    private readonly List<(SkinnedMeshRenderer Renderer, int[] Slots)> _shapes;
    private readonly float[] _weights = new float[CharacterSkeleton.FaceSlots.Length];
    private readonly float[] _applied;
    private readonly bool[] _present;
    private readonly int _jaw;
    private readonly int[] _lids;
    private readonly BandPose[] _bind;
    private double[] _blinks = Array.Empty<double>();
    private StageLipSync? _lipSync;
    private bool _facialClips;
    public string Summary { get; }

    public CharacterFace(List<(SkinnedMeshRenderer, int[])> shapes, string[] gh3Bones, BandPose[] gh3Bind, string[] faceNames)
    {
        _shapes = shapes;
        _applied = Enumerable.Repeat(-1f, _weights.Length).ToArray();
        _bind = gh3Bind;
        _jaw = Array.IndexOf(gh3Bones, Gh3Checksum.Of(Packages.HumanoidRoles.Gh3Jaw));
        _lids = Packages.HumanoidRoles.Gh3Lids.Select(n => Array.IndexOf(gh3Bones, Gh3Checksum.Of(n))).Where(i => i >= 0).ToArray();
        _present = CharacterSkeleton.FaceSlots.Select((slot, i) => i < faceNames.Length && faceNames[i].Length > 0 && shapes.Any(s => s.Item2[i] >= 0)).ToArray();
        Summary = string.Join(", ", CharacterSkeleton.FaceSlots.Where((slot, i) => _present[i]));
    }

    public void Prepare(StageTimeline timeline, string role)
    {
        var end = Math.Max(MinimumBlinkSeconds, (timeline.Cues.LastOrDefault()?.Time ?? 0) + BlinkTail);
        _blinks = StageLipSync.BlinkTimes(timeline.Start + 1, end, Gh3Checksum.Value(role) ^ XorShift32.GoldenRatioIncrement);
        _lipSync = StageLipSync.Shapes(timeline, role);
        _facialClips = timeline.Track("face", role).Length > 0;
    }

    public bool LipSync => _lipSync != null;

    // The applied weights of the face slots that the character has (GH3_SKIN_TRACE).
    public string Trace => string.Join(",", CharacterSkeleton.FaceSlots.Where((slot, i) => _present[i])
        .Select((slot, i) => FormattableString.Invariant($"{slot}:{Math.Max(0, _applied[Array.IndexOf(CharacterSkeleton.FaceSlots, slot)]):F3}")));

    public void Update(double time, BandPose[] gh3Pose)
    {
        Array.Clear(_weights, 0, _weights.Length);
        if (_facialClips)
        {
            // The GH3 facial clip opens the jaw and closes the eyelids; the shapes follow the angles from the rest pose.
            if (_jaw >= 0) _weights[1] = Math.Clamp(Turn(gh3Pose, _jaw) / OpenJawDegrees, 0, 1);
            foreach (var lid in _lids) _weights[0] = Math.Max(_weights[0], Math.Clamp((Turn(gh3Pose, lid) - LidStartDegrees) / LidRangeDegrees, 0, 1));
        }
        else
        {
            _weights[0] = StageLipSync.BlinkAt(_blinks, time);
            _lipSync?.Face(time, _weights, _present);
        }
        for (var slot = 0; slot < _weights.Length; slot++)
        {
            if (Math.Abs(_weights[slot] - _applied[slot]) < WeightChange) continue;
            _applied[slot] = _weights[slot];
            foreach (var (renderer, slots) in _shapes)
                if (slots[slot] >= 0) renderer.SetBlendShapeWeight(slots[slot], _weights[slot] * 100);
        }
    }

    private float Turn(BandPose[] pose, int bone)
    {
        var dot = Math.Abs(System.Numerics.Quaternion.Dot(pose[bone].Rotation, _bind[bone].Rotation));
        return 2 * MathF.Acos(Math.Min(1, dot)) * 180 / MathF.PI;
    }
}
