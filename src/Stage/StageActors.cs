using System.Text.Json;
using UnityEngine;

namespace Gh3Skin;

internal sealed class StageActorDefinition
{
    public string Id { get; set; } = "";
    public int Clip { get; set; }
    public string Playback { get; set; } = "loop";
    public StagePlacement Transform { get; set; } = new();
    public string LightGroup { get; set; } = "";
}

internal sealed class StageActorBank
{
    public int Version { get; set; }
    public StageActorDefinition[] Actors { get; set; } = Array.Empty<StageActorDefinition>();
}

internal sealed class StageActors : IDisposable
{
    private readonly List<StageAssets> _assets = new();
    private readonly List<(Transform[] Bones, BandAnimation Animation, int Clip, bool Hold)> _actors = new();

    public void Load(string directory, StageAssets stage, GameObject root, int layer)
    {
        var path = Path.Combine(directory, "actors.json");
        if (!File.Exists(path)) return;
        var bank = JsonSerializer.Deserialize<StageActorBank>(File.ReadAllText(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (bank?.Version != 1 || bank.Actors.Length > 128) throw new InvalidDataException("Invalid stage actors");
        foreach (var actor in bank.Actors)
        {
            if (actor.Id.Length != 8 || !actor.Id.All(Uri.IsHexDigit) || !actor.Transform.Valid || actor.Playback is not ("loop" or "hold"))
                throw new InvalidDataException("Invalid stage actor");
            var folder = Path.Combine(directory, "actors", actor.Id);
            using var reader = new BinaryReader(File.OpenRead(Path.Combine(folder, "animation.bin")));
            var animation = new BandAnimation(reader);
            if (actor.Clip < 0 || actor.Clip >= animation.Clips.Length) throw new InvalidDataException("Invalid stage actor clip");
            var obj = new GameObject("Stage Actor " + actor.Id) { layer = layer };
            obj.transform.SetParent(root.transform, false);
            obj.transform.localPosition = StageAssets.Vector(actor.Transform.Position);
            var q = actor.Transform.Rotation;
            obj.transform.localRotation = new(q[0], q[1], q[2], q[3]);
            var bones = animation.Parents.Select((_, i) => new GameObject("Actor Bone " + i) { layer = layer }.transform).ToArray();
            for (var i = 0; i < bones.Length; i++)
            {
                bones[i].SetParent(animation.Parents[i] < 0 ? obj.transform : bones[animation.Parents[i]], false);
                Apply(bones[i], animation.BindPose[i]);
            }
            var assets = new StageAssets();
            _assets.Add(assets);
            assets.Load(folder, obj, layer, bones, lighting: true, lightGroup: actor.LightGroup);
            stage.BandMeshes.AddRange(assets.BandMeshes);
            stage.TransparentMeshes.AddRange(assets.TransparentMeshes);
            _actors.Add((bones, animation, actor.Clip, actor.Playback == "hold"));
        }
        MelonLoader.MelonLogger.Msg($"Stage actors loaded: {_actors.Count}");
    }

    public void Update(double time)
    {
        foreach (var (bones, animation, clip, hold) in _actors)
        {
            var track = animation.Clips[clip];
            var clock = hold ? 0 : Math.Max(0, time) % track.Duration;
            for (var i = 0; i < bones.Length; i++) Apply(bones[i], animation.SampleClip(track, clock, i));
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
    }
}
