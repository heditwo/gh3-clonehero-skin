using System.Text.Json.Nodes;

namespace Gh3Skin.Packages;

// The player's stage and band choices from the in-game picker, kept in UserData/GH3Skin/choices.json.
internal sealed class PlayerChoices
{
    public const string RandomStage = "random";
    public const string SongStage = "song";
    public const string DefaultCharacter = "default";
    public const string FallbackStage = "gh3.budokan";
    public static readonly string[] Roles = { "guitarist", "bassist", "drummer", "singer" };

    public string Stage { get; set; } = FallbackStage;
    public bool SongSuggestions { get; set; } = true;
    public bool ShowHint { get; set; } = true;
    public Dictionary<string, string> Characters { get; } = Roles.ToDictionary(role => role, _ => DefaultCharacter);

    public string Character(string role) => Characters.TryGetValue(role, out var id) ? id : DefaultCharacter;

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
            ["characters"] = new JsonObject(Roles.Select(role => KeyValuePair.Create(role, (JsonNode?)Character(role)))),
        });
        File.Move(temporary, path, true);
    }
}
