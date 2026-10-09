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
}

internal sealed class StageBand : IDisposable
{
    private sealed class Performer
    {
        public string Role = "";
        public Transform Root = null!;
        public Transform[] Bones = Array.Empty<Transform>();
        public BandAnimation Animation = null!;
        public PerformancePlayer? Performance;
        public PerformanceMember? Definition;
        public StageHair? Hair;
        public BandPose[] Pose = Array.Empty<BandPose>();
        public Vector3 Start;
        public Quaternion Rotation;
        public List<Walk> Walks = new();
    }
    private sealed record Walk(double Time, double Duration, Vector3 From, Vector3 To, Quaternion Rotation);
    private readonly List<StageAssets> _assets = new();
    private readonly List<Performer> _players = new();
    private StagePerformance? _performance;
    private SkinnedGlbSpike? _spike;
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
            var animation = new BandAnimation(reader);
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
            var bones = new Transform[animation.Parents.Length];
            for (var i = 0; i < bones.Length; i++)
                bones[i] = new GameObject($"GH3 {member.Role} Bone {i}") { layer = layer }.transform;
            for (var i = 0; i < bones.Length; i++)
            {
                bones[i].SetParent(animation.Parents[i] < 0 ? performer.transform : bones[animation.Parents[i]], false);
                Apply(bones[i], animation.BindPose[i]);
            }
            var assets = characters != null && characters.TryGetValue(member.Role, out var character)
                ? LoadCharacter(character, directory, member.Role, performer, layer, bones, lighting, surfaceShader) : null;
            if (assets == null)
            {
                assets = new StageAssets();
                assets.Load(folder, performer, layer, bones, lighting, surfaceShader: surfaceShader);
            }
            _assets.Add(assets);
            stage.SurfaceMaterials.AddRange(assets.SurfaceMaterials);
            stage.ShadedSurfaces.AddRange(assets.ShadedSurfaces);
            stage.BandMeshes.AddRange(assets.BandMeshes);
            stage.TransparentMeshes.AddRange(assets.TransparentMeshes);
            var player = new Performer { Role = member.Role, Root = performer.transform, Bones = bones, Animation = animation,
                Start = performer.transform.localPosition, Rotation = performer.transform.localRotation, Pose = new BandPose[bones.Length] };
            if (hair != null && hair.Members.TryGetValue(member.Role, out var hairDefinition))
                player.Hair = new StageHair(hairDefinition, animation.Parents, (time, pose) => SamplePose(player, time, pose));
            _players.Add(player);
        }
        MelonLogger.Msg($"GH3 band loaded: {_players.Count} performers; default male singer; original GH3 animations");
        if (hair != null) MelonLogger.Msg($"GH3 hair physics: {hair.Members.Values.Sum(m => m.Joints.Length)} original joints");
    }

    // A character package plays the role on the role's rig: its joints map to the rig bones by name, and its own inverse
    // bind matrices skin the mesh. It draws through StageAssets as the library members do. Null: use the venue default.
    private static StageAssets? LoadCharacter(Packages.CharacterModel character, string directory, string role, GameObject performer, int layer,
        Transform[] bones, bool lighting, Shader? surfaceShader)
    {
        var children = performer.transform.childCount;
        StageAssets? assets = null;
        try
        {
            var rig = Packages.CharacterImporter.Rig(Path.GetDirectoryName(Path.GetFullPath(directory))!, role).Bones;
            var map = character.Map(rig) ?? throw new InvalidDataException($"its joints are not all in the {role} rig");
            var bindposes = character.Bindposes.Select(m => new Matrix4x4
            {
                m00 = m[0], m01 = m[1], m02 = m[2], m03 = m[3], m10 = m[4], m11 = m[5], m12 = m[6], m13 = m[7],
                m20 = m[8], m21 = m[9], m22 = m[10], m23 = m[11], m30 = m[12], m31 = m[13], m32 = m[14], m33 = m[15],
            }).ToArray();
            assets = new StageAssets();
            assets.Load(character.Folder, performer, layer, map.Select(i => bones[i]).ToArray(), lighting, surfaceShader: surfaceShader, bindposes: bindposes);
            MelonLogger.Msg($"GH3 band {role}: character {character.Id} ({character.Name}, {character.Role} profile)");
            return assets;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or Packages.PackageException or UnauthorizedAccessException or InvalidOperationException)
        {
            assets?.Dispose();
            for (var i = performer.transform.childCount - 1; i >= children; i--)
                UnityEngine.Object.Destroy(performer.transform.GetChild(i).gameObject);
            MelonLogger.Warning($"GH3 band {role}: character {character.Id} could not load ({error.Message}); the venue default plays");
            return null;
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
            waited += player.Performance.Clips.Wait();
            clips += player.Performance.Clips.Prefetched;
            worker = Math.Max(worker, player.Performance.Clips.PrefetchMilliseconds);
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
            player.Definition = definition;
            if (SkinnedGlbSpike.Enabled && player.Role == "guitarist")
                _spike = SkinnedGlbSpike.Attach(player.Root, player.Bones, definition.Bones, _assets[_players.IndexOf(player)]);
            player.Performance = new PerformancePlayer(player.Role, directory, definition, player.Animation.BindPose, timeline,
                player.Animation.Parents, _performance.Constants);
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
            $"{timeline.Track("drum").Length} drum cues; {timeline.Track("face", "singer").Length} singer facial cues");
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
        foreach (var player in _players)
        {
            player.Hair?.Sample(time - _start);
            var root = SamplePose(player, time, player.Pose);
            Apply(player.Root, root);
            player.Hair?.Apply(player.Pose);
            for (var i = 0; i < player.Bones.Length; i++) Apply(player.Bones[i], player.Pose[i]);
        }
    }

    public void SpikeUpdate(double time, Camera camera) => _spike?.Update(time, camera);

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
        _spike?.Dispose();
        _spike = null;
        foreach (var assets in _assets) assets.Dispose();
        _assets.Clear();
        _players.Clear();
        _performance = null;
        Positions.Clear();
    }
}
