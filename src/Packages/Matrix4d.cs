using System.Text.Json.Nodes;

namespace Gh3Skin.Packages;

// Row-major 4 × 4 double matrix for the importer (column vectors, as numpy in pipeline/stagepack/gltf.py).
internal readonly struct Matrix4d
{
    private readonly double[] _m;
    public Matrix4d(double[] values) => _m = values;
    public double this[int row, int column] => _m[row * 4 + column];
    public static Matrix4d Identity => Diagonal(1, 1, 1, 1);
    public static readonly Matrix4d Reflection = Diagonal(1, 1, -1, 1);

    public static Matrix4d Diagonal(double a, double b, double c, double d)
    {
        var m = new double[16];
        m[0] = a; m[5] = b; m[10] = c; m[15] = d;
        return new Matrix4d(m);
    }

    public static Matrix4d operator *(Matrix4d a, Matrix4d b)
    {
        var m = new double[16];
        for (var r = 0; r < 4; r++)
            for (var c = 0; c < 4; c++)
            {
                double sum = 0;
                for (var k = 0; k < 4; k++) sum += a._m[r * 4 + k] * b._m[k * 4 + c];
                m[r * 4 + c] = sum;
            }
        return new Matrix4d(m);
    }

    public static Matrix4d Reflect(Matrix4d m) => Reflection * m * Reflection;

    public static double[] QuaternionMatrix(double x, double y, double z, double w)
    {
        var n = Math.Sqrt(x * x + y * y + z * z + w * w);
        x /= n; y /= n; z /= n; w /= n;
        return new[]
        {
            1 - 2 * (y * y + z * z), 2 * (x * y - z * w), 2 * (x * z + y * w),
            2 * (x * y + z * w), 1 - 2 * (x * x + z * z), 2 * (y * z - x * w),
            2 * (x * z - y * w), 2 * (y * z + x * w), 1 - 2 * (x * x + y * y),
        };
    }

    public static Matrix4d FromNode(JsonObject node)
    {
        var m = new double[16];
        if (node["matrix"] is JsonArray matrix)
        {
            if (matrix.Count != 16) throw new PackageException("invalid glTF node matrix");
            for (var i = 0; i < 16; i++) m[(i % 4) * 4 + i / 4] = matrix[i]!.GetValue<double>();
        }
        else
        {
            var q = Vector(node["rotation"], 0, 0, 0, 1);
            var s = Vector(node["scale"], 1, 1, 1);
            var t = Vector(node["translation"], 0, 0, 0);
            var r = QuaternionMatrix(q[0], q[1], q[2], q[3]);
            for (var row = 0; row < 3; row++)
            {
                for (var column = 0; column < 3; column++) m[row * 4 + column] = r[row * 3 + column] * s[column];
                m[row * 4 + 3] = t[row];
            }
            m[15] = 1;
        }
        var result = new Matrix4d(m);
        if (m.Any(v => !double.IsFinite(v)) || Math.Abs(result.Determinant3()) < 1e-10) throw new PackageException("invalid glTF node transform");
        return result;
    }

    private static double[] Vector(JsonNode? node, params double[] fallback) =>
        node is JsonArray array ? array.Select(v => v!.GetValue<double>()).ToArray() : fallback;

    public double Determinant3() =>
        this[0, 0] * (this[1, 1] * this[2, 2] - this[1, 2] * this[2, 1]) -
        this[0, 1] * (this[1, 0] * this[2, 2] - this[1, 2] * this[2, 0]) +
        this[0, 2] * (this[1, 0] * this[2, 1] - this[1, 1] * this[2, 0]);

    // Inverse of the upper-left 3 × 3 block, row-major.
    public double[] Inverse3()
    {
        var d = Determinant3();
        double a = this[0, 0], b = this[0, 1], c = this[0, 2], e = this[1, 0], f = this[1, 1], g = this[1, 2], h = this[2, 0], i = this[2, 1], k = this[2, 2];
        return new[]
        {
            (f * k - g * i) / d, (c * i - b * k) / d, (b * g - c * f) / d,
            (g * h - e * k) / d, (a * k - c * h) / d, (c * e - a * g) / d,
            (e * i - f * h) / d, (b * h - a * i) / d, (a * f - b * e) / d,
        };
    }

    public Matrix4d Inverse()
    {
        // General 4 × 4 inverse (Gauss-Jordan with partial pivoting).
        var a = new double[4, 8];
        for (var r = 0; r < 4; r++)
        {
            for (var c = 0; c < 4; c++) a[r, c] = this[r, c];
            a[r, 4 + r] = 1;
        }
        for (var c = 0; c < 4; c++)
        {
            var pivot = c;
            for (var r = c + 1; r < 4; r++) if (Math.Abs(a[r, c]) > Math.Abs(a[pivot, c])) pivot = r;
            if (a[pivot, c] == 0) throw new PackageException("singular matrix");
            if (pivot != c) for (var k = 0; k < 8; k++) (a[c, k], a[pivot, k]) = (a[pivot, k], a[c, k]);
            var scale = a[c, c];
            for (var k = 0; k < 8; k++) a[c, k] /= scale;
            for (var r = 0; r < 4; r++)
            {
                if (r == c || a[r, c] == 0) continue;
                var factor = a[r, c];
                for (var k = 0; k < 8; k++) a[r, k] -= factor * a[c, k];
            }
        }
        var m = new double[16];
        for (var r = 0; r < 4; r++) for (var c = 0; c < 4; c++) m[r * 4 + c] = a[r, 4 + c];
        return new Matrix4d(m);
    }

    public Matrix4d Transposed()
    {
        var m = new double[16];
        for (var r = 0; r < 4; r++) for (var c = 0; c < 4; c++) m[r * 4 + c] = this[c, r];
        return new Matrix4d(m);
    }

    public Matrix4d WithUpper3(double[] r, double[] t)
    {
        var m = new double[16];
        for (var row = 0; row < 3; row++)
        {
            for (var c = 0; c < 3; c++) m[row * 4 + c] = r[row * 3 + c];
            m[row * 4 + 3] = t[row];
        }
        m[15] = 1;
        return new Matrix4d(m);
    }

    public double[] Upper3()
    {
        var r = new double[9];
        for (var row = 0; row < 3; row++) for (var c = 0; c < 3; c++) r[row * 3 + c] = this[row, c];
        return r;
    }

    public double[] Translation => new[] { this[0, 3], this[1, 3], this[2, 3] };

    public bool Close(Matrix4d other, double tolerance)
    {
        for (var i = 0; i < 16; i++)
            if (Math.Abs(_m[i] - other._m[i]) > tolerance + 1e-5 * Math.Abs(other._m[i])) return false;
        return true;
    }

    // pipeline/stagepack/gltf.py matrix_quaternion: [x, y, z, w] of the upper-left 3 × 3 block (row-major input).
    public static double[] Quaternion(double[] m)
    {
        double M(int r, int c) => m[r * 3 + c];
        var trace = M(0, 0) + M(1, 1) + M(2, 2);
        double[] q;
        if (trace > 0)
        {
            var s = Math.Sqrt(trace + 1) * 2;
            q = new[] { (M(2, 1) - M(1, 2)) / s, (M(0, 2) - M(2, 0)) / s, (M(1, 0) - M(0, 1)) / s, s / 4 };
        }
        else
        {
            var i = 0;
            if (M(1, 1) > M(i, i)) i = 1;
            if (M(2, 2) > M(i, i)) i = 2;
            int j = (i + 1) % 3, k = (i + 2) % 3;
            var s = Math.Sqrt(Math.Max(0, 1 + M(i, i) - M(j, j) - M(k, k))) * 2;
            q = new double[4];
            q[3] = (M(k, j) - M(j, k)) / s;
            q[i] = s / 4;
            q[j] = (M(j, i) + M(i, j)) / s;
            q[k] = (M(k, i) + M(i, k)) / s;
        }
        var n = Math.Sqrt(q.Sum(v => v * v));
        return q.Select(v => v / n).ToArray();
    }
}
