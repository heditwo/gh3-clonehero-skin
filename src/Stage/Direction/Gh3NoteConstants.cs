namespace Gh3Skin;

// GH3 camera notes: the camera group of each note in CameraCuts_NoteMapping (guitar_cameras.qb, the same in all ten
// venues). docs/CUSTOM-CONTENT.md#camera-cuts has the full table.
internal static class Gh3CameraNote
{
    // The cameras group, any type.
    public const int Any = 79;
    // cameras_solo.
    public const int Solo = 81;
    public const int OrbitStage = 82;
    // The cameras group, type closeup (the shots aim at the guitarist).
    public const int Closeup = 84;
    // The cameras group, type mid.
    public const int Mid = 85;
    // The cameras group, type stage (wide).
    public const int Wide = 87;
    public const int Singer = 88;
    public const int Drummer = 89;
    public const int OrbitDrummer = 92;
    public const int GuitarCloseup = 95;
    public const int Bassist = 97;
    // cameras_vert_stagerear: behind the drummer, over the band to the crowd.
    public const int StageRear = 102;
    public const int SingerCloseup = 104;
    public const int ZoomFast = 112;
    public const int ZoomOut = 113;
    // cameras_pan: between the guitarist and the singer.
    public const int Pan = 114;
    public const int StageDive = 117;
}

// GH3 lightshow notes (LightShow_NoteMapping, scripts\fx\lightshow.qb; PARITY-NOTES.md#lighting-and-node-flags).
internal static class Gh3LightshowNote
{
    // 39-53: blend times (BlendTimes).
    public const int BlendFirst = 39, BlendLast = 53;
    public const int Pyro = 56;
    // Next state, keep the beams.
    public const int NextState = 57;
    // Next snapshot of the current state, with saved positions.
    public const int NextSnapshot = 58;
    public const int StrobeToggle = 60;
    // 61-69: color overrides (Colors).
    public const int ColorFirst = 61, ColorLast = 69;
    // 70-76: moods (Moods).
    public const int MoodFirst = 70, MoodLast = 76;
    public static readonly string[] BlendTimes = { "0.15", "0", "0.05", "0.1", "0.15", "0.2", "0.25", "0.3", "0.4", "0.5", "0.6", "0.7", "0.8", "0.9", "1" };
    public static readonly string[] Colors = { "purple", "orange", "off", "magenta", "white", "yellow", "blue", "red", "green" };
    public static readonly string[] Moods = { "strobe", "flare", "blackout", "solo", "chorus", "verse", "intro" };
}

// GH3 notes of the drums, crowd, and fret-hand tracks (<song>_drums_notes, _crowd_notes, and the anim notes).
internal static class Gh3TrackNote
{
    // drums: 36-45 left hand (37-45 animated), 48 kick, 49-57 right hand, 70 the count-off.
    public const int DrumLeftFirst = 36, DrumLeftAnimatedFirst = 37, DrumLeftLast = 45;
    public const int DrumKick = 48, DrumRightFirst = 49, DrumRightLast = 57;
    public const int DrumCountoff = 70;
    // crowd: 72-80 cycle applause, surge_fast, surge_slow; 72-74 and 78-80 also override the crowd state.
    public const int CrowdFirst = 72, CrowdLast = 80;
    public const int CrowdOverrideLast = 74, CrowdOverrideFirst = 78;
    // Fret-hand positions: 101-110 bassist, 118-127 guitarist.
    public const int BassFretFirst = 101, BassFretLast = 110;
    public const int GuitarFretFirst = 118, GuitarFretLast = 127;
    // The lane bits of an instrument note, green (bit 0) to orange (bit 4).
    public const int LaneMask = 31;
}

// Rock Band VENUE track notes (RB1/RB2). Source: the VENUE format of GuitarGame_ChartFormats (docs/CUSTOM-CONTENT.md).
internal static class RockBandVenueNote
{
    public const int SpotlightFirst = 37, SpotlightLast = 41;
    public const int KeyframeNext = 48, KeyframePrevious = 49, KeyframeFirst = 50;
    // Camera: 60 random cut, 61-64 focus on bassist, drummer, guitarist, vocalist.
    public const int CameraRandom = 60;
    public const int FocusBassist = 61, FocusDrummer = 62, FocusGuitarist = 63, FocusVocalist = 64;
    // Camera constraints: 70 no behind, 71 only far, 72 only close-ups, 73 no close-ups.
    public const int ConstraintFirst = 70;
    public const int OnlyFar = 71, OnlyCloseups = 72, NoCloseups = 73;
    public const int SingAlongFirst = 85, SingAlongLast = 87;
    public const int PostProcessingFirst = 96, PostProcessingLast = 110;
}
