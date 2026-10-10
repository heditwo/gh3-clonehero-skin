namespace Gh3Skin;

// Marsaglia's xorshift32: the mod's own repeatable choices (blinks, idle clips, crowd clips, sounds). GH3's own
// generator is Gh3Random.
internal static class XorShift32
{
    // 2^32 / golden ratio: spreads consecutive seeds over the state space.
    public const uint GoldenRatioIncrement = 0x9e3779b9;

    public static uint Next(ref uint state)
    {
        state ^= state << 13;
        state ^= state >> 17;
        state ^= state << 5;
        return state;
    }
}
