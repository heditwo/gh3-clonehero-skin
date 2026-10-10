namespace Gh3Skin;

// Marsaglia's xorshift32: the mod's own repeatable choices (blinks, idle clips, crowd clips, sounds). GH3's own
// generator is Gh3Random.
internal static class XorShift32
{
    // 2^32 / golden ratio: spreads consecutive seeds over the state space.
    public const uint GoldenRatioIncrement = 0x9e3779b9;
    // The (13, 17, 5) shift triple of Marsaglia's paper.
    private const int ShiftA = 13, ShiftB = 17, ShiftC = 5;

    public static uint Next(ref uint state)
    {
        state ^= state << ShiftA;
        state ^= state >> ShiftB;
        state ^= state << ShiftC;
        return state;
    }
}

// The PCG hash (O'Neill, RXS-M-XS output): a value in 0-1 from an integer seed, for stateless particle randomness
// (the fallback effects and hand fire).
internal static class PcgHash
{
    private const uint Multiplier = 747796405u;
    private const uint Increment = 2891336453u;
    private const uint OutputMultiplier = 277803737u;
    private const int ShiftSelectBits = 28;
    private const int OutputShift = 22;
    // Primes that give each cue (or particle), hand, and emitter its own stream: seed = a * CuePrime + b * EmitterPrime.
    public const int CuePrime = 7919;
    public const int EmitterPrime = 997;
    public const int ParticlePrime = 101;

    public static float Unit(int seed)
    {
        var value = unchecked((uint)seed * Multiplier + Increment);
        value = ((value >> (int)((value >> ShiftSelectBits) + 4)) ^ value) * OutputMultiplier;
        return ((value >> OutputShift) ^ value) / (float)uint.MaxValue;
    }
}
