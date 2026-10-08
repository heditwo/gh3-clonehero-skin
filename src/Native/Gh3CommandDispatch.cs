using System.Globalization;

namespace Gh3Skin;

internal sealed record Gh3CommandResult(string Name, string Status, string[] Gaps, string Type = "", string Value = "",
    string Role = "", bool Immediate = false, bool Loop = false, int Count = 1, int DisableArms = 0);

internal static class Gh3CommandDispatch
{
    public const double DefaultLightBlend = 0.15;
    private sealed record Command(string Name, string Type, string Parameter, string Default, string[] Fields, string[] Flags);
    private static readonly string[] AnimFlags = { "no_wait", "cycle", "disable_auto_arms", "disable_auto_strum" };
    private static readonly Command[] Commands =
    {
        new("Band_ChangeStance", "stance", "stance", "stance_a", new[] { "name", "stance" }, new[] { "no_wait" }),
        new("Band_PlayAnim", "perform", "anim", "idle", new[] { "name", "anim", "repeat_count" }, AnimFlags),
        new("Band_PlayIdle", "perform", "anim", "idle", new[] { "name" }, AnimFlags),
        new("Band_PlayFacialAnim", "face", "anim", "none", new[] { "name", "anim" }, Array.Empty<string>()),
        new("Band_PlaySimpleAnim", "clip", "anim", "none", new[] { "name", "anim" }, Array.Empty<string>()),
        new("Band_WalkToNode", "walk", "node", "guitarist_start", new[] { "name", "node" }, Array.Empty<string>()),
        new("Band_SetStrumStyle", "strum-style", "style", "long", new[] { "name", "style" }, Array.Empty<string>()),
        new("Band_StopStrumming", "strum-style", "style", "none", new[] { "name" }, Array.Empty<string>()),
        new("Band_DisableArms", "arms", "", "2", new[] { "name" }, Array.Empty<string>()),
        new("Band_EnableArms", "arms", "", "0", new[] { "name" }, Array.Empty<string>())
    };
    private static readonly Dictionary<uint, Command> ByKey = Commands.ToDictionary(command => Gh3Checksum.Value(command.Name));
    private static readonly Dictionary<uint, string> AudioCommands = new[] {
        "SoundEvent", "StopSoundEvent", "Countoff_NoteMap", "GH_SFX_Countoff_Logic", "GH3_Crowd_Event_Listener",
        "GH3_AdjustCrowdFastSurge", "GH3_AdjustCrowdSlowSurge", "GH3_Play_A_Crowd_Applause_Based_On_Venue",
        "GH3_Play_A_Fast_Crowd_Swell_Based_On_Venue", "GH3_Play_A_Crowd_OneShot_Positive_Based_On_Venue", "GH3_Play_A_Crowd_OneShot_Negative_Based_On_Venue",
        "GH3_AdjustCrowdSingingVolumeUp", "GH3_AdjustCrowdSingingVolumeDown", "crowd_singalong_volume_up", "crowd_singalong_volume_down",
        "GH3_SFX_Encore_Accept", "GH3_SFX_Encore_Decline", "change_crowd_looping_sfx", "Change_Crowd_Looping_SFX_Good",
        "Change_Crowd_Looping_SFX_Neutral", "Change_Crowd_Looping_SFX_Bad", "One_Shot_SoundEvent", "Skate8_SFX_Backgrounds_New_Area",
        "SoundBussUnlock", "SoundBussLock", "SetSoundBussParams", "PlaySound", "StopSound", "StopStream", "SetSoundParams"
    }.ToDictionary(Gh3Checksum.Value);
    private static readonly Dictionary<uint, string> Roles = new[] { "guitarist", "bassist", "vocalist", "drummer", "world" }
        .ToDictionary(Gh3Checksum.Value, name => name == "vocalist" ? "singer" : name);
    private static readonly Dictionary<uint, string> Names = ("stance_a stance_b stance_c intro intro_smstg win lose win_smstg lose_smstg " +
        "idle jump kick solo special release long_note transitions good normal bad slow med fast short long " +
        "no_strum_anim start_anim cycle_anim stop_left_anim stop_right_anim guitarist_start guitarist_walk01 guitarist_walk02 guitarist_walk03 " +
        "bassist_start bassist_walk01 bassist_walk02 strum mute normal_strums bass_normal_strums bass_pick_strums bass_slap_strums bass_normal bass_pick bass_slap none")
        .Split(' ').ToDictionary(Gh3Checksum.Value);

