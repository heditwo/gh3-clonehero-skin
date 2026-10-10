using System.Numerics;

namespace Gh3Skin;

internal sealed record StageLightInterval(float Start, float End, int[] Members);

internal sealed class StageLightSpatial
{
    private readonly List<StageLightInterval>[] _axes = { new(), new(), new() };
    private readonly List<int> _unbounded = new();
    public IReadOnlyList<StageLightInterval> Axis(int index) => _axes[index];

    public void Add(int index, Vector3 minimum, Vector3 maximum)
    {
        for (var axis = 0; axis < 3; axis++)
        {
            var start = Coordinate(minimum, axis);
            var end = Coordinate(maximum, axis);
            var previous = _axes[axis].ToArray();
            var edges = previous.SelectMany(interval => new[] { interval.Start, interval.End }).Append(start).Append(end).Distinct().OrderBy(value => value).ToArray();
            _axes[axis].Clear();
            for (var i = 0; i < edges.Length - 1; i++)
            {
                var left = edges[i];
                var right = edges[i + 1];
                var members = previous.FirstOrDefault(interval => interval.Start <= left && interval.End >= right)?.Members ?? Array.Empty<int>();
                if (left >= start && right <= end) members = members.Append(index).OrderBy(value => value).ToArray();
                if (members.Length > 0) _axes[axis].Add(new(left, right, members));
            }
        }
    }

    public void AddUnbounded(int index) => _unbounded.Add(index);

    public void Remove(int index)
    {
        foreach (var axis in _axes)
            for (var i = axis.Count - 1; i >= 0; i--)
            {
                var members = axis[i].Members.Where(value => value != index).ToArray();
                if (members.Length == 0) axis.RemoveAt(i);
                else axis[i] = axis[i] with { Members = members };
            }
        _unbounded.Remove(index);
    }

    public void Compact()
    {
        foreach (var axis in _axes)
            for (var i = 1; i < axis.Count;)
            {
                var previous = axis[i - 1];
                var current = axis[i];
                // GH3: the native merge tolerance of neighbor cells.
                if (current.Start - previous.End < .00001f && previous.Members.SequenceEqual(current.Members))
                {
                    axis[i - 1] = previous with { End = current.End };
                    axis.RemoveAt(i);
                }
                else i++;
            }
    }

    public int[] Query(Vector3 position, int limit = 256)
    {
        var x = Find(_axes[0], position.X);
        var y = Find(_axes[1], position.Y);
        var z = Find(_axes[2], position.Z);
        return x.Intersect(y).Intersect(z).Concat(_unbounded).Take(Math.Max(0, limit)).ToArray();
    }

    // Same order as Query: distinct first-axis members present on all axes, then unbounded lights.
    public void QueryInto(Vector3 position, List<int> output, int limit = 256)
    {
        output.Clear();
        var x = Find(_axes[0], position.X);
        var y = Find(_axes[1], position.Y);
        var z = Find(_axes[2], position.Z);
        foreach (var member in x)
        {
            if (output.Count >= limit) return;
            if (Array.IndexOf(y, member) >= 0 && Array.IndexOf(z, member) >= 0 && !output.Contains(member)) output.Add(member);
        }
        foreach (var member in _unbounded)
        {
            if (output.Count >= limit) return;
            output.Add(member);
        }
    }

    private static int[] Find(IReadOnlyList<StageLightInterval> intervals, float value)
    {
        var start = 0;
        var end = intervals.Count - 1;
        while (start <= end)
        {
            var middle = (start + end) / 2;
            var interval = intervals[middle];
            if (value > interval.End) start = middle + 1;
            else if (value < interval.Start) end = middle - 1;
            else return interval.Members;
        }
        return Array.Empty<int>();
    }

    private static float Coordinate(Vector3 value, int axis) => axis == 0 ? value.X : axis == 1 ? value.Y : value.Z;
}
