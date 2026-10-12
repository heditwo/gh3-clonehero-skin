using System.Numerics;

namespace Gh3Skin;

// GH3's walk of a band member to a waypoint (guitar_walking.qb walk_to_waypoint, walk_to_pos, face_audience): the start,
// cycle, and stop clips of the set's walking struct, the root motion of the clips (MotionExtractedSource), and the
// turns of Obj_RotY. Positions are horizontal (x, z) in the frame of the band placements; a yaw of 0 faces -Z.
internal sealed class StageWalk
{
    // GH3: hero_play_anim blends a new body clip over 0.2 s; walk_to_pos plays the cycle with BlendDuration 0.0.
    public const double ClipBlend = 0.2;
    // GH3: face_audience turns to the world point (0, 0, 50); runtime coordinates flip z.
    private const float AudienceZ = -50;
    // GH3: min_walk_dist (guitar_animation_data.qb), the default when the constants have none.
    private const double DefaultMinimumDistance = 0.75;
    // GH3: walk_to_pos rounds the cycle count to whole or half cycles: a fraction above 0.75 adds a cycle, above 0.25
    // half a cycle; with a cycle without displacement it plays 3 cycles.
    private const double RoundUpFraction = 0.75;
    private const double HalfCycleFraction = 0.25;
    private const double CyclesWithoutDisplacement = 3;
    // GH3: hero_get_foot_down: a frame factor of the body timer in (0.12, 0.62) means the right foot is down.
    private const double RightFootFirst = 0.12;
    private const double RightFootLast = 0.62;
    // GH3: turn_speed and face_audience_speed (degrees per second) of every guitarist walking struct
    // (guitar_animation_data_<name>.qb); a set without them (the bassist) uses these values.
    private const float DefaultTurnSpeed = 45;
    private const float DefaultFaceAudienceSpeed = 90;
    // GH3: min_turn of default_turn_data (guitar_animation_data.qb), for a set without it.
    private const float DefaultMinimumTurn = 45;
    // The root path is integrated and stored at 120 steps per second.
    private const double Step = 1.0 / 120;
    // A walk with its turns lasts at most one minute (turn speeds below 3 degrees per second are not valid data).
    private const double MaximumSeconds = 60;
    private const float MinimumTurnSpeed = 3;

    public sealed record Branch(double Time, string Clip, double Blend, bool Loop, double Duration);

    public double Start { get; }
    public bool Backward { get; }
    public bool Walks => Branches.Length > 0;
    // The start, cycle, and stop clips; empty when the target is nearer than min_walk_dist (GH3 then only turns).
    public Branch[] Branches { get; }
    // The end of the stop clip: the body plays the idle again (guitarist_idle).
    public double End { get; }
    // The end of the last turn (face_audience).
    public double TurnEnd { get; }
    public double Cycles { get; }
    public bool RightFoot { get; }
    private readonly float[] _x, _z, _yaw;

    private StageWalk(double start, bool backward, Branch[] branches, double end, double cycles, bool rightFoot, List<float> x, List<float> z, List<float> yaw)
    {
        Start = start;
        Backward = backward;
        Branches = branches;
        End = end;
        Cycles = cycles;
        RightFoot = rightFoot;
        _x = x.ToArray();
        _z = z.ToArray();
        _yaw = yaw.ToArray();
        TurnEnd = start + (_x.Length - 1) * Step;
    }

    // The root position (x, z) and yaw (degrees) at `time`; before Start the start state, after TurnEnd the end state.
    public (Vector2 Position, float Yaw) Root(double time)
    {
        var at = Math.Clamp((time - Start) / Step, 0, _x.Length - 1);
        var a = (int)at;
        var b = Math.Min(a + 1, _x.Length - 1);
        var f = (float)(at - a);
        var yaw = _yaw[a] + (_yaw[b] - _yaw[a]) * f;
        return (new(_x[a] + (_x[b] - _x[a]) * f, _z[a] + (_z[b] - _z[a]) * f), yaw);
    }

    // The newest walk clip that has started at `time` and its clip time, or null outside the clips.
    public (Branch Branch, double ClipTime)? Active(double time)
    {
        if (time < Start || time >= End) return null;
        for (var i = Branches.Length - 1; i >= 0; i--)
            if (Branches[i].Time <= time) return (Branches[i], ClipTime(Branches[i], time));
        return null;
    }

    public static double ClipTime(Branch branch, double time)
    {
        var age = Math.Max(0, time - branch.Time);
        return branch.Loop && branch.Duration > 0 ? age % branch.Duration : Math.Min(age, branch.Duration);
    }

    // The weight of a branch over the older ones at `time` (CDegenerateBlendAnimNode with the branch's BlendDuration).
    public static float Weight(Branch branch, double time) =>
        branch.Blend > 0 ? (float)Math.Clamp((time - branch.Time) / branch.Blend, 0, 1) : time >= branch.Time ? 1 : 0;

