using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;
using UnityEngine.Rendering;

namespace Gh3Skin;

internal sealed class StageEffects
{
    private readonly StageAssets _assets;
    private readonly Camera _camera;
    private readonly Vector3[] _emitters;
    private readonly List<(Transform Transform, Material Material, Vector3 Origin, int Index)> _beams = new();
    private readonly ParticleBatch _sparks;
    private readonly ParticleBatch _smoke;
    private readonly StageLightingRenderer? _lighting;
    public int ActiveParticles { get; private set; }

    public StageEffects(StageAssets assets, GameObject root, Camera camera, int layer, StageLighting? lighting = null)
    {
        _assets = assets;
        _camera = camera;
        _emitters = assets.Definition.PyroPositions.Select(StageAssets.Vector).ToArray();
        var texture = assets.Own(new Texture2D(32, 32, TextureFormat.RGBA32, false));
        var pixels = new Color32[32 * 32];
        for (var y = 0; y < 32; y++)
            for (var x = 0; x < 32; x++)
            {
                var distance = MathF.Sqrt(MathF.Pow((x - 15.5f) / 15.5f, 2) + MathF.Pow((y - 15.5f) / 15.5f, 2));
                var alpha = (byte)(255 * MathF.Pow(Math.Max(0, 1 - distance), 2));
                pixels[y * 32 + x] = new Color32(255, 255, 255, alpha);
            }
        texture.SetPixels32(new Il2CppStructArray<Color32>(pixels));
        texture.Apply(false, true);
        texture.wrapMode = TextureWrapMode.Clamp;
        var shader = Shader.Find("Particles/Standard Unlit");
        _sparks = new ParticleBatch(assets, root, layer, shader, texture, true, 2048);
        _smoke = new ParticleBatch(assets, root, layer, shader, texture, false, 256);
        if (lighting != null)
        {
            _lighting = new StageLightingRenderer(lighting, assets, root, layer);
            return;
        }
        var cone = assets.Own(CreateCone());
        for (var i = 0; i < 8; i++)
        {
            var obj = new GameObject("GH3 Stage Beam " + i) { layer = layer };
            obj.transform.SetParent(root.transform, false);
            var origin = new Vector3(-12 + i * 24f / 7, 12, 7);
            obj.transform.localPosition = origin;
            obj.AddComponent<MeshFilter>().sharedMesh = cone;
            var material = assets.Own(StageAssets.MakeMaterial(shader, true, true));
            var renderer = obj.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            _beams.Add((obj.transform, material, origin, i));
        }
    }

    public void Update(StageTimeline timeline, double time)
    {
        var light = timeline.Lighting(time);
        var flash = 0f;
        _sparks.Begin(_camera.transform.right, _camera.transform.up);
        _smoke.Begin(_camera.transform.right, _camera.transform.up);
        var first = timeline.FirstEffectAfter(time - 34);
        for (var i = first; i < timeline.Effects.Length && timeline.Effects[i].Time <= time; i++)
        {
            var cue = timeline.Effects[i];
            var age = (float)(time - cue.Time);
            var duration = cue.Duration > 0 ? (float)cue.Duration : cue.Type == "smoke" ? 3 : 0.6f;
            if (age > duration + 4) continue;
            if (cue.Type is "flash" or "pyro" or "fireworks") flash = Math.Max(flash, Math.Max(0, 1 - age / 0.35f));
            if (_lighting != null && cue.Type == "flash" && age < 0.35f)
            {
                var height = MathF.Tan(_camera.fieldOfView * MathF.PI / 360);
                _sparks.Add(_camera.transform.position + _camera.transform.forward, height * _camera.aspect, height,
                    new Color(1, 1, 1, (1 - age / 0.35f) * 0.3f));
            }
            for (var emitter = 0; emitter < _emitters.Length; emitter++)
            {
                var origin = _emitters[emitter];
                var seed = i * 7919 + emitter * 997;
                if (cue.Type == "fireworks") Firework(origin, age, seed, emitter);
                else if (cue.Type is "pyro" or "sparks") Fountain(origin, age, duration, seed, cue.Type == "pyro");
                if (cue.Type is "smoke" or "pyro") Smoke(origin, age, duration, seed, cue.Type == "pyro");
            }
        }
        _lighting?.Update(time);
        if (_lighting == null) foreach (var material in _assets.SurfaceMaterials)
        {
            var strength = Math.Max(light.R, Math.Max(light.G, light.B));
            material.color = new Color(0.035f + strength * 0.32f + light.R * 0.34f + flash * 0.2f,
                0.035f + strength * 0.32f + light.G * 0.34f + flash * 0.12f,
                0.045f + strength * 0.32f + light.B * 0.34f + flash * 0.05f, 1);
        }
        foreach (var (transform, material, origin, index) in _beams)
        {
            var target = new Vector3(MathF.Sin((float)time * 0.55f + index * 1.7f) * 11, 0,
                -3 + MathF.Cos((float)time * 0.4f + index) * 4);
            var direction = target - origin;
            transform.localRotation = Quaternion.FromToRotation(Vector3.forward, direction.normalized);
            transform.localScale = new Vector3(1.6f, 1.6f, direction.magnitude);
            material.color = new Color(light.R, light.G, light.B, 0.08f);
        }
        _sparks.End();
        _smoke.End();
        ActiveParticles = _sparks.Count + _smoke.Count;
    }

