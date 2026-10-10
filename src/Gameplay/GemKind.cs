namespace Gh3Skin;

// The GH3 gem kind of a Clone Hero note, from its sprite names (regular, HOPO, tap, Star Power).

internal readonly record struct GemKind(int Index)
{
    public static readonly GemKind Stock = new(-1);

    public static GemKind FromSprites(string head, string body)
    {
        if (head.StartsWith("spr_open_notes_strip5_", StringComparison.Ordinal))
            return Stock;
        if (head.StartsWith("spr_newnotes_strip4_", StringComparison.Ordinal))
            return new GemKind(body == "spr_newnotes_strip4_3" ? 4 : head == "spr_newnotes_strip4_1" ? 1 : 0);
        if (head.StartsWith("spr_star_notes_", StringComparison.Ordinal))
            return new GemKind(body.StartsWith("spr_star_notes_strip2_", StringComparison.Ordinal) ? 5 :
                head == "spr_star_notes_cap_strip4_2" ? 3 : 2);
        return Stock;
    }
}
