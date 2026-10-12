using System.Text;
using System.Text.RegularExpressions;

// Fails when GH3-facing code (src/Stage, src/Hud, src/Gameplay, src/Gh3) has a numeric literal that is not trivial,
// not named, and has no source marker (docs/CODE-STYLE.md, "Constants"). A literal is accepted when:
// - its value is trivial (Trivial below: integers to 16, halves and quarters, powers of two, degrees, 1000);
// - it is part of a const or static readonly declaration, an enum, or a file named *Constants.cs or *Tables.cs;
// - a comment with "GH3:", "Measured:", or "Tuned:" is on a line of its statement or in the comment block directly
//   above the statement;
// - tests/fixtures/literal-allowlist.txt lists it ("<path> | <literal> | <trimmed source line>").
// GH3_LITERAL_REPORT=1 prints every rejected literal and stops the test run; GH3_LITERAL_FOLDERS=src/Packages,src
// reports other folders ("src" alone: its top folder).
internal static class LiteralGuardTests
{
    private static readonly string[] Folders = { "src/Stage", "src/Hud", "src/Gameplay", "src/Gh3" };
    private static readonly string[] Markers = { "GH3:", "Measured:", "Tuned:" };
    private static readonly HashSet<double> Trivial = new()
    {
        0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 0.25, 0.5, 0.75, 24, 32, 60, 64, 90, 100, 128, 180, 255, 256, 360,
        1000, 1e-6
    };
    private static readonly HashSet<ulong> TrivialHex = new() { 0, 1, 0xff, 0xffff, 0xffffffff };
    private static readonly Regex Number = new(
        @"(?<![\w.])(0[xX][0-9A-Fa-f_]+[uUlL]*|(?:\d[\d_]*\.?\d*|\.\d+)(?:[eE][+-]?\d+)?[fFdDmMuUlL]*)(?![\w.])", RegexOptions.Compiled);
    private static readonly Regex Declaration = new(@"\bconst\b|\bstatic readonly\b|\benum\b", RegexOptions.Compiled);

    public static void Run()
    {
        var root = Root();
        var allowed = File.ReadAllLines(Path.Combine(root, "tests/fixtures/literal-allowlist.txt"))
            .Where(line => line.Length > 0 && !line.StartsWith('#')).ToHashSet();
        var rejected = new List<string>();
        var used = new HashSet<string>();
        var folders = Environment.GetEnvironmentVariable("GH3_LITERAL_FOLDERS")?.Split(',') ?? Folders;
        foreach (var folder in folders)
            foreach (var path in Directory.EnumerateFiles(Path.Combine(root, folder), "*.cs", folder == "src" ? SearchOption.TopDirectoryOnly : SearchOption.AllDirectories).OrderBy(p => p, StringComparer.Ordinal))
            {
                var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
                if (relative.EndsWith("Constants.cs") || relative.EndsWith("Tables.cs")) continue;
                foreach (var (literal, line) in Rejected(File.ReadAllLines(path)))
                {
                    var key = $"{relative} | {literal} | {line}";
                    if (allowed.Contains(key)) { used.Add(key); continue; }
                    rejected.Add(key);
                }
            }
        var report = Environment.GetEnvironmentVariable("GH3_LITERAL_REPORT") == "1";
        if (report)
        {
            foreach (var line in rejected) Console.WriteLine(line);
            Console.WriteLine($"Literal guard: {rejected.Count} rejected literal(s), {allowed.Count} allowed");
            Environment.Exit(0);
        }
        var stale = allowed.Except(used).ToArray();
        if (rejected.Count > 0 || stale.Length > 0)
            throw new Exception("Literal guard: name these literals or add a GH3:, Measured:, or Tuned: source comment " +
                "(docs/CODE-STYLE.md):\n" + string.Join("\n", rejected.Take(40)) +
                (stale.Length > 0 ? "\nRemove these stale allowlist lines:\n" + string.Join("\n", stale) : ""));
        Console.WriteLine($"Literal guard passed: {allowed.Count} allowlisted literal(s)");
    }

