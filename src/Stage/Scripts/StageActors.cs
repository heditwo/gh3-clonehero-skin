using UnityEngine;

namespace Gh3Skin;

internal sealed class StageActors : IDisposable
{
    private sealed record Actor(GameObject Root, Transform[] Bones, BandAnimation Animation, StageActorDefinition Definition)
    {
        public StageActorState? State { get; set; }
    }
    private readonly List<StageAssets> _assets = new();
    private readonly List<Actor> _actors = new();
    public StageScriptState? Scripts { get; set; }
    public Dictionary<uint, StageScriptActorDefinition> ScriptDefinitions { get; } = new();
    public Dictionary<string, StageObjectPlacement> Placements { get; } = new();
    public Dictionary<uint, Transform> Objects { get; } = new();

    public void Load(string directory, StageAssets stage, GameObject root, int layer, Shader? surfaceShader = null)
    {
        var path = Path.Combine(directory, "actors.json");
        if (!File.Exists(path)) return;
        var bank = StageActorBank.Load(path);
        foreach (var actor in bank.Actors)
        {
            var folder = Path.Combine(directory, "actors", actor.Id);
            var section = LoadTiming.Now;
            var animationPath = Path.Combine(folder, "animation.bin");
            var animation = StageModelPrefetch.TakeAnimation(animationPath) ?? StageModelPrefetch.ReadAnimation(animationPath);
            LoadTiming.Add("actors: animation.bin", section);
            if (actor.Clip < 0 || actor.Clip >= animation.Clips.Length) throw new InvalidDataException("Invalid stage actor clip");
            var obj = new GameObject("Stage Actor " + actor.Id) { layer = layer };
            obj.transform.SetParent(root.transform, false);
            obj.transform.localPosition = StageAssets.Vector(actor.Transform.Position);
            var q = actor.Transform.Rotation;
            obj.transform.localRotation = new(q[0], q[1], q[2], q[3]);
            obj.transform.localScale = StageAssets.Vector(actor.Scale);
            var bones = animation.Parents.Select((_, i) => new GameObject("Actor Bone " + i) { layer = layer }.transform).ToArray();
            for (var i = 0; i < bones.Length; i++)
            {
                bones[i].SetParent(animation.Parents[i] < 0 ? obj.transform : bones[animation.Parents[i]], false);
                Apply(bones[i], animation.BindPose[i]);
            }
            var assets = new StageAssets();
            _assets.Add(assets);
            assets.Load(folder, obj, layer, bones, lighting: true, lightGroup: actor.LightGroup, surfaceShader: surfaceShader);
            stage.SurfaceMaterials.AddRange(assets.SurfaceMaterials);
            stage.ShadedSurfaces.AddRange(assets.ShadedSurfaces);
            stage.BandMeshes.AddRange(assets.BandMeshes);
            stage.TransparentMeshes.AddRange(assets.TransparentMeshes);
            obj.SetActive(actor.InitiallyVisible);
            _actors.Add(new(obj, bones, animation, actor));
            if (actor.Object.Length > 0 && !actor.Role.StartsWith("stage-diver/"))
            {
                var id = Gh3QbText.Key(actor.Object);
                if (Objects.ContainsKey(id)) throw new InvalidDataException($"Duplicate stage actor object {id:x8}: actor={actor.Id}, source={actor.Object}, path={path}");
                Objects.Add(id, obj.transform);
                ScriptDefinitions.Add(id, new(actor, animation.Clips.Select(clip => (double)clip.Duration).ToArray(), animation, animation.LoadBoneNames(Path.Combine(folder, "bones.json"))));
                Placements.Add(id.ToString("x8"), new() { Position = actor.Transform.Position, Rotation = actor.Transform.Rotation, Created = actor.Created, Script = actor.Script, Scale = actor.Scale });
            }
        }
        MelonLoader.MelonLogger.Msg($"Stage actors loaded: {_actors.Count}");
    }

    public void SetTimeline(StageTimeline timeline)
    {
        foreach (var actor in _actors)
            actor.State = new StageActorState(actor.Definition, actor.Animation.Clips.Select(clip => (double)clip.Duration), timeline);
    }

    public void Update(double time)
    {
        foreach (var actor in _actors)
        {
            if (actor.State == null) continue;
            if (actor.Definition.Object.Length > 0 && Scripts != null && Scripts.Actors.TryGetValue(Gh3QbText.Key(actor.Definition.Object), out var scriptActor) && scriptActor.Active)
            {
                var pose = Scripts.Sample(Gh3QbText.Key(actor.Definition.Object), time);
                var scripted = scriptActor.Sample(time);
                actor.Root.SetActive(pose.Visible);
                actor.Root.transform.localPosition = new(pose.Position.X, pose.Position.Y, pose.Position.Z);
                actor.Root.transform.localRotation = new(pose.Rotation.X, pose.Rotation.Y, pose.Rotation.Z, pose.Rotation.W);
                actor.Root.transform.localScale = new(pose.Scale.X, pose.Scale.Y, pose.Scale.Z);
                for (var i = 0; i < actor.Bones.Length; i++)
                {
                    var current = actor.Animation.SampleClip(actor.Animation.Clips[scripted.Clip], scripted.Time, i);
                    if (scripted.Blend < 1) current = BandPose.Mix(actor.Animation.SampleClip(actor.Animation.Clips[scripted.PreviousClip], scripted.PreviousTime, i), current, scripted.Blend);
                    Apply(actor.Bones[i], current);
                }
                continue;
            }
            var sample = actor.State.Sample(time);
            actor.Root.SetActive(sample.Visible);
            var track = actor.Animation.Clips[sample.Clip];
            for (var i = 0; i < actor.Bones.Length; i++) Apply(actor.Bones[i], actor.Animation.SampleClip(track, sample.Time, i));
        }
    }

    private static void Apply(Transform bone, BandPose pose)
    {
        var p = pose.Position; var q = pose.Rotation;
        bone.localPosition = new(p.X, p.Y, p.Z);
        bone.localRotation = new(q.X, q.Y, q.Z, q.W);
    }

    public void Dispose()
    {
        foreach (var assets in _assets) assets.Dispose();
        _assets.Clear();
        _actors.Clear();
        ScriptDefinitions.Clear();
        Placements.Clear();
        Objects.Clear();
        Scripts = null;
    }
}
