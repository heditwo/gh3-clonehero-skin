namespace Gh3Skin;

// GH3's particle emission over time: rate, burst, delay, and period.

internal sealed class StageParticleEmission
{
    private readonly uint _function;
    private readonly float _period;
    public float Time { get; private set; }
    public float Accumulator { get; private set; }

    public StageParticleEmission(uint function, float period)
    {
        _function = function;
        _period = period;
    }

    public int Step(float duration, float rate, float scale = 1)
    {
        if (duration <= 0) return 0;
        Time += duration;
        if (rate == 0) return 0;
        Accumulator += duration;
        var phase = _period > 0 ? Time % _period / _period : 0;
        var multiplier = 1f;
        if (_function == Gh3Checksum.Value("sin")) multiplier = ((float)Math.Sin(phase * (2 * MathF.PI)) + 1) * .5f;
        else if (_function == Gh3Checksum.Value("onoff")) multiplier = phase > .5f ? 1 : 0;
        else if (_function == Gh3Checksum.Value("triangle")) multiplier = phase > .5f ? 2 - phase * 2 : phase * 2;
        else if (_function == Gh3Checksum.Value("sawtooth")) multiplier = phase;
        else if (_function == Gh3Checksum.Value("invsawtooth")) multiplier = 1 - phase;
        var count = checked((int)((double)multiplier * scale * rate * Accumulator));
        if (count != 0) Accumulator = 0;
        return count;
    }
}
