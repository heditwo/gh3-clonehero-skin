using System.Numerics;

namespace Gh3Skin;

internal sealed class StageTriangleOrder
{
    private readonly int[] _triangles;
    private readonly int[] _order;
    private readonly float[] _depths;
    private readonly IComparer<int> _comparer;

    public StageTriangleOrder(int[] triangles)
    {
        if (triangles.Length % 3 != 0) throw new ArgumentException("Incomplete transparent triangle");
        _triangles = triangles.ToArray();
        _order = Enumerable.Range(0, triangles.Length / 3).ToArray();
        _depths = new float[_order.Length];
        _comparer = Comparer<int>.Create((a, b) => {
            var order = _depths[b].CompareTo(_depths[a]);
            return order == 0 ? a.CompareTo(b) : order;
        });
    }

    public void Sort(ReadOnlySpan<Vector3> positions, Vector3 forward, Span<int> output)
    {
        for (var i = 0; i < _order.Length; i++)
        {
            var at = i * 3;
            _depths[i] = Vector3.Dot(positions[_triangles[at]] + positions[_triangles[at + 1]] + positions[_triangles[at + 2]], forward);
        }
        Array.Sort(_order, _comparer);
        for (var i = 0; i < _order.Length; i++)
            _triangles.AsSpan(_order[i] * 3, 3).CopyTo(output.Slice(i * 3, 3));
    }
}
