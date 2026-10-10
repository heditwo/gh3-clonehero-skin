namespace Gh3Skin;

// The rock meter's needle angle and the background blend and light color of that angle.

internal sealed class RockMeterState
{
    private const float BackgroundFadeAngle = 15;

    public float Angle { get; private set; }
    public float BackgroundRed => Math.Clamp(Angle / BackgroundFadeAngle, 0, 1);
    public float BackgroundGreen => Math.Clamp(-Angle / BackgroundFadeAngle, 0, 1);
    public string Color => Angle < HudState.RockGreenBoundaryAngle ? "green" :
        Angle >= HudState.RockRedBoundaryAngle ? "red" : "yellow";

    public void Reset(float rock = 0.5f) => Angle = HudState.RockNeedleAngle(rock);

    public bool Update(float rock)
    {
        var previous = Angle;
        Angle = HudState.RockNeedleAngle(rock);
        return Angle != previous;
    }
}
