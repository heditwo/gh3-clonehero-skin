namespace Gh3Skin;

internal sealed class ReadyAnimationTrigger
{
    private bool _wasReady;
    private float _started = float.NegativeInfinity;

    public void Reset()
    {
        _wasReady = false;
        _started = float.NegativeInfinity;
    }

    public void Update(float power, float now)
    {
        var ready = power >= 0.5f;
        if (ready && !_wasReady)
            _started = now;
        _wasReady = ready;
    }

    public int Frame(float now, int count)
    {
        var elapsed = now - _started;
        return !_wasReady || elapsed < 0 || elapsed >= count / 60f
            ? -1
            : (int)(elapsed * 60);
    }
}
