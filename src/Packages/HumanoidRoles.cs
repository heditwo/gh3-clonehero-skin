namespace Gh3Skin.Packages;

// Humanoid roles of a character skeleton (character.json skeleton.humanoid, PACKAGE-FORMAT.md section 9.6): the VRM 1.0
// humanoid bone names, and the GH3 bone that drives each one. The twist roles have no GH3 bone; the retarget computes them.
internal static class HumanoidRoles
{
    public static readonly string[] Required =
    {
        "hips", "spine", "head", "leftUpperArm", "leftLowerArm", "leftHand", "rightUpperArm", "rightLowerArm", "rightHand",
        "leftUpperLeg", "leftLowerLeg", "leftFoot", "rightUpperLeg", "rightLowerLeg", "rightFoot",
    };
    public static readonly string[] Spine = { "spine", "chest", "upperChest" };
    public static readonly string[] Twist = { "leftUpperArmTwist", "leftLowerArmTwist", "rightUpperArmTwist", "rightLowerArmTwist" };
    private static readonly string[] Fingers = { "Thumb", "Index", "Middle", "Ring", "Little" };
    private static readonly string[] Phalanges = { "Metacarpal", "Proximal", "Distal" };
    private static readonly string[] Segments = { "Proximal", "Intermediate", "Distal" };
    // The GH3 finger joints (checksums), base to tip, of the left and right hand; their names are not known.
    private static readonly Dictionary<string, string[]> Gh3Fingers = new()
    {
        ["leftThumb"] = new[] { "9b410faa", "e139af08", "b28d0e8b" }, ["leftIndex"] = new[] { "7f8c4cd6", "22b5d884", "71017907" },
        ["leftMiddle"] = new[] { "68ec1dda", "f55a971a", "a6ee3699" }, ["leftRing"] = new[] { "d3dd2e3d", "cf2e73af", "9c9ad22c" },
        ["leftLittle"] = new[] { "0fd44a48", "1047c981", "43f36802" },
        ["rightThumb"] = new[] { "614e32c9", "1b36926b", "488233e8" }, ["rightIndex"] = new[] { "858371b5", "d8bae5e7", "8b0e4464" },
        ["rightMiddle"] = new[] { "92e320b9", "0f55aa79", "5ce10bfa" }, ["rightRing"] = new[] { "29d2135e", "35214ecc", "6695ef4f" },
        ["rightLittle"] = new[] { "f5db772b", "ea48f4e2", "b9fc5561" },
    };

    // Role to the GH3 bone checksum, without the spine roles (Source chooses them by the roles that a skeleton has).
    public static readonly IReadOnlyDictionary<string, string> Gh3 = Build();

    public static IEnumerable<string> All => Gh3.Keys.Concat(Spine).Concat(Twist);

    // The roles that a GH3 rig does not map: its jaw and eyes are face bones (Gh3Face).
    private static readonly string[] FaceRoles = { "jaw", "leftEye", "rightEye" };

    // The GH3 face bones that the singer's facial clips and lip sync move (StageLipSync). The GH3 guitarist, bassist,
    // and singer rigs have all of them; the drummer rig has none.
    public static readonly string[] Gh3Face =
    {
        "Bone_Jaw", "Bone_Tongue", "Bone_Mouth_L", "Bone_Mouth_R", "Bone_Lip_Lower_Mid", "Bone_Lip_Upper_Mid", "Bone_Lip_Lower_L",
        "Bone_Lip_Lower_R", "Bone_Lip_Upper_L", "Bone_Lip_Upper_R", "Bone_Lip_Lower_Corner_L", "Bone_Lip_Lower_Corner_R",
        "Bone_Lip_Upper_Corner_L", "Bone_Lip_Upper_Corner_R", "Bone_Cheek_L", "Bone_Cheek_R", "Bone_Cheek_Upper_L", "Bone_Cheek_Upper_R",
        "Bone_Eye_L", "Bone_Eye_R", "Bone_Eyelid_Upper_L", "Bone_Eyelid_Upper_R", "Bone_Eyelid_Lower_L", "Bone_Eyelid_Lower_R",
        "Bone_Brow_L", "Bone_Brow_R", "Bone_Brow_Mid", "Bone_Brow_Mid_L", "Bone_Brow_Mid_R",
    };

