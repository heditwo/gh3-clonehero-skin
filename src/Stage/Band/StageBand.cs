using System.Text.Json;
using MelonLoader;
using UnityEngine;

namespace Gh3Skin;

// The band on the stage: performers on their rigs, their performance players, walks, hair, and the per-frame pose.

internal sealed class BandDefinition
{
    public int Version { get; set; }
    public BandMember[] Members { get; set; } = Array.Empty<BandMember>();
}

internal sealed class BandMember
{
    public string Role { get; set; } = "";
    public float[] Position { get; set; } = Array.Empty<float>();
    public float Yaw { get; set; }
    public string Instrument { get; set; } = "";
}

internal sealed partial class StageBand : IDisposable
{
    // Walk speed (m/s) and duration (s) floors, and the blend into and out of a walk (Tuned).
    private const double MinimumWalkSpeed = 0.1;
    private const double MinimumWalk = 0.01;
    private const double WalkBlend = 0.2;
    private sealed class Performer
    {
        public string Role = "";
        public Transform Root = null!;
        public Transform[] Bones = Array.Empty<Transform>();
        public BandAnimation Animation = null!;
        public StageAssets Model = null!;
        public PerformancePlayer? Performance;
        public PerformanceMember? Definition;
        public StageHair? Hair;
        public bool HairWarned;
        // The animation set that plays and the GH3 rig that it plays on (the bones are this rig, or Retarget moves them).
        public CharacterMotion Motion = null!;
        public BandPose[] RoleBind = Array.Empty<BandPose>();
        public BandPose[] Pose = Array.Empty<BandPose>();
        // The bones that a character package adds after the rig bones (character.json bones), at their bind pose.
        public BandPose[] ExtraBind = Array.Empty<BandPose>();
        // A character with its own skeleton: the GH3 rig that plays and the retarget to the character's bones (null:
        // the bones are the rig).
        public StageRetarget? Retarget;
        public Vector3 Start;
        public Quaternion Rotation;
        public List<Walk> Walks = new();
    }
    // Backward: GH3 walks backward to the target, facing away from it (PerformanceMember.WalksBackward).
    private sealed record Walk(double Time, double Duration, Vector3 From, Vector3 To, Quaternion Rotation, bool Backward);
    private readonly List<StageAssets> _assets = new();
    private readonly List<Performer> _players = new();
    private StagePerformance? _performance;
    private double _start;
    public Dictionary<string, Vector3> Positions { get; } = new();

