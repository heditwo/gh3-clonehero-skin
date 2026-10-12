namespace Gh3Skin.Packages;

// The venue of each GH3 career song: the `level` of its tier in GH3_Career_Songs (DATA/PAK/qb.pak.xen). Bonus and
// download songs have no venue in GH3 (the player picks one), so they are not in the table.
internal static class Gh3CareerVenues
{
    public static readonly IReadOnlyDictionary<string, string> Tiers = new Dictionary<string, string>
    {
        ["load_z_party"] = "gh3.party", ["load_z_dive"] = "gh3.dive", ["load_z_video"] = "gh3.video", ["load_z_artdeco"] = "gh3.artdeco",
        ["load_z_prison"] = "gh3.prison", ["load_z_wikker"] = "gh3.wikker", ["load_z_budokan"] = "gh3.budokan", ["load_z_hell"] = "gh3.hell",
    };

    private static readonly (string Level, string[] Songs)[] Career =
    {
        ("load_z_party", new[] { "slowride", "talkdirtytome", "hitmewithyourbestshot", "storyofmylife", "rocknrollallnite" }),
        ("load_z_dive", new[] { "mississippiqueen", "schoolsout", "sunshineofyourlove", "barracuda", "bosstom", "bullsonparade" }),
        ("load_z_video", new[] { "whenyouwereyoung", "missmurder", "theseeker", "laydown", "paintitblack" }),
        ("load_z_artdeco", new[] { "paranoid", "anarchyintheuk", "koolthing", "mynameisjonas", "evenflow" }),
        ("load_z_prison", new[] { "holidayincambodia", "rockulikeahurricane", "sameoldsonganddance", "lagrange", "bossslash", "welcometothejungle" }),
        ("load_z_wikker", new[] { "blackmagicwoman", "cherubrock", "blacksunshine", "themetal", "pridenjoy" }),
        ("load_z_budokan", new[] { "beforeiforget", "stricken", "threesandsevens", "knightsofcydonia", "cultofpersonality" }),
        ("load_z_hell", new[] { "rainingblood", "cliffsofdover", "numberofthebeast", "one", "bossdevil" }),
    };

    public static readonly IReadOnlyDictionary<string, string> Songs =
        Career.SelectMany(tier => tier.Songs.Select(song => (song, Tiers[tier.Level]))).ToDictionary(pair => pair.song, pair => pair.Item2);

    public static IEnumerable<(string Level, string[] Songs)> Table => Career;

    public static string? Of(string song) => Songs.TryGetValue(song.ToLowerInvariant(), out var venue) ? venue : null;
}
