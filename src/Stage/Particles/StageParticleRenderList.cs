namespace Gh3Skin;

// GH3's particle submission list: at most 150 emitters per frame, in order.

internal static class StageParticleRenderList
{
    public const int Capacity = 150;

    public static IEnumerable<T> Select<T>(IEnumerable<T> candidates, Func<T, bool> enabled, Func<T, bool> prepare, Action<T>? observe = null)
    {
        var count = 0;
        foreach (var candidate in candidates)
        {
            if (!enabled(candidate)) continue;
            observe?.Invoke(candidate);
            if (!prepare(candidate)) continue;
            yield return candidate;
            if (++count == Capacity) yield break;
        }
    }
}