    // The data of a walk: the clip groups and walking scalars of the set, its clips, the GH3 constants, and the root
    // motion of a clip (the root bone position at a clip time, in the performer frame).
    public sealed record Data(PerformanceMember Member, IReadOnlyDictionary<string, float> Constants, Func<string, double, Vector3> RootAt, float Scale);

    // walk_to_waypoint at `time` from the root state `position` and `yaw` to `target`.
    public static StageWalk Plan(Data data, double time, Vector2 position, float yaw, Vector2 target)
    {
        var walking = data.Member.Walking;
        var minimumTurn = walking.GetValueOrDefault(PerformanceMember.WalkMinTurn, DefaultMinimumTurn);
        var turnSpeed = Math.Max(MinimumTurnSpeed, walking.GetValueOrDefault(PerformanceMember.WalkTurnSpeed, DefaultTurnSpeed));
        var audienceSpeed = Math.Max(MinimumTurnSpeed, walking.GetValueOrDefault(PerformanceMember.WalkFaceAudienceSpeed, DefaultFaceAudienceSpeed));
        var minimumDistance = data.Constants.TryGetValue(PerformanceMember.MinimumWalkDistance, out var found) ? found : DefaultMinimumDistance;
        var toward = target - position;
        var length = toward.Length();
        var turns = new List<(double Time, Func<Vector2, float, float?> Angle, float Speed)>();
        var branches = new List<Branch>();
        var backward = false;
        double cycles = 0;
        var rightFoot = false;
        var end = time;
        if (length >= minimumDistance)
        {
            // get_angle_to_target: the performer turns to face a target between min_turn and 180 - min_turn degrees
            // from its front; else it turns by the small angle that puts the target on its front or back axis.
            var angle = SignedAngle(yaw, toward);
            var magnitude = Math.Abs(angle);
            var turn = magnitude > minimumTurn && magnitude < HalfTurn - minimumTurn ? angle
                : magnitude <= QuarterTurn ? angle : angle - MathF.CopySign(HalfTurn, angle);
            turns.Add((time, (_, _) => turn, turnSpeed));
            // IsFacing reads the facing before the turn moves (Obj_RotY does not wait).
            backward = Vector2.Dot(Front(yaw), toward) < 0 && data.Member.Groups.ContainsKey(PerformanceMember.WalkClip(true, PerformanceMember.WalkCycle));
            (branches, cycles, rightFoot) = Clips(data, backward, length, time);
            end = branches.Count > 0 ? branches[^1].Time + branches[^1].Duration : time;
            var stop = branches.Count > 0 ? branches[^1].Time : time;
            // At the stop: a performer that faces the audience within min_turn degrees turns to it at face_audience_speed.
            turns.Add((stop, (p, y) => Math.Abs(AudienceAngle(p, y)) < minimumTurn ? AudienceAngle(p, y) : null, audienceSpeed));
        }
        turns.Add((end, (p, y) => AudienceAngle(p, y), turnSpeed));
        return Integrate(data, time, position, yaw, branches, turns, end, backward, cycles, rightFoot);
    }

    private const float HalfTurn = 180;
    private const float QuarterTurn = 90;

    private static (List<Branch>, double Cycles, bool RightFoot) Clips(Data data, bool backward, float length, double time)
    {
        string? Clip(string part) => data.Member.Groups.TryGetValue(PerformanceMember.WalkClip(backward, part), out var refs) && refs.Length > 0 ? refs[0] : null;
        PerformanceClipInfo? Info(string? clip) => clip != null && data.Member.Clips.TryGetValue(clip, out var info) ? info : null;
        var start = Clip(PerformanceMember.WalkStart);
        var cycle = Clip(PerformanceMember.WalkCycle);
        var stopLeft = Clip(PerformanceMember.WalkStopLeft);
        // Anim_GetTotalDisplacement of the start, stop_left, and cycle clips, scaled by the performer scale.
        var desired = length - (Info(start)?.RootDistance ?? 0) * data.Scale - (Info(stopLeft)?.RootDistance ?? 0) * data.Scale;
        var cycleInfo = Info(cycle);
        var displacement = (cycleInfo?.RootDistance ?? 0) * data.Scale;
        var cycles = displacement == 0 ? CyclesWithoutDisplacement : desired / displacement;
        var whole = Math.Truncate(cycles);
        var fraction = cycles - whole;
        cycles = whole + (fraction > RoundUpFraction ? 1 : fraction > HalfCycleFraction ? 0.5 : 0);
        var branches = new List<Branch>();
        var at = time;
        if (Info(start) is { } startInfo)
        {
            branches.Add(new(at, start!, ClipBlend, false, startInfo.Duration));
            at += startInfo.Duration;
        }
        var cycleTime = cycleInfo != null ? cycles * cycleInfo.Duration : 0;
        // hero_get_foot_down reads the frame factor of the body timer: the cycle, or the finished start clip (1).
        var frameFactor = 1.0;
        if (cycleTime > 0)
        {
            branches.Add(new(at, cycle!, 0, true, cycleInfo!.Duration));
            at += cycleTime;
            var phase = cycleTime / cycleInfo.Duration;
            frameFactor = phase - Math.Floor(phase);
        }
        var rightFoot = frameFactor > RightFootFirst && frameFactor < RightFootLast;
        var stop = Clip(rightFoot ? PerformanceMember.WalkStopRight : PerformanceMember.WalkStopLeft);
        if (Info(stop) is { } stopInfo) branches.Add(new(at, stop!, ClipBlend, false, stopInfo.Duration));
        return (branches, cycles, rightFoot);
    }

