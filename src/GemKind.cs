namespace Gh3Skin;

internal readonly record struct GemKind(int Index)
{
    public static readonly GemKind Stock = new(-1);

    public static GemKind FromSprites(string head, string body)
    {
        if (body == "spr_newnotes_strip4_3" || head.StartsWith("spr_open_notes_strip5_", StringComparison.Ordinal))
            return Stock;
        if (head.StartsWith("spr_newnotes_strip4_", StringComparison.Ordinal))
            return new GemKind(head == "spr_newnotes_strip4_1" ? 1 : 0);
        if (head.StartsWith("spr_star_notes_", StringComparison.Ordinal))
            return new GemKind(head is "spr_star_notes_strip4_1" or "spr_star_notes_cap_strip4_1" ? 3 : 2);
        return Stock;
    }
}
