using System.Text.Json.Nodes;

namespace Gh3Skin.Packages;

// The player's stage, band, and direction choices from the in-game picker, kept in UserData/GH3Skin/choices.json.
internal sealed class PlayerChoices
{
    public const string RandomStage = "random";
    public const string SongStage = "song";
    public const string DefaultCharacter = "default";
    // The stage of "Song's choice" when the song selects none, and of a chosen stage that is not installed.
    public const string FallbackStage = "gh3.budokan";
    public static readonly string[] Roles = { "guitarist", "bassist", "drummer", "singer" };

    public string Stage { get; set; } = SongStage;
    public bool SongSuggestions { get; set; } = true;
    public bool ShowHint { get; set; } = true;
    // Rock Band VENUE tracks (StageChartReader.UseVenueTrack) and cues from chart sections for charts without direction
    // (StageChartReader.UseGeneratedDirection). GH3_SKIN_VENUE_TRACK and GH3_SKIN_GENERATED_DIRECTION override them.
    public bool VenueTrack { get; set; } = true;
    public bool GeneratedDirection { get; set; } = true;
    // Weaker, rarer Rock Band flares and slower strobes (StageRockBandVenue.ReduceFlashing); GH3_SKIN_REDUCE_FLASHING overrides it.
    public bool ReduceFlashing { get; set; }
    public Dictionary<string, string> Characters { get; } = Roles.ToDictionary(role => role, _ => DefaultCharacter);

    public string Character(string role) => Characters.TryGetValue(role, out var id) ? id : DefaultCharacter;

    // The first ID of the next (or previous) character group: IDs that share the part before the second dot are one
    // character in several outfits (gh3.judy.outfit1.style2), so that 70 GH3 variants do not need 70 clicks.
    public static string Jump(IReadOnlyList<string> ids, int index, int direction)
    {
        static string Group(string id) => id.IndexOf('.', id.IndexOf('.') + 1) is var dot and > 0 ? id[..dot] : id;
        var group = Group(ids[index]);
        var i = index;
        do i = (i + direction + ids.Count) % ids.Count;
        while (i != index && Group(ids[i]) == group);
        if (direction < 0)
            while (Group(ids[(i - 1 + ids.Count) % ids.Count]) == Group(ids[i]) && (i - 1 + ids.Count) % ids.Count != index) i = (i - 1 + ids.Count) % ids.Count;
        return ids[i];
    }

    // An environment switch ("off" or "0" turns it off, another value on) wins over the player's choice.
    public static bool Switch(string? environment, bool choice) => environment == null ? choice : environment is not ("off" or "0");

    public static bool ValidStage(string value) => value is RandomStage or SongStage || PackageManifest.ValidId(value);

    // A missing or damaged file gives the defaults; the problem text goes to the log.
    public static PlayerChoices Load(string path, out string problem)
    {
        problem = "";
        var choices = new PlayerChoices();
        if (!File.Exists(path)) return choices;
        try
        {
            var root = PackageJson.Object(File.ReadAllBytes(path));
            if (PackageJson.Text(root["stage"]) is { } stage && ValidStage(stage)) choices.Stage = stage;
            if (PackageJson.Bool(root["songSuggestions"]) is { } suggestions) choices.SongSuggestions = suggestions;
            if (PackageJson.Bool(root["showHint"]) is { } hint) choices.ShowHint = hint;
            if (PackageJson.Bool(root["venueTrack"]) is { } venue) choices.VenueTrack = venue;
            if (PackageJson.Bool(root["generatedDirection"]) is { } generated) choices.GeneratedDirection = generated;
            if (PackageJson.Bool(root["reduceFlashing"]) is { } reduce) choices.ReduceFlashing = reduce;
            if (root["characters"] is JsonObject characters)
                foreach (var role in Roles)
                    if (PackageJson.Text(characters[role]) is { } id && (id == DefaultCharacter || PackageManifest.ValidId(id))) choices.Characters[role] = id;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or PackageException or System.Text.Json.JsonException)
        {
            problem = $"{path} could not be read ({error.Message}); the defaults are used";
        }
        return choices;
    }

    public void Save(string path)
    {
        var temporary = path + ".tmp";
        PackageJson.Write(temporary, new JsonObject
        {
            ["version"] = 1, ["stage"] = Stage, ["songSuggestions"] = SongSuggestions, ["showHint"] = ShowHint,
            ["venueTrack"] = VenueTrack, ["generatedDirection"] = GeneratedDirection, ["reduceFlashing"] = ReduceFlashing,
            ["characters"] = new JsonObject(Roles.Select(role => KeyValuePair.Create(role, (JsonNode?)Character(role)))),
        });
        File.Move(temporary, path, true);
    }
}