    public void Load(string directory, StageAssets stage, GameObject root, int layer, bool lighting = false,
        IReadOnlyDictionary<string, StagePlacement>? placements = null, Shader? surfaceShader = null,
        IReadOnlyDictionary<string, Packages.CharacterModel>? characters = null, IReadOnlyDictionary<string, Packages.InstrumentModel>? instruments = null)
    {
        var path = Path.Combine(directory, "band.json");
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Shared band library is missing. Run scripts/regenerate.sh library and install.sh.", path);
        }
        var definition = JsonSerializer.Deserialize<BandDefinition>(File.ReadAllText(path),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (definition?.Version != 1 || definition.Members.Length is < 1 or > 4 ||
            definition.Members.Select(m => m.Role).Distinct().Count() != definition.Members.Length)
            throw new InvalidDataException("Invalid band definition");
        var hair = HairBank.Load(Path.Combine(directory, "hair.json"));
        var library = Path.GetDirectoryName(Path.GetFullPath(directory))!;
        foreach (var member in definition.Members)
        {
            if (placements != null && !placements.ContainsKey(member.Role)) continue;
            if (member.Role is not ("singer" or "guitarist" or "bassist" or "drummer") ||
                member.Position.Length != 3 || !member.Position.All(float.IsFinite) || !float.IsFinite(member.Yaw))
                throw new InvalidDataException("Invalid band member");
            var folder = Path.Combine(directory, member.Role);
            using var reader = new BinaryReader(File.OpenRead(Path.Combine(folder, "animation.bin")));
            var roleAnimation = new BandAnimation(reader);
            var character = characters != null && characters.TryGetValue(member.Role, out var chosen) ? chosen : null;
            var instrument = instruments != null && instruments.TryGetValue(member.Role, out var picked) ? picked : null;
            // The rig bone names are needed when the role's set drives a retarget or a fixed profile plays.
            var roleMotion = LibraryMotion(library, member.Role, roleAnimation, hair, names: character != null &&
                (character.Library.Length == 0 || !Packages.CharacterLibrary.UsesOwnSet(character.Role, member.Role)));
            CharacterMotion? own = null;
            if (character != null)
                try { own = OwnMotion(character, roleMotion, directory, library, hair); }
                catch (Exception error) when (error is IOException or InvalidDataException or Packages.PackageException or System.Text.Json.JsonException or UnauthorizedAccessException)
                {
                    MelonLogger.Warning($"GH3 band {member.Role}: character {character.Id} could not load ({error.Message}); the venue default plays");
                    character = null;
                }
            var performer = new GameObject("GH3 Band " + member.Role) { layer = layer };
            performer.transform.SetParent(root.transform, false);
            performer.transform.localPosition = StageAssets.Vector(member.Position);
            performer.transform.localRotation = Quaternion.Euler(0, member.Yaw, 0);
            if (placements != null)
            {
                var placement = placements[member.Role];
                performer.transform.localPosition = StageAssets.Vector(placement.Position);
                var q = placement.Rotation;
                performer.transform.localRotation = new(q[0], q[1], q[2], q[3]);
            }
            Positions.Add(member.Role, performer.transform.localPosition);
            Transform[] Bones(BandAnimation rig)
            {
                var created = new Transform[rig.Parents.Length];
                for (var i = 0; i < created.Length; i++)
                    created[i] = new GameObject($"GH3 {member.Role} Bone {i}") { layer = layer }.transform;
                for (var i = 0; i < created.Length; i++)
                {
                    created[i].SetParent(rig.Parents[i] < 0 ? performer.transform : created[rig.Parents[i]], false);
                    Apply(created[i], rig.BindPose[i]);
                }
                return created;
            }
            var loaded = character != null && own != null
                ? LoadCharacter(character, own, roleMotion, directory, library, member, performer, layer, lighting, surfaceShader, instrument, Bones)
                : null;
            loaded ??= LoadMember(roleMotion, directory, library, member, performer, layer, lighting, surfaceShader, instrument, Bones);
            var player = loaded.Player;
            player.Role = member.Role;
            player.Root = performer.transform;
            player.Start = performer.transform.localPosition;
            player.Rotation = performer.transform.localRotation;
            player.Pose = new BandPose[player.Bones.Length];
            player.RoleBind = roleAnimation.BindPose;
            player.Model = loaded.Assets[0];
            foreach (var part in loaded.Assets)
            {
                _assets.Add(part);
                stage.SurfaceMaterials.AddRange(part.SurfaceMaterials);
                stage.ShadedSurfaces.AddRange(part.ShadedSurfaces);
                stage.BandMeshes.AddRange(part.BandMeshes);
                stage.TransparentMeshes.AddRange(part.TransparentMeshes);
                stage.ToonOutlines.AddRange(part.ToonOutlines);
            }
            if (loaded.Hair != null)
                try { player.Hair = new StageHair(loaded.Hair, loaded.Parents, (time, pose) => SamplePose(player, time, pose)); }
                catch (InvalidDataException error) when (loaded.ChainJoints > 0)
                {
                    MelonLogger.Warning($"GH3 band {member.Role}: the chains of character {character?.Id} could not load ({error.Message}); no hair physics");
                }
            _players.Add(player);
        }
        MelonLogger.Msg($"GH3 band loaded: {_players.Count} performers; default male singer; original GH3 animations");
        if (hair != null) MelonLogger.Msg($"GH3 hair physics: {hair.Members.Values.Sum(m => m.Joints.Length)} original joints");
    }

    // Waits for the clip prefetch of all performers; returns the clip count, the worker time, and the time the caller waited.
    public (int Clips, double Worker, double Waited) WaitForClips()
    {
        int clips = 0;
        double worker = 0, waited = 0;
        foreach (var player in _players)
        {
            if (player.Performance == null) continue;
            foreach (var cache in new[] { player.Performance.Clips, player.Performance.FaceClips })
            {
                if (cache == null) continue;
                waited += cache.Wait();
                clips += cache.Prefetched;
                worker = Math.Max(worker, cache.PrefetchMilliseconds);
            }
        }
        return (clips, worker, waited);
    }