    private void Fountain(Vector3 origin, float age, float duration, int seed, bool flame)
    {
        for (var i = 0; i < 80; i++)
        {
            var start = Random(seed + i * 13) * duration;
            var t = age - start;
            var life = 0.8f + Random(seed + i * 17) * 1.2f;
            if (t < 0 || t > life) continue;
            var angle = Random(seed + i * 19) * MathF.PI * 2;
            var speed = 6 + Random(seed + i * 23) * 7;
            var spread = 0.3f + Random(seed + i * 29) * 1.5f;
            var position = origin + new Vector3(MathF.Cos(angle) * spread * t, speed * t - 4.5f * t * t, MathF.Sin(angle) * spread * t);
            if (position.y < origin.y) continue;
            var fade = 1 - t / life;
            _sparks.Add(position, 0.035f, 0.16f, new Color(1, 0.5f + fade * 0.5f, 0.15f + fade * 0.55f, fade));
        }
        if (!flame) return;
        for (var i = 0; i < 28; i++)
        {
            var t = age - i * duration / 28;
            if (t < 0 || t > 0.7f) continue;
            var position = origin + new Vector3(MathF.Sin(seed + i) * t * 0.3f, t * 8, 0);
            var fade = 1 - t / 0.7f;
            _sparks.Add(position, 0.25f + t * 0.2f, 0.8f, new Color(1, 0.25f + fade * 0.55f, fade * 0.15f, fade));
        }
    }

    private void Firework(Vector3 origin, float age, int seed, int emitter)
    {
        if (age < 0.7f)
        {
            _sparks.Add(origin + Vector3.up * (age * 16), 0.08f, 0.7f, new Color(1, 0.8f, 0.3f, 1));
            return;
        }
        var t = age - 0.7f;
        if (t > 3) return;
        var color = StageColor.Named(emitter % 2 == 0 ? "orange" : "cyan");
        for (var i = 0; i < 100; i++)
        {
            var azimuth = i * 2.399963f;
            var vertical = 1 - 2 * (i + 0.5f) / 100;
            var horizontal = MathF.Sqrt(1 - vertical * vertical);
            var direction = new Vector3(MathF.Cos(azimuth) * horizontal, vertical, MathF.Sin(azimuth) * horizontal);
            var position = origin + Vector3.up * 11.2f + direction * (5 * (1 - MathF.Exp(-t * 0.9f))) + Vector3.down * t * t * 1.2f;
            var fade = MathF.Pow(1 - t / 3, 1.5f);
            var size = 0.06f + Random(seed + i) * 0.03f;
            _sparks.Add(position, size, size * 2, new Color(color.R, color.G, color.B, fade));
        }
    }

    private void Smoke(Vector3 origin, float age, float duration, int seed, bool aftermath)
    {
        for (var i = 0; i < 12; i++)
        {
            var t = age - i * duration / 12 - (aftermath ? 0.3f : 0);
            if (t < 0 || t > 4) continue;
            var position = origin + new Vector3((Random(seed + i * 31) - 0.5f) * t, t * 1.5f, t * 0.2f);
            var alpha = Math.Min(t * 2, 1) * (1 - t / 4) * 0.35f;
            _smoke.Add(position, 0.4f + t * 0.55f, 0.5f + t * 0.65f, new Color(0.35f, 0.4f, 0.5f, alpha));
        }
    }

