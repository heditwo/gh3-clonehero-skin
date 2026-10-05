namespace Gh3Skin;

internal static class GemAnimation
{
    public const int FrameCount = 16;
    public const int FramesPerSecond = 30;

    public static int Frame(double time) => (int)(time * FramesPerSecond % FrameCount);
}
