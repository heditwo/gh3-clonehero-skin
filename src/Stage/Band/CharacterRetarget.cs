using System.Numerics;
using Gh3Skin.Packages;

namespace Gh3Skin;

// A character skeleton for the retarget: the GLB skin joints in runtime coordinates (local bind poses), the humanoid
// map (role to joint), and the follower rows (MMD append rotation: a bone takes `Weight` times the rotation of `Source`
// from its rest pose).
internal sealed class RetargetSkeleton
{
    public string[] Bones { get; init; } = Array.Empty<string>();
    public int[] Parents { get; init; } = Array.Empty<int>();
    public BandPose[] Bind { get; init; } = Array.Empty<BandPose>();
    public Dictionary<string, int> Humanoid { get; init; } = new(StringComparer.Ordinal);
    public (int Bone, int Source, float Weight)[] Followers { get; init; } = Array.Empty<(int, int, float)>();
    // The bone that carries the instrument (-1: the top spine bone) and an offset in its GH3 frame.
    public int InstrumentBone { get; init; } = -1;
    public Vector3 InstrumentOffset { get; init; }
    // The instrument size (0: the arm length ratio).
    public float InstrumentScale { get; init; }
}

// Rotation-only retarget of a GH3 performance (local poses on a GH3 rig) to a character skeleton (PACKAGE-FORMAT.md
// section 9.6). Each mapped bone takes the world rotation of its GH3 bone from the GH3 rest pose, applied to its own rest
// pose turned so that its bone points as the GH3 bone points at rest (rest-pose correction: A-pose, T-pose, bone rolls).
// The hips move as the GH3 pelvis moves, scaled by the hip height. Unmapped bones keep their rest pose relative to their
// parent. The forearm twist goes to the twist bone (or half of it to the forearm), the upper-arm twist to its twist bone.
// The instrument moves with the chest (its offset scaled by the torso length), and two-bone IK with the character's own
// arm and leg lengths puts the hands on the instrument and the feet on the floor. No allocations per call.
internal sealed class CharacterRetarget
{
    // A hip height or torso length (metres) below this is not a usable measure; the scale stays 1 (or the hip scale).
    private const float MinimumBodyLength = 0.05f;
    private const float MinimumArmLength = 0.1f;
    // Tuned: the instrument scale (arm length ratio) stays in 0.5-1.5.
    private const float MinimumInstrumentScale = 0.5f;
    private const float MaximumInstrumentScale = 1.5f;
    private const float MinimumPathLength = 1e-9f;
    // Tuned: the microphone follows the nearer hand from 25 cm, fully at 10 cm.
    private const float HandFollowStart = 0.25f;
    private const float HandFollowRange = 0.15f;
    // Tuned: the instrument moves so that each hand reaches it at 98 % of the arm length.
    private const float ReachShare = 0.98f;
    // Two-bone IK: bones shorter than this are degenerate; the goal stays this far inside full reach; a pole shorter
    // than PoleLength gives no bend direction.
    private const float MinimumBoneLength = 1e-4f;
    private const float FullReach = 0.9999f;
    private const float PoleLength = 0.005f;
    private const float MinimumSine = 1e-7f;
    private const float OppositeDot = -0.999999f;
    private readonly RetargetSkeleton _target;
    private readonly int[] _sourceParents;
    private readonly int[] _sourceOrder;
    private readonly int[] _order;
    private readonly int[] _map;
    private readonly Quaternion[] _correction;
    private readonly Vector3[] _restPosition;
    private readonly Quaternion[] _restRotation;
    private readonly Vector3[] _sourceRestPosition;
    private readonly Quaternion[] _sourceRestRotation;
    private readonly Vector3[] _sp;
    private readonly Quaternion[] _sq;
    private readonly Vector3[] _wp;
    private readonly Quaternion[] _wq;
    private readonly Quaternion[] _desired;
    private readonly bool[] _driven;
    private readonly int _rootMotion;
    private readonly int _sourcePelvis;
    public float HipScale { get; }
    public float TorsoScale { get; }
    // The instrument size and its distance from the chest: the arm length ratio, so that the hands reach the same places
    // on the instrument (GH3 scales a band member with its instrument; create_guitarist scale).
    public float InstrumentScale { get; }
    private readonly int _chestSource;
    private readonly int _chestTarget;
    // Twist bones: the role bone, its twist bone, and the bone after it (lower arm and hand).
    private readonly (int Upper, int Twist, int Next, Vector3 Axis)[] _upperTwist;
    private readonly (int Lower, int Twist, int Hand, Vector3 Axis, Quaternion HandRest)[] _lowerTwist;
    private readonly (int Bone, int Source, float Weight, Quaternion Frame)[] _followers;
    private readonly int[] _followerOf;
    private readonly (int Upper, int Lower, float Fraction)[] _between;
    private readonly (int Upper, int Lower, int End, int SourceEnd, int Knuckle, Vector3 KnuckleOffset, int Side, float Reach, int SourceUpper, int SourceLower)[] _arms;
    private readonly (int Upper, int Lower, int End, int SourceEnd, int SourceUpper, float Lift, int SourceLower)[] _legs;
    private readonly (int Root, int Mode)[] _attachments;
    private readonly int[] _palms = new int[2];
    private readonly int[] _hands = new int[2];
    // The local poses of the GH3 rig with the instrument moved (only the attachment roots change).
    public BandPose[] SourceOut { get; }
    // The rig indices of the GH3 bones that follow the moved instrument, parents first, and the instrument roots that
    // take InstrumentScale.
    public int[] AttachmentBones { get; }
    public int[] InstrumentRoots { get; }
    // The largest distance (m) between an IK hand target and the hand in the last call, for the log.
    public float HandMiss { get; private set; }
    public Vector3 InstrumentMove { get; private set; }

