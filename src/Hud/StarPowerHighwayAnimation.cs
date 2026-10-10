namespace Gh3Skin;

// The frame of the active Star Power rail animation (GH3: sys_Big_Bolt01, 8 frames at 15 fps).

internal sealed class StarPowerHighwayAnimation
{
    public const int FrameCount = 8;
    public const int FramesPerSecond = 15;
    private float _started;
    private bool _active;

    public int Frame { get; private set; } = -1;

    public void Reset()
    {
        _started = 0;
        _active = false;
        Frame = -1;
    }

    public void Update(bool active, float time)
    {
        if (!float.IsFinite(time))
            return;
        if (active && (!_active || time < _started))
            _started = time;
        _active = active;
        Frame = active ? (int)((time - _started) * FramesPerSecond) % FrameCount : -1;
    }
}
