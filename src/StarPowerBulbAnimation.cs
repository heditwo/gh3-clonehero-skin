namespace Gh3Skin;

internal readonly record struct RockMeterPose(float Scale, float Angle, float X, float Y)
{
    public static RockMeterPose Rest => new(1, 0, 0, 0);
}

internal sealed class StarPowerBulbAnimation
{
    // GH3 interrupts the meter morphs before their endpoints; measured poses retain that motion.
    private static readonly RockMeterPose[] EntryPoses =
    {
        RockMeterPose.Rest,
        new(1.0059f, 0.17f, 0.04f, -0.53f), new(1.0237f, 0.49f, -0.04f, -1.93f),
        new(1.0559f, 1.12f, -0.26f, -4.53f), new(1.0972f, 1.93f, -0.46f, -7.88f),
        new(1.1495f, 3.01f, -0.58f, -12.22f), new(1.2220f, 4.46f, -0.89f, -18.13f),
        new(1.3040f, 6.08f, -1.35f, -24.81f), new(1.3924f, 7.84f, -1.81f, -32.02f),
        new(1.4453f, 8.88f, -2.20f, -36.40f), new(1.4238f, 8.32f, -2.12f, -34.07f),
        new(1.3802f, 7.23f, -1.74f, -29.36f), new(1.3063f, 5.35f, -1.31f, -21.61f),
        new(1.2112f, 2.96f, -0.72f, -11.67f), new(1.0994f, 0.15f, -0.10f, -0.05f),
        new(0.9565f, -3.39f, 0.62f, 14.72f), new(0.7995f, -7.35f, 1.17f, 30.83f),
        new(0.6226f, -11.80f, 1.28f, 48.98f), new(0.5995f, -12.45f, 1.39f, 51.45f),
        new(0.6098f, -12.21f, 1.20f, 50.23f), new(0.6291f, -11.57f, 1.19f, 47.70f),
        new(0.6580f, -10.61f, 1.32f, 43.93f), new(0.6964f, -9.43f, 1.24f, 38.98f),
        new(0.7496f, -7.79f, 1.05f, 32.20f), new(0.8067f, -6.01f, 0.87f, 24.95f),
        new(0.8771f, -3.79f, 0.65f, 15.78f), new(0.9532f, -1.44f, 0.30f, 6.02f),
        RockMeterPose.Rest
    };
    private readonly float[] _from = new float[3];
    private bool _expanded;
    private bool _initialized;
    private bool _animating;
    private float _started = float.NegativeInfinity;
    private float _pulseStarted = float.NegativeInfinity;
    private float _now;

    public void Reset()
    {
        _expanded = false;
        _initialized = false;
        _animating = false;
        _started = float.NegativeInfinity;
        _pulseStarted = float.NegativeInfinity;
        _now = 0;
        Array.Clear(_from);
    }

    public bool Update(float power, bool active, float now)
    {
        var wasAnimating = _animating;
        var expanded = active || power >= 0.5f;
        _now = now;
        var changed = expanded != _expanded;
        if (!_initialized && active)
        {
            _expanded = true;
        }
        else if (changed)
        {
            for (var bulb = 0; bulb < 3; bulb++)
                _from[bulb] = Extension(bulb + 3);
            _expanded = expanded;
            _started = now;
            if (expanded && now - _pulseStarted >= 27f / 60)
                _pulseStarted = now;
        }
        _initialized = true;
        _animating = now - _started < (_expanded ? 0.8f : 0.9f) || now - _pulseStarted < 27f / 60;
        return changed || wasAnimating || _animating;
    }

    public float Extension(int bulb)
    {
        if (bulb < 3)
            return 1;
        var elapsed = _now - _started;
        if (_expanded)
            return Lerp(_from[bulb - 3], 1, (elapsed - (bulb - 3) * 0.2f) / 0.4f);
        elapsed -= (5 - bulb) * 0.2f;
        var start = _from[bulb - 3];
        return elapsed < 0.1f ? Lerp(start, start * 1.1f, elapsed / 0.1f) :
            Lerp(start * 1.1f, 0, (elapsed - 0.1f) / 0.4f);
    }

    public RockMeterPose Pose
    {
        get
        {
            var frame = (_now - _pulseStarted) * 60;
            if (frame < 0 || frame >= EntryPoses.Length - 1)
                return RockMeterPose.Rest;
            var index = (int)frame;
            var fraction = frame - index;
            var a = EntryPoses[index];
            var b = EntryPoses[index + 1];
            return new(Lerp(a.Scale, b.Scale, fraction), Lerp(a.Angle, b.Angle, fraction),
                Lerp(a.X, b.X, fraction), Lerp(a.Y, b.Y, fraction));
        }
    }

    private static float Lerp(float a, float b, float fraction) => a + (b - a) * Math.Clamp(fraction, 0, 1);
}
