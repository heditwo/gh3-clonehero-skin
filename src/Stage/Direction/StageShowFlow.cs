namespace Gh3Skin;

// GH3's pre-song and post-song transitions (guitar_transitions.qb, guitar_intro.qb, guitar_events.qb) as stage cues.
internal static class StageShowFlow
{
    // Default_Intro_Transition and Default_SongWon_Transition both last 8000 ms.
    public const double IntroLength = 8;
    public const double WinLength = 8;
    // win_song waits for the end of the last note, then 1.1 s (IsWinPort) and Song_Win_Delay (2 s) before song_won.
    public const double WinDelay = 3.1;
    // waitAndKillHighway hides the highway 0.5 s after the "You Rock!" text appears.
    public const double HighwayHideDelay = 0.5;
    // Orders a queued cue after the cue at the same time.
    private const double QueueAfter = 0.001;
    private static readonly string[] Members = { "guitarist", "bassist", "singer", "drummer" };

    public static double WinTime(double lastNoteEnd) => lastNoteEnd + WinDelay;

    // `smallVenue`: the stage's placements.json smallVenue (GH3 UseSmallVenueAnims, guitar_character.qb): the small-stage
    // intro and win animations.
    public static StageTimeline Apply(StageTimeline timeline, bool smallVenue, double? winTime)
    {
        var cues = new List<StageCue>(timeline.Cues);
        cues.AddRange(Intro(timeline, smallVenue));
        if (winTime is { } win)
        {
            // play_outro kills the camera-cut iterator, so later note cameras do not cut away from the win camera.
            cues.RemoveAll(cue => cue.Type is "camera" or "gh3-camera" && cue.Time >= win);
            cues.AddRange(Win(win, smallVenue));
        }
        return new StageTimeline(cues, timeline.Source)
        {
            SourceSong = timeline.SourceSong, Countoff = timeline.Countoff,
            ChannelSources = timeline.ChannelSources, Diagnostics = timeline.Diagnostics
        };
    }

    private static IEnumerable<StageCue> Intro(StageTimeline timeline, bool smallVenue)
    {
        const double start = -IntroLength;
        // EndWithDefaultCamera with SyncWithNoteCameras: a note camera less than 2 s after the transition takes over.
        var noteCamera = timeline.Cameras.FirstOrDefault(cue => cue.Time >= 0 && cue.Time < 2);
        var end = noteCamera?.Time ?? 0;
        yield return new(start, "camera", "intro", end - start);
        if (noteCamera == null) yield return new(0, "camera", "default");
        // play_intro_anims: every member changes to its intro stance at once, then queues Stance_A for the end of that animation.
        var intro = smallVenue ? "intro_smstg" : "intro";
        foreach (var role in Members)
        {
            yield return new(start, "stance", role == "drummer" ? "intro" : intro, Role: role, Immediate: true);
            yield return new(start + QueueAfter, "stance", "stance_a", Role: role);
        }
        // GH3: GH_SFX_Intro_WarmUp starts 100 ms into the transition. intro_sequence_props: the highway rises at -2000 ms
        // (its sound 1.5 s before the move ends), the buttons ripple up at -800 ms, and the HUD kick plays when the
        // 200 ms HUD move from -400 ms ends.
        yield return Audio(start + 0.1, "GH_SFX_Intro_WarmUp");
        yield return Audio(-1.5, "SoundEvent", ("event", "Song_Intro_Highway_Up")); // GH3: see above
        yield return Audio(-0.8, "SoundEvent", ("event", "Notes_Ripple_Up_SFX")); // GH3: see above
        yield return Audio(-0.2, "SoundEvent", ("event", "Song_Intro_Kick_SFX")); // GH3: see above
    }

    private static IEnumerable<StageCue> Win(double time, bool smallVenue)
    {
        // GuitarEvent_SongWon_Spawned and Common_SongWon_Transition.
        yield return Audio(time, "SoundEvent", ("event", "You_Rock_End_SFX"));
        yield return Audio(time, "You_Rock_Waiting_Crowd_SFX");
        yield return Audio(time + HighwayHideDelay, "SoundEvent", ("event", "Crowd_Fast_Surge_Cheer"));
        yield return new(time, "camera", "win", WinLength);
        // play_win_anims: play_special_anim with the win stance, the Idle animation, and the arms disabled.
        var win = smallVenue ? "win_smstg" : "win";
        foreach (var role in Members)
        {
            yield return new(time, "stance", role is "guitarist" or "bassist" ? win : "win", Role: role, Immediate: true);
            yield return new(time, "perform", "idle", Role: role, Immediate: true, DisableArms: 2);
        }
    }

    private static StageCue Audio(double time, string script, params (string Name, string Value)[] parameters)
    {
        var call = new Gh3ScriptEvent("scripts", Gh3ScriptRuntime.Structure(new[]
        {
            new Gh3QbField(Gh3Checksum.Value("time"), new(Gh3QbType.Integer, -1, (uint)Math.Max(0, Math.Round(time * 1000)))),
            new Gh3QbField(Gh3Checksum.Value("scr"), Gh3ScriptRuntime.Key(script)),
            new Gh3QbField(Gh3Checksum.Value("params"), Gh3ScriptRuntime.Structure(
                parameters.Select(parameter => new Gh3QbField(Gh3Checksum.Value(parameter.Name), Gh3ScriptRuntime.Key(parameter.Value)))))
        }));
        return new(time, "gh3-audio", call.Encode());
    }
}
