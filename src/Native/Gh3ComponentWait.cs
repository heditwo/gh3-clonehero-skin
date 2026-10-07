namespace Gh3Skin;

internal sealed class Gh3ComponentWait
{
    private readonly bool _gameFrames;
    private readonly bool _onePerFrame;
    private readonly bool _interleavable;
    private readonly bool _ignoreSlowMotion;
    private float _remaining;
    private bool _updated;

    public Gh3ComponentWait(Gh3QbValue parameters, uint milliseconds = 0)
    {
        var amount = (float)Gh3ScriptRuntime.Numeric(parameters.Fields.FirstOrDefault(field => field.Name == 0 && field.Value.Type is 1 or 2)?.Value ?? Gh3ScriptRuntime.Number(0));
        if (!float.IsFinite(amount) || amount < 0 || amount > 86400000) throw new InvalidDataException("Invalid component wait duration");
        if (parameters.Flag("Seconds") || parameters.Flag("Second")) amount *= 1000;
        else if (parameters.Flag("GameFrame") || parameters.Flag("GameFrames") || parameters.Flag("Game")) _gameFrames = true;
        else if (parameters.Flag("Frame") || parameters.Flag("Frames")) amount *= 16.666666030883789f;
        else _onePerFrame = parameters.Flag("one_per_frame");
        _interleavable = !_gameFrames && !_onePerFrame && parameters.Flag("interleavable");
        _ignoreSlowMotion = parameters.Flag("ignoreslomo");
        _remaining = _gameFrames || _onePerFrame ? (int)amount : _interleavable ? (float)((double)milliseconds + (uint)amount) : (int)amount * .001f;
    }

    public bool Advance(float duration, float unscaledDuration, uint milliseconds, Func<bool> takeFrame)
    {
        if (_interleavable) return milliseconds >= (double)_remaining;
        if (_gameFrames || _onePerFrame)
        {
            if (_remaining == 0) return !_onePerFrame || takeFrame();
            _remaining--;
            return false;
        }
        if (_ignoreSlowMotion) duration = unscaledDuration;
        if (duration > _remaining)
        {
            if (_updated) return true;
            _remaining = 0;
        }
        else _remaining -= duration;
        _updated = true;
        return false;
    }
}
