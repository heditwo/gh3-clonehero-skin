namespace Gh3Skin;

// Decides when the centre messages play: "N Note Streak!" and the Star Power ready banner. GH3 shows one centre
// message at a time for each player (PARITY-NOTES.md, HUD messages):
// - show_star_power_ready (guitar_starpower.qb) waits while the HUD_Note_Streak_Combo container exists, then sets
//   star_power_ready_on_p1 and plays the banner. It clears the flag at its end. It plays nothing when Star Power is
//   active at that time.
// - hud_show_note_streak_combo (guitar_hud_2d.qb) waits while star_power_ready_on_p1 is 1. It returns without a message
//   when the container of a streak message already exists.
// The message that waits starts when the other one ends; it plays in full. When both start in the same frame, the
// ready banner plays first (measured: Number of the Beast, 200 notes and Star Power ready at song 31.77 s).

internal sealed class CenterMessageQueue
{
    // GH3: GH3.exe 0x422ebb (the streak routine at 0x422bc0) spawns hud_show_note_streak_combo at a streak of exactly
    // 50 and at each multiple of 100.
    public const int FirstStreakMessage = 50;
    public const int StreakMessageInterval = 100;
    // GH3: increase_star_power_guts (guitar_starpower.qb) spawns show_star_power_ready when the charge reaches 50.
    private const float ReadyCharge = 0.5f;
    private readonly float _streakSeconds;
    private readonly float _readySeconds;
    private int _lastCombo;
    private bool _wasReady;
    private bool _readyPending;
    private int _pendingStreak;

    // streakSeconds is 0 when the mod draws no streak message.
    public CenterMessageQueue(float streakSeconds, float readySeconds)
    {
        _streakSeconds = streakSeconds;
        _readySeconds = readySeconds;
    }

    public float StreakStart { get; private set; } = float.NegativeInfinity;
    public int StreakCombo { get; private set; }
    public float ReadyStart { get; private set; } = float.NegativeInfinity;

    public void Reset()
    {
        _lastCombo = 0;
        _wasReady = false;
        _readyPending = false;
        _pendingStreak = 0;
        StreakStart = float.NegativeInfinity;
        StreakCombo = 0;
        ReadyStart = float.NegativeInfinity;
    }

    public void Update(int combo, float power, bool active, float now)
    {
        var ready = power >= ReadyCharge && !active;
        if (ready && !_wasReady)
        {
            _readyPending = StreakPlaying(now);
            ReadyStart = _readyPending ? float.NegativeInfinity : now;
        }
        if (!ready) _readyPending = false;
        _wasReady = ready;

        if (combo < _lastCombo) _lastCombo = 0;
        if (StreakMessageAt(_lastCombo, combo) is { } streak && !StreakPlaying(now) && _pendingStreak == 0)
        {
            if (ReadyPlaying(now)) _pendingStreak = streak;
            else StartStreak(streak, now);
        }
        _lastCombo = combo;

        // A message that waits starts at the end of the other one; then the other side can wait in turn.
        if (_readyPending && !StreakPlaying(now))
        {
            _readyPending = false;
            ReadyStart = StreakStart + _streakSeconds;
        }
        if (_pendingStreak != 0 && !ReadyPlaying(now))
        {
            // The banner hides early when Star Power is activated; then the streak starts now.
            StartStreak(_pendingStreak, Math.Min(now, ReadyStart + _readySeconds));
            _pendingStreak = 0;
        }
    }

    // The ready banner frame at this time (60 frames a second), or -1.
    public int ReadyFrame(float now)
    {
        var elapsed = now - ReadyStart;
        return !_wasReady || elapsed < 0 || elapsed >= _readySeconds ? -1 : (int)(elapsed * 60);
    }

    private void StartStreak(int combo, float start)
    {
        StreakCombo = combo;
        StreakStart = start;
    }

    private bool StreakPlaying(float now) => now - StreakStart < _streakSeconds;

    private bool ReadyPlaying(float now) => _wasReady && now - ReadyStart < _readySeconds;

    // The streak that GH3 announces when the streak goes from previous to current, or null.
    private static int? StreakMessageAt(int previous, int current)
    {
        if (current / StreakMessageInterval > previous / StreakMessageInterval)
            return current / StreakMessageInterval * StreakMessageInterval;
        return previous < FirstStreakMessage && current >= FirstStreakMessage ? FirstStreakMessage : null;
    }
}
