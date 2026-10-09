using System.Runtime.InteropServices;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using N2 = System.Numerics.Vector2;
using N3 = System.Numerics.Vector3;
using N4 = System.Numerics.Vector4;
using UnityEngine;
using UnityEngine.Rendering;

namespace Gh3Skin;

internal sealed class StageEffects
{
    private readonly StageAssets _assets;
    private readonly Camera _camera;
    private readonly Dictionary<string, (Vector3 Position, Quaternion Rotation)[]> _emitters;
    private readonly List<(Transform Transform, Material Material, Vector3 Origin, int Index)> _beams = new();
    private readonly ParticleBatch _sparks;
    private readonly ParticleBatch _smoke;
    private readonly StageLightingRenderer? _lighting;
    public int ActiveParticles { get; private set; }
    public bool SourcePyro { get; set; }

    public StageEffects(StageAssets assets, GameObject root, Camera camera, int layer, StageLighting? lighting = null, StageFixtures? fixtures = null,
        IReadOnlyDictionary<string, StagePlacement[]>? emitters = null)
    {
        _assets = assets;
        _camera = camera;
        _emitters = (emitters ?? new Dictionary<string, StagePlacement[]>()).ToDictionary(e => e.Key,
            e => e.Value.Select(p => (StageAssets.Vector(p.Position), new Quaternion(p.Rotation[0], p.Rotation[1], p.Rotation[2], p.Rotation[3]))).ToArray());
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
            _lighting = new StageLightingRenderer(lighting, assets, root, layer, fixtures);
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
            if (SourcePyro && cue.Type == "pyro") continue;
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
            var emitters = _emitters.GetValueOrDefault(cue.Type, Array.Empty<(Vector3, Quaternion)>());
            for (var emitter = 0; emitter < emitters.Length; emitter++)
            {
                var (origin, rotation) = emitters[emitter];
                var seed = i * 7919 + emitter * 997;
                if (cue.Type == "fireworks") Firework(origin, rotation, age, seed, emitter);
                else if (cue.Type is "pyro" or "sparks") Fountain(origin, rotation, age, duration, seed, cue.Type == "pyro");
                if (cue.Type is "smoke" or "pyro") Smoke(origin, rotation, age, duration, seed, cue.Type == "pyro");
            }
        }
        FrameProfile.Measure("e.lighting", () => _lighting?.Update(time));
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

