namespace Gh3Skin;

// GH3's highway as measured in a 1600 x 900 recording of original GH3 (My Curse, Expert):
// rails meet at VanishingY, the frets are at StrikeY, and a beat line τ seconds before the frets
// is drawn at VanishingY + DepthScale / (τ + DepthScale / (StrikeY - VanishingY)).
// The player camera that reproduces this projection follows from Clone Hero's note speed and lane width.
// DepthScale is calibrated so that the skin's beat lines and gems both land on GH3's (within 0.5%).
internal static class HighwayView
{
    public const float ScreenHeight = 900;
    public const float VanishingY = 183.6f;
    public const float StrikeY = 796.5f;
    public const float DepthScale = 442.8f;
    public const float LaneSpacing = 128.3f;
    public const float RailSlope = 0.5102f;
    // GH3 highway_top_width1 = 160 at y 305 in GH3's 1280 x 720 layout (highway_playline1 655 - highway_height1 350).
    public const float TopY = 305 * ScreenHeight / 720;
    public const float TextureWidth = 0.987f;
    // The board, gems and beat lines fade in between these rows (measured from the board's contrast in GH3 frames).
    public const float FadeStartY = 384;
    public const float FadeLength = 22;

    public const float NoteSpeed = 7;
    // Clone Hero's gem lanes; its fret sprites sit 0.2 apart, so the skin moves them onto the gem lanes.
    public const float LaneWidth = 0.1935f;
    public const float StrikeZ = 0.09f;
    public const float NeckStartZ = -1;
    public const float NeckSpriteHalfWidth = 0.48f;
    public const float NeckSpriteLength = 1.92f;

    public static readonly float LeadTime = DepthScale / (StrikeY - VanishingY);
    private static readonly float FocalOverCos = LaneSpacing * NoteSpeed * LeadTime / LaneWidth;
    public static readonly float PitchRadians = MathF.Asin((ScreenHeight / 2 - VanishingY) / FocalOverCos);
    public static readonly float Focal = FocalOverCos * MathF.Cos(PitchRadians);
    public static readonly float FieldOfView = 2 * MathF.Atan(ScreenHeight / 2 / Focal) * 180 / MathF.PI;
    public static readonly float CameraPitch = PitchRadians * 180 / MathF.PI;
    public static readonly float CameraHeight = DepthScale * NoteSpeed * MathF.Pow(MathF.Cos(PitchRadians), 2) / Focal;
    public static readonly float CameraZ = StrikeZ - (LeadTime * NoteSpeed - CameraHeight * MathF.Tan(PitchRadians));
    public static readonly float NeckWidth = TextureWidth * RailSlope * (StrikeY - VanishingY) / LaneSpacing * LaneWidth / NeckSpriteHalfWidth;
    public static readonly float TopTime = TimeAt(TopY);
    public static readonly float NeckLength = (StrikeZ + NoteSpeed * TopTime - NeckStartZ) / NeckSpriteLength;
    public static readonly float NeckEndZ = NeckStartZ + NeckLength * NeckSpriteLength;
    // guitar_fretbar.qb: thin_fretbar_8note_params (1 to 180 BPM) for the x/4 time signatures that GH3 songs use.
    public const float ThinFretbarMaxBpm = 180;
    // GH3's board repeats every 1 / 0.987 s of gem travel and scrolls by one repeat per second.
    public const float RepeatsPerUnit = 0.987f / NoteSpeed;
    public const float RepeatsPerSecond = 1;

    public static float FadeAt(float z)
    {
        var t = Math.Clamp((ScreenY((z - StrikeZ) / NoteSpeed) - FadeStartY) / FadeLength, 0, 1);
        return t * t * (3 - 2 * t);
    }

    // GH3 draws a thin fretbar halfway between two beats when the beat is at most ThinFretbarMaxBpm.
    public static bool HasThinFretbar(float beatSeconds) => beatSeconds > 0 && 60 / beatSeconds <= ThinFretbarMaxBpm + 1e-3f;

    public static float TimeAt(float screenY) => DepthScale / (screenY - VanishingY) - LeadTime;

    public static float ScreenY(float time) => VanishingY + DepthScale / (time + LeadTime);

    // Clone Hero's scroll shader moves the texture by songTime * speed / 6 UV.
    public static float ScrollSpeed(float noteSpeed) => noteSpeed / NoteSpeed * 6 * RepeatsPerSecond;

    public static float TextureTiling => NeckLength * NeckSpriteLength * RepeatsPerUnit;
}