    public void SetTimeline(string directory, StageTimeline timeline, IReadOnlyDictionary<string, StagePlacement>? waypoints = null,
        StagePerformance? performance = null)
    {
        _start = timeline.Start;
        _performance = performance ?? StagePerformance.Load(Path.Combine(directory, "performance.json"));
        if (_performance == null)
        {
            MelonLogger.Warning("GH3 direction assets are missing. Run scripts/regenerate.sh library and install.sh.");
            return;
        }
        foreach (var player in _players)
        {
            if (!_performance.Members.TryGetValue(player.Role, out var definition)) continue;
            var motion = player.Motion;
            if (motion.IsLibrary)
            {
                if (!_performance.Members.TryGetValue(motion.LibraryRole, out var set)) continue;
                motion.Member = set;
                motion.ClipDirectory = Path.Combine(directory, motion.LibraryRole);
            }
            if (!motion.IsLibrary || motion.LibraryRole != player.Role)
            {
                // The song facial clips stay on the band library member of the role; they move to the rig of the set as
                // the set clips do.
                motion.Face = definition;
                motion.FaceDirectory = Path.Combine(directory, player.Role);
                motion.FaceOffsets = CharacterMotion.RestOffsets(motion.Profile.Bones, motion.Profile.Bind, definition.Bones,
                    player.RoleBind.Select(p => new[] { p.Position.X, p.Position.Y, p.Position.Z }).ToArray());
            }
            definition = motion.Member;
            player.Definition = definition;
            var driver = player.Retarget?.Source ?? player.Animation;
            player.Performance = new PerformancePlayer(player.Role, directory, definition, driver.BindPose, timeline,
                driver.Parents, _performance.Constants, motion);
            player.Performance.Prepare();
            if (player.Retarget?.Face is { } face)
            {
                face.Prepare(timeline, player.Role);
                MelonLogger.Msg($"GH3 band {player.Role}: face blend shapes with {(face.LipSync ? "lip sync from the sung notes" : "blinks")}");
            }
            var position = player.Start;
            var facing = player.Rotation;
            foreach (var cue in timeline.Track("walk", player.Role))
            {
                if (waypoints == null || !waypoints.TryGetValue(cue.Value, out var waypoint)) continue;
                var target = StageAssets.Vector(waypoint.Position);
                var q = waypoint.Rotation;
                // The performer looks along -Z of its root (the GH3 `at` axis).
                var front = facing * Vector3.back;
                var backward = definition.WalksBackward(front.x, front.z, target.x - position.x, target.z - position.z);
                var speed = 1.0;
                if (definition.Groups.TryGetValue(PerformanceMember.WalkCycle(backward), out var cycle))
                {
                    var clip = definition.Clips[cycle[0]];
                    speed = Math.Max(MinimumWalkSpeed, clip.RootDistance / clip.Duration);
                }
                var rotation = new Quaternion(q[0], q[1], q[2], q[3]);
                player.Walks.Add(new Walk(cue.Time, Math.Max(MinimumWalk, Vector3.Distance(position, target) / speed), position, target, rotation, backward));
                position = target;
                facing = rotation;
            }
        }
        MelonLogger.Msg($"GH3 original performance: {timeline.Track("stance", "guitarist").Length} guitar stance cues; " +
            $"{timeline.Track("drum").Length} drum cues; {timeline.Track("face", "singer").Length} singer facial cues; " +
            LipSyncSummary(timeline));
        if (BindingTrace.Enabled) TracePoses();
        _drumHits = timeline.Track("drum").Where(c => c.Value != "0").Select(c => c.Time).ToArray();
    }

    // GH3_SKIN_TRACE: a hash of the local poses (bits of each float) of each performer at fixed times, before the hair
    // physics. Two builds that give the same hash give the same poses (refactors of the band, VERIFICATION.md).
    private static readonly double[] PoseHashTimes = { 5, 10, 20, 30, 45 };
    // FNV-1a, 32 bits (the offset basis and the prime of the specification).
    private const uint HashBasis = 2166136261;
    private const uint HashPrime = 16777619;

    private void TracePoses()
    {
        foreach (var player in _players)
        {
            if (player.Performance == null) continue;
            var pose = new BandPose[player.Pose.Length];
            var hash = HashBasis;
            void Add(float value)
            {
                foreach (var b in BitConverter.GetBytes(value)) hash = (hash ^ b) * HashPrime;
            }
            void AddPose(BandPose p)
            {
                Add(p.Position.X); Add(p.Position.Y); Add(p.Position.Z); Add(p.Rotation.X); Add(p.Rotation.Y); Add(p.Rotation.Z); Add(p.Rotation.W);
            }
            foreach (var time in PoseHashTimes)
            {
                AddPose(SamplePose(player, time, pose));
                foreach (var bone in pose) AddPose(bone);
            }
            MelonLogger.Msg(FormattableString.Invariant($"[trace] pose role={player.Role} bones={pose.Length} hash={hash:x8}"));
        }
    }