    private void Fountain(Vector3 origin, Quaternion rotation, float age, float duration, int seed, bool flame)
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
            var offset = new Vector3(MathF.Cos(angle) * spread * t, speed * t - 4.5f * t * t, MathF.Sin(angle) * spread * t);
            if (offset.y < 0) continue;
            var position = origin + rotation * offset;
            var fade = 1 - t / life;
            _sparks.Add(position, 0.035f, 0.16f, new Color(1, 0.5f + fade * 0.5f, 0.15f + fade * 0.55f, fade));
        }
        if (!flame) return;
        for (var i = 0; i < 28; i++)
        {
            var t = age - i * duration / 28;
            if (t < 0 || t > 0.7f) continue;
            var position = origin + rotation * new Vector3(MathF.Sin(seed + i) * t * 0.3f, t * 8, 0);
            var fade = 1 - t / 0.7f;
            _sparks.Add(position, 0.25f + t * 0.2f, 0.8f, new Color(1, 0.25f + fade * 0.55f, fade * 0.15f, fade));
        }
    }

    private void Firework(Vector3 origin, Quaternion rotation, float age, int seed, int emitter)
    {
        if (age < 0.7f)
        {
            _sparks.Add(origin + rotation * Vector3.up * (age * 16), 0.08f, 0.7f, new Color(1, 0.8f, 0.3f, 1));
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
            var position = origin + rotation * (Vector3.up * 11.2f + direction * (5 * (1 - MathF.Exp(-t * 0.9f))) + Vector3.down * t * t * 1.2f);
            var fade = MathF.Pow(1 - t / 3, 1.5f);
            var size = 0.06f + Random(seed + i) * 0.03f;
            _sparks.Add(position, size, size * 2, new Color(color.R, color.G, color.B, fade));
        }
    }

    private void Smoke(Vector3 origin, Quaternion rotation, float age, float duration, int seed, bool aftermath)
    {
        for (var i = 0; i < 12; i++)
        {
            var t = age - i * duration / 12 - (aftermath ? 0.3f : 0);
            if (t < 0 || t > 4) continue;
            var position = origin + rotation * new Vector3((Random(seed + i * 31) - 0.5f) * t, t * 1.5f, t * 0.2f);
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

    // Particle effects draw after the highway mask (StageHighwayMask.Queue).
    public const int EffectQueue = StageHighwayMask.Queue + 5;

    internal sealed class ParticleBatch
    {
        // Vertex math stays in System.Numerics: Unity struct operators and constructors are native calls in this game.
        private readonly Mesh _mesh;
        private readonly N3[] _vertices;
        private readonly N4[] _colors;
        private readonly N2[] _uv;
        private readonly N2[]? _particleData;
        private readonly N3[]? _normals;
        private readonly N4[]? _tangents;
        private readonly N2[]? _bitangents;
        private readonly Il2CppStructArray<Vector3> _vertexBuffer;
        private readonly Il2CppStructArray<Color> _colorBuffer;
        private readonly Il2CppStructArray<Vector2> _uvBuffer;
        private readonly Il2CppStructArray<Vector2>? _particleDataBuffer;
        private readonly Il2CppStructArray<Vector3>? _normalBuffer;
        private readonly Il2CppStructArray<Vector4>? _tangentBuffer;
        private readonly Il2CppStructArray<Vector2>? _bitangentBuffer;
        public float SoftEdgeWidth { get; set; }
        public N3 LightNormal { get; set; }
        public N3 LightTangent { get; set; }
        public N3 LightBitangent { get; set; }
        private readonly Il2CppStructArray<int> _indices;
        private readonly Il2CppSystem.Array _indexArray;
        private readonly Il2CppSystem.Array _vertexArray;
        private readonly Il2CppSystem.Array _colorArray;
        private readonly Il2CppSystem.Array _uvArray;
        private readonly Il2CppSystem.Array? _particleDataArray;
        private readonly Il2CppSystem.Array? _normalArray;
        private readonly Il2CppSystem.Array? _tangentArray;
        private readonly Il2CppSystem.Array? _bitangentArray;
        private const MeshUpdateFlags UploadFlags = MeshUpdateFlags.DontValidateIndices | MeshUpdateFlags.DontResetBoneBounds | MeshUpdateFlags.DontRecalculateBounds;
        private int _vertexCount;
        private readonly int _atlas;
        private readonly int _capacity;
        private N3 _right;
        private N3 _up;
        private int _previousCount;
        private bool _customUv;
        public int Count { get; private set; }
        public Material Material { get; }
        private readonly MeshRenderer _renderer;
        public int SortingOrder { get => _renderer.sortingOrder; set => _renderer.sortingOrder = value; }
        public bool Full => Count == _capacity;
        public int Capacity => _capacity;

        public ParticleBatch(StageAssets assets, GameObject root, int layer, Shader shader, Texture2D texture, bool additive, int capacity,
            string? name = null, int atlas = 1, Material? sourceMaterial = null)
        {
            _capacity = capacity;
            _atlas = atlas;
            _vertices = new N3[capacity * 4];
            _colors = new N4[capacity * 4];
            _uv = new N2[capacity * 4];
            _vertexBuffer = new Il2CppStructArray<Vector3>(capacity * 4);
            _colorBuffer = new Il2CppStructArray<Color>(capacity * 4);
            _uvBuffer = new Il2CppStructArray<Vector2>(capacity * 4);
            var indices = new int[capacity * 6];
            var quad = sourceMaterial == null ? new[] { 0, 1, 2, 0, 2, 3 } : new[] { 0, 2, 1, 0, 3, 2 };
            for (var i = 0; i < capacity; i++)
            {
                _uv[i * 4] = new(0, 0);
                _uv[i * 4 + 1] = new(1, 0);
                _uv[i * 4 + 2] = new(1, 1);
                _uv[i * 4 + 3] = new(0, 1);
                for (var j = 0; j < 6; j++) indices[i * 6 + j] = i * 4 + quad[j];
            }
            Upload(_uv, _uvBuffer, _uv.Length);
            _mesh = assets.Own(new Mesh { name = name ?? (additive ? "GH3 Stage Sparks" : "GH3 Stage Smoke") });
            _mesh.MarkDynamic();
            if (sourceMaterial != null)
            {
                _particleData = new N2[capacity * 4];
                _particleDataBuffer = new Il2CppStructArray<Vector2>(capacity * 4);
                _normals = new N3[capacity * 4];
                _normalBuffer = new Il2CppStructArray<Vector3>(capacity * 4);
                _tangents = new N4[capacity * 4];
                _tangentBuffer = new Il2CppStructArray<Vector4>(capacity * 4);
                _bitangents = new N2[capacity * 4];
                _bitangentBuffer = new Il2CppStructArray<Vector2>(capacity * 4);
            }
            _indices = new Il2CppStructArray<int>(indices.Length);
            indices.CopyTo(_indices.AsSpan());
            _indexArray = new(_indices.Pointer);
            _vertexArray = new(_vertexBuffer.Pointer);
            _colorArray = new(_colorBuffer.Pointer);
            _uvArray = new(_uvBuffer.Pointer);
            if (_particleDataBuffer != null) _particleDataArray = new(_particleDataBuffer.Pointer);
            if (_normalBuffer != null) _normalArray = new(_normalBuffer.Pointer);
            if (_tangentBuffer != null) _tangentArray = new(_tangentBuffer.Pointer);
            if (_bitangentBuffer != null) _bitangentArray = new(_bitangentBuffer.Pointer);
            _mesh.bounds = new Bounds(Vector3.zero, new Vector3(100, 100, 100));
            var material = sourceMaterial ?? assets.Own(StageAssets.MakeMaterial(shader, true, additive));
            material.renderQueue = EffectQueue;
            Material = material;
            material.mainTexture = texture;
            var obj = new GameObject(_mesh.name) { layer = layer };
            obj.transform.SetParent(root.transform, false);
            obj.AddComponent<MeshFilter>().sharedMesh = _mesh;
            var renderer = obj.AddComponent<MeshRenderer>();
            _renderer = renderer;
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            renderer.enabled = false;
        }

        private static N3 Numeric(Vector3 value) => new(value.x, value.y, value.z);

        private static void Upload<TSource, TTarget>(TSource[] source, Il2CppStructArray<TTarget> target, int length)
            where TSource : unmanaged where TTarget : unmanaged =>
            MemoryMarshal.Cast<TSource, TTarget>(source.AsSpan(0, length)).CopyTo(target.AsSpan());

        public void Begin(Vector3 right, Vector3 up) => Begin(Numeric(right), Numeric(up));
        public void Begin(N3 right, N3 up) { Count = 0; _right = right; _up = up; }

        public void Add(Vector3 position, float width, float height, Color color, int frame = 0, float angle = 0)
        {
            if (Count == _capacity) return;
            var start = Count++ * 4;
            var cosine = MathF.Cos(angle);
            var sine = MathF.Sin(angle);
            var right = (_right * cosine + _up * sine) * width;
            var up = (_up * cosine - _right * sine) * height;
            var center = Numeric(position);
            _vertices[start] = center - right - up;
            _vertices[start + 1] = center + right - up;
            _vertices[start + 2] = center + right + up;
            _vertices[start + 3] = center - right + up;
            var tint = new N4(color.r, color.g, color.b, color.a);
            for (var i = 0; i < 4; i++) _colors[start + i] = tint;
            if (_atlas > 1)
            {
                var size = 1f / _atlas;
                var u = frame % _atlas * size;
                var v = 1 - (frame / _atlas + 1) * size;
                _uv[start] = new(u, v);
                _uv[start + 1] = new(u + size, v);
                _uv[start + 2] = new(u + size, v + size);
                _uv[start + 3] = new(u, v + size);
            }
        }

        public void End()
        {
            if (Count == 0)
            {
                if (_previousCount != 0) _renderer.enabled = false;
                _previousCount = 0;
                return;
            }
            // Vertex counts grow in 64-quad steps so the GPU buffer is rarely reallocated; only indexed quads are drawn.
            var vertices = Math.Min(_capacity, (Count + 63) / 64 * 64) * 4;
            var used = Count * 4;
            var resized = vertices != _vertexCount;
            if (vertices < _vertexCount) SetIndices();
            if (resized) used = vertices;
            var length = _capacity * 4;
            Upload(_vertices, _vertexBuffer, used);
            SetChannel(VertexAttribute.Position, 3, _vertexArray, length, vertices);
            Upload(_colors, _colorBuffer, used);
            SetChannel(VertexAttribute.Color, 4, _colorArray, length, vertices);
            if (_atlas > 1 || _customUv || resized) { Upload(_uv, _uvBuffer, used); SetChannel(VertexAttribute.TexCoord0, 2, _uvArray, length, vertices); }
            if (_particleData != null) { Upload(_particleData, _particleDataBuffer!, used); SetChannel(VertexAttribute.TexCoord1, 2, _particleDataArray!, length, vertices); }
            if (_normals != null) { Upload(_normals, _normalBuffer!, used); SetChannel(VertexAttribute.Normal, 3, _normalArray!, length, vertices); }
            if (_tangents != null) { Upload(_tangents, _tangentBuffer!, used); SetChannel(VertexAttribute.Tangent, 4, _tangentArray!, length, vertices); }
            if (_bitangents != null) { Upload(_bitangents, _bitangentBuffer!, used); SetChannel(VertexAttribute.TexCoord2, 2, _bitangentArray!, length, vertices); }
            if (vertices > _vertexCount || !resized && Count != _previousCount) SetIndices();
            _vertexCount = vertices;
            if (_previousCount == 0) _renderer.enabled = true;
            _previousCount = Count;
        }

        // The public ranged Mesh overloads are stripped from this game; these internal calls back them and are always present.
        private void SetIndices() => _mesh.SetIndicesImpl(0, MeshTopology.Triangles, IndexFormat.UInt32, _indexArray, 0, Count * 6, false, 0);

        private void SetChannel(VertexAttribute channel, int dimension, Il2CppSystem.Array values, int length, int count) =>
            _mesh.SetArrayForChannelImpl(channel, VertexAttributeFormat.Float32, dimension, values, length, 0, count, UploadFlags);

        public void AddQuad(N3 position, N3 right, N3 up, N4 color, N4 uv)
            => AddQuadVertices(position - right - up, position + right - up, position + right + up, position - right + up, color, color, uv);

        public void AddQuadVertices(N3 a, N3 b, N3 c, N3 d, N4 color, N4 uv) => AddQuadVertices(a, b, c, d, color, color, uv);

        public void AddQuadVertices(N3 a, N3 b, N3 c, N3 d, N4 startColor, N4 endColor, N4 uv)
        {
            if (Count == _capacity) return;
            var start = Count++ * 4;
            _vertices[start] = a;
            _vertices[start + 1] = b;
            _vertices[start + 2] = c;
            _vertices[start + 3] = d;
            if (_particleData != null)
                for (var i = 0; i < 4; i++) _particleData[start + i] = new(SoftEdgeWidth, LightBitangent.X);
            if (_normals != null)
                for (var i = 0; i < 4; i++) _normals[start + i] = LightNormal;
            if (_tangents != null)
                for (var i = 0; i < 4; i++) _tangents[start + i] = new(LightTangent, 0);
            if (_bitangents != null)
                for (var i = 0; i < 4; i++) _bitangents[start + i] = new(LightBitangent.Y, LightBitangent.Z);
            _colors[start] = _colors[start + 1] = startColor;
            _colors[start + 2] = _colors[start + 3] = endColor;
            _uv[start] = new(uv.Z, 1 - uv.W);
            _uv[start + 1] = new(uv.X, 1 - uv.W);
            _uv[start + 2] = new(uv.X, 1 - uv.Y);
            _uv[start + 3] = new(uv.Z, 1 - uv.Y);
            _customUv = true;
        }

        public void AddSegment(Vector3 from, Vector3 to, float width, Color color)
        {
            if (Count == _capacity) return;
            var start = Numeric(from);
            var end = Numeric(to);
            var side = N3.Cross(end - start, N3.Cross(_right, _up));
            side = side.LengthSquared() > 0 ? N3.Normalize(side) * (width * .5f) : N3.Zero;
            var index = Count++ * 4;
            _vertices[index] = start - side;
            _vertices[index + 1] = start + side;
            _vertices[index + 2] = end + side;
            _vertices[index + 3] = end - side;
            var tint = new N4(color.r, color.g, color.b, color.a);
            for (var i = 0; i < 4; i++) _colors[index + i] = tint;
        }
    }
}