    public const int ModeChest = 1, ModeHand = 2;

    public CharacterRetarget(string[] sourceBones, int[] sourceParents, BandPose[] sourceBind, RetargetSkeleton target, string role)
    {
        _target = target;
        _sourceParents = sourceParents;
        var count = target.Bones.Length;
        _sourceOrder = Order(sourceParents, null);
        var sourceIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < sourceBones.Length; i++) sourceIndex.TryAdd(sourceBones[i], i);
        int Source(string checksum) => sourceIndex.TryGetValue(checksum, out var index) ? index : -1;
        var (sourceRestP, sourceRestQ) = World(sourceParents, sourceBind, _sourceOrder);
        _sourceRestPosition = sourceRestP;
        _sourceRestRotation = sourceRestQ;
        var restOrder = Order(target.Parents, null);
        (_restPosition, _restRotation) = World(target.Parents, target.Bind, restOrder);

        // Role to GH3 bone: the fixed table and the spine bones for the spine roles that the skeleton has.
        var roles = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (role2, checksum) in HumanoidRoles.Gh3) roles[role2] = Source(checksum);
        var spine = HumanoidRoles.Spine.Where(target.Humanoid.ContainsKey).ToArray();
        var spineSources = HumanoidRoles.SpineSources(spine.Length);
        for (var i = 0; i < spine.Length; i++) roles[spine[i]] = Source(spineSources[i]);
        _map = Enumerable.Repeat(-1, count).ToArray();
        foreach (var (role2, bone) in target.Humanoid)
            if (roles.TryGetValue(role2, out var source) && source >= 0 && bone >= 0 && bone < count) _map[bone] = source;
        int T(string role2) => target.Humanoid.TryGetValue(role2, out var bone) ? bone : -1;
        int S(string role2) => roles.TryGetValue(role2, out var bone) ? bone : -1;

        // Rest-pose correction: the turn that points each target bone along its GH3 bone, then C = inv(source rest) * turn * target rest.
        var align = new Quaternion[count];
        var aligned = new bool[count];
        foreach (var (role2, bone) in target.Humanoid)
        {
            if (bone < 0 || bone >= count || _map[bone] < 0) continue;
            var next = HumanoidRoles.Next(role2, r => T(r) >= 0 && S(r) >= 0);
            if (next == null || T(next) < 0 || S(next) < 0) continue;
            var from = _restPosition[T(next)] - _restPosition[bone];
            var to = sourceRestP[S(next)] - sourceRestP[_map[bone]];
            if (from.LengthSquared() < StageTolerance.ZeroLengthSquared || to.LengthSquared() < StageTolerance.ZeroLengthSquared) continue;
            if (role2.EndsWith("Hand", StringComparison.Ordinal) && HumanoidRoles.PalmLine(role2[..^4], r => T(r) >= 0 && S(r) >= 0) is var (first, last))
            {
                var (ti, tl, si, sl) = (T(first), T(last), S(first), S(last));
                if (ti >= 0 && tl >= 0 && si >= 0 && sl >= 0)
                {
                    align[bone] = FrameTurn(from, _restPosition[ti] - _restPosition[tl], to, sourceRestP[si] - sourceRestP[sl]);
                    aligned[bone] = true;
                    continue;
                }
            }
            align[bone] = FromTo(Vector3.Normalize(from), Vector3.Normalize(to));
            aligned[bone] = true;
        }
        _correction = new Quaternion[count];
        var keepsFrame = new HashSet<string> { "hips", "head", "jaw", "leftEye", "rightEye" };
        foreach (var bone in restOrder)
        {
            if (_map[bone] < 0) continue;
            if (!aligned[bone])
            {
                // A bone without a direction (a finger tip, the toes) takes the turn of its nearest mapped parent.
                var role2 = target.Humanoid.First(pair => pair.Value == bone).Key;
                if (!keepsFrame.Contains(role2))
                    for (var parent = target.Parents[bone]; parent >= 0; parent = target.Parents[parent])
                        if (aligned[parent]) { align[bone] = align[parent]; break; }
                if (align[bone] == default) align[bone] = Quaternion.Identity;
                aligned[bone] = true;
            }
            _correction[bone] = Quaternion.Normalize(Quaternion.Inverse(sourceRestQ[_map[bone]]) * align[bone] * _restRotation[bone]);
        }

        // Root motion: the lowest common parent of the hips, the spine, and the legs takes the pelvis translation.
        _sourcePelvis = S("hips");
        var group = new[] { T("hips"), T("spine"), T("leftUpperLeg"), T("rightUpperLeg") }.Where(b => b >= 0).ToArray();
        _rootMotion = group.Length == 0 ? -1 : group.Aggregate((a, b) => Common(target.Parents, a, b));
        float Height(string role2, Vector3[] positions, Func<string, int> index) => index(role2) >= 0 ? positions[index(role2)].Y : float.NaN;
        var targetHip = (Height("leftUpperLeg", _restPosition, T) + Height("rightUpperLeg", _restPosition, T)) / 2;
        var sourceHip = (Height("leftUpperLeg", sourceRestP, S) + Height("rightUpperLeg", sourceRestP, S)) / 2;
        HipScale = float.IsFinite(targetHip / sourceHip) && targetHip > MinimumBodyLength && sourceHip > MinimumBodyLength ? targetHip / sourceHip : 1;
        var top = spine.Length > 0 ? spine[^1] : "spine";
        _chestTarget = target.InstrumentBone >= 0 ? target.InstrumentBone : T(top);
        _chestSource = Source(Gh3Checksum.Of("bone_chest"));
        var neckT = T("neck") >= 0 ? T("neck") : T("head");
        var neckS = S("neck") >= 0 ? S("neck") : S("head");
        var hipsT = (Position(_restPosition, T("leftUpperLeg")) + Position(_restPosition, T("rightUpperLeg"))) / 2;
        var hipsS = (Position(sourceRestP, S("leftUpperLeg")) + Position(sourceRestP, S("rightUpperLeg"))) / 2;
        var torsoT = neckT >= 0 ? Vector3.Distance(_restPosition[neckT], hipsT) : 0;
        var torsoS = neckS >= 0 ? Vector3.Distance(sourceRestP[neckS], hipsS) : 0;
        TorsoScale = torsoT > MinimumBodyLength && torsoS > MinimumBodyLength ? torsoT / torsoS : HipScale;
        float Arm(Vector3[] positions, Func<string, int> index, string side) =>
            index(side + "UpperArm") >= 0 && index(side + "LowerArm") >= 0 && index(side + "Hand") >= 0 ?
                Vector3.Distance(positions[index(side + "UpperArm")], positions[index(side + "LowerArm")]) +
                Vector3.Distance(positions[index(side + "LowerArm")], positions[index(side + "Hand")]) : 0;
        var armT = Arm(_restPosition, T, "left") + Arm(_restPosition, T, "right");
        var armS = Arm(sourceRestP, S, "left") + Arm(sourceRestP, S, "right");
        InstrumentScale = target.InstrumentScale > 0 ? target.InstrumentScale : armT > MinimumArmLength && armS > MinimumArmLength ? Math.Clamp(armT / armS, MinimumInstrumentScale, MaximumInstrumentScale) : 1;

        // Twist bones.
        var upper = new List<(int, int, int, Vector3)>();
        var lower = new List<(int, int, int, Vector3, Quaternion)>();
        foreach (var side in new[] { "left", "right" })
        {
            var (u, ut, l, lt, h) = (T(side + "UpperArm"), T(side + "UpperArmTwist"), T(side + "LowerArm"), T(side + "LowerArmTwist"), T(side + "Hand"));
            if (u >= 0 && ut >= 0 && l >= 0 && target.Parents[ut] == u && IsAbove(target.Parents, ut, l))
                upper.Add((u, ut, l, Vector3.Normalize(Vector3.Transform(_restPosition[l] - _restPosition[u], Quaternion.Inverse(_restRotation[u])))));
            if (l >= 0 && h >= 0 && IsAbove(target.Parents, l, h))
            {
                var axis = Vector3.Normalize(Vector3.Transform(_restPosition[h] - _restPosition[l], Quaternion.Inverse(_restRotation[l])));
                var rest = Quaternion.Normalize(Quaternion.Inverse(_restRotation[l]) * _restRotation[h]);
                var twist = lt >= 0 && IsAbove(target.Parents, l, lt) && IsAbove(target.Parents, lt, h) ? lt : -1;
                lower.Add((l, twist, h, axis, rest));
            }
        }
        _upperTwist = upper.ToArray();
        _lowerTwist = lower.ToArray();

        // Joints without a role between two torso roles turn part of the way, so a long spine or neck bends along its length.
        _between = Enumerable.Repeat((-1, -1, 0f), count).ToArray();
        var torso = new[] { "hips", "spine", "chest", "upperChest", "neck", "head" }.Select(T).Where(b => b >= 0).ToArray();
        foreach (var below in torso)
        {
            var path = new List<int>();
            var x = target.Parents[below];
            while (x >= 0 && Array.IndexOf(torso, x) < 0) { path.Add(x); x = target.Parents[x]; }
            if (x < 0 || path.Count == 0 || _map[x] < 0 || _map[below] < 0) continue;
            path.Reverse();
            var points = new List<Vector3> { _restPosition[x] };
            points.AddRange(path.Select(b => _restPosition[b]));
            points.Add(_restPosition[below]);
            var along = new float[points.Count];
            for (var i = 1; i < points.Count; i++) along[i] = along[i - 1] + Vector3.Distance(points[i - 1], points[i]);
            var total = along[^1] > MinimumPathLength ? along[^1] : 1;
            for (var i = 0; i < path.Count; i++)
                if (_map[path[i]] < 0) _between[path[i]] = (x, below, along[i + 1] / total);
        }

        // Followers: the source rotation from rest, turned into the follower's rest frame.
        _followers = target.Followers.Where(f => f.Bone >= 0 && f.Bone < count && f.Source >= 0 && f.Source < count && _map[f.Bone] < 0 && f.Bone != f.Source)
            .Select(f => (f.Bone, f.Source, f.Weight, Quaternion.Normalize(Quaternion.Inverse(_restRotation[f.Bone]) * _restRotation[f.Source]))).ToArray();
        _followerOf = Enumerable.Repeat(-1, count).ToArray();
        for (var i = 0; i < _followers.Length; i++) _followerOf[_followers[i].Bone] = i;
        _order = Order(target.Parents, _followers.Select(f => (f.Bone, f.Source)).ToArray());

        // IK chains with the character's own lengths.
        var arms = new List<(int, int, int, int, int, Vector3, int, float, int, int)>();
        var legs = new List<(int, int, int, int, int, float, int)>();
        foreach (var (side, sign) in new[] { ("left", -1), ("right", 1) })
        {
            var (u, l, h, s) = (T(side + "UpperArm"), T(side + "LowerArm"), T(side + "Hand"), S(side + "Hand"));
            if (u >= 0 && l >= 0 && h >= 0 && s >= 0 && IsAbove(target.Parents, u, l) && IsAbove(target.Parents, l, h))
            {
                var finger = HumanoidRoles.Knuckle(side, r => T(r) >= 0 && S(r) >= 0);
                var knuckle = finger != null ? S(finger) : -1;
                var tk = finger != null ? T(finger) : -1;
                var offset = tk >= 0 ? Vector3.Transform(_restPosition[tk] - _restPosition[h], Quaternion.Inverse(_restRotation[h])) : Vector3.Zero;
                var reach = Vector3.Distance(_restPosition[u], _restPosition[l]) + Vector3.Distance(_restPosition[l], _restPosition[h]);
                arms.Add((u, l, h, s, tk >= 0 ? knuckle : -1, offset, sign, reach, S(side + "UpperArm"), S(side + "LowerArm")));
            }
            var (ul, ll, f, sf) = (T(side + "UpperLeg"), T(side + "LowerLeg"), T(side + "Foot"), S(side + "Foot"));
            var su = S(side + "UpperLeg");
            if (ul >= 0 && ll >= 0 && f >= 0 && sf >= 0 && su >= 0 && IsAbove(target.Parents, ul, ll) && IsAbove(target.Parents, ll, f))
            {
                // The foot goes where the GH3 foot is from the GH3 hip, scaled by the hip height; the lift keeps the
                // character's own ankle height at rest (GH3 rigs have higher ankles than many models).
                var lift = _restPosition[f].Y - (_restPosition[ul].Y + (sourceRestP[sf].Y - sourceRestP[su].Y) * HipScale);
                legs.Add((ul, ll, f, sf, su, lift, S(side + "LowerLeg")));
            }
        }
        _arms = arms.ToArray();
        _legs = legs.ToArray();
        _palms[0] = S("leftHand"); _palms[1] = S("rightHand");
        _hands[0] = T("leftHand"); _hands[1] = T("rightHand");

        // Attachments: the instrument of a guitarist or bassist moves with the chest; the singer's microphone follows the
        // nearer hand while the GH3 hand holds it. The drum kit and the microphone stand stay where GH3 puts them.
        var attachments = new List<(int, int)>();
        if (role is "guitarist" or "bassist" && Source(Gh3Checksum.Of("bone_guitar_body")) is var guitar and >= 0) attachments.Add((guitar, ModeChest));
        if (role == "singer" && Source(Gh3Checksum.Of("bone_mic")) is var mic and >= 0) attachments.Add((mic, ModeHand));
        _attachments = _chestSource >= 0 && _chestTarget >= 0 ? attachments.ToArray() : Array.Empty<(int, int)>();
        var moved = new List<int>();
        foreach (var bone in _sourceOrder)
            foreach (var (root, _) in _attachments)
                if (IsAbove(sourceParents, root, bone) || bone == root) { moved.Add(bone); break; }
        AttachmentBones = moved.ToArray();
        InstrumentRoots = _attachments.Where(a => a.Mode == ModeChest).Select(a => a.Root).ToArray();

        SourceOut = new BandPose[sourceParents.Length];
        _sp = new Vector3[sourceParents.Length];
        _sq = new Quaternion[sourceParents.Length];
        _wp = new Vector3[count];
        _wq = new Quaternion[count];
        _desired = new Quaternion[count];
        _driven = new bool[count];
    }

    public int MappedSource(int bone) => _map[bone];
    public Quaternion SourceRestRotation(int sourceBone) => _sourceRestRotation[sourceBone];
    public int Role(string role) => _target.Humanoid.TryGetValue(role, out var bone) ? bone : -1;

    // The turn of a bone after the last Apply from its rest pose, relative to `reference` (-1: the performer space), as
    // a rotation vector in degrees in the rest frame of the performer.
    public Vector3 RestTurn(int bone, int reference)
    {
        var now = reference < 0 ? _wq[bone] : Quaternion.Inverse(_wq[reference]) * _wq[bone];
        var rest = reference < 0 ? _restRotation[bone] : Quaternion.Inverse(_restRotation[reference]) * _restRotation[bone];
        var turn = now * Quaternion.Inverse(rest);
        if (reference >= 0) turn = _restRotation[reference] * turn * Quaternion.Inverse(_restRotation[reference]);
        if (turn.W < 0) turn = new Quaternion(-turn.X, -turn.Y, -turn.Z, -turn.W);
        var sin = MathF.Sqrt(turn.X * turn.X + turn.Y * turn.Y + turn.Z * turn.Z);
        if (sin < MinimumSine) return Vector3.Zero;
        return new Vector3(turn.X, turn.Y, turn.Z) / sin * (2 * MathF.Atan2(sin, turn.W) * 180 / MathF.PI);
    }
    public Quaternion Correction(int bone) => _correction[bone];
    public int Count => _target.Bones.Length;

    // The world (performer space) position and rotation of a target bone after the last Apply.
    public BandPose World(int bone) => new(_wp[bone], _wq[bone]);
    public BandPose SourceWorld(int bone) => new(_sp[bone], _sq[bone]);

    // `source`: local poses of the GH3 rig; `target`: local poses of the skeleton (written). `right`, `left`: the arm IK
    // strength of the GH3 performance (strum and fret arm); legs always use IK.
    public void Apply(BandPose[] source, BandPose[] target, float right, float left)
    {
        foreach (var bone in _sourceOrder)
        {
            var parent = _sourceParents[bone];
            var local = source[bone];
            if (parent < 0) { _sp[bone] = local.Position; _sq[bone] = local.Rotation; continue; }
            _sp[bone] = _sp[parent] + Vector3.Transform(local.Position, _sq[parent]);
            _sq[bone] = Quaternion.Normalize(_sq[parent] * local.Rotation);
        }
        for (var bone = 0; bone < _driven.Length; bone++)
        {
            var source2 = _map[bone];
            _driven[bone] = source2 >= 0;
            if (_driven[bone]) _desired[bone] = Quaternion.Normalize(_sq[source2] * _correction[bone]);
        }
        var pelvisMove = _sourcePelvis >= 0 ? (_sp[_sourcePelvis] - _sourceRestPosition[_sourcePelvis]) * HipScale : Vector3.Zero;
        Forward(pelvisMove);

        // The instrument keeps its offset from the chest and its size, both scaled by the arm length ratio.
        Array.Copy(source, SourceOut, source.Length);
        var chestMove = Vector3.Zero;
        var instrument = -1;
        if (_attachments.Length > 0)
        {
            var offset = Vector3.Transform(_target.InstrumentOffset, _sq[_chestSource]);
            foreach (var (root, mode) in _attachments)
            {
                var moved = _wp[_chestTarget] + (_sp[root] - _sp[_chestSource]) * TorsoScale + offset - _sp[root];
                if (mode == ModeChest) moved += Reach(root, moved, left, right);
                if (mode == ModeHand)
                {
                    var best = float.MaxValue;
                    var hand = Vector3.Zero;
                    for (var k = 0; k < 2; k++)
                    {
                        if (_palms[k] < 0 || _hands[k] < 0) continue;
                        var distance = Vector3.Distance(_sp[_palms[k]], _sp[root]);
                        if (distance < best) { best = distance; hand = _wp[_hands[k]] - _sp[_palms[k]]; }
                    }
                    var weight = Smooth((HandFollowStart - best) / HandFollowRange);
                    moved = Vector3.Lerp(moved, hand, weight);
                }
                var parent = _sourceParents[root];
                var position = _sp[root] + moved;
                SourceOut[root] = new BandPose(parent < 0 ? position : Vector3.Transform(position - _sp[parent], Quaternion.Inverse(_sq[parent])), source[root].Rotation);
                if (mode == ModeChest) (chestMove, instrument) = (moved, root);
            }
        }
        InstrumentMove = chestMove;

        // Arms: the hand (its middle knuckle when the hand has fingers) where the GH3 hand is on the moved instrument.
        HandMiss = 0;
        var changed = false;
        foreach (var arm in _arms)
        {
            var gain = arm.Side < 0 ? left : right;
            if (gain <= 0) continue;
            var goal = Goal(arm.Knuckle >= 0 ? arm.Knuckle : arm.SourceEnd, instrument, chestMove);
            if (arm.Knuckle >= 0) goal -= Vector3.Transform(arm.KnuckleOffset, _desired[arm.End]);
            Solve(arm.Upper, arm.Lower, arm.End, goal, gain, Pole(arm.SourceUpper, arm.SourceLower, arm.SourceEnd));
            changed = true;
        }
        foreach (var leg in _legs)
        {
            var goal = _wp[leg.Upper] + (_sp[leg.SourceEnd] - _sp[leg.SourceUpper]) * HipScale;
            goal.Y += leg.Lift;
            Solve(leg.Upper, leg.Lower, leg.End, goal, 1, Pole(leg.SourceUpper, leg.SourceLower, leg.SourceEnd));
            changed = true;
        }
        if (changed) Forward(pelvisMove);
        foreach (var arm in _arms)
        {
            var gain = arm.Side < 0 ? left : right;
            if (gain <= 0) continue;
            var goal = Goal(arm.Knuckle >= 0 ? arm.Knuckle : arm.SourceEnd, instrument, chestMove);
            if (arm.Knuckle >= 0) goal -= Vector3.Transform(arm.KnuckleOffset, _wq[arm.End]);
            HandMiss = Math.Max(HandMiss, Vector3.Distance(_wp[arm.End], goal) * gain);
        }

        for (var bone = 0; bone < target.Length && bone < _wq.Length; bone++)
        {
            var parent = _target.Parents[bone];
            if (parent < 0) { target[bone] = new BandPose(_wp[bone], _wq[bone]); continue; }
            var inverse = Quaternion.Inverse(_wq[parent]);
            var position = bone == _rootMotion ? Vector3.Transform(_wp[bone] - _wp[parent], inverse) : _target.Bind[bone].Position;
            target[bone] = new BandPose(position, Quaternion.Normalize(inverse * _wq[bone]));
        }
    }

    // The shift of the instrument that brings the hand goals of the active arms within the reach of the character's arms
    // (a character with narrower shoulders than the GH3 rig must hold the neck nearer). It changes continuously with the
    // goals, so it needs no state.
    private Vector3 Reach(int instrument, Vector3 move, float left, float right)
    {
        var shift = Vector3.Zero;
        for (var pass = 0; pass < 2; pass++)
        {
            var step = Vector3.Zero;
            foreach (var arm in _arms)
            {
                var gain = arm.Side < 0 ? left : right;
                if (gain <= 0) continue;
                var goal = Goal(arm.Knuckle >= 0 ? arm.Knuckle : arm.SourceEnd, instrument, move + shift);
                if (arm.Knuckle >= 0) goal -= Vector3.Transform(arm.KnuckleOffset, _desired[arm.End]);
                var reach = goal - _wp[arm.Upper];
                var excess = reach.Length() - arm.Reach * ReachShare;
                if (excess > 0) step -= Vector3.Normalize(reach) * (excess * gain);
            }
            shift += step;
        }
        return shift;
    }

    // A GH3 hand point on the moved and scaled instrument (or moved with the chest when there is none).
    private Vector3 Goal(int sourceBone, int instrument, Vector3 move) =>
        instrument < 0 ? _sp[sourceBone] + move : _sp[instrument] + move + (_sp[sourceBone] - _sp[instrument]) * InstrumentScale;

    // World poses of the skeleton from the desired rotations of the driven bones, the rest pose of the others, the twist
    // bones, and the followers.
    private void Forward(Vector3 pelvisMove)
    {
        foreach (var bone in _order)
        {
            var parent = _target.Parents[bone];
            var rest = _target.Bind[bone];
            var parentP = parent < 0 ? Vector3.Zero : _wp[parent];
            var parentQ = parent < 0 ? Quaternion.Identity : _wq[parent];
            _wp[bone] = bone == _rootMotion ? _restPosition[bone] + pelvisMove : parentP + Vector3.Transform(rest.Position, parentQ);
            var rotation = _driven[bone] ? _desired[bone] : Quaternion.Normalize(parentQ * rest.Rotation);
            var (upperTorso, lowerTorso, fraction) = _between[bone];
            if (upperTorso >= 0)
            {
                var turnUpper = _desired[upperTorso] * Quaternion.Inverse(_restRotation[upperTorso]);
                var turnLower = _desired[lowerTorso] * Quaternion.Inverse(_restRotation[lowerTorso]);
                rotation = Quaternion.Normalize(turnUpper * Power(Quaternion.Inverse(turnUpper) * turnLower, fraction) * _restRotation[bone]);
            }
            var follower = _followerOf[bone];
            if (follower >= 0)
            {
                var (_, source, weight, frame) = _followers[follower];
                var sourceParent = _target.Parents[source];
                var sourceLocal = sourceParent < 0 ? _wq[source] : Quaternion.Inverse(_wq[sourceParent]) * _wq[source];
                var delta = Quaternion.Inverse(_target.Bind[source].Rotation) * sourceLocal;
                rotation = Quaternion.Normalize(parentQ * rest.Rotation * (frame * Power(delta, weight) * Quaternion.Inverse(frame)));
            }
            foreach (var (upper, twist, _, axis) in _upperTwist)
            {
                // The upper arm keeps the swing; its twist bone gets the rotation that the whole arm would have.
                if (bone == upper && _driven[bone])
                    rotation = Quaternion.Normalize(parentQ * rest.Rotation * Swing(Quaternion.Inverse(parentQ * rest.Rotation) * rotation, axis));
                else if (bone == twist && _driven[upper])
                    rotation = Quaternion.Normalize(_desired[upper] * rest.Rotation);
            }
            foreach (var (lower, twist, hand, axis, handRest) in _lowerTwist)
            {
                if (!_driven[hand]) continue;
                if (bone == lower && _driven[bone] && twist < 0)
                {
                    // No twist bone: the forearm takes half of the hand's turn about the forearm.
                    var turn = TwistAbout(Quaternion.Inverse(rotation) * _desired[hand] * Quaternion.Inverse(handRest), axis);
                    rotation = Quaternion.Normalize(rotation * Power(turn, 0.5f));
                }
                else if (bone == twist)
                {
                    // The twist bone takes all of the hand's turn about the forearm (MMD twist bones; its followers share it).
                    var turn = TwistAbout(Quaternion.Inverse(_wq[lower]) * _desired[hand] * Quaternion.Inverse(handRest), axis);
                    rotation = Quaternion.Normalize(_wq[lower] * turn * Quaternion.Inverse(_wq[lower]) * rotation);
                }
            }
            _wq[bone] = rotation;
        }
    }

    // The direction in which the GH3 elbow or knee sticks out of its shoulder-hand or hip-foot line (zero without one).
    private Vector3 Pole(int upper, int lower, int end)
    {
        if (upper < 0 || lower < 0 || end < 0) return Vector3.Zero;
        var line = _sp[end] - _sp[upper];
        if (line.LengthSquared() < StageTolerance.TinyLengthSquared) return Vector3.Zero;
        line = Vector3.Normalize(line);
        var offset = _sp[lower] - _sp[upper];
        return offset - line * Vector3.Dot(offset, line);
    }

    // Two-bone IK in world space: bends the middle joint in the plane of the GH3 limb (its elbow or knee direction, so the
    // bend does not flip when the character's limb is nearly straight), else of the current pose, then turns the chain to the goal.
    private void Solve(int upper, int lower, int end, Vector3 goal, float gain, Vector3 pole)
    {
        var a = _wp[upper];
        var b = _wp[lower];
        var c = _wp[end];
        var upperLength = Vector3.Distance(a, b);
        var lowerLength = Vector3.Distance(b, c);
        if (upperLength < MinimumBoneLength || lowerLength < MinimumBoneLength) return;
        var toGoal = goal - a;
        var distance = Math.Clamp(toGoal.Length(), Math.Abs(upperLength - lowerLength) + MinimumBoneLength, (upperLength + lowerLength) * FullReach);
        if (toGoal.LengthSquared() < StageTolerance.ZeroLengthSquared) return;
        var u = Vector3.Normalize(toGoal);
        var bend = b - a - u * Vector3.Dot(b - a, u);
        var hint = pole - u * Vector3.Dot(pole, u);
        if (pole.Length() > PoleLength && hint.LengthSquared() > StageTolerance.SmallLengthSquared) bend = hint;
        if (bend.LengthSquared() < StageTolerance.ZeroLengthSquared)
        {
            bend = c - a - u * Vector3.Dot(c - a, u);
            if (bend.LengthSquared() < StageTolerance.ZeroLengthSquared) bend = Vector3.Cross(u, Math.Abs(u.Y) < StageTolerance.NearAxis ? Vector3.UnitY : Vector3.UnitX);
        }
        var v = Vector3.Normalize(bend);
        var cos = Math.Clamp((upperLength * upperLength + distance * distance - lowerLength * lowerLength) / (2 * upperLength * distance), -1f, 1f);
        var elbow = a + (u * cos + v * MathF.Sqrt(1 - cos * cos)) * upperLength;
        var wrist = a + u * distance;
        var first = FromTo(Vector3.Normalize(b - a), Vector3.Normalize(elbow - a));
        var second = FromTo(Vector3.Normalize(Vector3.Transform(c - b, first)), Vector3.Normalize(wrist - elbow));
        if (gain < 1)
        {
            first = Quaternion.Slerp(Quaternion.Identity, first, gain);
            second = Quaternion.Slerp(Quaternion.Identity, second, gain);
        }
        _desired[upper] = Quaternion.Normalize(first * (_driven[upper] ? _desired[upper] : _wq[upper]));
        _desired[lower] = Quaternion.Normalize(second * first * (_driven[lower] ? _desired[lower] : _wq[lower]));
        _driven[upper] = _driven[lower] = true;
    }

    // The twist of q about a unit axis (q = swing * twist), and the swing.
    private static Quaternion TwistAbout(Quaternion q, Vector3 axis)
    {
        var projection = axis * Vector3.Dot(new Vector3(q.X, q.Y, q.Z), axis);
        var twist = new Quaternion(projection, q.W);
        return twist.LengthSquared() < StageTolerance.TinyLengthSquared ? Quaternion.Identity : Quaternion.Normalize(twist);
    }

    private static Quaternion Swing(Quaternion q, Vector3 axis) => Quaternion.Normalize(q * Quaternion.Inverse(TwistAbout(q, axis)));

    // q to the power t (any real t, also negative): the same axis, t times the angle.
    private static Quaternion Power(Quaternion q, float t)
    {
        if (q.W < 0) q = new Quaternion(-q.X, -q.Y, -q.Z, -q.W);
        var sin = MathF.Sqrt(q.X * q.X + q.Y * q.Y + q.Z * q.Z);
        if (sin < MinimumSine) return Quaternion.Identity;
        var angle = 2 * MathF.Atan2(sin, q.W) * t;
        var s = MathF.Sin(angle / 2) / sin;
        return new Quaternion(q.X * s, q.Y * s, q.Z * s, MathF.Cos(angle / 2));
    }

    public static Quaternion FromTo(Vector3 from, Vector3 to)
    {
        var dot = Vector3.Dot(from, to);
        if (dot < OppositeDot)
        {
            var axis = Vector3.Cross(Math.Abs(from.X) < StageTolerance.NearAxis ? Vector3.UnitX : Vector3.UnitY, from);
            return Quaternion.CreateFromAxisAngle(Vector3.Normalize(axis), MathF.PI);
        }
        var cross = Vector3.Cross(from, to);
        return Quaternion.Normalize(new Quaternion(cross, 1 + dot));
    }

    // The turn of an orthonormal frame (primary axis, secondary axis in its plane) to another one.
    private static Quaternion FrameTurn(Vector3 fromPrimary, Vector3 fromSecondary, Vector3 toPrimary, Vector3 toSecondary)
    {
        var first = FromTo(Vector3.Normalize(fromPrimary), Vector3.Normalize(toPrimary));
        var axis = Vector3.Normalize(toPrimary);
        var a = Vector3.Transform(fromSecondary, first);
        a -= axis * Vector3.Dot(a, axis);
        var b = toSecondary - axis * Vector3.Dot(toSecondary, axis);
        if (a.LengthSquared() < StageTolerance.ZeroLengthSquared || b.LengthSquared() < StageTolerance.ZeroLengthSquared) return first;
        return Quaternion.Normalize(FromTo(Vector3.Normalize(a), Vector3.Normalize(b)) * first);
    }

    private static float Smooth(float x)
    {
        x = Math.Clamp(x, 0, 1);
        return x * x * (3 - 2 * x);
    }

    private static Vector3 Position(Vector3[] positions, int bone) => bone >= 0 ? positions[bone] : Vector3.Zero;

    private static bool IsAbove(int[] parents, int above, int bone)
    {
        for (var parent = parents[bone]; parent >= 0; parent = parents[parent]) if (parent == above) return true;
        return false;
    }

    private static int Common(int[] parents, int a, int b)
    {
        for (var x = a; x >= 0; x = parents[x])
            if (x == b || IsAbove(parents, x, b)) return x;
        return a;
    }

    private static (Vector3[] Positions, Quaternion[] Rotations) World(int[] parents, BandPose[] bind, int[] order)
    {
        var positions = new Vector3[parents.Length];
        var rotations = new Quaternion[parents.Length];
        foreach (var bone in order)
        {
            var parent = parents[bone];
            if (parent < 0) { positions[bone] = bind[bone].Position; rotations[bone] = bind[bone].Rotation; continue; }
            positions[bone] = positions[parent] + Vector3.Transform(bind[bone].Position, rotations[parent]);
            rotations[bone] = Quaternion.Normalize(rotations[parent] * bind[bone].Rotation);
        }
        return (positions, rotations);
    }

    // Parents first, and each follower after the bone that it follows.
    private static int[] Order(int[] parents, (int Bone, int Source)[]? after)
    {
        var count = parents.Length;
        var waits = new int[count];
        var next = new List<int>[count];
        for (var i = 0; i < count; i++) next[i] = new List<int>();
        for (var i = 0; i < count; i++)
            if (parents[i] >= 0) { waits[i]++; next[parents[i]].Add(i); }
        if (after != null)
            foreach (var (bone, source) in after) { waits[bone]++; next[source].Add(bone); }
        var queue = new Queue<int>(Enumerable.Range(0, count).Where(i => waits[i] == 0));
        var order = new List<int>(count);
        while (queue.Count > 0)
        {
            var bone = queue.Dequeue();
            order.Add(bone);
            foreach (var child in next[bone]) if (--waits[child] == 0) queue.Enqueue(child);
        }
        if (order.Count != count) throw new InvalidDataException("The character skeleton has a cycle (parents or followers)");
        return order.ToArray();
    }
}
