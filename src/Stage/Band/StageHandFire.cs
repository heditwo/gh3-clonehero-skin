using MelonLoader;
using UnityEngine;

namespace Gh3Skin;

internal sealed class StageHandFire
{
    private readonly StageHandFireState _state;
    private readonly StageEffects.ParticleBatch[] _batches;
    private readonly Transform _left;
    private readonly Transform _right;
    private readonly Transform _root;
    private readonly Camera _camera;
    public bool Active => _state.Active;
    public int ActiveParticles => _state.Particles.Count;

    private StageHandFire(string directory, HandFireDefinition definition, StageAssets assets, GameObject root, Camera camera, int layer,
        Transform left, Transform right)
    {
        _state = new StageHandFireState(definition);
        _root = root.transform;
        _camera = camera;
        _left = left;
        _right = right;
        _batches = new StageEffects.ParticleBatch[definition.Emitters.Length];
        for (var i = 0; i < _batches.Length; i++)
        {
            var emitter = definition.Emitters[i];
            var texture = assets.Own(new Texture2D(2, 2, TextureFormat.RGBA32, false));
            if (!ImageConversion.LoadImage(texture, File.ReadAllBytes(Path.Combine(directory, emitter.Texture))))
                throw new InvalidDataException("Invalid hand-fire texture");
            texture.wrapMode = TextureWrapMode.Clamp;
            texture.filterMode = FilterMode.Bilinear;
            _batches[i] = new StageEffects.ParticleBatch(assets, root, layer, Shader.Find("Particles/Standard Unlit"), texture, true, 132,
                i == 0 ? "GH3 Hand Flames" : "GH3 Hand Sparks", emitter.Atlas);
        }
    }

    public static StageHandFire? Load(string directory, StageAssets assets, GameObject root, Camera camera, int layer, StageBand band)
    {
        var definition = HandFireDefinition.Load(Path.Combine(directory, "hand-fire.json"));
        if (definition == null)
        {
            MelonLogger.Warning("GH3 hand-fire assets are missing. Run scripts/regenerate.sh library and install.sh.");
            return null;
        }
        var left = band.HandFireBone(definition.Bones[0], 45);
        var right = band.HandFireBone(definition.Bones[1], 48);
        if (left == null || right == null) return null;
        var fire = new StageHandFire(directory, definition, assets, root, camera, layer, left, right);
        MelonLogger.Msg("GH3 original hand flames and sparks loaded; 100-note streak trigger");
        return fire;
    }

    public void Update(double time, int combo)
    {
        _state.Update(time, combo, Pose(_left), Pose(_right));
        var right = _root.InverseTransformDirection(_camera.transform.right);
        var up = _root.InverseTransformDirection(_camera.transform.up);
        foreach (var batch in _batches) batch.Begin(right, up);
        foreach (var particle in _state.Particles)
        {
            var sample = _state.Sample(particle, time);
            var point = _root.InverseTransformPoint(new Vector3(sample.Position.X, sample.Position.Y, sample.Position.Z));
            var color = sample.Color;
            _batches[particle.Emitter].Add(point, sample.Size.X * 0.5f, sample.Size.Y * 0.5f, new Color(color.X, color.Y, color.Z, color.W), sample.Frame);
        }
        foreach (var batch in _batches) batch.End();
    }

    private static BandPose Pose(Transform transform)
    {
        var p = transform.position;
        var q = transform.rotation;
        return new(new(p.x, p.y, p.z), new(q.x, q.y, q.z, q.w));
    }
}