    public static Gh3CommandResult Resolve(Gh3ScriptEvent call)
    {
        var parameters = call.Parameters;
        var gaps = new List<string>();
        var command = call.Command;
        if (AudioCommands.TryGetValue(command, out var audioCommand))
            return new(audioCommand, "partial", new[] { "Uses source sound events and samples; original-game mixing and random selection remain unverified." }, "gh3-audio", call.Encode());
        if (command == Gh3Checksum.Value("Crowd_StageDiver_Jump") || command == Gh3Checksum.Value("Crowd_StageDiver_Hide"))
        {
            var jump = command == Gh3Checksum.Value("Crowd_StageDiver_Jump");
            var index = parameters?.Field("index");
            var diverCommand = jump ? "Crowd_StageDiver_Jump" : "Crowd_StageDiver_Hide";
            if (index != null && (index.Type != 1 || index.Integer is < 1 or > 128))
                return new(diverCommand, "invalid", new[] { "Invalid stage-diver index." });
            Parameters(parameters, new[] { "index" }, new[] { "no_wait" }, gaps);
            gaps.Add("Uses source venue actors and clips; original-game timing and visual parity remain unverified.");
            return new(diverCommand, "partial", gaps.ToArray(), "stage-diver", jump ? "jump" : "hide", (index?.Integer ?? 1).ToString(CultureInfo.InvariantCulture));
        }
        var crowdAction = new[] { "Crowd_SetHand", "Crowd_AllSetHand", "Crowd_PlayAnim", "Crowd_AllPlayAnim" }
            .FirstOrDefault(name => Gh3Checksum.Value(name) == command);
        if (crowdAction != null)
        {
            var all = crowdAction.Contains("_All", StringComparison.Ordinal);
            var hand = crowdAction.EndsWith("SetHand", StringComparison.Ordinal);
            string? Choice(string parameter, string fallback, IEnumerable<string> choices)
            {
                var reference = parameters?.Field(parameter);
                return reference == null ? fallback : reference.Type == 13 ? choices.FirstOrDefault(name => Gh3Checksum.Value(name) == reference.Integer) : null;
            }
            var target = all ? "" : Choice("name", "crowd1", Enumerable.Range(1, 8).Select(i => "crowd" + i));
            var selection = hand ? Choice("Type", "clap", new[] { "clap", "lighter", "rock", "fist" }) :
                Choice("anim", "idle", new[] { "idle", "special", "starpower" });
            var side = hand ? Choice("hand", "left", new[] { "left", "right" }) : "";
            if (target == null || selection == null || side == null)
                return new(crowdAction, "unimplemented", new[] { "Unresolved crowd member, hand, or animation reference." });
            Parameters(parameters, (hand ? new[] { "hand", "Type" } : new[] { "anim" }).Concat(all ? Array.Empty<string>() : new[] { "name" }).ToArray(), Array.Empty<string>(), gaps);
            gaps.Add("Uses source crowd profiles; original-game animation selection and scheduler parity remain unverified.");
            return new(crowdAction, "partial", gaps.ToArray(), hand ? "crowd-hand" : "crowd-animation", hand ? side + "/" + selection : selection, target);
        }
        if (command == Gh3Checksum.Value("Crowd_StartLighters") || command == Gh3Checksum.Value("Crowd_StopLighters") ||
            command == Gh3Checksum.Value("Crowd_ToggleLighters"))
        {
            var start = command == Gh3Checksum.Value("Crowd_StartLighters");
            var stop = command == Gh3Checksum.Value("Crowd_StopLighters");
            var crowdCommand = start ? "Crowd_StartLighters" : stop ? "Crowd_StopLighters" : "Crowd_ToggleLighters";
            var operation = start ? "start" : stop ? "stop" : parameters?.Flag("on") == true || parameters?.Field("on") != null ? "on" :
                parameters?.Flag("off") == true || parameters?.Field("off") != null ? "off" : "";
            var toggles = start || stop ? Array.Empty<string>() : new[] { "on", "off" };
            Parameters(parameters, toggles, toggles, gaps);
            gaps.Add("Uses source crowd assets and a 60 Hz script wait model; original-game visual and scheduler parity remain unverified.");
            if (operation.Length == 0) return new(crowdCommand, "partial", gaps.ToArray());
            return new(crowdCommand, "partial", gaps.ToArray(), "crowd-lighters", operation);
        }
        if (command == Gh3Checksum.Value("LightShow_SetTime") ||
            command == Gh3Checksum.Value("LightShow") && parameters?.Flag("SetTime") == true)
        {
            var time = parameters?.Field("time");
            var blend = time is { Type: 1 or 2 } ? time.Number : time == null && parameters?.Flag("default") == true ? DefaultLightBlend : double.NaN;
            if (time != null && time.Type is not (1 or 2) || !double.IsFinite(blend) || blend is < 0 or > 30)
                return new("LightShow_SetTime", "invalid", new[] { "Missing or invalid light blend time." });
            Parameters(parameters, new[] { "time" }, new[] { "SetTime", "default" }, gaps);
            return new("LightShow_SetTime", gaps.Count == 0 ? "implemented" : "partial", gaps.ToArray(), "blend",
                blend.ToString(CultureInfo.InvariantCulture));
        }
        if (command == Gh3Checksum.Value("LightShow_SetParams") || command == Gh3Checksum.Value("LightShow_OverrideColor"))
        {
            var mood = command == Gh3Checksum.Value("LightShow_SetParams");
            var field = mood ? "mood" : "color";
            var reference = parameters?.Field(field);
            var choices = mood ? new[] { "intro", "verse", "chorus", "solo", "blackout", "flare", "strobe" } :
                new[] { "green", "red", "blue", "yellow", "white", "magenta", "orange", "purple" };
            var selected = reference?.Type == 13 ? choices.FirstOrDefault(choice => Gh3Checksum.Value(choice) == reference.Integer) : null;
            if (!mood && parameters?.Flag("off") == true) selected = "off";
            var lightingCommand = mood ? "LightShow_SetParams" : "LightShow_OverrideColor";
            if (selected == null) return new(lightingCommand, "unimplemented", new[] { $"Unresolved {field} reference or command variant." });
            Parameters(parameters, new[] { field }, mood ? Array.Empty<string>() : new[] { "off" }, gaps);
            gaps.Add("Venue processors and game-state lighting parity are not fully verified.");
            return new(lightingCommand, "partial", gaps.ToArray(), mood ? "lighting" : "color", selected);
        }
        if (command == Gh3Checksum.Value("LightShow_PyroEvent"))
        {
            Parameters(parameters, Array.Empty<string>(), Array.Empty<string>(), gaps);
            gaps.Add("Uses the source venue pyro script when present; particle rendering parity remains incomplete.");
            return new("LightShow_PyroEvent", "partial", gaps.ToArray(), "pyro");
        }
        if (!ByKey.TryGetValue(command, out var action))
            return Gh3PcCommands.Constants.TryGetValue(command, out var constant) ? new(constant.Name, "implemented", Array.Empty<string>()) :
                new(Gh3QbText.Key(command), "unimplemented", new[] { "No direct handler. Playback also checks the loaded stage script library." });
        Parameters(parameters, action.Fields, action.Flags, gaps);
        var roleValue = parameters?.Field("Name");
        var roleKey = roleValue == null ? Gh3Checksum.Value("guitarist") : roleValue.Type == 13 ? roleValue.Integer : 0;
        if (!Roles.TryGetValue(roleKey, out var role))
            return new(action.Name, "unimplemented", gaps.Append("Unresolved performer reference.").ToArray());
        var value = action.Parameter.Length == 0 ? null : parameters?.Field(action.Parameter);
        if (value != null && value.Type is not (3 or 4 or 13))
            return new(action.Name, "invalid", gaps.Append("Invalid command value type.").ToArray());
        var name = value == null ? action.Default : value.Data is string text ? text.ToLowerInvariant() :
            action.Type is "face" or "clip" ? value.Integer.ToString("x8") : Names.GetValueOrDefault(value.Integer, value.Integer.ToString("x8"));
        var repeat = parameters?.Field("repeat_count");
        if (repeat != null && (repeat.Type != 1 || repeat.Integer is < 1 or > 100))
            return new(action.Name, "invalid", gaps.Append("Invalid animation repeat count.").ToArray());
        gaps.Add("Animation, asset selection, and game-state parity are not fully verified.");
        return new(action.Name, "partial", gaps.ToArray(), action.Type, name, role,
            parameters?.Flag("no_wait") == true, parameters?.Field("cycle") != null || parameters?.Flag("cycle") == true || action.Name == "Band_PlayIdle",
            (int)(repeat?.Integer ?? 1), parameters?.Flag("disable_auto_arms") == true ? 2 : parameters?.Flag("disable_auto_strum") == true ? 1 : 0);
    }

    private static void Parameters(Gh3QbValue? parameters, string[] fields, string[] flags, List<string> gaps)
    {
        if (parameters == null) return;
        var allowed = fields.Select(Gh3Checksum.Value).ToHashSet();
        if (flags.Contains("cycle")) allowed.Add(Gh3Checksum.Value("cycle"));
        var allowedFlags = flags.Select(Gh3Checksum.Value).ToHashSet();
        foreach (var field in parameters.Fields)
        {
            if (field.Name == 0)
            {
                if (field.Value.Type != 13 || !allowedFlags.Contains(field.Value.Integer))
                    gaps.Add("Unhandled flag " + Gh3QbText.Encode(field.Value) + ".");
            }
            else if (!allowed.Contains(field.Name)) gaps.Add($"Unhandled parameter {Gh3QbText.Key(field.Name)}.");
        }
        foreach (var duplicate in parameters.Fields.Where(field => field.Name != 0).GroupBy(field => field.Name).Where(group => group.Count() > 1))
            gaps.Add($"Repeated parameter {Gh3QbText.Key(duplicate.Key)} requires source lookup semantics.");
    }
}
