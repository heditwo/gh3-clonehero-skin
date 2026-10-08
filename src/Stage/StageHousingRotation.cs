using System.Numerics;

namespace Gh3Skin;

// Chooses a light housing's orientation from its light show snapshot and its object script, whichever changed last.
// A switch of source turns the housing from where it is, at the light show's turn rate, instead of snapping.
internal sealed class StageHousingRotation
{
    private const double MinimumDuration = 0.3;
    private const double AngularSpeed = Math.PI;
    private Quaternion _snapshot;
    private double _snapshotTime = double.NaN;
    private Quaternion _shown = Quaternion.Identity;
    private bool _scripted;
    private bool _started;
    private (Quaternion From, double Time, double Duration)? _handover;
    private double _time = double.NaN;

    public Quaternion Update(double time, Quaternion snapshot, Quaternion scripted, bool scriptChanged, double scriptTime)
    {
        if (time < _time) Reset();
        _time = time;
        if (double.IsNaN(_snapshotTime) || snapshot != _snapshot) (_snapshot, _snapshotTime) = (snapshot, time);
        var useScript = scriptChanged && scriptTime >= _snapshotTime;
        if (_started && _scripted && !useScript)
        {
            var angle = 2 * Math.Acos(Math.Min(1, Math.Abs(Quaternion.Dot(_shown, snapshot))));
            _handover = (_shown, time, Math.Max(MinimumDuration, 2 * angle / AngularSpeed));
        }
        if (useScript) _handover = null;
        _scripted = useScript;
        _started = true;
        var target = useScript ? scripted : snapshot;
        if (_handover is { } handover)
        {
            if (time >= handover.Time + handover.Duration) _handover = null;
            else
            {
                var t = (float)((time - handover.Time) / handover.Duration);
                target = Quaternion.Slerp(handover.From, target, t * t * (3 - 2 * t));
            }
        }
        return _shown = target;
    }

    private void Reset()
    {
        _snapshotTime = double.NaN;
        _scripted = false;
        _started = false;
        _handover = null;
    }
}
