namespace Gh3Skin;

// The animation frame of the Star Power gem atlas (GH3: 16 frames at 30 fps, one clock for all lanes).

internal static class GemAnimation
{
    public const int FrameCount = 16;
    public const int FramesPerSecond = 30;

    public static int Frame(double time) => (int)(time * FramesPerSecond % FrameCount);
}
