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
        // A character on a character library profile: its rig, animation set, hair, and IK (null: the role's).
        public CharacterMotion? Motion;
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
    private sealed record Walk(double Time, double Duration, Vector3 From, Vector3 To, Quaternion Rotation);
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
            CharacterMotion? motion = null;
            if (character != null && character.Library.Length > 0 && member.Role is not ("guitarist" or "bassist") && character.Skeleton == null)
            {
                MelonLogger.Warning($"GH3 band {member.Role}: character {character.Id} has a guitarist profile of {character.Library}; the venue default plays");
                character = null;
            }
            if (character != null && character.Library.Length > 0 && member.Role is "guitarist" or "bassist")
                try { motion = CharacterMotion.Create(character); }
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
            var animation = roleAnimation;
            Transform[] bones;
            HairDefinition? characterHair = null;
            List<StageAssets>? assets = null;
            CharacterExtras? extras = null;
            StageRetarget? retarget = null;
            if (character?.Skeleton != null)
            {
                var own = LoadRetargeted(character, motion, directory, member, performer, layer, roleAnimation, lighting, surfaceShader, instrument, Bones);
                if (own != null)
                {
                    (assets, retarget, animation, bones, characterHair) = own.Value;
                    var parentsOwn = animation.Parents;
                    var player2 = new Performer { Role = member.Role, Root = performer.transform, Bones = bones, Animation = animation, Model = assets[0],
                        Start = performer.transform.localPosition, Rotation = performer.transform.localRotation, Pose = new BandPose[bones.Length],
                        Motion = motion, RoleBind = roleAnimation.BindPose, Retarget = retarget };
                    foreach (var part in assets)
                    {
                        _assets.Add(part);
                        stage.SurfaceMaterials.AddRange(part.SurfaceMaterials);
                        stage.ShadedSurfaces.AddRange(part.ShadedSurfaces);
                        stage.BandMeshes.AddRange(part.BandMeshes);
                        stage.TransparentMeshes.AddRange(part.TransparentMeshes);
                    }
                    if (characterHair != null)
                        try { player2.Hair = new StageHair(characterHair, parentsOwn, (time, pose) => SamplePose(player2, time, pose)); }
                        catch (InvalidDataException error)
                        {
                            MelonLogger.Warning($"GH3 band {member.Role}: the chains of character {character.Id} could not load ({error.Message}); no hair physics");
                        }
                    _players.Add(player2);
                    continue;
                }
                for (var i = performer.transform.childCount - 1; i >= 0; i--) UnityEngine.Object.Destroy(performer.transform.GetChild(i).gameObject);
                motion = null;
                character = null;
            }
            if (motion != null)
            {
                // GH3 create_guitarist: the profile `scale` scales the whole band member (Judy 0.9).
                performer.transform.localScale = new Vector3(motion.Profile.Scale, motion.Profile.Scale, motion.Profile.Scale);
                animation = BandAnimation.FromRig(motion.Profile.Parents, motion.Profile.Bind);
                bones = Bones(animation);
                assets = LoadOwnCharacter(character!, motion, directory, member, performer, layer, bones, roleAnimation, lighting, surfaceShader, instrument, animation,
                    out extras);
                if (assets == null)
                {
                    for (var i = performer.transform.childCount - 1; i >= 0; i--) UnityEngine.Object.Destroy(performer.transform.GetChild(i).gameObject);
                    performer.transform.localScale = Vector3.one;
                    motion = null;
                    animation = roleAnimation;
                    bones = Bones(animation);
                }
            }
            else
            {
                bones = Bones(animation);
                if (character != null)
                    assets = LoadCharacter(character, directory, member, performer, layer, bones, lighting, surfaceShader, hair, out characterHair, instrument, animation,
                        out extras);
            }
            if (assets == null && instrument != null)
                assets = LoadMemberWithInstrument(directory, member, performer, layer, bones, animation, lighting, surfaceShader, instrument);
            if (assets == null)
            {
                var model = new StageAssets();
                assets = new List<StageAssets> { model };
                model.Load(folder, performer, layer, bones, lighting, surfaceShader: surfaceShader);
            }
            foreach (var part in assets)
            {
                _assets.Add(part);
                stage.SurfaceMaterials.AddRange(part.SurfaceMaterials);
                stage.ShadedSurfaces.AddRange(part.ShadedSurfaces);
                stage.BandMeshes.AddRange(part.BandMeshes);
                stage.TransparentMeshes.AddRange(part.TransparentMeshes);
            }
            var parents = animation.Parents;
            if (extras != null)
            {
                bones = bones.Concat(extras.Bones).ToArray();
                parents = parents.Concat(extras.Parents).ToArray();
            }
            var player = new Performer { Role = member.Role, Root = performer.transform, Bones = bones, Animation = animation, Model = assets[0],
                Start = performer.transform.localPosition, Rotation = performer.transform.localRotation, Pose = new BandPose[bones.Length],
                Motion = motion, RoleBind = roleAnimation.BindPose, ExtraBind = extras?.Bind ?? Array.Empty<BandPose>() };
            var hairDefinition = motion != null ? motion.Hair :
                characterHair ?? (hair != null && hair.Members.TryGetValue(member.Role, out var roleHair) ? roleHair : null);
            if (extras != null) hairDefinition = HairDefinition.Merge(hairDefinition, extras.Joints, extras.Colliders);
            if (hairDefinition != null)
                try { player.Hair = new StageHair(hairDefinition, parents, (time, pose) => SamplePose(player, time, pose)); }
                catch (InvalidDataException error) when (extras is { Joints.Length: > 0 })
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
            if (player.Motion is { } motion)
            {
                // The song facial clips stay on the band library member of the role; they move to the profile rig as the
                // set clips do.
                motion.Face = definition;
                motion.FaceDirectory = Path.Combine(directory, player.Role);
                motion.FaceOffsets = CharacterMotion.RestOffsets(motion.Profile.Bones, motion.Profile.Bind, definition.Bones,
                    player.RoleBind.Select(p => new[] { p.Position.X, p.Position.Y, p.Position.Z }).ToArray());
                definition = motion.Member;
            }
            player.Definition = definition;
            var driver = player.Retarget?.Source ?? player.Animation;
            player.Performance = new PerformancePlayer(player.Role, directory, definition, driver.BindPose, timeline,
                driver.Parents, _performance.Constants, player.Motion);
            player.Performance.Prepare();
            if (player.Retarget?.Face is { } face)
            {
                face.Prepare(timeline, player.Role);
                MelonLogger.Msg($"GH3 band {player.Role}: face blend shapes with {(face.LipSync ? "lip sync from the sung notes" : "blinks")}");
            }
            var position = player.Start;
            var speed = 1.0;
            if (definition.Groups.TryGetValue("walk/cycle_anim/med", out var cycle))
            {
                var clip = definition.Clips[cycle[0]];
                speed = Math.Max(MinimumWalkSpeed, clip.RootDistance / clip.Duration);
            }
            foreach (var cue in timeline.Track("walk", player.Role))
            {
                if (waypoints == null || !waypoints.TryGetValue(cue.Value, out var waypoint)) continue;
                var target = StageAssets.Vector(waypoint.Position);
                var q = waypoint.Rotation;
                player.Walks.Add(new Walk(cue.Time, Math.Max(MinimumWalk, Vector3.Distance(position, target) / speed), position, target, new(q[0], q[1], q[2], q[3])));
                position = target;
            }
        }
        MelonLogger.Msg($"GH3 original performance: {timeline.Track("stance", "guitarist").Length} guitar stance cues; " +
            $"{timeline.Track("drum").Length} drum cues; {timeline.Track("face", "singer").Length} singer facial cues; " +
            LipSyncSummary(timeline));
    }

    private string LipSyncSummary(StageTimeline timeline)
    {
        var sung = timeline.Track("sing", "singer").Length;
        var lipSync = _players.FirstOrDefault(p => p.Role == "singer")?.Performance?.LipSync;
        if (lipSync != null) return $"lip sync from {sung} sung notes ({lipSync.Syllables.Length} syllables)";
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
        if (BindingTrace.Enabled && time >= _traceNext)
        {
            _traceNext = Math.Floor(time * 2 + 1) / 2;
            foreach (var player in _players.Where(p => p.Performance != null && p.Role is "guitarist" or "bassist")) Trace(player, time);
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
                    FormattableString.Invariant($"moves at {hair.PeakSpeed:F0} m/s (profile {player.Motion?.Profile.Id ?? "library"}); its ragdoll may not fit the rig"));
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
                if (FrameProfile.Enabled) FrameProfile.Add("band.retarget", (System.Diagnostics.Stopwatch.GetTimestamp() - sampled) * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
            }
        }
    }

    private double _traceNext = double.NegativeInfinity;
    private static readonly string[] TraceBones = { "bone_pelvis", "bone_head", "Bone_IK_Hand_Guitar_R", "Bone_IK_Hand_Guitar_L" };

    // GH3_SKIN_TRACE: the body clip and its local pose of a few bones, to compare with the GH3 clip on the GH3 skeleton.
    private static void Trace(Performer player, double time)
    {
        var raw = new BandPose[(player.Retarget?.Source ?? player.Animation).Parents.Length];
        var (segment, clipTime) = player.Performance!.SampleClip(time, raw);
        var bones = player.Definition!.Bones;
        var values = TraceBones.Select(name => Array.IndexOf(bones, Gh3Checksum.Of(name))).Where(i => i >= 0).Select(i =>
            FormattableString.Invariant($"{i}:{raw[i].Position.X:F6},{raw[i].Position.Y:F6},{raw[i].Position.Z:F6},{raw[i].Rotation.X:F6},{raw[i].Rotation.Y:F6},{raw[i].Rotation.Z:F6},{raw[i].Rotation.W:F6}"));
        MelonLogger.Msg(FormattableString.Invariant($"[trace] band t={time:F3} role={player.Role} profile={player.Motion?.Profile.Id ?? "library"} ") +
            FormattableString.Invariant($"set={player.Motion?.SetName ?? "library"} stance={segment.Stance} clip={segment.Clip} clipTime={clipTime:F4} ") +
            FormattableString.Invariant($"scale={player.Root.localScale.x:F3} bones={string.Join(";", values)} hairPeak={player.Hair?.PeakSpeed ?? 0:F2}") +
            (player.Retarget is { } retarget ? FormattableString.Invariant($" retarget handMiss={retarget.Rig.HandMiss * 100:F2}cm move={retarget.Rig.InstrumentMove.Length() * 100:F1}cm") : ""));
    }

    private static BandPose SamplePose(Performer player, double time, BandPose[] pose)
    {
        if (player.Retarget is { } retarget)
        {
            var root = SampleRig(player, time, retarget.SourcePose, retarget.Source);
            var (right, left) = player.Performance?.ArmGain ?? (0, 0);
            retarget.Rig.Apply(retarget.SourcePose, pose, right, left);
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
                    var facing = Quaternion.LookRotation(walk.From - walk.To, Vector3.up);
                    rotation = Quaternion.Slerp(walk.Rotation, facing, walkWeight);
                }
            }
            player.Performance.Sample(time, walkAge, walkWeight);
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
