using Gh3Skin;
using Gh3Skin.Packages;

// Character attachments (PACKAGE-FORMAT.md section 20.6): the split of a band model into body and attachment, which must
// equal pipeline/package/character.py split_model (tests/test_blender_addon_characters.py has the same expected parts), and
// the instrument rules.
internal static class AttachmentLogicTests
{
    // The attachment parts (GLB primitives) of the GH3 band models: guitar, bass, microphone and stand, drum kit and sticks.
    public static readonly Dictionary<string, int[]> LibraryAttachments = new()
    {
        ["guitarist"] = new[] { 11, 12, 13 }, ["bassist"] = new[] { 1, 2 }, ["singer"] = new[] { 4, 5 }, ["drummer"] = new[] { 1 },
    };

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception(name);
    }

    public static void Run()
    {
        CheckSplit();
        CheckChoice();
        CheckJointNames();
        var library = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../assets/libraries/gh3-band-v1"));
        if (File.Exists(Path.Combine(library, "catalog.json"))) CheckLibrary(library);
    }

    private static byte[] Vertices(params (int Joint, float Weight, int Other)[] vertices)
    {
        var data = new byte[vertices.Length * StageGeometry.SkinnedStride];
        for (var v = 0; v < vertices.Length; v++)
        {
            var row = data.AsSpan(v * StageGeometry.SkinnedStride);
            row[36] = (byte)vertices[v].Other;
            row[37] = (byte)vertices[v].Joint;
            BitConverter.TryWriteBytes(row[40..], 1 - vertices[v].Weight);
            BitConverter.TryWriteBytes(row[44..], vertices[v].Weight);
        }
        return data;
    }

    private static void CheckSplit()
    {
        // Root 0; 1 is the parent of the pelvis (2) and has the spine (4); 3 is an instrument bone under the root.
        var bones = new[] { "00000001", "00000002", BandAttachments.Pelvis, "00000004", "00000005" };
        var parents = new[] { -1, 0, 1, 0, 1 };
        var body = BandAttachments.BodyJoints(bones, parents);
        Check(body.SetEquals(new[] { 1, 2, 4 }), "the body is the subtree of the parent of the pelvis");
        Check(BandAttachments.BodyJoints(new[] { "a", "b", "c" }, new[] { -1, 0, 1 }).SetEquals(new[] { 1, 2 }), "without a pelvis the body starts at joint 1");
        var path = Path.Combine(Path.GetTempPath(), "gh3-attachment-test-" + Guid.NewGuid().ToString("N") + ".bin");
        try
        {
            StageGeometry.WriteMeshes(path, new[]
            {
                new MeshPart("0000000a", 3, Vertices((3, 0.9f, 2), (3, 0.9f, 2), (2, 0.9f, 3)), new[] { 0, 1, 2 }),
                new MeshPart("0000000b", 2, Vertices((3, 0.9f, 2), (2, 0.9f, 3)), new[] { 0, 1, 1 }),
                // Equal weights: the first influence (joint 2, body) is the largest, as numpy argmax.
                new MeshPart("0000000c", 1, Vertices((3, 0.5f, 2)), new[] { 0, 0, 0 }),
            }, skinned: true);
            var parts = BandAttachments.ReadParts(path);
            Check(parts.Length == 3 && parts[0].Dominant.SequenceEqual(new[] { 3, 3, 2 }) && parts[2].Dominant.SequenceEqual(new[] { 2 }), "dominant joints");
            Check(BandAttachments.AttachmentParts(parts, body).SequenceEqual(new[] { 0 }), "more than half of the vertices outside the body make an attachment");
            Check(BandAttachments.PartMaterials(path).SequenceEqual(new[] { 0xau, 0xbu, 0xcu }), "part materials");
            // A character whose joint 3 is rig bone 4 (the body): no attachment.
            Check(BandAttachments.AttachmentParts(parts, body, new[] { 0, 1, 2, 4 }).Length == 0, "character joints map to the rig");
        }
        finally { File.Delete(path); }
    }

    private static void CheckChoice()
    {
        Check(BandAttachments.Choose(false, true, "", "SG_Black", "guitarist") is (true, false, var a) && a.Contains("SG_Black"), "no name: the role's instrument");
        Check(BandAttachments.Choose(false, true, "SG_Black", "SG_Black", "guitarist").Add, "the named instrument of the role");
        Check(BandAttachments.Choose(false, false, "SG_Black", "tobB4", "bassist") is (true, false, var b) && b.Contains("tobB4") && b.Contains("SG_Black is not used"),
            "a guitarist character that plays the bassist gets the bass");
        Check(BandAttachments.Choose(true, true, "", "SG_Black", "guitarist") is (false, true, var c) && c.Contains("own"), "a GLB with its own instrument keeps it in its role");
        Check(BandAttachments.Choose(true, true, "tobB4", "tobB4", "bassist") is (false, true, _), "an own instrument wins over a name");
        Check(BandAttachments.Choose(true, false, "", "tobB4", "bassist") is (true, false, var d) && d.Contains("tobB4"),
            "in another role, the role's instrument replaces the own instrument");
    }

    // The joints of a character on its GH3 rig (CharacterChainRig.ByName): each joint follows the bone with the same name.
    private static void CheckJointNames()
    {
        var rig = new[] { "r", "b", "h", "f" };
        var own = CharacterChainRig.ByName(new[] { "f", "r", "b" }, rig)!.Value;
        Check(own.Bones.SequenceEqual(new[] { 3, 0, 1 }) && own.Bind.SequenceEqual(Enumerable.Range(0, 3)), "joints by name, own bind poses");
        Check(CharacterChainRig.ByName(new[] { "r", "missing" }, rig) == null, "a joint outside the rig");
    }

    private static void CheckLibrary(string library)
    {
        foreach (var (role, expected) in LibraryAttachments)
        {
            var (bones, parents, _) = CharacterImporter.Rig(library, role);
            var parts = BandAttachments.ReadParts(Path.Combine(library, "band", role, "meshes.bin"));
            var found = BandAttachments.AttachmentParts(parts, BandAttachments.BodyJoints(bones, parents));
            Check(found.SequenceEqual(expected), $"{role} attachment parts {string.Join(",", found)} equal character.py split_model");
        }
    }
}
