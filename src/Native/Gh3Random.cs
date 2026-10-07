namespace Gh3Skin;

internal sealed class Gh3Random
{
    public int Seed1 { get; set; }
    public int Seed2 { get; set; } = 0x12b9b0a1;
    public int Seed3 { get; set; } = 0x0aa2fb3f;

    public Gh3Random(int seed) => Seed1 = seed;

    public int Next(int limit)
    {
        if (limit < 0) throw new InvalidDataException("Random limit cannot be negative");
        unchecked
        {
            var value = Seed2 * Seed1 + Seed3;
            Seed2 = (Seed2 ^ value) + (value >> 4);
            Seed1 = value;
            Seed3 += (value >> 3) - 0x10101010;
            return ((value & 0xffff) * limit) >> 16;
        }
    }

    public Gh3QbValue Range(float a, float b, bool? integer = null)
    {
        if (!float.IsFinite(a) || !float.IsFinite(b) || a < int.MinValue || b < int.MinValue || a >= int.MaxValue || b >= int.MaxValue)
            throw new InvalidDataException("Invalid random range");
        var low = Math.Min(a, b);
        var high = Math.Max(a, b);
        var lowInteger = (int)(low < 0 ? low - .5f : low + .5f);
        var highInteger = (int)(high < 0 ? high - .5f : high + .5f);
        if (integer ?? (MathF.Abs(low - lowInteger) <= .00001f && MathF.Abs(high - highInteger) <= .00001f))
            return Gh3ScriptRuntime.Number(unchecked(lowInteger + Next(checked(highInteger - lowInteger + 1))));
        return new(2, -1, Next(32761) * (high - low) * 3.052503234357573e-05f + low);
    }
}

internal sealed class Gh3CrtRandom
{
    public uint State { get; set; } = 1;

    public int Next()
    {
        State = unchecked(State * 214013 + 2531011);
        return (int)((State >> 16) & 32767);
    }
}
