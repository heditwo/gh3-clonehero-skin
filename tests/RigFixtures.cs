using Gh3Skin;
using Gh3Skin.Packages;

// The rig data of the GH3 band rigs by GH3 bone name (guitar_anim_tree.qb Hero_Ik_params, drummer_IK_params,
// hero_ConstraintBones; the library writes the same rows into catalog.json), for tests with rigs that are not read from
// a library.
internal static class RigFixtures
{
    private static string K(string name) => Gh3Checksum.Of(name);

    public static IkChainDefinition[] Ik(string handTarget = "Bone_IK_Hand_Guitar") => new[]
    {
        Chain("Bone_Bicep_R", "Bone_Forearm_R", "Bone_Palm_R", handTarget + "_R", -1, -0.97f, 0.97f, "right"),
        Chain("Bone_Bicep_L", "Bone_Forearm_L", "Bone_Palm_L", handTarget + "_L", -1, -0.97f, 0.97f, "left"),
        Chain("Bone_Thigh_L", "Bone_Knee_L", "Bone_Ankle_L", "Bone_IK_Foot_Slave_L", 1, -0.98999995f, 0.97f, ""),
        Chain("Bone_Thigh_R", "Bone_Knee_R", "Bone_Ankle_R", "Bone_IK_Foot_Slave_R", 1, -0.98999995f, 0.97f, ""),
    };

    public static RigConstraint[] Constraints() => new (string Type, string Bone, string Target, float Amount)[]
    {
        ("TwistChild", "Bone_Twist_Wrist_L", "Bone_Palm_L", 0.5f), ("TwistChild", "Bone_Twist_Wrist_R", "Bone_Palm_R", 0.5f),
        ("Twist", "Bone_Twist_Bicep_Mid_R", "Bone_Bicep_R", 0.5f), ("Twist", "Bone_Twist_Bicep_Mid_L", "Bone_Bicep_L", 0.5f),
        ("Twist", "Bone_Twist_Bicep_Top_R", "Bone_Bicep_R", 1), ("Twist", "Bone_Twist_Bicep_Top_L", "Bone_Bicep_L", 1),
        ("Twist", "Bone_Twist_Thigh_R", "Bone_Thigh_R", 0.5f), ("Twist", "Bone_Twist_Thigh_L", "Bone_Thigh_L", 0.5f),
        ("Split", "Bone_Split_Knee_R", "Bone_Knee_R", 0.5f), ("Split", "Bone_Split_Knee_L", "Bone_Knee_L", 0.5f),
        ("SplitR", "Bone_Split_Ass_R", "Bone_Twist_Thigh_R", 0.5f), ("SplitR", "Bone_Split_Ass_L", "Bone_Twist_Thigh_L", 0.5f),
        ("Split", "Bone_Split_Elbow_R", "Bone_Forearm_R", 0.5f), ("Split", "Bone_Split_Elbow_L", "Bone_Forearm_L", 0.5f),
    }.Select(c => new RigConstraint { Type = c.Type, Bone = K(c.Bone), Target = K(c.Target), Amount = c.Amount }).ToArray();

    // The rig points of a GH3 rig by the GH3 bone names that it has.
    public static RigPoints Points(IReadOnlyCollection<string> bones)
    {
        string P(string name) => bones.Contains(K(name)) ? K(name) : "";
        return new RigPoints
        {
            Instrument = P("bone_guitar_body"), Microphone = P("bone_mic"), MouthUpper = P("Bone_Lip_Upper_Mid"), MouthLower = P("Bone_Lip_Lower_Mid"),
            StickLeft = P("Bone_Drum_Stick_L"), StickRight = P("Bone_Drum_Stick_R"), Seat = bones.Contains("fdeaebd9") ? "fdeaebd9" : "",
        };
    }

    // A set that plays the band library member `member` (from performance.json) with the rig data of this file.
    public static CharacterMotion Motion(string role, string directory, PerformanceMember member) => new()
    {
        Member = member, ClipDirectory = Path.Combine(directory, role),
        Ik = role == "drummer" ? Ik("Bone_IK_Hand_Slave") : Ik(), Constraints = Constraints(),
    };

    private static IkChainDefinition Chain(string first, string second, string end, string target, float hinge, float cosMax, float cosMin, string hand) => new()
    {
        Bones = new[] { K(first), K(second), K(end) }, Target = K(target), Hinge = new[] { 0f, 0, hinge }, CosMax = cosMax, CosMin = cosMin, Hand = hand,
    };
}