    // GH3: the face bones of the lip sync (StageLipSync) in the order of the rows of its measured shape table
    // (StageLipSync.Shapes.cs, tools/lipsync-shapes.py); the middle lip bones are rows 3 and 4.
    public static readonly string[] Gh3Mouth =
    {
        "Bone_Jaw", "Bone_Mouth_L", "Bone_Mouth_R", "Bone_Lip_Lower_Mid", "Bone_Lip_Upper_Mid", "Bone_Cheek_L", "Bone_Cheek_R",
        "Bone_Cheek_Upper_L", "Bone_Cheek_Upper_R", "Bone_Lip_Lower_Corner_L", "Bone_Lip_Lower_Corner_R", "Bone_Lip_Lower_L",
        "Bone_Lip_Lower_R", "Bone_Lip_Upper_Corner_L", "Bone_Lip_Upper_Corner_R", "Bone_Lip_Upper_L", "Bone_Lip_Upper_R", "Bone_Tongue"
    };
    // GH3: the upper eyelids (blinks) and the jaw (the open mouth of a face with blend shapes).
    public static readonly string[] Gh3Lids = { "Bone_Eyelid_Upper_L", "Bone_Eyelid_Upper_R" };
    public const string Gh3Jaw = "Bone_Jaw";
    // GH3: the head, eyes, brows, lower eyelids, and upper cheeks that the singing face moves (StageLipSync.Life), in the
    // order of its measured poses.
    public static readonly string[] Gh3FaceLife =
    {
        "Bone_Head", "Bone_Eye_L", "Bone_Eye_R", "Bone_Brow_L", "Bone_Brow_R", "Bone_Brow_Mid", "Bone_Brow_Mid_L", "Bone_Brow_Mid_R",
        "Bone_Eyelid_Lower_L", "Bone_Eyelid_Lower_R", "Bone_Cheek_Upper_L", "Bone_Cheek_Upper_R"
    };
    // The face bones of the [trace] band lines.
    public static readonly string[] Gh3TraceFace = { "Bone_Jaw", "Bone_Eyelid_Upper_L", "Bone_Lip_Lower_Mid", "Bone_Brow_L" };
    // The leg and spine bones of the [trace] walk lines (the joints of the GH3 walk measurement, PARITY-NOTES.md Walk clips).
    public static readonly string[] Gh3TraceWalk =
    {
        "Bone_Pelvis", "Bone_Thigh_L", "Bone_Thigh_R", "Bone_Knee_L", "Bone_Knee_R", "Bone_Ankle_L", "Bone_Ankle_R", "Bone_Stomach_Lower",
        "Bone_Stomach_Upper", "Bone_Chest", "Bone_Neck", "Bone_Head",
    };