    private string LipSyncSummary(StageTimeline timeline)
    {
        var sung = timeline.Track("sing", "singer").Length;
        var lipSync = _players.FirstOrDefault(p => p.Role == "singer")?.Performance?.LipSync;
        if (lipSync != null) return $"lip sync from {sung} sung notes ({lipSync.NoteCount} syllables, {lipSync.KeyCount} viseme keys; " +
            $"{lipSync.DictionaryWords} words from the pronouncing dictionary, {lipSync.RuleWords} from spelling rules)";
        return sung == 0 ? "no sung notes" : $"lip sync off ({sung} sung notes; {(StageLipSync.Enabled ? "GH3 facial clips or no singer jaw" : "GH3_SKIN_LIPSYNC=off")})";
    }

    public BandPose? Anchor(string role, string bone)
    {
        var player = _players.FirstOrDefault(p => p.Role == role);
        if (player == null) return null;
        var index = player.Definition == null ? -1 : Array.IndexOf(player.Definition.Bones, bone);
        var transform = index < 0 ? player.Root : player.Retarget?.Anchor(index) ?? player.Bones[index];
        var p = transform.position;
        var q = transform.rotation;
        return new BandPose(new(p.x, p.y, p.z), new(q.x, q.y, q.z, q.w));
    }

    public Transform? HandFireBone(string bone, int fallback)
    {
        var player = _players.FirstOrDefault(p => p.Role == "guitarist");
        if (player == null) return null;
        var index = player.Definition == null ? fallback : Array.IndexOf(player.Definition.Bones, bone);
        if (player.Retarget is { } retarget) return index >= 0 && index < retarget.SourceBones.Length ? retarget.Anchor(index) : null;
        return index >= 0 && index < player.Bones.Length ? player.Bones[index] : null;
    }