    // Moves the root by the blended root motion of the clips and turns it by the Obj_RotY commands (a new command
    // replaces the turn in progress), until the stop clip and the last turn end.
    private static StageWalk Integrate(Data data, double time, Vector2 position, float yaw, List<Branch> branches,
        List<(double Time, Func<Vector2, float, float?> Angle, float Speed)> turns, double end, bool backward, double cycles, bool rightFoot)
    {
        var xs = new List<float> { position.X };
        var zs = new List<float> { position.Y };
        var yaws = new List<float> { yaw };
        var next = 0;
        var remaining = 0f;
        var speed = 0f;
        for (var step = 1; step * Step <= MaximumSeconds; step++)
        {
            var from = time + (step - 1) * Step;
            var to = from + Step;
            while (next < turns.Count && turns[next].Time <= from)
            {
                if (turns[next].Angle(position, yaw) is { } angle)
                {
                    remaining = angle;
                    speed = turns[next].Speed;
                }
                next++;
            }
            var motion = Vector3.Zero;
            foreach (var branch in branches)
            {
                if (branch.Time > from) break;
                var delta = Displacement(data, branch, from, to);
                motion += (delta - motion) * Weight(branch, (from + to) / 2);
            }
            var turn = Math.Clamp(remaining, -speed * (float)Step, speed * (float)Step);
            remaining -= turn;
            position += Rotate(new(motion.X, motion.Z), yaw + turn / 2) * data.Scale;
            yaw += turn;
            xs.Add(position.X);
            zs.Add(position.Y);
            yaws.Add(yaw);
            if (to >= end && next >= turns.Count && remaining == 0) break;
        }
        return new StageWalk(time, backward, branches.ToArray(), end, cycles, rightFoot, xs, zs, yaws);
    }

    // The root motion of a clip between two song times (performer frame); a cycle clip wraps.
    private static Vector3 Displacement(Data data, Branch branch, double from, double to)
    {
        var a = Math.Max(0, from - branch.Time);
        var b = Math.Max(0, to - branch.Time);
        if (!branch.Loop) return data.RootAt(branch.Clip, Math.Min(b, branch.Duration)) - data.RootAt(branch.Clip, Math.Min(a, branch.Duration));
        var duration = branch.Duration;
        var wraps = Math.Floor(b / duration) - Math.Floor(a / duration);
        var whole = data.RootAt(branch.Clip, duration) - data.RootAt(branch.Clip, 0);
        return data.RootAt(branch.Clip, b % duration) - data.RootAt(branch.Clip, a % duration) + whole * (float)wraps;
    }

    // The front of a yaw (degrees about +Y): the performer looks along -Z of its root (the GH3 `at` axis).
    public static Vector2 Front(float yaw)
    {
        var radians = yaw * MathF.PI / HalfTurn;
        return new(-MathF.Sin(radians), -MathF.Cos(radians));
    }

    // The yaw (degrees) of a front direction.
    public static float Yaw(Vector2 front) => MathF.Atan2(-front.X, -front.Y) * HalfTurn / MathF.PI;

    // A local horizontal vector (x, z) of a root with this yaw, in the placement frame.
    private static Vector2 Rotate(Vector2 local, float yaw)
    {
        var radians = yaw * MathF.PI / HalfTurn;
        var (sin, cos) = MathF.SinCos(radians);
        return new(local.X * cos + local.Y * sin, -local.X * sin + local.Y * cos);
    }

    // The yaw change (degrees, -180 to 180) that turns the front of `yaw` to `direction`.
    public static float SignedAngle(float yaw, Vector2 direction)
    {
        var front = Front(yaw);
        var side = new Vector2(front.Y, -front.X);
        return MathF.Atan2(Vector2.Dot(direction, side), Vector2.Dot(direction, front)) * HalfTurn / MathF.PI;
    }

    private static float AudienceAngle(Vector2 position, float yaw) => SignedAngle(yaw, new Vector2(0, AudienceZ) - position);

}
