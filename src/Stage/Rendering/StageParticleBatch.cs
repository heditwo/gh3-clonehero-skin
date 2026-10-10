using System.Runtime.InteropServices;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using N2 = System.Numerics.Vector2;
using N3 = System.Numerics.Vector3;
using N4 = System.Numerics.Vector4;
using UnityEngine;
using UnityEngine.Rendering;

namespace Gh3Skin;

// A dynamic mesh of camera-facing quads, rebuilt each frame: the batches of the GH3 particles, the hand fire, and the
// fallback effects. One draw call per batch.
internal sealed class StageParticleBatch
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
    private const int QuadStep = 64;
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

    public StageParticleBatch(StageAssets assets, GameObject root, int layer, Shader shader, Texture2D texture, bool additive, int capacity,
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
        // Larger than any stage, so that Unity never culls the batch.
        _mesh.bounds = new Bounds(Vector3.zero, new Vector3(100, 100, 100));
        var material = sourceMaterial ?? assets.Own(StageAssets.MakeMaterial(shader, true, additive));
        material.renderQueue = StageEffects.EffectQueue;
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
        var vertices = Math.Min(_capacity, (Count + (QuadStep - 1)) / QuadStep * QuadStep) * 4;
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