    // The humanoid map of a GH3 rig (bone checksums) from the GH3 bone names: the bones of the Gh3 table, and the three
    // spine roles on bone_stomach_lower, bone_stomach_upper, and bone_chest (SpineSources(3)), so that a GH3 rig maps to
    // itself. The face bones move by name (Gh3Face).
    public static Dictionary<string, int> FromGh3(IReadOnlyList<string> bones)
    {
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < bones.Count; i++) index.TryAdd(bones[i], i);
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (role, checksum) in Gh3)
            if (Array.IndexOf(FaceRoles, role) < 0 && index.TryGetValue(checksum, out var bone)) result[role] = bone;
        var spine = SpineSources(Spine.Length);
        for (var i = 0; i < Spine.Length; i++)
            if (index.TryGetValue(spine[i], out var bone)) result[Spine[i]] = bone;
        return result;
    }

    private static Dictionary<string, string> Build()
    {
        var table = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["hips"] = Gh3Checksum.Of("bone_pelvis"), ["neck"] = Gh3Checksum.Of("bone_neck"), ["head"] = Gh3Checksum.Of("bone_head"),
            ["jaw"] = Gh3Checksum.Of("bone_jaw"), ["leftEye"] = Gh3Checksum.Of("bone_eye_l"), ["rightEye"] = Gh3Checksum.Of("bone_eye_r"),
        };
        foreach (var (side, s) in new[] { ("left", "l"), ("right", "r") })
        {
            table[side + "Shoulder"] = Gh3Checksum.Of($"bone_collar_{s}");
            table[side + "UpperArm"] = Gh3Checksum.Of($"bone_bicep_{s}");
            table[side + "LowerArm"] = Gh3Checksum.Of($"bone_forearm_{s}");
            table[side + "Hand"] = Gh3Checksum.Of($"bone_palm_{s}");
            table[side + "UpperLeg"] = Gh3Checksum.Of($"bone_thigh_{s}");
            table[side + "LowerLeg"] = Gh3Checksum.Of($"bone_knee_{s}");
            table[side + "Foot"] = Gh3Checksum.Of($"bone_ankle_{s}");
            table[side + "Toes"] = Gh3Checksum.Of($"bone_toe_{s}");
            foreach (var finger in Fingers)
            {
                var names = finger == "Thumb" ? Phalanges : Segments;
                for (var k = 0; k < 3; k++) table[side + finger + names[k]] = Gh3Fingers[side + finger][k];
            }
        }
        return table;
    }

    // The GH3 spine bones, bottom to top, for the spine roles that a skeleton has (bottom to top): the top role takes
    // bone_chest so that the shoulders turn as in GH3, the bottom role bone_stomach_lower.
    public static string[] SpineSources(int count) => count switch
    {
        1 => new[] { Gh3Checksum.Of("bone_stomach_upper") },
        2 => new[] { Gh3Checksum.Of("bone_stomach_lower"), Gh3Checksum.Of("bone_chest") },
        _ => new[] { Gh3Checksum.Of("bone_stomach_lower"), Gh3Checksum.Of("bone_stomach_upper"), Gh3Checksum.Of("bone_chest") },
    };

    private static readonly string[] Knuckles = { "Middle", "Ring", "Index", "Little" };

    // The finger base that gives the direction of a hand: the middle finger, else the ring, index, or little finger.
    public static string? Knuckle(string side, Func<string, bool> has)
    {
        foreach (var finger in Knuckles)
            if (has(side + finger + "Proximal")) return side + finger + "Proximal";
        return null;
    }

    // The two finger bases across the palm (index side, little side) for the roll of a hand, or null.
    public static (string First, string Last)? PalmLine(string side, Func<string, bool> has)
    {
        var first = new[] { "Index", "Middle" }.Select(f => side + f + "Proximal").FirstOrDefault(has);
        var last = new[] { "Little", "Ring" }.Select(f => side + f + "Proximal").FirstOrDefault(has);
        return first != null && last != null && first != last ? (first, last) : null;
    }

    // The role whose joint gives the direction of a role's bone (null: the bone keeps the world rest frame or the
    // correction of its parent).
    public static string? Next(string role, Func<string, bool> has)
    {
        if (Array.IndexOf(Spine, role) is var at and >= 0)
        {
            for (var i = at + 1; i < Spine.Length; i++) if (has(Spine[i])) return Spine[i];
            return has("neck") ? "neck" : "head";
        }
        if (role == "neck") return "head";
        foreach (var side in new[] { "left", "right" })
        {
            if (!role.StartsWith(side, StringComparison.Ordinal)) continue;
            var part = role[side.Length..];
            if (part == "Hand") return Knuckle(side, has);
            string? next = part switch
            {
                "Shoulder" => "UpperArm", "UpperArm" => "LowerArm", "LowerArm" => "Hand",
                "UpperLeg" => "LowerLeg", "LowerLeg" => "Foot", "Foot" => "Toes",
                "ThumbMetacarpal" => "ThumbProximal", "ThumbProximal" => "ThumbDistal",
                _ => null,
            };
            foreach (var finger in Fingers.Skip(1))
                if (part == finger + "Proximal") next = finger + "Intermediate";
                else if (part == finger + "Intermediate") next = finger + "Distal";
            return next == null ? null : side + next;
        }
        return null;
    }
}
