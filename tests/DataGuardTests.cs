using System.Text;
using System.Text.RegularExpressions;

// PACKAGING-PLAN decision 16 (docs/CODE-STYLE.md, "Data, not code"): the mod does not choose behaviour by stage ID,
// GH3 zone, character name, or GH3 bone name. Fails when a string literal in src/ is
// - a GH3 stage ID (gh3.<name>) or a GH3 zone name (z_<name>),
// - a GH3 character name (profile or full name),
// - a GH3 bone name (Bone_<name>),
// outside the files of Allowed: the GH3 humanoid and face bone names (HumanoidRoles.cs, the reader of the GH3 rigs) and
// the default stage setting (PlayerChoices.cs). The packages carry the rest (placements.json smallVenue, scripts.json
// zone, the rig points, IK chains, and constraints of catalog.json and characters.json).
internal static class DataGuardTests
{
    private static readonly (string File, string Rule, string Reason)[] Allowed =
    {
        ("src/Packages/HumanoidRoles.cs", "bone", "the humanoid map and the face bones of a GH3 rig come from its bone names (decision 15)"),
        ("src/Packages/PlayerChoices.cs", "stage", "FallbackStage: the default stage of the setting 'Song's choice'"),
    };
    private static readonly string[] CharacterNames =
    {
        "Axel", "Casey", "Izzy", "Johnny", "Judy", "Lars", "Midori", "Xavier", "Slash", "Morello", "Satan", "Lou", "RockGod", "God of Rock",
        "Ripper", "Grim Ripper", "Bret", "Singer_Female", "Singer_Bret",
    };
    private static readonly (string Rule, Regex Pattern)[] Rules =
    {
        ("stage", new Regex(@"^gh3\.[a-z]|^z_[a-z]", RegexOptions.Compiled)),
        ("character", new Regex(@"\b(" + string.Join("|", CharacterNames.Select(Regex.Escape)) + @")\b", RegexOptions.Compiled | RegexOptions.IgnoreCase)),
        ("bone", new Regex(@"\b[Bb]one_[A-Za-z]", RegexOptions.Compiled)),
    };

    public static void Run()
    {
        var root = Root();
        var found = new List<string>();
        foreach (var path in Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories).OrderBy(p => p, StringComparer.Ordinal))
        {
            var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
            if (relative.Contains("/obj/") || relative.Contains("/bin/")) continue;
            foreach (var (literal, line) in Literals(File.ReadAllLines(path)))
                foreach (var (rule, pattern) in Rules)
                    if (pattern.IsMatch(literal) && !Allowed.Any(a => a.File == relative && a.Rule == rule))
                        found.Add($"{relative}:{line}: {rule} \"{literal}\"");
        }
        Check(Rules[0].Pattern.IsMatch("gh3.dive") && Rules[0].Pattern.IsMatch("z_dive") && Rules[1].Pattern.IsMatch("God of Rock") &&
            Rules[2].Pattern.IsMatch("Bone_IK_Hand_Guitar_R") && !Rules[1].Pattern.IsMatch("slashes"), "the guard patterns find their examples");
        Check(Literals(new[] { "var a = \"gh3.dive\"; // \"Bone_Jaw\"", "/* \"Judy\" */ var b = @\"z_\"\"x\";" }).Select(l => l.Literal)
            .SequenceEqual(new[] { "gh3.dive", "z_\"x" }), "the guard reads string literals and skips comments");
        if (found.Count > 0)
            throw new Exception("Data guard (PACKAGING-PLAN decision 16): put this into the package data, not into the code:\n" + string.Join("\n", found.Take(40)));
        Console.WriteLine($"Data guard passed: no GH3 stage ID, zone, character name, or bone name in src/ outside {Allowed.Length} allowed file(s)");
    }

    // The string literals of a file (also the text parts of interpolated strings) and their line numbers; comments
    // are skipped.
    public static IEnumerable<(string Literal, int Line)> Literals(string[] lines)
    {
        var inBlockComment = false;
        for (var n = 0; n < lines.Length; n++)
        {
            var line = lines[n];
            for (var i = 0; i < line.Length; i++)
            {
                if (inBlockComment)
                {
                    var close = line.IndexOf("*/", i, StringComparison.Ordinal);
                    if (close < 0) break;
                    inBlockComment = false;
                    i = close + 1;
                    continue;
                }
                if (line[i] == '/' && i + 1 < line.Length && line[i + 1] == '/') break;
                if (line[i] == '/' && i + 1 < line.Length && line[i + 1] == '*') { inBlockComment = true; i++; continue; }
                if (line[i] == '\'')
                {
                    for (i++; i < line.Length && line[i] != '\''; i++) if (line[i] == '\\') i++;
                    continue;
                }
                if (line[i] != '"') continue;
                var verbatim = i > 0 && line[i - 1] == '@' || i > 1 && line[i - 2] == '@';
                var text = new StringBuilder();
                for (i++; i < line.Length; i++)
                {
                    if (verbatim && line[i] == '"' && i + 1 < line.Length && line[i + 1] == '"') { text.Append('"'); i++; continue; }
                    if (line[i] == '"') break;
                    if (!verbatim && line[i] == '\\' && i + 1 < line.Length) { text.Append(line[i + 1]); i++; continue; }
                    text.Append(line[i]);
                }
                yield return (text.ToString(), n + 1);
            }
        }
    }

    private static string Root()
    {
        for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder != null; folder = folder.Parent)
            if (File.Exists(Path.Combine(folder.FullName, "src/Gh3Skin.csproj"))) return folder.FullName;
        return Directory.GetCurrentDirectory();
    }

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception(name);
    }
}
