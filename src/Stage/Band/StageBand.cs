using System.Text.Json;
using MelonLoader;
using UnityEngine;

namespace Gh3Skin;

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

internal sealed class StageBand : IDisposable
{
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
        IReadOnlyDictionary<string, Packages.CharacterModel>? characters = null)
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
            CharacterMotion? motion = null;
            if (character != null && character.Library.Length > 0 && member.Role is not ("guitarist" or "bassist"))
            {
                MelonLogger.Warning($"GH3 band {member.Role}: character {character.Id} has a guitarist profile of {character.Library}; the venue default plays");
                character = null;
            }
            if (character != null && character.Library.Length > 0)
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
            if (motion != null)
            {
                // GH3 create_guitarist: the profile `scale` scales the whole band member (Judy 0.9).
                performer.transform.localScale = new Vector3(motion.Profile.Scale, motion.Profile.Scale, motion.Profile.Scale);
                animation = BandAnimation.FromRig(motion.Profile.Parents, motion.Profile.Bind);
                bones = Bones(animation);
                assets = LoadOwnCharacter(character!, motion, directory, member, performer, layer, bones, roleAnimation, lighting, surfaceShader);
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
                    assets = LoadCharacter(character, directory, member, performer, layer, bones, lighting, surfaceShader, hair, out characterHair);
            }
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
            var player = new Performer { Role = member.Role, Root = performer.transform, Bones = bones, Animation = animation, Model = assets[0],
                Start = performer.transform.localPosition, Rotation = performer.transform.localRotation, Pose = new BandPose[bones.Length],
                Motion = motion, RoleBind = roleAnimation.BindPose };
            var hairDefinition = motion != null ? motion.Hair :
                characterHair ?? (hair != null && hair.Members.TryGetValue(member.Role, out var roleHair) ? roleHair : null);
            if (hairDefinition != null)
                player.Hair = new StageHair(hairDefinition, animation.Parents, (time, pose) => SamplePose(player, time, pose));
            _players.Add(player);
        }
        MelonLogger.Msg($"GH3 band loaded: {_players.Count} performers; default male singer; original GH3 animations");
        if (hair != null) MelonLogger.Msg($"GH3 hair physics: {hair.Members.Values.Sum(m => m.Joints.Length)} original joints");
    }

    // A character package plays the role on the role's rig: its joints map to the rig bones by name (CharacterRig), and its
    // own inverse bind matrices skin the mesh. It draws through StageAssets as the library members do. Its own instrument
    // plays in the role of its profile; otherwise the role's instrument comes from the library band model (BandAttachments).
    // In another role, `characterHair` is the hair of its profile on the role's rig. Null: use the venue default.
    private static List<StageAssets>? LoadCharacter(Packages.CharacterModel character, string directory, BandMember member, GameObject performer, int layer,
        Transform[] bones, bool lighting, Shader? surfaceShader, HairBank? hair, out HairDefinition? characterHair)
    {
        var role = member.Role;
        var children = performer.transform.childCount;
        characterHair = null;
        StageAssets? assets = null;
        try
        {
            var library = Path.GetDirectoryName(Path.GetFullPath(directory))!;
            var (rig, parents, _) = Packages.CharacterImporter.Rig(library, role);
            var ownRole = character.Role == role;
            var (profileBones, profileParents, _) = ownRole ? (rig, parents, Array.Empty<float[]>()) : Packages.CharacterImporter.Rig(library, character.Role);
            HairDefinition? profileHair = null;
            if (hair != null && hair.Members.TryGetValue(character.Role, out var found)) profileHair = found;
            var hairJoints = profileHair?.Joints.Select(j => j.Bone).ToHashSet() ?? new HashSet<int>();
            var (map, bind) = Packages.CharacterRig.Map(character.Joints, profileBones, profileParents, rig, hairJoints, ownRole)
                ?? throw new InvalidDataException($"its joints are not all in the {role} rig");
            if (!ownRole && profileHair != null)
                characterHair = profileHair.Remap(bone => bone < profileBones.Length ? Array.IndexOf(rig, profileBones[bone]) : -1);
            var bindposes = bind.Select(b => character.Bindposes[b]).Select(m => new Matrix4x4
            {
                m00 = m[0], m01 = m[1], m02 = m[2], m03 = m[3], m10 = m[4], m11 = m[5], m12 = m[6], m13 = m[7],
                m20 = m[8], m21 = m[9], m22 = m[10], m23 = m[11], m30 = m[12], m31 = m[13], m32 = m[14], m33 = m[15],
            }).ToArray();
            var body = Packages.BandAttachments.BodyJoints(rig, parents);
            var parts = Packages.BandAttachments.ReadParts(Path.Combine(character.Folder, "meshes.bin"));
            var ownParts = Packages.BandAttachments.AttachmentParts(parts, body, map);
            var (add, keepOwn, note) = Packages.BandAttachments.Choose(ownParts.Length > 0, ownRole, character.Instrument, member.Instrument, role);
            assets = new StageAssets();
            assets.Load(character.Folder, performer, layer, map.Select(i => bones[i]).ToArray(), lighting, surfaceShader: surfaceShader, bindposes: bindposes,
                parts: keepOwn || ownParts.Length == 0 ? null : Enumerable.Range(0, parts.Length).Except(ownParts).ToArray());
            var result = new List<StageAssets> { assets };
            if (add) note = LoadInstrument(Path.Combine(directory, role), body, performer, layer, bones, lighting, surfaceShader, result) ?? note;
            MelonLogger.Msg($"GH3 band {role}: character {character.Id} ({character.Name}, {character.Role} profile) with {note}" +
                (characterHair != null ? $"; hair of the {character.Role} profile ({characterHair.Joints.Length} joints)" : ""));
            return result;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or Packages.PackageException or UnauthorizedAccessException or InvalidOperationException)
        {
            assets?.Dispose();
            characterHair = null;
            for (var i = performer.transform.childCount - 1; i >= children; i--)
                UnityEngine.Object.Destroy(performer.transform.GetChild(i).gameObject);
            MelonLogger.Warning($"GH3 band {role}: character {character.Id} could not load ({error.Message}); the venue default plays");
            return null;
        }
    }

    // A character on a character library profile (format 1.1): its mesh on its own rig, with the GLB inverse bind matrices.
    // Its own instrument plays in the role of its profile. In another role (GH3 co-op: a guitarist character plays the
    // bass with its own animation set), the role's library instrument hangs on the bones with the same names, bound
    // with the role rig's rest pose, so that it follows the character's instrument bones.
    private static List<StageAssets>? LoadOwnCharacter(Packages.CharacterModel character, CharacterMotion motion, string directory, BandMember member,
        GameObject performer, int layer, Transform[] bones, BandAnimation roleRig, bool lighting, Shader? surfaceShader)
    {
        var role = member.Role;
        StageAssets? assets = null;
        try
        {
            var profile = motion.Profile;
            var (map, bind) = Packages.CharacterRig.Map(character.Joints, profile.Bones, profile.Parents, profile.Bones, new HashSet<int>(), ownRole: true)
                ?? throw new InvalidDataException($"its joints are not all in profile {profile.Id}");
            var bindposes = bind.Select(b => character.Bindposes[b]).Select(Unity).ToArray();
            var body = Packages.BandAttachments.BodyJoints(profile.Bones, profile.Parents);
            var parts = Packages.BandAttachments.ReadParts(Path.Combine(character.Folder, "meshes.bin"));
            var ownParts = Packages.BandAttachments.AttachmentParts(parts, body, map);
            var ownRole = character.Role == role;
            var (add, keepOwn, note) = Packages.BandAttachments.Choose(ownParts.Length > 0, ownRole, character.Instrument, member.Instrument, role);
            assets = new StageAssets();
            assets.Load(character.Folder, performer, layer, map.Select(i => bones[i]).ToArray(), lighting, surfaceShader: surfaceShader, bindposes: bindposes,
                parts: keepOwn || ownParts.Length == 0 ? null : Enumerable.Range(0, parts.Length).Except(ownParts).ToArray());
            var result = new List<StageAssets> { assets };
            if (add)
            {
                var library = Path.GetDirectoryName(Path.GetFullPath(directory))!;
                var (roleBones, roleParents, _) = Packages.CharacterImporter.Rig(library, role);
                var byName = roleBones.Select(name => Array.IndexOf(profile.Bones, name)).ToArray();
                if (byName.Any(i => i < 0)) throw new InvalidDataException($"the {role} instrument bones are not in profile {profile.Id}");
                note = LoadInstrument(Path.Combine(directory, role), Packages.BandAttachments.BodyJoints(roleBones, roleParents), performer, layer,
                    byName.Select(i => bones[i]).ToArray(), lighting, surfaceShader, result, RestBindposes(roleRig)) ?? note;
            }
            MelonLogger.Msg($"GH3 band {role}: character {character.Id} ({character.Name}) on its GH3 profile {profile.Id} with {note}; " +
                $"animation set {motion.SetName}, scale {profile.Scale:0.##}, {(motion.Hair != null ? $"hair {motion.Hair.Joints.Length} joints" : "no hair physics")}, IK {profile.Ik}");
            return result;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or Packages.PackageException or UnauthorizedAccessException or InvalidOperationException)
        {
            assets?.Dispose();
            MelonLogger.Warning($"GH3 band {role}: character {character.Id} could not load ({error.Message}); the venue default plays");
            return null;
        }
    }

    private static Matrix4x4 Unity(float[] m) => new()
    {
        m00 = m[0], m01 = m[1], m02 = m[2], m03 = m[3], m10 = m[4], m11 = m[5], m12 = m[6], m13 = m[7],
        m20 = m[8], m21 = m[9], m22 = m[10], m23 = m[11], m30 = m[12], m31 = m[13], m32 = m[14], m33 = m[15],
    };

    // Inverse bind matrices of a rig at its rest pose, in the space of the performer (row-major, as bindposes.bin).
    private static Matrix4x4[] RestBindposes(BandAnimation rig)
    {
        var world = new System.Numerics.Matrix4x4[rig.Parents.Length];
        var result = new Matrix4x4[world.Length];
        for (var i = 0; i < world.Length; i++)
        {
            var pose = rig.BindPose[i];
            // System.Numerics uses row vectors: local = rotation, then translation; world = local, then the parent's world.
            var local = System.Numerics.Matrix4x4.CreateFromQuaternion(pose.Rotation) * System.Numerics.Matrix4x4.CreateTranslation(pose.Position);
            world[i] = rig.Parents[i] < 0 ? local : local * world[rig.Parents[i]];
        }
        for (var i = 0; i < world.Length; i++)
        {
            if (!System.Numerics.Matrix4x4.Invert(world[i], out var inverse)) throw new InvalidDataException("Invalid rig rest pose");
            // Transposed to the column-vector layout of Unity.
            result[i] = Unity(new[]
            {
                inverse.M11, inverse.M21, inverse.M31, inverse.M41, inverse.M12, inverse.M22, inverse.M32, inverse.M42,
                inverse.M13, inverse.M23, inverse.M33, inverse.M43, inverse.M14, inverse.M24, inverse.M34, inverse.M44,
            });
        }
        return result;
    }

    // The attachment parts of the role's library band model, on the role's bones with the library bind pose. A failure
    // leaves the character without an instrument; returns the note for the log then.
    private static string? LoadInstrument(string folder, HashSet<int> body, GameObject performer, int layer, Transform[] bones, bool lighting,
        Shader? surfaceShader, List<StageAssets> result, Matrix4x4[]? bindposes = null)
    {
        var children = performer.transform.childCount;
        StageAssets? instrument = null;
        try
        {
            var parts = Packages.BandAttachments.AttachmentParts(Packages.BandAttachments.ReadParts(Path.Combine(folder, "meshes.bin")), body);
            if (parts.Length == 0) return "no instrument (the library band model has no attachment parts)";
            instrument = new StageAssets();
            instrument.Load(folder, performer, layer, bones, lighting, surfaceShader: surfaceShader, parts: parts, bindposes: bindposes);
            result.Add(instrument);
            return null;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException)
        {
            instrument?.Dispose();
            for (var i = performer.transform.childCount - 1; i >= children; i--)
                UnityEngine.Object.Destroy(performer.transform.GetChild(i).gameObject);
            MelonLogger.Warning($"GH3 band: the library instrument could not load ({error.Message})");
            return "no instrument";
        }
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
            player.Performance = new PerformancePlayer(player.Role, directory, definition, player.Animation.BindPose, timeline,
                player.Animation.Parents, _performance.Constants, player.Motion);
            player.Performance.Prepare();
            var position = player.Start;
            var speed = 1.0;
            if (definition.Groups.TryGetValue("walk/cycle_anim/med", out var cycle))
            {
                var clip = definition.Clips[cycle[0]];
                speed = Math.Max(0.1, clip.RootDistance / clip.Duration);
            }
            foreach (var cue in timeline.Track("walk", player.Role))
            {
                if (waypoints == null || !waypoints.TryGetValue(cue.Value, out var waypoint)) continue;
                var target = StageAssets.Vector(waypoint.Position);
                var q = waypoint.Rotation;
                player.Walks.Add(new Walk(cue.Time, Math.Max(0.01, Vector3.Distance(position, target) / speed), position, target, new(q[0], q[1], q[2], q[3])));
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
        var transform = index < 0 ? player.Root : player.Bones[index];
        var p = transform.position;
        var q = transform.rotation;
        return new BandPose(new(p.x, p.y, p.z), new(q.x, q.y, q.z, q.w));
    }

    public Transform? HandFireBone(string bone, int fallback)
    {
        var player = _players.FirstOrDefault(p => p.Role == "guitarist");
        if (player == null) return null;
        var index = player.Definition == null ? fallback : Array.IndexOf(player.Definition.Bones, bone);
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
            player.Hair?.Sample(time - _start);
            if (player.Hair is { PeakSpeed: > StageHair.WarningSpeed } hair && !player.HairWarned)
            {
                player.HairWarned = true;
                MelonLogger.Warning(FormattableString.Invariant($"GH3 band {player.Role}: hair joint {player.Definition?.Bones.ElementAtOrDefault(hair.PeakBone) ?? hair.PeakBone.ToString()} ") +
                    FormattableString.Invariant($"moves at {hair.PeakSpeed:F0} m/s (profile {player.Motion?.Profile.Id ?? "library"}); its ragdoll may not fit the rig"));
            }
            var root = SamplePose(player, time, player.Pose);
            Apply(player.Root, root);
            player.Hair?.Apply(player.Pose);
            for (var i = 0; i < player.Bones.Length; i++) Apply(player.Bones[i], player.Pose[i]);
        }
    }

    private double _traceNext = double.NegativeInfinity;
    private static readonly string[] TraceBones = { "bone_pelvis", "bone_head", "Bone_IK_Hand_Guitar_R", "Bone_IK_Hand_Guitar_L" };

    // GH3_SKIN_TRACE: the body clip and its local pose of a few bones, to compare with the GH3 clip on the GH3 skeleton.
    private static void Trace(Performer player, double time)
    {
        var raw = new BandPose[player.Bones.Length];
        var (segment, clipTime) = player.Performance!.SampleClip(time, raw);
        var bones = player.Definition!.Bones;
        var values = TraceBones.Select(name => Array.IndexOf(bones, Gh3Checksum.Of(name))).Where(i => i >= 0).Select(i =>
            FormattableString.Invariant($"{i}:{raw[i].Position.X:F6},{raw[i].Position.Y:F6},{raw[i].Position.Z:F6},{raw[i].Rotation.X:F6},{raw[i].Rotation.Y:F6},{raw[i].Rotation.Z:F6},{raw[i].Rotation.W:F6}"));
        MelonLogger.Msg(FormattableString.Invariant($"[trace] band t={time:F3} role={player.Role} profile={player.Motion?.Profile.Id ?? "library"} ") +
            FormattableString.Invariant($"set={player.Motion?.SetName ?? "library"} stance={segment.Stance} clip={segment.Clip} clipTime={clipTime:F4} ") +
            FormattableString.Invariant($"scale={player.Root.localScale.x:F3} bones={string.Join(";", values)} hairPeak={player.Hair?.PeakSpeed ?? 0:F2}"));
    }

    private static BandPose SamplePose(Performer player, double time, BandPose[] pose)
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
                    walkWeight = (float)Math.Clamp(Math.Min(age, walk.Duration - age) / 0.2, 0, 1);
                    var facing = Quaternion.LookRotation(walk.From - walk.To, Vector3.up);
                    rotation = Quaternion.Slerp(walk.Rotation, facing, walkWeight);
                }
            }
            player.Performance.Sample(time, walkAge, walkWeight);
            Array.Copy(player.Performance.Pose, pose, pose.Length);
        }
        else if (player.Animation.Clips.Length == 0) Array.Copy(player.Animation.BindPose, pose, pose.Length);
        else
        {
            var sample = player.Animation.At(time);
            for (var i = 0; i < pose.Length; i++) pose[i] = player.Animation.Sample(sample, i);
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
