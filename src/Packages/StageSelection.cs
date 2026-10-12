using System.Text.Json.Nodes;

namespace Gh3Skin.Packages;

// A song's stage suggestion: a venue package in the song folder, or the ID of an installed stage.
internal sealed record StageSuggestion(string StageId, string PackagePath, string Source);

internal sealed record StageDecision(string StageId, string PackagePath, string Reason);

// Selects the stage of one song from the player's choice, the song's suggestion, and the installed stages.
internal static class StageSelection
{
    public const string SongIniKey = "gh3_stage";
    public const string SongPackage = "venue.chstage";
    // The song direction file beside the chart (StageChartReader).
    public const string EventFile = "stage-events.json";
    // The size limit of StageChartReader (Midi.MaximumFileBytes).
    private const long MaximumEventFileBytes = 32L * 1024 * 1024;

    // In order: venue.chstage (zip or development folder) in the song folder, `gh3_stage` in song.ini, then `stage` in the
    // song's direction file (the GH3 career venue of a song of a GH3 chart pack, docs/CUSTOM-CONTENT.md section 6).
    public static StageSuggestion? Suggestion(string songFolder)
    {
        if (string.IsNullOrEmpty(songFolder) || !Directory.Exists(songFolder)) return null;
        var package = Path.Combine(songFolder, SongPackage);
        if (File.Exists(package) || Directory.Exists(package)) return new StageSuggestion("", package, SongPackage);
        var ini = Path.Combine(songFolder, "song.ini");
        if (File.Exists(ini) && SongIniValue(File.ReadAllLines(ini), SongIniKey) is { Length: > 0 } stage)
            return new StageSuggestion(stage, "", "song.ini " + SongIniKey);
        if (SongDirection(songFolder) is { Stage.Length: > 0 } direction)
            return new StageSuggestion(direction.Stage, "", EventFile + " stage");
        return null;
    }

    // The band members that the song's direction file suggests: {role: character ID}.
    public static IReadOnlyDictionary<string, string> CharacterSuggestions(string songFolder) =>
        string.IsNullOrEmpty(songFolder) || !Directory.Exists(songFolder) ? new Dictionary<string, string>() :
            SongDirection(songFolder)?.Characters ?? new Dictionary<string, string>();

    private sealed record DirectionSuggestions(string Stage, Dictionary<string, string> Characters);

    // Only the suggestion fields of stage-events.json; StageChartReader checks the whole file when the song loads.
    private static DirectionSuggestions? SongDirection(string songFolder)
    {
        var path = Path.Combine(songFolder, EventFile);
        if (!File.Exists(path) || new FileInfo(path).Length > MaximumEventFileBytes) return null;
        try
        {
            var root = PackageJson.Object(File.ReadAllBytes(path));
            var stage = PackageJson.Text(root["stage"]) is { } id && PackageManifest.ValidId(id) ? id : "";
            var characters = new Dictionary<string, string>();
            if (root["characters"] is JsonObject roles)
                foreach (var role in PlayerChoices.Roles)
                    if (PackageJson.Text(roles[role]) is { } character && PackageManifest.ValidId(character)) characters[role] = character;
            return new DirectionSuggestions(stage, characters);
        }
        catch (Exception error) when (error is IOException or PackageException or System.Text.Json.JsonException) { return null; }
    }

    // The value of a key in the [song] section of song.ini (case-insensitive, as Clone Hero reads it).
    public static string? SongIniValue(IEnumerable<string> lines, string key)
    {
        var inSong = false;
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.StartsWith('['))
            {
                inSong = line.Equals("[song]", StringComparison.OrdinalIgnoreCase);
                continue;
            }
            var equals = line.IndexOf('=');
            if (!inSong || equals < 0) continue;
            if (line[..equals].Trim().Equals(key, StringComparison.OrdinalIgnoreCase)) return line[(equals + 1)..].Trim();
        }
        return null;
    }

    // `environment` is GH3_SKIN_STAGE (tests and recordings): a stage ID there wins over everything. `available` are the
    // stages that can load (installed packages). `previous` is avoided by a random choice when possible.
    public static StageDecision Choose(PlayerChoices choices, string? environment, IReadOnlyList<string> available, StageSuggestion? suggestion,
        Random random, string previous = "")
    {
        if (environment == "off") return new StageDecision("", "", SkinEnvironment.Stage + "=off");
        var mode = !string.IsNullOrEmpty(environment) && PlayerChoices.ValidStage(environment) ? environment : choices.Stage;
        var fixedByEnvironment = mode == environment && mode is not (PlayerChoices.RandomStage or PlayerChoices.SongStage);
        var note = "";
        if (suggestion != null && !fixedByEnvironment && (mode == PlayerChoices.SongStage || choices.SongSuggestions))
        {
            if (suggestion.PackagePath.Length > 0) return new StageDecision("", suggestion.PackagePath, "the song's " + suggestion.Source);
            if (available.Contains(suggestion.StageId)) return new StageDecision(suggestion.StageId, "", "the song's " + suggestion.Source);
            note = $" (the song's stage {suggestion.StageId} from {suggestion.Source} is not installed)";
        }
        if (mode is PlayerChoices.RandomStage or PlayerChoices.SongStage)
        {
            if (available.Count == 0) return new StageDecision(PlayerChoices.FallbackStage, "", "no stage is installed" + note);
            if (mode == PlayerChoices.SongStage && available.Contains(PlayerChoices.FallbackStage))
                return new StageDecision(PlayerChoices.FallbackStage, "", "the default stage: the song has no stage" + note);
            var pool = available.Count > 1 ? available.Where(id => id != previous).ToArray() : available.ToArray();
            var reason = mode == PlayerChoices.RandomStage ? "random choice" : $"random choice: the song has no stage and {PlayerChoices.FallbackStage} is not installed";
            return new StageDecision(pool[random.Next(pool.Length)], "", reason + note);
        }
        if (available.Contains(mode) || fixedByEnvironment) return new StageDecision(mode, "", (fixedByEnvironment ? SkinEnvironment.Stage : "the player's choice") + note);
        var fallback = available.Contains(PlayerChoices.FallbackStage) ? PlayerChoices.FallbackStage : available.FirstOrDefault() ?? PlayerChoices.FallbackStage;
        return new StageDecision(fallback, "", $"the chosen stage {mode} is not installed" + note);
    }
}
