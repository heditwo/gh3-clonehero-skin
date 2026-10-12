namespace Gh3Skin;

// The seed of the venue scripts and sound banks for the current song. GH3 seeds one shared generator at boot and every
// script draws from it, so its choices cannot be reproduced; the mod picks a new random seed for each song so the show
// varies from play to play. GH3_SKIN_SEED fixes the seed for repeatable captures and tests.
internal static class ScriptSeed
{
    // Tuned: the seed when nothing chose one yet (the logic tests).
    private const int Default = 1;

    public static int Current { get; private set; } = Default;

    public static int NewSong()
    {
        Current = SkinEnvironment.Get(SkinEnvironment.Seed) is { } text && int.TryParse(text, out var fixedSeed)
            ? fixedSeed
            : Random.Shared.Next(1, int.MaxValue);
        return Current;
    }
}
