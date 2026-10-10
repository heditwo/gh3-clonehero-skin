namespace Gh3Skin.Packages;

// The attachment of a band model (instrument, microphone and stand, drum kit and sticks): the GH3BND02 parts whose vertices
// mostly follow joints outside the performer body. The same rule as pipeline/package/character.py split_model, so the
// game adds to a character the parts that the Blender add-on and `python -m pipeline.package.character` leave out.
internal static class BandAttachments
{
    public static readonly string Pelvis = Gh3Checksum.Of("bone_pelvis");

    public sealed record Part(uint Material, int[] Dominant);

    // The body joints of a rig: the subtree of the parent of bone_pelvis. The other subtrees of the root are the
    // instrument, microphone stand, drum kit, and IK helpers.
    public static HashSet<int> BodyJoints(IReadOnlyList<string> bones, IReadOnlyList<int> parents)
    {
        var pelvis = bones.ToList().IndexOf(Pelvis);
        var root = pelvis >= 0 && parents[pelvis] >= 0 ? parents[pelvis] : Math.Min(1, bones.Count - 1);
        var body = new HashSet<int>();
        var stack = new Stack<int>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var joint = stack.Pop();
            if (!body.Add(joint)) continue;
            for (var i = 0; i < parents.Count; i++)
                if (parents[i] == joint) stack.Push(i);
        }
        return body;
    }

    // The material and, for each vertex, the joint with the largest weight (the first one on a tie, as numpy argmax).
    public static Part[] ReadParts(string meshesPath)
    {
        using var reader = new BinaryReader(File.OpenRead(meshesPath));
        if (System.Text.Encoding.ASCII.GetString(reader.ReadBytes(8)) != "GH3BND02") throw new InvalidDataException("not a GH3BND02 band model");
        var count = reader.ReadInt32();
        if (count is < 1 or > 10000) throw new InvalidDataException("invalid band mesh count");
        var parts = new Part[count];
        var row = new byte[StageGeometry.SkinnedStride];
        for (var p = 0; p < count; p++)
        {
            var material = reader.ReadUInt32();
            var vertices = reader.ReadInt32();
            var indices = reader.ReadInt32();
            if (vertices is < 1 or > 65535 || indices is < 0 or > 1000000) throw new InvalidDataException("invalid band mesh size");
            var dominant = new int[vertices];
            for (var v = 0; v < vertices; v++)
            {
                if (reader.Read(row, 0, row.Length) != row.Length) throw new EndOfStreamException();
                var best = 0;
                for (var c = 1; c < 4; c++)
                    if (BitConverter.ToSingle(row, SkinnedVertex.Weights + c * 4) > BitConverter.ToSingle(row, SkinnedVertex.Weights + best * 4)) best = c;
                dominant[v] = row[SkinnedVertex.Joints + best];
            }
            reader.BaseStream.Seek(indices * 4L, SeekOrigin.Current);
            parts[p] = new Part(material, dominant);
        }
        return parts;
    }

    // The material of each part, without the vertices.
    public static uint[] PartMaterials(string meshesPath)
    {
        using var reader = new BinaryReader(File.OpenRead(meshesPath));
        if (System.Text.Encoding.ASCII.GetString(reader.ReadBytes(8)) != "GH3BND02") throw new InvalidDataException("not a GH3BND02 band model");
        var count = reader.ReadInt32();
        if (count is < 1 or > 10000) throw new InvalidDataException("invalid band mesh count");
        var materials = new uint[count];
        for (var p = 0; p < count; p++)
        {
            materials[p] = reader.ReadUInt32();
            var vertices = reader.ReadInt32();
            var indices = reader.ReadInt32();
            if (vertices is < 1 or > 65535 || indices is < 0 or > 1000000) throw new InvalidDataException("invalid band mesh size");
            reader.BaseStream.Seek((long)vertices * StageGeometry.SkinnedStride + indices * 4L, SeekOrigin.Current);
        }
        return materials;
    }

    // A part is an attachment when more than half of its vertices have their largest weight outside the body. `map` gives
    // the rig bone of each mesh joint (a character's joints in skin order); null when the mesh joints are the rig bones.
    public static bool IsAttachment(Part part, ISet<int> body, IReadOnlyList<int>? map = null)
    {
        var outside = 0;
        foreach (var joint in part.Dominant)
        {
            var bone = map == null ? joint : joint < map.Count ? map[joint] : -1;
            if (!body.Contains(bone)) outside++;
        }
        return outside * 2 > part.Dominant.Length;
    }

    public static int[] AttachmentParts(Part[] parts, ISet<int> body, IReadOnlyList<int>? map = null) =>
        Enumerable.Range(0, parts.Length).Where(i => IsAttachment(parts[i], body, map)).ToArray();

    // The instrument of a character that plays a band role (PACKAGE-FORMAT.md section 20.6). A GLB with its own attachment
    // parts keeps them in the role of its profile, and the library adds nothing. In another role, and without own parts,
    // the library adds the instrument of the role that the character plays: the v1 library has one instrument for each
    // role, and the role's clips are made for that instrument.
    public static (bool Add, bool KeepOwn, string Note) Choose(bool ownInstrument, bool ownRole, string named, string roleInstrument, string role)
    {
        roleInstrument = roleInstrument.Length > 0 ? roleInstrument : "of the " + role;
        if (ownInstrument && ownRole)
            return (false, true, named.Length > 0 ? $"its own instrument (the GLB has one; attachments.instrument {named} is not used)" : "its own instrument");
        var library = $"instrument {roleInstrument} from the library";
        if (ownInstrument) return (true, false, $"{library} (its own instrument is for the role of its profile)");
        if (named.Length == 0 || named == roleInstrument) return (true, false, library);
        return (true, false, $"{library} (the {role} plays the instrument of the role; {named} is not used)");
    }
}
