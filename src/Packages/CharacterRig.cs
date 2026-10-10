namespace Gh3Skin.Packages;

// How the joints of a character follow the rig of the band role that it plays (PACKAGE-FORMAT.md section 20.6). In the
// role of its profile, each joint follows the bone with the same name. In another role (a GH3 guitarist that plays the
// bassist), the body and instrument joints still follow the bones with the same name, so that the mesh takes the role's
// body; the face and accessory bones of the two rigs are at other places on the head and body, so these joints follow
// the head or their body parent without a move of their own, and the hair joints keep the hair chains of the profile.
internal static class CharacterRig
{
    public static readonly string Head = Gh3Checksum.Of("bone_head");
    public static readonly HashSet<string> Accessories = Enumerable.Range(1, 15).Select(i => Gh3Checksum.Of($"Bone_ACC_{i:00}")).ToHashSet();

    // For each character joint: the role bone that drives it, and the character joint whose bind pose skins its vertices.
    // `profileHair` holds the profile rig indices of the hair chain joints (band/hair.json). Null: a joint that the role
    // needs is not in the role rig.
    public static (int[] Bones, int[] Bind)? Map(IReadOnlyList<string> joints, IReadOnlyList<string> profileBones, IReadOnlyList<int> profileParents,
        IReadOnlyList<string> roleBones, ISet<int> profileHair, bool ownRole)
    {
        var role = Index(roleBones);
        var profile = Index(profileBones);
        var character = Index(joints);
        var bones = new int[joints.Count];
        var bind = new int[joints.Count];
        for (var i = 0; i < joints.Count; i++)
        {
            var target = joints[i];
            bind[i] = i;
            if (!ownRole && profile.TryGetValue(target, out var bone) && !profileHair.Contains(bone))
            {
                var body = Accessories.Contains(target) ? Ancestor(bone, profileParents, b => !Accessories.Contains(profileBones[b]))
                    : UnderHead(bone, profileBones, profileParents) ? profile.GetValueOrDefault(Head, -1) : bone;
                if (body >= 0 && body != bone && character.TryGetValue(profileBones[body], out var source))
                {
                    target = profileBones[body];
                    bind[i] = source;
                }
            }
            if (!role.TryGetValue(target, out bones[i])) return null;
        }
        return (bones, bind);
    }

    private static Dictionary<string, int> Index(IReadOnlyList<string> names)
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < names.Count; i++) result.TryAdd(names[i], i);
        return result;
    }

    private static int Ancestor(int bone, IReadOnlyList<int> parents, Func<int, bool> accept)
    {
        for (var parent = parents[bone]; parent >= 0; parent = parents[parent])
            if (accept(parent)) return parent;
        return -1;
    }

    private static bool UnderHead(int bone, IReadOnlyList<string> bones, IReadOnlyList<int> parents) =>
        Ancestor(bone, parents, b => bones[b] == Head) >= 0;
}
