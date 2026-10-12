namespace Gh3Skin;

// GH3's random generator Rnd (GH3.exe 0x477120, PARITY-NOTES.md#cameras) and RandomRange.
internal sealed class Gh3Random
{
    // GH3.exe 0x4016f3: the seed at boot; the song start does not seed again.
    public const int BootSeed = 0x06680bf3;
    // GH3.exe Rnd: the initial second and third state words, and the step of the third.
    private const int InitialSeed2 = 0x12b9b0a1;
    private const int InitialSeed3 = 0x0aa2fb3f;
    private const int Seed3Step = 0x10101010;
    // GH3 RandomRange of floats: Rnd(32761) / 32760 of the range (3.0525e-05 = 1 / 32760).
    private const int FloatSteps = 32761;
    private const float FloatStepScale = 3.052503234357573e-05f;
    // A range end this close to an integer counts as an integer (RandomRange without "integer" or "float").
    private const float IntegerTolerance = .00001f;

    public int Seed1 { get; set; }
    public int Seed2 { get; set; } = InitialSeed2;
    public int Seed3 { get; set; } = InitialSeed3;

    public Gh3Random(int seed) => Seed1 = seed;

    public int Next(int limit)
    {
        if (limit < 0) throw new InvalidDataException("Random limit cannot be negative");
        unchecked
        {
            var value = Seed2 * Seed1 + Seed3;
            Seed2 = (Seed2 ^ value) + (value >> 4);
            Seed1 = value;
            Seed3 += (value >> 3) - Seed3Step;
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
        if (integer ?? (MathF.Abs(low - lowInteger) <= IntegerTolerance && MathF.Abs(high - highInteger) <= IntegerTolerance))
            return Gh3ScriptRuntime.Number(unchecked(lowInteger + Next(checked(highInteger - lowInteger + 1))));
        return new(Gh3QbType.Float, -1, Next(FloatSteps) * (high - low) * FloatStepScale + low);
    }
}

// The Microsoft C runtime rand(), which GH3's TimeSeed uses (PARITY-NOTES.md#script-runtime).
internal sealed class Gh3CrtRandom
{
    private const uint Multiplier = 214013;
    private const uint Increment = 2531011;
    private const int OutputMask = 32767;

    public uint State { get; set; } = 1;

    public int Next()
    {
        State = unchecked(State * Multiplier + Increment);
        return (int)((State >> 16) & OutputMask);
    }
}
