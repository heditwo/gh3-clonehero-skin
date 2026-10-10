namespace Gh3Skin;

// Validation limits of stage data and script values. They reject corrupt or hostile input; GH3's own data stays far
// below them.
internal static class StageLimits
{
    // One day: the largest time or duration in seconds.
    public const int MaximumSeconds = 86400;
    // The largest absolute value of a script number field.
    public const int MaximumMagnitude = 100000;
    // The largest count of generated items (segments, cuts, cues, particles, frames).
    public const int MaximumCount = 100000;
    public const int MaximumFrameRate = 240;
}