    public void Update(double time)
    {
        var trace = BindingTrace.Enabled && time >= _traceNext;
        if (trace)
        {
            _traceNext = Math.Floor(time * 2 + 1) / 2;
            foreach (var player in _players.Where(p => p.Performance != null && (p.Role is "guitarist" or "bassist" || !p.Motion.IsLibrary || p.Retarget != null))) Trace(player, time);
        }
        foreach (var player in _players)
        {
            var mark = FrameProfile.Enabled ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
            player.Hair?.Sample(time - _start);
            if (FrameProfile.Enabled) FrameProfile.Add("band.hair", (System.Diagnostics.Stopwatch.GetTimestamp() - mark) * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
            if (player.Hair is { PeakSpeed: > StageHair.WarningSpeed } hair && !player.HairWarned)
            {
                player.HairWarned = true;
                MelonLogger.Warning(FormattableString.Invariant($"GH3 band {player.Role}: hair joint {player.Definition?.Bones.ElementAtOrDefault(hair.PeakBone) ?? hair.PeakBone.ToString()} ") +
                    FormattableString.Invariant($"moves at {hair.PeakSpeed:F0} m/s (profile {ProfileName(player.Motion)}); its ragdoll may not fit the rig"));
            }
            var sampled = FrameProfile.Enabled && player.Retarget != null ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
            var root = SamplePose(player, time, player.Pose);
            Apply(player.Root, root);
            player.Hair?.Apply(player.Pose);
            for (var i = 0; i < player.Bones.Length; i++) Apply(player.Bones[i], player.Pose[i]);
            if (player.Retarget is { } retarget)
            {
                retarget.ApplySource();
                retarget.Face?.Update(time, retarget.SourcePose);
                if (trace) TraceFace(player.Role, retarget, time);
                if (BindingTrace.Enabled && retarget.Rig.HasKit) TraceDrumHits(retarget.Rig, time);
                if (FrameProfile.Enabled) FrameProfile.Add("band.retarget", (System.Diagnostics.Stopwatch.GetTimestamp() - sampled) * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
            }
        }
    }

    private double _traceNext = double.NegativeInfinity;
    private double[] _drumHits = Array.Empty<double>();
    private int _drumHit;

    // GH3_SKIN_TRACE: at the first frame after each drum note, the distance of each drum stick tip from the GH3 stick
    // tip on the scaled kit (where the GH3 stick hits the drum), and the height of the GH3 tips.
    private void TraceDrumHits(CharacterRetarget rig, double time)
    {
        if (_drumHit >= _drumHits.Length || time < _drumHits[_drumHit]) return;
        while (_drumHit < _drumHits.Length && _drumHits[_drumHit] <= time) _drumHit++;
        MelonLogger.Msg(FormattableString.Invariant($"[trace] drum t={time:F3} note={_drumHits[_drumHit - 1]:F3} tipMiss={rig.StickTipMiss[0] * 100:F2},{rig.StickTipMiss[1] * 100:F2}cm ") +
            FormattableString.Invariant($"tipHeight={rig.StickTips[0].Y:F3},{rig.StickTips[1].Y:F3} handMiss={rig.HandMiss * 100:F2}cm"));
    }
    private static readonly string[] TraceFaceBones = Packages.HumanoidRoles.Gh3TraceFace.Select(Gh3Checksum.Of).ToArray();

    // GH3_SKIN_TRACE: the body clip and its local pose of a few bones, to compare with the GH3 clip on the GH3 skeleton; for
    // the singer also the song facial clip and the local pose of a few face bones; during a walk also the walk cycle clip.
    private static void Trace(Performer player, double time)
    {
        var raw = new BandPose[(player.Retarget?.Source ?? player.Animation).Parents.Length];
        var (segment, clipTime) = player.Performance!.SampleClip(time, raw);
        var bones = player.Definition!.Bones;
        string Values(IEnumerable<string> checksums, BandPose[] pose) => string.Join(";", checksums.Select(checksum => Array.IndexOf(bones, checksum)).Where(i => i >= 0).Select(i =>
            FormattableString.Invariant($"{i}:{pose[i].Position.X:F6},{pose[i].Position.Y:F6},{pose[i].Position.Z:F6},{pose[i].Rotation.X:F6},{pose[i].Rotation.Y:F6},{pose[i].Rotation.Z:F6},{pose[i].Rotation.W:F6}")));
        // The hips, the head, and the right and left hand IK targets of the rig.
        var traced = new[] { Packages.HumanoidRoles.Gh3["hips"], Packages.HumanoidRoles.Gh3["head"] }
            .Concat(new[] { "right", "left" }.Select(hand => player.Motion.Ik.FirstOrDefault(chain => chain.Hand == hand)?.Target ?? ""));
        var values = Values(traced, raw);
        var walkIndex = StageTimeline.UpperBound(player.Walks, time, w => w.Time) - 1;
        var walkAge = walkIndex >= 0 ? time - player.Walks[walkIndex].Time : -1;
        var walkPose = new BandPose[raw.Length];
        var walk = walkAge >= 0 && walkAge < player.Walks[walkIndex].Duration ? player.Performance.SampleWalk(walkAge, player.Walks[walkIndex].Backward, walkPose) : null;
        var walkValues = walk is { } walking ? FormattableString.Invariant($" walk={walking.Clip} walkTime={walking.ClipTime:F4} walkBones={Values(traced, walkPose)}") : "";
        var face = player.Role == "singer" ? player.Performance.SampleFace(time, raw) : null;
        MelonLogger.Msg(FormattableString.Invariant($"[trace] band t={time:F3} role={player.Role} profile={ProfileName(player.Motion)} ") +
            FormattableString.Invariant($"set={(player.Motion.IsLibrary ? "library" : player.Motion.SetName)} stance={segment.Stance} clip={segment.Clip} clipTime={clipTime:F4} ") +
            FormattableString.Invariant($"scale={player.Root.localScale.x:F3} bones={values} hairPeak={player.Hair?.PeakSpeed ?? 0:F2}") +
            (face is { } facial ? FormattableString.Invariant($" face={facial.Clip} faceTime={facial.ClipTime:F4} faceBones={Values(TraceFaceBones, raw)}") : "") + walkValues +
            (player.Retarget is { } retarget ? FormattableString.Invariant($" retarget handMiss={retarget.Rig.HandMiss * 100:F2}cm move={retarget.Rig.InstrumentMove.Length() * 100:F1}cm") +
                (retarget.Rig.HasMicrophone ? FormattableString.Invariant($" micToMouth={System.Numerics.Vector3.Distance(retarget.Rig.MicrophoneHead, retarget.Rig.Mouth) * 100:F1}cm ") +
                    FormattableString.Invariant($"gh3MicToMouth={System.Numerics.Vector3.Distance(retarget.Rig.Gh3MicrophoneHead, retarget.Rig.Gh3Mouth) * 100:F1}cm mouthHandMiss={retarget.Rig.MouthMiss * 100:F1}cm") : "") : ""));
    }

    private static string ProfileName(CharacterMotion motion) => motion.IsLibrary ? "library" : motion.Profile.Id;

    // GH3_SKIN_TRACE: the face of a character with its own skeleton: the blend shape weights of the face slots and the
    // turns (degrees, rotation vector in the character frame at rest) of the head from its rest pose and of the eyes from
    // their rest pose relative to the head.
    private static void TraceFace(string role, StageRetarget retarget, double time)
    {
        var rig = retarget.Rig;
        static string Degrees(System.Numerics.Vector3 v) => FormattableString.Invariant($"{v.X:F2},{v.Y:F2},{v.Z:F2}");
        var head = rig.Role("head");
        var turns = new List<string>();
        if (head >= 0)
        {
            turns.Add("head=" + Degrees(rig.RestTurn(head, -1)));
            if (rig.MappedSource(head) is var source and >= 0)
            {
                var rest = rig.SourceRestRotation(source);
                turns.Add("gh3HeadRestUp=" + Degrees(System.Numerics.Vector3.Transform(System.Numerics.Vector3.UnitX, rest)) +
                    " gh3HeadRestFront=" + Degrees(System.Numerics.Vector3.Transform(System.Numerics.Vector3.UnitY, rest)));
            }
            foreach (var eye in new[] { "leftEye", "rightEye" })
                if (rig.Role(eye) is var bone and >= 0) turns.Add(eye + "=" + Degrees(rig.RestTurn(bone, head)));
        }
        MelonLogger.Msg(FormattableString.Invariant($"[trace] face t={time:F3} role={role} ") + string.Join(" ", turns) +
            (retarget.Face is { } face ? " weights=" + face.Trace : ""));
    }

    private static BandPose SamplePose(Performer player, double time, BandPose[] pose)
    {
        if (player.Retarget is { } retarget)
        {
            var root = SampleRig(player, time, retarget.SourcePose, retarget.Source);
            var (right, left) = player.Performance?.ArmGain ?? (0, 0);
            retarget.Rig.Apply(retarget.SourcePose, pose, right, left);
            retarget.Constraints?.Apply(pose, 0, 0);
            return root;
        }
        var result = SampleRig(player, time, pose, player.Animation);
        Array.Copy(player.ExtraBind, 0, pose, player.Animation.Parents.Length, player.ExtraBind.Length);
        return result;
    }

    // The root and the local poses of the rig that plays the performance.
    private static BandPose SampleRig(Performer player, double time, BandPose[] pose, BandAnimation rig)
    {
        var position = player.Start;
        var rotation = player.Rotation;
        if (player.Performance != null)
        {
            var walkIndex = StageTimeline.UpperBound(player.Walks, time, w => w.Time) - 1;
            var walkAge = -1.0;
            var walkWeight = 1f;
            var walkBackward = false;
            if (walkIndex >= 0)
            {
                var walk = player.Walks[walkIndex];
                var age = time - walk.Time;
                position = Vector3.Lerp(walk.From, walk.To, (float)Math.Clamp(age / walk.Duration, 0, 1));
                rotation = walk.Rotation;
                if (age < walk.Duration)
                {
                    walkAge = age;
                    walkWeight = (float)Math.Clamp(Math.Min(age, walk.Duration - age) / WalkBlend, 0, 1);
                    var facing = Quaternion.LookRotation(walk.Backward ? walk.To - walk.From : walk.From - walk.To, Vector3.up);
                    rotation = Quaternion.Slerp(walk.Rotation, facing, walkWeight);
                }
                walkBackward = walk.Backward;
            }
            player.Performance.Sample(time, walkAge, walkWeight, walkBackward);
            Array.Copy(player.Performance.Pose, pose, player.Performance.Pose.Length);
        }
        else if (rig.Clips.Length == 0) Array.Copy(rig.BindPose, pose, rig.BindPose.Length);
        else
        {
            var sample = rig.At(time);
            for (var i = 0; i < rig.Parents.Length; i++) pose[i] = rig.Sample(sample, i);
        }
        return new(new(position.x, position.y, position.z), new(rotation.x, rotation.y, rotation.z, rotation.w));
    }

    private static void Apply(Transform bone, BandPose pose)
    {
        bone.localPosition = new Vector3(pose.Position.X, pose.Position.Y, pose.Position.Z);
        bone.localRotation = new Quaternion(pose.Rotation.X, pose.Rotation.Y, pose.Rotation.Z, pose.Rotation.W);
    }

    public void Dispose()
    {
        foreach (var assets in _assets) assets.Dispose();
        _assets.Clear();
        _players.Clear();
        _performance = null;
        Positions.Clear();
    }
}
