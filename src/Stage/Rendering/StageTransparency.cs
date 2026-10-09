using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;
using UnityEngine.Rendering;
using Vec = System.Numerics.Vector3;

namespace Gh3Skin;

internal sealed class StageTransparency
{
    private readonly SkinnedMeshRenderer _renderer;
    private readonly Mesh _pose;
    private readonly Il2CppStructArray<Vector3> _positions;
    private readonly Il2CppSystem.Array _positionBuffer;
    private readonly Il2CppStructArray<int> _indices;
    private readonly StageTriangleOrder _order;
    private readonly Vec[] _vertices;

    public StageTransparency(StageAssets assets, SkinnedMeshRenderer renderer, int vertices, int[] indices)
    {
        _renderer = renderer;
        _pose = assets.Own(new Mesh { name = "GH3 Hair Sorting Pose" });
        _positions = new(vertices);
        _positionBuffer = new(_positions.Pointer);
        _vertices = new Vec[vertices];
        _indices = new(indices.Length);
        _order = new(indices);
    }

    public void Update(Camera camera)
    {
        _renderer.BakeMesh(_pose);
        _pose.GetArrayFromChannelImpl(VertexAttribute.Position, VertexAttributeFormat.Float32, 3, _positionBuffer);
        var positions = _positions.AsSpan();
        for (var i = 0; i < positions.Length; i++) _vertices[i] = new(positions[i].x, positions[i].y, positions[i].z);
        var rotation = _renderer.transform.rotation;
        var forward = camera.transform.forward;
        var local = Vec.Transform(new Vec(forward.x, forward.y, forward.z),
            System.Numerics.Quaternion.Inverse(new(rotation.x, rotation.y, rotation.z, rotation.w)));
        _order.Sort(_vertices, local, _indices.AsSpan());
        _renderer.sharedMesh.triangles = _indices;
    }
}
