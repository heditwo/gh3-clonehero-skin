namespace Gh3Skin;

// The Wait command of a component script (CScriptComponent), with GH3's units: seconds, game frames, frames, or one
// frame, and the interleavable form against the millisecond clock.
internal sealed class Gh3ComponentWait
{
    // GH3: one frame of "Wait n frames" in milliseconds, the float value 1000 / 60 of GH3.exe.
    private const float FrameMilliseconds = 16.666666030883789f;
    private readonly bool _gameFrames;
    private readonly bool _onePerFrame;
    private readonly bool _interleavable;
    private readonly bool _ignoreSlowMotion;
    private float _remaining;
    private bool _updated;

    public Gh3ComponentWait(Gh3QbValue parameters, uint milliseconds = 0)
    {
        var amount = (float)Gh3ScriptRuntime.Numeric(parameters.Fields.FirstOrDefault(field => field.Name == 0 && field.Value.Type is Gh3QbType.Integer or Gh3QbType.Float)?.Value ?? Gh3ScriptRuntime.Number(0));
        if (!float.IsFinite(amount) || amount < 0 || amount > Gh3Limits.MaximumMilliseconds) throw new InvalidDataException("Invalid component wait duration");
        if (parameters.Flag("Seconds") || parameters.Flag("Second")) amount *= Gh3Time.MillisecondsPerSecond;
        else if (parameters.Flag("GameFrame") || parameters.Flag("GameFrames") || parameters.Flag("Game")) _gameFrames = true;
        else if (parameters.Flag("Frame") || parameters.Flag("Frames")) amount *= FrameMilliseconds;
        else _onePerFrame = parameters.Flag("one_per_frame");
        _interleavable = !_gameFrames && !_onePerFrame && parameters.Flag("interleavable");
        _ignoreSlowMotion = parameters.Flag("ignoreslomo");
        _remaining = _gameFrames || _onePerFrame ? (int)amount : _interleavable ? (float)((double)milliseconds + (uint)amount) : (int)amount * Gh3Time.SecondsPerMillisecond;
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
