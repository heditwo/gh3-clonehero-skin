namespace Gh3Skin;

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