    private static float Random(int seed)
    {
        var value = unchecked((uint)seed * 747796405u + 2891336453u);
        value = ((value >> (int)((value >> 28) + 4)) ^ value) * 277803737u;
        return ((value >> 22) ^ value) / (float)uint.MaxValue;
    }

    private static Mesh CreateCone()
    {
        const int sides = 24;
        var vertices = new Vector3[sides + 1];
        var colors = new Color[sides + 1];
        var triangles = new int[sides * 3];
        colors[0] = new Color(1, 1, 1, 0.65f);
        for (var i = 0; i < sides; i++)
        {
            var angle = i * MathF.PI * 2 / sides;
            vertices[i + 1] = new Vector3(MathF.Cos(angle), MathF.Sin(angle), 1);
            colors[i + 1] = new Color(1, 1, 1, 0);
            triangles[i * 3] = 0;
            triangles[i * 3 + 1] = i + 1;
            triangles[i * 3 + 2] = (i + 1) % sides + 1;
        }
        var mesh = new Mesh { name = "GH3 Stage Light Cone" };
        mesh.vertices = new Il2CppStructArray<Vector3>(vertices);
        mesh.colors = new Il2CppStructArray<Color>(colors);
        mesh.triangles = new Il2CppStructArray<int>(triangles);
        mesh.RecalculateBounds();
        return mesh;
    }

    private sealed class ParticleBatch
    {
        private readonly Mesh _mesh;
        private readonly Il2CppStructArray<Vector3> _vertices;
        private readonly Il2CppStructArray<Color> _colors;
        private readonly int _capacity;
        private Vector3 _right;
        private Vector3 _up;
        private int _previousCount;
        public int Count { get; private set; }

        public ParticleBatch(StageAssets assets, GameObject root, int layer, Shader shader, Texture2D texture, bool additive, int capacity)
        {
            _capacity = capacity;
            _vertices = new Il2CppStructArray<Vector3>(capacity * 4);
            _colors = new Il2CppStructArray<Color>(capacity * 4);
            var uv = new Vector2[capacity * 4];
            var indices = new int[capacity * 6];
            var quad = new[] { 0, 1, 2, 0, 2, 3 };
            for (var i = 0; i < capacity; i++)
            {
                uv[i * 4] = new Vector2(0, 0);
                uv[i * 4 + 1] = new Vector2(1, 0);
                uv[i * 4 + 2] = new Vector2(1, 1);
                uv[i * 4 + 3] = new Vector2(0, 1);
                for (var j = 0; j < 6; j++) indices[i * 6 + j] = i * 4 + quad[j];
            }
            _mesh = assets.Own(new Mesh { name = additive ? "GH3 Stage Sparks" : "GH3 Stage Smoke" });
            _mesh.MarkDynamic();
            _mesh.vertices = _vertices;
            _mesh.colors = _colors;
            _mesh.uv = new Il2CppStructArray<Vector2>(uv);
            _mesh.triangles = new Il2CppStructArray<int>(indices);
            _mesh.bounds = new Bounds(Vector3.zero, new Vector3(100, 100, 100));
            var material = assets.Own(StageAssets.MakeMaterial(shader, true, additive));
            material.mainTexture = texture;
            var obj = new GameObject(_mesh.name) { layer = layer };
            obj.transform.SetParent(root.transform, false);
            obj.AddComponent<MeshFilter>().sharedMesh = _mesh;
            var renderer = obj.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
        }

        public void Begin(Vector3 right, Vector3 up) { Count = 0; _right = right; _up = up; }
        public void Add(Vector3 position, float width, float height, Color color)
        {
            if (Count == _capacity) return;
            var start = Count++ * 4;
            var right = _right * width;
            var up = _up * height;
            _vertices[start] = position - right - up;
            _vertices[start + 1] = position + right - up;
            _vertices[start + 2] = position + right + up;
            _vertices[start + 3] = position - right + up;
            for (var i = 0; i < 4; i++) _colors[start + i] = color;
        }

        public void End()
        {
            for (var i = Count * 4; i < _previousCount * 4; i++) _colors[i] = Color.clear;
            if (Count != 0 || _previousCount != 0)
            {
                _mesh.vertices = _vertices;
                _mesh.colors = _colors;
            }
            _previousCount = Count;
        }
    }
}
