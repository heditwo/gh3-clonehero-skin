using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using static Gh3Skin.Packages.PackageJson;

namespace Gh3Skin.Packages;

// The bones of a GH3 rig that the band code needs by their use (PACKAGE-FORMAT.md section 10.2): the library writes
// them from the GH3 rig (pipeline/library/rigs.py), so the runtime does not look for bone names. Each value is a bone
// checksum of the rig, or "" when the rig does not have the point.
internal sealed class RigPoints
{
    public static readonly string[] Names = { "instrument", "microphone", "mouthUpper", "mouthLower", "stickLeft", "stickRight", "seat" };
    public static readonly RigPoints None = new();
    // The bone that holds the guitar or the bass.
    public string Instrument { get; init; } = "";
    // The microphone bone; its parent is the microphone stand.
    public string Microphone { get; init; } = "";
    // The middle bones of the upper and the lower lip: the mouth is between them.
    public string MouthUpper { get; init; } = "";
    public string MouthLower { get; init; } = "";
    // The drum sticks; their common parent is the drum kit.
    public string StickLeft { get; init; } = "";
    public string StickRight { get; init; } = "";
    // The drum throne, a child of the drum kit.
    public string Seat { get; init; } = "";

    public static RigPoints Read(JsonNode? node, IReadOnlyCollection<string> bones, string what)
    {
        if (node == null) return None;
        var row = node as JsonObject ?? throw new PackageException($"{what} points must be an object");
        string Point(string name)
        {
            if (row[name] == null) return "";
            var bone = Text(row[name]) ?? throw new PackageException($"invalid {what} point {name}");
            return Regex.IsMatch(bone, @"\A[0-9a-f]{8}\z") && bones.Contains(bone) ? bone : throw new PackageException($"{what} point {name} is not a bone of the rig");
        }
        if (row.Select(pair => pair.Key).FirstOrDefault(key => !Names.Contains(key)) is { } unknown) throw new PackageException($"unknown {what} point {unknown}");
        return new RigPoints
        {
            Instrument = Point("instrument"), Microphone = Point("microphone"), MouthUpper = Point("mouthUpper"), MouthLower = Point("mouthLower"),
            StickLeft = Point("stickLeft"), StickRight = Point("stickRight"), Seat = Point("seat"),
        };
    }
}

// One chain of a GH3 IK parameter array (guitar_anim_tree.qb): bone0-2 and boneTarget as bone checksums, HingeAxis, and
// the hinge cosine limits. `Hand`: "right" or "left" for an arm chain (its strength follows the strum or the fret arm),
// "" for a leg.
internal sealed class IkChainDefinition
{
    private const int MaximumChains = 8;
    public string[] Bones { get; set; } = System.Array.Empty<string>();
    public string Target { get; set; } = "";
    public float[] Hinge { get; set; } = System.Array.Empty<float>();
    public float CosMax { get; set; }
    public float CosMin { get; set; }
    public string Hand { get; set; } = "";

    public bool Valid => Bones.Length == 3 && Hinge.Length == 3 && Hinge.All(float.IsFinite) && float.IsFinite(CosMax) && float.IsFinite(CosMin) &&
        CosMax is >= -1 and <= 1 && CosMin is >= -1 and <= 1 && Hand is "" or "right" or "left";

    public static bool ValidChains(IkChainDefinition[] chains) => chains.Length <= MaximumChains && chains.All(c => c.Valid);
}

// One row of GH3 hero_ConstraintBones (guitar_anim_tree.qb): a twist or split bone that follows `Target` by `Amount`.
internal sealed class RigConstraint
{
    public static readonly string[] Types = { "Split", "SplitR", "Twist", "TwistChild" };
    private const int MaximumConstraints = 64;
    public string Type { get; set; } = "";
    public string Bone { get; set; } = "";
    public string Target { get; set; } = "";
    public float Amount { get; set; }

    public static bool ValidRows(RigConstraint[] rows) => rows.Length <= MaximumConstraints &&
        rows.All(r => Types.Contains(r.Type) && Regex.IsMatch(r.Bone, @"\A[0-9a-f]{8}\z") && Regex.IsMatch(r.Target, @"\A[0-9a-f]{8}\z") &&
            float.IsFinite(r.Amount) && r.Amount is >= 0 and <= 1);
}