    private static string Root()
    {
        for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder != null; folder = folder.Parent)
            if (File.Exists(Path.Combine(folder.FullName, "src/Gh3Skin.csproj"))) return folder.FullName;
        return Directory.GetCurrentDirectory();
    }

    // The rejected literals of a file and their trimmed source lines.
    public static IEnumerable<(string Literal, string Line)> Rejected(string[] lines)
    {
        var code = new string[lines.Length];
        var comments = new string[lines.Length];
        var inBlockComment = false;
        for (var i = 0; i < lines.Length; i++)
            (code[i], comments[i]) = Split(lines[i], ref inBlockComment);
        var named = NamedLines(code);
        for (var i = 0; i < lines.Length; i++)
        {
            if (named[i] || code[i].TrimStart().StartsWith('[')) continue;
            foreach (Match match in Number.Matches(code[i]))
            {
                if (IsTrivial(match.Value) || HasMarker(code, comments, i)) continue;
                yield return (match.Value, lines[i].Trim());
            }
        }
    }

    private static bool IsTrivial(string literal)
    {
        var text = literal.Replace("_", "");
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return TrivialHex.Contains(Convert.ToUInt64(text[2..].TrimEnd('u', 'U', 'l', 'L'), 16));
        return double.TryParse(text.TrimEnd('f', 'F', 'd', 'D', 'm', 'M', 'u', 'U', 'l', 'L'), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var value) && Trivial.Contains(Math.Abs(value));
    }

    // Lines of const and static readonly declarations (to the end of their statement) and of enum bodies.
    private static bool[] NamedLines(string[] code)
    {
        var named = new bool[code.Length];
        var depth = 0;
        var inside = false;
        for (var i = 0; i < code.Length; i++)
        {
            if (!inside && Declaration.IsMatch(code[i])) { inside = true; depth = 0; }
            if (!inside) continue;
            named[i] = true;
            foreach (var character in code[i])
                depth += character is '{' or '(' or '[' ? 1 : character is '}' or ')' or ']' ? -1 : 0;
            var end = code[i].TrimEnd();
            if (depth <= 0 && (end.EndsWith(';') || end.EndsWith('}') && Regex.IsMatch(code[i], @"\benum\b|^\s*}"))) inside = false;
        }
        return named;
    }

    // A marker on a line of the statement of line i, or in the comment block directly above the statement.
    private static bool HasMarker(string[] code, string[] comments, int i)
    {
        bool Marked(int line) => Markers.Any(marker => comments[line].Contains(marker, StringComparison.Ordinal));
        var start = i;
        while (start > 0)
        {
            var previous = code[start - 1].TrimEnd();
            if (previous.Length == 0 && comments[start - 1].Length == 0) break;
            if (previous.Length == 0) { start--; continue; }
            // Braces of blocks are on their own lines (Allman style); a brace after code continues an expression.
            if (previous.Trim() == "{" && start > 1 && Regex.IsMatch(code[start - 2].TrimEnd(), @"(\bswitch|=|=>|\bnew(\[\])?|\bnew \w+(\[\])?)$"))
            {
                start--;
                continue;
            }
            if (previous.EndsWith(';') || previous.Trim() is "{" or "}") break;
            start--;
        }
        for (var line = start; line <= i; line++)
            if (Marked(line)) return true;
        // The statement may continue after line i on later lines of the same statement.
        for (var line = i + 1; line < code.Length && !code[line - 1].TrimEnd().EndsWith(';') && line <= i + 8; line++)
            if (Marked(line)) return true;
        for (var line = start - 1; line >= 0 && code[line].Trim().Length == 0 && comments[line].Length > 0; line--)
            if (Marked(line)) return true;
        return false;
    }

    // Splits a line into code (strings and characters blanked, interpolation holes kept) and comment text.
    private static (string Code, string Comment) Split(string line, ref bool inBlockComment)
    {
        var code = new StringBuilder();
        var comment = new StringBuilder();
        for (var i = 0; i < line.Length; i++)
        {
            if (inBlockComment)
            {
                var close = line.IndexOf("*/", i, StringComparison.Ordinal);
                comment.Append(close < 0 ? line[i..] : line[i..close]);
                if (close < 0) break;
                inBlockComment = false;
                i = close + 1;
                continue;
            }
            if (line[i] == '/' && i + 1 < line.Length && line[i + 1] == '/') { comment.Append(line[(i + 2)..]); break; }
            if (line[i] == '/' && i + 1 < line.Length && line[i + 1] == '*') { inBlockComment = true; i++; continue; }
            if (line[i] is '"' or '\'')
            {
                var quote = line[i];
                var interpolated = i > 0 && line[i - 1] == '$' || i > 1 && line[i - 2] == '$';
                var verbatim = i > 0 && line[i - 1] == '@' || i > 1 && line[i - 2] == '@';
                code.Append(' ');
                for (i++; i < line.Length && line[i] != quote; i++)
                {
                    if (line[i] == '\\' && !verbatim) { i++; continue; }
                    if (interpolated && line[i] == '{')
                    {
                        if (i + 1 < line.Length && line[i + 1] == '{') { i++; continue; }
                        var close = line.IndexOf('}', i);
                        if (close < 0) close = line.Length;
                        var hole = line[(i + 1)..close];
                        var format = hole.IndexOf(':');
                        code.Append(' ').Append(format >= 0 && !hole.Contains('?') ? hole[..format] : hole).Append(' ');
                        i = close;
                    }
                }
                code.Append(' ');
                continue;
            }
            code.Append(line[i]);
        }
        return (code.ToString(), comment.ToString());
    }
}
