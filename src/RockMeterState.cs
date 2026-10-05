namespace Gh3Skin;

internal sealed class RockMeterState
{
    private const float SmoothTime = 0.16f;
    private const float MaximumSpeed = 90;
    private const float BackgroundFadeAngle = 15;
    private float _velocity;

    public float Angle { get; private set; }
    public float BackgroundRed => Math.Clamp(Angle / BackgroundFadeAngle, 0, 1);
    public float BackgroundGreen => Math.Clamp(-Angle / BackgroundFadeAngle, 0, 1);
    public string Color => Angle < HudState.RockGreenBoundaryAngle ? "green" :
        Angle >= HudState.RockRedBoundaryAngle ? "red" : "yellow";

    public void Reset(float rock = 0.5f)
    {
        Angle = HudState.RockNeedleAngle(rock);
        _velocity = 0;
    }

    public bool Update(float rock, float deltaTime)
    {
        if (!float.IsFinite(deltaTime) || deltaTime <= 0)
            return false;
        // A stalled frame must not consume the entire visible needle movement.
        deltaTime = Math.Min(deltaTime, 1f / 30);
        var target = HudState.RockNeedleAngle(rock);
        var previous = Angle;
        var omega = 2 / SmoothTime;
        var steps = (int)Math.Ceiling(deltaTime * 240);
        var stepTime = deltaTime / steps;
        var decay = MathF.Exp(-omega * stepTime);
        for (var index = 0; index < steps; index++)
        {
            var start = Angle;
            var change = Math.Clamp(Angle - target, -MaximumSpeed * SmoothTime, MaximumSpeed * SmoothTime);
            var destination = Angle - change;
            var step = (_velocity + omega * change) * stepTime;
            _velocity = (_velocity - omega * step) * decay;
            Angle = destination + (change + step) * decay;
            if ((target - start) * (target - Angle) <= 0 ||
                Math.Abs(Angle - target) < 0.0001f && Math.Abs(_velocity) < 0.001f)
            {
                Angle = target;
                _velocity = 0;
                break;
            }
        }
        return Angle != previous;
    }
}
