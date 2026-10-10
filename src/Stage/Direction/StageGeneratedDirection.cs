namespace Gh3Skin;

// Cues for charts without direction: a light mood and a camera cut at each section, and a lightshow step on each bar.
internal static class StageGeneratedDirection
{
    private const int StepLimit = 20000;
    private const string SoloCameraNote = "81"; // GH3: Gh3CameraNote.Solo
    private const string AnyCameraNote = "79"; // GH3: Gh3CameraNote.Any
    private const string NextSnapshotNote = "58"; // GH3: Gh3LightshowNote.NextSnapshot

    public static List<StageTickCue> FromSections(IReadOnlyList<(long Tick, string Name)> sections, int resolution, long end)
    {
        var output = new List<StageTickCue>();
        if (sections.Count == 0) output.Add(new(0, "lighting", "verse"));
        foreach (var (tick, name) in sections)
        {
            var mood = Mood(name);
            output.Add(new(tick, "lighting", mood));
            output.Add(new(tick, "gh3-camera", mood == "solo" ? SoloCameraNote : AnyCameraNote));
        }
        var starts = sections.Select(s => s.Tick).ToHashSet();
        var bar = 4L * resolution;
        var count = 0;
        for (var at = (sections.Count > 0 ? sections[0].Tick : 0) + bar; at < end && count < StepLimit; at += bar, count++)
            if (!starts.Contains(at)) output.Add(new(at, "gh3-note", NextSnapshotNote));
        return output;
    }

    public static string? Section(string text)
    {
        var value = StageRockBandVenue.Normalize(text);
        if (value.StartsWith("section ", StringComparison.Ordinal)) return value[8..].Trim();
        if (value.StartsWith("section_", StringComparison.Ordinal)) return value[8..].Trim();
        if (value.StartsWith("prc_", StringComparison.Ordinal)) return value[4..].Trim();
        return null;
    }

    public static string Mood(string section)
    {
        var name = section.Replace('_', ' ').Replace('-', ' ');
        if (name.Contains("solo")) return "solo";
        if (name.Contains("chorus") && !name.Contains("pre chorus") && !name.Contains("prechorus")) return "chorus";
        if (name.StartsWith("intro", StringComparison.Ordinal)) return "intro";
        return "verse";
    }
}
